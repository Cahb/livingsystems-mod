using System;
using System.Collections.Generic;
using System.Linq;
using Eleon;
using Eleon.Modding;
using LivingSystems.Core;

namespace LivingSystems
{
    /// <summary>
    /// Dedi half of the F00 spike: reads "/ls" chat commands, spawns ships via the legacy API,
    /// shows dialogs, and forwards movement/inspection commands to the player's PfServer.
    /// </summary>
    sealed partial class DediSpike
    {
        readonly IModApi api;
        readonly Action<string> log;
        ModGameAPI legacy;
        ushort seq = 1000;
        readonly Dictionary<ushort, Action<PlayerInfo>> pendingPlayerInfo = new Dictionary<ushort, Action<PlayerInfo>>();
        readonly Dictionary<ushort, Action<int>> pendingNewId = new Dictionary<ushort, Action<int>>();
        readonly Dictionary<ushort, int> pendingFactions = new Dictionary<ushort, int>();
        int spawnCounter;
        int purgeFor;
        int gsinfoFor; List<int> gsinfoIds = new List<int>();
        int gstatsFor;
        DediTrade trade;

        /// <summary>Called with (entity id, name, playfield) for every ship we spawn — the registry's source of truth.</summary>
        public Action<int, string, string> OnSpawned;
        /// <summary>Called with the entity id of every structure we destroy from here (the registry forgets it).</summary>
        public Action<int> OnDestroyed;
        /// <summary>/ls sim … → the offline world (DediSim).</summary>
        public Action<int, SpikeCommand> OnSim;
        /// <summary>Every Event_GlobalStructure_List (the offline sim reads stations from it).</summary>
        public Action<GlobalStructureList> OnGlobalList;
        public void RequestGlobalList() { if (legacy != null) Request(CmdId.Request_GlobalStructure_List, null); }

        public DediSpike(IModApi api, Action<string> log)
        {
            this.api = api; this.log = log;
            trade = new DediTrade(api, log, Say, (pid, then) => RequestPlayerInfo(pid, pi => then(pi.credits)));
            log("Dedi spike ready. Type /ls in chat.");
        }

        public void AttachLegacy(ModGameAPI l)
        {
            legacy = l; log("legacy API attached");
            // (the old kill-counter probe ran on a thread-pool timer and touched shared state off the main thread: removed)
        }

        System.Threading.Timer killTimer;
        readonly Dictionary<int, int> lastKills = new Dictionary<int, int>();
        public void Stop() { killTimer?.Dispose(); }

        // ---- chat -------------------------------------------------------------------------------

        public void OnChat(MessageData m)
        {
            try
            {
                if (m != null) RunCommand(m.SenderEntityId, m.Text, "chat");
            }
            catch (Exception e) { log("OnChat threw: " + e); }
        }

        /// <summary>One "/ls …" line as if <paramref name="pid"/> typed it (chat and the script file share this). False = not ours.</summary>
        public bool RunCommand(int pid, string text, string from)
        {
            if (!SpikeCommand.TryParse(text, out var cmd)) return false;
            log($"{from} cmd from {pid}: {text}");
            Handle(pid, cmd);
            return true;
        }

