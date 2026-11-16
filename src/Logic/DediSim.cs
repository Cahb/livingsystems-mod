using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Eleon;
using Eleon.Modding;
using LivingSystems.Core;
using LivingSystems.Core.Sim;

namespace LivingSystems
{
    /// <summary>
    /// Stage 2 (PLAN START HERE): the offline world, owned by the Dedi, stored in <c>&lt;save&gt;/Mods/LivingSystems/sim.txt</c>.
    /// Ticked by PfServer heartbeats ("simtick", every 15 s while a player is on a playfield) and by /ls sim; while
    /// nobody is online the next tick replays the gap. Server downtime pauses the world (<see cref="World.Resume"/> at load).
    ///
    /// Out: "0|fly|feed|text" (events of ships a player has met = agents with a live entity), "0|fly|risk|Quiet|Moderate|Hot"
    /// (what traders on that playfield quote), chat for fights involving met ships.
    /// Positions: PfServers report their stations (anchors); each trader runs a lane from its station (World.Track). The
    /// PfServer gets the lanes ("traffic"), and asks for a trader ("simwant") once a player is near its lane position.
    /// </summary>
    sealed class DediSim
    {
        readonly IModApi api;
        readonly Action<string> log;
        readonly string path;
        readonly World world;
        readonly Random rng = new Random();
        double lastStep;
        const double MinStep = 10;
        /// <summary>Test phase: every sim event goes to the LCD feed, and fights to chat tagged [sim] — not only ships someone has met.</summary>
        static readonly bool TestChat = false;   // true: fights of every ship you ever met go to chat, not only ones in your system

        /// <summary>Trade network playfields. Edit the "systems=" field in sim.txt to change; ';'-separated.</summary>

        readonly DediSpike dedi;
        double startedAt;
        /// <summary>Old entities of agents that are no longer live, already destroyed by this instance.</summary>
        readonly HashSet<int> staleDone = new HashSet<int>();
        readonly Dictionary<string, double> wreckSpawning = new Dictionary<string, double>();
        /// <summary>Live agents asked for but not spawned yet (agent → when asked); given up after a while.</summary>
        readonly Dictionary<string, double> asked = new Dictionary<string, double>();
        const string TraderPrefab = "CV_Freighter_SparrowA";   // fallback only (adopted live ships, tests)

        public DediSim(IModApi api, Action<string> log, DediSpike dedi)
        {
            this.api = api; this.log = log; this.dedi = dedi;
            path = Path.Combine(api.Application.GetPathFor(AppFolder.SaveGame), "Mods", "LivingSystems", "sim.txt");
            world = Load(path, out var skipped);
            // a fresh save starts EMPTY: the network grows from stations as players discover them (JoinNetwork + Populate) —
            // no hardcoded systems (playfield names differ per save)
            var now = Now;
            // live agents stay live across a logic reload / restart: their PfServer re-adopts them on simhello, and
            // Materialize sends them back to the sim if their playfield stays empty (after a grace period)
            startedAt = now;
            world.Resume(now);
            if (world.Agents.Count == 0 && world.Config.Systems.Count > 0) { world.Seed(rng, now, 5, 2); world.LastTick = now; log("sim: new world seeded"); }
            if (world.Config.Systems.Count == 0) log("sim: fresh world — no trade network yet; it grows as players find stations");
            // every agent flies its own hull; a trader's guns follow its hull
            foreach (var a in world.Agents.Values.Where(x => x.Alive)) world.ShipOf(a, true);
            // marked live but never spawned when the Dedi went down: nobody will ever spawn it now — back to the sim
            foreach (var a in world.Agents.Values.Where(x => x.IsLive && x.EntityId == 0).ToList()) world.EndLive(a.Id, "reset", Now);
            Save();
            log($"sim: {world.Agents.Count} agent(s) in {world.Config.Systems.Count} system(s){(skipped > 0 ? $", {skipped} bad line(s) skipped" : "")}");
            dedi.OnGlobalList = OnGlobalList;
            nextGlobal = 0;   // first tick asks for the structure list
            LoadGalaxy();
            SeedFromTemplates();
            // the trade network = systems with stations (old saves may list a gate or an empty sector)
            var withStations = world.Config.Systems.Where(world.Stations.ContainsKey).ToList();
            if (withStations.Count > 0 && withStations.Count != world.Config.Systems.Count) { world.Config.Systems = withStations; Save(); }
        }

        /// <summary>The galaxy map from &lt;save&gt;/Sectors/*.yaml: real distances, warp range, jump gates.</summary>
        void LoadGalaxy()
        {
            try
            {
                var dir = Path.Combine(api.Application.GetPathFor(AppFolder.SaveGame), "Sectors");
                var files = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.yaml") : new string[0];
                var g = Galaxy.Parse(files.Select(f => File.ReadAllText(f, Encoding.UTF8)));
                world.Galaxy = g.Stars.Count > 0 ? g : null;
                log($"sim: galaxy map: {g.Stars.Count} stars, {g.PlayfieldCount} playfields, {g.GatePairs.Count} gate link(s) from {files.Length} file(s)");
            }
            catch (Exception e) { log("sim: galaxy map failed (any-to-any routes): " + e.Message); }
        }

        /// <summary>
        /// Space stations the seed already placed (&lt;save&gt;/Templates/&lt;pf&gt;/playfield.yaml) join the network before anyone
        /// visits: the offline world runs from minute one. Only playfields on the galaxy map and not known yet; each template
        /// read once (new ones appear as solar systems generate — re-checked with the global list).
        /// </summary>
        void SeedFromTemplates()
        {
            // reading ~1,000 template files under Proton takes seconds: OFF the main thread (it froze the Dedi 8 s per
            // reload and dropped the player); the results are applied on the next sim tick (ApplyTemplateScan)
            if (templateScan != null && !templateScan.IsCompleted) return;
            string dir;
            try { dir = Path.Combine(api.Application.GetPathFor(AppFolder.SaveGame), "Templates"); } catch { return; }
            var known = new HashSet<string>(world.Stations.Keys, StringComparer.Ordinal);
            var done = new HashSet<string>(templatesRead, StringComparer.Ordinal);
            var galaxy = world.Galaxy;
            templateScan = System.Threading.Tasks.Task.Run(() =>
            {
                var found = new List<KeyValuePair<string, List<Core.Sim.SimStation>>>();
                var bandits = new List<KeyValuePair<string, List<Core.Sim.SimPoi>>>();
                var read = new List<string>();
                if (!Directory.Exists(dir)) return new TemplateScan { Found = found, Read = read, Bandits = bandits };
                foreach (var d in Directory.GetDirectories(dir))
                {
                    var pf = Path.GetFileName(d);
                    if (done.Contains(pf)) continue;
                    if (galaxy != null && galaxy.StarOf(pf) == null) continue;   // not on the map yet: a later scan
                    read.Add(pf);
                    var f = Path.Combine(d, "playfield.yaml");
                    if (!File.Exists(f)) continue;
                    try
                    {
                        var text = File.ReadAllText(f, Encoding.UTF8);
                        var list = known.Contains(pf) ? null : Core.Sim.TemplateStations.Parse(pf, text);
                        if (list != null && list.Count > 0) found.Add(new KeyValuePair<string, List<Core.Sim.SimStation>>(pf, list));
                        var guns = Core.Sim.TemplateStations.Bandits(text);
                        if (guns.Count > 0) bandits.Add(new KeyValuePair<string, List<Core.Sim.SimPoi>>(pf, guns));
                    }
                    catch { }
                }
                return new TemplateScan { Found = found, Read = read, Bandits = bandits };
            });
        }

        sealed class TemplateScan { public List<KeyValuePair<string, List<Core.Sim.SimStation>>> Found; public List<string> Read; public List<KeyValuePair<string, List<Core.Sim.SimPoi>>> Bandits; }
        System.Threading.Tasks.Task<TemplateScan> templateScan;

        /// <summary>A finished template scan: its stations join the network (main thread — the world isn't thread-safe).</summary>
        void ApplyTemplateScan()
        {
            if (templateScan == null || !templateScan.IsCompleted) return;
            var t = templateScan; templateScan = null;
            if (t.IsFaulted) { log("sim: template stations failed: " + t.Exception?.GetBaseException().Message); return; }
            var r = t.Result;
            foreach (var pf in r.Read) templatesRead.Add(pf);
            var added = new List<string>();
            foreach (var kv in r.Found)
            {
                if (world.Stations.ContainsKey(kv.Key)) continue;   // reported for real meanwhile
                world.SetStations(kv.Key, kv.Value);
                if (JoinNetwork(kv.Key)) added.Add($"{kv.Key} ({string.Join(", ", kv.Value.Select(x => x.Name))})");
            }
            if (added.Count > 0) { log($"sim: {added.Count} station system(s) known from the save's templates (not visited yet): {string.Join("; ", added.Take(12))}{(added.Count > 12 ? " …" : "")}"); Save(); }
            foreach (var kv in r.Bandits) world.Bandits[kv.Key] = kv.Value;
            if (r.Bandits.Count > 0) log($"sim: bandit (Warlord) POIs in {r.Bandits.Count} playfield(s), {r.Bandits.Sum(x => x.Value.Count)} guns/bases");
            // hideouts rolled before the map was read: off the bandits' turf — only where nothing of ours stands yet and nobody watches
            var pfsNow = PlayerPlayfields();
            foreach (var h in world.Hubs.Values.Where(x => x.Den == 0 && !pfsNow.Contains(x.System) && !world.Wrecks.Values.Any(w => w.Hub == x.Id && w.Entity != 0)))
                if (world.KeepOffTurf(h)) { log($"sim: hub {h.Name} moved off bandit turf in '{h.System}'"); Save(); }
        }
        readonly HashSet<string> templatesRead = new HashSet<string>(StringComparer.Ordinal);

        double nextGlobal, nextFocus;

