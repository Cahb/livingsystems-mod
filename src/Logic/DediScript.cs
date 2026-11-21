using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using Eleon.Modding;
using LivingSystems.Core;

namespace LivingSystems
{
    /// <summary>
    /// Test harness, Dedi side: runs commands from <c>&lt;save&gt;/Mods/LivingSystems/script.in</c> as if an admin typed
    /// them, so a scenario runner (tests/live/run.py) can drive the game without anyone clicking. Admin-only by nature:
    /// whoever can write that file already owns the server.
    ///
    /// Each line: <c>&lt;seq&gt; &lt;pid&gt; &lt;command…&gt;</c>. The file is renamed to script.work, read, deleted; a line runs once
    /// (seq must be above the last one, kept in script.seq so a logic reload doesn't replay it). Results are appended to
    /// script.out as <c>&lt;seq&gt; ok|err &lt;text&gt;</c> (async commands add a later line) and logged as "script &lt;seq&gt;: …".
    ///
    /// The Dedi has no frame update: <see cref="Pump"/> runs off incoming traffic. PfServers send "scriptpoke" once a
    /// second while script.in exists (LogicEntry), so a step runs within ~1 s whenever a player is on a playfield.
    /// </summary>
    sealed class DediScript
    {
        readonly IModApi api;
        readonly DediSpike dedi;
        readonly Action<string> log;
        readonly Action<string> loaderLog;
        readonly string inPath, workPath, outPath, seqPath;
        long lastSeq;
        int lastSpawned;
        DateTime nextPoll = DateTime.MinValue;
        /// <summary>Legacy requests waiting for Event_Ok / Event_Error, by request seq.</summary>
        readonly Dictionary<ushort, PendingAck> acks = new Dictionary<ushort, PendingAck>();
        sealed class PendingAck { public long Seq; public string What = ""; public DateTime At; }
        /// <summary>No Ok / Error from the game this long after a request: reported as sent, outcome unknown.</summary>
        const double AckTimeoutSeconds = 10;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <param name="loaderLog">The loader's own log delegate: its target is the loader instance (for "ls reload").</param>
        public DediScript(IModApi api, DediSpike dedi, Action<string> log, Action<string> loaderLog)
        {
            this.api = api; this.dedi = dedi; this.log = log; this.loaderLog = loaderLog;
            var dir = Dir(api);
            inPath = Path.Combine(dir, "script.in"); workPath = Path.Combine(dir, "script.work");
            outPath = Path.Combine(dir, "script.out"); seqPath = Path.Combine(dir, "script.seq");
            try { if (File.Exists(seqPath)) long.TryParse(File.ReadAllText(seqPath).Trim(), NumberStyles.Integer, Inv, out lastSeq); } catch (Exception e) { log("script: seq file unreadable: " + e.Message); }
        }

        public static string Dir(IModApi api) => Path.Combine(api.Application.GetPathFor(AppFolder.SaveGame), "Mods", "LivingSystems");
        public static string InPath(IModApi api) => Path.Combine(Dir(api), "script.in");

