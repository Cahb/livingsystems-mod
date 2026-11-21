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
    sealed partial class DediSim
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
        /// <summary>Old hulls destroyed while their playfield may have been unloaded (a destroy there does nothing, the hull
        /// comes back with the playfield): destroyed again the next time that playfield reports in.</summary>
        readonly Dictionary<string, List<int>> ghostsLeft = new Dictionary<string, List<int>>();

        /// <summary>An old hull of ours goes: now, and again when <paramref name="pf"/> next comes back.</summary>
        void DestroyLeft(int id, string pf)
        {
            if (id <= 0 || !staleDone.Add(id)) return;
            dedi.Destroy(id);
            if (string.IsNullOrEmpty(pf)) return;
            if (!ghostsLeft.TryGetValue(pf, out var l)) ghostsLeft[pf] = l = new List<int>();
            if (!l.Contains(id)) l.Add(id);
        }
        readonly Dictionary<string, double> wreckSpawning = new Dictionary<string, double>();
        /// <summary>Live agents asked for but not spawned yet (agent → when asked); given up after a while.</summary>
        readonly Dictionary<string, double> asked = new Dictionary<string, double>();
        const string TraderPrefab = "CV_Freighter_SparrowA";   // fallback only (adopted live ships, tests)

        public DediSim(IModApi api, Action<string> log, DediSpike dedi)
        {
            this.api = api; this.log = log; this.dedi = dedi;
            World.TugShip = TugHull.Name;
            log($"sim: salvage tugs fly {World.TugShip}{(World.TugShip == World.TugOverride ? " (the server's own prefabs/LS_Tug.epb)" : " (vanilla; drop your own prefabs/LS_Tug.epb to change it)")}");
            path = Path.Combine(api.Application.GetPathFor(AppFolder.SaveGame), "Mods", "LivingSystems", "sim.txt");
            world = Load(path, out var skipped);
            // a fresh save starts EMPTY: the network grows from stations as players discover them (JoinNetwork + Populate) —
            // no hardcoded systems (playfield names differ per save)
            var now = Now;
            // live agents stay live across a logic reload / restart: their PfServer re-adopts them on simhello, and
            // Materialize sends them back to the sim if their playfield stays empty (after a grace period)
            startedAt = now;
            world.Config.TugFleet = true;   // tugs live at their yards; hires take a real one
            LoadPrices();
            world.Resume(now);
            if (world.Agents.Count == 0 && world.Config.Systems.Count > 0) { world.Seed(rng, now, 5, 2); world.LastTick = now; log("sim: new world seeded"); }
            if (world.Config.Systems.Count == 0) log("sim: fresh world — no trade network yet; it grows as players find stations");
            // every agent flies its own hull; a trader's guns follow its hull
            foreach (var a in world.Agents.Values.Where(x => x.Alive)) world.ShipOf(a, true);
            // marked live but never spawned when the Dedi went down: nobody will ever spawn it now — back to the sim
            foreach (var a in world.Agents.Values.Where(x => x.IsLive && x.EntityId == 0).ToList()) world.EndLive(a.Id, "reset", Now);
            Save();
            log($"sim: {world.Agents.Count} agent(s) in {world.Config.Systems.Count} system(s){(skipped > 0 ? $", {skipped} bad line(s) skipped" : "")}");
            // hires survive a reload: the menu gets them back (a call-in still on its way is called again)
            foreach (var a in world.Agents.Values.Where(x => x.IsTug && x.State == "hired").ToList())
                dedi.RestoreHire(World.HirePid(a), a.Name, World.HireEntity(a), World.HireFee(a), a.System, a.Extra.TryGetValue("tughome", out var th) ? th : "", World.HireOff(a));
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
        /// <summary>Factions whose bases are trading stations (Talon, Polaris, UCH, Civilian, Trader, Colonists).</summary>
        static readonly HashSet<int> TradingFactions = new HashSet<int> { 6, 7, 11, 15, 18, 19 };

        void OnGlobalList(GlobalStructureList gl)
        {
            if (gl?.globalStructures == null) return;
            var changed = new List<string>();
            foreach (var kv in gl.globalStructures)
            {
                var found = kv.Value.Where(g => g.type == 2 && !Factions.Hostile.Contains(g.factionGroup) && g.factionGroup != 0 && g.factionGroup != 1
                                               && g.factionGroup != HailDisplay.WreckFaction
                                               // a freshly generated playfield lists its bases with 0 blocks (not counted yet): a trading faction's
                                               // base then passes on its name — else a new system dropped its own shipyard, and every ship
                                               // docked there or bound for it vanished at once (Beta V Sector)
                                               && HailDisplay.StationRule(g.name, g.cntBlocks <= 0 && TradingFactions.Contains(g.factionGroup) ? 300 : g.cntBlocks))
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
            PayOwed();
            FollowHires();
            HireClocks();
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
            foreach (var a in world.Agents.Values.Where(x => x.Alive && x.System == pf && !(x.IsTug && (x.State == "salvage" || (x.State == "fight" && x.After == "salvage")))).OrderBy(x => x.Id, StringComparer.Ordinal))   // tugs at the job: below
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
                                        a.IsTug ? "salvage tug" : a.IsTrader ? World.RoleOf(a).Label : "pirate", a.IsTrader ? a.Cargo : a.Loot, a.IsTrader ? a.CargoAmt : a.LootMainAmt));
            }
            // salvage tugs at work: sitting at their target (field 12 = its wreck id, field 20 = the share cut so far); the
            // PfServer makes one live only beside its target's spawned hull. Their other legs stay offline for now.
            foreach (var a in world.Agents.Values.Where(x => x.IsTug && x.Alive && x.System == pf && (x.State == "salvage" || (x.State == "fight" && x.After == "salvage"))).OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var job = world.TugJobAny(a);
                if (job == null) continue;
                var tf = a.State == "fight" ? world.FightOf(a) : null;
                if (tf != null && tf.HasPos)
                {   // jumped at the job: the fight's place (it spawns there as a tug, pirates on it)
                    entries.Add(string.Join(";", a.Id, Clean(a.Name), "fight", N(tf.Pos.X), N(tf.Pos.Y), N(tf.Pos.Z), N(tf.Pos.X), N(tf.Pos.Y), N(tf.Pos.Z),
                                            N(Now), N(tf.Next), a.IsLive ? "1" : "0", Clean(job.Id), Clean(Where(pf, a.Extra.TryGetValue("tughome", out var fy) ? fy : pf)), a.Kind, a.Hull.ToString("0.##", inv),
                                            "salvage tug", a.Cargo, a.CargoAmt, "0", world.TugShare(a, Now).ToString("0.###", inv)));
                    continue;
                }
                if (a.State != "salvage") continue;
                entries.Add(string.Join(";", a.Id, Clean(a.Name), "salvage", N(job.Pos.X), N(job.Pos.Y), N(job.Pos.Z), N(job.Pos.X), N(job.Pos.Y), N(job.Pos.Z),
                                        N(Math.Max(Now, World.WorkStart(a))), N(a.Until), a.IsLive ? "1" : "0", Clean(job.Id), Clean(Where(pf, a.Extra.TryGetValue("tughome", out var yard) ? yard : pf)), a.Kind, a.Hull.ToString("0.##", inv),
                                        "salvage tug", a.Cargo, a.CargoAmt, "0", world.TugShare(a, Now).ToString("0.###", inv),
                                        a.Extra.TryGetValue(World.HoldKey, out var hold) ? hold : ""));   // field 21: its real hold (refills it if it goes live again)
            }
            // traders in warp toward here: on the band before they arrive — where they'll drop out, and when (chase them
            // through the jump: wait at their warp point)
            foreach (var a in world.Agents.Values.Where(x => x.IsTrader && x.Alive && x.State == "warp" && x.Dest == pf && !x.Extra.ContainsKey("via")))
            {
                if (!world.Arrival(a, out var wp, out var st)) continue;
                entries.Add(string.Join(";", a.Id, Clean(a.Name), "arriving", N(wp.X), N(wp.Y), N(wp.Z), N(wp.X), N(wp.Y), N(wp.Z),
                                        N(a.Until), N(a.Until), "0", Clean(Where(pf, a.System)), Clean(st.Name), a.Kind, a.Hull.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                                        a.IsTug ? "salvage tug" : World.RoleOf(a).Label, a.Cargo, a.CargoAmt));
            }
            // wrecks: distress beacons (and the ones already spawned); hub wrecks ("hubwreck" / "debris") have no beacon —
            // station field "swap:<entity>" = take that hull down while nobody's near (it respawns as what it should be)
            // field 19: a lootable hub wreck's ghost-ping chance per minute (the PfServer rolls it)
            foreach (var w in world.Wrecks.Values.Where(x => x.System == pf && !(x.Entity == 0 && x.NotBefore > Now)))
                entries.Add(string.Join(";", w.Id, Clean(w.Name), !w.IsHub ? "wreck" : w.Debris || w.Gone ? "debris" : "hubwreck", N(w.Pos.X), N(w.Pos.Y), N(w.Pos.Z), N(w.Pos.X), N(w.Pos.Y), N(w.Pos.Z),
                                        N(w.Time), N(Math.Min(w.Until, 1e12)), w.Entity != 0 ? "1" : "0", "", w.NeedsSwap ? (w.Decor && w.Gone ? "swapnow:" : "swap:") + w.Entity.ToString(inv) : "", "wreck", "0",
                                        w.Kind == "pirate" ? "pirate" : Core.Sim.World.Roles.FirstOrDefault(r => r.Id == w.Role)?.Label ?? "freighter", w.Cargo, w.Amt,
                                        world.GhostChance(w).ToString("0.####", inv)));
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
                // a hub broken in front of you: what's up for grabs, and the salvage tug that's coming for the rest
                if (fresh && here && (e.Kind == "broken" || e.Kind == "tugcall")) SayIn(e.System, e.Text);
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
                // the spool message names the jump vector: home (a lead to the hub) or a hunting ground (a raid warning)
                var home = world.HubOf(a) is Core.Sim.SimHub hub && hub.System == a.Dest;
                Send(a.Live, "simdepart", a.Id, Where(a.System, a.Dest) + (home ? " — heading home, by the course" : " — out hunting"));
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
                var box = world.Wrecks.Values.Where(x => x.Hub == hub.Id && !x.Debris && !x.Gone && x.Entity > 0 && x.Shown == "wreck").Where(x => !x.Centre && !x.Chest).OrderByDescending(x => x.Time).FirstOrDefault();   // the hauled wrecks: the listening post has no crates
                // the payout goes into the strongbox sack by the listening post (the loot into the newest hauled wreck)
                // (a strongbox not spawned yet keeps the payout waiting for it)
                var chestRec = world.Wrecks.Values.FirstOrDefault(x => x.Hub == hub.Id && x.Chest && !x.Gone);
                var chest = chestRec != null && chestRec.Entity > 0 ? chestRec : null;
                var sent = false;
                foreach (var to in new[] { chest, box }.Where(x => x != null).Distinct())
                {
                    var items = hub.Stash.Where(x => to == chest ? x.Item == Core.Sim.World.Strongbox : chestRec == null || x.Item != Core.Sim.World.Strongbox).GroupBy(x => x.Item).ToList();
                    if (items.Count == 0) continue;
                    var msg = new List<object> { "stash", to.Entity, hub.Id };
                    foreach (var s in items) { msg.Add(s.Key); msg.Add(s.Sum(x => x.Amt)); }
                    Send(hub.System, msg.ToArray()); sent = true;
                }
                if (sent) stashSent[hub.Id] = now;
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
            // a gang just broken with its post standing in front of a player: its core comes out now (loot / claimable);
            // nobody there → the swap respawns it stripped
            foreach (var w in world.Wrecks.Values.Where(x => x.Centre && !x.Post && x.Entity > 0 && x.Shown == "post" && pfs.Contains(x.System)))
            { Send(w.System, "stripcore", w.Entity); w.Shown = "wreck"; changed = true; log($"sim: {w.Name}'s listening post {w.Entity}: gang broken, core out"); }
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
                    if (m.IsTug && m.After == "salvage") { asked[m.Id] = now; changed = true; Send(f.Live, "tug", m.Id, m.Name, world.ShipOf(m)); }   // jumped at the job: it sits there and takes it
                    else if (m.IsTrader) { asked[m.Id] = now; changed = true; Send(f.Live, "trader", m.Id, m.Name, world.ShipOf(m), m.After == "out" ? Where(m.System, m.Dest) : "", MemoryLine(m), m.Cargo, m.CargoAmt, F(m.Speed), World.FormatHold(world.InventoryOf(m))); }
                    else if (target != null)
                    {
                        asked[m.Id] = now; changed = true;
                        Send(f.Live, "pirate", m.Id, m.Name, PiratePrefab(m), target.EntityId, F(f.Pos.X), F(f.Pos.Y), F(f.Pos.Z), m.Loot, m.LootMainAmt, MemoryLine(m), World.FormatHold(world.InventoryOf(m)));
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
            if (TryBody(f, playfield)) return true;
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            switch (f[0])
            {
                case "spill":   // spill|<name>|x|y|z|<hold>: one loot sack of what didn't fit a hull's crates
                {
                    if (f.Length < 6) return true;
                    float C(string v) => float.TryParse(v, System.Globalization.NumberStyles.Float, inv, out var x) ? x : 0f;
                    var hold = new Dictionary<string, int>(StringComparer.Ordinal);
                    foreach (var kv in World.ParseHold(f[5])) if (kv.Value > 0) hold[kv.Key] = (hold.TryGetValue(kv.Key, out var had) ? had : 0) + kv.Value;
                    var sack = world.AddSpill(playfield, new Core.Flight.Vec3(C(f[2]), C(f[3]), C(f[4])), f[1], hold, Now);
                    if (sack == null) return true;
                    Save();
                    log($"sim: spill {sack.Id} '{sack.Name}' in '{playfield}': {hold.Values.Sum()} item(s) in {hold.Count} line(s)");
                    SendTraffic(playfield);
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
                    // everything it carried goes on the record (a later tug gets it all); the hull keeps its own live cargo
                    var w = world.Wreck(a, new Core.Flight.Vec3(G(f[3]), G(f[4]), G(f[5])), world.TakeShare(a, 1), Now, rng);
                    if (w != null)
                    {
                        w.Entity = int.Parse(f[2], inv); Save(); log($"sim: live wreck {w.Id} '{a.Name}' = entity {w.Entity}");
                        SendBody(playfield, WreckAdopt(w, false));   // its PfServer tracks the hull as this record (its pocket / purse into its crates)
                    }
                    return true;
                }
                case "simrob":   // "simrob|pirate|wreck": a live pirate killed its prey and strips the hull — its share off the record
                {
                    if (f.Length < 3) return true;
                    var before = world.Events.Count;
                    var got = world.RobWreck(f[1], f[2], Now, rng);
                    foreach (var e in world.Events.Skip(before)) log("sim: " + e.Text);
                    if (got.Count > 0 && world.Wrecks.TryGetValue(f[2], out var rw) && rw.Entity > 0)
                        Send(playfield, "robbed", rw.Entity, World.FormatHold(got), BodyMsg.Clean(world.Get(f[1])?.Name ?? "the pirates"));
                    Save();
                    return true;
                }
                case "wreckcashed":   // "wreckcashed|wreck|put": the live hull's crates got its money (put < cash: no room)
                {
                    if (world.Wrecks.TryGetValue(f[1], out var w) && !w.CashAboard)
                    {
                        int.TryParse(f.Length >= 3 ? f[2] : "0", out var put);
                        w.CashAboard = true; Save();
                        log($"sim: wreck {w.Id} '{w.Name}': {put:N0} of {w.Cash:N0} cr into its crates");
                    }
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
                case "hirehold":   // "hirehold|pid|entity|Item:N,…" → the trade window on it
                    if (f.Length >= 4 && int.TryParse(f[1], out var hh) && int.TryParse(f[2], out var he)) dedi.OpenHiredHold(hh, he, f[3]);
                    return true;
                case "hiredone":   // "hiredone|pid|Item:N,…" → dismissed: the player's share of the haul
                    if (f.Length >= 2 && int.TryParse(f[1], out var hd)) dedi.HireDone(hd, f.Length > 2 ? f[2] : "");
                    return true;
                case "hirestat":   // "hirestat|pid|text": the hired tug's status line for the menu
                    if (f.Length >= 3 && int.TryParse(f[1], out var hsp)) dedi.HireStat(hsp, string.Join("|", f.Skip(2)));
                    return true;
                case "hirecash":   // "hirecash|pid|credits": money a hired tug found — the player's, in full
                    if (f.Length >= 3 && int.TryParse(f[1], out var hcp) && int.TryParse(f[2], out var hcn)) dedi.HireCash(hcp, hcn);
                    return true;
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
                        var post = !debris && wr.Centre && wr.Post;   // an active gang's post: theirs, core in
                        var mode = debris ? "debris" : post ? "post" : wr.Chest ? "chest" : wr.Spill ? "spill" : wr.IsHub ? "hub" : "";   // chest: the strongbox — the stash only; spill: its own lot only
                        wr.Entity = -1;   // spawning
                        wr.Shown = debris ? "debris" : post ? "post" : "wreck";
                        // adrift: pitched and rolled at a wrong angle, not parked level; NO faction (255): spawned as Polaris,
                        // looting a faction-owned hull costs that faction's rep (vanilla)
                        dedi.Spawn(wpf, prefab, new PVector3(wr.Pos.X, wr.Pos.Y, wr.Pos.Z), new PVector3(rng.Next(-60, 61), rng.Next(360), rng.Next(-90, 91)), StructureTag.Apply(wr.Centre ? $"{wr.Name} ({(world.Hubs.TryGetValue(wr.Hub, out var ch) ? world.HubRank(ch) : "camp")})" : wr.Decor ? "Debris" : wr.Chest ? "Pirate strongbox" : wr.Spill ? wr.Name : "Wreck of " + wr.Name), post ? SpikeCommand.FactionGroup("pirates") : (byte)255, id =>
                        {
                            wr.Entity = id; Save();
                            SendBody(wpf, WreckFill(wr, id, debris, Core.Sim.World.IsPrewrecked(prefab) || wr.IsHub || wr.Spill, mode));
                            log($"sim: wreck {wr.Id} '{wr.Name}' spawned as {id} in '{wpf}' — {prefab} ({(wr.Looted ? "picked over" : "whole")}, was {(wr.Ship.Length > 0 ? wr.Ship : "?")}; {wr.Amt} {wr.Cargo})");
                        });
                        return true;
                    }
                    var a = world.Get(f[1]);
                    var pf = playfield;
                    if (a == null || !a.Alive || a.IsLive || a.System != pf) return true;
                    if (a.IsTug && a.State == "salvage")
                    {   // a salvage tug at work: the PfServer asks only once its target's hull is there
                        var job = world.TugJob(a);
                        if (job == null || job.Entity <= 0) return true;
                        if (a.EntityId > 0 && staleDone.Add(a.EntityId)) dedi.Destroy(a.EntityId);
                        a.Live = pf; a.EntityId = 0; asked[a.Id] = Now;
                        Send(pf, "tug", a.Id, a.Name, world.ShipOf(a));
                        log($"sim: tug {a.Id} '{a.Name}' goes live in '{pf}' on {job.Id} '{job.Name}' ({world.TugShare(a, Now):P0} cut)");
                        Save();
                        return true;
                    }
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
                        Send(pf, "pirate", a.Id, a.Name, PiratePrefab(a), 0, F(spot.A.X), F(spot.A.Y), F(spot.A.Z), a.Loot, a.LootMainAmt, MemoryLine(a), World.FormatHold(world.InventoryOf(a)));   // every loot line aboard (field 11)
                        log($"sim: lurking pirate {a.Id} '{a.Name}' goes live in '{pf}'");
                        Save();
                        return true;
                    }
                    if (!a.OnLeg && a.State != "dock" && a.State != "pass") return true;   // docked ones too: parked at their berth
                    if (a.EntityId > 0 && staleDone.Add(a.EntityId)) { dedi.Destroy(a.EntityId); log($"sim: old copy {a.EntityId} of {a.Id} destroyed before going live again"); }
                    a.Live = pf; a.EntityId = 0; asked[a.Id] = Now;
                    Send(pf, "trader", a.Id, a.Name, world.ShipOf(a), a.State == "out" || a.State == "pass" ? Where(a.System, a.Dest) : "", MemoryLine(a), a.Cargo, a.CargoAmt, F(a.Speed), World.FormatHold(world.InventoryOf(a)));   // everything aboard (field 9)
                    log($"sim: {a.Id} '{a.Name}' goes live in '{pf}' ({a.State} lane, {world.StationOf(a)?.Name})");
                    Save();
                    return true;
                }
                case "tugcut":   // a live tug finished a target ("tugcut|agent|wreck|1") or filled its hold part-way (share < 1)
                {
                    var a = world.Get(f[1]);
                    if (a == null || f.Length < 4) return true;
                    var share = double.TryParse(f[3], System.Globalization.NumberStyles.Float, inv, out var sh) ? sh : 1;
                    var before = world.Events.Count;
                    var more = world.TugCutLive(a.Id, f[2], share, Now);
                    foreach (var e in world.Events.Skip(before)) log("sim: " + e.Text);
                    log($"sim: tug {a.Id} '{a.Name}' cut {f[2]} live ({share:P0}) — {(more ? "next: " + world.TugJob(a)?.Id : "heading home")}");
                    if (!more && a.IsLive) Send(a.Live, "tugleave", a.Id, Where(a.System, a.Extra.TryGetValue("tughome", out var th) ? th : ""));
                    Save(); SendTraffic(a.System);
                    return true;
                }
                case "simhello":
                {
                    // a PfServer (re)started on this playfield: hand it its live agents back; spawns that never landed
                    // go back to the sim; old copies of agents that aren't live any more are destroyed (orphans)
                    var pf = f[1].Length > 0 ? f[1] : playfield;
                    nextGlobal = Math.Min(nextGlobal, Now + 20);   // a playfield just started (maybe generated just now): learn its stations
                    if (JoinNetwork(pf)) Save();
                    // a ship marked live here that never spawned: back to the sim (the rest are adopted as bodies below)
                    foreach (var a in world.Agents.Values.Where(x => x.Live == pf && x.EntityId == 0).ToList()) { world.EndLive(a.Id, "reset", Now); asked.Remove(a.Id); }
                    AdoptBodies(pf);   // live ships, hired tugs (every kind on bodies): the PfServer picks them up again
                    foreach (var a in world.Agents.Values.Where(x => !x.IsLive && x.EntityId > 0 && x.State != "dead" && !staleDone.Contains(x.EntityId)).ToList())
                    { staleDone.Add(a.EntityId); dedi.Destroy(a.EntityId); log($"sim: stale copy {a.EntityId} of {a.Id} '{a.Name}' destroyed"); }
                    Save();
                    SendTraffic(pf);
                    Send(pf, "hellodone", pf);   // the PfServer asks until it sees this (an early hello's answers can be lost)
                    return true;
                }
            }
            return false;
        }

        /// <summary>
        /// Hail → Talk: the <paramref name="n"/>-th thing this ship's crew has to say (rumours from the sim), or null if it's
        /// not a sim ship. Each "ask around" moves to the next one; wraps.
        /// </summary>
        /// <summary>A station that can send a tug to a player: where, and how far (LY; 0 = this star system).</summary>
        public sealed class TugOffer { public string Station = "", System = ""; public double Ly; public int Idle = 1; }

        /// <summary>
        /// Services → Call for a tug: stations with a yard (trading stations, shipyards, repair docks) within
        /// <paramref name="rangeLy"/> of the player's playfield, nearest first, one per system.
        /// </summary>
        public List<TugOffer> TugOffers(string playfield, double rangeLy = 60)
        {
            var res = new List<TugOffer>();
            foreach (var kv in world.Stations)
            {
                var yard = kv.Value.FirstOrDefault(x => Core.Sim.LiveRules.IsYard(x.Name));
                if (yard == null) continue;
                var ly = kv.Key == playfield ? 0 : world.Galaxy != null ? world.Galaxy.Distance(playfield, kv.Key) : double.PositiveInfinity;
                if (double.IsNaN(ly) || ly > rangeLy) continue;
                if (world.Config.TugFleet && !world.TugHomes().Contains(kv.Key)) continue;
                res.Add(new TugOffer { Station = yard.Name, System = kv.Key, Ly = ly, Idle = world.Config.TugFleet ? world.IdleAt(kv.Key).Count : 1 });
            }
            return res.OrderBy(o => o.Ly).ThenBy(o => o.System, StringComparer.Ordinal).Take(5).ToList();
        }

        /// <summary>Salvaged parts priced by the scenario's ItemsConfig MarketPrice (our own table keeps its tuned goods).</summary>
        void LoadPrices()
        {
            {
                try
                {
                    var dir = Recipes.FindConfigDir(api.Application.GetPathFor(AppFolder.Content), api.Application.GetPathFor(AppFolder.SaveGame));
                    if (!File.Exists(Path.Combine(dir, "ItemsConfig.ecf"))) { log($"sim: no ItemsConfig in {dir}"); return; }
                    var r = Recipes.Parse(File.ReadAllText(Path.Combine(dir, "BlocksConfig.ecf")), "", File.ReadAllText(Path.Combine(dir, "ItemsConfig.ecf")));
                    World.PriceFallback = r.Price;
                    World.StackSizeOf = r.StackSize;
                    log($"sim: item prices from {dir} ({r.ItemCount} items; Motor {r.Price("Motor"):0} cr, stacks of {r.StackSize("SteelPlate")} plates)");
                    return;
                }
                catch (Exception ex) { log($"sim: reading item prices threw {ex.Message}"); }
            }
            log("sim: no ItemsConfig found — unknown items price at 1 cr");
        }

        /// <summary>A hire: the tug's name, and its hull when it was docked live right there (it flies over; nothing spawns).</summary>
        public sealed class HireResult { public string Name = ""; public int Entity; }

        /// <summary>A player hires a tug from <paramref name="home"/>'s yard; null if none is free there.</summary>
        public HireResult HireTug(string home, int pid, string playfield, int fee)
        {
            if (!world.Config.TugFleet) return new HireResult();   // the old way: a tug made for the hire (the menu names it)
            var a = world.HireTug(home, pid, playfield, fee, Now);
            if (a == null) return null;
            var hull = World.HireEntity(a);
            Save(); log($"sim: {a.Id} '{a.Name}' hired by {pid} from {home} for '{playfield}' ({fee} cr){(hull > 0 ? $", its live hull {hull} undocks" : "")}");
            if (hull > 0) Send(playfield, "hiretake", pid, a.Id, hull, a.Name.Replace(' ', '_'), home);
            return new HireResult { Name = a.Name, Entity = hull };
        }

        /// <summary>What the sim owes players who are online now (a dismissed tug's share, once it sold): wired, with why.</summary>
        void PayOwed()
        {
            if (world.Owed.Count == 0) return;
            List<int> online; try { online = (api.Application.GetPlayerEntityIds() ?? new List<int>()).ToList(); } catch { return; }
            foreach (var pid in world.Owed.Keys.Where(online.Contains).ToList())
            {
                var o = world.Owed[pid];
                world.Owed.Remove(pid);
                dedi.PayOwedTo(pid, o.Credits, o.Note);
                log($"sim: paid {pid} {o.Credits} cr owed ({o.Note})");
            }
            Save();
        }

        /// <summary>A hire ends: "dismiss" (home to sell <paramref name="manifest"/>; the player's <paramref name="payShare"/>
        /// is paid when it sells), "release" (never got there), "lost". False = no sim tug (the caller pays at once).</summary>
        public bool HireEnd(int pid, string how, string manifest, double payShare)
        {
            var a = world.HiredBy(pid);
            if (a == null) return false;
            world.HireEnds(a, how, manifest, Now, payShare); Save();
            if (how == "dismiss" && a.IsLive) Send(a.Live, "hiregone", a.Id, a.EntityId, a.Dest);   // the PfServer flies it out as a leaving ship
            log($"sim: hire of {a.Id} '{a.Name}' by {pid} ends ({how})");
            return how == "dismiss";
        }

        /// <summary>
        /// Hail → Talk: what this crew knows, by topic, its own request first ("What we need"). Null = not a sim ship;
        /// empty = nothing to tell (or a pirate, who doesn't chat).
        /// </summary>
        public List<KeyValuePair<string, List<string>>> TalkTopics(int entityId, int pid)
        {
            var a = world.ByEntity(entityId);
            if (a == null) return null;
            var res = new List<KeyValuePair<string, List<string>>>();
            if (!a.IsTrader) return res;
            if (world.WantOf(a) is World.Want w)
                res.Add(new KeyValuePair<string, List<string>>("What we need", new List<string> { $"{w.Why} We'd pay {w.Price:N0} a piece for {World.Humanize(w.Item)} — up to {w.Left}." }));
            foreach (var g in world.RumourTopics(a, PlayerKeys(pid), Now, Where).GroupBy(r => r.Topic))
                res.Add(new KeyValuePair<string, List<string>>(g.Key, g.Select(r => r.Text).ToList()));
            return res;
        }

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
            if (a == null || !a.IsTrader || a.IsTug || !a.Alive) return null;   // a tug is kind "trader" in the sim but sells nothing
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
            if (a == null || !a.IsTrader || a.IsTug || !a.Alive) return null;   // a tug is kind "trader" in the sim but sells nothing
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
                case "tug":   // test: a salvage tug at work on the wrecks in the caller's system right now (goes live beside its target)
                {
                    var pf = api.Application.GetPlayerDataFor(pid)?.PlayfieldName ?? "";
                    var tug = world.TugHere(pf, Now);
                    if (tug == null) { say(pid, "sim: no wreck here a tug could take (try /ls sim wreck first)"); return; }
                    Save(); SendTraffic(pf);
                    say(pid, $"sim: {tug.Name} is at work on {world.TugJob(tug)?.Name} ({world.Wrecks.Values.Count(x => x.Tug == tug.Id)} on its list) — fly to the wreck: /ls fly where");
                    return;
                }
                case "hireclock":   // test: wind your hire's share clock to <minutes> (a step test can't wait 2 h)
                {
                    var a = world.HiredBy(pid);
                    if (a == null) { say(pid, "sim: no hire of yours"); return; }
                    if (double.TryParse(cmd.Arg(1, ""), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mins) && mins >= 0)
                    { a.Extra["hireclock"] = (mins * 60).ToString("R", System.Globalization.CultureInfo.InvariantCulture); a.Extra.Remove("hirestep"); Save(); }
                    var line = HireClockLine(pid);
                    log($"sim: hireclock {a.Id} '{a.Name}' of {pid}: {World.HireClock(a) / 60:0.0} min, you'd get {world.HirePayout(a):P0} — {line}");
                    say(pid, $"sim: {a.Name}: {World.HireClock(a) / 60:0} min on hire — you'd get {world.HirePayout(a):P0}; {line}");
                    return;
                }
                case "spillclear":   // test / admin: every spill sack gone (records, hulls; the playfields forget them)
                {
                    var sacks = world.Wrecks.Values.Where(x => x.Spill).ToList();
                    foreach (var w in sacks)
                    {
                        world.Wrecks.Remove(w.Id);
                        SendBody(w.System, BodyMsg.Drop("wreck", w.Id));
                        if (w.Entity > 0 && staleDone.Add(w.Entity)) dedi.Destroy(w.Entity);
                    }
                    Save();
                    log($"sim: spillclear — {sacks.Count} spill sack(s) gone");
                    say(pid, $"sim: {sacks.Count} spill sack(s) cleared");
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
                    say(pid, $"sim: wreck {w.Id} '{w.Name}' ({World.RoleOf(dummy).Label}, {w.Amt} {World.Humanize(w.Cargo)}) {where} at {w.Pos.X:0},{w.Pos.Y:0},{w.Pos.Z:0} — /ls fly where");
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
        public void Heartbeat(string playfield)
        {
            if (string.IsNullOrEmpty(playfield)) return;
            var back = !heartbeat.TryGetValue(playfield, out var last) || Now - last > 60;
            heartbeat[playfield] = Now;
            if (!back || !ghostsLeft.TryGetValue(playfield, out var left)) return;
            ghostsLeft.Remove(playfield);
            foreach (var id in left) dedi.Destroy(id);
            log($"sim: '{playfield}' back — {left.Count} hull(s) left behind there destroyed again ({string.Join(", ", left)})");
        }

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

        /// <summary>Chat to every player in one playfield.</summary>
        void SayIn(string pf, string text)
        {
            foreach (var id in api.Application.GetPlayerEntityIds())
                if (api.Application.GetPlayerDataFor(id)?.PlayfieldName == pf) SayTo(id, "LS", text);
        }

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