        /// <summary>
        /// The sim moves with the players: systems within FocusLy of a player are kept populated
        /// (warm-started traders, pirates in the least protected), and over budget the agents furthest from any player
        /// are retired. Nobody online: nothing changes.
        /// </summary>
        void Focus(List<string> pfs, double now)
        {
            if (pfs.Count == 0) return;
            double ToPlayers(string sys) => world.Galaxy == null ? (pfs.Contains(sys) ? 0 : 1000) : pfs.Min(p => world.Galaxy.Distance(sys, p));
            // pirate hubs: stars within a couple of hops of a player get their one roll (permanent); hub playfields near
            // players join Focus so their crews exist (Populate keeps traffic to the station systems)
            var hubsChanged = false;
            if (world.Galaxy != null)
            {
                var near = world.Galaxy.Stars.Values.Where(st => pfs.Any(p => world.Galaxy.StarOf(p) is Core.Sim.Galaxy.Star ps && (ps.Pos - st.Pos).Length <= world.Config.HubDiscoverLy));
                var made = world.DiscoverHubs(near, now);
                if (made.Count > 0) { hubsChanged = true; log($"sim: pirate hubs found: {string.Join(", ", made.Select(h => $"{h.Name} in {h.System} ({h.Size} crews)"))}"); }
            }
            world.Focus = world.Config.Systems.Where(x => ToPlayers(x) <= world.Config.FocusLy)
                               .Concat(world.Hubs.Values.Select(h => h.System).Where(x => ToPlayers(x) <= world.Config.FocusLy)).Distinct().ToList();
            var added = world.Populate(rng, now);
            var retired = new List<Core.Sim.SimAgent>();
            var gone = world.Recycle(ToPlayers, retired);
            // only living ships retired far away: a dead pirate's hull is its wreck — players loot those
            foreach (var r in retired.Where(x => x.EntityId > 0 && x.Alive && staleDone.Add(x.EntityId))) dedi.Destroy(r.EntityId);
            if (added.Count > 0 || gone > 0 || hubsChanged)
            {
                log($"sim: focus {world.Focus.Count} system(s) near players — added {string.Join(", ", added.Select(a => $"{a.Name} ({a.Kind}, {a.State}, {a.System})"))}{(gone > 0 ? $"; retired {gone} far away" : "")}");
                Save();
            }
        }

        /// <summary>
        /// Stations from the game's global structure list (every generated playfield, near a player or not): a base of
        /// a non-hostile faction, big enough, not a satellite/beacon/gate/dummy/wreck. Authoritative per playfield (PfServer
        /// reports, which only see bases near a player, are merged sticky instead).
        /// Playfields never generated have no structures yet — they get theirs when someone first goes there.
        /// </summary>
        void OnGlobalList(GlobalStructureList gl)
        {
            if (gl?.globalStructures == null) return;
            var changed = new List<string>();
            foreach (var kv in gl.globalStructures)
            {
                var found = kv.Value.Where(g => g.type == 2 && !Factions.Hostile.Contains(g.factionGroup) && g.factionGroup != 0 && g.factionGroup != 1
                                               && g.factionGroup != HailDisplay.WreckFaction && HailDisplay.StationRule(g.name, g.cntBlocks))
                                    .Select(g => new SimStation { Id = g.id, Name = g.name ?? "", Pos = new Core.Flight.Vec3(g.pos.x, g.pos.y, g.pos.z) }).ToList();
                // authoritative for a generated playfield (it lists every structure): replaces what we had — drops a base
                // that's gone or no longer passes the rule (a crashed hulk listed once as a base)
                if (found.Count == 0 && !world.Stations.ContainsKey(kv.Key)) continue;
                if (found.Count == 0 && world.Stations.TryGetValue(kv.Key, out var only) && only.All(Core.Sim.TemplateStations.IsPlaceholder))
                {   // generated now, and none of the template's stations passed the station rule: off the network
                    world.SetStations(kv.Key, found); world.Config.Systems.Remove(kv.Key); changed.Add($"{kv.Key} (template stations not there)"); continue;
                }
                // the global list has no hull sizes: keep the boxes PfServers measured
                if (world.Stations.TryGetValue(kv.Key, out var had))
                    foreach (var st in found) { var o = had.FirstOrDefault(x => x.Id == st.Id); if (o != null && o.HasBox) { st.HasBox = true; st.Box = o.Box; st.Half = o.Half; st.Rot = o.Rot; } }
                if (world.SetStations(kv.Key, found)) changed.Add($"{kv.Key} ({found.Count})");
            }
            // a playfield the list doesn't cover can't keep a station the list places elsewhere (stale PfServer report)
            var listed = gl.globalStructures.Where(kv => kv.Value.Any()).ToDictionary(kv => kv.Key, kv => new HashSet<int>(kv.Value.Select(g => g.id)));
            foreach (var pf in world.Stations.Keys.Where(k => !gl.globalStructures.ContainsKey(k)).ToList())
            {
                var keep = world.Stations[pf].Where(s => !listed.Any(l => l.Key != pf && l.Value.Contains(s.Id))).ToList();
                if (keep.Count != world.Stations[pf].Count && world.SetStations(pf, keep)) changed.Add($"{pf} ({keep.Count}, stale dropped)");
            }
            if (OffNetwork()) changed.Add("network tidied");
            // a hub in a playfield with an Eden pirate base (Smuggler Market / Hideout / Waystation…): the crews hang around
            // THAT, not a made-up spot 6 km off — biggest pirate base wins
            foreach (var hub in world.Hubs.Values)
            {
                if (hub.Den != 0) continue;   // anchored once: a cored / destroyed den doesn't send the gang somewhere else
                if (!gl.globalStructures.TryGetValue(hub.System, out var here)) continue;
                var den = here.Where(g => g.type == 2 && g.factionGroup == 16).OrderByDescending(g => (g.name ?? "").IndexOf("Smuggler", StringComparison.OrdinalIgnoreCase) >= 0 ? 1 : 0)
                              .ThenByDescending(g => g.cntBlocks).FirstOrDefault();
                if (den.id == 0) continue;
                var at = new Core.Flight.Vec3(den.pos.x, den.pos.y, den.pos.z) + Core.Sim.World.LaneDir("hubden|" + hub.Id) * 500f;   // just off it, not inside
                hub.Den = den.id;
                if ((at - hub.Pos).Length < 50) { changed.Add($"hub {hub.Name} anchored to '{den.name}' ({den.id})"); continue; }
                // its hauled pile comes along (same offset from the hub); hulls already standing stay put — nobody watches a
                // spawned wreck vanish (a playfield's bases only show up once someone first visits it: the hub moves then)
                var moved = 0;
                foreach (var w in world.Wrecks.Values.Where(x => x.Hub == hub.Id && x.Entity == 0)) { w.Pos = at + (w.Pos - hub.Pos); moved++; }
                hub.Pos = at;
                changed.Add($"hub {hub.Name} → '{den.name}' ({den.id}){(moved > 0 ? $", {moved} hauled wreck(s) with it" : "")}");
            }
            if (changed.Count > 0) { log($"sim: stations from the global list: {string.Join(", ", changed)}"); Save(); }
            SeedFromTemplates();   // solar systems generated since: their templates
            // the galaxy is procedural: a newly generated solar system gets its own sector file — re-read the map when a
            // playfield with stations isn't on it yet (else traders could never route there until a reload)
            if (world.Galaxy != null && world.Stations.Keys.Any(pf => world.Galaxy.StarOf(pf) == null)) LoadGalaxy();
            // everything known runs: every system with stations is on the network
            var joined = world.Stations.Keys.ToList().Count(JoinNetwork);
            if (joined > 0) { log($"sim: {joined} system(s) joined the trade network — {world.Config.Systems.Count} systems, {world.Agents.Values.Count(a => a.IsTrader && a.Alive)} traders, {world.Agents.Values.Count(a => !a.IsTrader && a.Alive)} pirates"); Save(); }
            foreach (var pf in PlayerPlayfields()) SendTraffic(pf);
        }

        /// <summary>
        /// A player is in a system with stations that isn't on the trade network yet: it joins, and two traders are
        /// docked at its stations right away (forward travel finds traffic, not an empty system that fills up later).
        /// </summary>
        /// <summary>
        /// Takes stationless hub and warp-target playfields off the trade network (they only got on through stale station
        /// reports). Systems from the old hand-made list stay. Returns whether anything changed.
        /// </summary>
        bool OffNetwork()
        {
            var off = world.Config.Systems.Where(s => !world.Stations.ContainsKey(s) &&
                (world.Hubs.Values.Any(h => h.System == s) || (world.Galaxy != null && world.Galaxy.Types.TryGetValue(s, out var t) && t.IndexOf("WarpTarget", StringComparison.OrdinalIgnoreCase) >= 0))).ToList();
            foreach (var s in off) world.Config.Systems.Remove(s);
            if (off.Count > 0) log($"sim: off the trade network (no stations): {string.Join(", ", off)}");
            return off.Count > 0;
        }

        bool JoinNetwork(string pf)
        {
            if (string.IsNullOrEmpty(pf) || world.Config.Systems.Contains(pf) || !world.Stations.ContainsKey(pf)) return false;
            world.Config.Systems.Add(pf);   // population comes from Populate near players (the sim moves with them)
            return true;
        }

        static double Now => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

        public void Tick(bool force = false)
        {
            ApplyTemplateScan();
            var now = Now;
            if (!force && now - lastStep < MinStep) return;
            lastStep = now;
            if (now >= nextGlobal) { nextGlobal = now + 600; dedi.RequestGlobalList(); }
            List<SimEvent> news;
            var pfs = PlayerPlayfields();
            world.Occupied = new HashSet<string>(pfs);
            try { news = world.Step(now, rng); }
            catch (Exception e) { log("sim: step threw: " + e); return; }
            foreach (var e in news) log("sim: " + e.Text);
            Save();
            Dispatch(news, pfs);
            TellBounties();
            foreach (var pf in pfs)
                api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(0, "fly", "risk", world.RiskIn(pf, now).ToString()));
            Materialize(pfs, now);
            Departures();
            IncomingRequests(pfs, now);
            FlushStash(pfs, now);
            if (now >= nextFocus) { nextFocus = now + 60; Focus(pfs, now); }
            foreach (var pf in pfs) SendTraffic(pf);
        }

