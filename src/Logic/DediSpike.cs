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
    sealed class DediSpike
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
                if (m == null || !SpikeCommand.TryParse(m.Text, out var cmd)) return;
                var pid = m.SenderEntityId;
                log($"chat cmd from {pid}: {m.Text}");
                Handle(pid, cmd);
            }
            catch (Exception e) { log("OnChat threw: " + e); }
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
                case "sim": if (OnSim != null) OnSim(pid, cmd); else Say(pid, "sim not running (see log)"); break;
                case "kiosk": Kiosk(pid, cmd.Arg(0, "")); break;
                case "kit":
                {
                    // Everything needed to build a kiosk prefab. Items land in the TOOLBAR first.
                    var kit = new[] { P("CoreNPC", 2), P("Core", 2), P("HullLargeBlocks", 60), P("LightLargeBlocks", 4), P("Trader", 3) };   // no tuples: no ValueTuple in the game runtime
                    var gave = new List<string>();
                    foreach (var k in kit)
                        if (trade.TryId(k.Key, out var kid))
                        {
                            legacy.Game_Request(CmdId.Request_Player_AddItem, NextSeq(), new IdItemStack { id = pid, itemStack = new ItemStack { id = kid, count = k.Value } });
                            gave.Add($"{k.Key}x{k.Value}");
                        }
                        else gave.Add($"{k.Key}=?");
                    Say(pid, "kiosk kit (needs 5 free toolbar slots): " + string.Join(", ", gave));
                    break;
                }
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
                case "move": case "stop": case "clear": case "list": case "info": case "faction": case "goto": case "follow": case "scan": case "cargo": case "perf": case "npc": case "traderblock": case "holo": case "tp": case "find": case "spawn2": case "record": case "makekiosk": case "pad": case "padinfo": case "hail": case "comms": case "fly":
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
                if (f.Length >= 4 && f[0] == "export" && int.TryParse(f[1], out var epid) && int.TryParse(f[2], out var eid) && int.TryParse(f[3], out var etype))
                {
                    var path = KioskPath();
                    System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                    kioskType = (EntityType)etype;
                    var s = Request(CmdId.Request_Entity_Export, new EntityExportInfo { id = eid, playfield = playfieldName, filePath = path, isForceUnload = false });
                    log($"export {eid} ({kioskType}) from '{playfieldName}' to '{path}' seq={s}");
                    Say(epid, $"export requested -> {path}. Then: /ls kiosk");
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
                    if (hailContacts[hpid].Count > 0 && hailContacts[hpid][0].Nose) { hailIndex[hpid] = 0; hailFocus[hpid] = hailContacts[hpid][0]; ShowShip(hpid); }
                    else ShowContacts(hpid);
                    return;
                }
                if (f.Length >= 3 && f[0] == "kioskgone" && int.TryParse(f[1], out var gpid) && int.TryParse(f[2], out var gid))
                {
                    if (kioskOf.TryGetValue(gpid, out var cur) && cur == gid) kioskOf.Remove(gpid);
                    // Playfield RemoveEntity leaves the structure in the global structure DB (verified via gents): destroy it there too.
                    var ds = Request(CmdId.Request_Entity_Destroy, new Id { id = gid });
                    log($"kiosk/structure {gid} gone on the playfield; global destroy seq={ds}");
                    return;
                }
                if (f.Length >= 3 && f[0] == "npcname" && int.TryParse(f[1], out var nid))
                {
                    var s = Request(CmdId.Request_Entity_SetName, new IdPlayfieldName { id = nid, playfield = playfieldName, name = f[2] });
                    log($"SetName {nid} -> '{f[2]}' seq={s}");
                    return;
                }
                if (f.Length >= 8 && f[0] == "padspawn" && int.TryParse(f[1], out var ppid))
                {
                    float F(string v) => float.Parse(v, System.Globalization.CultureInfo.InvariantCulture);
                    SpawnKioskAt(ppid, playfieldName, new PVector3(F(f[2]), F(f[3]), F(f[4])), new PVector3(F(f[5]), F(f[6]), F(f[7])));
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
                legacy.Game_Request(CmdId.Request_Entity_Spawn, s, new EntitySpawnInfo
                {
                    forceEntityId = newId, playfield = playfield, pos = pos, rot = rot, name = name,
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

        static KeyValuePair<string, int> P(string k, int v) => new KeyValuePair<string, int>(k, v);

        // ---- kiosk: spawn a copy of the exported construct next to the player --------------------

        EntityType kioskType = EntityType.CV;
        readonly Dictionary<int, int> kioskOf = new Dictionary<int, int>();   // player -> kiosk entity id

        static string KioskPath() => System.IO.Path.Combine(
            System.IO.Path.GetDirectoryName(typeof(LivingSystemsMod).Assembly.Location) ?? ".", "prefabs", "LS_Kiosk.dat");

        void Kiosk(int pid, string arg)
        {
            if (arg == "off")
            {
                if (kioskOf.TryGetValue(pid, out var kid))
                {
                    // NPCs first (bodies otherwise stay behind), then the structure.
                    var pf = PlayfieldOf(pid);
                    if (pf != null) api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(pid, "kioskoff", kid));
                    else Request(CmdId.Request_Entity_Destroy, new Id { id = kid });   // player not on a playfield: best effort
                    kioskOf.Remove(pid); Say(pid, $"kiosk {kid} dismissed");
                }
                else Say(pid, "no kiosk of yours");
                return;
            }
            var path = KioskPath();
            if (!System.IO.File.Exists(path)) { Say(pid, $"no {path} yet: /ls makekiosk first (and wait a second for the export)"); return; }
            RequestPlayerInfo(pid, p =>
            {
                var place = SpawnGeometry.Place(new SpawnGeometry.V3(p.pos.x, p.pos.y, p.pos.z), new SpawnGeometry.V3(p.rot.x, p.rot.y, p.rot.z), 25f, 0);
                SpawnKioskAt(pid, p.playfield, new PVector3(place.Pos.X, place.Pos.Y, place.Pos.Z), new PVector3(0, place.Rot.Y, 0));
            });
        }

        void SpawnKioskAt(int pid, string playfield, PVector3 pos, PVector3 rot)
        {
            var path = KioskPath();
            if (!System.IO.File.Exists(path)) { Say(pid, "no kiosk export yet: /ls makekiosk first"); return; }
            if (kioskOf.TryGetValue(pid, out var old))
            {
                api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, playfield, Wire.Pack(pid, "kioskoff", old));
                Request(CmdId.Request_Entity_Destroy, new Id { id = old }); kioskOf.Remove(pid);
            }
            RequestNewEntityId(newId =>
            {
                var info = new EntitySpawnInfo
                {
                    forceEntityId = newId, playfield = playfield, pos = pos, rot = rot,
                    name = StructureTag.Apply(KioskNames.Kiosk), type = (byte)kioskType,
                    entityTypeName = "", exportedEntityDat = path, factionGroup = 7, factionId = 0,
                };
                api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, playfield, Wire.Pack(pid, "kioskfix", newId));
                var s = Request(CmdId.Request_Entity_Spawn, info);
                kioskOf[pid] = newId;
                log($"kiosk spawn seq={s} id={newId} type={kioskType} at ({pos.x:0.0},{pos.y:0.0},{pos.z:0.0}) rot ({rot.x:0},{rot.y:0},{rot.z:0})");
                Say(pid, $"kiosk {newId} deploying… '/ls kiosk off' to send it away.");
            });
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

        public void OnUpdate() { try { if (legacy != null) trade?.Pump(Request); } catch (Exception e) { log("trade pump threw: " + e.Message); } }

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
                case CmdId.Event_GlobalStructure_List:
                    try { OnGlobalList?.Invoke(data as GlobalStructureList); } catch (Exception e) { log("global list hook threw: " + e); }
                    if (gsinfoFor != 0)
                    {
                        var gl2 = data as GlobalStructureList;
                        foreach (var kv in gl2?.globalStructures ?? new Dictionary<string, List<GlobalStructureInfo>>())
                            foreach (var gs in kv.Value)
                                if (gsinfoIds.Contains(gs.id))
                                {
                                    var line = $"{gs.id} '{gs.name}' in '{kv.Key}' pos=({gs.pos.x:0},{gs.pos.y:0},{gs.pos.z:0}): factionGroup={gs.factionGroup} factionId={gs.factionId} coreType={gs.coreType} type={gs.type}";
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

        // ---- dialog (spike Q4): multi-button, two levels deep -----------------------------------

        // ---- hail: contacts -> ship -> who are you / request trade ---------------------------------

        sealed class Contact
        {
            public int Id, Group, FacId, Dist, Blocks; public bool OwnTrader, Nose; public string Name, Type, Core, Status = "", Says = "";
            public static Contact Parse(string s)
            {
                var p = s.Split('~'); if (p.Length < 8) return null;
                int.TryParse(p[0], out var id); int.TryParse(p[2], out var g); int.TryParse(p[3], out var fid);
                int.TryParse(p[4], out var d); int.TryParse(p[7], out var b);
                return new Contact { Id = id, Name = p[1], Group = g, FacId = fid, Dist = d, OwnTrader = p[5] == "1", Type = p[6], Blocks = b, Core = p.Length > 8 ? p[8] : "?", Status = p.Length > 9 ? p[9] : "", Says = p.Length > 10 ? p[10] : "", Nose = p.Length > 11 && p[11] == "1" };
            }
        }
        readonly Dictionary<int, List<Contact>> hailContacts = new Dictionary<int, List<Contact>>();
        readonly Dictionary<int, string> hailPf = new Dictionary<int, string>();
        readonly Dictionary<int, Contact> hailFocus = new Dictionary<int, Contact>();

        static HashSet<int> HostileGroups => Factions.Hostile;
        static string FactionName(int g) => Factions.Name(g);

        // The dialog shows at most 3 buttons (verified), and long labels wrap. So: a pager over contacts,
        // details in the body, three short buttons, Esc closes.
        readonly Dictionary<int, int> hailIndex = new Dictionary<int, int>();

        string Describe(Contact c, int i, int n) =>
            $"Contact {i + 1} of {n}\n\n" +
            $"<b>{c.Name}</b>\n" +
            $"Faction: {FactionName(c.Group)}{(HostileGroups.Contains(c.Group) ? "  (hostile)" : "")}\n" +
            $"Type: {c.Type}, {c.Blocks} blocks, core {c.Core}\n" +
            $"Range: {c.Dist:0} m{(c.Dist > ApproveRange ? $"  — too far to trade ({ApproveRange} m)" : "")}\n" +
            $"Trade: {(IsAutomated(c) ? "none — automated structure" : c.OwnTrader ? "trader aboard — dock with them" : "kiosk to your TRADE_PAD")}" +
            StatusLine(c);

        void ShowContacts(int pid)
        {
            var cs = hailContacts.TryGetValue(pid, out var l) ? l : new List<Contact>();
            if (cs.Count == 0) { Say(pid, "Scanning… no NPC vessels or stations within 5 km."); return; }
            var i = hailIndex.TryGetValue(pid, out var k) ? Math.Max(0, Math.Min(k, cs.Count - 1)) : 0;
            hailIndex[pid] = i;
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = "COMMS — contacts in range",
                BodyText = Describe(cs[i], i, cs.Count) + "\n\n(Esc to close)",
                ButtonTexts = new[] { "< Prev", "Hail", "Next >" },
                ButtonIdxForEnter = 1, ButtonIdxForEsc = -1,
            }, OnContactsButton, 100);
        }

        void OnContactsButton(int button, string link, string input, int pid, int cv)
        {
            if (!hailContacts.TryGetValue(pid, out var cs) || cs.Count == 0) return;
            var i = hailIndex.TryGetValue(pid, out var k) ? k : 0;
            if (button == 0) { hailIndex[pid] = (i - 1 + cs.Count) % cs.Count; ShowContacts(pid); }
            else if (button == 2) { hailIndex[pid] = (i + 1) % cs.Count; ShowContacts(pid); }
            else if (button == 1)
            {
                hailFocus[pid] = cs[i];
                if (IsAutomated(cs[i])) { ShowShip(pid, $"{cs[i].Name}: automated structure. No crew aboard. No response."); return; }
                ShowShip(pid);
            }
            // anything else (Esc) closes
        }

        /// <summary>Hail → Talk: a sim ship's rumours (DediSim.Talk: entity, pid, n → text; null = not a sim ship).</summary>
        public Func<int, int, int, string> TalkFor;
        readonly Dictionary<int, int> talkIndex = new Dictionary<int, int>();

        // Dialogs show at most 3 buttons. Our ships: Talk | Trade | Jobs (Esc = leave); others: Trade | Back.
        // Orders (follow / hold / join) are parked: ShowOrders stays in code, not on the menu.
        void ShowShip(int pid, string reply = null)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return;
            if (StructureTag.IsTagged(c.Name) && TollFor?.Invoke(c.Id, pid) is DediSim.Toll toll) { ShowPirate(pid, c, toll, reply); return; }
            if (StructureTag.IsTagged(c.Name) && CollectFor != null && legacy != null && TradeLineFor?.Invoke(c.Id) != null)   // traders pay, not wrecks / patrols / raiders
            {   // a bounty settled since: any trader pays it
                var paid = CollectFor(pid, out var credits);
                if (paid != null && credits > 0) { Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = credits }); reply = paid + (reply != null ? "\n" + reply : ""); }
            }
            var what = StructureTag.IsTagged(c.Name) && TradeLineFor?.Invoke(c.Id) is string tl
                ? $"{tl}\nTrade within {ApproveRange} m."
                : c.OwnTrader ? "Trader aboard — dock with us to trade." : "No trader aboard; we can project a trade kiosk to your TRADE_PAD.";
            var ours = StructureTag.IsTagged(c.Name);   // our ships: third button = work (escort); Esc closes
            Echo(pid, c.Name, reply ?? c.Says);
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = $"{c.Name} [{FactionName(c.Group)}]",
                BodyText = (reply ?? (string.IsNullOrEmpty(c.Says) ? "Channel open." : $"\"{c.Says}\"")) + StatusLine(c) + $"\n\n{c.Type}, {c.Blocks} blocks. Range {c.Dist:0} m.\n{what}\n(Esc to close)",
                ButtonTexts = ours ? new[] { "Talk", "Trade", "Jobs" } : new[] { "Trade", "Back" },
                ButtonIdxForEsc = -1,
            }, OnShipOption, 101);
        }

        void OnShipOption(int button, string link, string input, int pid, int cv)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return;
            if (!StructureTag.IsTagged(c.Name))
            {
                if (button == 0) RequestTrade(pid, c);
                else if (button == 1) ShowContacts(pid);
                return;
            }
            if (button == 0) { talkIndex[pid] = 0; ShowTalk(pid); return; }
            if (button == 1) { RequestTrade(pid, c); return; }
            if (button == 2) ShowJobs(pid);
        }

        /// <summary>A pirate answers a hail: its price to look the other way — Pay | Back. Paying makes it break off and leave you be.</summary>
        void ShowPirate(int pid, Contact c, DediSim.Toll toll, string reply)
        {
            var text = reply ?? toll.Line;
            Echo(pid, c.Name, text);
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = $"{c.Name} [Pirates]",
                BodyText = $"\"{text}\"\n\nRange {c.Dist:0} m.\n(Esc to close)",
                ButtonTexts = toll.Price > 0 ? new[] { $"Pay {toll.Price:N0} cr", "Back" } : new[] { "Back" },
                ButtonIdxForEsc = -1,
            }, (button, link, input, p, cv) =>
            {
                if (!hailFocus.TryGetValue(p, out var cc) || cc.Id != c.Id) return;
                if (toll.Price > 0 && button == 0) PayToll(p, cc, toll.Price);
                else ShowContacts(p);
            }, 108);
        }

        void PayToll(int pid, Contact c, int price)
        {
            if (legacy == null || !hailPf.TryGetValue(pid, out var pf)) { Say(pid, "Lost contact."); return; }
            RequestPlayerInfo(pid, info =>
            {
                // the dialog may have sat open: still alive, still the same price? (no paying a dead pirate)
                var now = TollFor?.Invoke(c.Id, pid);
                if (now == null || now.Price <= 0) { Echo(pid, c.Name, "(no answer — they're gone)"); return; }
                if (now.Price != price) { Echo(pid, c.Name, $"Price changed: {now.Price:N0} cr. Hail again."); return; }
                if (info == null || info.credits < price) { Echo(pid, c.Name, "You haven't got that kind of money. Shame."); return; }
                Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = -price });
                api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(0, "fly", "sim", "paid", c.Id, pid));
                TollPaid?.Invoke(c.Id, price);   // the pirate pockets it (banked at its hub, or in its wreck if you shoot it after all)
                Echo(pid, c.Name, $"Pleasure doing business. ({price:N0} cr paid)");
                log($"toll: {pid} paid {price} cr to {c.Id} '{c.Name}'");
            });
        }

        /// <summary>Talk: what the crew has heard (rumours from the sim) — "Ask more" walks through them.</summary>
        void ShowTalk(int pid)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return;
            var n = talkIndex.TryGetValue(pid, out var k) ? k : 0;
            var text = TalkFor?.Invoke(c.Id, pid, n) ?? (string.IsNullOrEmpty(c.Says) ? "Not much to say." : c.Says);
            Echo(pid, c.Name, System.Text.RegularExpressions.Regex.Replace(text, @"\s*\(\d+ of \d+\)$", ""));
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = $"{c.Name} — talk",
                BodyText = $"\"{text}\"\n\n(Esc to close)",
                ButtonTexts = new[] { "Ask more", "Back" },
                ButtonIdxForEsc = -1,
            }, (button, link, input, p, cv) =>
            {
                if (button == 0) { talkIndex[p] = (talkIndex.TryGetValue(p, out var i) ? i : 0) + 1; ShowTalk(p); }
                else if (button == 1) ShowShip(p);
            }, 106);
        }

        /// <summary>Jobs: work this ship offers. Escort today (it decides per leg whether it wants one); more jobs later.</summary>
        void ShowJobs(int pid)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return;
            var bounty = BountyFor?.Invoke(c.Id, pid);
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = $"{c.Name} — jobs",
                BodyText = "Work we might have for you:\n• Escort — see us safely to our jump." + (bounty != null ? "\n• Bounty — there's a pirate we want dead." : "") + "\n\n(Esc to go back)",
                ButtonTexts = bounty != null ? new[] { "Escort?", "Bounty?" } : new[] { "Escort?" },
                ButtonIdxForEsc = -1,
            }, (button, link, input, p, cv) =>
            {
                if (!hailFocus.TryGetValue(p, out var cc)) return;
                if (button == 0) SendOrder(p, cc, "escort", 0);
                else if (button == 1 && bounty != null) ShowBounty(p, cc, bounty);
                else ShowShip(p);
            }, 107);
        }

        /// <summary>The bounty on offer: Accept | Back.</summary>
        void ShowBounty(int pid, Contact c, string text)
        {
            Echo(pid, c.Name, text);
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = $"{c.Name} — bounty",
                BodyText = $"\"{text}\"\n\n(Esc to go back)",
                ButtonTexts = new[] { "Accept", "Back" },
                ButtonIdxForEsc = -1,
            }, (button, link, input, p, cv) =>
            {
                if (!hailFocus.TryGetValue(p, out var cc) || cc.Id != c.Id) return;
                if (button == 0) ShowShip(p, AcceptBountyFor?.Invoke(cc.Id, p) ?? "That job's gone.");
                else ShowJobs(p);
            }, 109);
        }

        void ShowOrders(int pid, string reply = null)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return;
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = $"{c.Name} — orders",
                BodyText = (reply ?? "Orders?") + StatusLine(c) + "\n\n(Esc to close)",
                ButtonTexts = new[] { "Follow me", "Hold position", "More…" },
                ButtonIdxForEsc = -1,
            }, OnOrder, 102);
        }

        /// <summary>Second orders page (3-button limit): faction join/leave, release, back.</summary>
        void ShowMoreOrders(int pid)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return;
            var joined = (c.Status ?? "").Contains("under your colours");
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = $"{c.Name} — orders",
                BodyText = (joined ? "Flying under your colours: your enemies are ours."
                                   : "Join your faction: we fly under your colours and our turrets treat your enemies as ours.") +
                           StatusLine(c) + "\n\n(Esc to close)",
                ButtonTexts = new[] { joined ? "Leave my faction" : "Join my faction", "Release", "Back" },
                ButtonIdxForEsc = -1,
            }, (button, link, input, p, cv) =>
            {
                if (!hailFocus.TryGetValue(p, out var cc)) return;
                if (button == 0) SendOrder(p, cc, joined ? "leave" : "join", 0);
                else if (button == 1) SendOrder(p, cc, "release", 0);
                else if (button == 2) ShowOrders(p);
            }, 104);
        }

        /// <summary>The trader's escort offer (text from its PfServer): Accept | Decline | Back.</summary>
        void ShowEscortOffer(int pid, int shipId, string text)
        {
            if (!hailFocus.TryGetValue(pid, out var c) || c.Id != shipId) { Say(pid, text); return; }
            Echo(pid, c.Name, text);
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = $"{c.Name} — escort contract",
                BodyText = text + "\n\n(Esc to close)",
                ButtonTexts = new[] { "Accept", "Decline", "Back" },
                ButtonIdxForEsc = -1,
            }, (button, link, input, p, cv) =>
            {
                if (!hailFocus.TryGetValue(p, out var cc) || cc.Id != shipId) return;
                if (button == 0) SendOrder(p, cc, "accept", 0);
                else if (button == 1) SendOrder(p, cc, "decline", 0);
                else if (button == 2) ShowShip(p);
            }, 105);
        }

        static string StatusLine(Contact c) => string.IsNullOrEmpty(c.Status) ? "" : $"\nStatus: {c.Status}.";

        void OnOrder(int button, string link, string input, int pid, int cv)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return;
            if (button == 2) { ShowMoreOrders(pid); return; }
            if (button == 1) { SendOrder(pid, c, "hold", 0); return; }
            if (button == 0)
                api.Application.ShowDialogBox(pid, new DialogConfig
                {
                    TitleText = $"{c.Name} — follow",
                    BodyText = "At what distance?\n\n(Esc to close)",
                    ButtonTexts = new[] { "Close (100 m)", "Medium (250 m)", "Far (500 m)" },
                    ButtonIdxForEsc = -1,
                }, OnFollowDistance, 103);
        }

        void OnFollowDistance(int button, string link, string input, int pid, int cv)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return;
            var d = button == 0 ? 100 : button == 1 ? 250 : button == 2 ? 500 : -1;
            if (d > 0) SendOrder(pid, c, "follow", d);
        }

        /// <summary>Directed order to one ship; its PfServer answers with "orderreply", shown in the ship dialog.</summary>
        void SendOrder(int pid, Contact c, string kind, int distance)
        {
            if (!hailPf.TryGetValue(pid, out var pf)) { Say(pid, "Lost contact."); return; }
            api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, kind == "escort" || kind == "accept" || kind == "decline"
                ? Wire.Pack(pid, "fly", "escort", kind == "escort" ? "offer" : kind, c.Id)
                : Wire.Pack(pid, "fly", "order", c.Id, kind, distance));
            log($"order {pid} -> {c.Id} '{c.Name}': {kind} {distance}");
        }

        /// <summary>Hail → Trade with a sim trader: its hold (DediSim fills it in; null = not a sim trader).</summary>
        public Func<int, int, DediTrade.ShipDeal> ShipDealFor;   // (ship entity, player)
        /// <summary>Hail screen: "Selling: … Buys: …" for a sim trader (null = not one).</summary>
        public Func<int, string> TradeLineFor;
        /// <summary>A pirate's price to look the other way (null = not one of our pirates).</summary>
        public Func<int, int, DediSim.Toll> TollFor;
        public Action<int, int> TollPaid;
        /// <summary>Bounties: what this ship offers (text, null = none), taking it, and paying out settled ones on any hail.</summary>
        public Func<int, int, string> BountyFor, AcceptBountyFor;
        public CollectDelegate CollectFor;
        public delegate string CollectDelegate(int pid, out int credits);

        void RequestTrade(int pid, Contact c)
        {
            if (StructureTag.IsTagged(c.Name) && ShipDealFor?.Invoke(c.Id, pid) is DediTrade.ShipDeal deal)
            {   // our traders sell from their own hold — heave-to range, no TRADE_PAD needed
                if (c.Dist > ApproveRange) { ShowShip(pid, $"You're {c.Dist:0} m out. Come within {ApproveRange} m and we'll open the hold."); return; }
                if (legacy == null) { ShowShip(pid, "(trade needs the legacy API)"); return; }
                if (!trade.OpenShip(pid, c.Id, deal, Request)) ShowShip(pid, "Can't open the hold right now — try again in a moment.");
                return;
            }
            string answer;
            if (IsAutomated(c)) answer = "No response. It's an automated structure.";
            else if (HostileGroups.Contains(c.Group)) answer = "Not dealing with you. Channel closed.";
            else if (c.OwnTrader) answer = "Our trader is aboard — dock with us and talk to them in person.";
            else if (c.Dist > ApproveRange) answer = $"You're {c.Dist:0} m out (as of your scan). Come within {ApproveRange} m and hail again.";
            else
            {
                answer = "Approved. Activate your TRADE_PAD and we'll project a kiosk.";
                if (hailPf.TryGetValue(pid, out var pf)) api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(pid, "partner", c.Id));
            }
            log($"trade request {pid} -> {c.Id} '{c.Name}': {answer}");
            ShowShip(pid, answer);
        }

        const int ApproveRange = 1100;   // approve trade within this; the kiosk only ends beyond the PfServer's 1300 (hysteresis)

        /// <summary>Unmanned = small bases and obvious automata. They answer, but can't trade.</summary>
        static bool IsAutomated(Contact c)
        {
            var n = (c.Name ?? "").ToLowerInvariant();
            if (new[] { "satellite", "beacon", "relay", "drone", "wreck", "debris", "buoy", "sign", "gate", "jump" }.Any(n.Contains)) return true;
            // Admin core + no trader aboard = infrastructure, any size (trading stations have a trader, so they're excluded).
            if ((c.Core ?? "").Contains("Admin") && !c.OwnTrader) return true;
            return c.Type == "BA" && c.Blocks > 0 && c.Blocks < 300;
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