        void Handle(int pid, SpikeCommand cmd)
        {
            switch (cmd.Verb)
            {
                case "spawn": Spawn(pid, cmd); break;
                case "dialog": ShowTraderDialog(pid); break;
                case "factions": RequestFactions(pid); break;
                case "fine":
                {
                    // Test: does legacy AddCredits with a NEGATIVE amount deduct? (and what happens below zero)
                    var amount = cmd.IntArg(0, 100, 1, 10000000);
                    RequestPlayerInfo(pid, before =>
                    {
                        var s1 = Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = -amount });
                        log($"fine {pid}: {amount} cr, before={before.credits}, AddCredits seq={s1}");
                        // read back a moment later (next request is queued after the AddCredits)
                        RequestPlayerInfo(pid, after =>
                        {
                            log($"fine {pid}: after={after.credits} (delta {after.credits - before.credits})");
                            Say(pid, $"fine {amount} cr: balance {before.credits:0} -> {after.credits:0} (delta {after.credits - before.credits:0})");
                        });
                    });
                    break;
                }
                case "gsinfo":
                    // Exact faction group/id of any structure (e.g. a real [Pirates] POI), from the global structure list.
                    gsinfoFor = pid; gsinfoIds = cmd.Args.Select(a => int.TryParse(a, out var x) ? x : -1).Where(x => x >= 0).ToList();
                    Request(CmdId.Request_GlobalStructure_List, null);
                    break;
                case "purge":
                    // Playfield side clears what is loaded; here we also destroy every «LS» entry in the GLOBAL structure
                    // list (RemoveEntity leaves them there - verified). Test-save tool.
                    ForwardToPlayfield(pid, cmd);
                    purgeFor = pid;
                    Request(CmdId.Request_GlobalStructure_List, null);
                    break;
                case "movepf": MovePlayfield(pid, cmd); break;
                case "dlg": DialogProbe(pid, cmd.Arg(0, "")); break;
                case "gstats":
                {   // probe: block counts of ANY structure by id (far POI in this playfield, or an unloaded one) via legacy BlockStatistics
                    if (cmd.Args.Count < 1 || !int.TryParse(cmd.Args[0], out var sid)) { Say(pid, "/ls gstats <entity id>"); break; }
                    gstatsFor = pid;
                    var seq = Request(CmdId.Request_Structure_BlockStatistics, new Id { id = sid });
                    log($"gstats {sid} seq={seq} (by {pid})");
                    Say(pid, $"block stats for {sid} requested (seq {seq})");
                    break;
                }
                case "gdestroy":
                {   // probe: does Request_Entity_Destroy remove a structure (e.g. a POI) whose playfield is NOT loaded? Check with /ls gsinfo / gents.
                    if (cmd.Args.Count < 1 || !int.TryParse(cmd.Args[0], out var gid)) { Say(pid, "/ls gdestroy <entity id>"); break; }
                    var seq = Request(CmdId.Request_Entity_Destroy, new Id { id = gid });
                    log($"gdestroy {gid} seq={seq} (by {pid})");
                    Say(pid, $"destroy {gid} sent (seq {seq}) — check /ls gsinfo {gid}");
                    break;
                }
                case "sim": if (OnSim != null) OnSim(pid, cmd); else Say(pid, "sim not running (see log)"); break;
                case "heal":
                    // Test-session convenience: refill the player's vitals through the legacy API.
                    RequestPlayerInfo(pid, p =>
                    {
                        legacy.Game_Request(CmdId.Request_Player_SetPlayerInfo, NextSeq(), new PlayerInfoSet
                        {
                            entityId = pid,
                            health = (int)p.healthMax, oxygen = (int)p.oxygenMax, food = (int)p.foodMax,
                            stamina = (int)p.staminaMax, radiation = 0,
                        });
                        // Oxygen set this way is overridden by the client (verified), so hand out bottles too.
                        var gave = "";
                        foreach (var name in new[] { "Eden_OxygenBottleLarge", "OxygenBottleLarge" })
                            if (trade.TryId(name, out var oid))
                            {
                                legacy.Game_Request(CmdId.Request_Player_AddItem, NextSeq(), new IdItemStack { id = pid, itemStack = new ItemStack { id = oid, count = 10 } });
                                gave = $" + 10x {name} (id {oid}) in your inventory";
                                break;
                            }
                        if (gave == "") gave = " (no item list yet, so no bottles: /ls reload while on a playfield)";
                        Say(pid, $"healed: hp {p.health:0}->{p.healthMax:0}, food {p.food:0}->{p.foodMax:0}{gave}");
                    });
                    break;
                case "trade": if (legacy == null) Say(pid, "legacy API not attached"); else trade.Open(pid, cmd.Args, Request); break;
                case "items": trade.Items(pid, cmd.Arg(0, "")); break;
                case "move": case "stop": case "clear": case "list": case "info": case "faction": case "goto": case "follow": case "scan": case "cargo": case "perf": case "npc": case "traderblock": case "holo": case "tp": case "find": case "spawn2": case "record": case "hail": case "comms": case "fly": case "rock":
                    ForwardToPlayfield(pid, cmd); break;
                default: Say(pid, SpikeCommand.HelpFor(cmd.Verb == "help" ? cmd.Arg(0, "") : "")); break;
            }
        }

        /// <summary>
        /// Dialog text mirrored into private chat (dialogs leave no history — the chat log is it), from the
        /// ship's name. The same line from the same ship isn't repeated (Back re-opens the menu).
        /// </summary>
        void Echo(int pid, string from, string text)
        {
            if (string.IsNullOrEmpty(text)) return;
            var key = pid + "|" + from;
            if (lastEcho.TryGetValue(key, out var was) && was == text) return;
            lastEcho[key] = text;
            api.Application.SendChatMessage(new MessageData
            {
                SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = pid,
                SenderNameOverride = StructureTag.Strip(from ?? "LS"), Text = text,
            });
        }
        readonly Dictionary<string, string> lastEcho = new Dictionary<string, string>();

        public void Say(int pid, string text)
        {
            log($"say {pid}: {text}");
            api.Application.SendChatMessage(new MessageData
            {
                SenderType = Eleon.SenderType.ServerInfo,
                Channel = Eleon.MsgChannel.SinglePlayer,
                RecipientEntityId = pid,
                SenderNameOverride = "LS",
                Text = text,
            });
        }

        string PlayfieldOf(int pid) => api.Application.GetPlayerDataFor(pid)?.PlayfieldName;

        void ForwardToPlayfield(int pid, SpikeCommand cmd)
        {
            var pf = PlayfieldOf(pid);
            if (pf == null) { Say(pid, "can't find your playfield"); return; }
            var ok = api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf,
                Wire.Pack(new object[] { pid, cmd.Verb }.Concat(cmd.Args).ToArray()));
            log($"forward '{cmd.Verb}' to pf '{pf}': {ok}");
            if (!ok) Say(pid, $"couldn't reach playfield server for '{pf}'");
        }

        public void OnPacket(string sender, string playfieldName, byte[] data)
        {
            try
            {
                if (!Wire.IsOurs(sender)) return;
                var f = Wire.Unpack(data);
                if (f.Length >= 2 && f[0] == "items")
                {
                    var m = new Dictionary<string, int>();
                    foreach (var pair in f.Skip(1))
                    {
                        var i = pair.LastIndexOf('=');
                        if (i > 0 && int.TryParse(pair.Substring(i + 1), out var id) && !m.ContainsKey(pair.Substring(0, i))) m[pair.Substring(0, i)] = id;
                    }
                    if (!trade.HasMapping) trade.SetMapping(m);
                    return;
                }
                if (f.Length >= 5 && f[0] == "tpto" && int.TryParse(f[1], out var tpid))
                {
                    float G(string v) => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                    var ts = Request(CmdId.Request_Entity_Teleport, new IdPositionRotation { id = tpid, pos = new PVector3(G(f[2]), G(f[3]), G(f[4])), rot = new PVector3(0, 0, 0) });
                    log($"legacy teleport {tpid} seq={ts}");
                    return;
                }
                if (f.Length >= 2 && f[0] == "destroy" && int.TryParse(f[1], out var dpid))
                {
                    var n = 0;
                    foreach (var s in f.Skip(2)) if (int.TryParse(s, out var did)) { Request(CmdId.Request_Entity_Destroy, new Id { id = did }); OnDestroyed?.Invoke(did); n++; }
                    log($"destroy: {n} entit(ies) for {dpid} via Request_Entity_Destroy");
                    return;
                }
                if (f.Length >= 4 && f[0] == "escortoffer" && int.TryParse(f[1], out var xpid) && int.TryParse(f[2], out var xid))
                {
                    ShowEscortOffer(xpid, xid, string.Join("|", f.Skip(3)));
                    return;
                }
                if (f.Length >= 6 && f[0] == "patrolspawn")
                {   // a station patrol for a live pirate attack (SimLive decides; Polaris colours — vanilla turrets know the enemy)
                    float G(string v) => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                    var pfName = playfieldName;
                    SpawnShip(pfName, f[1], new PVector3(G(f[2]), G(f[3]), G(f[4])), new PVector3(0, G(f[5]), 0), StructureTag.Apply("Polaris Patrol"), 7, 0, id =>
                        api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pfName, Wire.Pack(0, "fly", "sim", "patrolspawned", id)));
                    return;
                }
                if (f.Length >= 7 && f[0] == "escortspawn" && int.TryParse(f[1], out var traderId))
                {
                    float G(string v) => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                    var pfName = playfieldName;
                    SpawnShip(pfName, f[2], new PVector3(G(f[3]), G(f[4]), G(f[5])), new PVector3(0, G(f[6]), 0), StructureTag.Apply("Raider"),
                        SpikeCommand.FactionGroup("pirates"), 0, id =>
                            api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pfName, Wire.Pack(0, "fly", "escort", "spawned", traderId, id)));
                    return;
                }
                if (f.Length >= 3 && f[0] == "pay" && int.TryParse(f[1], out var paypid) && int.TryParse(f[2], out var credits) && credits > 0)
                {
                    var s = Request(CmdId.Request_Player_AddCredits, new IdCredits { id = paypid, credits = credits });
                    log($"pay {paypid}: +{credits} cr seq={s}");
                    return;
                }
                if (f.Length >= 4 && f[0] == "orderreply" && int.TryParse(f[1], out var opid) && int.TryParse(f[2], out var oid))
                {
                    if (hailFocus.TryGetValue(opid, out var oc) && oc.Id == oid) { if (f.Length >= 5) oc.Status = f[4]; ShowShip(opid, f[3]); }
                    else Say(opid, f[3]);
                    return;
                }
                if (f.Length >= 2 && f[0] == "hailist" && int.TryParse(f[1], out var hpid))
                {
                    hailContacts[hpid] = f.Skip(2).Select(Contact.Parse).Where(c => c != null).ToList();
                    hailPf[hpid] = playfieldName;
                    // pointed at one of ours (nose cone): straight to that ship, no pager
                    if (hailContacts[hpid].Count > 0 && hailContacts[hpid][0].Nose) { var hm = MenuOf(hpid); hm.Back.Clear(); hm.Build = null; hailFocus[hpid] = hailContacts[hpid][0]; ShowShip(hpid); }
                    else ShowContacts(hpid);
                    return;
                }
                if (f.Length >= 3 && f[0] == "kioskgone" && int.TryParse(f[1], out var gpid) && int.TryParse(f[2], out var gid))
                {
                    // Playfield RemoveEntity leaves the structure in the global structure DB (verified via gents): destroy it there too.
                    var ds = Request(CmdId.Request_Entity_Destroy, new Id { id = gid });
                    log($"structure {gid} removed on the playfield (purge / leftover kiosk); global destroy seq={ds}");
                    return;
                }
                if (f.Length >= 3 && f[0] == "npcname" && int.TryParse(f[1], out var nid))
                {
                    var s = Request(CmdId.Request_Entity_SetName, new IdPlayfieldName { id = nid, playfield = playfieldName, name = f[2] });
                    log($"SetName {nid} -> '{f[2]}' seq={s}");
                    return;
                }
                if (f.Length >= 3 && f[0] == "say" && int.TryParse(f[1], out var pid))
                    Say(pid, string.Join("|", f.Skip(2)));
                else log($"packet from pf '{playfieldName}': {string.Join("|", f)}");
            }
            catch (Exception e) { log("OnPacket threw: " + e); }
        }

        // ---- spawning (legacy API: the only spawn that takes faction + name) --------------------

        void Spawn(int pid, SpikeCommand cmd)
        {
            if (legacy == null) { Say(pid, "legacy API not attached"); return; }
            var prefab = cmd.Arg(0, SpikeCommand.DefaultPrefab);
            var count = cmd.IntArg(1, 1, 1, 10);
            SpikeCommand.ParseFaction(cmd.Arg(2, "polaris"), out var faction, out var factionId);
            var typeArg = cmd.Arg(3, "").ToUpperInvariant();
            var dist = cmd.FloatArg(4, 80f, 20f, 3000f);   // metres ahead of the player (was a fixed 300)
            var type = typeArg == "BA" ? EntityType.BA : typeArg == "CV" ? EntityType.CV : typeArg == "SV" ? EntityType.SV : typeArg == "HV" ? EntityType.HV
                     : prefab.IndexOf("_BA", StringComparison.OrdinalIgnoreCase) >= 0 || prefab.StartsWith("BA", StringComparison.OrdinalIgnoreCase) ? EntityType.BA
                     : prefab.StartsWith("SV", StringComparison.OrdinalIgnoreCase) ? EntityType.SV : EntityType.CV;

            RequestPlayerInfo(pid, p =>
            {
                for (int i = 0; i < count; i++)
                {
                    var place = SpawnGeometry.Place(
                        new SpawnGeometry.V3(p.pos.x, p.pos.y, p.pos.z),
                        new SpawnGeometry.V3(p.rot.x, p.rot.y, p.rot.z), dist, i);
                    var pos = place.Pos; var rot = place.Rot;
                    var name = StructureTag.Apply($"Spike {++spawnCounter}");
                    // forceEntityId=0 is taken literally (the first spike got id 0), so ask for a fresh id first.
                    RequestNewEntityId(newId =>
                    {
                        // Our own prefabs ship in Content/Mods/LivingSystems/prefabs/ (never inside RE2).
                        var ownDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(LivingSystemsMod).Assembly.Location) ?? ".", "prefabs");
                        var useOwn = System.IO.File.Exists(System.IO.Path.Combine(ownDir, prefab + ".epb"));
                        var info = new EntitySpawnInfo
                        {
                            prefabDir = useOwn ? ownDir : null,
                            forceEntityId = newId,
                            playfield = p.playfield,
                            pos = new PVector3(pos.X, pos.Y, pos.Z),
                            rot = new PVector3(rot.X, rot.Y, rot.Z),
                            name = name,
                            type = (byte)type,
                            entityTypeName = "",
                            prefabName = prefab,
                            factionGroup = faction,
                            factionId = factionId,
                        };
                        // Legacy spawn ignores factionGroup: ask the PfServer to SetFaction once the structure is ready.
                        api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, p.playfield, Wire.Pack(pid, "wantfaction", newId, faction, factionId));
                        var s2 = NextSeq();
                        legacy.Game_Request(CmdId.Request_Entity_Spawn, s2, info);
                        OnSpawned?.Invoke(newId, name, p.playfield);
                        log($"spawn seq={s2} id={newId} '{name}' prefab={prefab}{(useOwn ? " (own dir " + ownDir + ")" : "")} type={type} faction={faction}:{factionId} pf='{p.playfield}' at {pos} yaw={rot.Y:0} (player at {p.pos.x:0},{p.pos.y:0},{p.pos.z:0})");
                    });
                }
                Say(pid, $"spawning {count}x {prefab} ({type}) ~{dist:0} m ahead of you");
            });
        }

        /// <summary>Spawn one ship we fly (registered as ours), faction set by the PfServer once it's ready.</summary>
        /// <summary>For the offline sim: spawn a ship (registered via OnSpawned) / destroy one anywhere.</summary>
        public void Spawn(string playfield, string prefab, PVector3 pos, PVector3 rot, string name, byte group, Action<int> spawned) =>
            SpawnShip(playfield, prefab, pos, rot, name, group, 0, spawned);
        /// <summary>Spawn as a BASE (BA): station services (refuel/repair) need a base, not a ship.</summary>
        public void SpawnBase(string playfield, string prefab, PVector3 pos, PVector3 rot, string name, byte group, Action<int> spawned) =>
            SpawnShip(playfield, prefab, pos, rot, name, group, 0, spawned, EntityType.BA);
        /// <summary>Eden / RE2 base prefabs by name (BA_, BAO_, BAJ_, TSO_ …).</summary>
        public static bool LooksLikeBase(string prefab) =>
            prefab != null && System.Text.RegularExpressions.Regex.IsMatch(prefab, @"(^|_)(BA|BAO|BAJ|BAOJ|TSO)_|^R2BAO_|^RE2_BAO_");
        public void Destroy(int id) { if (legacy != null) { Request(CmdId.Request_Entity_Destroy, new Id { id = id }); OnDestroyed?.Invoke(id); } }

        void SpawnShip(string playfield, string prefab, PVector3 pos, PVector3 rot, string name, byte group, int factionId, Action<int> spawned, EntityType type = EntityType.CV)
        {
            if (legacy == null) { log("SpawnShip: legacy API not attached"); return; }
            RequestNewEntityId(newId =>
            {
                api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, playfield, Wire.Pack(0, "wantfaction", newId, group, factionId));
                var s = NextSeq();
                // our own prefabs (Content/Mods/LivingSystems/prefabs/, e.g. a re-centred tug) win over the scenario's
                var ownDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(LivingSystemsMod).Assembly.Location) ?? ".", "prefabs");
                var useOwn = System.IO.File.Exists(System.IO.Path.Combine(ownDir, prefab + ".epb"));
                legacy.Game_Request(CmdId.Request_Entity_Spawn, s, new EntitySpawnInfo
                {
                    forceEntityId = newId, playfield = playfield, pos = pos, rot = rot, name = name, prefabDir = useOwn ? ownDir : null,
                    type = (byte)type, entityTypeName = "", prefabName = prefab, factionGroup = group, factionId = factionId,
                });
                OnSpawned?.Invoke(newId, name, playfield);
                log($"spawn seq={s} id={newId} '{name}' prefab={prefab} faction={group}:{factionId} pf='{playfield}' at {pos.x:0},{pos.y:0},{pos.z:0}");
                spawned?.Invoke(newId);
            });
        }

        void RequestNewEntityId(Action<int> then)
        {
            var s = NextSeq();
            pendingNewId[s] = then;
            legacy.Game_Request(CmdId.Request_NewEntityId, s, null);
        }

        void RequestFactions(int pid)
        {
            var s = NextSeq();
            pendingFactions[s] = pid;
            legacy.Game_Request(CmdId.Request_Get_Factions, s, new Id { id = 1 });
        }


        /// <summary>
        /// Stage 2 probe (PLAN START HERE): does legacy Request_Entity_ChangePlayfield move a structure whose source
        /// and/or target playfield is NOT loaded? Keeps the entity id if it works. Check with /ls gsinfo afterwards.
        /// </summary>
        void MovePlayfield(int pid, SpikeCommand cmd)
        {
            if (cmd.Args.Count < 2 || !int.TryParse(cmd.Args[0], out var id)) { Say(pid, "/ls movepf <id> [x y z] <playfield>"); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var pos = new PVector3(0, 0, 0); var rest = 1;
            if (cmd.Args.Count >= 5 && float.TryParse(cmd.Args[1], System.Globalization.NumberStyles.Float, inv, out var x)
                && float.TryParse(cmd.Args[2], System.Globalization.NumberStyles.Float, inv, out var y)
                && float.TryParse(cmd.Args[3], System.Globalization.NumberStyles.Float, inv, out var z)) { pos = new PVector3(x, y, z); rest = 4; }
            var target = string.Join(" ", cmd.Args.Skip(rest));
            var s = Request(CmdId.Request_Entity_ChangePlayfield, new IdPlayfieldPositionRotation { id = id, playfield = target, pos = pos, rot = new PVector3(0, 0, 0) });
            pendingReply[s] = pid;
            log($"movepf {id} -> '{target}' ({pos.x}, {pos.y}, {pos.z}) seq={s}");
            Say(pid, $"ChangePlayfield {id} -> '{target}' sent (seq {s}). Then /ls gsinfo {id}");
        }
        readonly Dictionary<ushort, int> pendingReply = new Dictionary<ushort, int>();

        ushort Request(CmdId c, object d) { var s = NextSeq(); legacy.Game_Request(c, s, d); return s; }
        /// <summary>Pay a player (official legacy Request_Player_AddCredits).</summary>
        public void GiveCredits(int pid, int credits) { if (legacy != null && credits > 0) Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = credits }); }

        public void OnUpdate()
        {
            try { if (legacy != null) trade?.Pump(Request); } catch (Exception e) { log("trade pump threw: " + e.Message); }
            try { PumpCalls(); } catch (Exception e) { log("call-ins threw: " + e.Message); }
        }

        ushort NextSeq() { if (++seq < 1000) seq = 1000; return seq; }

        void RequestPlayerInfo(int pid, Action<PlayerInfo> then)
        {
            var s = NextSeq();
            pendingPlayerInfo[s] = then;
            legacy.Game_Request(CmdId.Request_Player_Info, s, new Id { id = pid });
        }

        public void OnLegacyEvent(CmdId id, ushort seqNr, object data)
        {
            // Probe: log EVERY game-pushed legacy event (seq < 1000 = not a reply to us), with its payload's fields.
            if (seqNr < 1000) log($"LEGACY EVT {id} seq={seqNr} {data?.GetType().Name ?? "null"}");
            if (id == CmdId.Event_GameEvent)
            {
                // Probe: which game events reach the server? (KilledEntity / AttackedEntity / WindowOpened would be gold)
                if (data is GameEventData g)
                    log($"LEGACY GAMEEVENT type={g.EventType} name='{g.Name}' t={g.Type} amount={g.Amount} player={g.PlayerId} flag={g.Flag} items={g.ItemStacks?.Length ?? 0}");
                else log($"LEGACY GAMEEVENT data={data?.GetType().Name}");
                return;
            }
            if (id == CmdId.Event_TraderNPCItemSold)
            {
                // Native NPC trade, pushed by the game (verified to exist in the API; semantics under test).
                var t = data as TraderNPCItemSoldInfo;
                log($"NATIVE TRADE player={t?.playerEntityId} trader={t?.traderEntityId} type='{t?.traderType}' struct={t?.structEntityId} " +
                    $"item={t?.boughtItemId}({trade.Name(t?.boughtItemId ?? 0)}) count={t?.boughtItemCount} price={t?.boughtItemPrice} seq={seqNr}");
                if (t != null) Say(t.playerEntityId, $"[trade seen] item {trade.Name(t.boughtItemId)} x{t.boughtItemCount} @ {t.boughtItemPrice} from struct {t.structEntityId} ({t.traderType})");
                return;
            }
            if (seqNr < 1000 && id != CmdId.Event_ChatMessage && id != CmdId.Event_Player_ItemExchange) return;   // not ours
            switch (id)
            {
                case CmdId.Event_Player_Info:
                    if (pendingPlayerInfo.TryGetValue(seqNr, out var then)) { pendingPlayerInfo.Remove(seqNr); then((PlayerInfo)data); }
                    break;
                case CmdId.Event_Player_ItemExchange:
                    trade.OnExchange(seqNr, data as ItemExchangeInfo, Request);
                    break;
                case CmdId.Event_Player_Credits:
                    log($"credits event seq={seqNr}: {(data as IdCredits)?.credits}");
                    break;
                case CmdId.Event_Structure_BlockStatistics:
                {
                    var bs = data as IdStructureBlockInfo;
                    var stats = bs?.blockStatistics ?? new Dictionary<int, int>();
                    foreach (var kv in stats.OrderByDescending(kv => kv.Value)) log($"gstats {bs?.id}: {trade.Name(kv.Key)} ({kv.Key}) x{kv.Value}");
                    var line = $"{bs?.id}: {stats.Count} block types, {stats.Values.Sum()} blocks. " +
                        string.Join(", ", stats.OrderByDescending(kv => kv.Value).Take(10).Select(kv => $"{trade.Name(kv.Key)}x{kv.Value}"));
                    log("gstats " + line);
                    if (gstatsFor != 0) Say(gstatsFor, line + " (full list in log)");
                    break;
                }
                case CmdId.Event_GlobalStructure_List:
                    try { OnGlobalList?.Invoke(data as GlobalStructureList); } catch (Exception e) { log("global list hook threw: " + e); }
                    if (gsinfoFor != 0)
                    {
                        var gl2 = data as GlobalStructureList;
                        foreach (var kv in gl2?.globalStructures ?? new Dictionary<string, List<GlobalStructureInfo>>())
                            foreach (var gs in kv.Value)
                                if (gsinfoIds.Contains(gs.id))
                                {
                                    var line = $"{gs.id} '{gs.name}' in '{kv.Key}' pos=({gs.pos.x:0},{gs.pos.y:0},{gs.pos.z:0}): factionGroup={gs.factionGroup} factionId={gs.factionId} coreType={gs.coreType} type={gs.type} blocks={gs.cntBlocks}";
                                    log("gsinfo " + line); Say(gsinfoFor, line);
                                }
                        gsinfoFor = 0;
                    }
                    if (purgeFor != 0)
                    {
                        var gl = data as GlobalStructureList; var n = 0;
                        foreach (var kv in gl?.globalStructures ?? new Dictionary<string, List<GlobalStructureInfo>>())
                            foreach (var gs in kv.Value)
                                if (StructureTag.IsTagged(gs.name)) { Request(CmdId.Request_Entity_Destroy, new Id { id = gs.id }); n++; log($"purge global: destroy {gs.id} '{gs.name}' in '{kv.Key}'"); }
                        Say(purgeFor, $"global purge: destroy requested for {n} «LS» structure(s). Check again in a few seconds.");
                        purgeFor = 0;
                    }
                    break;
                case CmdId.Event_NewEntityId:
                    if (pendingNewId.TryGetValue(seqNr, out var withId)) { pendingNewId.Remove(seqNr); withId(((Id)data).id); }
                    break;
                case CmdId.Event_Get_Factions:
                    if (pendingFactions.TryGetValue(seqNr, out var fpid))
                    {
                        pendingFactions.Remove(seqNr);
                        var list = (data as FactionInfoList)?.factions ?? new List<FactionInfo>();
                        foreach (var f in list) log($"faction origin={f.origin} id={f.factionId} name='{f.name}' abbrev='{f.abbrev}'");
                        Say(fpid, $"{list.Count} factions: " + string.Join(", ", list.Take(25).Select(f => $"{f.abbrev ?? f.name}={f.origin}:{f.factionId}")));
                    }
                    break;
                case CmdId.Event_ChatMessage:
                    if (data is ChatInfo c && c.msg != null && c.msg.StartsWith("/ls")) log($"(legacy chat event also seen: '{c.msg}')");
                    break;
                case CmdId.Event_Ok:
                    log($"Event_Ok seq={seqNr}");
                    if (pendingReply.TryGetValue(seqNr, out var okPid)) { pendingReply.Remove(seqNr); Say(okPid, $"seq {seqNr}: Ok"); }
                    break;
                case CmdId.Event_Error:
                    var err = data as ErrorInfo;
                    log($"Event_Error seq={seqNr}: {err?.errorType}");
                    if (pendingReply.TryGetValue(seqNr, out var erPid)) { pendingReply.Remove(seqNr); Say(erPid, $"seq {seqNr}: Error {err?.errorType}"); }
                    break;
                default:
                    log($"legacy event {id} seq={seqNr}");
                    break;
            }
        }

        static string Clip(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");

        void ShowTraderDialog(int pid)
        {
            var cfg = new DialogConfig
            {
                TitleText = "«LS» Kestrel — Polaris freighter",
                BodyText = "Hey chief. We're hauling ore to Beta. What do you want?",
                ButtonTexts = new[] { "Trade", "Ask for escort", "Demand cargo", "Close" },
                ButtonIdxForEnter = 0,
                ButtonIdxForEsc = 3,
            };
            var ok = api.Application.ShowDialogBox(pid, cfg, OnTraderChoice, 1);
            log($"ShowDialogBox(level 1) -> {ok}");
        }

        /// <summary>
        /// Probe: what the dialog box renders (rich text, colours, sizes) and whether clickable links / the text box come back
        /// in the handler. "/ls dlg" = formatting + links (stays open on a link click), "/ls dlg close" = closes on click,
        /// "/ls dlg input" = with a text box.
        /// </summary>
        void DialogProbe(int pid, string mode, string last = null)
        {
            var body = (last != null ? $"<color=#ffd040>Last answer: {last}</color>\n\n" : "") +
                "<b>bold</b>  <i>italic</i>  <u>underline</u>  <s>strike</s>  <b><i><u>all three</u></i></b>\n" +
                "<color=#ff4040>red</color> <color=#40ff40>green</color> <color=#4080ff>blue</color> <color=yellow>named yellow</color> <color=#ffffff80>half-alpha</color>\n" +
                "<size=80%>size 80%</size>  <size=130%>size 130%</size>  <size=24>size 24</size>\n" +
                "<mark=#ff000060>marked</mark>  <sup>sup</sup>x<sub>sub</sub>  <alpha=#60>alpha tag</alpha>  <smallcaps>Small Caps</smallcaps>\n" +
                "<mspace=0.7em>mono 123 abc</mspace>   <cspace=0.3em>spaced</cspace>\n" +
                "<align=center>centred line</align>\n<align=right>right line</align>\n<indent=10%>indented line</indent>\n" +
                "Glyphs: ▲▼◀▶ ● ○ ■ □ ★ ✓ ✗ → ← ↑ ↓ ░▒▓█ │─┼\n\n" +
                "Links (click each):\n" +
                "  1 <link=\"q1\">plain link q1</link>\n" +
                "  2 <link=\"q2\"><u><color=#4af>styled link q2</color></u></link>\n" +
                "  3 <link=q3>unquoted link q3</link>\n" +
                "  4 <link=\"tug:queue:42\"><color=#4af>[Mark for salvage]</color></link>   <link=\"tug:cancel\"><color=#f84>[Cancel]</color></link>  (two on one line)\n" +
                "  5 <a href=\"q5\">html anchor q5</a>\n" +
                "  6 <link=\"q6\">▶ glyph link q6</link>";
            var cfg = new DialogConfig
            {
                TitleText = "<color=#4af>Dialog</color> <b>probe</b> — mode '" + mode + "'",
                BodyText = body,
                ButtonTexts = new[] { "One", "Two", "Three", "Four", "Five" },   // more than 3: how many show?
                ButtonIdxForEsc = -1,
                CloseOnLinkClick = mode == "close",
            };
            if (mode == "input") { cfg.MaxChars = 40; cfg.Placeholder = "type an amount…"; cfg.InitialContent = "100"; }
            var ok = api.Application.ShowDialogBox(pid, cfg, (b, link, input, p, cv) =>
            {
                var ans = $"button={b} link='{link}' input='{input}'";
                log("dlg probe answer: " + ans);
                Say(p, "dlg: " + ans);
                if (!string.IsNullOrEmpty(link) && mode != "close") DialogProbe(p, mode, ans);   // re-show so link clicks can be chained
            }, 7);
            log($"dlg probe shown to {pid} mode='{mode}' ok={ok}");
        }

        void OnTraderChoice(int buttonIdx, string linkId, string input, int playerId, int customValue)
        {
            log($"dialog answer: button={buttonIdx} link='{linkId}' input='{input}' player={playerId} custom={customValue}");
            if (customValue == 1 && buttonIdx == 0)
            {
                var ok = api.Application.ShowDialogBox(playerId, new DialogConfig
                {
                    TitleText = "«LS» Kestrel — trade",
                    BodyText = "We've got 220 ore and 80 fuel. Offer?",
                    ButtonTexts = new[] { "Buy fuel", "Back" },
                    ButtonIdxForEsc = 1,
                }, OnTraderChoice, 2);
                log($"ShowDialogBox(level 2) -> {ok}");
            }
            else Say(playerId, $"dialog OK: level {customValue}, button {buttonIdx}");
        }
    }
}