        /// <summary>Run whatever is waiting (at most every 0.5 s). Call last in a handler: "ls reload" replaces this logic.</summary>
        public void Pump()
        {
            var now = DateTime.UtcNow;
            if (now < nextPoll) return;
            nextPoll = now.AddSeconds(0.5);
            foreach (var kv in acks.Where(x => (now - x.Value.At).TotalSeconds > AckTimeoutSeconds).ToList())
            { acks.Remove(kv.Key); Reply(kv.Value.Seq, true, kv.Value.What + ": sent, no Ok/Error from the game in " + AckTimeoutSeconds.ToString("0", Inv) + " s"); }
            string[] lines;
            try
            {
                if (!File.Exists(workPath))
                {
                    if (!File.Exists(inPath)) return;
                    File.Move(inPath, workPath);   // the runner writes script.in by rename too: we never see half a file
                }
                lines = File.ReadAllLines(workPath);
                File.Delete(workPath);
            }
            catch (Exception e) { log("script: reading script.in failed (retrying): " + e.Message); return; }

            for (int i = 0; i < lines.Length; i++)
            {
                var line = lines[i].Trim();
                if (line.Length == 0 || line.StartsWith("#", StringComparison.Ordinal)) continue;
                var parts = line.Split(new[] { ' ', '\t' }, 3, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 3 || !long.TryParse(parts[0], NumberStyles.Integer, Inv, out var seq) || !int.TryParse(parts[1], NumberStyles.Integer, Inv, out var pid))
                { log($"script: bad line '{line}' (want: <seq> <pid> <command…>)"); continue; }
                if (seq <= lastSeq) { log($"script {seq}: skipped (already ran up to {lastSeq})"); continue; }
                lastSeq = seq;
                try { File.WriteAllText(seqPath, seq.ToString(Inv)); } catch (Exception e) { log("script: seq file not written: " + e.Message); }
                var text = parts[2].Replace("$last", lastSpawned.ToString(Inv));
                log($"script {seq}: {pid} {text}");
                if (IsReload(text))
                {
                    // the rest waits in script.in for the new logic (it reads script.seq, so nothing runs twice)
                    var rest = lines.Skip(i + 1).Where(l => l.Trim().Length > 0).ToList();
                    try { if (rest.Count > 0) { if (File.Exists(inPath)) rest.AddRange(File.ReadAllLines(inPath)); File.WriteAllLines(inPath, rest); } }
                    catch (Exception e) { log("script: re-queueing after reload failed: " + e.Message); }
                    Reload(seq, pid);
                    return;   // this logic instance is gone now
                }
                try { Run(seq, pid, text); }
                catch (Exception e) { Reply(seq, false, "threw: " + e.Message); log($"script {seq} threw: {e}"); }
            }
        }

