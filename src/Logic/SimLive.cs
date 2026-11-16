using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// Stage 3, PfServer side: sim agents made real while a player is here (the Dedi decides who; see DediSim).
    ///   trader → runs a lane (station approach ↔ warp point) the Dedi sends as "traffic"; spawned where the lane puts it
    ///            once a player is within 2 km of that spot (we ask: "simwant"), flies the rest of the lane
    ///   pirate → spawned 1.6 km from a live trader and intercepts it; or, found lurking at its ambush spot, waits there
    ///   fight  → a player near an engagement: all of it goes live (the Dedi decides; we just ask for whoever is near)
    /// Back to the sim ("simend|agent|how|frac"): reached the end of its lane → "arrived"; out of every player's bubble for
    /// 20 s → "left" (frac = how far along the lane it got);
    /// core gone → "dead"; a pirate that warps out (morale) → "fled"; vanished → "left".
    /// Hail it and accept an escort → the escort run owns it until it jumps (then the Dedi adopts it again).
    ///
    /// Wire: Dedi → us  "0|fly|sim|traffic|id;name;leg;ax;ay;az;bx;by;bz;t0;t1;live;other;station|…"
    ///                  "0|fly|sim|trader|agent|name|prefab|bound"  "0|fly|sim|pirate|agent|name|prefab|traderEntity"
    ///                  "0|fly|sim|spawned|agent|entity"
    ///       us → Dedi  "simspawn|agent|prefab|x|y|z|yaw|name|group"  "simend|agent|how|frac"  "simhello|playfield"
    ///                  "simstations|playfield|presentBaseIds|id;name;x;y;z|…"  "simwant|agent"
    /// </summary>
    sealed class SimLive
    {
        readonly FlightDriver fly;
        readonly Action<string> log;
        readonly System.Random rng = new System.Random();

        sealed class Live
        {
            public string Agent = "", Kind = "";
            public int Entity;            // 0 until spawned
            public int Target;            // pirate: trader entity
            public Vector3 Dest;          // trader: end of its line
            public double Since, OutSince = -1;
            public bool Steered, Released;
            public string Bound = "";     // trader: the system it's bound for (named in escort offers)
            public Vector3 LaneA;         // trader: start of its lane (for how far along it got)
            public float Frac = -1;
            public float Cruise = 40f;    // trader: its own speed (the sim's), not a flat top speed
            public double SpoolAt = -1;   // trader at its warp point: spooling the drive since (then it jumps)
            public bool Slow;             // trader: on its final approach to the berth (slow, precise)
            public int Chase;             // lurker: the player ship it went for (0 = at / back to its spot)
            public bool Returning;        // lurker: heading back to its spot; won't go for anyone until it's there
            public bool Departing;        // pirate the sim sent off: flies out to its warp point, spools, jumps (like a trader)
            public bool Arriving;         // pirate just in from warp: flies from the system edge to its spot
            public bool Bitter;           // lurker: chasing someone it holds a grudge against (longer reach)
            public bool Pack;             // lurker: called in by its crew (defending the hub / the spot): longer leash
            public double WatchSince = -1; // lurker: a player ship has hung around within Notice since then
            public bool Patrolling;       // hub crew on patrol duty: orbits the hub, sees further
            public int PatrolLeg;         // which orbit waypoint it's flying to
            public double SightSince = -1; // patrol: a player ship in its long sight since then
            public double PatrolOrderAt = -1; // patrol: when its waypoint order was last given (re-given every 15 s)
            public string FillItem = "";  // its hold: loaded into its containers once it's there (live cargo / a pirate's loot)
            public int FillAmt;
            public int Blocks0;           // block count when first seen (hull lost live = blocks now / this)
            public float Hull = -1;
            public string Station = "", LegKind = "";
            public readonly Core.Sim.LiveRules.StuckWatch Stuck = new Core.Sim.LiveRules.StuckWatch();   // trader: progress on its lane
            public double NextChat;       // docked trader: next station-control line (players nearby)
            public bool ClearedSaid;      // docked trader: "cleared to undock" said this stay
            public bool Scuffed;          // its sim damage drawn onto the fresh hull (once per spawn)
            public double NextMend;       // docked: next repair pass
            public string Prefab = "";    // trader: its hull (guns → whether it wants an escort)
            public bool Passing;          // trader: crossing a stationless system on the way (transit), not leaving a station
            public Dictionary<string, string> Memory = new Dictionary<string, string>();   // memory.* from the agent (+ its hub's)
            public HashSet<int> HitBy = new HashSet<int>();
            /// <summary>Last "Big mistake." (once a minute: circling in and out of its leash re-started the chase every pass).</summary>
            public double LastTaunt = -1e9;
            public bool Fueled;           // tank topped up once it's there (some prefabs spawn dry and unpowered)
        }

        /// <summary>A sim trader's track this leg, as the Dedi sent it.</summary>
        public sealed class Track
        {
            public string Agent = "", Name = "", Leg = "", Other = "", Station = "", Kind = "trader", Role = "", Cargo = "";
            public int Amt;
            /// <summary>Passing through a stationless system (sim leg "pass"): flown like an outbound lane, jumps on at the end.</summary>
            public bool Passing;
            public bool IsTrader => Kind == "trader";
            /// <summary>Where it is now: moving along its lane, or sitting still (docked, lurking, in a fight).</summary>
            public Vector3 Pos(double unix) => Leg == "out" || Leg == "in" ? At(unix) : A;
            public Vector3 A, B;
            public double T0, T1;
            public bool Live;
            public float Hull = 1;
            public Vector3 At(double unix)
            {
                if (T1 <= T0) return B;
                return Vector3.Lerp(A, B, (float)Math.Max(0, Math.Min(1, (unix - T0) / (T1 - T0))));
            }
        }
        /// <summary>One LCD transponder line's data. (No C# tuples: the game's runtime has no System.ValueTuple.)</summary>
        public sealed class Transponder
        {
            public string Name = "", Leg = "", Other = "", Station = "", Role = "", Cargo = "";
            public int Amt; public Vector3 Pos; public float Hull = 1; public bool Passing;
            /// <summary>A wreck's distress beacon (Leg "wreck"): minutes since it went down.</summary>
            public double AgeMin;
            /// <summary>An arriving ship (Leg "arriving"): seconds until it drops out of warp.</summary>
            public double EtaSec;
            public bool IsWreck => Leg == "wreck";
            /// <summary>"bulk hauler · Iron Ore 420" / "bulk hauler · empty hold"</summary>
            public string What => $"{Role} · {(Amt > 0 ? $"{Core.Sim.World.Humanize(Cargo)} {Amt}" : IsWreck ? "empty hold" : "empty hold")}";
        }
        readonly Dictionary<string, Track> traffic = new Dictionary<string, Track>();
        readonly Dictionary<string, double> wanted = new Dictionary<string, double>();
        double nextStations;
        string lastStations = "";
        static double Unix => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        /// <summary>
        /// Transponders for the LCD: every sim trader with a place here — name, where it is now (the real ship if it's
        /// live), leg (out/in/dock/fight), the other system, its station.
        /// </summary>
        public IEnumerable<Transponder> Transponders()
        {
            var now = Unix;
            // pirates have no transponders — except hub crews while ShowHubs is on (test phase: no rumours yet to find them by)
            foreach (var t in traffic.Values.Where(x => x.IsTrader || x.Leg == "wreck" || (ShowHubs && x.Leg == "home")).ToList())
            {
                var pos = t.Pos(now);
                var hull = t.Hull;
                if (live.TryGetValue(t.Agent, out var l) && l.Entity > 0 && Pf?.Entities != null && Pf.Entities.TryGetValue(l.Entity, out var e) && e != null)
                { pos = e.Position; if (l.Hull >= 0) hull *= l.Hull; }   // live: what it had × what it's lost since
                yield return new Transponder { Name = t.IsTrader ? t.Name + " #" + CallSign(t.Agent) : t.Name, Pos = pos, Leg = t.Leg, Other = t.Other, Station = t.Station, Hull = hull, Role = t.Role, Cargo = t.Cargo, Amt = t.Amt, AgeMin = (now - t.T0) / 60, EtaSec = t.T1 - now, Passing = t.Passing };
            }
        }
        /// <summary>A ship's transponder call sign: 4 hex digits, stable per sim agent (look-alike names stay apart).</summary>
        public static string CallSign(string agent)
        {
            unchecked { uint h = 2166136261; foreach (var c in (agent ?? "") + "|callsign") { h ^= c; h *= 16777619; } return (h & 0xFFFF).ToString("X4"); }
        }

        readonly Dictionary<string, Live> live = new Dictionary<string, Live>();
        double nextTick;

        /// <summary>A trader is made real when a player is this close to its lane position; retired beyond <see cref="Bubble"/>.</summary>
        const float SpawnRange = 2000f, LineLength = 6000f, Bubble = 2500f, PirateRange = 1600f;
        /// <summary>Hub crews spawn when a player is this close (inside <see cref="Bubble"/>, or they would retire at once).</summary>
        const float HubRange = 2400f;
        /// <summary>Test phase: hub crews show in /ls fly where (and the LCD) so a hub can be found before rumours exist.</summary>
        const bool ShowHubs = false;   // rumours (hail → Talk) point at hubs now; on = test listing in /ls fly where + LCD
        const double OutGrace = 20, SpawnTimeout = 90;
        /// <summary>A lurker goes for a player ship this close; gives up and goes back to its spot when the chase gets this
        /// far from the spot (can't be lured away and left out of position).</summary>
        const float Aggro = 1000f, Leash = 2000f;
        /// <summary>Hang around within <see cref="Notice"/> m of a resting / lurking pirate for <see cref="NoticeSeconds"/> and it
        /// spots you anyway; whoever goes for you calls in every live crewmate within <see cref="PackRange"/> (same hub — the
        /// whole den scrambles), and a pack chases <see cref="PackLeash"/>× as far.</summary>
        const float Notice = 2000f, PackRange = 4000f, PackLeash = 1.75f;
        const double NoticeSeconds = 90;
        /// <summary>One crew of a hub a player watches flies a slow orbit of <see cref="PatrolRadius"/> m round it, duty
        /// rotating every <see cref="PatrolDuty"/> s; it spots players <see cref="PatrolSight"/>× as far as a resting one.</summary>
        const float PatrolRadius = 1200f, PatrolSpeed = 25f, PatrolSight = 2f;
        /// <summary>The patrol's long sight needs it out on its orbit (≥ <see cref="PatrolOut"/> m from its spot) and you in
        /// view for <see cref="PatrolReact"/> s — time to back off when you see it coming round. Inside Aggro: instant.</summary>
        const float PatrolOut = 600f;
        const double PatrolReact = 10;
        const double PatrolDuty = 600;
        readonly Dictionary<string, KeyValuePair<string, double>> hubPatrol = new Dictionary<string, KeyValuePair<string, double>>(StringComparer.Ordinal);

        /// <summary>Hub crews resting near a player: one of them on patrol (orbit), the duty rotating.</summary>
        void HubPatrols(double now)
        {
            var crews = live.Values.Where(l => l.Kind == "pirate" && l.Entity != 0 && l.Target == 0 && !l.Departing && !l.Released
                                             && traffic.TryGetValue(l.Agent, out var t) && t.Leg == "home")
                                   .GroupBy(l => traffic[l.Agent].Other).ToList();
            foreach (var g in crews)
            {
                var list = g.OrderBy(x => x.Agent, StringComparer.Ordinal).ToList();
                var centre = list.Aggregate(Vector3.zero, (acc, x) => acc + x.Dest) / list.Count;   // crews rest 80–250 m round the hub
                hubPatrol.TryGetValue(g.Key, out var cur);
                var onDuty = list.FirstOrDefault(x => x.Agent == cur.Key && x.Patrolling);
                if (onDuty != null && now < cur.Value) { PatrolStep(onDuty, centre); continue; }
                if (onDuty != null)
                {   // shift over: back to its spot, the next one takes it
                    onDuty.Patrolling = false;
                    if (onDuty.Chase == 0) { fly.GoTo(onDuty.Entity, onDuty.Dest, "its spot", 0f, 30f); onDuty.Returning = true; }
                }
                if (list.Count < 2) { hubPatrol.Remove(g.Key); continue; }   // a lone crew stays put
                var idle = list.Where(x => x.Chase == 0 && !x.Arriving && !x.Returning && x != onDuty).ToList();
                if (idle.Count == 0) continue;
                var next = idle[(int)((now / PatrolDuty) % idle.Count)];
                next.Patrolling = true;
                {   // starts at the orbit waypoint nearest to where it is (a reload mid-orbit doesn't send it across the hub)
                    var pos = Pf.Entities.TryGetValue(next.Entity, out var ne) && ne != null ? ne.Position : next.Dest;
                    next.PatrolLeg = Enumerable.Range(0, 8).OrderBy(i => (pos - (centre + new Vector3(Mathf.Cos(i * Mathf.PI / 4f), 0.15f * Mathf.Sin(i * Mathf.PI / 2f), Mathf.Sin(i * Mathf.PI / 4f)) * PatrolRadius)).sqrMagnitude).First();
                }
                hubPatrol[g.Key] = new KeyValuePair<string, double>(next.Agent, now + PatrolDuty);
                log($"SIM {next.Agent} on patrol round {g.Key}");
                PatrolStep(next, centre, true);
            }
        }

        void PatrolStep(Live l, Vector3 centre, bool start = false)
        {
            if (l.Chase != 0 || !Pf.Entities.TryGetValue(l.Entity, out var e) || e == null) return;
            Vector3 Wp(int i) { var a = i * Mathf.PI / 4f; return centre + new Vector3(Mathf.Cos(a), 0.15f * Mathf.Sin(2 * a), Mathf.Sin(a)) * PatrolRadius; }
            var reached = (e.Position - Wp(l.PatrolLeg)).magnitude <= 200f;
            // re-given now and then: another order (the spawn's "go to your spot", a fall-back) may have replaced it
            if (!start && !reached && fly.Now - l.PatrolOrderAt < 15) return;
            if (!start && reached) l.PatrolLeg = (l.PatrolLeg + 1) % 8;
            if (fly.GoTo(l.Entity, Wp(l.PatrolLeg), "patrol", 0f, PatrolSpeed)) l.PatrolOrderAt = fly.Now;
        }

        public SimLive(FlightDriver fly, Action<string> log) { this.fly = fly; this.log = log; fly.ShipGone += OnGone; fly.Where = Where; fly.HubBearing = HubBearing; fly.SimOwns = id => wrecks.ContainsKey(id) || live.Values.Any(l => l.Entity == id); fly.Signals = Signals; fly.Wreck = WreckSpike; fly.Prefab = PrefabCmd; fly.Patrol = pid => { forcePatrol = true; fly.Say(pid, "Patrol: the next minute's roll is a yes (needs a live pirate attack here)."); }; fly.Scan = pid => { scanLeft[pid] = 5; fly.Say(pid, "Transponder scan: 5 pings."); }; fly.Remembered += OnRemembered; fly.CombatantHitBy += OnPirateHit; }

        // ---- pirate memory ("this guy helped the trader I was after", "he killed my buddy", "I chased him") ----
        // Kept on the agent (hub crews are permanent), "killed my buddy" on the hub (every crew there, new recruits too).

        void RememberLive(Live l, string key, string value)
        {
            if (l.Memory.TryGetValue(key, out var old) && old == value) return;
            l.Memory[key] = value;
            fly.Send("simmem", l.Agent, key, value);
            log($"SIM {l.Agent} remembers {key} = {value}");
        }

        /// <summary>A freshly spawned ship can come dry (prefab saved with empty tanks: no fuel, no power, can't move). Fill
        /// the fuel tank to ~70 % once. True when done (or nothing to do).</summary>
        bool Refuel(Live l, IEntity e)
        {
            try
            {
                var t = e.Structure.FuelTank;
                if (t == null || t.Capacity <= 0) return false;   // not ready yet: try again next tick
                var before = t.Content;
                if (before < t.Capacity * 0.3f) t.AddContent(t.Capacity * 0.7f - before);
                log($"SIM {l.Agent} fuel {before:0} → {t.Content:0}/{t.Capacity:0}, powered={e.Structure.IsPowered}");
                return true;
            }
            catch (Exception ex) { log($"SIM {l.Agent} refuel failed: {ex.GetType().Name}"); return true; }
        }

        static string Stamp() => DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

        void OnPirateHit(int entity, int pid)
        {
            var l = live.Values.FirstOrDefault(x => x.Entity == entity && x.Kind == "pirate");
            if (l == null) return;
            // shot while sitting at its spot: it goes for the shooter's ship (on foot: its turrets answer, it holds)
            if (l.Target == 0 && l.Chase == 0 && !l.Released && Pf?.Players != null && Pf.Players.TryGetValue(pid, out var shooter) && shooter?.DrivingEntity != null
                && fly.Intercept(l.Entity, shooter.DrivingEntity.Id, 300f))
            {
                l.Chase = shooter.DrivingEntity.Id; l.Returning = false; l.Bitter = true;
                if (fly.Now - l.LastTaunt > 60)
                {   // what it really knows about you first ("You're the one who killed Rust Jaw…"); a stranger gets the stock line
                    l.LastTaunt = fly.Now;
                    var nm = StructureTag.Strip(Pf.Entities.TryGetValue(entity, out var me) && me != null ? me.Name : l.Agent);
                    var g = Grudge(l, fly.PlayerKey(pid), nm, out _);
                    fly.Say(pid, g.Length > 0 ? g : $"{nm}: Big mistake.");
                }
                log($"SIM lurker {l.Agent} shot by {pid} — goes for their ship {l.Chase}");
            }
            if (!l.HitBy.Add(pid)) return;   // the first hit from each player counts
            var who = fly.PlayerKey(pid);
            if (l.Target != 0 && Pf?.Entities != null && Pf.Entities.TryGetValue(l.Target, out var t) && t != null)
                RememberLive(l, "memory.foiled." + who, StructureTag.Strip(t.Name));   // jumped in to save the trader it was after
            else RememberLive(l, "memory.hurt." + who, Stamp());
        }

        /// <summary>How long a paid-off pirate leaves that player alone.</summary>
        const double PaidSeconds = 6 * 3600;

        void PaidOff(int entity, int pid)
        {
            var l = live.Values.FirstOrDefault(x => x.Entity == entity && x.Kind == "pirate");
            if (l == null) return;
            RememberLive(l, "memory.paid." + fly.PlayerKey(pid), Unix.ToString("0", CultureInfo.InvariantCulture));
            if (l.Target > 0)
            {   // on a trader: it takes the money and leaves the system — the trader goes free
                fly.Escort?.AttackerPaidOff(l.Target, pid);
                fly.SendAway(l.Entity); l.Released = true; l.Target = 0;
            }
            else if (l.Chase != 0)
            {   // coming for the player: back to its spot
                fly.GoTo(l.Entity, l.Dest, "its spot", 0f, 40f);
                l.Chase = 0; l.Returning = true; l.Bitter = false;
            }
            fly.AddEvent($"{(Pf?.Entities != null && Pf.Entities.TryGetValue(entity, out var e) && e != null ? StructureTag.Strip(e.Name) : l.Agent)} paid off");
            log($"SIM pirate {l.Agent} paid off by {pid}");
        }

        /// <summary>Paid this pirate within the last few hours: it lets that player pass.</summary>
        bool PaidBy(Live l, string who) =>
            l.Memory.TryGetValue("memory.paid." + who, out var v) && double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) && Unix - t < PaidSeconds;

        /// <summary>What a pirate says to a player it goes for, from what it remembers about them ("" = nothing).</summary>
        static string Grudge(Live l, string who, string name, out bool bitter)
        {
            string M(string what) => l.Memory.TryGetValue("memory." + what + "." + who, out var v) ? v : null;
            bitter = true;
            if (M("killed") is string buddy) return $"{name}: You're the one who killed {buddy}. Nowhere to run.";
            if (M("foiled") is string prey) return $"{name}: You cost us {prey}. Time to pay up.";
            bitter = false;
            if (M("hurt") != null) return $"{name}: Back for more? I still owe you for last time.";
            if (M("escaped") != null) return $"{name}: You got away last time. Not today.";
            if (M("chased") != null) return $"{name}: Thought I knew that hull.";
            return "";
        }

        /// <summary>A live sim trader remembered something: it belongs to the agent (sent to the Dedi, saved in sim.txt).</summary>
        void OnRemembered(int entity, string key, string value)
        {
            var l = live.Values.FirstOrDefault(x => x.Entity == entity);
            if (l == null || !key.StartsWith("memory.")) return;
            l.Memory[key] = value;
            fly.Send("simmem", l.Agent, key, value);
        }

        /// <summary>For EscortRun: a live sim trader's warp point (the end of its outbound lane), else null.</summary>
        public Vector3? WarpPointOf(int entity)
        {
            var l = live.Values.FirstOrDefault(x => x.Entity == entity && x.Kind == "trader");
            return l != null && (l.LegKind == "out" || l.LegKind == "fight") && l.Dest != Vector3.zero ? l.Dest : (Vector3?)null;
        }

        /// <summary>
        /// For EscortRun: does this live sim trader want an escort this leg (null = not one of ours: no opinion)? Outbound /
        /// passing only; hold value, its guns, the lane's reported risk, temperament (see <see cref="Escort.Ask"/>).
        /// </summary>
        public EscortAsk EscortAskOf(int entity)
        {
            var l = live.Values.FirstOrDefault(x => x.Entity == entity && x.Kind == "trader");
            if (l == null) return null;
            traffic.TryGetValue(l.Agent, out var t);
            var outbound = l.LegKind == "out" || (l.LegKind == "fight" && l.Bound.Length > 0);
            var value = t != null && t.Amt > 0 ? t.Amt * (Core.Sim.World.Prices.TryGetValue(t.Cargo, out var pr) ? pr : 50) : 0;
            var guns = Core.Sim.World.ShipInfo(l.Prefab)?.Strength ?? 0;
            var hull = (t?.Hull ?? 1f) * (l.Hull >= 0 ? l.Hull : 1f);
            return Escort.Ask($"{l.Agent}|{l.Bound}|{l.Station}|{(l.Passing ? "pass" : "out")}", outbound, fly.LaneRisk ?? Risk.Quiet, value, guns, Cagey(l.Agent), hull);
        }

        /// <summary>For EscortRun: what a live sim trader remembers under <paramref name="key"/>.</summary>
        public string MemoryOf(int entity, string key)
        {
            var l = live.Values.FirstOrDefault(x => x.Entity == entity);
            return l != null && l.Memory.TryGetValue(key, out var v) ? v : null;
        }

        /// <summary>
        /// "What's up?" — a sim trader says where it's headed and why, so you can follow it (an inbound one leads you to
        /// a station). "" for ships that aren't ours to speak for.
        /// </summary>
        /// <summary>What a trader says when hailed — opening with a thank-you if this player once brought it what it needed.</summary>
        public string Says(int entity, int pid)
        {
            var said = SaysPlain(entity, pid);
            var l = live.Values.FirstOrDefault(x => x.Entity == entity && x.Kind == "trader");
            if (l == null || said.Length == 0 || said.StartsWith("Can't talk", StringComparison.Ordinal) || said.StartsWith("After last time", StringComparison.Ordinal)
                || said.StartsWith("You left us", StringComparison.Ordinal)) return said;
            return l.Memory.TryGetValue("memory.helped.pid:" + pid.ToString(CultureInfo.InvariantCulture), out var gotUs)
                ? $"Good to see you again — you got us that {Core.Sim.World.Humanize(gotUs)}. {said}" : said;
        }

        string SaysPlain(int entity, int pid)
        {
            var l = live.Values.FirstOrDefault(x => x.Entity == entity && x.Kind == "trader");
            if (l == null || Pf?.Entities == null || !Pf.Entities.TryGetValue(entity, out var e) || e == null) return "";
            traffic.TryGetValue(l.Agent, out var t);
            var here = Pf.Name;
            var left = (l.Dest - e.Position).magnitude / 1000f;
            var other = t?.Other ?? "";
            if (t?.Leg == "fight" || (live.Values.Any(p => p.Kind == "pirate" && p.Target == entity)))
                return "Can't talk — pirates on us! If you've got guns, now's the time.";
            // they talk, but they don't have to tell you where they're going (no rep API for mods: memory + temperament)
            if (fly.Distrusts(entity, pid)) return "After last time? Where we're headed is none of your business.";
            if (fly.Escort != null && l.Memory.TryGetValue(fly.Escort.KeyFor(pid), out var mem) && mem == "abandoned")
                return "You left us out there last time. We'll keep our route to ourselves.";
            if (Cagey(l.Agent)) return "Minding our own business. Fly safe.";
            if (l.LegKind == "dock")
            {
                var undock = t != null ? t.T1 - Unix : -1;
                var repairing = t != null && t.Hull < 0.95f && Core.Sim.LiveRules.IsYard(l.Station);
                return (repairing ? $"Docked at {l.Station} for repairs — the yard's rebuilding us. " : $"Docked at {l.Station}, loading up. ") + (undock > 60 ? $"We undock in about {Math.Ceiling(undock / 60):0} min." : undock > 0 ? "Undocking any moment now." : "We'll be off in a while.");
            }
            if (l.LegKind == "in")
                return $"Not much. Came in from {(other.Length > 0 ? other : "the lanes")}, heading in to dock at {l.Station}, here in {here} — {left:0.0} km out. Follow along if you like.";
            if (l.Passing && (l.LegKind == "out" || l.LegKind == "fight"))
                return $"Just passing through — dropped out of warp to let the drive cool. On to {(l.Bound.Length > 0 ? l.Bound : other.Length > 0 ? other : "the next system")} once we're clear, {left:0.0} km to go.";
            if (l.LegKind == "out" || l.LegKind == "fight")
                return $"Not much. Just left {l.Station}; heading out to the warp point, {left:0.0} km to go, then on to {(l.Bound.Length > 0 ? l.Bound : other.Length > 0 ? other : "the next system")}.";
            return "Just passing through.";
        }

        /// <summary>About one crew in four keeps its route to itself (stable per ship: same answer every time you ask).</summary>
        static bool Cagey(string agent)
        {
            unchecked { uint h = 2166136261; foreach (var c in agent + "|cagey") { h ^= c; h *= 16777619; } return h % 4 == 0; }
        }

        /// <summary>
        /// Transponder scan from the cockpit: a signal named <see cref="ScanSignal"/> on the ship you fly (a switch in its
        /// control panel). Flip it on → 5 pings, one every 2 s (the /ls fly where list in chat); flip off and on to scan again.
        /// </summary>
        public const string ScanSignal = "LS_TRANSPONDER";
        /// <summary>A device named LS_HAIL on your ship: each off → on flip hails (nose target first, else the contacts list).</summary>
        public const string HailSignal = "LS_HAIL";
        public Action<int> OnHail;
        readonly Dictionary<int, bool> hailSwitchWas = new Dictionary<int, bool>();
        readonly HashSet<int> hailSeen = new HashSet<int>();
        readonly Dictionary<int, double> hailLast = new Dictionary<int, double>();
        readonly Dictionary<int, bool> scanWas = new Dictionary<int, bool>();
        readonly Dictionary<int, int> scanLeft = new Dictionary<int, int>();
        double nextScanPing;

        void ScanSwitches(double now)
        {
            if (Pf?.Players == null) return;
            foreach (var p in Pf.Players.Values.Where(x => x != null).ToList())
            {
                // the ship you fly, or the one you're walking around in (switches get flipped on foot)
                var st = ShipOf(p);
                if (st == null) continue;
                ProbeSignals(p.Id, st);
                var on = SwitchOn(st);
                var was = scanWas.TryGetValue(p.Id, out var w) && w;
                var hailOn = SwitchOn(st, HailSignal);
                var hailWas = hailSwitchWas.TryGetValue(p.Id, out var hw) && hw;
                // the control panel's "all lights" switch flips our lights too: other lights coming on in the same scan
                // (or LS_HAIL and LS_TRANSPONDER together) = a mass toggle — learn the new state, act on nothing
                var mass = OtherLightsTurnedOn(p.Id, st) >= 2 || (on && !was && hailOn && !hailWas);
                if (mass && ((on && !was) || (hailOn && !hailWas))) log($"SIM {p.Id}: all-lights toggle — LS_HAIL / LS_TRANSPONDER flip ignored");
                scanWas[p.Id] = on;
                hailSwitchWas[p.Id] = hailOn;
                if (mass) { hailSeen.Add(p.Id); continue; }
                // light ON = scanning until you switch it off (the player's call; no auto-off)
                if (on && !was) { scanLeft[p.Id] = int.MaxValue; fly.Say(p.Id, "Transponder scan on (switch LS_TRANSPONDER off to stop)."); }
                else if (!on && was && scanLeft.Remove(p.Id)) fly.Say(p.Id, "Transponder scan off.");
                // hail: active, one request per off → on flip (staying on does nothing; the player switches it off)
                if (hailOn && !hailWas && hailSeen.Contains(p.Id) && now - (hailLast.TryGetValue(p.Id, out var hl) ? hl : 0) > 3)
                { hailLast[p.Id] = now; OnHail?.Invoke(p.Id); }   // one hail per 3 s at most (a logic circuit can't spam it)
                hailSeen.Add(p.Id);   // the first reading only learns the state: a switch already on when you board doesn't hail
            }
            if (now < nextScanPing || scanLeft.Count == 0) return;
            nextScanPing = now + 5;   // continuous: one combined message every 5 s
            foreach (var kv in scanLeft.ToList())
            {
                // listening costs power: each ping takes a bit of the ship's fuel; too low → no sweep
                if (!PayForPing(kv.Key)) { scanLeft.Remove(kv.Key); continue; }
                Where(kv.Key, 5);
                if (kv.Value == int.MaxValue) continue;   // switch-driven: runs while the light is on
                if (kv.Value <= 1) { scanLeft.Remove(kv.Key); fly.Say(kv.Key, "Scan done."); } else scanLeft[kv.Key] = kv.Value - 1;
            }
        }

        /// <summary>
        /// A switch/lever block named LS_TRANSPONDER (its device name): the block's own switch state — the way the
        /// TRADE_PAD_ACTIVATE lever is read. Signal reads come back false/empty on a dedicated server; kept as a fallback.
        /// </summary>
        /// <summary>
        /// id;name;x;y;z[;bx;by;bz;hx;hy;hz;qx;qy;qz;qw] — with its bounding box (centre, half size in its own frame,
        /// rotation) when the structure is loaded: MinPos/MaxPos in blocks → world corners via StructToGlobalPos.
        /// </summary>
        static string StationEntry(IEntity e)
        {
            var head = string.Join(";", e.Id, StructureTag.Strip(e.Name ?? "").Replace(";", ",").Replace("|", "/"), F(e.Position.x), F(e.Position.y), F(e.Position.z));
            try
            {
                var st = e.Structure;
                if (st == null) return head;
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                var corners = new List<Vector3>();
                // the block grid's y is MinPos/MaxPos y + 128 (without it the boxes come out 256 m too low)
                foreach (var x in new[] { mn.x, mx.x }) foreach (var y in new[] { mn.y + GridY, mx.y + GridY }) foreach (var z in new[] { mn.z, mx.z })
                    corners.Add(st.StructToGlobalPos(new VectorInt3(x, y, z)));
                var centre = Vector3.zero; foreach (var c in corners) centre += c; centre /= corners.Count;
                var inv = Quaternion.Inverse(e.Rotation);
                var half = Vector3.zero;
                foreach (var c in corners) { var l = inv * (c - centre); half = Vector3.Max(half, new Vector3(Mathf.Abs(l.x), Mathf.Abs(l.y), Mathf.Abs(l.z))); }
                half += Vector3.one * 2f;   // corners are block centres: + a block
                if (half.magnitude < 5f || half.magnitude > 5000f) return head;   // nonsense: keep the safe 1 km
                var q = e.Rotation;
                return head + ";" + string.Join(";", F(centre.x), F(centre.y), F(centre.z), F(half.x), F(half.y), F(half.z),
                    q.x.ToString("0.#####", CultureInfo.InvariantCulture), q.y.ToString("0.#####", CultureInfo.InvariantCulture), q.z.ToString("0.#####", CultureInfo.InvariantCulture), q.w.ToString("0.#####", CultureInfo.InvariantCulture));
            }
            catch { return head; }
        }

        /// <summary>Block grid y = MinPos/MaxPos y + this.</summary>
        const int GridY = 128;

        // PROBE (2026-11-15): can the PfServer see control-panel signals? (GetSignalState / GetControlPanelSignals came back
        // empty on 2026-11-13; SignalChanged never tried.) If yes, a named signal can drive hail/scan instead of a light.
        readonly HashSet<int> signalHooked = new HashSet<int>();
        readonly Dictionary<int, string> signalLast = new Dictionary<int, string>();

        void ProbeSignals(int pid, IStructure st)
        {
            int sid; try { sid = st.Id; } catch { return; }
            if (signalHooked.Add(sid))
                try { st.SignalChanged += (name, state, x) => log($"SIGNAL PROBE event on {sid}: '{name}' = {state} ({x})"); log($"SIGNAL PROBE: hooked SignalChanged on {sid}"); }
                catch (Exception e) { log($"SIGNAL PROBE: hook on {sid} threw {e.GetType().Name}: {e.Message}"); }
            string now;
            try
            {
                var cp = st.GetControlPanelSignals();
                bool S(string n) { try { return st.GetSignalState(n); } catch { return false; } }
                now = $"panel={(cp == null ? "null" : cp.Count.ToString(CultureInfo.InvariantCulture))} TransponderHail={S("TransponderHail")} TransponderScan={S("TransponderScan")}";
            }
            catch (Exception e) { now = "threw " + e.GetType().Name; }
            if (!signalLast.TryGetValue(sid, out var was) || was != now) { signalLast[sid] = now; log($"SIGNAL PROBE {sid} (player {pid}): {now}"); }
        }

        /// <summary>Per player: on/off of up to 8 other lights on the ship they're in, last scan.</summary>
        readonly Dictionary<int, Dictionary<string, bool>> otherLights = new Dictionary<int, Dictionary<string, bool>>();
        static readonly DeviceTypeName? LightType = Enum.TryParse<DeviceTypeName>("Light", out var lt) ? lt : (DeviceTypeName?)null;

        /// <summary>How many of the ship's other lights (not ours) went off → on since the last scan.</summary>
        int OtherLightsTurnedOn(int pid, IStructure st)
        {
            if (LightType == null) return 0;
            var now = new Dictionary<string, bool>();
            try
            {
                var ours = new HashSet<string>();
                foreach (var name in new[] { ScanSignal, HailSignal })
                { var dp = st.GetDevicePositions(name); if (dp != null) foreach (var v in dp) ours.Add($"{v.x},{v.y},{v.z}"); }
                var list = st.GetDevices(LightType.Value);
                for (var i = 0; list != null && i < list.Count && now.Count < 8; i++)
                {
                    var pos = list.GetAt(i); var key = $"{pos.x},{pos.y},{pos.z}";
                    if (ours.Contains(key)) continue;
                    var b = st.GetBlock(pos); if (b == null) continue;
                    b.Get(out _, out _, out _, out var active); now[key] = active;
                }
            }
            catch { return 0; }
            var turned = 0;
            if (otherLights.TryGetValue(pid, out var before))
                foreach (var kv in now) if (kv.Value && before.TryGetValue(kv.Key, out var was) && !was) turned++;
            otherLights[pid] = now;
            return turned;
        }

        bool SwitchOn(IStructure st) => SwitchOn(st, ScanSignal);

        /// <summary>Ships (structure id + name) whose control-panel SIGNAL of that name has been seen on: from then on only
        /// the signal counts — a light of the same name is just an indicator (the panel's "all lights" switch can't fire it).</summary>
        readonly HashSet<string> signalDriven = new HashSet<string>();

        /// <summary>
        /// LS_HAIL / LS_TRANSPONDER: a control-panel signal of that name (read by polling GetSignalState — verified in game
        /// 2026-11-15; the SignalChanged event never fired), else a device block of that name: switch/lever state, or any
        /// device's on/off (a light).
        /// </summary>
        bool SwitchOn(IStructure st, string device)
        {
            string key; try { key = st.Id + "|" + device; } catch { key = device; }
            bool sig; try { sig = st.GetSignalState(device); } catch { sig = false; }
            if (sig) { if (signalDriven.Add(key)) log($"SIM {key}: driven by its control-panel signal from now on"); return true; }
            if (signalDriven.Contains(key)) return false;
            try
            {
                var dp = st.GetDevicePositions(device);
                if (dp != null && dp.Count > 0)
                {
                    var b = st.GetBlock(dp[0]);
                    if (b != null)
                    {
                        if (b.GetSwitchState() == true) return true;   // a switch / lever
                        // any device (a light is handy): its on/off
                        b.Get(out _, out _, out _, out var active);
                        if (active) return true;
                    }
                }
            }
            catch { }
            return false;
        }

        static IStructure ShipOf(IPlayer p)
        {
            try { return p.DrivingEntity?.Structure ?? p.CurrentStructure; } catch { return null; }
        }

        /// <summary>/ls fly signals: what the control panel of your ship sends (to set up LS_TRANSPONDER).</summary>
        void Signals(int pid)
        {
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var me) || me == null) return;
            var st = ShipOf(me);
            if (st == null) { fly.Say(pid, "You're not in or on a structure."); return; }
            List<SenderSignal> sigs; try { sigs = st.GetControlPanelSignals() ?? new List<SenderSignal>(); } catch (Exception e) { fly.Say(pid, "Can't read signals: " + e.Message); return; }
            fly.Say(pid, $"{sigs.Count} signal(s) on this structure:");
            foreach (var s in sigs.Take(15))
            {
                bool on; try { on = st.GetSignalState(s.Name); } catch { on = false; }
                fly.Say(pid, $"  '{s.Name}' = {(on ? "ON" : "off")}");
            }
            int devs; try { devs = st.GetDevicePositions(ScanSignal)?.Count ?? 0; } catch { devs = 0; }
            fly.Say(pid, $"{devs} block(s) named {ScanSignal}; it reads {(SwitchOn(st) ? "ON" : "off")}.");
        }

        // ---- wrecks (PLAN "wrecks & loot") ---------------------------------------------------------------------

        sealed class WreckLive
        {
            public int Entity, Cash; public string Id = "", Item = ""; public int Amt; public double Since, OutSince = -1; public bool Filled, Adopted, Prewrecked;
            /// <summary>"" = a beacon wreck (cleared when nobody comes back); "hub" = hauled to a pirate hub (kept: loot boxes,
            /// the stash goes in); "debris" = stripped hub debris (kept, empty).</summary>
            public string Mode = "";
            public bool Keep => Mode.Length > 0;
        }

        /// <summary>
        /// Holds we filled: written again ~30 s after and when a player first comes within 200 m — contents set while the
        /// hull is still arriving on clients never reach them (the server shows the goods, the player sees empty boxes).
        /// Only if the containers still hold exactly what we wrote (nobody took anything).
        /// </summary>
        sealed class Refill { public int Entity; public string Item = ""; public int Amt, Total; public double At; public bool Late, Near, Claimed; public List<KeyValuePair<string, int>> All; public List<BoxLoad> Layout; }
        readonly Dictionary<int, Refill> refills = new Dictionary<int, Refill>();

        void Refills(double now)
        {
            foreach (var r in refills.Values.ToList())
            {
                if (!Pf.Entities.TryGetValue(r.Entity, out var e) || e?.Structure == null) { if (now - r.At > 3600) refills.Remove(r.Entity); continue; }
                var lateDue = !r.Late && now - r.At > 30;
                var nearDue = !r.Near && fly.NearestPlayerDistance(e.Position) < 200f;
                // the one that matters: writes only reach the player once the hull is THEIRS (their core)
                string core; try { core = e.Structure.CoreType.ToString(); } catch { core = ""; }
                var claimDue = !r.Claimed && core != "" && core != "None" && core != "NoFaction" && core != "NPC" && core != "NPCAdmin";
                if (!lateDue && !nearDue && !claimDue) continue;
                if (lateDue) r.Late = true;
                if (nearDue) r.Near = true;
                if (claimDue) r.Claimed = true;
                if (CountItem(e.Structure, r.Item) == r.Total) { if (r.Layout != null) Rewrite(e.Structure, r.Layout); else if (r.All != null) Hold(e.Structure, r.All); else Hold(e.Structure, r.Item, r.Amt); log($"SIM hold re-sent {r.Entity}: {r.Total} {r.Item} ({(claimDue ? $"claimed, core {core}" : nearDue ? "player near" : "30 s")})"); }
                if (r.Claimed) refills.Remove(r.Entity);
            }
        }

        int CountItem(IStructure st, string item)
        {
            Dictionary<string, int> map; try { map = fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>(); } catch { return -1; }
            if (!map.TryGetValue(item ?? "", out var id)) return -1;
            var n = 0;
            try
            {
                var list = st.GetDevices(DeviceTypeName.Container);
                for (var i = 0; list != null && i < list.Count; i++) { var c = st.GetDevice<IContainer>(list.GetAt(i)); if (c != null) n += c.GetTotalItems(id); }
            }
            catch { return -1; }
            return n;
        }
        readonly Dictionary<int, WreckLive> wrecks = new Dictionary<int, WreckLive>();
        const double WreckCleanup = 300;   // nobody within the bubble this long → an unclaimed wreck is cleared

        void WreckTick(double now)
        {
            foreach (var w in wrecks.Values.ToList())
            {
                if (!Pf.Entities.TryGetValue(w.Entity, out var e) || e == null)
                {   // re-adopted ones may just be out of sight (Entities lists only what's near players): wait until seen
                    if (!w.Adopted && now - w.Since > 120)
                    {   // gone with a player right there = blown up / scrapped (never comes back); else vanished on its own
                        var seenNear = traffic.TryGetValue(w.Id, out var tw) && fly.NearestPlayerDistance(tw.A) <= Bubble;
                        wrecks.Remove(w.Entity); fly.Send("wreckgone", w.Entity, seenNear ? "destroyed" : "vanished");
                    }
                    continue;
                }
                w.Adopted = false;   // seen: from here on the normal rules
                var st = e.Structure;
                if (!w.Filled)
                {
                    if (st == null || now - w.Since < 3) continue;
                    w.Filled = true;
                    var cores = StripCore(st);
                    splashed = 0;
                    var holes = w.Prewrecked ? 0 : Batter(st, 0.06);   // Eden wreck hulls come chewed up already
                    // the hold: what's left of the cargo + the ship's own stores (a little fuel, oxygen, pentaxid, cash)
                    var all = new List<KeyValuePair<string, int>>();
                    if (w.Amt > 0 && w.Item.Length > 0) all.Add(new KeyValuePair<string, int>(w.Item, w.Amt));
                    if (w.Mode != "debris") all.AddRange(Stores());   // debris: stripped, nothing aboard
                    if (w.Cash > 0 && w.Mode != "debris") all.Add(new KeyValuePair<string, int>(Core.Sim.World.Strongbox, w.Cash));   // the dead pirate's pocket / the purse that survived
                    var tanks = w.Mode == "debris" ? "none (debris)" : Tanks(st);
                    var layout = new List<BoxLoad>();
                    var put = all.Count > 0 ? Hold(st, all, layout) : 0;
                    // only crates with loot are left — except at a hub: its wrecks' crates are where the stash goes
                    var blown = layout.Count > 0 && !w.Keep ? BlowEmpty(st, layout) : 0;
                    if (put > 0) { var main = all[0]; refills[w.Entity] = new Refill { Entity = w.Entity, Item = main.Key, Amt = main.Value, Total = CountItem(st, main.Key), At = now, All = all, Layout = layout }; }
                    log($"SIM wreck {w.Id} {w.Entity}{(w.Keep ? " [" + w.Mode + "]" : "")}: core(s) removed {cores}, blocks knocked off {holes}, damaged {lastSplashed}, hold {string.Join(", ", all.Select(x => $"{x.Value} {x.Key}"))} in {layout.Count} crate(s), {blown} empty crate(s) blown out; tanks {tanks}");
                    continue;
                }
                // claimed: a player put a core in → theirs, we let go; unclaimed and nobody around for a while → cleared
                string core; try { core = st?.CoreType.ToString() ?? "None"; } catch { core = "None"; }
                if (core != "None" && core != "NoFaction") { wrecks.Remove(w.Entity); fly.Send("wreckgone", w.Entity, "claimed"); log($"SIM wreck {w.Entity} claimed ({core})"); continue; }
                if (w.Keep) continue;   // hub wrecks stay: the sim swaps them (debris, cap) while nobody's near
                var far = fly.NearestPlayerDistance(e.Position) > Bubble;
                if (!far) { w.OutSince = -1; continue; }
                if (w.OutSince < 0) w.OutSince = now;
                // the last player left the playfield: clear it NOW, while it's still loaded (a destroy sent once it's unloaded
                // can silently fail — the hull would linger in the save); in the playfield, the 5 min grace stands
                var empty = Pf?.Players == null || Pf.Players.Count == 0;
                if (empty || now - w.OutSince > WreckCleanup) { fly.Retire(w.Entity); wrecks.Remove(w.Entity); fly.Send("wreckgone", w.Entity, "cleared"); log($"SIM wreck {w.Entity} cleared (nobody came back)"); }
            }
        }

        /// <summary>
        /// Gut the hull in one pass over its bounds (+128 y), then salvage-balance it against Eden's wreck POIs (a POI wreck
        /// has a warp drive 8 times in 102, ~1 generator, ~8 thrusters; a stripped live hull would have all of it intact):
        /// every core (a coreless derelict anyone can loot) and crew block (static NPC meshes — BlocksConfig Class NPCDialogue);
        /// the warp drive 85 % of the time; all but 1–2 generators, 1 fuel tank, 1 O2 tank; the pentaxid tank half the time;
        /// ~2/3 of the thrusters; turrets but maybe one; ~half the lights/gear/gravity/shields. Returns the cores removed.
        /// </summary>
        int StripCore(IStructure st)
        {
            Dictionary<string, int> map; try { map = fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>(); } catch { return 0; }
            bool Has(string name, params string[] parts) => parts.Any(p => name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0);
            string KindOf(string n)
            {
                if (n.StartsWith("Core", StringComparison.OrdinalIgnoreCase)) return "core";
                if (Has(n, "Crew", "NPC", "StandingHuman", "StandingTalon", "OperatorPilot", "CommandingOfficer", "SecurityGuard", "TacticalOfficer",
                        "EngineerMainStation", "PilotMale", "Dancing", "TalonChief", "ZiraxCommander")) return "crew";
                if (Has(n, "WarpDrive", "JumpDrive")) return "warp";
                if (Has(n, "FuelTank")) return "fuel tank";
                if (Has(n, "OxygenTank")) return "O2 tank";
                if (Has(n, "PentaxidTank")) return "pentaxid tank";
                if (n.StartsWith("Weapon", StringComparison.OrdinalIgnoreCase) || Has(n, "SentryGun", "Blaster", "Torpedo", "Railgun", "ModularLaser", "ModularPulseLaser", "RocketBossGun")) return "weapon";
                if (Has(n, "TurretRadar")) return null;   // a radar dish, not a gun
                if (Has(n, "Turret")) return "turret";
                if (Has(n, "Thruster", "RCS")) return "thruster";
                if (Has(n, "Generator") && !Has(n, "Gravity", "Shield", "Oxygen")) return "generator";
                if (Has(n, "LandingGear", "LandinGear", "Light", "Gravity", "ShieldGenerator", "ShieldCapacitor", "ShieldBooster")) return "gear";
                return null;
            }
            var kindOf = new Dictionary<int, string>();
            foreach (var kv in map) { var k = KindOf(kv.Key); if (k != null) kindOf[kv.Value] = k; }
            lastKindOf = kindOf;
            var found = new Dictionary<string, List<VectorInt3>>();
            try
            {
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y + GridY; y <= mx.y + GridY; y++) for (var z = mn.z; z <= mx.z; z++)
                {
                    var b = st.GetBlock(x, y, z);
                    if (b == null) continue;
                    b.Get(out var type, out _, out _, out _);
                    if (type == 0 || !kindOf.TryGetValue(type, out var kind)) continue;
                    if (!found.TryGetValue(kind, out var l)) found[kind] = l = new List<VectorInt3>();
                    l.Add(new VectorInt3(x, y, z));
                }
            }
            catch { }
            var removed = new Dictionary<string, int>(); var kept = new Dictionary<string, int>();
            foreach (var kv in found)
            {
                var list = kv.Value.OrderBy(_ => rng.Next()).ToList();
                int keep;
                switch (kv.Key)
                {
                    case "core": case "crew": case "weapon": keep = 0; break;   // fixed guns, launchers, sentries: none left on a wreck
                    case "warp": keep = list.Count(_ => rng.NextDouble() < 0.15); break;
                    case "fuel tank": case "O2 tank": keep = 1; break;
                    case "pentaxid tank": keep = rng.NextDouble() < 0.5 ? 1 : 0; break;
                    case "generator": keep = 1 + rng.Next(2); break;
                    case "turret": keep = rng.NextDouble() < 0.5 ? 1 : 0; break;
                    case "thruster": keep = list.Count(_ => rng.NextDouble() < 0.33); break;
                    default: keep = list.Count(_ => rng.NextDouble() < 0.5); break;
                }
                keep = Math.Min(keep, list.Count);
                var gone = 0;
                foreach (var pos in list.Skip(keep)) { try { st.GetBlock(pos.x, pos.y, pos.z).Set(0, null, null, null); gone++; } catch { } }
                removed[kv.Key] = gone; kept[kv.Key] = keep;
            }
            log($"SIM strip: " + string.Join(", ", found.Keys.OrderBy(k => k).Select(k => $"{k} -{removed[k]}/{found[k].Count}")));
            return removed.TryGetValue("core", out var n) ? n : 0;
        }

        /// <summary>The ship's own stores, POI-sized (Eden wreck POIs: fuel ~10 units median, little oxygen/pentaxid, cash cards
        /// 1–3k in some boxes): rolled per wreck.</summary>
        List<KeyValuePair<string, int>> Stores()
        {
            var l = new List<KeyValuePair<string, int>>();
            void Maybe(double chance, string item, int min, int max) { if (rng.NextDouble() < chance) l.Add(new KeyValuePair<string, int>(item, min + rng.Next(max - min + 1))); }
            Maybe(0.6, "EnergyCell", 2, 8);
            Maybe(0.4, "OxygenBottleLarge", 2, 6);
            Maybe(0.3, "PentaxidCrystal", 2, 10);
            Maybe(0.5, "MoneyCard", 300, 2500);
            return l;
        }

        /// <summary>The tanks that survived the strip: 0–20 % full. Returns a log line.</summary>
        string Tanks(IStructure st)
        {
            string One(string what, Func<IStructureTank> get)
            {
                try
                {
                    var t = get(); if (t == null || t.Capacity <= 0) return $"{what} none";
                    // empty, or enough to be worth pulling: a 0–20 % sliver was often under the game's minimum to take out (seen in game)
                    var before = t.Content; var want = rng.NextDouble() < 0.5 ? 0f : (float)(t.Capacity * (0.3 + rng.NextDouble() * 0.3));
                    t.AddContent(want - before);
                    return $"{what} {before:0}→{t.Content:0}/{t.Capacity:0}";
                }
                catch (Exception ex) { return $"{what} error {ex.GetType().Name}"; }
            }
            return string.Join(", ", One("fuel", () => st.FuelTank), One("O2", () => st.OxygenTank), One("pentaxid", () => st.PentaxidTank));
        }

        /// <summary>
        /// Battle damage that reads from a distance: 3–5 impact craters (spheres of blocks blown out) and one big breach,
        /// not a sprinkle of single missing blocks (that reads as a clean ship). Containers survive.
        /// </summary>
        int Batter(IStructure st, double unused)
        {
            var keep = new HashSet<string>();
            foreach (var kind in new[] { DeviceTypeName.Container, DeviceTypeName.AmmoCntr, DeviceTypeName.Fridge })
            { try { var l = st.GetDevices(kind); for (var i = 0; l != null && i < l.Count; i++) { var p = l.GetAt(i); keep.Add($"{p.x},{p.y},{p.z}"); } } catch { } }
            var n = 0;
            try
            {
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                var craters = new List<KeyValuePair<Vector3, float>>();
                var count = 3 + rng.Next(3);
                // aim each crater like a shot: from outside the box toward its centre until the line hits a real block —
                // centring on the box faces mostly blasts empty space
                var centre = new Vector3((mn.x + mx.x) / 2f, (mn.y + mx.y) / 2f + GridY, (mn.z + mx.z) / 2f);
                var reach = new Vector3(mx.x - mn.x, mx.y - mn.y, mx.z - mn.z).magnitude;
                for (var i = 0; i < count * 3 && craters.Count < count; i++)
                {
                    var dir = UnityEngine.Random.onUnitSphere;
                    var from = centre + dir * reach;
                    for (var step = 0f; step < reach; step += 1f)
                    {
                        var q = from - dir * step;
                        int bx = Mathf.RoundToInt(q.x), by = Mathf.RoundToInt(q.y), bz = Mathf.RoundToInt(q.z);
                        if (bx < mn.x || bx > mx.x || by < mn.y + GridY || by > mx.y + GridY || bz < mn.z || bz > mx.z) continue;
                        var hit = st.GetBlock(bx, by, bz);
                        if (hit == null) continue;
                        hit.Get(out var ht, out _, out _, out _);
                        if (ht == 0) continue;
                        craters.Add(new KeyValuePair<Vector3, float>(new Vector3(bx, by, bz), craters.Count == 0 ? 4.5f : 2f + (float)rng.NextDouble() * 1.5f));   // the first is the breach
                        break;
                    }
                }
                const float Splash = 3f;   // blocks this far past the crater's edge are left standing but damaged
                foreach (var cr in craters)
                {
                    var r = cr.Value; var c = cr.Key;
                    for (var x = (int)(c.x - r - Splash); x <= c.x + r + Splash; x++) for (var y = (int)(c.y - r - Splash); y <= c.y + r + Splash; y++) for (var z = (int)(c.z - r - Splash); z <= c.z + r + Splash; z++)
                    {
                        if (x < mn.x || x > mx.x || y < mn.y + GridY || y > mx.y + GridY || z < mn.z || z > mx.z) continue;
                        var d = new Vector3(x - c.x, y - c.y, z - c.z).magnitude;
                        if (d > r + Splash) continue;
                        var b = st.GetBlock(x, y, z);
                        if (b == null) continue;
                        b.Get(out var type, out _, out _, out _);
                        if (type == 0) continue;
                        var blown = d <= r && !(d > r - 1 && rng.NextDouble() < 0.5) && !keep.Contains($"{x},{y},{z}");   // ragged edge
                        if (blown) { try { b.Set(0, null, null, null); n++; } catch { } continue; }
                        // splash (every block at 10–40 % hp, not painted-on damage): the game draws the damage
                        try { var hp = b.GetHitPoints(); if (hp > 0) { b.SetDamage((int)(hp * (0.6 + rng.NextDouble() * 0.3))); splashed++; } } catch { }
                    }
                }
            }
            catch { }
            lastSplashed = splashed;
            return n;
        }
        int splashed, lastSplashed;

        /// <summary>
        /// A live ship that's hurt in the sim, made to look it: a share of its blocks (more the lower the hull) knocked down
        /// to partial hp — no block removed, so it flies as before and "blocks lost live" still measures only live damage.
        /// </summary>
        /// <summary>Repairs on screen: no block keeps more damage than a ship at <paramref name="hull"/> would show. Returns how many changed.</summary>
        int Mend(IStructure st, float hull)
        {
            var n = 0;
            try
            {
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                var allowed = Math.Max(0f, 1f - hull) * 1.2f;   // the most damage a block may show, as a share of its hp
                for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y + GridY; y <= mx.y + GridY; y++) for (var z = mn.z; z <= mx.z; z++)
                {
                    var b = st.GetBlock(x, y, z);
                    if (b == null) continue;
                    b.Get(out var type, out _, out _, out _);
                    if (type == 0) continue;
                    try
                    {
                        var dmg = b.GetDamage(); var hp = b.GetHitPoints();
                        var full = dmg + hp;
                        if (dmg <= 0 || full <= 0) continue;
                        var cap = (int)(full * allowed);
                        if (dmg > cap) { b.SetDamage(cap); n++; }
                    }
                    catch { }
                }
            }
            catch { }
            return n;
        }

        int Scuff(IStructure st, float hull)
        {
            var n = 0;
            try
            {
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                var share = Mathf.Clamp01((1f - hull) * 1.2f);   // 40 % hull → ~70 % of blocks marked
                for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y + GridY; y <= mx.y + GridY; y++) for (var z = mn.z; z <= mx.z; z++)
                {
                    if (rng.NextDouble() >= share) continue;
                    var b = st.GetBlock(x, y, z);
                    if (b == null) continue;
                    b.Get(out var type, out _, out _, out _);
                    if (type == 0) continue;
                    try { var hp = b.GetHitPoints(); if (hp > 1) { b.SetDamage((int)(hp * Math.Min(0.85, (1f - hull) * (0.6 + rng.NextDouble() * 0.6)))); n++; } } catch { }   // never a kill: engines and core survive
                }
            }
            catch { }
            return n;
        }

        /// <summary>
        /// Load <paramref name="amt"/> of an item into a structure's cargo containers with SetContent (AddItems doesn't
        /// reach players' clients), in stacks of ≤ 500, container by container. Returns how many.
        /// </summary>
        int Hold(IStructure st, string item, int amt) => Hold(st, new List<KeyValuePair<string, int>> { new KeyValuePair<string, int>(item, amt) });

        /// <summary>Several items at once (SetContent replaces a box's contents): each spread over every cargo box, biggest
        /// first, stacks ≤ 500. Returns how many items went in.</summary>
        /// <summary>Where a hold went: container position → what was written there (re-sends rewrite exactly this).</summary>
        /// <summary>Most goods stack to 500 here; MoneyCard (credits) stacks to 50k in Eden2's ItemsConfig.</summary>
        static int StackOf(string item) => item == Core.Sim.World.Strongbox ? 50000 : 500;
        sealed class BoxLoad { public VectorInt3 Pos; public List<ItemStack> Stacks = new List<ItemStack>(); }

        int Hold(IStructure st, List<KeyValuePair<string, int>> items) => Hold(st, items, null);

        int Hold(IStructure st, List<KeyValuePair<string, int>> items, List<BoxLoad> layout)
        {
            Dictionary<string, int> map; try { map = fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>(); } catch { return 0; }
            IDevicePosList list; try { list = st.GetDevices(DeviceTypeName.Container); } catch { return 0; }
            var found = new List<KeyValuePair<VectorInt3, IContainer>>();
            for (var i = 0; list != null && i < list.Count; i++) { try { var pos = list.GetAt(i); var c = st.GetDevice<IContainer>(pos); if (c != null) found.Add(new KeyValuePair<VectorInt3, IContainer>(pos, c)); } catch { } }
            found = found.OrderByDescending(kv => { try { return kv.Value.VolumeCapacity; } catch { return 0f; } }).ToList();
            var boxes = found.Select(kv => kv.Value).ToList();
            if (boxes.Count == 0) return 0;
            var content = boxes.Select(_ => new List<ItemStack>()).ToList();
            var put = 0;
            // not the same mix in every crate: the first item (the
            // cargo) goes into the 2 biggest boxes + 1 random one; every other item (ship's stores, cash) into ONE random box.
            // Overflow (a box out of slots) spills into the next box.
            var first = true;
            int seed; try { seed = st.Id; } catch { seed = 0; }
            var hr = new System.Random(seed * 7919 + items.Count);   // same crates every time for this hull: a re-send overwrites, never doubles
            foreach (var it in items)
            {
                if (string.IsNullOrEmpty(it.Key) || it.Value <= 0) continue;
                if (!map.TryGetValue(it.Key, out var id)) { log($"SIM hold: no item '{it.Key}'"); continue; }
                var targets = new List<int>();
                if (first) { for (var b = 0; b < Math.Min(2, boxes.Count); b++) targets.Add(b); if (boxes.Count > 2) targets.Add(2 + hr.Next(boxes.Count - 2)); }
                else targets.Add(hr.Next(boxes.Count));
                first = false;
                for (var b = 0; b < boxes.Count; b++) if (!targets.Contains(b)) targets.Add(b);   // spill order
                var left = it.Value; var share = (int)Math.Ceiling(it.Value / (double)Math.Min(targets.Count, it.Key == items[0].Key ? Math.Min(3, boxes.Count) : 1));
                foreach (var b in targets)
                {
                    if (left <= 0) break;
                    var mine = Math.Min(left, share);
                    while (mine > 0 && content[b].Count < 8) { var n = Math.Min(StackOf(it.Key), mine); content[b].Add(new ItemStack { id = id, count = n, slotIdx = (byte)content[b].Count }); mine -= n; left -= n; put += n; }
                }
            }
            for (var b = 0; b < boxes.Count; b++)
                if (content[b].Count > 0)
                {
                    try { boxes[b].SetContent(content[b]); layout?.Add(new BoxLoad { Pos = found[b].Key, Stacks = content[b] }); }
                    catch { put -= content[b].Sum(x => x.count); }
                }
            return put;
        }

        /// <summary>Write a hold back exactly as it was laid out (the late / player-near re-sends).</summary>
        void Rewrite(IStructure st, List<BoxLoad> layout)
        {
            foreach (var b in layout) { try { st.GetDevice<IContainer>(b.Pos)?.SetContent(b.Stacks); } catch { } }
        }

        /// <summary>
        /// Hub loot into a hub wreck's boxes, ADDED to what's there (players may have taken some): topped-up stacks first,
        /// then free slots, biggest box first. Tells the Dedi what fit ("stashed"); the rest stays queued in the sim.
        /// </summary>
        void Stash(int entity, string hub, string[] items)
        {
            if (!wrecks.TryGetValue(entity, out var w) || !w.Filled || w.Mode != "hub") return;
            if (refills.TryGetValue(entity, out var rf) && !rf.Late) return;   // the hold's own re-send first, then the stash
            if (!Pf.Entities.TryGetValue(entity, out var e) || e?.Structure == null) return;
            var st = e.Structure;
            Dictionary<string, int> map; try { map = fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>(); } catch { return; }
            IDevicePosList list; try { list = st.GetDevices(DeviceTypeName.Container); } catch { return; }
            var boxes = new List<KeyValuePair<VectorInt3, IContainer>>();
            for (var i = 0; list != null && i < list.Count; i++) { try { var pos = list.GetAt(i); var c = st.GetDevice<IContainer>(pos); if (c != null) boxes.Add(new KeyValuePair<VectorInt3, IContainer>(pos, c)); } catch { } }
            boxes = boxes.OrderByDescending(kv => { try { return kv.Value.VolumeCapacity; } catch { return 0f; } }).ToList();
            if (boxes.Count == 0) { log($"SIM stash {hub} → {entity}: no boxes left"); return; }
            var content = boxes.Select(b => { try { return b.Value.GetContent() ?? new List<ItemStack>(); } catch { return new List<ItemStack>(); } }).ToList();
            var touched = new HashSet<int>();
            var reply = new List<object> { "stashed", hub, entity };
            for (var k = 0; k + 1 < items.Length; k += 2)
            {
                if (!int.TryParse(items[k + 1], out var amt) || amt <= 0) continue;
                if (!map.TryGetValue(items[k], out var id)) { log($"SIM stash: no item '{items[k]}'"); continue; }
                var left = amt;
                var stack = StackOf(items[k]);
                for (var b = 0; b < boxes.Count && left > 0; b++)
                {
                    var c = content[b];
                    for (var j = 0; j < c.Count && left > 0; j++)
                        if (c[j].id == id && c[j].count < stack) { var n = Math.Min(left, stack - c[j].count); var s = c[j]; s.count += n; c[j] = s; left -= n; touched.Add(b); }
                    while (left > 0 && c.Count < 8)
                    {
                        byte slot = 0; while (c.Any(x => x.slotIdx == slot)) slot++;
                        var n = Math.Min(stack, left); c.Add(new ItemStack { id = id, count = n, slotIdx = slot }); left -= n; touched.Add(b);
                    }
                }
                if (amt - left > 0) { reply.Add(items[k]); reply.Add(amt - left); }
            }
            var layout = new List<BoxLoad>();
            foreach (var b in touched) { try { boxes[b].Value.SetContent(content[b]); } catch { } }
            for (var b = 0; b < boxes.Count; b++) if (content[b].Count > 0) layout.Add(new BoxLoad { Pos = boxes[b].Key, Stacks = content[b] });
            if (rf != null) { rf.Layout = layout; rf.Total = CountItem(st, rf.Item); }   // later re-sends write the hold WITH the stash
            if (reply.Count > 3) fly.Send(reply.ToArray());
            log($"SIM stash {hub} → {entity}: {string.Join(", ", reply.Skip(3))} into {touched.Count} box(es)");
        }

        /// <summary>
        /// A wreck keeps only the crates with something in them — the rest were blown out with the hold (looting a hundred
        /// empty boxes is no fun). Returns how many went.
        /// </summary>
        int BlowEmpty(IStructure st, List<BoxLoad> layout)
        {
            IDevicePosList list; try { list = st.GetDevices(DeviceTypeName.Container); } catch { return 0; }
            var keep = new HashSet<string>(layout.Select(b => $"{b.Pos.x},{b.Pos.y},{b.Pos.z}"));
            var gone = 0;
            for (var i = 0; list != null && i < list.Count; i++)
            {
                try
                {
                    var pos = list.GetAt(i);
                    if (keep.Contains($"{pos.x},{pos.y},{pos.z}")) continue;
                    st.GetBlock(pos)?.Set(0, null, null, null); gone++;
                }
                catch { }
            }
            return gone;
        }

        // ---- prefab survey: candidate hulls one after another, 300 m ahead ---------------------------------------------

        static readonly Dictionary<string, string[]> ProbeGroups = new Dictionary<string, string[]>
        {
            // hideout centrepiece candidates (the hub's "hulk")
            ["hideouts"] = new[] { "Eden_BAO_Antenna", "Eden_BAO_Antenna2", "Eden_BAJ_Antenna", "Eden_BAJ_AntennaBroken", "Eden_BAJ_RadioScope", "Eden_BAJ_SpaceCommand",
                                   "Eden_BAO_SmugOutpost1", "Eden_BAO_SmugOutpost1b", "Eden_BAO_SmugOutpost1c", "Eden_BAO_SmugOutpost2", "Eden_BAO_SmugOutpost2b", "Eden_BAO_SmugOutpost3",
                                   "Eden_BAO_SmugOutpost4", "Eden_BAO_SmugOutpost5", "Eden_BAO_RogueOutpost1", "Eden_BAO_RogueOutpost2", "Eden_BAO_RogueOutpost3", "Eden_BAO_RaidOutpost1",
                                   "Eden_BAO_SyndOutpost1", "Eden_BAO_PirateOutpost1", "Eden_BAO_PirateThief", "Eden_BAO_PirateFarm", "Eden_BAO_PirateMain1" },
            ["hulks"] = new[] { "CV_LightCarrierWreck", "CVJ_FrigateWreck", "Eden_CVJ_Wreck3", "Eden_CVJ_Wreck4", "Eden_Wrecked Horizon", "CVJ_Pack-Mule" },
            ["traders"] = new[] { "CV_Freighter_SparrowA", "CV_Freighter_SparrowB", "CV_Freighter_HarkonA", "CV_Freighter_HarkonB", "CV_Freighter_HarkonC",
                                  "CV_Freighter_HarkonD", "CV_Freighter_HorizonA", "CV_Freighter_HorizonB", "CV_Freighter_PatronA", "CV_Freighter_PatronB",
                                  "CV_Freighter_AlbatrossA", "CV_Freighter_Mule-Class-Bulk-Frgt", "CV_Freighter_Luxonberg-Ore-Hauler", "CV_Freighter_Pharos-Small-Freighter",
                                  "CV_Freighter_Stalos-MK4", "CV_Freighter_Warhound-Transport", "CV_Freighter_Atherton-Heavy-Lift", "CV_FreighterPelikanUCH",
                                  "CV_FreighterPromethiumT", "CV_PolarisFreighterP016", "CV_PolarisFreighterP017", "CVJ_Pack-Mule", "CV_Freighter-Tug-3" },
            ["pirates"] = new[] { "OPV_PirateCorvetteT1A", "OPV_PirateCorvetteT1B", "OPV_PirateCorvetteT1C", "OPV_PirateCorvetteT1D", "EPH_PirateOPVT1",
                                  "Eden_CV_PirateBarge1", "CV_SkaarRaider", "Eden_OPV_BanditT1_1", "Eden_OPV_BanditT1_2", "Eden_OPV_BanditT1_3",
                                  "Eden_OPV_BanditT1_4", "Eden_OPV_BanditT1_5", "Eden_OPV_BanditRT1_1", "Eden_OPV_BanditRT1_3" },
            ["wrecks"] = new[] { "Eden_Hauler Wreck", "CVJ_FrigateWreck", "Eden_Spathe Wreck", "CVJ_PlanetraiderWreck", "Eden_Arashi Wreck", "Eden_Blade Wreck",
                                 "Eden_Ferro Wreck", "Eden_Planken Wreck", "Eden_Refuge Wreck", "Eden_Scout Wreck", "Eden_Starduster Wreck", "Eden_Thunder Wreck",
                                 "Eden_Wrecked Horizon", "Eden_CVJ_Wreck1", "Eden_CVJ_Wreck2", "Eden_CVJ_Wreck3", "Eden_CVJ_Wreck4", "CVJ_ArgosNGWreck",
                                 "CVJ_OsanWreck", "CVJ_StardusterWreck", "CV_LightCarrierWreck", "CVJ_CerberusFrontWreck", "CVJ_CerberusBackWreck" },
        };
        sealed class Probe { public int Pid, Entity, Prev; public string Prefab = "", Group = ""; public int Index; public double Since; public bool Done; }
        Probe probe;
        string probeGroup = "traders"; int probeIndex = -1;
        float probeDist = 30f;   // on foot, 30 m (/ls fly prefab dist <m> to change — big hulls are 100 m+ long)
        Dictionary<int, string> lastKindOf, probeKinds;

        void PrefabCmd(int pid, string[] args)
        {
            var cmd = args.Length > 0 ? args[0] : "next";
            if (cmd == "list") { fly.Say(pid, string.Join("\n", ProbeGroups.Select(g => $"{g.Key} ({g.Value.Length}): {string.Join(", ", g.Value)}"))); return; }
            if (cmd == "clear") { if (probe?.Entity > 0) fly.Retire(probe.Entity); probe = null; fly.Say(pid, "Prefab probe cleared."); return; }
            if (cmd == "dist" && args.Length > 1 && float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dm)) { probeDist = Mathf.Clamp(dm, 10f, 2000f); fly.Say(pid, $"Prefab probe distance: {probeDist:0} m."); return; }
            string prefab;
            if (ProbeGroups.ContainsKey(cmd)) { probeGroup = cmd; probeIndex = 0; prefab = ProbeGroups[cmd][0]; }
            else if (cmd == "next" || cmd == "prev")
            {
                var list = ProbeGroups[probeGroup];
                probeIndex = (probeIndex + (cmd == "next" ? 1 : -1) + list.Length) % list.Length;
                prefab = list[probeIndex];
            }
            else prefab = string.Join(" ", args);   // any prefab by name
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var me) || me == null) return;
            var at = me.DrivingEntity != null ? me.DrivingEntity.Position : me.Position;
            var fwd = (me.DrivingEntity != null ? me.DrivingEntity.Rotation : me.Rotation) * Vector3.forward;
            var pos = at + fwd * probeDist;
            if (probe?.Entity > 0) fly.Retire(probe.Entity);   // one at a time
            probe = new Probe { Pid = pid, Prefab = prefab, Group = probeGroup, Index = probeIndex, Since = fly.Now };
            fly.Send("wreckspawn", prefab, F(pos.x), F(pos.y), F(pos.z), F(Yaw(-fwd)), StructureTag.Apply("Probe " + prefab));
            var n = ProbeGroups.TryGetValue(probeGroup, out var g2) && probeIndex >= 0 && g2[probeIndex] == prefab ? $" ({probeIndex + 1}/{g2.Length} {probeGroup})" : "";
            fly.Say(pid, $"Spawning {prefab}{n} {probeDist:0} m ahead…");
        }

        void ProbeStep(double now)
        {
            var p = probe;
            if (p.Done) return;
            if (p.Entity == 0) { if (now - p.Since > 30) { fly.Say(p.Pid, $"{p.Prefab}: never spawned (bad name?)."); probe = null; } return; }
            if (Pf?.Entities == null || !Pf.Entities.TryGetValue(p.Entity, out var e) || e?.Structure == null) { if (now - p.Since > 60) { fly.Say(p.Pid, $"{p.Prefab}: spawned but never loaded."); p.Done = true; } return; }
            if (now - p.Since < 4) return;   // let it settle
            p.Done = true;
            var st = e.Structure;
            var say = new List<string>();
            try
            {
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                say.Add($"{p.Prefab}: {st.BlockCount} blocks, {mx.x - mn.x + 1}×{mx.y - mn.y + 1}×{mx.z - mn.z + 1} m, {e.Type}, powered={st.IsPowered}, core={st.CoreType}, shield level {st.ShieldLevel} (active={st.IsShieldActive})");
            }
            catch (Exception ex) { say.Add($"{p.Prefab}: (stats failed: {ex.GetType().Name})"); }
            // containers
            try
            {
                var l = st.GetDevices(DeviceTypeName.Container); var cap = 0f; var n = 0;
                for (var i = 0; l != null && i < l.Count; i++) { var c = st.GetDevice<IContainer>(l.GetAt(i)); if (c != null) { n++; try { cap += c.VolumeCapacity; } catch { } } }
                say.Add($"cargo: {n} box(es), {cap / 1000f:0.#}k L");
            }
            catch { }
            // tanks
            string T(string what, Func<IStructureTank> get) { try { var t = get(); return t == null || t.Capacity <= 0 ? $"{what} none" : $"{what} {t.Content:0}/{t.Capacity:0}"; } catch { return $"{what} ?"; } }
            say.Add($"tanks: {T("fuel", () => st.FuelTank)}, {T("O2", () => st.OxygenTank)}, {T("pentaxid", () => st.PentaxidTank)}");
            // devices by kind (same classification as the wreck strip)
            try
            {
                if (probeKinds == null)
                {   // build the map once without stripping anything: a dry run over an empty structure isn't possible, so classify names here
                    Dictionary<string, int> map = fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>();
                    probeKinds = new Dictionary<int, string>();
                    foreach (var kv in map)
                    {
                        var nm = kv.Key; bool H(params string[] ps) => ps.Any(x => nm.IndexOf(x, StringComparison.OrdinalIgnoreCase) >= 0);
                        var crew = H("Crew", "NPC", "StandingHuman", "StandingTalon", "OperatorPilot", "CommandingOfficer", "SecurityGuard", "TacticalOfficer", "EngineerMainStation", "PilotMale", "Dancing") && !nm.StartsWith("Core", StringComparison.OrdinalIgnoreCase);
                        var spawner = H("Spawner");
                        var k = crew ? "crew" : spawner ? "spawner" : nm.StartsWith("Core", StringComparison.OrdinalIgnoreCase) ? "core" : H("WarpDrive", "JumpDrive") ? "warp" : H("FuelTank") ? "fuel tank" : H("OxygenTank") ? "O2 tank"
                              : H("PentaxidTank") ? "pentaxid tank" : H("TurretRadar", "Radar", "Detector") ? "radar" : H("Sentry", "Interior") && H("Turret", "Gun") ? "sentry (inside)" : H("Turret") ? "turret" : H("Thruster", "RCS") ? "thruster" : H("Generator") && !H("Gravity", "Shield", "Oxygen") ? "generator"
                              : H("ShieldGenerator", "ShieldCapacitor", "ShieldBooster") ? "shield" : H("Forcefield") ? "forcefield (door)"
                              : H("Cockpit") ? "cockpit" : H("Constructor") ? "constructor" : null;
                        if (k != null) probeKinds[kv.Value] = k;
                    }
                }
                // devices, not cells: a multi-block device fills many cells with its type — touching cells of one type = one device
                var cells = new Dictionary<long, int>();   // packed position → block type (only the kinds we care about)
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                long Key(int x, int y, int z) => ((long)(x + 2048) << 40) | ((long)(y + 2048) << 20) | (long)(z + 2048);
                for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y + GridY; y <= mx.y + GridY; y++) for (var z = mn.z; z <= mx.z; z++)
                {
                    var b = st.GetBlock(x, y, z); if (b == null) continue;
                    b.Get(out var type, out _, out _, out _);
                    if (type != 0 && probeKinds.ContainsKey(type)) cells[Key(x, y, z)] = type;
                }
                Dictionary<int, string> names; try { names = (fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key); } catch { names = new Dictionary<int, string>(); }
                var devs = new Dictionary<string, Dictionary<string, int>>();   // kind → block name → devices
                var seen = new HashSet<long>();
                foreach (var start in cells.Keys.ToList())
                {
                    if (!seen.Add(start)) continue;
                    var type = cells[start];
                    var q = new Queue<long>(); q.Enqueue(start);
                    while (q.Count > 0)
                    {
                        var c = q.Dequeue();
                        int cx = (int)(c >> 40) - 2048, cy = (int)((c >> 20) & 0xFFFFF) - 2048, cz = (int)(c & 0xFFFFF) - 2048;
                        foreach (var n in new[] { Key(cx + 1, cy, cz), Key(cx - 1, cy, cz), Key(cx, cy + 1, cz), Key(cx, cy - 1, cz), Key(cx, cy, cz + 1), Key(cx, cy, cz - 1) })
                            if (cells.TryGetValue(n, out var nt) && nt == type && seen.Add(n)) q.Enqueue(n);
                    }
                    var kind = probeKinds[type]; var nm = names.TryGetValue(type, out var bn) ? bn : type.ToString();
                    if (!devs.TryGetValue(kind, out var byName)) devs[kind] = byName = new Dictionary<string, int>();
                    byName[nm] = byName.TryGetValue(nm, out var k0) ? k0 + 1 : 1;
                }
                var counts = devs.ToDictionary(kv => kv.Key, kv => kv.Value.Values.Sum());
                if (devs.TryGetValue("turret", out var tur)) say.Add("turrets: " + string.Join(", ", tur.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value}× {kv.Key}")));
                if (devs.TryGetValue("shield", out var sh)) say.Add("shields: " + string.Join(", ", sh.Select(kv => $"{kv.Value}× {kv.Key}")));
                say.Add("devices (count): " + (counts.Count == 0 ? "none found" : string.Join(", ", counts.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key} {kv.Value}"))));
            }
            catch { }
            var msg = string.Join("\n", say);
            fly.Say(p.Pid, msg);
            log("SIM probe: " + msg.Replace("\n", " | "));
        }

        // ---- spike: what is a dead trader worth? (PLAN "wrecks & loot") ----------------------------------------------

        sealed class WreckTest { public int Pid, Entity; public string Item = "IronOre"; public int Count = 200; public double Since; public bool Done; }
        WreckTest wreckTest;

        /// <summary>/ls fly wreck [item] [count]: spawn a SparrowA 300 m ahead (via the Dedi), then strip + inspect + fill it.</summary>
        int lastWreck;

        void WreckSpike(int pid, string[] args)
        {
            if (args.Length >= 3 && args[0] == "paint" && int.TryParse(args[1], out var pe) && int.TryParse(args[2], out var pc)) { PaintTest(pid, pe, pc); return; }
            if (args.Length >= 1 && args[0] == "check") { WreckCheck(pid, args.Length >= 2 && int.TryParse(args[1], out var cid) ? cid : lastWreck, args.Length >= 3 && args[2] == "set"); return; }
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var me) || me == null) return;
            var at = me.DrivingEntity != null ? me.DrivingEntity.Position : me.Position;
            var fwd = (me.DrivingEntity != null ? me.DrivingEntity.Rotation : me.Rotation) * Vector3.forward;
            var pos = at + fwd * 300f;
            wreckTest = new WreckTest { Pid = pid, Since = fly.Now };
            if (args.Length >= 1) wreckTest.Item = args[0];
            if (args.Length >= 2 && int.TryParse(args[1], out var n)) wreckTest.Count = n;
            fly.Send("wreckspawn", "CV_Freighter_SparrowA", F(pos.x), F(pos.y), F(pos.z), F(Yaw(fwd)), StructureTag.Apply("Wreck test"));
            fly.Say(pid, $"Wreck spike: spawning a SparrowA 300 m ahead; then core off, containers listed, {wreckTest.Count} {wreckTest.Item} put in.");
        }

        void WreckStep(double now)
        {
            var w = wreckTest;
            if (w.Done || w.Entity == 0) { if (now - w.Since > 60) wreckTest = null; return; }
            if (!Pf.Entities.TryGetValue(w.Entity, out var e) || e?.Structure == null) { if (now - w.Since > 60) { fly.Say(w.Pid, "Wreck spike: the hull never loaded."); wreckTest = null; } return; }
            if (now - w.Since < 3) return;   // let it settle
            w.Done = true;
            var st = e.Structure;
            var say = new List<string>();
            Dictionary<string, int> map; try { map = fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>(); } catch { map = new Dictionary<string, int>(); }
            // 1. the core: scan the block bounds for core block types (no direct "where is the core" call)
            var coreIds = map.Where(kv => kv.Key.StartsWith("Core", StringComparison.OrdinalIgnoreCase)).Select(kv => kv.Value).ToList();
            int scanned = 0, cores = 0;
            try
            {
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y + GridY; y <= mx.y + GridY; y++) for (var z = mn.z; z <= mx.z; z++)
                {
                    scanned++;
                    var b = st.GetBlock(x, y, z);
                    if (b == null) continue;
                    b.Get(out var type, out _, out _, out _);
                    if (!coreIds.Contains(type)) continue;
                    cores++;
                    var name = map.FirstOrDefault(kv => kv.Value == type).Key;
                    // SetDamage alone doesn't kill a core (the hull keeps its faction):
                    // remove the block outright (type 0 = empty), then read back what's there
                    try { b.SetDamage(b.GetHitPoints() + 100000); } catch { }
                    try { b.Set(0, null, null, null); } catch (Exception ex) { say.Add($"core {name}: Set(0) threw {ex.Message}"); }
                    int after = -1; try { st.GetBlock(x, y, z)?.Get(out after, out _, out _, out _); } catch { }
                    say.Add($"core {name} at {x},{y},{z}: removed → block type now {after} ({(after == 0 ? "empty" : map.FirstOrDefault(kv => kv.Value == after).Key ?? "?")})");
                }
            }
            catch (Exception ex) { say.Add("core scan threw " + ex.Message); }
            say.Insert(0, $"Wreck spike '{e.Name}' {e.Id}: {S(() => st.BlockCount)} blocks, scanned {scanned} cells, {cores} core(s), core type now {S(() => st.CoreType)}");
            // 2. containers: kinds, capacity, what's in them; 3. fill the first cargo container
            var item = map.TryGetValue(w.Item, out var itemId) ? itemId : -1;
            var filled = false;
            foreach (var kind in new[] { DeviceTypeName.Container, DeviceTypeName.AmmoCntr, DeviceTypeName.Fridge, DeviceTypeName.HarvestCntr })
            {
                IDevicePosList list; try { list = st.GetDevices(kind); } catch { continue; }
                if (list == null || list.Count == 0) continue;
                say.Add($"{kind}: {list.Count}");
                for (var i = 0; i < list.Count && i < 6; i++)
                {
                    IContainer c; try { c = st.GetDevice<IContainer>(list.GetAt(i)); } catch { continue; }
                    if (c == null) continue;
                    var content = S(() => c.GetContent()?.Count.ToString());
                    say.Add($"  #{i} at {list.GetAt(i).x},{list.GetAt(i).y},{list.GetAt(i).z}: capacity {S(() => c.VolumeCapacity)}, stacks {content}");
                    if (!filled && kind == DeviceTypeName.Container && item >= 0)
                    {
                        try { var left = c.AddItems(item, w.Count); filled = true; say.Add($"  AddItems({w.Item}={item}, {w.Count}) → {left} not added; now {c.GetTotalItems(item)} in it"); }
                        catch (Exception ex) { say.Add("  AddItems threw " + ex.Message); }
                    }
                }
            }
            if (item < 0) say.Add($"no item named '{w.Item}' in the mapping");
            if (!filled && item >= 0) say.Add("no cargo container took the items");
            foreach (var line in say) { log("SIM " + line); fly.Say(w.Pid, line); }
            wreckTest = null;
        }

        /// <summary>
        /// /ls fly wreck check [id] [set]: re-read every container of the test hull NOW (did the ore stick?); "set" also
        /// tries SetContent (replace the whole content) on the cargo containers with 100 IronOre each.
        /// </summary>
        void WreckCheck(int pid, int id, bool set)
        {
            if (id == 0 || Pf?.Entities == null || !Pf.Entities.TryGetValue(id, out var e) || e?.Structure == null) { fly.Say(pid, $"wreck check: {id} isn't loaded here"); return; }
            var st = e.Structure;
            Dictionary<string, int> map; try { map = fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>(); } catch { map = new Dictionary<string, int>(); }
            string Name(int i) => map.FirstOrDefault(kv => kv.Value == i).Key ?? "#" + i;
            var ore = map.TryGetValue("IronOre", out var oid) ? oid : -1;
            var lines = new List<string> { $"wreck check '{e.Name}' {id}: core type {S(() => st.CoreType)}, faction {S(() => e.Faction.Group)}, {S(() => st.BlockCount)} blocks" };
            foreach (var kind in new[] { DeviceTypeName.Container, DeviceTypeName.AmmoCntr, DeviceTypeName.Fridge })
            {
                IDevicePosList list; try { list = st.GetDevices(kind); } catch { continue; }
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    IContainer c; try { c = st.GetDevice<IContainer>(list.GetAt(i)); } catch { continue; }
                    if (c == null) continue;
                    if (set && kind == DeviceTypeName.Container && ore >= 0)
                        try { c.SetContent(new List<ItemStack> { new ItemStack { id = ore, count = 100, slotIdx = 0 } }); } catch (Exception ex) { lines.Add($"  SetContent threw {ex.Message}"); }
                    var content = S(() => string.Join(", ", (c.GetContent() ?? new List<ItemStack>()).Select(x => $"{Name(x.id)}×{x.count}")));
                    var p = list.GetAt(i);
                    lines.Add($"  {kind} #{i} at {p.x},{p.y},{p.z}: [{content}]");
                }
            }
            foreach (var l in lines) { log("SIM " + l); fly.Say(pid, l); }
        }

        /// <summary>
        /// /ls fly wreck paint &lt;id&gt; &lt;colour index&gt;: colour every block within ~8 blocks of the wreck's emptiest spot
        /// (its breach) with one palette index — to find the index that reads as scorch.
        /// </summary>
        void PaintTest(int pid, int id, int colour)
        {
            if (Pf?.Entities == null || !Pf.Entities.TryGetValue(id, out var e) || e?.Structure == null) { fly.Say(pid, $"paint: {id} isn't loaded here"); return; }
            var st = e.Structure;
            VectorInt3 mn = st.MinPos, mx = st.MaxPos;
            // the breach: the empty cell with the most filled neighbours nearby is too slow to find — paint around the
            // box centre's nearest empty cell instead, a fair proxy for a blown-out interior
            var c = new Vector3((mn.x + mx.x) / 2f, (mn.y + mx.y) / 2f + GridY, (mn.z + mx.z) / 2f);
            var n = 0;
            for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y + GridY; y <= mx.y + GridY; y++) for (var z = mn.z; z <= mx.z; z++)
            {
                if ((new Vector3(x, y, z) - c).magnitude > 8) continue;
                var b = st.GetBlock(x, y, z);
                if (b == null) continue;
                b.Get(out var type, out _, out _, out _);
                if (type == 0) continue;
                try { b.SetColorForWholeBlock(colour); n++; } catch { }
            }
            fly.Say(pid, $"paint: {n} blocks around the middle of {id} set to colour {colour}");
        }

        static string S(Func<object> f) { try { return f()?.ToString() ?? "null"; } catch (Exception e) { return "!" + e.GetType().Name; } }

        /// <summary>Share of the fuel tank one transponder ping costs, and the reserve below which the sweep refuses.</summary>
        const float PingCost = 0.002f, PingReserve = 0.05f;
        static bool pingLogged;

        /// <summary>Take one ping's worth of fuel from the ship the player is in; false (and a message) if it's too low.</summary>
        bool PayForPing(int pid)
        {
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var p) || p == null) return false;
            var st = ShipOf(p);
            // the transponder is ship's gear: no ship, or a dead one, no sweep
            if (st == null) { fly.Say(pid, "No ship around you — no transponder. Scan off."); return false; }
            bool powered; try { powered = st.IsPowered; } catch { powered = true; }
            if (!powered) { fly.Say(pid, "Ship has no power — the transponder is dead. Scan off."); return false; }
            IStructureTank tank; try { tank = st.FuelTank; } catch { tank = null; }
            if (tank == null) return true;   // powered but no tank to read (a base): free
            float cap, before; try { cap = tank.Capacity; before = tank.Content; } catch { return true; }
            if (cap <= 0) return true;
            if (before < cap * PingReserve) { fly.Say(pid, "Not enough power for a transponder sweep (fuel below 5 %). Scan off."); return false; }
            try { tank.AddContent(-cap * PingCost); } catch (Exception e) { log("SIM ping fuel: AddContent threw " + e.Message); return true; }
            if (!pingLogged) { pingLogged = true; float after; try { after = tank.Content; } catch { after = -1; } log($"SIM ping fuel: {before:0.0} → {after:0.0} of {cap:0.0}"); }
            return true;
        }

        /// <summary>TEST: /ls fly hub — the hub's crews (home spots) and hauled wrecks here, from your nose. Not for real play.</summary>
        void HubBearing(int pid)
        {
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var me) || me == null) return;
            var ship = me.DrivingEntity;
            var at = ship != null ? ship.Position : me.Position;
            var facing = ship != null ? ship.Rotation : me.Rotation;
            var unix = Unix;
            var spots = traffic.Values.Where(t => t.Leg == "home" || t.Leg == "hubwreck" || t.Leg == "debris").Select(t => new { t, d = t.Pos(unix) - at }).OrderBy(x => x.d.magnitude).ToList();
            if (spots.Count == 0) { fly.Say(pid, "[test] no hub crews or hub wrecks in this playfield's traffic."); return; }
            foreach (var x in spots.Take(4))
                fly.Say(pid, $"[test] {(x.t.Leg == "home" ? "crew " + x.t.Name + " @ " + x.t.Other : "hub wreck " + x.t.Name)}: {x.d.magnitude / 1000f:0.0} km · {HailDisplay.Relative(facing, x.d)} (spawns within {HubRange / 1000f:0.0} km)");
        }

        /// <summary>/ls fly where: transponders, nearest first, direction from the nose of the ship the player flies.</summary>
        void Where(int pid) => Where(pid, 5);

        void Where(int pid, int max)
        {
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var me) || me == null) return;
            var ship = me.DrivingEntity;
            var at = ship != null ? ship.Position : me.Position;
            var facing = ship != null ? ship.Rotation : me.Rotation;
            // what matters first (a far wreck must not drop off a nearest-N list): wrecks, MAYDAYs, arrivals,
            // ships on the move, docked ones — nearest first within each
            int Rank(Transponder t) => t.IsWreck || t.Leg == "home" ? 0 : t.Leg == "fight" ? 1 : t.Leg == "arriving" ? 2 : t.Leg == "dock" ? 4 : 3;
            var all = Transponders().Select(t => new { t, d = t.Pos - at }).ToList();
            // compact: one short line per ship, distance + direction first; hull only
            // when hurt; destination = star + LY (no playfield code); docked ships folded into one line at the end
            var moving = all.Where(x => x.t.Leg != "dock").OrderBy(x => Rank(x.t)).ThenBy(x => x.d.magnitude).Take(max).ToList();
            var docked = all.Where(x => x.t.Leg == "dock").OrderBy(x => x.d.magnitude).ToList();
            if (moving.Count == 0 && docked.Count == 0) { fly.Say(pid, "No transponders on the band here."); return; }
            string Dest(string o) => System.Text.RegularExpressions.Regex.Replace(o ?? "", @"\s*\([^)]*\)", "");
            string Hull(Transponder t) => t.Hull < 0.95f ? $" [{t.Hull * 100:0}%]" : "";
            string Hold(Transponder t) => t.Amt > 0 ? $"{Core.Sim.World.Humanize(t.Cargo)} {t.Amt}" : "empty";
            var lines = new List<string>();
            foreach (var x in moving)
            {
                var where = $"{x.d.magnitude / 1000f:0.0} km · {HailDisplay.Relative(facing, x.d)}";
                var t = x.t;
                if (t.IsWreck) { lines.Add($"{where} — WRECK {t.Name} ({(t.Amt > 0 ? Hold(t) : "empty hold")}, {t.AgeMin:0} min)"); continue; }
                if (t.Leg == "home") { lines.Add($"{where} — [test] HUB {t.Other}: {t.Name}"); continue; }
                if (t.Leg == "arriving")
                {
                    var eta = Math.Max(0, t.EtaSec);
                    lines.Add($"{where} — ARRIVING {(int)eta / 60}:{(int)eta % 60:00} {t.Name}{Hull(t)}, {t.Role} · {Hold(t)} (from {Dest(t.Other)})");
                    continue;
                }
                var go = t.Leg == "out" ? $"{(t.Passing ? "passing → " : "→ ")}{Dest(t.Other)}" : t.Leg == "in" ? $"inbound {t.Station}" : t.Leg == "fight" ? "MAYDAY under attack" : "";
                lines.Add($"{where} — {t.Name}{Hull(t)}, {t.Role} · {Hold(t)} {go}".TrimEnd());
            }
            string Out(Transponder t) => t.EtaSec > 0 ? $", out in {Math.Max(1, Math.Ceiling(t.EtaSec / 60)):0} min" : ", leaving now";
            if (docked.Count > 0) lines.Add("Docked: " + string.Join(", ", docked.Select(x => $"{x.t.Name}{Hull(x.t)} ({x.t.Station}{Out(x.t)})")));
            if (ship == null) lines.Add("(directions from where you look)");
            var all0 = all.Count - moving.Count - docked.Count;
            if (all0 > 0) lines.Add($"(+{all0} more on the band)");
            fly.Say(pid, string.Join("\n", lines));   // one message per ping, not a chat flood
        }

        IPlayfield Pf => fly.Playfield;

        /// <summary>The system a live sim trader is bound for, or null (escort offers name it).</summary>
        public string BoundFor(int entity) { var l = live.Values.FirstOrDefault(x => x.Entity == entity); return string.IsNullOrEmpty(l?.Bound) ? null : l.Bound; }

        /// <summary>New playfield / logic reload: forget everything; the Dedi cleans up what was live here and re-sends.</summary>
        // seenStations too: PfServer processes are reused across playfields — stale stations would be reported for the next
        // playfield (phantom stations put a hub playfield on the trade network)
        public void OnLoaded(IPlayfield p) { live.Clear(); traffic.Clear(); passAnchor.Clear(); wanted.Clear(); wrecks.Clear(); refills.Clear(); seenStations.Clear(); lastStations = ""; nextStations = 0; fly.Send("simhello", p?.Name ?? ""); }

        public void Handle(string[] a)
        {
            if (a.Length >= 1 && a[0] == "clear") { live.Clear(); traffic.Clear(); log("SIM live list cleared (sim reset)"); return; }
            if (a.Length >= 1 && a[0] == "traffic") { ReadTraffic(a.Skip(1)); return; }
            if (a.Length < 2) return;
            switch (a[0])
            {
                case "trader":
                    if (a.Length >= 4)
                    {
                        SpawnTrader(a[1], a[2], a[3], a.Length >= 5 ? a[4] : "");
                        if (a.Length >= 6 && live.TryGetValue(a[1], out var nl))
                            foreach (var kv in a[5].Split(';').Select(x => x.Split(new[] { '=' }, 2)).Where(x => x.Length == 2)) nl.Memory[kv[0]] = kv[1];
                        if (a.Length >= 8 && live.TryGetValue(a[1], out var cl) && int.TryParse(a[7], out var camt) && camt > 0) { cl.FillItem = a[6]; cl.FillAmt = camt; }
                        if (a.Length >= 9 && live.TryGetValue(a[1], out var sl) && float.TryParse(a[8], NumberStyles.Float, CultureInfo.InvariantCulture, out var spd) && spd > 5) sl.Cruise = spd;
                    }
                    break;
                case "pirate":
                    if (a.Length >= 5 && int.TryParse(a[4], out var tr))
                    {
                        var at = a.Length >= 8 ? new Vector3(G(a[5]), G(a[6]), G(a[7])) : (Vector3?)null;
                        SpawnPirate(a[1], a[2], a[3], tr, at);
                        if (a.Length >= 10 && live.TryGetValue(a[1], out var pl2) && int.TryParse(a[9], out var lamt) && lamt > 0) { pl2.FillItem = a[8]; pl2.FillAmt = lamt; }   // its loot rides along
                        if (a.Length >= 11 && live.TryGetValue(a[1], out var pm))   // what it (and its hub) remembers
                            foreach (var kv in a[10].Split(';').Select(x => x.Split(new[] { '=' }, 2)).Where(x => x.Length == 2)) pm.Memory[kv[0]] = kv[1];
                    }
                    break;
                case "wreckfill":   // a wreck hull the Dedi spawned: strip its core, knock bits off, load what's left of its hold
                    if (a.Length >= 5 && int.TryParse(a[1], out var went) && int.TryParse(a[4], out var wamt))
                    { wrecks[went] = new WreckLive { Entity = went, Id = a[2], Item = a[3], Amt = wamt, Since = fly.Now, Prewrecked = a.Length >= 6 && a[5] == "1", Mode = a.Length >= 7 ? a[6] : "", Cash = a.Length >= 8 && int.TryParse(a[7], out var wcash) ? wcash : 0 }; log($"SIM wreck {a[2]} = {went}: {wamt} {a[3]}{(a.Length >= 7 && a[6].Length > 0 ? " [" + a[6] + "]" : "")}"); }
                    break;
                case "simdepart":   // a live pirate's rest / lurk is over: it flies out and jumps (the sim has it in warp already)
                    if (a.Length >= 2 && live.TryGetValue(a[1], out var dl) && dl.Kind == "pirate" && dl.Target == 0 && Pf?.Entities != null && Pf.Entities.TryGetValue(dl.Entity, out var de) && de != null)
                    {
                        var away = PlayerSpot(out var pp) ? Level(de.Position - pp) : Level(UnityEngine.Random.onUnitSphere);
                        if (away.sqrMagnitude < 0.01f) away = Vector3.forward;
                        away = Quaternion.AngleAxis((float)(rng.NextDouble() * 80 - 40), Vector3.up) * away;
                        dl.Departing = true; dl.LegKind = "out"; dl.Bound = a.Length >= 3 ? a[2] : ""; dl.Chase = 0; dl.Returning = false;
                        dl.Dest = de.Position + away * DepartMetres; dl.Steered = false;
                        SayNear(de.Position, $"{StructureTag.Strip(de.Name)} is heading out.");
                        log($"SIM pirate {dl.Agent} departs live toward {dl.Dest} (bound for {dl.Bound})");
                    }
                    break;
                case "stash":   // hub loot from the sim: "stash|entity|hub|item|amt…" → into that hub wreck's boxes, say what fit
                    if (a.Length >= 5 && int.TryParse(a[1], out var sent)) Stash(sent, a[2], a.Skip(3).ToArray());
                    break;
                case "wreckadopt":   // after a reload / playfield load: a wreck we spawned earlier — clean it up when nobody's near
                    if (a.Length >= 3 && int.TryParse(a[1], out var wad) && !wrecks.ContainsKey(wad))
                    { wrecks[wad] = new WreckLive { Entity = wad, Id = a[2], Since = fly.Now, Filled = true, Adopted = true, Mode = a.Length >= 4 ? a[3] : "" }; log($"SIM wreck {a[2]} re-adopted as {wad}"); }
                    break;
                case "wreck":    // spike: the Dedi spawned our test hull
                    if (a.Length >= 2 && int.TryParse(a[1], out var pid0) && probe != null && probe.Entity == 0) { probe.Entity = pid0; probe.Since = fly.Now; log($"SIM probe: {probe.Prefab} spawned as {pid0}"); break; }
                    if (a.Length >= 2 && int.TryParse(a[1], out var wid) && wreckTest != null) { wreckTest.Entity = wid; lastWreck = wid; wreckTest.Since = fly.Now; log($"SIM wreck spike: spawned {wid}"); }
                    break;
                case "attack":   // a live pirate: a trader just reached it
                    if (a.Length >= 3 && live.TryGetValue(a[1], out var pl) && int.TryParse(a[2], out var prey)) { pl.Target = prey; pl.Steered = false; log($"SIM pirate {a[1]} attacks {prey}"); }
                    break;
                case "adopt":   // after a reload: a live agent we already have in the playfield; keep flying it
                    if (a.Length >= 4 && int.TryParse(a[3], out var ent) && !live.ContainsKey(a[1]))
                    {
                        int.TryParse(a.Length >= 5 ? a[4] : "0", out var tgt);
                        var nl2 = new Live { Agent = a[1], Kind = a[2], Entity = ent, Target = tgt, Since = fly.Now };
                        // its spot (lurkers / hub crews); none sent → it holds where it is now, never the playfield origin
                        if (a.Length >= 8 && float.TryParse(a[5], NumberStyles.Float, CultureInfo.InvariantCulture, out var sx)
                            && float.TryParse(a[6], NumberStyles.Float, CultureInfo.InvariantCulture, out var sy) && float.TryParse(a[7], NumberStyles.Float, CultureInfo.InvariantCulture, out var sz))
                            nl2.Dest = new Vector3(sx, sy, sz);
                        else if (Pf?.Entities != null && Pf.Entities.TryGetValue(ent, out var here) && here != null) nl2.Dest = here.Position;
                        if (a.Length >= 9) foreach (var kv in a[8].Split(';').Select(x => x.Split(new[] { '=' }, 2)).Where(x => x.Length == 2)) nl2.Memory[kv[0]] = kv[1];
                        live[a[1]] = nl2;
                        log($"SIM {a[1]} ({a[2]}) re-adopted as {ent}, spot {nl2.Dest}");
                    }
                    break;
                case "paid":   // a player paid this pirate off: it breaks off and leaves them alone for a while
                    if (a.Length >= 3 && int.TryParse(a[1], out var pe2) && int.TryParse(a[2], out var payer)) PaidOff(pe2, payer);
                    break;
                case "patrolspawned":   // a station patrol we asked for is in the playfield: send it at the pirates
                    if (a.Length >= 2 && int.TryParse(a[1], out var pid2)) { patrols.Add(new PatrolShip { Entity = pid2, Since = fly.Now }); log($"SIM patrol {pid2} spawned"); }
                    break;
                case "spawned":
                    if (a.Length >= 3 && live.TryGetValue(a[1], out var l) && int.TryParse(a[2], out var id)) { l.Entity = id; l.Since = fly.Now; }
                    break;
            }
        }

        void ReadTraffic(IEnumerable<string> entries)
        {
            var inv = CultureInfo.InvariantCulture;
            float G(string v) => float.TryParse(v, NumberStyles.Float, inv, out var x) ? x : 0f;
            double D(string v) => double.TryParse(v, NumberStyles.Float, inv, out var x) ? x : 0;
            traffic.Clear();
            var seen = new HashSet<string>();
            foreach (var e in entries)
            {
                var p = e.Split(';');
                if (p.Length < 14) continue;
                // a passer (transit, sim leg "pass"): "~pass" = its line is relative — centred on a player here, picked once
                // (an empty system has no anchor); "~passhub" = absolute (past a pirate hub). Flown as an outbound lane.
                var passing = p[2] == "pass" || p[13].StartsWith("~pass", StringComparison.Ordinal);
                var offset = Vector3.zero;
                if (passing && p[13] == "~pass")
                {
                    if (!passAnchor.TryGetValue(p[0], out offset))
                    {
                        if (!fly.AnyPlayerPos(out offset)) continue;   // nobody here to pass by (several here: each passer picks one)
                        passAnchor[p[0]] = offset;
                    }
                }
                if (passing) { seen.Add(p[0]); p[13] = ""; if (p[2] == "pass") p[2] = "out"; }
                traffic[p[0]] = new Track
                {
                    Passing = passing,
                    Agent = p[0], Name = p[1], Leg = p[2], A = new Vector3(G(p[3]), G(p[4]), G(p[5])) + offset, B = new Vector3(G(p[6]), G(p[7]), G(p[8])) + offset,
                    T0 = D(p[9]), T1 = D(p[10]), Live = p[11] == "1", Other = p[12], Station = p[13], Kind = p.Length > 14 ? p[14] : "trader", Hull = p.Length > 15 ? G(p[15]) : 1,
                    Role = p.Length > 16 ? p[16] : "", Cargo = p.Length > 17 ? p[17] : "", Amt = p.Length > 18 && int.TryParse(p[18], out var am) ? am : 0,
                };
            }
            foreach (var k in passAnchor.Keys.Where(k => !seen.Contains(k)).ToList()) passAnchor.Remove(k);
        }
        /// <summary>Transit: where each relative passer's line is centred (a player's spot when it first showed up).</summary>
        readonly Dictionary<string, Vector3> passAnchor = new Dictionary<string, Vector3>();

        static float G(string v) => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var x) ? x : 0f;

        // ---- station patrols (live) --------------------------------------------------------------------------

        /// <summary>A real patrol ship, called while a pirate attack is live in a system with stations.</summary>
        sealed class PatrolShip { public int Entity; public double Since; public int Target; }
        readonly List<PatrolShip> patrols = new List<PatrolShip>();
        double nextPatrolRoll, nextPatrolSweep; bool forcePatrol;
        static readonly System.Random patrolRng = new System.Random();

        /// <summary>How well this system is protected (same rule as the sim's World.Safety): stations seen here, a trading hub.</summary>
        double Safety()
        {
            return Core.Sim.LiveRules.Safety(seenStations.Values.Select(v => v.Split(';')).Where(v => v.Length > 1).Select(v => v[1]));
        }

        /// <summary>
        /// Live pirate attack in a protected system: each minute the sim's patrol odds (0.12 × safety) can bring a real
        /// patrol — one light Polaris OPV, two heavier ones at a well-protected hub. Vanilla turrets fight; we steer it
        /// onto the pirates and send it home (gone) once they're dead or gone, or after 10 min.
        /// </summary>
        void Patrols(double now)
        {
            if (Pf?.Entities == null) return;
            // a pirate on a trader, or charging a player's ship: either is a distress call
            var attackers = live.Values.Where(l => l.Kind == "pirate" && l.Entity > 0 && (l.Target > 0 || l.Chase > 0) && Pf.Entities.ContainsKey(l.Entity)).ToList();
            if (now >= nextPatrolSweep)
            {   // after a reload / restart the list is empty but the ships are still out there: take them back by name
                nextPatrolSweep = now + 30;
                foreach (var e in Pf.Entities.Values.Where(x => x != null && (x.Type == EntityType.CV || x.Type == EntityType.SV) && StructureTag.IsTagged(x.Name)
                                                              && StructureTag.Strip(x.Name) == "Polaris Patrol" && patrols.All(p => p.Entity != x.Id)).ToList())
                { patrols.Add(new PatrolShip { Entity = e.Id, Since = now - Core.Sim.LiveRules.PatrolSeconds + 10 }); log($"SIM patrol {e.Id} re-adopted (left over from before a reload)"); }   // short leash: leaves soon unless there's a fight
            }
            foreach (var p in patrols.ToList())
            {
                if (!Pf.Entities.TryGetValue(p.Entity, out var pe) || pe == null || fly.IsDead(p.Entity))
                {   // destroyed (or never showed up)
                    if (now - p.Since > 60 || fly.IsDead(p.Entity)) { patrols.Remove(p); if (fly.IsDead(p.Entity)) Tell("Polaris patrol down!", "patrol lost"); }
                    continue;
                }
                if (Core.Sim.LiveRules.PatrolDone(attackers.Count, now - p.Since))
                {   // job done (or given up): back to its station
                    patrols.Remove(p); SayNear(pe.Position, "Polaris patrol: area clear. Returning to station."); fly.Retire(p.Entity);
                    continue;
                }
                if (p.Target == 0 || !attackers.Any(x => x.Entity == p.Target))
                {
                    var t = attackers.OrderBy(x => (Pf.Entities[x.Entity].Position - pe.Position).sqrMagnitude).First();
                    if (fly.Intercept(p.Entity, t.Entity, 400f, 90f)) { p.Target = t.Entity; log($"SIM patrol {p.Entity} → pirate {t.Entity}"); }
                }
            }
            if (attackers.Count == 0 || now < nextPatrolRoll) return;
            nextPatrolRoll = now + 60;
            if (patrols.Count > 0) return;
            var safety = Safety();
            if (!forcePatrol && (safety <= 0 || patrolRng.NextDouble() >= Core.Sim.LiveRules.PatrolChance * safety)) return;
            forcePatrol = false;
            var at = Pf.Entities[attackers[0].Entity].Position;
            var plan = Core.Sim.LiveRules.Patrol(safety);
            for (var i = 0; i < plan.Count; i++)
            {
                var dir = Level(UnityEngine.Random.onUnitSphere);
                var pos = at + dir * 1500f + Vector3.up * (i * 60f);
                fly.Send("patrolspawn", plan.Prefab, F(pos.x), F(pos.y), F(pos.z), F(Yaw(-dir)));
            }
            SayNear(at, "Polaris patrol: distress call received — we're coming in. Hold on!");
            log($"SIM patrol called ({plan.Count}× {plan.Prefab}, safety {safety:0.00})");
        }

        /// <summary>A re-adopted trader gets its leg back: what SpawnTrader would have set, from where it is now.</summary>
        void Readopt(Live l, Track t, IEntity e)
        {
            var leg = Core.Sim.LiveRules.ResumeLeg(t.Leg);
            l.LegKind = leg; l.Station = t.Station; l.Passing = t.Passing; l.Bound = t.Other; l.Steered = false; l.Slow = false;
            l.LaneA = U(Core.Sim.LiveRules.ResumeLaneStart(leg, V(t.A), V(t.B)));
            l.Dest = U(Core.Sim.LiveRules.ResumeDest(leg, V(t.A), V(t.B), V(e.Position)));
            t.Live = true;
            log($"SIM {l.Agent} re-adopted on its {leg} leg ({t.Station}) → {l.Dest}");
        }

        void SpawnTrader(string agent, string name, string prefab, string dest)
        {
            if (live.ContainsKey(agent)) return;   // a repeated request (the Dedi re-asks after a reload): it's already flying — ending it would orphan the hull
            if (!traffic.TryGetValue(agent, out var t) || (t.Leg != "out" && t.Leg != "in" && t.Leg != "fight" && t.Leg != "dock")) { End(agent, "left"); return; }
            var heading = (t.B - t.A).sqrMagnitude > 1 ? (t.B - t.A).normalized : Level(UnityEngine.Random.onUnitSphere);
            var start = t.Pos(Unix);
            // never on top of a player who parked on the lane: a bit further along
            if (t.Leg != "dock" && fly.NearestPlayerDistance(start) < 400f) start = Vector3.MoveTowards(start, t.B, 600f);
            // in a fight A is where it is, B where its leg goes on to; LaneA for "how far along" is only known on a leg
            var l = new Live { Agent = agent, Kind = "trader", Dest = t.B, Since = fly.Now, Bound = dest, LaneA = t.Leg == "fight" ? t.B : t.A, Station = t.Station, LegKind = t.Leg, Prefab = prefab ?? "", Passing = t.Passing };
            if (t.Leg == "dock") { l.Dest = start; l.LaneA = start; }   // parked at its berth slot
            live[agent] = l;
            t.Live = true;
            fly.Send("simspawn", agent, prefab, F(start.x), F(start.y), F(start.z), F(Yaw(heading)), StructureTag.Apply(name), TraderGroup(prefab));
            log($"SIM trader {agent} '{name}' live on its {t.Leg} lane ({t.Station}): {start} → {l.Dest}");
        }

        /// <summary>
        /// Traders fly under the Trader faction (18); only Polaris-branded hulls under Polaris (7) — so one faction war doesn't
        /// turn every trader hostile. Vanilla rep does the rest.
        /// </summary>
        static int TraderGroup(string prefab) => Core.Sim.LiveRules.TraderGroup(prefab);

        /// <summary>Ask the Dedi for traders whose lane position is near a player; report our stations now and then.</summary>
        void Watch(double now)
        {
            if (now >= nextStations)
            {
                nextStations = now + 60;
                // a far station can be a proxy (no structure → IsStation can't count its blocks): remember the ones we've
                // seen; the Dedi keeps a station as long as its entity is still in the playfield ("present")
                var bases = Pf.Entities.Values.Where(e => e != null && e.Type == EntityType.BA).ToList();
                // anchors are places traders would dock: not Zirax / pirate / alien bases (SNUO's are all Zirax defences)
                foreach (var e in bases.Where(b => HailDisplay.IsStation(b) && !Factions.Hostile.Contains((int)b.Faction.Group)))
                {
                    var entry = StationEntry(e);
                    // a measured box beats an unmeasured one: don't lose it when the station is out of range again
                    if (entry.Split(';').Length >= 15 || !seenStations.TryGetValue(e.Id, out var had) || had.Split(';').Length < 15) seenStations[e.Id] = entry;
                }
                var present = string.Join(",", bases.Select(e => e.Id).OrderBy(i => i));
                // never forget one because it's out of sight: far bases drop out of Entities
                var st = seenStations.OrderBy(kv => kv.Key).Select(kv => kv.Value).ToList();
                var key = string.Join("|", st) + "#" + present;
                // re-sent every 10 min even unchanged (a restarted Dedi forgets nothing — it's saved — but costs nothing)
                if (key != lastStations || now - lastStationsSent > 600)
                {
                    lastStations = key; lastStationsSent = now;
                    fly.Send(new object[] { "simstations", Pf.Name, present }.Concat(st).ToArray());
                    log("SIM station boxes: " + string.Join(", ", seenStations.Values.Select(v => v.Split(';')).Select(v => v.Length >= 15 ? $"{v[1]} half {v[8]}×{v[9]}×{v[10]} m" : $"{v[1]} (not measured)")));
                    log($"SIM stations reported: {st.Count}; bases here: " + string.Join(", ", bases.Select(b =>
                        { string n; try { n = b.Structure == null ? "proxy" : b.Structure.BlockCount + " blocks"; } catch { n = "?"; }
                          return $"{b.Id} '{b.Name}' {n} {(b.Position.magnitude / 1000f):0} km from origin"; })));
                }
            }
            var unix = Unix;
            foreach (var t in traffic.Values)
            {
                if (t.Kind == "wreck" && t.Station.StartsWith("swap:", StringComparison.Ordinal))
                {   // a hub hull that's no longer what the sim says (made debris / over the cap): down while nobody's near
                    if (!int.TryParse(t.Station.Substring(5), out var sw) || sw <= 0 || fly.NearestPlayerDistance(t.A) < HubRange + 600) continue;
                    if (wanted.TryGetValue("swap:" + t.Agent, out var sw0) && now - sw0 < 30) continue;
                    wanted["swap:" + t.Agent] = now;
                    wrecks.Remove(sw); refills.Remove(sw); fly.Retire(sw);
                    fly.Send("wreckgone", sw, "swapped");
                    log($"SIM hub wreck {t.Agent} '{t.Name}' hull {sw} taken down ({t.Leg} now)");
                    continue;
                }
                var wants = t.Kind == "wreck" ? true : t.IsTrader ? t.Leg == "out" || t.Leg == "in" || t.Leg == "fight" || t.Leg == "dock" : t.Leg == "lurk" || t.Leg == "home" || t.Leg == "fight";
                if (t.Live || live.ContainsKey(t.Agent) || !wants) continue;
                if (wanted.TryGetValue(t.Agent, out var w) && now - w < 30) continue;
                var at = t.Pos(unix);
                var dist = fly.NearestPlayerDistance(at);
                if (dist > (t.Leg == "home" || t.Leg == "hubwreck" || t.Leg == "debris" ? HubRange : SpawnRange)) continue;   // a hub is seen from further off
                if (t.Kind == "wreck")
                {   // wrecks don't count against the crowd cap (the hub caps bound them)
                    wanted[t.Agent] = now;
                    var c = ClearSpot(at, 60f); fly.Send("simwant", t.Agent, F(c.x), F(c.y), F(c.z));
                    log($"SIM {t.Agent} '{t.Name}' ({t.Kind}, {t.Leg}) near a player: asking to go live");
                    continue;
                }
                crowd.Add(new Want { T = t, Dist = dist, Rank = t.Leg == "fight" ? 0 : !t.IsTrader ? 1 : t.Leg == "dock" ? 3 : 2 });
            }
            // crowd cap: at most MaxLiveShips ships live here (fights first, then pirates, then the nearest), at most
            // MaxDockedPerStation docked traders live per station; the rest wait in the sim and come on as slots free up
            var ships = live.Values.Count(l => l.Kind == "trader" || l.Kind == "pirate");
            var docked = traffic.Values.Where(x => x.Leg == "dock" && (x.Live || live.ContainsKey(x.Agent))).GroupBy(x => x.Station).ToDictionary(g => g.Key, g => g.Count());
            var held = 0;
            foreach (var c in crowd.OrderBy(x => x.Rank).ThenBy(x => x.Dist))
            {
                var t = c.T;
                if (ships >= MaxLiveShips) { held++; continue; }
                if (t.Leg == "dock") { docked.TryGetValue(t.Station, out var d); if (d >= MaxDockedPerStation) { held++; continue; } docked[t.Station] = d + 1; }
                ships++;
                wanted[t.Agent] = now;
                fly.Send("simwant", t.Agent);
                log($"SIM {t.Agent} '{t.Name}' ({t.Kind}, {t.Leg}) near a player: asking to go live ({ships}/{MaxLiveShips})");
            }
            if (held > 0 && now - lastHeldLog > 60) { lastHeldLog = now; log($"SIM crowd cap: {ships} live, {held} held back in the sim"); }
            crowd.Clear();
        }

        /// <summary>Live crowd cap per playfield (Freelancer-style zone density): ships, and docked traders per station.</summary>
        const int MaxLiveShips = 14, MaxDockedPerStation = 4;
        sealed class Want { public Track T; public float Dist; public int Rank; }
        readonly List<Want> crowd = new List<Want>();
        double lastHeldLog;
        /// <summary>
        /// A spot near <paramref name="p"/> that no hull or asteroid we can see overlaps (bounding sphere + our radius + 20 m):
        /// pushed straight out of whatever it's in, a few rounds. Wrecks used to be placed blind (inside a Smuggler Market
        /// 500 m from its hub, in an asteroid).
        /// </summary>
        Vector3 ClearSpot(Vector3 p, float radius)
        {
            if (Pf?.Entities == null) return p;
            var others = Pf.Entities.Values.Where(e => e != null && (e.Position - p).magnitude < 2000)
                .Select(e => new { c = e.Position, r = e.Type == EntityType.AstVoxel ? 150f : e.Structure != null ? PfSpike.HullRadius(e) : 30f }).ToList();
            for (var round = 0; round < 6; round++)
            {
                var hit = others.FirstOrDefault(o => (p - o.c).magnitude < o.r + radius + 20);
                if (hit == null) break;
                var d = p - hit.c; if (d.sqrMagnitude < 1) d = Vector3.up;
                p = hit.c + d.normalized * (hit.r + radius + 25);
            }
            return p;
        }

        double lastStationsSent;
        readonly Dictionary<int, string> seenStations = new Dictionary<int, string>();

        void SpawnPirate(string agent, string name, string prefab, int traderEntity, Vector3? at)
        {
            if (live.ContainsKey(agent)) return;   // already live: a repeated request
            if (Pf?.Entities == null) { End(agent, "left"); return; }
            if (traderEntity == 0)
            {   // a lurker / hub crew a player found: sits at its spot (vanilla turrets do the talking) — or, just in from warp,
                // drops out at the system edge and flies there (popping in at its spot in front of a player looked fake)
                if (at == null) { End(agent, "left"); return; }
                var justIn = traffic.TryGetValue(agent, out var tk) && Unix - tk.T0 < ArriveWindow;
                var from = at.Value;
                if (justIn)
                {
                    var edge = Level(UnityEngine.Random.onUnitSphere); if (edge.sqrMagnitude < 0.01f) edge = Vector3.forward;
                    from = at.Value + edge * ArriveMetres;
                }
                live[agent] = new Live { Agent = agent, Kind = "pirate", Dest = at.Value, Since = fly.Now, Arriving = justIn };
                var yaw = justIn ? Yaw(at.Value - from) : (float)(rng.NextDouble() * 360);
                fly.Send("simspawn", agent, prefab, F(from.x), F(from.y), F(from.z), F(yaw), StructureTag.Apply(name), SpikeCommand.FactionGroup("pirates"));
                if (justIn) SayNear(from, $"{name} dropped out of warp.");
                log($"SIM {(justIn ? "arriving" : "lurking")} pirate {agent} '{name}' live at {from}{(justIn ? $" → its spot {at.Value}" : "")}");
                return;
            }
            if (!Pf.Entities.TryGetValue(traderEntity, out var t) || t == null) { End(agent, "left"); return; }
            // come in from the players' side of the trader, so the pirate starts inside their bubble (from the far side it
            // would be retired as "out of range" before it reached the fight)
            var dir = PlayerSpot(out var pl) ? Level(pl - t.Position) : Level(UnityEngine.Random.onUnitSphere);
            dir = Quaternion.AngleAxis((float)(rng.NextDouble() * 120 - 60), Vector3.up) * dir;
            var pos = t.Position + dir * PirateRange;
            live[agent] = new Live { Agent = agent, Kind = "pirate", Target = traderEntity, Since = fly.Now };
            fly.Send("simspawn", agent, prefab, F(pos.x), F(pos.y), F(pos.z), F(Yaw(-dir)), StructureTag.Apply(name), SpikeCommand.FactionGroup("pirates"));
            fly.AddEvent($"Pirates closing on {StructureTag.Strip(t.Name)}");
            log($"SIM pirate {agent} '{name}' live on trader {traderEntity}");
        }

        public void Tick(double now)
        {
            if (Pf?.Entities == null || now < nextTick) return;
            nextTick = now + 1;
            Watch(now);
            if (wrecks.Count > 0) WreckTick(now);
            if (refills.Count > 0) Refills(now);
            if (wreckTest != null) WreckStep(now);
            if (probe != null) ProbeStep(now);
            ScanSwitches(now);
            try { Patrols(now); } catch (Exception ex) { ShipThrew("patrols", ex); }
            try { HubPatrols(now); } catch (Exception ex) { ShipThrew("hub patrols", ex); }
            foreach (var l in live.Values.ToList())
            {
                try {   // one ship's trouble never stops the others
                if (l.Entity == 0) { if (now - l.Since > SpawnTimeout) { log($"SIM {l.Agent} never spawned"); End(l.Agent, "left"); } continue; }
                if (!Pf.Entities.TryGetValue(l.Entity, out var e) || e == null)
                {
                    if (now - l.Since > SpawnTimeout) End(l.Agent, "left");   // appeared and vanished (or never appeared)
                    continue;
                }
                if (l.Kind == "trader" && l.LegKind.Length == 0)
                {   // re-adopted after a reload: no leg yet — rebuild it from the traffic list (sent right after the adopts);
                    // until then nothing may treat it as arrived
                    if (!traffic.TryGetValue(l.Agent, out var at0)) { if (now - l.Since > 60) { log($"SIM {l.Agent} re-adopted but not on the traffic list"); End(l.Agent, "left"); } continue; }
                    Readopt(l, at0, e);
                }
                if (!l.Steered && l.Kind == "trader" && l.Dest == Vector3.zero)
                    l.Dest = e.Position + (e.Rotation * Vector3.forward) * LineLength;   // re-adopted: carry on straight ahead
                // hull lost live = blocks now / blocks at first sight — measured only near a player: out there the game
                // reports a near-empty structure
                if (fly.NearestPlayerDistance(e.Position) < 2000f)
                {
                    int b = 0; try { b = e.Structure?.BlockCount ?? 0; } catch { }
                    // a ship still loading reports -1 blocks (not a start count)
                    if (b <= 0) { }
                    else if (l.Blocks0 <= 0) l.Blocks0 = b;
                    else if (b > l.Blocks0 * 0.1f) l.Hull = Mathf.Clamp01(b / (float)l.Blocks0);
                }
                if (!l.Scuffed && e.Structure != null && now - l.Since > 3 && fly.NearestPlayerDistance(e.Position) < 2000f)
                {   // a ship the sim has hurt shows it: blocks at partial hp, matching its hull (fresh prefabs spawn pristine)
                    l.Scuffed = true;
                    var simHull = traffic.TryGetValue(l.Agent, out var ht) ? ht.Hull : 1f;
                    if (simHull < 0.95f) log($"SIM {l.Agent} shows its damage: {Scuff(e.Structure, simHull)} blocks hurt (hull {simHull:P0})");
                }
                if (l.FillAmt > 0 && e.Structure != null && now - l.Since > 3)
                {   // its cargo (or loot) in its holds, so a kill in front of you leaves something real
                    var put = Hold(e.Structure, l.FillItem, l.FillAmt);
                    log($"SIM {l.Agent} hold: {put} {l.FillItem}");
                    if (put > 0) refills[l.Entity] = new Refill { Entity = l.Entity, Item = l.FillItem, Amt = l.FillAmt, Total = put, At = now };
                    l.FillAmt = 0;
                }
                if (!l.Fueled && e.Structure != null) l.Fueled = Refuel(l, e);
                if (l.Kind == "pirate") fly.MarkCombatant(l.Entity);
                if (!l.Steered)
                {
                    // the ship registers with the flight driver a moment after it appears: keep trying
                    l.Steered = l.Kind == "trader" ? fly.GoTo(l.Entity, l.Dest, l.LegKind == "in" || l.LegKind == "dock" ? l.Station : "its warp point", 0f, l.LegKind == "dock" ? 10f : l.Slow ? FinalSpeed : l.Cruise)
                              : l.Departing ? fly.GoTo(l.Entity, l.Dest, "its warp point", 0f, 70f)
                              : l.Patrolling ? true   // its patrol order stands (HubPatrols re-gives it)
                              : l.Target == 0 ? fly.GoTo(l.Entity, l.Dest, "its spot", 0f, l.Arriving ? 60f : 30f)
                              : fly.Intercept(l.Entity, l.Target, 300f);
                    if (l.Steered && l.Kind == "trader") Announce(e);
                    continue;
                }
                if (fly.Escort != null && fly.Escort.Owns(l.Entity)) continue;   // escort contract: the run owns it now
                if (l.Kind == "trader" && Berth(l, e)) continue;
                if (l.Kind == "pirate" && l.Target == 0 && !l.Released && !l.Departing && !l.Arriving) Lurk(l, e);
                if (l.Arriving && (e.Position - l.Dest).magnitude < 150f) { l.Arriving = false; l.Steered = false; }   // at its spot: lurk / rest as usual

                if (l.Kind == "pirate" && l.Target != 0 && !l.Released && !fly.IsFleeing(l.Entity))
                {
                    var preyThere = Pf.Entities.TryGetValue(l.Target, out var tgt) && tgt != null;
                    if (!preyThere || fly.IsDead(l.Target))
                    {
                        // another trader of ours still here (same fight, most likely)? go for that one
                        var next = live.Values.FirstOrDefault(x => x.Kind == "trader" && x.Entity > 0 && !fly.IsDead(x.Entity) && Pf.Entities.ContainsKey(x.Entity));
                        if (next != null) { l.Target = next.Entity; l.Steered = false; continue; }
                        if (preyThere || fly.IsDead(l.Target)) { fly.SendAway(l.Entity); l.Released = true; }   // killed it: off it goes
                        else if (fly.NearestPlayerDistance(e.Position) <= Bubble)
                        {   // prey got away (jumped / went back to the sim) with a player watching: back to its spot, lurking
                            l.Target = 0; l.Chase = 0; l.Returning = true; l.Steered = false;
                            l.Dest = traffic.TryGetValue(l.Agent, out var pt) ? pt.A : e.Position;
                            fly.AddEvent($"{StructureTag.Strip(e.Name)} falls back");
                            log($"SIM pirate {l.Agent} lost its prey: back to its spot {l.Dest}");
                            continue;
                        }
                        else { fly.Retire(l.Entity); End(l.Agent, "left"); continue; }   // nobody watching: the sim has it
                    }
                }

                if (l.Kind == "trader" && l.LaneA != l.Dest)
                {   // how far along its lane (projected), for the sim to carry on from
                    var lane = l.Dest - l.LaneA;
                    l.Frac = Mathf.Clamp01(Vector3.Dot(e.Position - l.LaneA, lane) / Mathf.Max(1f, lane.sqrMagnitude));
                }
                // inbound ships "arrive" at their berth slot (then dock, staying real); outbound at the warp point
                if (l.Kind == "trader" && (l.LegKind == "out" || l.LegKind == "in") && l.SpoolAt < 0)
                {   // crippled (thrusters shot off, wedged on something): it gives up on the lane and ends it where it is
                    if (l.Stuck.Update(V(e.Position), now, Vector3.Distance(e.Position, l.Dest)))
                    {
                        var sn = StructureTag.Strip(e.Name);
                        SayNear(e.Position, l.LegKind == "out" ? $"{sn}: thrusters are shot — we'll jump from right here." : $"{sn}: drives are dead — we'll hold here and dock where we are.");
                        log($"SIM {l.Agent} stuck at {e.Position} on its {l.LegKind} leg — ends the leg in place");
                        l.Dest = e.Position;   // "arrived": out → spools and jumps, in → docks here
                        l.Stuck.Reset();
                    }
                }
                var arrived = (l.Kind == "trader" || l.Departing) && Core.Sim.LiveRules.Arrived(l.LegKind, Vector3.Distance(e.Position, l.Dest));
                if (arrived && l.LegKind == "in")
                {
                    l.LegKind = "dock"; l.Slow = false;
                    fly.Send("simdock", l.Agent);
                    Tell($"{StructureTag.Strip(e.Name)} docked at {l.Station}.", $"{StructureTag.Strip(e.Name)} docked");
                    continue;
                }
                // a pirate at home / lurking / on patrol is judged by its SPOT (why it spawned), not where its ship is: a patrol
                // orbit's far side or a short chase mustn't drop it out of the bubble and pop it back in; leaving ones go by position
                var anchor = l.Kind == "pirate" && l.Target == 0 && !l.Departing && l.Chase == 0 ? l.Dest : e.Position;
                var far = fly.NearestPlayerDistance(anchor) > Bubble;
                // a pirate stays while its prey is still in someone's bubble (it's on its way in)
                if (far && l.Kind == "pirate" && Pf.Entities.TryGetValue(l.Target, out var prey) && prey != null && fly.NearestPlayerDistance(prey.Position) <= Bubble) far = false;
                if (far) { if (l.OutSince < 0) l.OutSince = now; } else l.OutSince = -1;
                if (arrived && l.LegKind != "in" && l.SpoolAt < 0)
                {   // at its warp point: hold, announce, jump in a bit (mods can't play the warp effect: telegraph it)
                    l.SpoolAt = now;
                    fly.GoTo(l.Entity, e.Position, "its warp point", 0f, 5f);
                    var dest = l.Bound.Length > 0 ? l.Bound : "the next system";
                    Tell($"{StructureTag.Strip(e.Name)} is spooling its warp drive — jumping to {dest} in {Spool:0} s.", $"{StructureTag.Strip(e.Name)} spooling");
                    continue;
                }
                if (arrived && l.LegKind != "in" && now - l.SpoolAt < Spool) continue;
                if (arrived || (far && now - l.OutSince > OutGrace))
                {
                    fly.Retire(l.Entity);
                    var n = StructureTag.Strip(e.Name);
                    if (arrived && l.LegKind == "in") Tell($"{n} docked at {l.Station}.", $"{n} docked");
                    else if (arrived) Tell($"{n} jumped — follow it: /ls fly where shows it ARRIVING at the other end.", $"{n} jumped out");
                    else Tell($"{n} is out of sensor range.", $"{n} out of range");
                    End(l.Agent, l.Departing ? "left" : arrived ? "arrived" : "left", arrived ? -1 : l.Frac, l.Hull);   // a pirate is in warp already: hand it back as is
                }
                } catch (Exception ex) { ShipThrew(l.Agent, ex); }
            }
        }

        const float FinalApproach = 600f, FinalSpeed = 8f;
        /// <summary>Pirates leaving fly this far out before they spool and jump; a crew in from warp within
        /// <see cref="ArriveWindow"/> s drops out this far from its spot and flies in.</summary>
        const float DepartMetres = 5000f, ArriveMetres = 3500f;
        const double ArriveWindow = 90;
        const double Spool = 10;

        /// <summary>
        /// Berth logic for a live trader. Inbound: slow down for the last <see cref="FinalApproach"/> m and stop at its slot.
        /// Docked: parked; when the sim says it's undocked (traffic shows its leg as "out"), fly out along the lane. True =
        /// handled this tick (nothing else to do).
        /// </summary>
        bool Berth(Live l, IEntity e)
        {
            traffic.TryGetValue(l.Agent, out var t);
            if (l.LegKind == "in" && !l.Slow && Vector3.Distance(e.Position, l.Dest) < FinalApproach)
            {
                l.Slow = true;
                fly.GoTo(l.Entity, l.Dest, l.Station, 0f, FinalSpeed);   // precise, slow: don't scrape the hull
                return false;
            }
            if (l.LegKind != "dock") return false;
            if (t != null && t.Leg == "dock" && (t.A - l.Dest).magnitude > 50f)
            {   // its slot moved (the station got measured, or it docked before the slot was known): go there, slowly
                l.Dest = t.A; l.Slow = true; l.Steered = false;
                log($"SIM {l.Agent} moves to its berth slot at {l.Station} ({t.A})");
                return true;
            }
            if (t != null && t.Leg == "dock")
            {   // parked: station control says how long (a ship just sitting there looks stuck) — only to
                // players near it, every 2 min, and once "cleared" in the last minute
                var left = t.T1 - Unix;
                var now = fly.Now;
                var name = StructureTag.Strip(e.Name);
                var ctl = string.IsNullOrEmpty(l.Station) ? "Station control" : $"{l.Station} control";
                if (t.Hull < 0.95f && now >= l.NextMend && e.Structure != null)
                {   // repair crews at work: the damage fades over the stay, so it undocks looking like its new hull
                    l.NextMend = now + 15;
                    var span = Math.Max(1, t.T1 - t.T0);
                    var target = (float)Core.Sim.LiveRules.HullDuringStay(t.Hull, l.Station, (Unix - t.T0) / span);
                    var mended = Mend(e.Structure, target);
                    if (mended > 0) log($"SIM {l.Agent} repairs at {l.Station}: {mended} blocks patched (looks {target:P0})");
                }
                var say = Core.Sim.LiveRules.DockChatter(left, now, ref l.NextChat, ref l.ClearedSaid);
                if (say == "cleared") SayNear(e.Position, $"[{ctl}] {name}, you're cleared to undock. Safe flight.");
                else if (say == "loading") SayNear(e.Position, $"[{ctl}] {name}, loading — cleared to undock in {Math.Ceiling(left / 60):0} min.");
            }
            if (t != null && t.Leg == "out")
            {   // undocked by the sim: out along its lane
                l.LegKind = "out"; l.LaneA = t.A; l.Dest = t.B; l.Bound = t.Other; l.Steered = false; l.Slow = false;
                fly.AddEvent($"{StructureTag.Strip(e.Name)} undocking");
                SayNear(e.Position, $"{StructureTag.Strip(e.Name)}: undocking now — bound for {System.Text.RegularExpressions.Regex.Replace(t.Other ?? "", @"\s*\([^)]*\)", "")}.");
                l.ClearedSaid = false;
                log($"SIM {l.Agent} undocks from {l.Station} → {t.Other}");
                return true;
            }
            return false;   // parked (its GoTo holds it at the slot); the far / retire rules still apply
        }

        /// <summary>
        /// A live lurker: waits at its spot (<see cref="Live.Dest"/>); a player ship within <see cref="Aggro"/> gets charged;
        /// once the chase drags past <see cref="Leash"/> from the spot (you ran, or lured it), it flies back to its spot and
        /// only takes on someone new once it's there again.
        /// </summary>
        /// <summary>
        /// One goes for a player ship: its crewmates join — every live pirate of the same hub (the den scrambles), or for a lane
        /// lurker the ones within <see cref="PackRange"/>. They chase further than a lone one.
        /// </summary>
        void Rally(Live l, IEntity e, int ship)
        {
            var hub = traffic.TryGetValue(l.Agent, out var lt) && lt.Leg == "home" ? lt.Other : null;
            var called = new List<string>();
            foreach (var o in live.Values.ToList())
            {
                if (o == l || o.Kind != "pirate" || o.Target != 0 || o.Chase != 0 || o.Departing || o.Released || o.Entity == 0) continue;
                if (!Pf.Entities.TryGetValue(o.Entity, out var oe) || oe == null) continue;
                var mate = hub != null ? traffic.TryGetValue(o.Agent, out var ot) && ot.Leg == "home" && ot.Other == hub : (oe.Position - e.Position).magnitude <= PackRange;
                if (!mate || !fly.Intercept(o.Entity, ship, 300f)) continue;
                o.Chase = ship; o.Pack = true; o.Arriving = false; o.Returning = false;
                called.Add(StructureTag.Strip(oe.Name));
            }
            if (called.Count == 0) return;
            l.Pack = true;
            fly.AddEvent(hub != null ? $"{hub} scrambles: {called.Count + 1} ships" : $"{called.Count + 1} pirates close in");
            SayNear(e.Position, hub != null ? $"[{hub}] All hands — {StructureTag.Strip(e.Name)} spotted an intruder! ({string.Join(", ", called)} answering)"
                                           : $"{string.Join(", ", called)} join {StructureTag.Strip(e.Name)}!");
            log($"SIM rally: {l.Agent} called {called.Count} crewmate(s){(hub != null ? " of " + hub : "")} on ship {ship}");
        }

        void Lurk(Live l, IEntity e)
        {
            var name = StructureTag.Strip(e.Name);
            var fromSpot = (e.Position - l.Dest).magnitude;
            if (l.Chase != 0)
            {
                // gone — or nobody near it any more (the pilot logged off / left): a parked, empty ship isn't worth it,
                // and an offline player's property is off limits
                var gone = !Pf.Entities.TryGetValue(l.Chase, out var prey) || prey == null || fly.NearestPlayerDistance(prey.Position) > Bubble;
                var leash = Leash * (l.Bitter ? 1.6f : 1f) * (l.Pack ? PackLeash : 1f);   // a grudge / its crew drags it further from its spot
                if (gone || (prey.Position - l.Dest).magnitude > leash || fromSpot > leash)
                {
                    if (!gone) foreach (var p in Pf.Players.Values.Where(x => x?.DrivingEntity?.Id == l.Chase).ToList()) RememberLive(l, "memory.escaped." + fly.PlayerKey(p.Id), Stamp());
                    fly.GoTo(l.Entity, l.Dest, "its spot", 0f, 40f);
                    l.Chase = 0; l.Returning = true; l.Bitter = false; l.Pack = false; l.WatchSince = -1;
                    fly.AddEvent($"{name} falls back");
                    log($"SIM lurker {l.Agent} '{name}' back to its spot ({fromSpot:0} m off it)");
                }
                return;
            }
            if (l.Returning) { if (fromSpot < 300f) l.Returning = false; else return; }
            var ship = fly.NearestPlayerShip(e.Position, out var d);
            if (ship == 0) return;
            var pilots = Pf.Players.Values.Where(x => x?.DrivingEntity?.Id == ship).ToList();
            if (pilots.Count > 0 && pilots.All(p => PaidBy(l, fly.PlayerKey(p.Id)))) return;   // paid us: let them pass
            var bitter = false; var lines = new Dictionary<int, string>();
            foreach (var p in pilots) { var g = Grudge(l, fly.PlayerKey(p.Id), name, out var b); if (g.Length > 0) lines[p.Id] = g; bitter |= b; }
            // close in → at once; hanging around nearby → noticed after a while (an AFK ship at a den doesn't stay unseen)
            var now = fly.Now;
            if (d <= Notice) { if (l.WatchSince < 0) l.WatchSince = now; } else l.WatchSince = -1;
            var noticed = l.WatchSince >= 0 && now - l.WatchSince >= NoticeSeconds;
            // the patrol's long sight: only out on its orbit, and only after watching you a while
            var aggro = Aggro * (bitter ? 1.6f : 1f);
            var patrolSees = false;
            if (l.Patrolling && fromSpot >= PatrolOut && d <= aggro * PatrolSight)
            {
                if (l.SightSince < 0) l.SightSince = now;
                patrolSees = now - l.SightSince >= PatrolReact;
            }
            else l.SightSince = -1;
            if ((d > aggro && !noticed && !patrolSees) || !fly.Intercept(l.Entity, ship, 300f)) return;   // it knows you: comes from further off
            l.Chase = ship; l.Bitter = bitter;
            Rally(l, e, ship);
            fly.AddEvent($"{name} is coming for you");
            foreach (var p in pilots)
            {
                fly.Say(p.Id, lines.TryGetValue(p.Id, out var g) ? g : $"{name} breaks cover and comes for you!");
                RememberLive(l, "memory.chased." + fly.PlayerKey(p.Id), Stamp());
            }
            log($"SIM lurker {l.Agent} '{name}' goes for player ship {ship} ({d:0} m)");
        }

        /// <summary>A sim trader just appeared: tell everyone here where (chat + feed). Pirates announce themselves by shooting.</summary>
        void Announce(IEntity e)
        {
            var name = StructureTag.Strip(e.Name);
            fly.AddEvent($"{name} on scope");
            foreach (var p in Pf.Players.Values.Where(x => x != null).ToList())
            {
                var at = p.DrivingEntity != null ? p.DrivingEntity.Position : p.Position;
                var d = e.Position - at;
                var bearing = (Mathf.Atan2(d.x, d.z) * Mathf.Rad2Deg + 360f) % 360f;
                var bound = BoundFor(e.Id);
                var l = live.Values.FirstOrDefault(x => x.Entity == e.Id);
                var where = l?.LegKind == "in" ? $"inbound to {l.Station}" : l?.LegKind == "dock" ? $"docked at {l.Station}"
                          : l?.Passing == true ? $"passing through{(bound != null ? " to " + bound : "")}" : $"outbound{(bound != null ? " for " + bound : "")}";
                fly.Say(p.Id, $"Contact: {name}, freighter, {d.magnitude / 1000f:0.0} km, bearing {bearing:000} — {where}. Hail it: /ls hail");
            }
        }

        /// <summary>Chat line to everyone on this playfield + a feed entry.</summary>
        readonly Dictionary<string, double> threwAt = new Dictionary<string, double>();
        void ShipThrew(string agent, Exception ex)
        {   // logged at most once a minute per ship (a broken ship would otherwise flood the log every frame)
            if (threwAt.TryGetValue(agent, out var t) && fly.Now - t < 60) return;
            threwAt[agent] = fly.Now; log($"SIM {agent} tick threw (skipped this frame): {ex}");
        }

        /// <summary>Chat to players within 1.5 km of a point (station chatter: not the whole playfield).</summary>
        void SayNear(Vector3 at, string text)
        {
            foreach (var p in Pf.Players.Values.Where(x => x != null).ToList())
            {
                var pos = p.DrivingEntity != null ? p.DrivingEntity.Position : p.Position;
                if ((pos - at).magnitude <= 1500f) fly.Say(p.Id, text);
            }
        }

        void Tell(string chat, string feed)
        {
            fly.AddEvent(feed);
            foreach (var p in Pf.Players.Values.Where(x => x != null).ToList()) fly.Say(p.Id, chat);
        }

        void OnGone(int entity, string how)
        {
            var l = live.Values.FirstOrDefault(x => x.Entity == entity);
            if (l == null) return;
            if (how == "dead")
            {
                // the hull stays, coreless, with whatever is in its holds — a wreck (beacon + cleanup), pirates too
                if (Pf?.Entities != null && Pf.Entities.TryGetValue(entity, out var he) && he != null)
                {
                    fly.Send("wreckfromlive", l.Agent, entity, F(he.Position.x), F(he.Position.y), F(he.Position.z));
                    wrecks[entity] = new WreckLive { Entity = entity, Since = fly.Now, Filled = true };
                }
                // a pirate killed by players: its crew remembers who (kept on its hub — new recruits inherit the grudge)
                if (l.Kind == "pirate")
                    foreach (var pid in l.HitBy) fly.Send("simmem", l.Agent, "memory.killed." + fly.PlayerKey(pid), StructureTag.Strip(Pf?.Entities != null && Pf.Entities.TryGetValue(entity, out var pe) && pe != null ? pe.Name : l.Agent));
                End(l.Agent, "dead");
            }
            else if (how == "jumped")
                // escorted traders: the run's Dedi side adopts them mid-warp; a trader that fled on its own really left the
                // system — the sim sends it on (was "left": it stayed docked in the sim where it had fled from)
                End(l.Agent, l.Kind == "pirate" ? (l.Released ? "jumped" : "fled") : fly.Escort != null && fly.Escort.Owns(entity) ? "left" : "jumped");
            else if (how == "lost") { fly.Retire(entity); End(l.Agent, "left", l.Frac, l.Hull); }   // went proxy: out of range, still alive — remove the proxy, the sim has it
            else End(l.Agent, "claimed");   // a player cored it: theirs now, the sim lets go for good
        }

        void End(string agent, string how, float frac = -1, float hull = -1)
        {
            live.Remove(agent);
            if (traffic.TryGetValue(agent, out var t)) t.Live = false;
            wanted[agent] = fly.Now;   // don't ask for it again right away (the Dedi re-sends its track first)
            fly.Send("simend", agent, how, frac.ToString("0.###", CultureInfo.InvariantCulture), hull.ToString("0.###", CultureInfo.InvariantCulture));
            log($"SIM {agent} back to the sim: {how}");
        }

        bool PlayerSpot(out Vector3 at)
        {
            at = Vector3.zero;
            var p = Pf?.Players?.Values.FirstOrDefault(x => x != null);
            if (p == null) return false;
            at = p.DrivingEntity != null ? p.DrivingEntity.Position : p.Position;
            return true;
        }

        static Core.Flight.Vec3 V(Vector3 v) => new Core.Flight.Vec3(v.x, v.y, v.z);
        static Vector3 U(Core.Flight.Vec3 v) => new Vector3(v.X, v.Y, v.Z);

        static Vector3 Level(Vector3 d) { d.y *= 0.2f; return d.sqrMagnitude < 0.01f ? Vector3.right : d.normalized; }
        static float Yaw(Vector3 dir) => Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg;
        static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
    }
}
