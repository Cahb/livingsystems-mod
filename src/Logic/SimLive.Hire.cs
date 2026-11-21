using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using LivingSystems.Core.Sim;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// A salvage tug for hire: a player pays a station (the Dedi charges), a tug drops out of warp 1.5 km off and follows
    /// them; they hail wrecks / rocks to put them on its work list; it cuts them one by one into its hold; they take things
    /// out (the Dedi's trade window, a fee per item) or send it home to sell (the Dedi pays them a share).
    /// A body (kind "hire", id "h&lt;pid&gt;") driven by <see cref="HireBrain"/>: its watcher is its player; out of their range
    /// the sim holds it here and its cut goes on (<see cref="World.HireShare"/>). Orders come as "/ls fly hire …" packets.
    /// </summary>
    sealed partial class SimLive
    {
        sealed class Hired
        {
            public int Pid;
            public string Name = "", Station = "";
            public readonly Cutter Cut = new Cutter();
            public readonly Queue<int> Work = new Queue<int>();
            public bool Follow = true, FullSaid;
            /// <summary>The last status line sent to the menu, and when (sent when it changes, at most every 3 s).</summary>
            public string LastStat = ""; public double StatAt;
            /// <summary>The cut it was on when it went out of range, how far it was, and since when it's gone on alone.</summary>
            public int OffTarget; public float OffShare; public double OffAt; public string TargetName = "";
        }
        /// <summary>A hired tug comes back live within this of its player (<see cref="BodyRules.InMetres"/>).</summary>
        const float HireBackMetres = BodyRules.InMetres;
        /// <summary>Player → their hired tug's body.</summary>
        readonly Dictionary<int, Body> hired = new Dictionary<int, Body>();
        HireBrain hireBrain;

        static string HireId(int pid) => "h" + pid.ToString(CultureInfo.InvariantCulture);
        static Hired H(Body b) => (Hired)b.Mind;

        /// <summary>The hired tug's entity for a player (0 = none) — the Dedi's menu asks via "hired".</summary>
        public int HiredTugOf(int pid) => hired.TryGetValue(pid, out var b) ? b.Entity : 0;

        void HireIndex()
        {   // the index follows the bodies (one ending drops out of both)
            foreach (var pid in hired.Keys.Where(p => !bodies.ContainsKey(HireId(p))).ToList()) hired.Remove(pid);
            foreach (var b in bodies.Values.Where(x => x.Kind == "hire")) hired[H(b).Pid] = b;
        }

        sealed class HireBrain : Brain, IAdopts
        {
            public HireBrain(SimLive s) : base(s) { }
            public override string Kind => "hire";
            public override int Watcher(Body b) => H(b).Pid;
            public override bool HeldHere => true;
            public override string Prefab(Body b) => TugPrefab;
            public override string Name(Body b) => H(b).Name;

            public override bool Settle(Body b, IEntity tug, double now)
            {
                var h = H(b); var c = h.Cut; c.Tug = tug.Id;
                try { tug.Structure.SetFaction(FactionGroup.Polaris, 0); } catch (Exception ex) { S.log("SIM hire: tug faction threw " + ex.Message); }
                S.fly.Adopt(tug.Id);
                S.PlaceBeam(c, tug);
                S.PinTug(tug);
                if (b.CameBack) { var aboard = Manifest(c); if (aboard.Count > 0) S.Hold(tug.Structure, aboard); }   // back from the sim: its hold as the sim kept it
                else if (b.Adopted) S.SeedCargo(c, tug);   // what it had aboard before the reload
                S.fly.Command(h.Pid, tug.Id, "follow", 150f);
                S.log($"SIM hire {h.Pid}: '{h.Name}' ready ({tug.Id}), beam {(c.HasBeam ? "fitted" : "NONE")}");
                if (b.CameBack) S.HireResume(b, tug, now);
                return true;
            }

            public override void Arriving(Body b, IEntity tug, double now)
            {
                var h = H(b);
                if (now - h.StatAt >= 3) { h.StatAt = now; S.SendStat(b, tug); }
            }

            public override void LiveTick(Body b, IEntity tug, double now)
            {
                var h = H(b);
                if (now - h.StatAt >= 3) { h.StatAt = now; S.SendStat(b, tug); }
                if (tug.Structure == null) return;   // a proxy for now
                var c = h.Cut; c.Tug = tug.Id;
                c.Say = s => S.fly.Say(h.Pid, $"{h.Name}: {s}");
                var hp = h.Pid; c.OnCash = n => S.fly.Send("hirecash", hp, n);
                if (c.Wreck == 0) return;
                var res = S.SalvageStep(c, tug, now);
                if (res == CutResult.Full)
                {
                    c.Wreck = 0;
                    if (!h.FullSaid) { h.FullSaid = true; S.fly.Say(h.Pid, $"{h.Name}: hold's full. Open it and take what you want, or send us home to sell."); }
                    S.fly.Command(h.Pid, tug.Id, h.Follow ? "follow" : "hold", h.Follow ? 150f : 0);
                }
                else if (res != CutResult.Working) S.NextHireJob(b, tug);
            }

            /// <summary>Its hold, its list, and the cut it was on (which goes on meanwhile).</summary>
            public override string OnBack(Body b, IEntity tug, string why, double now)
            {
                var h = H(b); var c = h.Cut;
                FoldPlates(c);
                var tgt = c.Wreck; var share = tgt != 0 ? c.Share : 0f; var tname = "";
                if (tgt != 0 && S.Pf.Entities.TryGetValue(tgt, out var w) && w != null) tname = StructureTag.Strip(w.Name);
                if (tgt != 0 && tname.Length == 0) tname = "the wreck";
                if (tug != null) { try { S.Beam(c, tug, false); } catch { } }
                S.fly.Say(h.Pid, tgt != 0 ? $"{h.Name}: you're out of range — we'll keep at {tname} on our own. Come back within {HireBackMetres / 1000f:0} km of us."
                                          : $"{h.Name}: you're out of range — we'll wait here. Come back within {HireBackMetres / 1000f:0} km of us.");
                S.log($"SIM hire {h.Pid}: '{h.Name}' ({b.Entity}) out of range{(tug == null ? " (out of view)" : "")} — the sim's now, {Manifest(c).Sum(kv => kv.Value)} items aboard{(tgt != 0 ? $", {tname} {share:P0} cut" : "")}");
                h.OffTarget = tgt; h.OffShare = share; h.OffAt = now; h.TargetName = tname; h.LastStat = "";
                c.NewTarget(0, "", 0, 0);
                return Progress(h, share);
            }

            public override string Hold(Body b, IEntity e) { FoldPlates(H(b).Cut); return World.FormatHold(H(b).Cut.Cargo); }

            public override void OnDead(Body b, string how)
            {
                var h = H(b);
                if (how == "destroyed") S.fly.Say(h.Pid, $"{h.Name}: …(no answer — the tug's been destroyed)");
                S.log($"SIM hire {h.Pid}: '{h.Name}' ({b.Entity}) {how}");
                // what it carried beyond what its crates show (crowded: logged, not racked) spills out as sacks by the hull
                IEntity hull = null; if (b.Entity > 0) S.Pf?.Entities?.TryGetValue(b.Entity, out hull);
                if (hull?.Structure != null)
                {
                    try
                    {
                        FoldPlates(h.Cut);
                        var racked = S.ReadHold(hull.Structure);
                        var over = new Dictionary<string, int>(StringComparer.Ordinal);
                        foreach (var kv in h.Cut.Cargo) { var n = kv.Value - (racked.TryGetValue(kv.Key, out var r) ? r : 0); if (n > 0) over[kv.Key] = n; }
                        if (over.Count > 0) S.SpillOut(h.Name, hull.Position, over);
                    }
                    catch (Exception ex) { S.log($"SIM hire {h.Pid}: spilling its hold threw {ex.Message}"); }
                }
            }

            /// <summary>Its status for the menu while the sim has it.</summary>
            public override void OfflineTick(Body b, Vector3? at, double now)
            {
                var h = H(b);
                if (now - h.StatAt < 3) return;
                h.StatAt = now;
                var doing = h.OffTarget != 0 ? $"out of range — cutting {h.TargetName} ({S.OffShareNow(h, now):P0})" : "out of range — waiting";
                var where = "";
                if (at != null && b.HasPos && S.Pf.Players.TryGetValue(h.Pid, out var me) && me != null)
                {
                    var d = b.Pos - at.Value; var facing = me.DrivingEntity != null ? me.DrivingEntity.Rotation : me.Rotation;
                    where = $" · {(d.magnitude >= 1000 ? $"{d.magnitude / 1000f:0.0} km" : $"{d.magnitude:0} m")}, {HailDisplay.Relative(facing, d)}";
                }
                var line = $"{doing} · {Manifest(h.Cut).Sum(kv => kv.Value)} items{where}";
                if (line != h.LastStat) { h.LastStat = line; S.fly.Send("hirestat", h.Pid, BodyMsg.Clean(line)); }
            }

            public override void OnComeIn(Body b, bool ghost) => S.log($"SIM hire {H(b).Pid}: '{H(b).Name}' back in range{(ghost ? $" — its hull {b.Entity} is still here" : " — spawning")}");

            public object Adopt(Body b, BodyMsg m)
            {
                var p = BodyProgress.Read(m.Progress);
                var pid = BodyProgress.Int(p, "pid");
                if (pid <= 0 || S.hired.ContainsKey(pid)) return null;
                var home = BodyProgress.Get(p, "home");
                var h = new Hired { Pid = pid, Name = BodyProgress.Get(p, "name"), Station = home.Length > 0 ? home : "the yard" };
                if (h.Name.Length == 0) h.Name = "Salvage tug";
                if (b.Offline)
                {
                    h.OffTarget = BodyProgress.Int(p, "tgt"); h.OffShare = (float)BodyProgress.Num(p, "share"); h.OffAt = S.fly.Now;
                    h.TargetName = BodyProgress.Get(p, "tname"); if (h.TargetName.Length == 0) h.TargetName = "the wreck";
                    foreach (var kv in World.ParseHold(m.Hold)) h.Cut.Cargo[kv.Key] = kv.Value;
                    foreach (var wv in BodyProgress.Get(p, "work").Split(',')) if (int.TryParse(wv, out var wi) && wi > 0) h.Work.Enqueue(wi);
                }
                S.hired[pid] = b;
                return h;
            }

            static string Progress(Hired h, float share) => BodyProgress.Write(new[]
            {
                new KeyValuePair<string, string>("tgt", h.OffTarget != 0 ? h.OffTarget.ToString(CultureInfo.InvariantCulture) : ""),
                new KeyValuePair<string, string>("tname", h.OffTarget != 0 ? h.TargetName : ""),
                new KeyValuePair<string, string>("share", h.OffTarget != 0 ? share.ToString("R", CultureInfo.InvariantCulture) : ""),
                new KeyValuePair<string, string>("work", string.Join(",", h.Work.Select(x => x.ToString(CultureInfo.InvariantCulture)))),
                // following, nothing to cut: it follows its player through warp (the Dedi sends it after them)
                new KeyValuePair<string, string>("follow", h.Follow && h.OffTarget == 0 && h.Work.Count == 0 ? "1" : ""),
            });
        }

        void HireCmd(int pid, string[] args)
        {
            var sub = args.Length > 0 ? args[0] : "";
            hired.TryGetValue(pid, out var b);
            var h = b != null ? H(b) : null;
            IEntity tug = null; if (b != null && b.Entity > 0) Pf?.Entities?.TryGetValue(b.Entity, out tug);
            switch (sub)
            {
                case "start":   // start <name> <station…>
                {
                    if (h != null) { fly.Say(pid, $"{h.Name}: we're already yours."); return; }
                    if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var me) || me == null)
                    { SendBody(BodyMsg.Dead("hire", HireId(pid), "nospawn")); return; }   // they've moved on (a call-in arriving late): the Dedi refunds
                    var at = me.DrivingEntity != null ? me.DrivingEntity.Position : me.Position;
                    var dir = UnityEngine.Random.onUnitSphere; dir.y *= 0.3f;
                    var pos = ClearSpot(at + dir.normalized * ArriveMetres, 60f);
                    h = new Hired { Pid = pid, Name = args.Length > 1 ? args[1].Replace('_', ' ') : "Salvage tug", Station = args.Length > 2 ? string.Join(" ", args.Skip(2)) : "the yard" };
                    b = hired[pid] = NewBody(hireBrain, HireId(pid), h);
                    SpawnBody(b, pos, Yaw(at - pos));
                    fly.Say(pid, $"{h.Name}: out of {h.Station}, dropping out of warp {ArriveMetres / 1000f:0.0} km off you — coming in. Hail us for orders.");
                    return;
                }
            }
            if (h == null) { fly.Say(pid, "You haven't hired a tug here."); return; }
            if (b.Offline) { fly.Say(pid, $"{h.Name}: …(you're out of range — come within {HireBackMetres / 1000f:0} km of us)"); return; }
            if (tug == null || !b.Ready) { fly.Say(pid, $"{h.Name}: still on our way in — give us a moment."); return; }
            var c = h.Cut;
            switch (sub)
            {
                case "follow":
                    h.Follow = true; Beam(c, tug, false);
                    fly.Command(pid, tug.Id, "follow", 150f);
                    fly.Say(pid, $"{h.Name}: right behind you." + (c.Wreck != 0 ? " (the cut can wait)" : ""));
                    c.Wreck = 0;   // following and cutting don't mix; the list stays
                    return;
                case "stay":
                    h.Follow = false;
                    fly.Command(pid, tug.Id, "hold", 0);
                    fly.Say(pid, $"{h.Name}: holding here.");
                    return;
                case "cut":   // cut <entity>
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out var wid) || !Pf.Entities.TryGetValue(wid, out var w) || w?.Structure == null)
                    { fly.Say(pid, $"{h.Name}: can't get a fix on that one from here."); return; }
                    if (wid == tug.Id) { fly.Say(pid, $"{h.Name}: very funny."); return; }
                    if (Cored(w)) { fly.Say(pid, $"{h.Name}: {StructureTag.Strip(w.Name)} has a core — someone owns it. Not touching it."); return; }
                    if (c.Wreck == wid || h.Work.Contains(wid)) { fly.Say(pid, $"{h.Name}: already on our list."); return; }
                    h.Work.Enqueue(wid);
                    var ahead = h.Work.Count + (c.Wreck != 0 ? 1 : 0) - 1;
                    fly.Say(pid, $"{h.Name}: {StructureTag.Strip(w.Name)} — on the list{(ahead > 0 ? $", {ahead} ahead of it" : ", starting now")}.");
                    if (c.Wreck == 0) NextHireJob(b, tug);
                    return;
                }
                case "clear":
                    h.Work.Clear(); Beam(c, tug, false); c.Wreck = 0;
                    fly.Command(pid, tug.Id, h.Follow ? "follow" : "hold", h.Follow ? 150f : 0);
                    fly.Say(pid, $"{h.Name}: list cleared.");
                    return;
                case "hold":   // the Dedi wants what's aboard (to open the trade window on it)
                    FoldPlates(c);
                    fly.Send("hirehold", pid, tug.Id, World.FormatHold(c.Cargo));
                    return;
                case "took":   // took <Item:N,…> — the player took these out (paid at the Dedi)
                {
                    FoldPlates(c);
                    foreach (var kv in World.ParseHold(args.Length > 1 ? args[1] : ""))
                        if (c.Cargo.TryGetValue(kv.Key, out var have)) { if (have - kv.Value > 0) c.Cargo[kv.Key] = have - kv.Value; else c.Cargo.Remove(kv.Key); }
                    try { Hold(tug.Structure, Manifest(c)); } catch (Exception ex) { log("SIM hire: rewriting the hold threw " + ex.Message); }
                    h.FullSaid = false;
                    return;
                }
                case "dismiss":   // home to sell: the Dedi pays the player their share of what's aboard
                {
                    FoldPlates(c);
                    var manifest = World.FormatHold(c.Cargo);
                    Beam(c, tug, false);
                    fly.Send("hiredone", pid, manifest);
                    fly.Say(pid, $"{h.Name}: taking it home to {h.Station} to sell — your share's wired when it's sold. Pleasure.");
                    bodies.Remove(b.Id); hired.Remove(pid);   // not dead, not back: the Dedi hands the hull back as a leaving sim ship ("hiregone")
                    fly.Command(pid, tug.Id, "hold", 0);
                    log($"SIM hire {pid}: '{h.Name}' dismissed with {manifest}");
                    return;
                }
                default:
                    fly.Say(pid, "/ls fly hire follow | stay | cut <id> | clear | hold | dismiss");
                    return;
            }
        }

        /// <summary>The live hold's plates (counted from blocks cut) into the cargo list, so taking some out is plain arithmetic.</summary>
        static void FoldPlates(Cutter c)
        {
            if (c.BlocksCut < 2) return;
            c.Cargo["SteelPlate"] = (c.Cargo.TryGetValue("SteelPlate", out var p) ? p : 0) + c.BlocksCut / 2;
            c.BlocksCut %= 2;
        }

        void NextHireJob(Body b, IEntity tug)
        {
            var h = H(b); var c = h.Cut;
            while (h.Work.Count > 0)
            {
                var next = h.Work.Dequeue();
                if (Pf.Entities.TryGetValue(next, out var w) && w?.Structure != null && !Cored(w))
                {
                    c.NewTarget(next, "", 0, 0);
                    fly.Say(h.Pid, $"{h.Name}: on to {StructureTag.Strip(w.Name)}.");
                    return;
                }
            }
            c.Wreck = 0;
            fly.Command(h.Pid, tug.Id, h.Follow ? "follow" : "hold", h.Follow ? 150f : 0);
            fly.Say(h.Pid, $"{h.Name}: list's done — {Manifest(c).Sum(kv => kv.Value)} items aboard. Hail us to open the hold or send us home.");
        }

        /// <summary>How far its cut has got while the sim had it (that cut's own 0–100 % bar over the rest of the hull).</summary>
        float OffShareNow(Hired h, double now) =>
            h.OffTarget == 0 ? 0f : h.OffShare + (1 - h.OffShare) * (float)Math.Max(0, Math.Min(1, (now - h.OffAt) / World.HireOffSeconds));

        /// <summary>Live again after the sim had it: back on the cut it was on, caught up to where the sim got (crates first,
        /// as at any cut; the hold's room still counts), then the rest of its list.</summary>
        void HireResume(Body b, IEntity tug, double now)
        {
            var h = H(b); var c = h.Cut;
            var tgt = h.OffTarget; var upTo = OffShareNow(h, now);
            h.OffTarget = 0;
            if (tgt != 0 && Pf.Entities.TryGetValue(tgt, out var w) && w?.Structure != null && !Cored(w))
            {
                c.NewTarget(tgt, "", h.OffShare, upTo);
                fly.Say(h.Pid, $"{h.Name}: back with you. {h.TargetName}: {upTo:P0} cut while you were away — catching the hold up now.");
            }
            else
            {
                fly.Say(h.Pid, $"{h.Name}: back with you{(tgt != 0 ? $" ({h.TargetName}'s gone — on to the list)" : "")}.");
                NextHireJob(b, tug);
            }
        }

        /// <summary>"Your tug" in the player's COMMS: where it is from their nose, what it's doing, how full it is.</summary>
        void SendStat(Body b, IEntity tug)
        {
            var h = H(b);
            string where = "";
            if (Pf?.Players != null && Pf.Players.TryGetValue(h.Pid, out var me) && me != null)
            {
                var ship = me.DrivingEntity;
                var at = ship != null ? ship.Position : me.Position;
                var facing = ship != null ? ship.Rotation : me.Rotation;
                var d = tug.Position - at;
                where = $"{(d.magnitude >= 1000 ? $"{d.magnitude / 1000f:0.0} km" : $"{d.magnitude:0} m")}, {HailDisplay.Relative(facing, d)}";
            }
            var c = h.Cut;
            string doing;
            if (!b.Ready) doing = "on its way in";
            else if (c.Wreck != 0 && Pf.Entities.TryGetValue(c.Wreck, out var w) && w != null) doing = $"cutting {StructureTag.Strip(w.Name)} ({c.Share:P0})";
            else doing = h.Follow ? "following you" : "holding";
            var hold = "";
            if (tug.Structure != null)
            {
                var cap = CapacityOf(tug);
                var aboard = Manifest(c);
                hold = cap > 0 ? $" · hold {Litres(aboard) / cap:P0}" : $" · {aboard.Sum(kv => kv.Value)} items";
                if (h.Work.Count > 0) hold += $" · {h.Work.Count} queued";
            }
            var line = $"{doing}{hold}{(where.Length > 0 ? " · " + where : "")}";
            if (line == h.LastStat) return;
            h.LastStat = line;
            fly.Send("hirestat", h.Pid, BodyMsg.Clean(line));
        }

        /// <summary>A tug docked live at its yard, hired: its hull is the player's tug now (it undocks and follows).</summary>
        void HireTake(int pid, string agent, int entity, string name, string home)
        {
            DropShip(agent);
            if (hired.ContainsKey(pid)) return;
            var b = NewBody(hireBrain, HireId(pid), new Hired { Pid = pid, Name = name, Station = home.Length > 0 ? home : "the yard" });
            b.Entity = entity; b.Adopted = true; b.EverLive = true;
            hired[pid] = b;
            fly.Say(pid, $"{name}: undocking — on our way to you. Hail us for orders.");
            log($"SIM hire {pid}: '{name}' ({entity}) taken off its berth ({agent})");
        }

        /// <summary>A dismissed tug becomes a leaving sim ship: flies out to its warp point, spools, jumps — or the sim takes
        /// it over once nobody's near (the same rules as every live ship; its warp home is the sim's already).</summary>
        void HireGone(string agent, int entity, string home)
        {
            if (live.ContainsKey(agent) || Pf?.Entities == null || !Pf.Entities.TryGetValue(entity, out var e) || e == null) return;
            var away = PlayerSpot(out var pp) ? Level(e.Position - pp) : Level(UnityEngine.Random.onUnitSphere);
            if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
            var l = AddShip(new Live { Agent = agent, Kind = "tug", Since = fly.Now, Departing = true, LegKind = "out", Bound = home,
                                       Dest = e.Position + away.normalized * DepartMetres });
            l.Entity = entity; l.B.EverLive = true;
            log($"SIM hire: dismissed tug {entity} is {agent} again — flying out for {home}");
        }
    }
}