        static bool IsReload(string text)
        {
            var w = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            return w.Length == 2 && w[0].Equals("ls", StringComparison.OrdinalIgnoreCase) && w[1].Equals("reload", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Event_Ok / Event_Error for a request a script step sent. Called from LogicEntry before the spike sees it.</summary>
        public void OnLegacyEvent(CmdId id, ushort seqNr, object data)
        {
            if (id != CmdId.Event_Ok && id != CmdId.Event_Error) return;
            if (!acks.TryGetValue(seqNr, out var a)) return;
            acks.Remove(seqNr);
            if (id == CmdId.Event_Ok) Reply(a.Seq, true, a.What + ": game says Ok");
            else Reply(a.Seq, false, $"{a.What}: game says Error {(data as ErrorInfo)?.errorType}");
        }

        void Reply(long seq, bool ok, string text)
        {
            var line = $"{seq} {(ok ? "ok" : "err")} {text}";
            log($"script {seq}: {(ok ? "ok" : "err")} {text}");
            try { File.AppendAllText(outPath, line.Replace("\r", " ").Replace("\n", " / ") + "\n"); } catch (Exception e) { log("script: script.out not written: " + e.Message); }
        }

        void Track(ushort reqSeq, long seq, string what) { acks[reqSeq] = new PendingAck { Seq = seq, What = what, At = DateTime.UtcNow }; }

        // ---- commands -----------------------------------------------------------------------------------------------

        void Run(long seq, int pid, string text)
        {
            var w = text.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            var verb = w[0].ToLowerInvariant();
            var args = w.Skip(1).ToList();
            var tail = text.Length > w[0].Length ? text.Substring(w[0].Length).Trim() : "";
            switch (verb)
            {
                case "ls":
                    if (dedi.RunCommand(pid, "/ls " + tail, "script")) Reply(seq, true, "/ls " + tail);
                    else Reply(seq, false, "not an /ls command");
                    break;
                case "hail":   // = "ls hail": the PfServer scans, then the contact list opens
                    dedi.RunCommand(pid, "/ls hail", "script"); Reply(seq, true, "/ls hail");
                    break;
                case "menu":
                {
                    var why = dedi.MenuClick(pid, tail);
                    if (why != null) Reply(seq, false, why);
                    else Reply(seq, true, "clicked; now: " + dedi.MenuDump(pid));
                    break;
                }
                case "page": Reply(seq, true, dedi.MenuDump(pid)); break;
                case "mark": log($"script mark: {tail}"); Reply(seq, true, "mark " + tail); break;
                case "say": dedi.Say(pid, tail); Reply(seq, true, "said"); break;
                case "whereami":
                    if (!NeedLegacy(seq)) break;
                    dedi.ScriptPlayerInfo(pid, p => Reply(seq, p != null, p == null ? "no player info" :
                        $"pf='{p.playfield}' pos={p.pos.x.ToString("0", Inv)},{p.pos.y.ToString("0", Inv)},{p.pos.z.ToString("0", Inv)} rot={p.rot.y.ToString("0", Inv)} credits={p.credits.ToString("0", Inv)}"));
                    break;
                case "credits":
                {
                    if (!NeedLegacy(seq)) break;
                    if (args.Count < 1 || !int.TryParse(args[0], NumberStyles.Integer, Inv, out var n)) { Reply(seq, false, "credits <n> (negative takes, 0 only reads)"); break; }
                    if (n != 0) dedi.ScriptRequest(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = n });
                    dedi.ScriptPlayerInfo(pid, p => Reply(seq, p != null, $"credits {n:+0;-0}: balance now {(p?.credits ?? 0).ToString("0", Inv)}"));
                    break;
                }
                case "tp": Teleport(seq, pid, args); break;
                case "tpent":
                {
                    if (!NeedLegacy(seq)) break;
                    PVector3 pos;
                    if (args.Count != 4 || !int.TryParse(args[0], NumberStyles.Integer, Inv, out var id) || !Vec(args, 1, out pos)) { Reply(seq, false, "tpent <entityId|$last> <x> <y> <z>"); break; }
                    // unverified in game: legacy Request_Entity_Teleport (IdPositionRotation) on a ship
                    var r = dedi.ScriptRequest(CmdId.Request_Entity_Teleport, new IdPositionRotation { id = id, pos = pos, rot = new PVector3(0, 0, 0) });
                    Track(r, seq, $"teleport entity {id}");
                    break;
                }
                case "destroy":
                {
                    if (!NeedLegacy(seq)) break;
                    if (args.Count != 1 || !int.TryParse(args[0], NumberStyles.Integer, Inv, out var id)) { Reply(seq, false, "destroy <entityId|$last>"); break; }
                    var r = dedi.ScriptRequest(CmdId.Request_Entity_Destroy, new Id { id = id });
                    Track(r, seq, $"destroy {id}");
                    break;
                }
                case "spawn": Spawn(seq, pid, args); break;
                default:
                    Reply(seq, false, $"unknown command '{verb}' (ls, hail, menu, page, mark, say, whereami, credits, tp, tpent, destroy, spawn)");
                    break;
            }
        }

        bool NeedLegacy(long seq) { if (dedi.LegacyReady) return true; Reply(seq, false, "legacy API not attached"); return false; }

        static bool F(string s, out float v) => float.TryParse(s, NumberStyles.Float, Inv, out v);
        static bool Vec(List<string> a, int i, out PVector3 v)
        {
            v = new PVector3(0, 0, 0);
            if (a.Count < i + 3 || !F(a[i], out var x) || !F(a[i + 1], out var y) || !F(a[i + 2], out var z)) return false;
            v = new PVector3(x, y, z); return true;
        }

        /// <summary>
        /// tp &lt;playfield…&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; (playfield may have spaces; "here" = where the player is). Same playfield →
        /// Request_Entity_Teleport on the player; another → Request_Player_ChangePlayerfield. Both unverified in game.
        /// </summary>
        void Teleport(long seq, int pid, List<string> args)
        {
            if (!NeedLegacy(seq)) return;
            if (args.Count < 4 || !Vec(args, args.Count - 3, out var pos)) { Reply(seq, false, "tp <playfield|here> <x> <y> <z>"); return; }
            var target = string.Join(" ", args.Take(args.Count - 3)).Trim('\'', '"');
            dedi.ScriptPlayerInfo(pid, p =>
            {
                if (p == null) { Reply(seq, false, "no player info"); return; }
                var pf = target.Equals("here", StringComparison.OrdinalIgnoreCase) ? p.playfield : target;
                if (pf == p.playfield)
                {
                    var r = dedi.ScriptRequest(CmdId.Request_Entity_Teleport, new IdPositionRotation { id = pid, pos = pos, rot = p.rot });
                    Track(r, seq, $"teleport player in '{pf}'");
                }
                else
                {
                    var r = dedi.ScriptRequest(CmdId.Request_Player_ChangePlayerfield, new IdPlayfieldPositionRotation { id = pid, playfield = pf, pos = pos, rot = p.rot });
                    Track(r, seq, $"change playfield '{p.playfield}' -> '{pf}'");
                }
            });
        }

        /// <summary>
        /// spawn &lt;prefab&gt; &lt;playfield…&gt; &lt;x&gt; &lt;y&gt; &lt;z&gt; [name=A_B] [faction=N], or spawn &lt;prefab&gt; ahead &lt;metres&gt; […]
        /// (in front of the player, their playfield). Replies "spawned &lt;id&gt;"; "$last" in later lines is that id.
        /// </summary>
        void Spawn(long seq, int pid, List<string> args)
        {
            if (!NeedLegacy(seq)) return;
            var name = "Script Test"; byte group = 7;   // Polaris, like the sim's wrecks (0 = a player faction: the hull would count as a player's)
            var rest = new List<string>();
            foreach (var a in args)
            {
                if (a.StartsWith("name=", StringComparison.OrdinalIgnoreCase)) name = a.Substring(5).Replace('_', ' ');
                else if (a.StartsWith("faction=", StringComparison.OrdinalIgnoreCase)) byte.TryParse(a.Substring(8), NumberStyles.Integer, Inv, out group);
                else rest.Add(a);
            }
            const string usage = "spawn <prefab> <playfield> <x> <y> <z> | spawn <prefab> ahead <m>  [name=A_B] [faction=N]";
            if (rest.Count < 3) { Reply(seq, false, usage); return; }
            var prefab = rest[0];
            Action<int, ushort> done = (id, req) => { lastSpawned = id; Track(req, seq, $"spawned {id}"); Reply(seq, true, $"spawned {id} ({prefab})"); };
            if (rest[1].Equals("ahead", StringComparison.OrdinalIgnoreCase))
            {
                if (!F(rest[2], out var dist)) { Reply(seq, false, usage); return; }
                dedi.ScriptPlayerInfo(pid, p =>
                {
                    if (p == null) { Reply(seq, false, "no player info"); return; }
                    var place = SpawnGeometry.Place(new SpawnGeometry.V3(p.pos.x, p.pos.y, p.pos.z), new SpawnGeometry.V3(p.rot.x, p.rot.y, p.rot.z), dist, 0);
                    dedi.ScriptSpawn(p.playfield, prefab, new PVector3(place.Pos.X, place.Pos.Y, place.Pos.Z), new PVector3(0, place.Rot.Y, 0), name, group, done);
                });
                return;
            }
            if (rest.Count < 5 || !Vec(rest, rest.Count - 3, out var pos)) { Reply(seq, false, usage); return; }
            var pf = string.Join(" ", rest.Skip(1).Take(rest.Count - 4)).Trim('\'', '"');
            dedi.ScriptSpawn(pf, prefab, pos, new PVector3(0, 0, 0), name, group, done);
        }

        /// <summary>
        /// "ls reload" can't go through the logic: the loader catches "/ls reload" in chat and owns the reload. Its log
        /// delegate is a method on the loader instance, so call its ReloadEverywhere (Dedi + every PfServer with a player)
        /// by reflection. Unverified in game. The reply is written first: this instance is stopped by the reload.
        /// </summary>
        void Reload(long seq, int pid)
        {
            var target = loaderLog?.Target;
            var mi = target?.GetType().GetMethod("ReloadEverywhere", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (mi == null) { Reply(seq, false, "reload: loader's ReloadEverywhere not found (type " + (target?.GetType().FullName ?? "null") + ")"); return; }
            Reply(seq, true, "reloading (Dedi + playfields)");
            try { mi.Invoke(target, new object[] { pid }); }
            catch (Exception e) { log("script reload threw: " + (e.InnerException ?? e)); }
        }
    }
}