        /// <summary>
        /// Every trader with a place in <paramref name="pf"/> (station known): its track this leg, for the PfServer to
        /// spawn it when a player comes near and to show transponders. "0|fly|sim|traffic|entry|entry…", entry =
        /// id;name;leg;ax;ay;az;bx;by;bz;t0;t1;live;other;station;kind;hull (times = unix seconds). Pirates are in it too (the
        /// PfServer needs their spots to make them real when a player comes near) — the LCD never shows them.
        /// </summary>
        void SendTraffic(string pf)
        {
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            string N(double v) => v.ToString("0.#", inv);
            string Clean(string v) => (v ?? "").Replace(";", ",").Replace("|", "/");
            var entries = new List<object> { "traffic" };
            foreach (var a in world.Agents.Values.Where(x => x.Alive && x.System == pf).OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                if (!world.Track(a, out var tr)) continue;
                var leg = a.State;
                // in a fight: Other = where it was going if outbound, empty if inbound (then Station says where it docks)
                var outward = a.State == "out" || a.State == "pass" || (a.State == "fight" && (a.After == "out" || a.After == "pass"));
                // passing through: the PfServer centres a relative line on a player ("~pass"); "~passhub" = absolute, by a hub
                var passing = a.IsTrader && (a.State == "pass" || (a.State == "fight" && a.After == "pass"));
                var stationName = passing ? (world.PassRelative(a) && a.State == "pass" ? "~pass" : "~passhub") : tr.Station?.Name ?? "";
                var other = a.State == "home" ? world.HubOf(a)?.Name ?? "" : Where(pf, !a.IsTrader ? "" : outward ? a.Dest : a.State == "fight" ? "" : a.From);   // hub crews: the hub's name
                var b = a.State == "fight" ? tr.Dest : tr.B;   // a fight: A = where it is, B = where its leg goes on to
                entries.Add(string.Join(";", a.Id, Clean(a.Name), leg, N(tr.A.X), N(tr.A.Y), N(tr.A.Z), N(b.X), N(b.Y), N(b.Z),
                                        N(tr.T0), N(tr.T1), a.IsLive ? "1" : "0", Clean(other), Clean(stationName), a.Kind, a.Hull.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                                        a.IsTrader ? World.RoleOf(a).Label : "pirate", a.IsTrader ? a.Cargo : a.Loot, a.IsTrader ? a.CargoAmt : a.LootMainAmt));
            }
            // traders in warp toward here: on the band before they arrive — where they'll drop out, and when (chase them
            // through the jump: wait at their warp point)
            foreach (var a in world.Agents.Values.Where(x => x.IsTrader && x.Alive && x.State == "warp" && x.Dest == pf && !x.Extra.ContainsKey("via")))
            {
                if (!world.Arrival(a, out var wp, out var st)) continue;
                entries.Add(string.Join(";", a.Id, Clean(a.Name), "arriving", N(wp.X), N(wp.Y), N(wp.Z), N(wp.X), N(wp.Y), N(wp.Z),
                                        N(a.Until), N(a.Until), "0", Clean(Where(pf, a.System)), Clean(st.Name), a.Kind, a.Hull.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                                        World.RoleOf(a).Label, a.Cargo, a.CargoAmt));
            }
            // wrecks: distress beacons (and the ones already spawned); hub wrecks ("hubwreck" / "debris") have no beacon —
            // station field "swap:<entity>" = take that hull down while nobody's near (it respawns as what it should be)
            foreach (var w in world.Wrecks.Values.Where(x => x.System == pf))
                entries.Add(string.Join(";", w.Id, Clean(w.Name), !w.IsHub ? "wreck" : w.Debris || w.Gone ? "debris" : "hubwreck", N(w.Pos.X), N(w.Pos.Y), N(w.Pos.Z), N(w.Pos.X), N(w.Pos.Y), N(w.Pos.Z),
                                        N(w.Time), N(Math.Min(w.Until, 1e12)), w.Entity != 0 ? "1" : "0", "", w.NeedsSwap ? "swap:" + w.Entity.ToString(inv) : "", "wreck", "0",
                                        w.Kind == "pirate" ? "pirate" : Core.Sim.World.Roles.FirstOrDefault(r => r.Id == w.Role)?.Label ?? "freighter", w.Cargo, w.Amt));
            Send(pf, entries.ToArray());
        }

        /// <summary>
        /// A playfield as players understand it: "Zubekaltho (SNUO 37444 I Sector), 46 LY" — the star name, the playfield,
        /// how far from <paramref name="from"/>. Raw playfield codes alone mean nothing to players.
        /// </summary>
        string Where(string from, string pf)
        {
            if (string.IsNullOrEmpty(pf) || world.Galaxy == null) return pf ?? "";
            var star = world.Galaxy.StarOf(pf);
            if (star == null) return pf;
            // the way it actually flies (a gate shortcut beats 700 LY straight across), not the straight line
            var ly = world.Galaxy.Route(from, pf, world.Config.WarpRangeLy, world.Config.GateLy, out var gate);
            var name = pf.StartsWith(star.Name, StringComparison.Ordinal) ? pf : $"{star.Name} ({pf})";
            return double.IsInfinity(ly) || ly < 0.5 ? name + " (this system)" : $"{name}, {ly:0} LY{(gate ? " via the jump gate" : "")}";
        }

        /// <summary>When each agent last left the live game (simend): its next routine sim line would repeat the live one.</summary>
        readonly Dictionary<string, double> leftLive = new Dictionary<string, double>();

