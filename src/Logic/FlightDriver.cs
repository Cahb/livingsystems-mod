using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using LivingSystems.Core.Flight;
using LivingSystems.Core.Registry;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// Game side of the flight backbone (PfServer). Gathers facts (position, rotation, mass), runs the pure
    /// <see cref="FlightController"/>, applies the result (set Rotation, <c>Move</c> push). Not spike code.
    ///
    /// Commands (/ls fly ...):
    ///   cal &lt;axis&gt; &lt;k&gt; &lt;sec&gt; [brake|coast]  push Move(axis*k) for sec, then stop; logs v and a every 0.25 s
    ///                                     axis = fwd|back|up|down|right|left (ship-local)
    ///   follow [dist] [cruise]           every «LS» ship follows you
    ///   hold                             every «LS» ship brakes and holds
    ///   off                              release all ships (one Move(zero))
    ///   probe                            log every state flag of the next spawned ship until it obeys Move
    ///   idle auto|brake|coast            what to send when the controller wants ~0 accel
    /// Push gains are learned per ship and direction (<see cref="ResponseEstimator"/>); no manual gain.
    /// </summary>
    sealed class FlightDriver
    {
        readonly IModApi api;
        readonly Action<string> log;
        IPlayfield playfield;

        readonly Stopwatch clock = Stopwatch.StartNew();
        double lastTick, nextSeenScan;

        class Track
        {
            public Vector3 LastPos;
            public Vector3 Vel, LastVel;
            public bool HasPos, HasVel;
            public ShipState State;
            public double NextLog;
            public Vector3 AccelSum; public int AccelN;   // measured local accel, averaged per log line
            public Vector3 PushSum;                        // commanded local accel, same window
            public Vector3 FrameAccel;                     // measured local accel over the last frame
            public Vec3 LastThrottle;                      // throttle in effect since last frame (Move persists), each axis −1…1
            public float MassKg = -1; public double MassAt = -1;
            public readonly ResponseEstimator Response = new ResponseEstimator();
            public Responsiveness LoggedState = (Responsiveness)(-1);
            public Quaternion RotSet; public bool HasRotSet;   // what we set last frame, to see if the game keeps it
            public Quat OwnRot; public bool HasOwnRot;          // our intended rotation (the game only eases toward what we set)
            public float RotDrift, RotErr;                      // degrees, worst in the log window
            public Quaternion LogRot; public double LogRotAt; public bool HasLogRot;   // actual turn rate per log window
            public float CmdTurnSum; public int CmdTurnN;
        }
        readonly Dictionary<int, Track> tracks = new Dictionary<int, Track>();

        /// <summary>Ships seen so far (the probe only watches ships spawned after it was armed).</summary>
        readonly Dictionary<int, double> firstSeen = new Dictionary<int, double>();

        /// <summary>One ship's standing order. Directed: each ship has its own (from the hail menu or /ls fly).</summary>
        sealed class Order
        {
            public string Kind;          // "follow" | "hold" | "goto" | "intercept"
            public string LeaderSteamId = "", LeaderName = "";   // follow: whom (stable across sessions); goto: destination name
            public int TargetId = -1;    // intercept: the entity to close on (not persisted: pirates idle after a reload)
            public int LeaderPid = -1;   // follow: the leader's entity id while in this playfield, else -1 (resolved live)
            public float Distance = 150f, Cruise = 40f;
            public Vector3 HoldAt;       // hold: the spot to keep
            public Vector3 WaitAt; public bool Waiting;   // follow with the leader away: hold here until they're back
        }
        readonly Dictionary<int, Order> orders = new Dictionary<int, Order>();

        // ---- registry (Dedi owns the file; see DediRegistry) ----
        /// <summary>Stored records for this playfield not yet matched to a ship (arrive after load / logic reload).</summary>
        readonly Dictionary<int, ShipRecord> stored = new Dictionary<int, ShipRecord>();
        /// <summary>
        /// Ships that joined a player's faction: their original faction ("group:id"), from the registry
        /// (<c>faction.orig</c>) — the game save keeps the temporary faction, only we know what to restore.
        /// </summary>
        readonly Dictionary<int, string> factionOrig = new Dictionary<int, string>();

        /// <summary>
        /// Our ships in this playfield: entity ids the registry knows (spawned by us). Identity is the id, never the
        /// «LS» name — a player can rename their own ship to «LS» (we must never fly or destroy it) or rename ours.
        /// </summary>
        readonly HashSet<int> known = new HashSet<int>();
        /// <summary>«LS»-named structures that are NOT registered, already logged once.</summary>
        readonly HashSet<int> impostors = new HashSet<int>();

        public bool IsOurs(int entityId) => known.Contains(entityId);
        /// <summary>Set by SimLive: the entity is a live sim agent or a sim wreck.</summary>
        public Func<int, bool> SimOwns;
        public bool IsCombatant(int entityId) => combatants.Contains(entityId);
        /// <summary>A pirate: being shot is the point (no "you betrayed us" flight). Marked as soon as it's live — a logic
        /// reload empties the set, and a re-adopted pirate shot before it went for anyone would flee like a trader.</summary>
        public void MarkCombatant(int entityId)
        {
            if (!combatants.Add(entityId)) return;
            if (playfield?.Entities != null && playfield.Entities.TryGetValue(entityId, out var e) && e?.Structure != null && !startBlocks.ContainsKey(entityId)
                && int.TryParse(S(() => e.Structure.BlockCount), out var sb)) startBlocks[entityId] = sb;
        }
        /// <summary>(entity id, how): "dead" (core gone), "jumped" (warped out of the system), "claimed" (a player took it).</summary>
        public Action<int, string> ShipGone;
        void Gone(int id, string how) { try { ShipGone?.Invoke(id, how); } catch (Exception ex) { log("ShipGone threw: " + ex.Message); } }

        /// <summary>
        /// Recent real events in this playfield (newest last) for displays: orders, attacks, flight, warp, jumps.
        /// Later the Dedi blends in off-screen agent life (entered/left systems, destroyed elsewhere).
        /// </summary>
        public readonly List<KeyValuePair<DateTime, string>> Feed = new List<KeyValuePair<DateTime, string>>();
        public void AddEvent(string text) => Event(text);
        void Event(string text)
        {
            Feed.Add(new KeyValuePair<DateTime, string>(DateTime.UtcNow, text));
            if (Feed.Count > 30) Feed.RemoveAt(0);
        }
        static string N(IEntity e) => StructureTag.Strip(e?.Name ?? "?");

        /// <summary>Our ships currently in this playfield (for displays).</summary>
        public List<IEntity> ShipsForDisplay() => Ships().ToList();

        public double Now => clock.Elapsed.TotalSeconds;

        // ---- hostility: a player who destroys a block on one of our ships ----
        /// <summary>Per ship: leader keys of players who attacked it (registry <c>memory.hostile.&lt;key&gt;</c>).</summary>
        readonly Dictionary<int, HashSet<string>> hostileTo = new Dictionary<int, HashSet<string>>();
        /// <summary>Ships currently fleeing: away from this point until this time, then they hold.</summary>
        readonly Dictionary<int, KeyValuePair<Vector3, double>> fleeing = new Dictionary<int, KeyValuePair<Vector3, double>>();
        const double FleeSeconds = double.MaxValue;   // no timer: it flees until it has escaped (EscapeRange)
        const float FleeDistance = 2000f;   // direction target; it jumps out long before getting there
        /// <summary>
        /// A fleeing ship jumps out once every player is at least this far away — never while being chased. Inside the
        /// physics bubble (under 2 km: a ship that ran 2 km froze and became a proxy), so it is still loaded and can be
        /// removed cleanly.
        /// </summary>
        const float EscapeRange = 1500f;
        /// <summary>
        /// "Escaped" must hold this long without a break: the reported player position glitches (single readings 1.2–1.6 km
        /// off while chasing at ~100 m) — one bad sample must not end a chase.
        /// </summary>
        const double EscapeHoldSeconds = 3;
        readonly Dictionary<int, double> escapingSince = new Dictionary<int, double>();

        /// <summary>
        /// A fleeing ship also spools its warp drive: ready after a random <see cref="WarpMin"/>…<see cref="WarpMax"/> s,
        /// announced <see cref="WarpWarn"/> s before; every block a player destroys meanwhile disrupts it
        /// (+<see cref="WarpDisrupt"/> s). Keep hitting it, or it gets away.
        /// </summary>
        sealed class Warp { public double ReadyAt; public bool Warned; public double NextTaunt; }
        readonly Dictionary<int, Warp> warp = new Dictionary<int, Warp>();
        const double WarpMin = 45, WarpMax = 90, WarpWarn = 10, WarpDisrupt = 20, TauntEvery = 25;
        const float TauntRange = 600f;
        readonly System.Random rng = new System.Random();
        static readonly string[] Taunts =
        {
            "Hey, stop chasing us!", "Back off!", "We're not worth it — leave us alone!",
            "Keep your distance or we'll report you!", "We've got nothing you want!",
        };

        /// <summary>Message every player within <paramref name="range"/> of a ship.</summary>
        void SayNear(IEntity e, float range, string text)
        {
            if (playfield?.Players == null) return;
            foreach (var kv in playfield.Players)
            {
                if (kv.Value == null) continue;
                var pos = kv.Value.DrivingEntity != null ? kv.Value.DrivingEntity.Position : kv.Value.Position;
                if ((pos - e.Position).magnitude <= range) reply(kv.Key, $"{StructureTag.Strip(e.Name)}: {text}");
            }
        }
        static readonly System.Text.RegularExpressions.Regex StructRef =
            new System.Text.RegularExpressions.Regex(@"id=(\d+), cIdx=\d+", System.Text.RegularExpressions.RegexOptions.Compiled);
        static readonly System.Text.RegularExpressions.Regex PlayerRef =
            new System.Text.RegularExpressions.Regex(@"^'(.*)', (\d+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

        /// <summary>
        /// Official v2 game events (PfServer). BlockDestroyed: a1 = the player who did it ("'Name', 1188"), a2 = the victim,
        /// whose text carries only its structure index ("… CIdx=M"). The victim is found from the events' own text: the
        /// cIdx → id pairs that AttackedBlock / ShieldAttacked print ("'name', id=N, cIdx=M"), else which of our ships near
        /// the attacker just lost blocks (IStructure.BlockCount). Acted on only if it is one of our registered ships.
        /// </summary>
        public void OnGameEvent(GameEventType type, object a1, object a2)
        {
            if (playfield == null || a1 == null) return;
            var s1 = Convert.ToString(a1, CultureInfo.InvariantCulture) ?? "";
            // AttackedBlock / ShieldAttacked: a1 = the VICTIM, "'name', id=N, cIdx=M" — learn cIdx → entity id
            var sm = StructRef.Match(s1);
            if (sm.Success && type != GameEventType.BlockDestroyed)
            {
                var vid0 = int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture);
                var ci = CIdxOf(sm.Value);
                if (ci >= 0) cidxToId[ci] = vid0;
                // AttackedEntity: a2 = the attacker ("'Name', 1188") — shield hits too (shooting only shields destroys no
                // block, so BlockDestroyed alone would miss the attacker)
                if (combatants.Contains(vid0) && a2 != null)
                {
                    var am = PlayerRef.Match(Convert.ToString(a2, CultureInfo.InvariantCulture) ?? "");
                    if (am.Success && int.TryParse(am.Groups[2].Value, out var apid) && apid > 0) CombatantHitBy?.Invoke(vid0, apid);
                }
                return;
            }
            if (type != GameEventType.BlockDestroyed) return;
            // BlockDestroyed: a1 = the ATTACKER — a player on foot "'Name', 1188", or the ship they fly
            // "'ShipName', id=40155, cIdx=6"; a2 = the victim, "…CIdx=8" or "id=N, cIdx=M"
            int pid;
            var pm = PlayerRef.Match(s1);
            if (pm.Success) int.TryParse(pm.Groups[2].Value, out pid);
            else if (sm.Success) pid = PilotOf(int.Parse(sm.Groups[1].Value, CultureInfo.InvariantCulture));
            else return;
            if (pid <= 0 || playfield.Players == null || !playfield.Players.ContainsKey(pid)) return;
            var victim = FindStructureId(a2);
            if (victim <= 0)
            {
                var vc = VictimCIdx.Match(Convert.ToString(a2, CultureInfo.InvariantCulture) ?? "");
                if (vc.Success && cidxToId.TryGetValue(int.Parse(vc.Groups[1].Value, CultureInfo.InvariantCulture), out var vid)) victim = vid;
            }
            if (victim <= 0) { victim = DamagedNear(pid); if (victim > 0) log($"FLY hit by player {pid}: victim {victim} found by its block loss"); }
            if (victim <= 0 || !known.Contains(victim)) return;
            Attacked(victim, pid);
        }

        /// <summary>Structure index (cIdx) → entity id, learned from AttackedBlock/ShieldAttacked (BlockDestroyed names the victim only by cIdx).</summary>
        readonly Dictionary<int, int> cidxToId = new Dictionary<int, int>();
        static readonly System.Text.RegularExpressions.Regex VictimCIdx =
            new System.Text.RegularExpressions.Regex(@"CIdx=(\d+)", System.Text.RegularExpressions.RegexOptions.Compiled);
        static int CIdxOf(string structRef)
        {
            var i = structRef.LastIndexOf("cIdx=", StringComparison.Ordinal);
            return i >= 0 && int.TryParse(structRef.Substring(i + 5), out var c) ? c : -1;
        }

        /// <summary>The player piloting (or riding) a structure, or -1.</summary>
        int PilotOf(int structureId)
        {
            if (playfield?.Players == null) return -1;
            foreach (var kv in playfield.Players)
                if (kv.Value?.DrivingEntity != null && kv.Value.DrivingEntity.Id == structureId) return kv.Key;
            return -1;
        }

        /// <summary>The structure id in an event argument's own text ("…, id=N, cIdx=M"), or -1. Official text only — the
        /// argument types are the game's internals, never looked into.</summary>
        static int FindStructureId(object o)
        {
            if (o == null) return -1;
            var m = StructRef.Match(Convert.ToString(o, CultureInfo.InvariantCulture) ?? "");
            return m.Success ? int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) : -1;
        }

        /// <summary>
        /// Last resort for a BlockDestroyed whose victim we can't name (its cIdx not learned yet): of our ships within 2 km
        /// of the attacker, the nearest that has lost blocks since we first saw it (official BlockCount). -1 if none.
        /// </summary>
        int DamagedNear(int pid)
        {
            if (playfield?.Players == null || !playfield.Players.TryGetValue(pid, out var pl) || pl == null) return -1;
            var at = pl.DrivingEntity != null ? pl.DrivingEntity.Position : pl.Position;
            var best = -1; var bestD = 2000f;
            foreach (var id in known)
            {
                if (!playfield.Entities.TryGetValue(id, out var e) || e == null || !startBlocks.TryGetValue(id, out var start) || start <= 0) continue;
                var d = Vector3.Distance(e.Position, at);
                if (d >= bestD || !int.TryParse(S(() => e.Structure.BlockCount), out var now) || now <= 0 || now >= start) continue;
                best = id; bestD = d;
            }
            return best;
        }

        /// <summary>A hulk, not a crewed ship: one of our wrecks, or anything without a core. Never reacts, flees or talks.</summary>
        static bool IsWreck(IEntity e)
        {
            bool coreless;
            try { coreless = e.Structure != null && e.Structure.CoreType == CoreType.None; } catch { coreless = false; }
            return Core.Sim.LiveRules.IsHulk(StructureTag.Strip(e.Name ?? ""), coreless);
        }

        /// <summary>A player destroyed a block on our ship: drop its order, leave their faction, flee, remember.</summary>
        void Attacked(int shipId, int pid)
        {
            if (!playfield.Entities.TryGetValue(shipId, out var e) || e?.Structure == null) return;
            // wrecks carry our tag too: salvaging one is not an attack
            if (IsWreck(e)) return;
            if (combatants.Contains(shipId)) { CombatantHitBy?.Invoke(shipId, pid); CombatantHit(e); return; }   // pirates: being shot is the point, no "you betrayed us" reaction
            playfield.Players.TryGetValue(pid, out var pl);
            var key = pl != null ? LeaderKey(pl, pid) : "pid:" + pid.ToString(CultureInfo.InvariantCulture);
            var first = !hostileTo.TryGetValue(shipId, out var set) || !set.Contains(key);
            if (set == null) hostileTo[shipId] = set = new HashSet<string>();
            set.Add(key);
            var now = clock.Elapsed.TotalSeconds;
            var wasFleeing = fleeing.ContainsKey(shipId);
            fleeing[shipId] = new KeyValuePair<Vector3, double>(pl != null ? pl.Position : e.Position, now + FleeSeconds);
            if (!warp.TryGetValue(shipId, out var w))
                warp[shipId] = new Warp { ReadyAt = now + WarpMin + rng.NextDouble() * (WarpMax - WarpMin), NextTaunt = now + TauntEvery };
            else if (wasFleeing)
            {
                w.ReadyAt = Math.Max(w.ReadyAt, now) + WarpDisrupt; w.Warned = false;
                SayNear(e, 2000f, "Warp drive disrupted!");
                log($"FLY hostility: {shipId} warp disrupted, ready in {w.ReadyAt - now:0} s");
                Event($"{N(e)}: warp drive disrupted");
            }
            if (wasFleeing && !first) return;   // already fleeing from them: just extend

            if (orders.Remove(shipId)) Persist(shipId, null);
            stored.Remove(shipId);
            if (factionOrig.ContainsKey(shipId)) GiveOrder(pid, shipId, "leave", 0);
            ToDedi("regpatch", ShipRecord.BuildLine(new Dictionary<string, string>
                { ["id"] = shipId.ToString(CultureInfo.InvariantCulture), ["memory.hostile." + key] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture) }));
            log($"FLY hostility: {pid} ({key}) destroyed a block on {shipId} '{e.Name}' — order dropped, fleeing");
            Event($"{N(e)} attacked by {(pl != null ? S(() => pl.Name) : "someone")} — fleeing");
            reply(pid, $"{StructureTag.Strip(e.Name)}: You're shooting at us! We're out.");
        }

        /// <summary>
        /// The ship leaves the system: destroy the structure while it is still loaded (Request_Entity_Destroy via the
        /// Dedi — sticks; a ship that runs out of the physics bubble freezes and turns into a proxy instead). Its
        /// registry record stays as an agent (state=departed, memory intact) — it may turn up again one day.
        /// </summary>
        void JumpOut(IEntity e, double now)
        {
            fleeing.Remove(e.Id); orders.Remove(e.Id); stored.Remove(e.Id); warp.Remove(e.Id);
            Persist(e.Id, null);   // no order (clears every order.* field)
            ToDedi("regpatch", ShipRecord.BuildLine(new Dictionary<string, string>
            {
                ["id"] = e.Id.ToString(CultureInfo.InvariantCulture), ["state"] = "departed",
                ["departed"] = DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture),
            }));
            ToDedi("destroy", 0, e.Id);
            known.Remove(e.Id);
            log($"FLY hostility: {e.Id} '{e.Name}' jumped out of the system (agent kept); nearest player {NearestPlayer(e.Position):0} m");
            Gone(e.Id, "jumped");
            Event($"{N(e)} jumped out of the system");
            foreach (var pl in playfield.Players?.Keys.ToList() ?? new List<int>())
                if (playfield.Players[pl] != null && (playfield.Players[pl].Position - e.Position).magnitude < 5000f)
                    reply(pl, $"{StructureTag.Strip(e.Name)} jumped out of the system.");
        }

        float NearestPlayer(Vector3 at) => NearestPlayer(at, out _);

        /// <summary>Distance to the closest player (their ship if they pilot one), and where they are.</summary>
        float NearestPlayer(Vector3 at, out Vector3 where)
        {
            var best = float.MaxValue; where = at;
            if (playfield?.Players == null) return best;
            foreach (var p in playfield.Players.Values)
            {
                if (p == null) continue;
                var pos = p.DrivingEntity != null ? p.DrivingEntity.Position : p.Position;
                var d = (pos - at).magnitude;
                if (d < best) { best = d; where = pos; }
            }
            return best;
        }

        public bool Distrusts(int shipId, int pid)
        {
            if (!hostileTo.TryGetValue(shipId, out var set) || playfield?.Players == null || !playfield.Players.TryGetValue(pid, out var pl)) return false;
            return set.Contains(LeaderKey(pl, pid)) || set.Contains("pid:" + pid.ToString(CultureInfo.InvariantCulture));
        }
        double loadedAt, nextReconcile, nextRegGet;
        /// <summary>
        /// Registry reply received? A freshly started PfServer can miss the Dedi's first reply, so we re-ask every few seconds until regdone arrives.
        /// </summary>
        bool regReceived;
        int regAsks;

        void ToDedi(params object[] fields) =>
            api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack(fields), playfield?.Name);

        ShipRecord RecordOf(int shipId, Order o)
        {
            var name = playfield?.Entities != null && playfield.Entities.TryGetValue(shipId, out var e) && e != null ? e.Name : "";
            var r = new ShipRecord { EntityId = shipId, Playfield = playfield?.Name ?? "", Name = name ?? "" };
            if (o != null)
                r.Order = new ShipOrder
                {
                    Kind = o.Kind, LeaderSteamId = o.LeaderSteamId, LeaderName = o.LeaderName, Distance = o.Distance, Cruise = o.Cruise,
                    HoldX = o.HoldAt.x, HoldY = o.HoldAt.y, HoldZ = o.HoldAt.z,
                };
            return r;
        }

        /// <summary>Order given or released (null): full update of the ship's record.</summary>
        void Persist(int shipId, Order o) => ToDedi("regset", ShipRecord.ToLine(RecordOf(shipId, o)));

        /// <summary>
        /// «LS» name but not registered. A player's ship (renamed) is never touched. NPC-owned with a live core = a copy of
        /// ours whose record went but whose hull survived (a destroy sent while it was only a proxy does nothing): retired
        /// once nobody's within the bubble for <see cref="OrphanSeconds"/>. Coreless hulks are left alone.
        /// </summary>
        void NoteImpostor(IEntity e, double now)
        {
            if (known.Contains(e.Id) || !StructureTag.IsTagged(e.Name) || KioskNames.IsKiosk(e.Name)) return;
            // the sim's own hulls (live agents, beacon / hub wrecks) aren't in the flight registry; after a restart the
            // re-adopt can arrive after this check — never retire them here (a hub wreck kept its core: it would go)
            if ((SimOwns != null && SimOwns(e.Id)) || StructureTag.Strip(e.Name).StartsWith("Wreck of ", StringComparison.Ordinal)) { orphanSince.Remove(e.Id); return; }
            if (impostors.Add(e.Id)) log($"FLY registry: '{e.Name}' ({e.Id}) has our tag but is not registered{(NpcOwned(e) ? " — NPC-owned: an orphaned copy, retired once nobody's near" : " — not ours, ignoring")}");
            if (!NpcOwned(e) || S(() => e.Structure.CoreType) == "None") { orphanSince.Remove(e.Id); return; }
            if (NearestPlayer(e.Position) <= 2500f) { orphanSince.Remove(e.Id); return; }
            if (!orphanSince.TryGetValue(e.Id, out var since)) { orphanSince[e.Id] = now; return; }
            if (now - since < OrphanSeconds) return;
            orphanSince.Remove(e.Id);
            log($"FLY orphan '{e.Name}' ({e.Id}) retired (tagged, unregistered, NPC-owned, nobody near)");
            Retire(e.Id);
        }
        readonly Dictionary<int, double> orphanSince = new Dictionary<int, double>();
        const double OrphanSeconds = 60;

        static bool NpcOwned(IEntity x)
        {
            try { var g = (int)x.Faction.Group; return g >= 2 && x.Faction.Group != FactionGroup.Admin; } catch { return false; }
        }

        void Unpersist(int shipId) => ToDedi("regdel", shipId);

        static Order FromStored(ShipOrder so) => new Order
        {
            Kind = so.Kind, LeaderSteamId = so.LeaderSteamId, LeaderName = so.LeaderName,
            Distance = so.Distance, Cruise = so.Cruise, HoldAt = new Vector3(so.HoldX, so.HoldY, so.HoldZ),
        };

        /// <summary>
        /// Players in this playfield by leader key. IPlayer.SteamId comes back EMPTY on the PfServer,
        /// so every player is also keyed "pid:&lt;entity id&gt;" — player entity ids are stable within a save
        /// (1188 across sessions). Steam id is used when the API does give one.
        /// </summary>
        Dictionary<string, int> PlayersBySteamId()
        {
            var m = new Dictionary<string, int>();
            if (playfield?.Players == null) return m;
            foreach (var kv in playfield.Players)
            {
                m["pid:" + kv.Key.ToString(CultureInfo.InvariantCulture)] = kv.Key;
                var sid = S(() => kv.Value?.SteamId);
                if (!string.IsNullOrEmpty(sid) && !sid.StartsWith("!")) m[sid] = kv.Key;
            }
            return m;
        }

        static string LeaderKey(IPlayer p, int pid)
        {
            var sid = S(() => p.SteamId);
            return !string.IsNullOrEmpty(sid) && !sid.StartsWith("!") ? sid : "pid:" + pid.ToString(CultureInfo.InvariantCulture);
        }

        /// <summary>Once a second: place stored orders on ships that have appeared; drop the ones whose ship never did.</summary>
        void ReconcileStored(double now)
        {
            if (stored.Count == 0 || now < nextReconcile) return;
            nextReconcile = now + 1;
            var present = new HashSet<int>(Ships().Select(e => e.Id));
            foreach (var d in Reconciler.Decide(stored.Values.ToList(), present, PlayersBySteamId(), now - loadedAt))
            {
                var id = d.Record.EntityId;
                switch (d.Action)
                {
                    case ReconcileAction.Resume:
                    case ReconcileAction.WaitForLeader:
                        orders[id] = FromStored(d.Record.Order);
                        Event($"{StructureTag.Strip(d.Record.Name)} resumed: {d.Record.Order.Kind}");
                        stored.Remove(id);
                        log($"FLY registry: {id} resumes {d.Record.Order.Kind}{(d.Action == ReconcileAction.WaitForLeader ? $" (waiting for {d.Record.Order.LeaderName})" : "")}");
                        break;
                    case ReconcileAction.Idle:
                    case ReconcileAction.Departed:
                        stored.Remove(id);
                        break;
                    case ReconcileAction.Drop:
                        stored.Remove(id); known.Remove(id); Unpersist(id);
                        log($"FLY registry: {id} '{d.Record.Name}' never appeared, forgotten");
                        break;
                }
            }
        }

        /// <summary>Follow orders: find the leader by Steam id each second; away → hold where we are, back → follow.</summary>
        void ResolveLeaders(double now)
        {
            if (now < nextLeaderResolve) return;
            nextLeaderResolve = now + 1;
            var bySteam = PlayersBySteamId();
            foreach (var kv in orders)
            {
                var o = kv.Value;
                if (o.Kind != "follow") continue;
                o.LeaderPid = bySteam.TryGetValue(o.LeaderSteamId ?? "", out var pid) ? pid : -1;
                if (o.LeaderPid < 0 && !o.Waiting && playfield.Entities.TryGetValue(kv.Key, out var e) && e != null) { o.Waiting = true; o.WaitAt = e.Position; }
                else if (o.LeaderPid >= 0) o.Waiting = false;
            }
        }
        double nextLeaderResolve;

        string mode = "off";             // "cal" while a calibration runs, else "off"
        string idle = "auto";

        // calibration run
        int calShip = -1; Vector3 calDir; float calK, calSec; string calAfter = "brake"; double calStart;

        /// <summary>Mass, re-read every few seconds (it changes with cargo and damage, not per frame).</summary>
        float MassOf(IEntity e, Track t, double now)
        {
            if (t.MassAt < 0 || now - t.MassAt > 5) { var m = Mass(e); if (m > 0) t.MassKg = m; t.MassAt = now; }
            return t.MassKg > 0 ? t.MassKg : 757_000f;
        }

        public FlightDriver(IModApi api, Action<string> log) { this.api = api; this.log = log; }
        public IModApi Api => api;

        void reply(int pid, string text) =>
            api.Network.SendToDedicatedServer(LivingSystemsMod.Channel, Wire.Pack("say", pid, text), playfield?.Name);

        public void OnLoaded(IPlayfield p)
        {
            Escort?.Reset();
            playfield = p; tracks.Clear(); firstSeen.Clear(); combatants.Clear(); startBlocks.Clear(); moraleLoss.Clear(); startTurrets.Clear(); combatState.Clear(); cidxToId.Clear(); wasPirate.Clear(); seenAlive.Clear(); dead.Clear(); orders.Clear(); stored.Clear(); known.Clear(); impostors.Clear(); factionOrig.Clear(); hostileTo.Clear(); fleeing.Clear(); escapingSince.Clear(); warp.Clear();
            loadedAt = clock.Elapsed.TotalSeconds;
            regReceived = false; regAsks = 0; nextRegGet = 0;   // asked from OnUpdate until regdone arrives
        }

        public void OnUnloading(IPlayfield p) { if (playfield == p) { playfield = null; tracks.Clear(); orders.Clear(); stored.Clear(); mode = "off"; calShip = -1; } }

        /// <summary>Logic stop / reload: brake and forget in memory — the registry keeps the orders.</summary>
        public void Stop()
        {
            foreach (var e in Ships()) Safe(() => e.Move(Vector3.zero));
            orders.Clear(); stored.Clear(); mode = "off"; calShip = -1;
        }

        /// <summary>/ls fly off: release every ship here, in memory and in the registry.</summary>
        void ReleaseAll()
        {
            foreach (var id in orders.Keys.ToList()) Persist(id, null);   // ships stay registered, without orders
            Stop();
        }

        /// <summary>
        /// What one of our ships is doing, worded for the player asking ("following you at 500 m"); "" when it is
        /// not ours, "awaiting orders" when it has none.
        /// </summary>
        public string StatusOf(int shipId, int askingPid)
        {
            if (playfield?.Entities == null || !playfield.Entities.TryGetValue(shipId, out var e) || e == null || !known.Contains(shipId)) return "";
            string what;
            var hasOrder = orders.TryGetValue(shipId, out var o);
            if (fleeing.ContainsKey(shipId)) what = "fleeing";
            else if (!hasOrder) what = stored.ContainsKey(shipId) ? "resuming orders" : "awaiting orders";
            else if (o.Kind == "follow" && o.LeaderPid < 0) what = $"waiting for {(string.IsNullOrEmpty(o.LeaderName) ? "its leader" : o.LeaderName)} (not here)";
            else if (o.Kind == "follow")
            {
                var who = o.LeaderPid == askingPid ? "you" : (playfield.Players != null && playfield.Players.TryGetValue(o.LeaderPid, out var p) && p != null ? p.Name : "someone");
                what = $"following {who} at {o.Distance:0} m";
            }
            else if (o.Kind == "hold") what = "holding position";
            else if (o.Kind == "goto") what = $"en route to {(string.IsNullOrEmpty(o.LeaderName) ? "its destination" : o.LeaderName)}";
            else if (o.Kind == "intercept") what = "closing in";
            else what = o.Kind;
            if (Distrusts(shipId, askingPid)) what += " — doesn't trust you";
            return factionOrig.ContainsKey(shipId) ? what + ", flying under your colours" : what;
        }

        /// <summary>Give one ship an order; returns the ship's reply line, or null if it isn't ours / isn't here.</summary>
        string GiveOrder(int pid, int shipId, string kind, float distance)
        {
            if (playfield?.Entities == null || !playfield.Entities.TryGetValue(shipId, out var e) || e?.Structure == null || !known.Contains(shipId))
                return null;
            if (Distrusts(shipId, pid) && kind != "leave") return "Not after you shot at us.";
            switch (kind)
            {
                case "follow":
                {
                    IPlayer leader = null;
                    if (playfield.Players == null || !playfield.Players.TryGetValue(pid, out leader) || leader == null) return "Can't see you.";
                    var o = new Order { Kind = "follow", LeaderPid = pid, LeaderSteamId = LeaderKey(leader, pid), LeaderName = S(() => leader.Name), Distance = distance > 0 ? distance : 150f };
                    orders[shipId] = o; stored.Remove(shipId); Persist(shipId, o);
                    log($"FLY order {shipId} '{e.Name}': follow {pid} ({o.LeaderSteamId}) at {o.Distance:0} m");
                    Event($"{N(e)} follows {o.LeaderName}");
                    return $"Understood. Following you at {o.Distance:0} m.";
                }
                case "hold":
                {
                    var o = new Order { Kind = "hold", HoldAt = e.Position };
                    orders[shipId] = o; stored.Remove(shipId); Persist(shipId, o);
                    log($"FLY order {shipId} '{e.Name}': hold at {e.Position}");
                    Event($"{N(e)} holds position");
                    return "Holding position.";
                }
                case "join":    // placeholder for a contract: fly under the player's faction (vanilla turrets then fight their enemies)
                {
                    if (factionOrig.ContainsKey(shipId)) return "Already flying with you.";
                    if (playfield.Players == null || !playfield.Players.TryGetValue(pid, out var pl) || pl == null) return "Can't see you.";
                    var theirs = pl.FactionData;
                    var orig = $"{(int)e.Faction.Group}:{e.Faction.Id}";
                    // record the original BEFORE switching, so a crash in between can't lose it
                    ToDedi("regpatch", ShipRecord.BuildLine(new Dictionary<string, string>
                        { ["id"] = shipId.ToString(CultureInfo.InvariantCulture), ["faction.orig"] = orig, ["faction.temp"] = $"{(int)theirs.Group}:{theirs.Id}" }));
                    e.Structure.SetFaction(theirs.Group, theirs.Id);
                    factionOrig[shipId] = orig;
                    log($"FLY faction {shipId} '{e.Name}': {orig} -> {(int)theirs.Group}:{theirs.Id} (joined {pid})");
                    Event($"{N(e)} joined {S(() => pl.Name)}'s faction");
                    return "We're flying under your colours now.";
                }
                case "leave":
                {
                    if (!factionOrig.TryGetValue(shipId, out var orig)) return "We fly our own colours already.";
                    var p2 = orig.Split(':');
                    if (p2.Length != 2 || !byte.TryParse(p2[0], out var g) || !int.TryParse(p2[1], out var fid)) return "Lost track of our own colours.";
                    e.Structure.SetFaction((FactionGroup)g, fid);
                    factionOrig.Remove(shipId);
                    ToDedi("regpatch", ShipRecord.BuildLine(new Dictionary<string, string>
                        { ["id"] = shipId.ToString(CultureInfo.InvariantCulture), ["faction.orig"] = "", ["faction.temp"] = "" }));
                    log($"FLY faction {shipId} '{e.Name}': back to {orig}");
                    Event($"{N(e)} back under its own colours");
                    return "Back under our own colours.";
                }
                case "release":
                    orders.Remove(shipId); stored.Remove(shipId); Persist(shipId, null); Safe(() => e.Move(Vector3.zero));
                    log($"FLY order {shipId} '{e.Name}': released");
                    return "Released. Standing by.";
            }
            return null;
        }

        // ---- for the escort run (EscortRun drives these; the driver just flies) ----
        public EscortRun Escort;
        /// <summary>Hostile actors we fly (pirates): no hostility reaction when shot.</summary>
        readonly HashSet<int> combatants = new HashSet<int>();
        /// <summary>Block count when a combatant engaged; it breaks off after losing its share of them (<see cref="moraleLoss"/>).</summary>
        readonly Dictionary<int, int> startBlocks = new Dictionary<int, int>();
        /// <summary>
        /// Each pirate breaks off after losing its own number of blocks (8–40): some bail early, some fight on. Absolute,
        /// not a share — with shields down a few blocks can expose the core; shields soak hits first,
        /// so the count effectively starts when they're down.
        /// </summary>
        readonly Dictionary<int, int> moraleLoss = new Dictionary<int, int>();
        /// <summary>Turrets when it engaged. Losing half of them = it breaks off (the strongest signal).
        /// Counts every turret alike — a big cannon and a small interior sentry weigh the same (not weighted yet).</summary>
        readonly Dictionary<int, int> startTurrets = new Dictionary<int, int>();
        /// <summary>Turrets as entities belonging to the ship (EntityType.Turret + BelongsTo, like traders) — unverified:
        /// -1 if none are found, then the blocks rule decides. Logged with the ship's custom device names to learn.</summary>
        int Turrets(IEntity ship)
        {
            var n = 0;
            foreach (var x in playfield.Entities.Values)
                if (x != null && x.Type == EntityType.Turret && int.TryParse(S(() => x.BelongsTo), out var b) && b == ship.Id) n++;
            return n > 0 ? n : -1;
        }
        static readonly string[] BreakOff = { "We're out! Break off!", "Not worth it — we're gone!", "Too hot! Pull back!" };

        /// <summary>A pirate took a hit from a player: fleeing → its warp drive is disrupted; badly hurt → it breaks off.</summary>
        void CombatantHit(IEntity e)
        {
            if (fleeing.ContainsKey(e.Id) && warp.TryGetValue(e.Id, out var w))
                w.ReadyAt = Math.Max(w.ReadyAt, clock.Elapsed.TotalSeconds) + WarpDisrupt;
        }

        /// <summary>Last logged state per combatant (to log changes only — learning how a shot-out core looks).</summary>
        readonly Dictionary<int, string> combatState = new Dictionary<int, string>();
        /// <summary>Combatants seen in the pirate faction (it is set ~1 s after spawn); leaving it after that = dead.</summary>
        readonly HashSet<int> wasPirate = new HashSet<int>();
        static readonly int PirateGroup = SpikeCommand.FactionGroup("pirates");

        /// <summary>
        /// Once a second: pirates' morale and death, polled (no event names a ship-fired hit reliably). Dead = gone,
        /// no structure, core gone (CoreType "None"), or no longer in its faction. Badly hurt → breaks off.
        /// </summary>
        void CheckCombatants()
        {
            foreach (var id in combatants.ToList())
            {
                if (!playfield.Entities.TryGetValue(id, out var e) || e?.Structure == null) { CombatantDead(id, null, "gone"); continue; }
                var core = S(() => e.Structure.CoreType);
                var group = (int)e.Faction.Group;
                var blocks = int.TryParse(S(() => e.Structure.BlockCount), out var b) ? b : -1;
                var turrets = Turrets(e);
                if (!startTurrets.ContainsKey(id) && turrets >= 0) startTurrets[id] = turrets;
                if (!combatState.ContainsKey(id))
                    log($"FLY combatant {id} devices: {S(() => string.Join(",", e.Structure.GetAllCustomDeviceNames() ?? new string[0]))}; turret entities in playfield: {playfield.Entities.Values.Count(x => x?.Type == EntityType.Turret)}");
                var state = $"turrets={turrets}/{(startTurrets.TryGetValue(id, out var st0) ? st0 : -1)} core={core} faction={group}:{e.Faction.Id} blocks={blocks} powered={S(() => e.Structure.IsPowered)} shield={S(() => e.Structure.ShieldLevel)} shieldOn={S(() => e.Structure.IsShieldActive)}";
                if (!combatState.TryGetValue(id, out var last) || last != state) { combatState[id] = state; log($"FLY combatant {id}: {state}"); }
                if (group == PirateGroup) wasPirate.Add(id);
                if (core.IndexOf("None", StringComparison.OrdinalIgnoreCase) >= 0 || (wasPirate.Contains(id) && group != PirateGroup))
                { CombatantDead(id, e, $"core={core} faction={group}"); continue; }
                if (fleeing.ContainsKey(id)) continue;
                if (turrets >= 0 && startTurrets.TryGetValue(id, out var t0) && t0 >= 2 && turrets * 2 <= t0)
                {
                    SayNear(e, 3000f, BreakOff[rng.Next(BreakOff.Length)]);
                    log($"FLY {id} '{e.Name}': morale broke, turrets {turrets}/{t0}");
                    SendAway(id);
                    continue;
                }
                if (blocks < 0 || !startBlocks.TryGetValue(id, out var start) || start <= 0) continue;
                if (!moraleLoss.TryGetValue(id, out var ml)) moraleLoss[id] = ml = 8 + rng.Next(33);
                if (start - blocks >= ml)
                {
                    SayNear(e, 3000f, BreakOff[rng.Next(BreakOff.Length)]);
                    log($"FLY {id} '{e.Name}': morale broke at {blocks}/{start} blocks");
                    SendAway(id);
                }
            }
        }

        /// <summary>
        /// One of ours now flies a player faction without having joined through us (no faction.orig): a player claimed it
        /// (e.g. a salvage core on a dead pirate). It's theirs: forget it for good — never fly or clear a player's ship.
        /// </summary>
        void CheckClaimed(IEntity e)
        {
            if (factionOrig.ContainsKey(e.Id) || !Factions.IsPlayer((int)e.Faction.Group)) return;
            var id = e.Id;
            if (orders.ContainsKey(id) || fleeing.ContainsKey(id)) Safe(() => e.Move(Vector3.zero));
            combatants.Remove(id); known.Remove(id); orders.Remove(id); stored.Remove(id); fleeing.Remove(id); warp.Remove(id); startBlocks.Remove(id); tracks.Remove(id);
            Unpersist(id);
            log($"FLY {id} '{e.Name}' claimed by a player (faction {(int)e.Faction.Group}:{e.Faction.Id}) — forgotten");
            Gone(id, "claimed");
            Event($"{N(e)} claimed by a player");
        }

        /// <summary>Ships seen alive (core reported and not "None") — a fresh spawn may not report a core yet.</summary>
        readonly HashSet<int> seenAlive = new HashSet<int>();
        /// <summary>Our ships whose core was destroyed this session (the hulk stays in the playfield).</summary>
        readonly HashSet<int> dead = new HashSet<int>();
        public bool IsDead(int shipId) => dead.Contains(shipId);

        /// <summary>
        /// Core destroyed (CoreType "None", faction 255): stop flying the hulk, mark it dead.
        /// Pirates go through <see cref="CheckCombatants"/>; this covers every other ship of ours (traders).
        /// </summary>
        void CheckDead(IEntity e)
        {
            if (combatants.Contains(e.Id)) return;
            var core = S(() => e.Structure.CoreType);
            if (core != "None") { if (!core.StartsWith("!")) seenAlive.Add(e.Id); return; }
            if (!seenAlive.Contains(e.Id) || !dead.Add(e.Id)) return;
            Safe(() => e.Move(Vector3.zero));
            orders.Remove(e.Id); stored.Remove(e.Id); fleeing.Remove(e.Id); warp.Remove(e.Id); factionOrig.Remove(e.Id);
            known.Remove(e.Id);
            Persist(e.Id, null);
            Remember(e.Id, "state", "dead");
            log($"FLY {e.Id} '{e.Name}': core destroyed — dead (faction {(int)e.Faction.Group})");
            Gone(e.Id, "dead");
            Event($"{N(e)} destroyed");
        }

        /// <summary>Destroy a structure outright via the Dedi (blown to pieces: no hulk, no loot).</summary>
        public void Obliterate(int id) => ToDedi("destroy", 0, id);

        void CombatantDead(int id, IEntity e, string why)
        {
            combatants.Remove(id); known.Remove(id); orders.Remove(id); fleeing.Remove(id); warp.Remove(id); startBlocks.Remove(id);
            log($"FLY combatant {id} dead ({why})");
            // "gone" = no structure: removed, or turned into a proxy out of range — NOT a kill
            Gone(id, why == "gone" ? "lost" : "dead");
            if (e != null) Event($"{N(e)} destroyed");
        }

        /// <summary>A live combatant (not breaking off) within <paramref name="range"/> of a point.</summary>
        public bool CombatantNear(Vector3 at, float range, out Vector3 nearest)
        {
            nearest = at; var best = range;
            var any = false;
            foreach (var x in Ships())
            {
                if (!combatants.Contains(x.Id) || fleeing.ContainsKey(x.Id)) continue;
                var d = (x.Position - at).magnitude;
                if (d <= best) { best = d; nearest = x.Position; any = true; }
            }
            return any;
        }

        public IPlayfield Playfield => playfield;
        public void Send(params object[] fields) => ToDedi(fields);
        public void Say(int pid, string text) => reply(pid, text);
        public bool IsFleeing(int shipId) => fleeing.ContainsKey(shipId);
        public bool HasOrder(int shipId, string kind) => orders.TryGetValue(shipId, out var o) && o.Kind == kind;
        /// <summary>Order through the same path as the hail menu (join / leave / hold / release); the ship's reply line.</summary>
        public string Command(int pid, int shipId, string kind) => GiveOrder(pid, shipId, kind, 0);
        public void Remember(int shipId, string key, string value)
        {
            ToDedi("regpatch", ShipRecord.BuildLine(new Dictionary<string, string> { ["id"] = shipId.ToString(CultureInfo.InvariantCulture), [key] = value }));
            Remembered?.Invoke(shipId, key, value);
        }
        /// <summary>Every Remember (SimLive keeps sim traders' memories on the agent: the entity is new each time it spawns).</summary>
        public event Action<int, string, string> Remembered;
        /// <summary>A player (pid) destroyed a block on a combatant (a pirate) — SimLive turns it into the pirate's memory.</summary>
        public event Action<int, int> CombatantHitBy;
        /// <summary>How memories name a player: Steam id when the PfServer knows it, else "pid:N" (stable within a save).</summary>
        public string PlayerKey(int pid) => playfield?.Players != null && playfield.Players.TryGetValue(pid, out var p) && p != null ? LeaderKey(p, pid) : "pid:" + pid.ToString(CultureInfo.InvariantCulture);

        /// <summary>
        /// A sim ship leaves the live game (out of every player's bubble / reached its point): forget it, drop its registry
        /// record (the sim agent is its identity) and destroy the structure. No event, no ShipGone — the caller reports it.
        /// </summary>
        public void Retire(int shipId)
        {
            orders.Remove(shipId); stored.Remove(shipId); fleeing.Remove(shipId); warp.Remove(shipId); combatants.Remove(shipId);
            known.Remove(shipId); factionOrig.Remove(shipId);
            Unpersist(shipId);
            ToDedi("destroy", 0, shipId);
            log($"FLY {shipId}: retired to the sim");
        }

        /// <summary>The nearest ship a player is flying (entity id, 0 if none — on foot doesn't count) and its distance.</summary>
        public int NearestPlayerShip(Vector3 at, out float distance)
        {
            distance = float.MaxValue; var best = 0;
            if (playfield?.Players == null) return 0;
            foreach (var p in playfield.Players.Values)
            {
                var d = p?.DrivingEntity;
                if (d == null) continue;
                var m = (d.Position - at).magnitude;
                if (m < distance) { distance = m; best = d.Id; }
            }
            return best;
        }

        /// <summary>Distance from <paramref name="at"/> to the nearest player (or their vehicle); +inf if none.</summary>
        public float NearestPlayerDistance(Vector3 at) => NearestPlayer(at);

        /// <summary>Where some player in this playfield is (their ship if piloting); false if nobody is here.</summary>
        public bool NearestPlayerPos(out Vector3 where) => NearestPlayer(Vector3.zero, out where) < float.MaxValue;

        /// <summary>Where a random player in this playfield is (their ship if piloting); false if nobody is here.</summary>
        public bool AnyPlayerPos(out Vector3 where)
        {
            where = Vector3.zero;
            var ps = playfield?.Players?.Values.Where(p => p != null).ToList();
            if (ps == null || ps.Count == 0) return false;
            var p = ps[rng.Next(ps.Count)];
            where = p.DrivingEntity != null ? p.DrivingEntity.Position : p.Position;
            return true;
        }

        /// <summary>Warp out of the system on purpose (end of a trip): same as an escape jump; the agent record stays.</summary>
        public void Depart(int shipId, string destination)
        {
            if (playfield?.Entities == null || !playfield.Entities.TryGetValue(shipId, out var e) || e == null || !known.Contains(shipId)) return;
            Remember(shipId, "trip.dest", destination ?? "");
            JumpOut(e, clock.Elapsed.TotalSeconds);
        }

        /// <summary>A trip: fly to <paramref name="dest"/>, stop <paramref name="stopShort"/> m short, then hold there. Persisted.</summary>
        public bool GoTo(int shipId, Vector3 dest, string destName, float stopShort, float cruise = 40f)
        {
            if (playfield?.Entities == null || !playfield.Entities.TryGetValue(shipId, out var e) || e == null || !known.Contains(shipId)) return false;
            var o = new Order { Kind = "goto", HoldAt = dest, LeaderName = destName ?? "", Distance = stopShort, Cruise = cruise };
            orders[shipId] = o; stored.Remove(shipId); Persist(shipId, o);
            log($"FLY order {shipId} '{e.Name}': goto '{destName}' {dest} stop {stopShort:0} m");
            Event($"{N(e)} sets course for {destName}");
            return true;
        }

        /// <summary>Close on another entity and stay <paramref name="distance"/> m off it (pirates; their turrets do the rest).</summary>
        /// <summary>Attackers circle their target at this sideways speed (m/s).</summary>
        const float OrbitSpeed = 25f;

        public bool Intercept(int shipId, int targetId, float distance, float cruise = 90f)
        {
            if (playfield?.Entities == null || !playfield.Entities.TryGetValue(shipId, out var e) || e == null || !known.Contains(shipId)) return false;
            orders[shipId] = new Order { Kind = "intercept", TargetId = targetId, Distance = distance, Cruise = cruise };
            stored.Remove(shipId); combatants.Add(shipId);
            if (!startBlocks.ContainsKey(shipId) && int.TryParse(S(() => e.Structure.BlockCount), out var sb)) startBlocks[shipId] = sb;
            log($"FLY order {shipId} '{e.Name}': intercept {targetId} at {distance:0} m");
            return true;
        }

        /// <summary>Break off and leave the system (flee from players, warp out when clear) — e.g. pirates after the fight.</summary>
        public void SendAway(int shipId)
        {
            if (playfield?.Entities == null || !playfield.Entities.TryGetValue(shipId, out var e) || e == null || !known.Contains(shipId)) return;
            orders.Remove(shipId); stored.Remove(shipId);
            var now = clock.Elapsed.TotalSeconds;
            fleeing[shipId] = new KeyValuePair<Vector3, double>(e.Position, now + FleeSeconds);
            if (!warp.ContainsKey(shipId)) warp[shipId] = new Warp { ReadyAt = now + 20 + rng.NextDouble() * 20, NextTaunt = double.MaxValue };
            log($"FLY {shipId} '{e.Name}': sent away");
            Event($"{N(e)} breaks off");
        }

        IEnumerable<IEntity> Ships() =>
            playfield?.Entities?.Values.Where(e => e != null && known.Contains(e.Id) && e.Structure != null)
            ?? Enumerable.Empty<IEntity>();

        public void Handle(int pid, string[] args)
        {
            var verb = args.Length > 0 ? args[0].ToLowerInvariant() : "";
            switch (verb)
            {
                case "cal":
                {
                    var ship = Ships().OrderBy(e => DistTo(pid, e)).FirstOrDefault();
                    if (ship == null) { reply(pid, "no «LS» ship here"); return; }
                    if (args.Length < 4 || !TryF(args[2], out calK) || !TryF(args[3], out calSec)) { reply(pid, "fly cal <axis> <k> <sec> [brake|coast]"); return; }
                    if (!TryAxis(args[1], out calDir)) { reply(pid, "axis: fwd back up down right left"); return; }
                    calAfter = args.Length > 4 ? args[4].ToLowerInvariant() : "brake";
                    calShip = ship.Id; calStart = clock.Elapsed.TotalSeconds; mode = "cal"; orders.Remove(ship.Id);
                    tracks.Remove(ship.Id);
                    log($"FLY cal ship {ship.Id} '{ship.Name}' mass={Mass(ship):0} kg axis={args[1]} k={calK} sec={calSec} then {calAfter}");
                    reply(pid, $"cal on {ship.Id}: Move({args[1]}*{calK}) for {calSec} s, then {calAfter}. Watch the log.");
                    break;
                }
                case "follow":   // debug broadcast: every «LS» ship here
                {
                    float d = 150f, c = 40f;
                    if (args.Length > 1) TryF(args[1], out d);
                    if (args.Length > 2) TryF(args[2], out c);
                    var n = 0;
                    foreach (var e in Ships().ToList())
                        if (GiveOrder(pid, e.Id, "follow", d) != null && orders.TryGetValue(e.Id, out var given)) { given.Cruise = c; Persist(e.Id, given); n++; }
                    reply(pid, $"(all) follow at {d:0} m, cruise {c:0} m/s; {n} ship(s)");
                    break;
                }
                case "order":   // directed, from the hail menu: order <shipId> follow|hold|release [distance]
                {
                    if (args.Length < 3 || !int.TryParse(args[1], out var id)) { reply(pid, "fly order <shipId> follow|hold|release [distance]"); return; }
                    var d = 0f; if (args.Length > 3) TryF(args[3], out d);
                    var answer = GiveOrder(pid, id, args[2].ToLowerInvariant(), d);
                    api.Network.SendToDedicatedServer(LivingSystemsMod.Channel,
                        Wire.Pack("orderreply", pid, id, answer ?? "No response.", StatusOf(id, pid)), playfield?.Name);
                    break;
                }
                case "fetch":   // test (PLAN next session): can a far-away frozen ship (proxy) be teleported to us, damage intact?
                {
                    if (args.Length < 2 || !int.TryParse(args[1], out var fid)) { reply(pid, "fly fetch <entityId>"); break; }
                    if (playfield?.Players == null || !playfield.Players.TryGetValue(pid, out var me) || me == null) { reply(pid, "can't find you"); break; }
                    var mePos = me.DrivingEntity != null ? me.DrivingEntity.Position : me.Position;
                    var meFwd = (me.DrivingEntity != null ? me.DrivingEntity.Rotation : me.Rotation) * Vector3.forward;
                    var to = mePos + meFwd * 300f;
                    var before = DescribeForFetch(fid, mePos);
                    log($"FETCH {fid} before: {before}");
                    reply(pid, $"fetch {fid} before: {before}");
                    if (args.Length >= 3 && args[2] == "pos")
                    {
                        // v2: set Position directly on the entity (works on loaded ships — spike Q1); proxy = the question
                        if (playfield.Entities.TryGetValue(fid, out var fe) && fe != null)
                        { try { fe.Position = to; log($"FETCH {fid}: Position set"); } catch (Exception ex) { reply(pid, $"fetch {fid}: Position set threw {ex.GetType().Name}: {ex.Message}"); } }
                    }
                    else ToDedi("tpto", fid, F1(to.x), F1(to.y), F1(to.z));
                    fetchCheck[fid] = new KeyValuePair<int, double>(pid, clock.Elapsed.TotalSeconds + 5);
                    break;
                }
                case "wreck":   // spike: spawn a trader hull ahead, strip its core, list + fill its containers
                    if (Wreck != null) Wreck(pid, args.Skip(1).ToArray());
                    break;
                case "prefab":  // test: cycle through candidate hulls 300 m ahead (/ls fly prefab traders|pirates|wrecks|next|prev|<name>|clear)
                    if (Prefab != null) Prefab(pid, args.Skip(1).ToArray());
                    break;
                case "patrol":  // test: call a station patrol onto the nearest live pirate attack right now
                    if (Patrol != null) Patrol(pid);
                    break;
                case "scan":    // 5 transponder pings, one every 2 s (same as flipping the LS_TRANSPONDER switch)
                    if (Scan != null) Scan(pid);
                    break;
                case "signals":
                    if (Signals != null) Signals(pid);
                    break;
                case "where":   // traffic in chat, relative to the ship you're flying (the LCD is no use from the cockpit)
                    if (Where != null) Where(pid); else reply(pid, "no traffic here");
                    break;
                case "hub":   // TEST ONLY: bearing + distance to a pirate hub in this playfield (real play finds hubs by rumour)
                    if (HubBearing != null) HubBearing(pid); else reply(pid, "no hub info here");
                    break;
                                case "probe":
                    probeArmed = !probeArmed; probes.Clear();
                    reply(pid, probeArmed ? "probe armed: spawn a ship now; it gets a small push from its first frame for 40 s, state changes logged as FLY probe" : "probe off");
                    break;
                case "hold":
                {
                    var n = 0; foreach (var e in Ships().ToList()) if (GiveOrder(pid, e.Id, "hold", 0) != null) n++;
                    reply(pid, $"(all) hold; {n} ship(s)"); break;
                }
                case "off": ReleaseAll(); reply(pid, "off: all ships released"); break;
                case "escort": Escort?.Handle(pid, args.Skip(1).ToArray()); break;
                case "regrecord":   // from the Dedi registry, after our regget
                {
                    var r = ShipRecord.FromLine(string.Join("|", args.Skip(1)));
                    if (r != null && r.Playfield == playfield?.Name && r.Field("state") != "departed")
                    {
                        if (known.Add(r.EntityId) && args.Length > 0 && regReceived) Event($"{StructureTag.Strip(r.Name)} registered");
                        if (!orders.ContainsKey(r.EntityId)) stored[r.EntityId] = r;
                        var orig = r.Field("faction.orig");
                        if (orig.Length > 0) factionOrig[r.EntityId] = orig;
                        foreach (var k in r.Extra.Keys.Where(k => k.StartsWith("memory.hostile.", StringComparison.Ordinal)))
                        {
                            if (!hostileTo.TryGetValue(r.EntityId, out var hs)) hostileTo[r.EntityId] = hs = new HashSet<string>();
                            hs.Add(k.Substring("memory.hostile.".Length));
                        }
                    }
                    break;
                }
                case "regdone":
                    if (regReceived) break;   // a reply to a repeated ask: records already merged
                    regReceived = true;
                    log($"FLY registry: {stored.Count} stored ship(s) for '{playfield?.Name}' ({stored.Values.Count(r => r.Order != null)} with orders), placing as ships appear");
                    break;
                case "feed":   // from the offline sim (Dedi): what ships we've met are up to elsewhere
                    if (args.Length > 1) Event(string.Join("|", args.Skip(1)));
                    break;
                case "risk":   // from the offline sim: what traders here know about pirates on their lanes
                    if (args.Length > 1 && Enum.TryParse<Risk>(args[1], out var risk)) LaneRisk = risk;
                    break;
                case "idle":
                    if (args.Length > 1 && (args[1] == "auto" || args[1] == "brake" || args[1] == "coast")) idle = args[1];
                    reply(pid, $"idle {idle}"); break;
                default:
                    reply(pid, "fly follow [dist] [cruise] | hold | off  (all ships; hail a ship for directed orders) | cal <axis> <k> <sec> [brake|coast] | probe | idle auto|brake|coast");
                    break;
            }
        }

        /// <summary>/ls fly where → SimLive lists the traffic for that player.</summary>
        public Action<int> Where;
        /// <summary>Test: where's the hub in this playfield (from the crews' home spots / hub wrecks in the traffic list).</summary>
        public Action<int> HubBearing;
        public Action<int> Signals;
        public Action<int> Scan;
        public Action<int, string[]> Wreck;
        public Action<int, string[]> Prefab;
        public Action<int> Patrol;

        /// <summary>The risk traders here quote, from the offline sim (null until the Dedi has told us).</summary>
        public Risk? LaneRisk;
        double nextSimTick;

        readonly Dictionary<int, KeyValuePair<int, double>> fetchCheck = new Dictionary<int, KeyValuePair<int, double>>();
        static string F1(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        string DescribeForFetch(int id, Vector3 from)
        {
            if (playfield?.Entities == null || !playfield.Entities.TryGetValue(id, out var e) || e == null) return "not in this playfield";
            var st = e.Structure;
            var blocks = st == null ? "no structure (proxy)" : $"blocks={S(() => st.BlockCount)} powered={S(() => st.IsPowered)}";
            return $"'{e.Name}' {e.Type} {(e.Position - from).magnitude:0} m away, {blocks}";
        }

        double nextBaseline;

        public void OnUpdate()
        {
            var now = clock.Elapsed.TotalSeconds;
            foreach (var fc in fetchCheck.ToList())
                if (now >= fc.Value.Value && playfield?.Players != null && playfield.Players.TryGetValue(fc.Value.Key, out var fp) && fp != null)
                {
                    var after = DescribeForFetch(fc.Key, fp.DrivingEntity != null ? fp.DrivingEntity.Position : fp.Position);
                    log($"FETCH {fc.Key} after 5 s: {after}");
                    reply(fc.Value.Key, $"fetch {fc.Key} after 5 s: {after}");
                    fetchCheck.Remove(fc.Key);
                }
            if (playfield != null && now >= nextSimTick) { nextSimTick = now + 15; ToDedi("simtick"); }   // drives the Dedi's offline sim
            if (playfield?.Entities != null && now >= nextBaseline)
            {   // every ship of ours near a player gets its starting block count: a BlockDestroyed whose victim the event text
                // doesn't name is matched by "which of ours just lost blocks" (DamagedNear) — official BlockCount only
                nextBaseline = now + 5;
                foreach (var id in known)
                    if (!startBlocks.ContainsKey(id) && playfield.Entities.TryGetValue(id, out var be) && be?.Structure != null && NearestPlayer(be.Position) < 2000f
                        && int.TryParse(S(() => be.Structure.BlockCount), out var bb) && bb > 0) startBlocks[id] = bb;
            }
            var dt = (float)(now - lastTick);
            lastTick = now;
            if (playfield == null || dt <= 0f || dt > 1f) return;
            if (probeArmed) Probe(now, dt);
            if (now >= nextSeenScan)
            {
                nextSeenScan = now + 1;
                foreach (var e in Ships()) if (!firstSeen.ContainsKey(e.Id)) firstSeen[e.Id] = now;
                if (combatants.Count > 0) CheckCombatants();
                foreach (var e in Ships().ToList()) { CheckClaimed(e); CheckDead(e); }
                if (regReceived && playfield.Entities != null)
                    foreach (var e in playfield.Entities.Values.ToList()) if (e?.Structure != null) NoteImpostor(e, now);
            }
            if (mode == "cal") Calibrate(now, dt);
            if (!regReceived && now >= nextRegGet)
            {
                // fast while the PfServer is starting up, then slowly — never give up (a ship would ignore its order)
                ToDedi("regget", playfield.Name); regAsks++; nextRegGet = now + (regAsks < 30 ? 5 : 60);
                if (regAsks > 1) log($"FLY registry: asking again ({regAsks})");
            }
            ReconcileStored(now);
            if (orders.Count == 0 && fleeing.Count == 0) return;
            ResolveLeaders(now);

            // leaders measured once per frame (players tracked under a negative key)
            var leaders = new Dictionary<int, IEntity>();
            foreach (var o in orders.Values)
                if (o.Kind == "follow" && o.LeaderPid >= 0 && !leaders.ContainsKey(o.LeaderPid))
                {
                    var lead = Leader(o.LeaderPid);
                    leaders[o.LeaderPid] = lead;
                    if (lead != null) Measure(-o.LeaderPid, lead.Position, lead.Rotation, dt);
                }

            foreach (var e in Ships().ToList())
            {
                if (e.Id == calShip) continue;
                var isFleeing = fleeing.TryGetValue(e.Id, out var flee);
                if (isFleeing && warp.TryGetValue(e.Id, out var wp))
                {
                    if (now >= wp.ReadyAt) { SayNear(e, 3000f, "Warp drive ready — we're gone!"); JumpOut(e, now); continue; }
                    if (!wp.Warned && now >= wp.ReadyAt - WarpWarn) { wp.Warned = true; SayNear(e, 3000f, "Warp drive charging…"); Event($"{N(e)}: warp drive charging"); }
                    if (now >= wp.NextTaunt && NearestPlayer(e.Position) <= TauntRange)
                    {
                        wp.NextTaunt = now + TauntEvery + rng.NextDouble() * 10;
                        SayNear(e, TauntRange * 2, Taunts[rng.Next(Taunts.Length)]);
                    }
                }
                if (isFleeing)
                {
                    if (NearestPlayer(e.Position) < EscapeRange) escapingSince.Remove(e.Id);
                    else if (!escapingSince.TryGetValue(e.Id, out var since)) escapingSince[e.Id] = now;
                    else if (now - since >= EscapeHoldSeconds) { escapingSince.Remove(e.Id); JumpOut(e, now); continue; }   // got away: leave the system
                }
                Order order;
                if (isFleeing) order = new Order { Kind = "flee", Cruise = float.MaxValue };   // flat out: GoTo caps it at the ship's max speed
                else if (!orders.TryGetValue(e.Id, out order)) continue;
                var t = Measure(e.Id, e.Position, e.Rotation, dt);
                if (!t.HasVel) continue;
                if (t.HasRotSet) t.RotDrift = Math.Max(t.RotDrift, Quaternion.Angle(t.RotSet, e.Rotation));
                // The game eases a set Rotation in over several frames (measured: real turn ≈ ¼ of commanded when we
                // built on the read-back value). Steer from our own intended rotation; resync if the ship was knocked away.
                if (t.HasOwnRot && Quat.AngleDeg(t.OwnRot, t.State.Rot) < ResyncDeg) t.State.Rot = t.OwnRot;
                t.Response.Observe(t.LastThrottle, V(t.FrameAccel), dt);
                if (t.Response.State != t.LoggedState)
                {
                    log($"FLY {e.Id} response {t.LoggedState} -> {t.Response.State} caps(f/b/u/d/r/l)={Caps(t)}");
                    t.LoggedState = t.Response.State;
                }
                var caps = t.Response.Caps(MassOf(e, t, now));
                FlightCommand cmd;
                var dist = 0f;
                if (isFleeing)
                {
                    var away = NearestPlayer(e.Position, out var chaser) < float.MaxValue ? e.Position - chaser : e.Position - flee.Key;   // away from whoever is closest now
                    if (away.sqrMagnitude < 1f) away = e.Rotation * Vector3.forward;
                    cmd = Behaviours.GoTo(t.State, caps, V(e.Position + away.normalized * FleeDistance), 0f, order.Cruise);
                    dist = NearestPlayer(e.Position);   // logged: how far the chaser is
                }
                else if (order.Kind == "follow" && order.Waiting)
                    cmd = Behaviours.HoldAt(t.State, caps, V(order.WaitAt), order.Cruise);   // leader away: wait here
                else if (order.Kind == "follow" && leaders.TryGetValue(order.LeaderPid, out var lead) && lead != null)
                {
                    var lt = tracks[-order.LeaderPid];
                    cmd = Behaviours.Follow(t.State, caps, V(lead.Position), V(lt.Vel), V(lead.Rotation * Vector3.up), order.Distance, order.Cruise);
                    dist = (lead.Position - e.Position).magnitude;
                }
                else if (order.Kind == "hold")
                    cmd = Behaviours.HoldAt(t.State, caps, V(order.HoldAt), order.Cruise);
                else if (order.Kind == "goto")
                {
                    dist = (order.HoldAt - e.Position).magnitude;
                    if (dist <= order.Distance + 100f)
                    {
                        // arrived: hold here (the trip is over; the escort run sees "arrived" by distance)
                        var held = new Order { Kind = "hold", HoldAt = e.Position };
                        orders[e.Id] = held; Persist(e.Id, held);
                        log($"FLY {e.Id} '{e.Name}': arrived at '{order.LeaderName}'");
                        Event($"{N(e)} arrived at {order.LeaderName}");
                        order = held;
                        cmd = Behaviours.HoldAt(t.State, caps, V(held.HoldAt), held.Cruise);
                    }
                    else cmd = Behaviours.GoTo(t.State, caps, V(order.HoldAt), order.Distance, order.Cruise);
                }
                else if (order.Kind == "intercept" && playfield.Entities.TryGetValue(order.TargetId, out var tgt) && tgt != null)
                {
                    var tv = tracks.TryGetValue(order.TargetId, out var tt) ? tt.Vel : Vector3.zero;   // target is often ours (the trader): reuse its track
                    // circle the target (each ship its own way round): a parked attacker never brought its fixed guns
                    // round on a still player and was an easy target
                    // the orbit's plane tilts slowly (up to ~70°, each ship on its own ~90 s cycle and side): it swings over
                    // and under the target, not just round it on one level
                    var tilt = 1.2f * (float)Math.Sin(now * 2 * Math.PI / 90 + e.Id);
                    var side = new Vector3(Mathf.Cos(e.Id), 0f, Mathf.Sin(e.Id));
                    var axis = Vector3.up * Mathf.Cos(tilt) + side * Mathf.Sin(tilt);
                    cmd = Behaviours.Attack(t.State, caps, V(tgt.Position), V(tv), order.Distance, order.Cruise, orbit: (e.Id % 2 == 0 ? 1f : -1f) * OrbitSpeed, orbitAxis: V(axis));
                    dist = (tgt.Position - e.Position).magnitude;
                }
                else
                    cmd = Behaviours.Hold(t.State);   // leader gone: stop where we are
                var o = FlightController.Step(t.State, caps, cmd, dt);
                t.State.AngVel = o.AngVel;   // we own the rotation, so our angular rate is the real one
                e.Rotation = U(o.Rot);
                t.RotSet = U(o.Rot); t.HasRotSet = true;
                t.OwnRot = o.Rot; t.HasOwnRot = true;
                t.RotErr = Math.Max(t.RotErr, Quat.AngleDeg(t.State.Rot, cmd.DesiredRot));
                t.CmdTurnSum += o.AngVel.Length * 57.29578f; t.CmdTurnN++;
                t.LastThrottle = Push(e, t, o.LocalAccel, cmd.DesiredVel.Length < 0.5f);
                t.PushSum += U(o.LocalAccel);
                if (now >= t.NextLog) LogTrack(e, t, now, dist, order.Kind);
            }
        }

        // ---- probe: which game-side flag flips when a fresh ship starts obeying Move? ----
        bool probeArmed;
        class ProbeState { public double Start; public string Last; public bool Moving; public bool Done; }
        readonly Dictionary<int, ProbeState> probes = new Dictionary<int, ProbeState>();
        const double ProbeSeconds = 40;

        void Probe(double now, float dt)
        {
            foreach (var e in playfield.Entities.Values.Where(x => x != null && StructureTag.IsTagged(x.Name)).ToList())
            {
                if (!probes.TryGetValue(e.Id, out var p))
                {
                    if (firstSeen.ContainsKey(e.Id)) continue;   // existed before arming: not fresh
                    probes[e.Id] = p = new ProbeState { Start = now };
                    firstSeen[e.Id] = now;
                }
                if (p.Done) continue;
                var age = now - p.Start;
                var t = Measure(e.Id, e.Position, e.Rotation, dt);
                var st = e.Structure;
                if (age < ProbeSeconds) Safe(() => e.Move(Vector3.forward * 3f));
                else { Safe(() => e.Move(Vector3.zero)); p.Done = true; log($"FLY probe {e.Id} done"); continue; }
                var flags = $"type={e.Type} local={S(() => e.IsLocal)} proxy={S(() => e.IsProxy)} docked={S(() => e.DockedTo)}" +
                    (st == null ? " structure=null" :
                    $" ready={S(() => st.IsReady)} powered={S(() => st.IsPowered)} blocks={S(() => st.BlockCount)} devices={S(() => st.DeviceCount)}" +
                    $" mass={S(() => st.TotalMass)} fuel={S(() => st.Fuel)} pwrOut={S(() => st.PowerOutCapacity)} pwrUse={S(() => st.PowerConsumption)}" +
                    $" dmg={S(() => st.DamageLevel)} shield={S(() => st.ShieldLevel)} size={S(() => st.SizeClass)}");
                var moving = t.Vel.magnitude > 0.05f;
                if (flags != p.Last || moving != p.Moving)
                {
                    log($"FLY probe {e.Id} age={age:0.00} speed={t.Vel.magnitude:0.00}{(moving && !p.Moving ? " <<< STARTED MOVING" : "")} {flags}");
                    p.Last = flags; p.Moving = moving;
                }
            }
        }

        static string S<T>(Func<T> f) { try { return Convert.ToString(f(), CultureInfo.InvariantCulture); } catch (Exception ex) { return "!" + ex.GetType().Name; } }

        /// <summary>
        /// Send the push for a local acceleration; returns the push now in effect (Move persists until the
        /// next Move — MoveStop does not clear it). ~0 acceleration: Move(zero) is the game's strong brake
        /// (~12 m/s²), right for stopping but it fights cruising, so "auto" brakes only when the ship should
        /// stand still and otherwise coasts with a negligible push along its velocity.
        /// </summary>
        Vec3 Push(IEntity e, Track t, Vec3 localAccel, bool wantStop)
        {
            if (localAccel.Length < 0.05f)
            {
                var brake = idle == "brake" || (idle == "auto" && wantStop);
                if (brake) { e.Move(Vector3.zero); return Vec3.Zero; }
                // coast: a negligible FORWARD throttle (any value on an on/off axis would be full sideways thrust)
                e.Move(new Vector3(0f, 0f, 0.01f));
                return Vec3.Zero;
            }
            // Measured: forward/back is a throttle (100 = full thrust, proportional below), but up/down is
            // on/off — 0.2, 0.5 and 2 all gave full lift — and so, by the follow logs, is left/right. Proportional
            // pushes there made every small correction a full-thrust kick (the bobbing). Drive them like a player's
            // keys: nothing for small corrections, full thrust when a big part of full thrust is wanted.
            // Left/right turned out proportional too (±1 gave ~1 % sideways thrust: no lateral control, sideways
            // overshoot; earlier runs learned 0.09–0.16 per unit ≈ 9–16 m/s² at 100). Only up/down is on/off.
            var th = t.Response.ThrottleFor(localAccel);
            // on/off key decided against the cautious planning estimate: against the high throttle guess a wanted
            // 6 m/s² read as 20 % and the lift key was never pressed (up/down stayed unlearned all run)
            th = new Vec3(th.X, Key(t.Response.FractionOfPlanned(localAccel).Y), th.Z);
            e.Move(new Vector3(th.X * ThrottleScale, th.Y, th.Z * ThrottleScale));
            return th;
        }

        /// <summary>Move units for full thrust on the proportional axes, forward/back and left/right (fwd 100 → full, measured).</summary>
        const float ThrottleScale = 100f;
        /// <summary>Wanted fraction of full thrust on an on/off axis below this: key released.</summary>
        const float KeyThrottle = 0.3f;
        static float Key(float throttle) => throttle > KeyThrottle ? 1f : throttle < -KeyThrottle ? -1f : 0f;

        void Calibrate(double now, float dt)
        {
            var e = playfield.Entities != null && playfield.Entities.TryGetValue(calShip, out var x) ? x : null;
            if (e == null) { mode = "off"; calShip = -1; return; }
            var t = Measure(e.Id, e.Position, e.Rotation, dt);
            var el = now - calStart;
            if (el < calSec) e.Move(calDir * calK);
            else if (calAfter == "brake") e.Move(Vector3.zero);
            else if (el - dt < calSec) e.MoveStop();   // coast: one MoveStop, then nothing
            if (now >= t.NextLog) LogTrack(e, t, now, 0f, $"cal t={el:0.00}");
            if (el > calSec + 8) { log($"FLY cal done {e.Id}"); mode = "off"; calShip = -1; }
        }

        Track Measure(int key, Vector3 pos, Quaternion rot, float dt)
        {
            if (!tracks.TryGetValue(key, out var t)) tracks[key] = t = new Track { NextLog = clock.Elapsed.TotalSeconds + 0.25 };
            if (t.HasPos)
            {
                var v = (pos - t.LastPos) / dt;
                t.Vel = t.HasVel ? Vector3.Lerp(t.Vel, v, 0.5f) : v;   // light smoothing: positions jitter per frame
                if (t.HasVel) { t.FrameAccel = Quaternion.Inverse(rot) * ((t.Vel - t.LastVel) / dt); t.AccelSum += t.FrameAccel; t.AccelN++; }
                t.LastVel = t.Vel; t.HasVel = true;
            }
            t.LastPos = pos; t.HasPos = true;
            t.State.Pos = V(pos); t.State.Vel = V(t.Vel); t.State.Rot = Q(rot);
            return t;
        }

        void LogTrack(IEntity e, Track t, double now, float dist, string tag = null)
        {
            var n = Math.Max(1, t.AccelN);
            var local = Quaternion.Inverse(e.Rotation) * t.Vel;
            log($"FLY {tag ?? mode} {e.Id} speed={t.Vel.magnitude:0.0} vLocal={F(local)} aMeas={F(t.AccelSum / n)} aCmd={F(t.PushSum / n)}" +
                (dist > 0 ? $" dist={dist:0}" : "") + $" resp={t.Response.State} caps(f/b/u/d/r/l)={Caps(t)}" +
                $" fuel={S(() => e.Structure.Fuel)} pwrUse={S(() => e.Structure.PowerConsumption)}/{S(() => e.Structure.PowerOutCapacity)}" +
                $" headingErr={t.RotErr:0} turnCmd={t.CmdTurnSum / Math.Max(1, t.CmdTurnN):0.0}dps turnReal={RealTurn(e, t, now)}dps rotKept={(t.RotDrift < 0.5f ? "yes" : $"NO drift={t.RotDrift:0.0}")}");
            t.RotErr = 0; t.RotDrift = 0; t.CmdTurnSum = 0; t.CmdTurnN = 0;
            t.AccelSum = Vector3.zero; t.PushSum = Vector3.zero; t.AccelN = 0;
            t.NextLog = now + 0.25;
        }

        /// <summary>Intended vs real rotation further apart than this: something else turned the ship, adopt the real one.</summary>
        const float ResyncDeg = 25f;

        /// <summary>Learned full-thrust accel per direction, m/s²; "~6" = still the cautious prior.</summary>
        static string Caps(Track t) => string.Join("/", new[] { G(t, 2, false), G(t, 2, true), G(t, 1, false), G(t, 1, true), G(t, 0, false), G(t, 0, true) });
        static string G(Track t, int axis, bool neg) =>
            (t.Response.Learned(axis, neg) ? "" : "~") + t.Response.FullAccel(axis, neg).ToString("0.0", CultureInfo.InvariantCulture);

        static string RealTurn(IEntity e, Track t, double now)
        {
            var r = e.Rotation;
            var s = t.HasLogRot && now > t.LogRotAt ? (Quaternion.Angle(t.LogRot, r) / (now - t.LogRotAt)).ToString("0.0", CultureInfo.InvariantCulture) : "?";
            t.LogRot = r; t.LogRotAt = now; t.HasLogRot = true;
            return s;
        }

        IEntity Leader(int pid)
        {
            if (playfield?.Players == null || !playfield.Players.TryGetValue(pid, out var p) || p == null) return null;
            return p.DrivingEntity ?? (IEntity)p;
        }

        float DistTo(int pid, IEntity e) =>
            playfield?.Players != null && playfield.Players.TryGetValue(pid, out var p) && p != null
                ? (p.Position - e.Position).magnitude : float.MaxValue;

        static float Mass(IEntity e) { try { return e.Structure.TotalMass; } catch { return -1; } }

        static bool TryAxis(string s, out Vector3 d)
        {
            switch (s.ToLowerInvariant())
            {
                case "fwd": d = Vector3.forward; return true;
                case "back": d = Vector3.back; return true;
                case "up": d = Vector3.up; return true;
                case "down": d = Vector3.down; return true;
                case "right": d = Vector3.right; return true;
                case "left": d = Vector3.left; return true;
                default: d = Vector3.zero; return false;
            }
        }

        static bool TryF(string s, out float f) =>
            float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out f) && !float.IsNaN(f) && !float.IsInfinity(f);

        static void Safe(Action a) { try { a(); } catch { } }
        static string F(Vector3 v) => $"({v.x:0.0},{v.y:0.0},{v.z:0.0})";
        static Vec3 V(Vector3 v) => new Vec3(v.x, v.y, v.z);
        static Vector3 U(Vec3 v) => new Vector3(v.X, v.Y, v.Z);
        static Quat Q(Quaternion q) => new Quat(q.x, q.y, q.z, q.w);
        static Quaternion U(Quat q) => new Quaternion(q.X, q.Y, q.Z, q.W);
    }
}