        /// <summary>Events → LCD feed + chat (fights, deaths).</summary>
        void Dispatch(List<SimEvent> news, List<string> pfs)
        {
            foreach (var e in news)
            {
                var a = world.Get(e.Agent);
                var fight = e.Kind == "attack" || e.Kind == "destroyed" || e.Kind == "limping" || e.Kind == "escaped" || e.Kind == "pirate-join";
                // with a whole galaxy running, only what happens where players are (+ ships someone has met) is news
                var met = a != null && a.EntityId != 0;
                var here = pfs.Contains(e.System);
                if (e.Kind == "attack" && !here && Now - e.Time < 120 && a != null && a.IsTrader) Mayday(e, a);   // nearby systems: someone could come
                if (e.Kind == "undock" && a != null && requestTo.TryGetValue(a.Id, out var asked))
                {   // it asked a player for something and is leaving with the want still open
                    requestTo.Remove(a.Id); var askedPid = asked.Key;
                    if (world.WantOf(a) is World.Want open && api.Application.GetPlayerEntityIds().Contains(askedPid))
                        SayTo(askedPid, a.Name, $"Never mind the {World.Humanize(open.Item)} — we'll try {Where(a.System, a.Dest)}.");
                }
                if (!met && !here) continue;
                // a gap replayed at once (nobody was online, the sim catches up on join) would stamp hours of undock / jump /
                // arrive lines with the same minute on the LCD: only fights and losses from the gap make the feed
                var fresh = Now - e.Time < 120;
                if (!fresh && !fight) continue;
                // just seen live: the PfServer already put its jump / dock on the feed ("X jumped out") — no second line
                var routine = e.Kind == "undock" || e.Kind == "jump" || e.Kind == "arrive" || e.Kind == "pass";
                if (routine && a != null && leftLive.TryGetValue(a.Id, out var gone) && Now - gone < 180) continue;
                foreach (var pf in met ? pfs : new List<string> { e.System })
                    api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(0, "fly", "feed", world.FeedLine(e)));
                // a trader entering a system with players broadcasts (private chat to everyone there) — no position:
                // to find it, scan (LS_TRANSPONDER). Pirates don't announce themselves.
                // chat only for what's happening now: a gap replayed after nobody was online (hours of events at once)
                // goes to the LCD feed (fights only, above), not as a wall of MAYDAYs and broadcasts
                if (fresh && here && a != null && a.IsTrader && (e.Kind == "pass" || e.Kind == "arrive")) Broadcast(e.System, a);
                if (fresh && fight && e.Kind != "pirate-join" && (here || TestChat)) SayAll(e.Kind == "attack" ? "MAYDAY — " + e.Text : e.Text);
            }
        }

        // ---- stage 3: live where players are --------------------------------------------------

        readonly Dictionary<string, double> stashSent = new Dictionary<string, double>();

        /// <summary>
        /// Hub loot waiting in the sim → into a spawned hub wreck's boxes while its playfield is live (newest lootable hull
        /// first). The PfServer answers "stashed" with what fit; the rest waits. Every 30 s per hub at most.
        /// </summary>
        /// <summary>Live pirates the sim just sent off (rest over / lurk over): their PfServer flies them out and jumps them.</summary>
        void Departures()
        {
            foreach (var a in world.Agents.Values.Where(x => !x.IsTrader && x.IsLive && x.Alive && x.State == "warp").ToList())
            {
                if (!departing.Add(a.Id)) continue;
                Send(a.Live, "simdepart", a.Id, Where(a.System, a.Dest));
                log($"sim: {a.Id} '{a.Name}' leaves '{a.Live}' live, bound for {a.Dest}");
            }
            departing.RemoveWhere(id => !(world.Get(id) is Core.Sim.SimAgent x) || !x.IsLive);
        }
        readonly HashSet<string> departing = new HashSet<string>(StringComparer.Ordinal);

        void FlushStash(List<string> pfs, double now)
        {
            foreach (var hub in world.Hubs.Values.Where(h => h.Stash.Count > 0 && pfs.Contains(h.System)))
            {
                if (stashSent.TryGetValue(hub.Id, out var at) && now - at < 30) continue;
                var box = world.Wrecks.Values.Where(x => x.Hub == hub.Id && !x.Debris && !x.Gone && x.Entity > 0 && x.Shown == "wreck").Where(x => !x.Centre).OrderByDescending(x => x.Time).FirstOrDefault();   // the hauled wrecks: the listening post has no crates
                if (box == null) continue;
                stashSent[hub.Id] = now;
                var msg = new List<object> { "stash", box.Entity, hub.Id };
                foreach (var s in hub.Stash.GroupBy(x => x.Item)) { msg.Add(s.Key); msg.Add(s.Sum(x => x.Amt)); }
                Send(hub.System, msg.ToArray());
            }
        }

        void Materialize(List<string> pfs, double now)
        {
            var changed = false;
            // a wreck spawn that never reported back (-1 = spawning): free it after 2 min so it can spawn again
            foreach (var w in world.Wrecks.Values.Where(x => x.Entity < 0))
            {
                if (!wreckSpawning.TryGetValue(w.Id, out var since)) wreckSpawning[w.Id] = since = now;
                if (now - since > 120) { w.Entity = 0; wreckSpawning.Remove(w.Id); changed = true; log($"sim: wreck {w.Id} '{w.Name}' spawn lost — beacon again"); }
            }
            foreach (var id in wreckSpawning.Keys.Where(k => !world.Wrecks.TryGetValue(k, out var x) || x.Entity >= 0).ToList()) wreckSpawning.Remove(id);
            foreach (var a in world.Agents.Values.ToList())
            {
                if (a.IsLive && ((!pfs.Contains(a.Live) && now - startedAt > 45) || (a.EntityId == 0 && asked.TryGetValue(a.Id, out var t0) && now - t0 > 120)))
                {   // player left that playfield (or the spawn never came): back to the sim
                    if (a.EntityId > 0) dedi.Destroy(a.EntityId);
                    world.EndLive(a.Id, "reset", now); asked.Remove(a.Id); changed = true;
                    log($"sim: {a.Id} '{a.Name}' no longer live (playfield '{a.System}' empty or spawn lost)");
                }
            }
            // traders go live when the PfServer sees a player near their lane position ("simwant"), not here
            // a live trader reached a pirate's spot on its lane (from the sim's timeline): the fight starts, live
            for (var guard = 0; guard < 20; guard++)
            {
                var pass = world.NextPass(now, true);
                if (pass == null) break;
                var before = world.Events.Count;
                var f = world.StartLiveFight(pass, rng);
                changed = true;
                log($"sim: {f.Id} starts live in '{f.Live}': {pass.Trader.Name} ran into {pass.Pirate.Name}");
                Dispatch(world.Events.Skip(before).ToList(), pfs);
                // a pirate that's already live is told to go for it; the rest are spawned below
                foreach (var m in world.MembersOf(f).Where(m => !m.IsTrader && m.EntityId > 0 && !asked.ContainsKey(m.Id)))
                    Send(f.Live, "attack", m.Id, pass.Trader.EntityId);
            }
            // everyone in a live fight that isn't real yet: traders spawn where the fight is; pirates once a trader is there
            foreach (var f in world.Fights.Values.Where(x => x.IsLive && pfs.Contains(x.Live)).ToList())
            {
                var members = world.MembersOf(f);
                var target = members.FirstOrDefault(m => m.IsTrader && m.IsLive && m.EntityId > 0);
                if (!members.Any(m => m.IsTrader && m.IsLive))
                {   // its trader never made it (or left): pirates still waiting to spawn go back to the sim with the fight
                    foreach (var m in members.Where(m => m.IsLive && m.EntityId == 0)) { world.EndLive(m.Id, "reset", now); asked.Remove(m.Id); changed = true; }
                    continue;
                }
                foreach (var m in members.Where(m => m.IsLive && m.EntityId == 0 && !asked.ContainsKey(m.Id)))
                {
                    if (m.IsTrader) { asked[m.Id] = now; changed = true; Send(f.Live, "trader", m.Id, m.Name, world.ShipOf(m), m.After == "out" ? Where(m.System, m.Dest) : "", MemoryLine(m), m.Cargo, m.CargoAmt, F(m.Speed)); }
                    else if (target != null)
                    {
                        asked[m.Id] = now; changed = true;
                        Send(f.Live, "pirate", m.Id, m.Name, PiratePrefab(m), target.EntityId, F(f.Pos.X), F(f.Pos.Y), F(f.Pos.Z), m.Loot, m.LootMainAmt, MemoryLine(m));
                        log($"sim: pirate {m.Id} '{m.Name}' goes live in {f.Id} on {target.Name}");
                    }
                }
            }
            if (changed) Save();
        }

        /// <summary>An agent's memories for the wire: "memory.escort.X=abandoned;…" (';' / '|' / '=' in values can't occur).</summary>
        string MemoryLine(SimAgent a) =>
            string.Join(";", world.MemoriesOf(a).Select(kv => kv.Key + "=" + kv.Value.Replace(";", ",").Replace("|", "/")));

        string PiratePrefab(SimAgent p) => world.ShipOf(p);   // its own hull, every time
        static string F(double v) => v.ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);

        void Send(string pf, params object[] f) =>
            api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(new object[] { 0, "fly", "sim" }.Concat(f).ToArray()));

        /// <summary>simspawn / simend / simhello from a PfServer. True if handled.</summary>
        public bool TryHandle(string[] f, string playfield)
        {
            if (f.Length < 2) return false;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (f[0])
            {
                case "simspawn":
                {
                    var a = world.Get(f[1]);
                    if (a == null || !a.IsLive || f.Length < 9) return true;
                    float G(string v) => float.Parse(v, inv);
                    var pf = a.Live;
                    dedi.Spawn(pf, f[2], new PVector3(G(f[3]), G(f[4]), G(f[5])), new PVector3(0, G(f[6]), 0), f[7], byte.Parse(f[8], inv), id =>
                    {
                        a.EntityId = id; asked.Remove(a.Id); Save();
                        Send(pf, "spawned", a.Id, id);
                        log($"sim: {a.Id} '{a.Name}' spawned as {id} in '{pf}'");
                    });
                    return true;
                }
                case "simstations":   // a PfServer's real stations: the anchors for that system's lanes
                {
                    var pf = f[1].Length > 0 ? f[1] : playfield;
                    // f[2] = every base still in the playfield: a known station stays while it's there, even if the
                    // PfServer couldn't recognise it this time (proxy, logic reload)
                    var present = new HashSet<int>((f.Length > 2 ? f[2] : "").Split(',').Select(x => int.TryParse(x, out var i) ? i : 0).Where(i => i != 0));
                    var list = new List<SimStation>();
                    foreach (var e in f.Skip(3))
                    {
                        var p = e.Split(';');
                        if (p.Length < 5 || !int.TryParse(p[0], out var sid)) continue;
                        float G(string v) => float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var x) ? x : 0f;
                        var st = new SimStation { Id = sid, Name = p[1], Pos = new Core.Flight.Vec3(G(p[2]), G(p[3]), G(p[4])) };
                        if (p.Length >= 15)
                        {   // measured box (centre, half size, rotation): berths sit just outside the hull
                            st.HasBox = true; st.Box = new Core.Flight.Vec3(G(p[5]), G(p[6]), G(p[7])); st.Half = new Core.Flight.Vec3(G(p[8]), G(p[9]), G(p[10]));
                            st.Rot = new Core.Flight.Quat(G(p[11]), G(p[12]), G(p[13]), G(p[14]));
                        }
                        list.Add(st);
                    }
                    // an entity id lives in ONE playfield: a station registered elsewhere, or not among this playfield's bases,
                    // is a stale report (PfServer processes are reused across playfields)
                    // Presence decides: a station really among this playfield's bases is ours — and stale wherever else it's listed.
                    bool Elsewhere(int id) => world.Stations.Any(kv => kv.Key != pf && kv.Value.Any(x => x.Id == id));
                    list = list.Where(x => present.Contains(x.Id)).ToList();
                    foreach (var other in world.Stations.Keys.Where(k => k != pf).ToList())
                    {
                        var keepOther = world.Stations[other].Where(x => list.All(y => y.Id != x.Id)).ToList();
                        var before = world.Stations[other].Count;
                        if (keepOther.Count != before) { world.SetStations(other, keepOther); log($"sim: '{pf}' has {before - keepOther.Count} station(s) that were listed under '{other}' — fixed"); }
                    }
                    // sticky: a PfServer only sees bases near a player, so "not reported" means "not seen", not "gone"
                    // (else a system's stations vanish while the player is out on a lane)
                    if (world.Stations.TryGetValue(pf, out var known))
                    {
                        var stale = known.Where(k => !present.Contains(k.Id) && Elsewhere(k.Id)).ToList();
                        list.AddRange(known.Where(k => !stale.Contains(k) && list.All(x => x.Id != k.Id) && !(list.Count > 0 && Core.Sim.TemplateStations.IsPlaceholder(k))));   // real ones replace the template's
                        if (stale.Count > 0) log($"sim: dropped {stale.Count} stale station(s) from '{pf}' (they belong to another playfield)");
                        if (list.Count == 0) { world.SetStations(pf, list); OffNetwork(); Save(); return true; }
                    }
                    if (list.Count == 0) return true;
                    if (world.SetStations(pf, list))
                    {
                        log($"sim: {list.Count} station(s) in '{pf}': {string.Join(", ", list.Select(x => x.Name))}");
                        Save(); SendTraffic(pf);
                    }
                    if (JoinNetwork(pf)) Save();
                    return true;
                }
                case "wreckgone":   // a spawned wreck: cleaned up (nobody came) or claimed (a player's core) — the record goes
                {                   // a hub wreck's hull swapped / vanished: it respawns as what it should be (or goes, over the cap)
                    var ent = int.TryParse(f[1], out var e1) ? e1 : 0;
                    var why = f.Length > 2 ? f[2] : "?";
                    var w = world.Wrecks.Values.FirstOrDefault(x => x.Entity == ent);
                    // "destroyed" = gone with a player right there (blown up / scrapped): gone for good, like a claim
                    if (w != null && w.IsHub && why != "claimed" && why != "destroyed") { world.HubHullDown(w, lost: why == "vanished"); var hubOf = world.Hubs.TryGetValue(w.Hub, out var hh0) ? hh0 : null; if (hubOf != null) world.HubCaps(hubOf, Now); Save(); log($"sim: hub wreck {w.Id} '{w.Name}' hull {ent} down ({why}) — {(world.Wrecks.ContainsKey(w.Id) ? "respawns as " + (w.Debris ? "debris" : "wreck") : "gone")}"); SendTraffic(playfield); }
                    else if (w != null) { world.Wrecks.Remove(w.Id); world.Count("eco|burn|wrecks gone live (looted or cleared)", w.Cash); Save(); log($"sim: wreck {w.Id} '{w.Name}' gone ({why})"); }
                    return true;
                }
                case "stashed":   // hub loot made it into a hub wreck's boxes: "stashed|hub|entity|item|amt|item|amt…"
                {
                    if (f.Length < 3 || !world.Hubs.TryGetValue(f[1], out var hub)) return true;
                    var got = new List<string>();
                    for (var i = 3; i + 1 < f.Length; i += 2)
                        if (int.TryParse(f[i + 1], out var n) && n > 0) { world.Unstash(hub, f[i], n); got.Add($"{n} {f[i]}"); }
                    if (got.Count > 0) { Save(); log($"sim: {hub.Name} stash into {f[2]}: {string.Join(", ", got)}; {hub.Stash.Count} load(s) still waiting"); }
                    return true;
                }
                case "wreckfromlive":   // a live ship's core died in front of a player: its hull stays (vanilla) — beacon + cleanup
                {                       // (a pirate's hull keeps its biggest load, like a sim kill)
                    var a = world.Get(f[1]);
                    if (a == null || f.Length < 6) return true;
                    float G(string v) => float.Parse(v, inv);
                    var w = world.Wreck(a, new Core.Flight.Vec3(G(f[3]), G(f[4]), G(f[5])), a.IsTrader ? a.Cargo : a.Loot, a.IsTrader ? a.CargoAmt : a.LootMainAmt, Now, rng);
                    if (w != null) { w.Entity = int.Parse(f[2], inv); Save(); log($"sim: live wreck {w.Id} '{a.Name}' = entity {w.Entity}"); }
                    return true;
                }
                case "simdock":   // a live inbound trader reached its berth: docked, and stays parked there (live)
                {
                    if (world.DockLive(f[1], rng, Now)) { var a = world.Get(f[1]); log($"sim: {a.Id} '{a.Name}' docked live at {world.StationOf(a)?.Name}"); Save(); SendTraffic(a.System); }
                    return true;
                }
                case "wreckspawn":   // spike: spawn a hull where the PfServer says, tell it the id
                {
                    if (f.Length < 7) return true;
                    float G(string v) => float.Parse(v, inv);
                    var pf = playfield;
                    Action<int> done = id => { Send(pf, "wreck", id); log($"sim: wreck spike spawned {id} in '{pf}'{(DediSpike.LooksLikeBase(f[1]) ? " as a BASE" : "")}"); };
                    if (DediSpike.LooksLikeBase(f[1])) dedi.SpawnBase(pf, f[1], new PVector3(G(f[2]), G(f[3]), G(f[4])), new PVector3(0, G(f[5]), 0), f.Length > 6 ? f[6] : "Wreck", 7, done);   // a station prefab: as a base (services need one)
                    else dedi.Spawn(pf, f[1], new PVector3(G(f[2]), G(f[3]), G(f[4])), new PVector3(0, G(f[5]), 0), f.Length > 6 ? f[6] : "Wreck", 7, done);
                    return true;
                }
                case "simmem":   // a live sim trader remembered something about a player: it's the agent's (persists across spawns)
                {
                    var a = world.Get(f[1]);
                    if (a == null || f.Length < 4 || !f[2].StartsWith("memory.")) return true;
                    world.Remember(a, f[2], f[3]); Save();
                    log($"sim: {a.Id} '{a.Name}' remembers {f[2]} = {f[3]}");
                    if (!a.IsTrader && f[2].StartsWith("memory.killed.", StringComparison.Ordinal)) Rescued(a, f[2].Substring("memory.killed.".Length));
                    return true;
                }
                case "simwant":   // a player is near this trader's lane position: make it real
                {
                    if (world.Wrecks.TryGetValue(f[1], out var wr))
                    {   // a wreck beacon a player came near: the hull, for real (the PfServer strips its core and loads its hold)
                        if (wr.Entity != 0 || wr.System != playfield || wr.Gone) return true;
                        // the PfServer nudged the spot clear of hulls / asteroids it can see: keep it (the record moves too)
                        if (f.Length >= 5 && float.TryParse(f[2], System.Globalization.NumberStyles.Float, inv, out var sx) && float.TryParse(f[3], System.Globalization.NumberStyles.Float, inv, out var sy) && float.TryParse(f[4], System.Globalization.NumberStyles.Float, inv, out var sz))
                            wr.Pos = new Core.Flight.Vec3(sx, sy, sz);
                        var wpf = playfield;
                        // an Eden wrecked ship: whole hulk, or picked-over wreckage; past a hub's lootable few, stripped debris
                        var debris = wr.IsHub && wr.Debris;
                        var prefab = debris ? Core.Sim.World.DebrisPrefab(wr) : Core.Sim.World.WreckPrefab(wr);
                        var mode = debris ? "debris" : wr.IsHub ? "hub" : "";
                        wr.Entity = -1;   // spawning
                        wr.Shown = debris ? "debris" : "wreck";
                        // adrift: pitched and rolled at a wrong angle, not parked level; NO faction (255): spawned as Polaris,
                        // looting a faction-owned hull costs that faction's rep (vanilla)
                        dedi.Spawn(wpf, prefab, new PVector3(wr.Pos.X, wr.Pos.Y, wr.Pos.Z), new PVector3(rng.Next(-60, 61), rng.Next(360), rng.Next(-90, 91)), StructureTag.Apply(wr.Centre ? $"{wr.Name} ({(world.Hubs.TryGetValue(wr.Hub, out var ch) ? world.HubRank(ch) : "camp")})" : wr.Decor ? "Debris" : "Wreck of " + wr.Name), 255, id =>
                        {
                            wr.Entity = id; Save();
                            Send(wpf, "wreckfill", id, wr.Id, debris ? "" : wr.Cargo, debris ? 0 : wr.Amt, Core.Sim.World.IsPrewrecked(prefab) || wr.IsHub ? "1" : "0", mode, debris ? 0 : wr.Cash);
                            log($"sim: wreck {wr.Id} '{wr.Name}' spawned as {id} in '{wpf}' — {prefab} ({(wr.Looted ? "picked over" : "whole")}, was {(wr.Ship.Length > 0 ? wr.Ship : "?")}; {wr.Amt} {wr.Cargo})");
                        });
                        return true;
                    }
                    var a = world.Get(f[1]);
                    var pf = playfield;
                    if (a == null || !a.Alive || a.IsLive || a.System != pf) return true;
                    var fight = world.FightOf(a);
                    if (fight != null)
                    {   // a player came near a fight: the whole engagement goes live (spawned by Materialize)
                        if (!fight.HasPos) return true;
                        foreach (var m in world.GoLive(fight, pf))
                            if (m.EntityId > 0 && staleDone.Add(m.EntityId)) dedi.Destroy(m.EntityId);
                        foreach (var m in world.MembersOf(fight)) { m.EntityId = 0; asked.Remove(m.Id); }
                        log($"sim: {fight.Id} goes live in '{pf}' ({string.Join(", ", world.MembersOf(fight).Select(m => m.Name))})");
                        Save(); Materialize(PlayerPlayfields(), Now);
                        return true;
                    }
                    if (!a.IsTrader)
                    {   // a player found a lurking pirate: it's there, waiting
                        if ((a.State != "lurk" && a.State != "home") || !world.Track(a, out var spot)) return true;
                        if (a.EntityId > 0 && staleDone.Add(a.EntityId)) dedi.Destroy(a.EntityId);
                        a.Live = pf; a.EntityId = 0; asked[a.Id] = Now;
                        Send(pf, "pirate", a.Id, a.Name, PiratePrefab(a), 0, F(spot.A.X), F(spot.A.Y), F(spot.A.Z), a.Loot, a.LootMainAmt, MemoryLine(a));
                        log($"sim: lurking pirate {a.Id} '{a.Name}' goes live in '{pf}'");
                        Save();
                        return true;
                    }
                    if (!a.OnLeg && a.State != "dock" && a.State != "pass") return true;   // docked ones too: parked at their berth
                    if (a.EntityId > 0 && staleDone.Add(a.EntityId)) { dedi.Destroy(a.EntityId); log($"sim: old copy {a.EntityId} of {a.Id} destroyed before going live again"); }
                    a.Live = pf; a.EntityId = 0; asked[a.Id] = Now;
                    Send(pf, "trader", a.Id, a.Name, world.ShipOf(a), a.State == "out" || a.State == "pass" ? Where(a.System, a.Dest) : "", MemoryLine(a), a.Cargo, a.CargoAmt, F(a.Speed));
                    log($"sim: {a.Id} '{a.Name}' goes live in '{pf}' ({a.State} lane, {world.StationOf(a)?.Name})");
                    Save();
                    return true;
                }
                case "simend":
                {
                    var a = world.Get(f[1]);
                    var how = f.Length > 2 ? f[2] : "left";
                    if (a == null) return true;
                    var frac = f.Length > 3 && double.TryParse(f[3], System.Globalization.NumberStyles.Float, inv, out var fr) ? fr : -1;
                    var hull = f.Length > 4 && double.TryParse(f[4], System.Globalization.NumberStyles.Float, inv, out var hl) ? hl : -1;
                    var before = world.Events.Count;
                    leftLive[a.Id] = Now;
                    world.EndLive(a.Id, how, Now, frac, hull); asked.Remove(a.Id);
                    var happened = world.Events.Skip(before).ToList();   // e.g. "Brass Goose was destroyed near SNUO" — announce it
                    foreach (var e in happened) log("sim: " + e.Text);
                    Dispatch(happened, PlayerPlayfields());
                    log($"sim: {a.Id} '{a.Name}' back in the sim ({how}{(frac >= 0 ? $", {frac:P0} along its lane" : "")}{(hull >= 0 ? $", {hull:P0} of its blocks" : "")}{(a.Fight.Length > 0 ? $", still in {a.Fight}" : "")})");
                    Save();
                    if (a.System.Length > 0) SendTraffic(a.System);
                    return true;
                }
                case "simhello":
                {
                    // a PfServer (re)started on this playfield: hand it its live agents back; spawns that never landed
                    // go back to the sim; old copies of agents that aren't live any more are destroyed (orphans)
                    var pf = f[1].Length > 0 ? f[1] : playfield;
                    nextGlobal = Math.Min(nextGlobal, Now + 20);   // a playfield just started (maybe generated just now): learn its stations
                    if (JoinNetwork(pf)) Save();
                    foreach (var a in world.Agents.Values.Where(x => x.Live == pf).ToList())
                    {
                        if (a.EntityId == 0) { world.EndLive(a.Id, "reset", Now); asked.Remove(a.Id); continue; }
                        var fight = world.FightOf(a);
                        var target = a.IsTrader || fight == null ? 0 : world.MembersOf(fight).FirstOrDefault(t => t.IsTrader && t.IsLive && t.EntityId > 0)?.EntityId ?? 0;
                        // a lurking / chilling pirate's spot goes along (else it flies off to the origin after a reload)
                        var spt = default(Core.Sim.SimTrack);
                        var spotOk = !a.IsTrader && (a.State == "lurk" || a.State == "home") && world.Track(a, out spt);
                        if (spotOk) Send(pf, "adopt", a.Id, a.Kind, a.EntityId, target, F(spt.A.X), F(spt.A.Y), F(spt.A.Z), MemoryLine(a));
                        else Send(pf, "adopt", a.Id, a.Kind, a.EntityId, target, "", "", "", MemoryLine(a));
                        log($"sim: {a.Id} '{a.Name}' re-adopted by '{pf}' as {a.EntityId}");
                    }
                    // spawned wrecks in this playfield: its PfServer tracks them again (cleanup when nobody's near, claim, vanished)
                    foreach (var w in world.Wrecks.Values.Where(x => x.System == pf && x.Entity > 0))
                    { Send(pf, "wreckadopt", w.Entity, w.Id, w.IsHub ? (w.Debris ? "debris" : "hub") : ""); log($"sim: wreck {w.Id} '{w.Name}' re-adopted by '{pf}' as {w.Entity}"); }
                    foreach (var a in world.Agents.Values.Where(x => !x.IsLive && x.EntityId > 0 && x.State != "dead" && !staleDone.Contains(x.EntityId)).ToList())
                    { staleDone.Add(a.EntityId); dedi.Destroy(a.EntityId); log($"sim: stale copy {a.EntityId} of {a.Id} '{a.Name}' destroyed"); }
                    Save();
                    SendTraffic(pf);
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Hail → Talk: the <paramref name="n"/>-th thing this ship's crew has to say (rumours from the sim), or null if it's
        /// not a sim ship. Each "ask around" moves to the next one; wraps.
        /// </summary>
        public string Talk(int entityId, int pid, int n)
        {
            var a = world.ByEntity(entityId);
            if (a == null) return null;
            var items = world.Rumours(a, PlayerKeys(pid), Now, Where);
            if (!a.IsTrader) return "Talk? Keep flying.";
            if (world.WantOf(a) is World.Want w) items.Insert(0, $"{w.Why} We'd pay {w.Price:N0} a piece for {World.Humanize(w.Item)} — up to {w.Left}.");
            if (items.Count == 0) return "Quiet out there lately. Nothing worth telling.";
            var i = ((n % items.Count) + items.Count) % items.Count;
            return items[i] + (items.Count > 1 ? $"\n\n({i + 1} of {items.Count})" : "");
        }

        /// <summary>
        /// Hail → Trade: a live sim trader's cargo + ship's stores as a deal (1.25× base); it buys its own line of goods at
        /// 0.8×, anything else with a known price at 0.5×. What you take leaves it for good. Null = not one of our traders.
        /// </summary>
        public DediTrade.ShipDeal ShipDeal(int entityId, int pid)
        {
            var a = world.ByEntity(entityId);
            if (a == null || !a.IsTrader || !a.Alive) return null;
            var d = new DediTrade.ShipDeal { Ship = a.Name, BuysAt = item => World.BuyPrice(a, item), Cash = world.Cash(a) };
            if (world.WantOf(a) is World.Want want)
            {
                d.WantItem = want.Item; d.WantLeft = want.Left; d.WantPrice = want.Price;
                d.Fulfilled = n =>
                {
                    world.Fulfil(a, n);
                    if (pid > 0)
                    {   // it remembers who (its next hail says so) and, once it has all it asked for, word gets around
                        world.Remember(a, "memory.helped.pid:" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture), want.Item);
                        if (world.WantOf(a) == null)
                        {
                            world.Helped(a, api.Application.GetPlayerDataFor(pid)?.PlayerName ?? "", want.Item, Now);
                            SayTo(pid, a.Name, "That's everything we needed — thank you! We won't forget it.");
                        }
                    }
                    Save();
                };
            }
            if (a.CargoAmt > 0) d.Stock.Add(new DediTrade.StockLine { Item = a.Cargo, Amt = a.CargoAmt, Price = World.SellPrice(a.Cargo) });
            foreach (var kv in world.Stores(a)) d.Stock.Add(new DediTrade.StockLine { Item = kv.Key, Amt = kv.Value, Price = World.SellPrice(kv.Key) });
            d.Done = (taken, paid) =>
            {
                if (taken.Count == 0 && paid == 0) return;
                foreach (var kv in taken) world.Sell(a, kv.Key, kv.Value);
                world.AddCash(a, paid);
                log($"sim: {a.Id} '{a.Name}' sold {string.Join(", ", taken.Select(kv => kv.Key + " " + kv.Value))}; cargo {a.CargoAmt} {a.Cargo}, stores {(a.Extra.TryGetValue("stores", out var stv) ? stv : "-")}");
                Save(); if (a.System.Length > 0) SendTraffic(a.System);
            };
            return d;
        }

        /// <summary>
        /// How memories may name a player: "pid:N" (what PfServers write — IPlayer.SteamId is empty there) and the Steam id.
        /// </summary>
        List<string> PlayerKeys(int pid)
        {
            var l = new List<string> { "pid:" + pid.ToString(System.Globalization.CultureInfo.InvariantCulture) };
            var sid = api.Application.GetPlayerDataFor(pid)?.SteamId ?? "";
            if (sid.Length > 0) l.Add(sid);
            return l;
        }

        /// <summary>A pirate's price to leave a player alone (hail → Pay them off), and what it says. Null = not a pirate of ours.</summary>
        public sealed class Toll { public int Price; public string Line = ""; }

        /// <summary>
        /// What it costs to make a pirate look the other way: ~8k for a raider, ~15k for a shielded gang ship, ±20 % by
        /// mood (stable per pirate and player). A crew whose mate you killed won't take money (Price 0).
        /// </summary>
        /// <summary>A player paid a pirate off: the money is in its pocket now.</summary>
        public void TollPaid(int entityId, int price)
        {
            var a = world.ByEntity(entityId);
            if (a == null || a.IsTrader || price <= 0) return;
            world.AddCash(a, price); world.Count("sys|" + a.System + "|ptoll", price);
            Save();
        }

        public Toll PirateToll(int entityId, int pid)
        {
            var a = world.ByEntity(entityId);
            if (a == null || a.IsTrader || !a.Alive || !a.IsLive) return null;   // only one that's really there (a stale entity id is not a pirate)
            var keys = PlayerKeys(pid);
            var grudge = world.MemoriesOf(a).FirstOrDefault(kv => keys.Any(k => kv.Key == "memory.killed." + k)).Value;
            var key = keys[0];
            if (grudge != null) return new Toll { Price = 0, Line = $"Money? You killed {grudge}. There's no price for that." };
            var price = LiveRules.TollPrice(a.Strength, a.Id + "|" + key);
            return new Toll { Price = price, Line = $"Nice ship you've got. {price:N0} cr and we never saw you." };
        }

        // ---- bounties ------------------------------------------------------------------------------------------

        /// <summary>The bounty this ship offers the player (hail → Jobs), as dialog text; null = none.</summary>
        public string BountyOfferText(int entityId, int pid)
        {
            var a = world.ByEntity(entityId);
            var o = world.BountyOffer(a, PlayerKeys(pid)[0], Now);
            if (o == null) return null;
            var p = world.Get(o.Pirate);
            var hub = world.HubOf(p);
            var ago = Math.Max(1, (Now - (a.Extra.TryGetValue("hitby", out var hb) && double.TryParse(hb.Split(':').ElementAtOrDefault(1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var t) ? t : Now)) / 60);
            return $"{o.PirateName} jumped us near {Where(a.System, o.Where)}, {(ago < 90 ? $"{ago:0} min" : $"{ago / 60:0} h")} ago." +
                   (hub != null ? $" His crowd hangs around {hub.Name}, {Where(a.System, hub.System)}." : "") +
                   (o.Hunters > 1
                       ? $"\nAnother {(o.Hunters > 2 ? $"{o.Hunters - 1} captains are" : "captain's")} already after him. Join in — the {o.Reward:N0} cr pot is split between everyone hunting him when he's dead. The more the better. {Math.Max(1, (o.Until - Now) / 3600):0} h left on it."
                       : $"\nSee him dead — your guns or anyone's — and it's {o.Reward:N0} cr, paid by any of us. You've got {world.Config.BountySeconds / 3600:0} hours.");
        }

        /// <summary>Take the bounty this ship offers: the contract is saved with the world. Returns what it says.</summary>
        public string AcceptBounty(int entityId, int pid)
        {
            var a = world.ByEntity(entityId);
            var o = world.BountyOffer(a, PlayerKeys(pid)[0], Now);
            if (o == null) return "That job's gone.";
            world.Accept(o); Save(); TellBounties();   // the others hear right away if they're on
            log($"sim: bounty {o.Id} on {o.PirateName} ({o.Pirate}) taken by {o.Player} for {o.Reward} cr");
            return o.Hunters > 1 ? $"Deal. You're in on {o.PirateName} — {o.Hunters} of you now on a {o.Reward:N0} cr pot." : $"Deal. {o.PirateName}, dead, {o.Reward:N0} cr. Come back to any of us when it's done.";
        }

        /// <summary>Any trader hailed: settled bounties are paid. Returns the line to say and the credits (0 = nothing due).</summary>
        public string CollectBounties(int pid, out int credits)
        {
            credits = 0;
            var due = world.Collect(PlayerKeys(pid), Now);
            if (due.Count == 0) return null;
            credits = due.Sum(b => b.Payout > 0 ? b.Payout : b.Reward);
            Save();
            log($"sim: bounties paid to {pid}: {string.Join(", ", due.Select(b => $"{b.PirateName} {b.Reward}"))}");
            var split = due.Any(b => b.Hunters > 1) ? " (your share of the pot)" : "";
            return $"Word travels — {string.Join(" and ", due.Select(b => b.PirateName))} {(due.Count > 1 ? "are" : "is")} dead. Here's your {credits:N0} cr{split}.";
        }

        /// <summary>Tell online players how their contracts ended (once each).</summary>
        void TellBounties()
        {
            var online = new HashSet<int>(api.Application.GetPlayerEntityIds() ?? new List<int>());
            foreach (var b in world.Bounties.Values.Where(x => x.Note.Length > 0 && x.State == "open").ToList())
            {   // pending notes (someone joined the hunt) — told once they're on
                if (!b.Player.StartsWith("pid:") || !int.TryParse(b.Player.Substring(4), out var np) || !online.Contains(np)) continue;
                api.Application.SendChatMessage(new MessageData { SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = np, SenderNameOverride = "LS", Text = b.Note });
                log($"sim: bounty {b.Id} note to {np}: {b.Note}");
                b.Note = ""; Save();
            }
            foreach (var b in world.Bounties.Values.Where(x => !x.Told && x.State != "open").ToList())
            {
                if (!b.Player.StartsWith("pid:") || !int.TryParse(b.Player.Substring(4), out var pid) || !online.Contains(pid)) continue;
                b.Told = true;
                var text = b.State == "done" ? $"Bounty: {b.PirateName} is dead.{(b.Hunters > 1 ? $" Split {b.Hunters} ways: {b.Payout:N0} cr each." : "")} Collect {(b.Payout > 0 ? b.Payout : b.Reward):N0} cr from any trader."
                         : b.State == "expired" ? $"Bounty on {b.PirateName} ran out."
                         : $"Bounty on {b.PirateName}: the trail went cold.";
                api.Application.SendChatMessage(new MessageData { SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = pid, SenderNameOverride = "LS", Text = text });
                log($"sim: bounty {b.Id} {b.State} — told {pid}");
                Save();
            }
        }

        /// <summary>One line for the hail screen: what it sells and what it buys.</summary>
        public string TradeLine(int entityId)
        {
            var a = world.ByEntity(entityId);
            if (a == null || !a.IsTrader || !a.Alive) return null;
            var sells = new List<string>();
            if (a.CargoAmt > 0) sells.Add($"{World.Humanize(a.Cargo)} {a.CargoAmt}");
            sells.AddRange(world.Stores(a).Select(kv => $"{World.Humanize(kv.Key)} {kv.Value}"));
            var buys = string.Join(", ", World.RoleOf(a).Items.Select(World.Humanize));
            var want = world.WantOf(a);
            var wanted = want != null ? $"\nWANTED: {World.Humanize(want.Item)} ×{want.Left} at {want.Price:N0} cr each — \"{want.Why}\"" : "";
            return $"Selling: {(sells.Count > 0 ? string.Join(", ", sells) : "nothing")}.\nBuys: {buys} (good price); other goods cheap — {world.Cash(a):N0} cr to spend.{wanted}";
        }

        /// <summary>Registry hook: a live ship got trip.dest = it jumped out at the end of a trip → it carries on here.</summary>
        public void OnDeparted(int entityId, string name, string playfield)
        {
            var a = world.Adopt(entityId, StructureTag.Strip(name ?? ""), playfield, 1.0, rng, Now);
            log($"sim: adopted {entityId} '{name}' as {a.Id}, {a.System} → {a.Dest}");
            Save();
        }

        /// <summary>/ls sim [log|tick|reset]</summary>
        public void Command(int pid, SpikeCommand cmd, Action<int, string> say)
        {
            switch (cmd.Arg(0, ""))
            {
                case "log":
                    var n = cmd.IntArg(1, 10, 1, 40);
                    var now = Now;
                    foreach (var e in world.Events.Skip(Math.Max(0, world.Events.Count - n)))
                        say(pid, $"{(now - e.Time) / 60:0} min ago: {e.Text}");
                    if (world.Events.Count == 0) say(pid, "sim: nothing has happened yet");
                    return;
                case "stats":   // lifetime counters: trade gross, lane losses, busiest stations, hubs by take, top haulers
                    foreach (var l in world.StatsReport(cmd.IntArg(1, 5, 1, 15))) say(pid, l);
                    return;
                case "here":   // test: a pirate + a trader in the caller's playfield → a live attack in front of them
                {
                    var pf = api.Application.GetPlayerDataFor(pid)?.PlayfieldName ?? "";
                    if (!world.Config.Systems.Contains(pf)) { say(pid, $"sim: '{pf}' is not one of the sim's systems"); return; }
                    say(pid, "sim: " + world.StageHere(pf, Now, rng));
                    Save(); Tick(true);
                    return;
                }
                case "wreck":   // test: a wreck beacon in the caller's system, ~3 km out on a station lane, with a random cargo
                {
                    if (cmd.Arg(1, "") == "clear")
                    {   // test wrecks ("Test Hauler …") are spawned hulls: they never expire on their own — remove them + their hulls
                        var junk = world.Wrecks.Values.Where(x => x.Name.StartsWith("Test Hauler", StringComparison.Ordinal)).ToList();
                        foreach (var x in junk) { if (x.Entity > 0) dedi.Destroy(x.Entity); world.Wrecks.Remove(x.Id); }
                        Save(); foreach (var p in PlayerPlayfields()) SendTraffic(p);
                        say(pid, $"sim: cleared {junk.Count} test wreck(s)");
                        return;
                    }
                    var pf = api.Application.GetPlayerDataFor(pid)?.PlayfieldName ?? "";
                    Core.Flight.Vec3 spot; string where;
                    if (world.Stations.TryGetValue(pf, out var sts) && sts.Count > 0)
                    {
                        var st = sts[rng.Next(sts.Count)];
                        world.Lane(st, world.TraderDest(rng, pf), out var near, out var far);
                        spot = near + (far - near).Normalized * 3000f; where = $"~3 km out on the {st.Name} lane";
                    }
                    else if (world.Hubs.Values.FirstOrDefault(h => h.System == pf) is Core.Sim.SimHub hub)
                    {   // no stations: a transit kill near the pirate hub (where a passing ship would get jumped)
                        spot = hub.Pos + Core.Sim.World.LaneDir("testwreck|" + Now.ToString(System.Globalization.CultureInfo.InvariantCulture)) * 1500f; where = $"~1.5 km from {hub.Name}";
                    }
                    else { spot = Core.Sim.World.LaneDir("testwreck|" + Now.ToString(System.Globalization.CultureInfo.InvariantCulture)) * 3000f; where = "~3 km from the playfield's centre"; }
                    var dummy = new SimAgent { Id = "test", Name = "Test Hauler " + rng.Next(100, 999), System = pf, Role = Core.Sim.World.Roles[rng.Next(Core.Sim.World.Roles.Length)].Id };
                    world.Load(dummy, rng);
                    if (dummy.CargoAmt == 0) world.Load(dummy, rng);
                    var focus = world.Focus; world.Focus = new List<string>();   // test: allowed anywhere, and past the cap
                    var cap = world.Config.WrecksPerSystem; world.Config.WrecksPerSystem = 99;
                    var w = world.Wreck(dummy, spot, dummy.Cargo, dummy.CargoAmt, Now, rng);
                    world.Focus = focus; world.Config.WrecksPerSystem = cap;
                    if (w == null) { say(pid, "sim: couldn't place a wreck (cap?)"); return; }
                    Save(); SendTraffic(pf);
                    say(pid, $"sim: wreck {w.Id} '{w.Name}' ({World.RoleOf(dummy).Label}, {w.Amt} {World.Humanize(w.Cargo)}) {where} — /ls fly where");
                    return;
                }
                case "reset":
                    // live copies first: destroy them and tell the PfServers to forget (new agents reuse the ids)
                    foreach (var a in world.Agents.Values.Where(x => x.IsLive && x.EntityId > 0)) dedi.Destroy(a.EntityId);
                    foreach (var pf in PlayerPlayfields()) Send(pf, "clear");
                    asked.Clear();
                    world.Agents.Clear(); world.Events.Clear(); world.Fights.Clear();
                    world.LastTick = Now;
                    var systems = world.Config.Systems.ToList(); world.Config.Systems.Clear();
                    foreach (var sys in systems) JoinNetwork(sys);
                    if (world.Agents.Count == 0) world.Seed(rng, Now, 5, 2);
                    Save();
                    say(pid, $"sim: world reseeded — {world.Config.Systems.Count} systems, {world.Agents.Values.Count(a => a.IsTrader)} traders, {world.Agents.Values.Count(a => !a.IsTrader)} pirates");
                    return;
                default:
                    Tick(true);
                    var t = Now;
                    var herePf = api.Application.GetPlayerDataFor(pid)?.PlayfieldName ?? "";
                    var mine = world.Agents.Values.Where(a => a.Alive && (a.System == herePf || a.Dest == herePf || a.IsLive)).Select(a => a.Id).ToList();
                    foreach (var line in world.Status(t).Where(l => mine.Contains(l.Split(' ')[0]))) say(pid, line);   // chat shows one line per message
                    say(pid, $"sim: {world.Config.Systems.Count} systems, {world.Agents.Values.Count(a => a.IsTrader && a.Alive)} traders, {world.Agents.Values.Count(a => !a.IsTrader && a.Alive)} pirates, {world.Fights.Count} fights — listed: here / bound here / live");
                    say(pid, $"pirate risk here: {world.RiskIn(herePf, t)}");
                    return;
            }
        }

        /// <summary>
        /// Playfields with a running PfServer = those that sent a heartbeat lately. (GetPlayerEntityIds also lists OFFLINE
        /// players with their last playfield — not a sign anyone is there.)
        /// </summary>
        List<string> PlayerPlayfields()
        {
            var now = Now;
            return heartbeat.Where(p => now - p.Value < 100).Select(p => p.Key).ToList();   // heartbeat every 15 s; a stall of a few isn't "empty"
        }
        readonly Dictionary<string, double> heartbeat = new Dictionary<string, double>();
        public void Heartbeat(string playfield) { if (!string.IsNullOrEmpty(playfield)) heartbeat[playfield] = Now; }

        /// <summary>A trader's hello on entering a system: to every player in that playfield, from the ship's name.</summary>
        void Broadcast(string pf, Core.Sim.SimAgent a)
        {
            var what = World.RoleOf(a).Label;
            var text = a.State == "pass" ? $"{what}, dropping out of warp here — just passing through, bound for {Where(pf, a.Dest)}. Fly safe."
                     : $"{what}, in from {Where(pf, a.From)}, heading in to dock{(world.StationOf(a) is Core.Sim.SimStation st ? " at " + st.Name : "")}.";
            foreach (var id in api.Application.GetPlayerEntityIds())
            {
                if (api.Application.GetPlayerDataFor(id)?.PlayfieldName != pf) continue;
                api.Application.SendChatMessage(new MessageData
                {
                    SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = id,
                    SenderNameOverride = a.Name, Text = "[broadcast] " + text,
                });
                // the first one a player ever hears: how to find and talk to them (once per player, saved)
                if (world.Tipped.Add(PlayerKeys(id)[0]))
                {
                    api.Application.SendChatMessage(new MessageData
                    {
                        SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = id, SenderNameOverride = "Station control",
                        Text = "New on the band? In your ship's control panel (P), name a signal LS_TRANSPONDER and switch it on to see who's out there. Name another LS_HAIL: point your nose at a ship and flip it to talk, trade or find work. (/ls help)",
                    });
                    Save();
                }
            }
        }

        /// <summary>
        /// A trader jumped by pirates calls for help in the systems around it: every player within <see cref="MaydayLy"/> (by
        /// the route they'd fly) hears where, how far and how many — at most one call per player every
        /// <see cref="MaydaySeconds"/> (a busy lane isn't a wall of chat). In its own system the old MAYDAY line says it.
        /// </summary>
        void Mayday(Core.Sim.SimEvent e, Core.Sim.SimAgent a)
        {
            if (world.Galaxy == null) return;
            var f = world.FightOf(a);
            var pirates = f == null ? 1 : world.MembersOf(f).Count(x => !x.IsTrader && x.Alive);
            foreach (var id in api.Application.GetPlayerEntityIds())
            {
                var pf = api.Application.GetPlayerDataFor(id)?.PlayfieldName;
                if (string.IsNullOrEmpty(pf) || pf == e.System) continue;
                var ly = world.Galaxy.Route(pf, e.System, world.Config.WarpRangeLy, world.Config.GateLy, out _);
                if (double.IsInfinity(ly) || ly > MaydayLy) continue;
                if (maydayAt.TryGetValue(id, out var last) && Now - last < MaydaySeconds) continue;
                maydayAt[id] = Now;
                var what = Core.Sim.World.RoleOf(a).Label;
                var hold = a.Hull >= 0.7 ? "holding for now" : a.Hull >= 0.4 ? "taking hits" : "won't last long";
                api.Application.SendChatMessage(new MessageData
                {
                    SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = id, SenderNameOverride = a.Name,
                    Text = $"MAYDAY — {what} under attack near {Where(pf, e.System)}! {(pirates > 1 ? $"{pirates} pirates" : "A pirate")} on us, {hold}. Anyone close?",
                });
            }
        }
        const double MaydayLy = 30, MaydaySeconds = 180;

        /// <summary>
        /// A trader near a player that wants something (its "want": item, amount, premium) tells them in chat — push to
        /// notify, hail to act. One per player every <see cref="RequestSeconds"/>, only docked / passing ships in the
        /// player's own playfield, never while a fight is on there, only if its purse covers the whole order, once per ship.
        /// </summary>
        void IncomingRequests(List<string> pfs, double now)
        {
            if (now < nextRequestCheck) return;
            nextRequestCheck = now + 30;
            foreach (var pid in api.Application.GetPlayerEntityIds().ToList())
            {
                var pf = api.Application.GetPlayerDataFor(pid)?.PlayfieldName;
                if (string.IsNullOrEmpty(pf) || !pfs.Contains(pf)) continue;
                if (requestAt.TryGetValue(pid, out var last) && now - last < RequestSeconds) continue;
                if (world.Fights.Values.Any(f => f.System == pf)) continue;   // not mid-fight
                var pick = world.Agents.Values.Where(x => x.IsTrader && x.Alive && x.System == pf && (x.State == "dock" || x.State == "pass")
                                                          && !(requestTo.TryGetValue(x.Id, out var was) && now - was.Value < 3600)   // once per stay (cleared on undock) or per hour (passers never undock)
                                                          && world.WantOf(x) is World.Want w && world.Cash(x) >= w.Left * w.Price)
                                              .OrderBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
                if (pick == null) continue;
                var want = world.WantOf(pick);
                var role = World.RoleOf(pick).Label;
                var mins = Math.Max(1, Math.Ceiling((pick.Until - world.LastTick) / 60));
                var where = pick.State == "dock" ? $"docked at {world.StationOf(pick)?.Name ?? "the station"}" : $"passing through, bound for {Where(pf, pick.Dest)}";
                var why = want.Why.Length > 0 ? want.Why + " " : "";
                SayTo(pid, pick.Name, $"{char.ToUpper(role[0]) + role.Substring(1)} here, {where}. {why}Need {want.Left} {World.Humanize(want.Item)}, paying {want.Price:N0} cr each. Here for ~{mins:0} more min — hail us if you've got it.");
                requestAt[pid] = now; requestTo[pick.Id] = new KeyValuePair<int, double>(pid, now);
                log($"sim: {pick.Id} '{pick.Name}' asks {pid} for {want.Left} {want.Item} at {want.Price}");
            }
        }
        double nextRequestCheck;
        const double RequestSeconds = 600;
        readonly Dictionary<int, double> requestAt = new Dictionary<int, double>();
        readonly Dictionary<string, KeyValuePair<int, double>> requestTo = new Dictionary<string, KeyValuePair<int, double>>(StringComparer.Ordinal);   // ship → (player asked, when)
        readonly Dictionary<int, double> maydayAt = new Dictionary<int, double>();

        /// <summary>
        /// A player killed a pirate that was attacking traders: each trader in that fight thanks them and pays out of its own
        /// purse (~10 %, at least <see cref="RescueMin"/>, never more than it has) — answering a mayday pays; a robbed-blind
        /// trader has little to give.
        /// </summary>
        void Rescued(Core.Sim.SimAgent pirate, string playerKey)
        {
            var f = world.FightOf(pirate);
            if (f == null) return;
            var pid = api.Application.GetPlayerEntityIds().FirstOrDefault(id => PlayerKeys(id).Contains(playerKey));
            if (pid == 0) return;
            foreach (var tr in world.MembersOf(f).Where(x => x.IsTrader && x.Alive).ToList())
            {
                if (rescuedBy.Contains(tr.Id + "|" + f.Id)) continue;   // one thank-you per fight, however many pirates you drop
                rescuedBy.Add(tr.Id + "|" + f.Id);
                var purse = world.Cash(tr);
                var pay = Math.Min(purse, Math.Max(RescueMin, (int)(purse * 0.1 / 50) * 50));
                if (pay <= 0) { SayTo(pid, tr.Name, "Thank you — I'd pay you if they'd left us anything."); continue; }
                world.AddCash(tr, -pay); world.Count("eco|burn|rescue rewards", pay);
                dedi.GiveCredits(pid, pay);
                SayTo(pid, tr.Name, $"You got them off us — thank you! {pay:N0} cr sent your way.");
                log($"sim: {tr.Id} '{tr.Name}' pays {pid} {pay} cr for the rescue (fight {f.Id})");
            }
            Save();
        }
        const int RescueMin = 500;
        readonly HashSet<string> rescuedBy = new HashSet<string>(StringComparer.Ordinal);

        void SayTo(int pid, string from, string text) =>
            api.Application.SendChatMessage(new MessageData
            {
                SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = pid, SenderNameOverride = from, Text = text,
            });

        void SayAll(string text)
        {
            foreach (var id in api.Application.GetPlayerEntityIds())
                api.Application.SendChatMessage(new MessageData
                {
                    SenderType = Eleon.SenderType.ServerInfo, Channel = Eleon.MsgChannel.SinglePlayer, RecipientEntityId = id,
                    SenderNameOverride = "LS", Text = text,
                });
        }

        void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                var tmp = path + ".tmp";
                File.WriteAllText(tmp, world.Serialize(), new UTF8Encoding(false));
                if (File.Exists(path)) File.Replace(tmp, path, path + ".bak"); else File.Move(tmp, path);
            }
            catch (Exception e) { log("sim: SAVE FAILED: " + e.Message); }
        }

        static World Load(string path, out int skipped)
        {
            skipped = 0;
            foreach (var p in new[] { path, path + ".bak" })
            {
                if (!File.Exists(p)) continue;
                try
                {
                    var w = World.Parse(File.ReadAllText(p, Encoding.UTF8), out skipped);
                    if (w.Agents.Count > 0 || skipped == 0) return w;
                }
                catch (IOException) { }
            }
            return new World();
        }
    }
}
