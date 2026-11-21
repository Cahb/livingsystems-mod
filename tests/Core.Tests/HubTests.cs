using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Sim;
using Xunit;

public class HubTests
{
    internal const string Yaml = @"SolarSystems:
  - Name: Alpha
    Coordinates: [0, 0, 0]
    StarClass: G
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Alpha Station, EdenSpaceG_PolTradeStation, '']
          - ['0,0,0', Alpha Belt, EdenSpaceD_AsteroidT1, '']
          - ['0,0,0', Alpha Pirate Orbit, EdenSpaceG_OrbitGasPirate, '']
          - ['0,0,0', Alpha Moon, EdenMoonBarren, '']
          - ['0,0,0', 'Alpha [Sun Right]', SpaceWarpTargetFixed, '']
  - Name: Beta
    Coordinates: [20, 0, 0]
    StarClass: K
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Beta Station, EdenSpaceG_PolTradeStation, '']
          - ['0,0,0', Beta Orbit, EdenSpaceD_Orbit, '']
  - Name: Gamma
    Coordinates: [40, 0, 0]
    StarClass: K
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Gamma, SunRandom, '']
          - ['0,0,0', 'Gamma [Sun Left]', SpaceWarpTarget, '']
";

    static World Make()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { Yaml }) };
        w.Config.Systems = new List<string> { "Alpha Station", "Beta Station" };
        w.SetStations("Alpha Station", new List<SimStation> { new SimStation { Id = 1, Name = "Lambda", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.SetStations("Beta Station", new List<SimStation> { new SimStation { Id = 2, Name = "Kappa", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        return w;
    }

    [Fact]
    public void Hideouts_are_open_space_without_stations_pirate_orbits_first()
    {
        var g = Galaxy.Parse(new[] { Yaml });
        Assert.Equal("EdenSpaceD_AsteroidT1", g.Types["Alpha Belt"]);
        Assert.Equal(3, g.HideoutScore("Alpha Pirate Orbit"));
        Assert.Equal(2, g.HideoutScore("Alpha Belt"));
        Assert.Equal(1, g.HideoutScore("Beta Orbit"));
        Assert.Equal(0, g.HideoutScore("Alpha Moon"));
        Assert.Equal(0, g.HideoutScore("Alpha [Sun Right]"));
        Assert.Equal(0, g.HideoutScore("Alpha Station"));
    }

    [Fact]
    public void A_star_is_rolled_once_ever_and_a_pirate_orbit_always_gets_the_hub()
    {
        var w = Make();
        var made = w.DiscoverHubs(w.Galaxy.Stars.Values, 1000);
        var alpha = Assert.Single(made, h => w.Galaxy.StarOf(h.System).Name == "Alpha");
        Assert.Equal("Alpha Pirate Orbit", alpha.System);
        Assert.InRange(alpha.Size, 3, 5);
        Assert.DoesNotContain(made, h => w.Galaxy.StarOf(h.System).Name == "Gamma");   // nowhere to hide
        Assert.Equal(3, w.RolledStars.Count);
        Assert.Empty(w.DiscoverHubs(w.Galaxy.Stars.Values, 2000));                     // rolled already: never again
        // deterministic: a fresh world rolls the same hubs at the same spots
        var fresh = Make(); var again = fresh.DiscoverHubs(fresh.Galaxy.Stars.Values, 5);
        Assert.Equal(made.Select(h => h.System + h.Pos.X), again.Select(h => h.System + h.Pos.X));
        // and they survive a save
        var back = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(w.Hubs.Count, back.Hubs.Count);
        Assert.Equal(alpha.Name, back.Hubs[alpha.Id].Name);
        Assert.Equal(3, back.RolledStars.Count);
    }

    [Fact]
    public void Hub_crews_chill_at_home_raid_and_come_back_and_are_never_recycled()
    {
        var w = Make(); var rng = new Random(5);
        w.Config.MoneyFlow = false; w.Config.HubWealth = false;   // the routine at a fixed size (wealth would grow the gang)
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First(h => h.System == "Alpha Pirate Orbit");
        w.Focus = new List<string> { "Alpha Station", "Beta Station", hub.System };
        var added = w.Populate(rng, 0);
        var crew = added.Where(a => a.Home == hub.Id).ToList();
        Assert.Equal(hub.Size, crew.Count);
        Assert.All(crew, a => { Assert.Equal("home", a.State); Assert.Equal(hub.System, a.System); });
        Assert.DoesNotContain(added, a => a.IsTrader && a.System == hub.System);   // no traffic at a hideout
        Assert.True(w.SpotOf(crew[0], out var spot));
        Assert.InRange((spot - hub.Pos).Length, 149, 331);                          // parked at the hub
        Assert.True(w.Track(crew[0], out _));                                      // on the traffic list → spawns live when a player comes near
        // budget can't touch them, even far from every player
        w.Config.PirateBudget = 0; w.Focus = new List<string>();
        w.Recycle(s => 1000);
        Assert.All(crew, a => Assert.NotNull(w.Get(a.Id)));
        // over a couple of days the roamers go out and come back; two always stay on guard
        w.Config.PatrolChance = 0; w.Config.LurkChase = 0;   // nobody dies: this is about the routine
        var states = crew.ToDictionary(c => c.Id, c => new HashSet<string>()); var homeAgain = 0; var minHome = 99;
        var was = crew.ToDictionary(c => c.Id, c => c.State);
        for (var t = 15.0; t < 48 * 3600; t += 15)
        {
            w.Step(t, rng);
            foreach (var c in crew) { states[c.Id].Add(c.State); if (c.State == "home" && was[c.Id] != "home") homeAgain++; was[c.Id] = c.State; }
            minHome = Math.Min(minHome, crew.Count(c => c.State == "home" || (c.State == "fight" && c.After == "home")));   // fighting off a passer at the hub = still on guard
        }
        Assert.Contains(crew, c => states[c.Id].Contains("lurk"));
        Assert.True(homeAgain >= 1, "nobody ever came home");
        Assert.True(minHome >= Math.Min(2, hub.Size - 1), $"guard dropped to {minHome}");
        // a populated hub stays at its size; losses come back ONE recruit at a time, hours apart
        w.Focus = new List<string> { hub.System };
        w.Populate(rng, 48 * 3600);
        Assert.Equal(hub.Size, w.Agents.Values.Count(a => a.Alive && a.Home == hub.Id));
        crew[0].State = "dead"; crew[1].State = "dead"; w.Recycle(s => 0);
        var t0 = 48 * 3600.0;
        Assert.Empty(w.Populate(rng, t0 + 60).Where(a => a.Home == hub.Id));       // not the next minute…
        Assert.Single(w.Populate(rng, t0 + 6.1 * 3600), a => a.Home == hub.Id);    // …one after ~6 h
        Assert.Empty(w.Populate(rng, t0 + 6.2 * 3600).Where(a => a.Home == hub.Id));
        Assert.Single(w.Populate(rng, t0 + 19 * 3600), a => a.Home == hub.Id);     // the next one 6–12 h after that
    }

    [Fact]
    public void A_hurt_hub_pirate_limps_home_to_repair()
    {
        var w = Make(); var rng = new Random(1);
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First(h => h.System == "Alpha Pirate Orbit");
        var p = w.Add(new SimAgent { Id = "p1", Kind = "pirate", Name = "Rust Jaw", System = "Beta Station", State = "repair", Until = 10, Hull = 0.2, Home = hub.Id });
        w.Step(11, rng);
        Assert.Equal("warp", p.State);
        Assert.Equal(hub.System, p.Dest);
        w.Step(p.Until + 1, rng);
        Assert.Equal("home", p.State);
        Assert.Equal(hub.System, p.System);
        Assert.Contains("chilling at", w.Describe(p));
        // hurt again right at the hub: it patches up at home (on guard, visible), not as an invisible "repair"
        p.State = "repair"; p.Until = p.Until + 10;
        w.Step(p.Until + 1, rng);
        Assert.Equal("home", p.State);
    }

    [Fact]
    public void A_killed_crewmate_is_remembered_by_the_whole_hub_recruits_too_and_survives_a_save()
    {
        var w = Make(); var rng = new Random(2);
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First(h => h.System == "Alpha Pirate Orbit");
        w.Focus = new List<string> { hub.System };
        var crew = w.Populate(rng, 0).Where(a => a.Home == hub.Id).ToList();
        var dead = crew[0]; var mate = crew[1];
        w.Remember(dead, "memory.killed.pid:1188", dead.Name);
        w.Remember(mate, "memory.foiled.pid:1188", "Kestrel");
        Assert.Contains(w.MemoriesOf(mate), kv => kv.Key == "memory.killed.pid:1188" && kv.Value == dead.Name);   // the crew's grudge
        Assert.Contains(w.MemoriesOf(mate), kv => kv.Key == "memory.foiled.pid:1188");                          // its own
        Assert.DoesNotContain(w.MemoriesOf(crew.Last() == mate ? crew[0] : crew.Last()), kv => kv.Key == "memory.foiled.pid:1188");
        // the dead one is replaced by a recruit: it knows too
        dead.State = "dead"; w.Recycle(s => 0);
        var recruit = w.Populate(rng, 7 * 3600).Single(a => a.Home == hub.Id);
        Assert.Contains(w.MemoriesOf(recruit), kv => kv.Key == "memory.killed.pid:1188");
        var back = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(dead.Name, back.Hubs[hub.Id].Memory["memory.killed.pid:1188"]);
        Assert.Equal("Kestrel", back.Get(mate.Id).Extra["memory.foiled.pid:1188"]);
    }

    static SimWreck Beacon(World w, string id, string by, double until, int entity = 0) =>
        w.Wrecks[id] = new SimWreck { Id = id, System = "Beta Station", Name = "Hulk " + id, Cargo = "IronOre", Amt = 100, Time = until - 1500, Until = until, By = by, Entity = entity, Ship = "CV_Freighter_SparrowA" };

    [Fact]
    public void An_unclaimed_wreck_is_hauled_to_the_winners_hub_others_are_towed_spawned_ones_stay()
    {
        var w = Make(); var rng = new Random(3);
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First(h => h.System == "Alpha Pirate Orbit");
        var won = Beacon(w, "w1", hub.Id, 100);
        Beacon(w, "w2", "", 100);
        var spawned = Beacon(w, "w3", hub.Id, 100, entity: 4242);
        var ev = w.Step(200, rng);
        Assert.Equal(hub.System, won.System);
        Assert.True(won.IsHub && won.Looted);
        Assert.Equal(World.Never, won.Until);
        Assert.InRange((won.Pos - hub.Pos).Length, 119, 371);   // a pile by the hub
        Assert.Contains(ev, e => e.Kind == "hauled" && e.Text.Contains(hub.Name) && e.Text.Contains("Hulk w1"));
        Assert.False(w.Wrecks.ContainsKey("w2"));                    // no winners' hub: towed
        Assert.Contains(ev, e => e.Kind == "towed");
        Assert.Equal("Beta Station", spawned.System);                // a hull already out there stays put
        Assert.Contains(World.Wreckage.Concat(new[] { "Eden_Wrecked Horizon" }), x => x == World.WreckPrefab(won));   // picked over
        // years later it's still there, and it survives a save
        w.Step(500 * 3600, rng);
        var back = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(hub.Id, back.Wrecks["w1"].Hub);
        Assert.Equal(hub.Id, back.Wrecks["w3"].By);
        // news: a hauled wreck isn't a beacon, but the haul is talked about
        var teller = w.Add(new SimAgent { Id = "t9", Kind = "trader", Name = "Gossip", System = "Alpha Station", State = "dock", Until = 1e9 });
        var talk = w.Rumours(teller, "pid:1", 300, null);
        Assert.Contains(talk, r => r.Contains("dragged the Hulk w1"));
        Assert.DoesNotContain(talk, r => r.Contains("Beacon's still up") && r.Contains("Hulk w1"));
    }

    [Fact]
    public void A_hub_keeps_six_lootable_wrecks_ten_debris_and_spawned_hulls_are_swapped_not_dropped()
    {
        var w = Make(); var rng = new Random(4);
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First(h => h.System == "Alpha Pirate Orbit");
        var first = Beacon(w, "w0", hub.Id, 10);
        w.Step(11, rng);
        first.Entity = 777; first.Shown = "wreck";                   // a player came by: its hull is out there now
        for (var i = 1; i < 18; i++) { Beacon(w, "w" + i, hub.Id, 100 + i * 10); w.Step(101 + i * 10, rng); }
        var mine = w.Wrecks.Values.Where(x => x.Hub == hub.Id && !x.Centre && !x.Decor).ToList();   // hauled hulls only (not the post, not the junk)
        Assert.Equal(6, mine.Count(x => !x.Debris && !x.Gone));
        Assert.Equal(10, mine.Count(x => x.Debris && !x.Gone));
        Assert.All(mine.Where(x => x.Debris), x => Assert.Equal(0, x.Amt));   // debris loses its contents
        // the oldest went over both caps: unspawned ones are simply gone, the spawned one waits for its hull to come down
        Assert.True(first.Gone && first.NeedsSwap);
        Assert.Equal(17, mine.Count);
        w.HubHullDown(first);
        Assert.False(w.Wrecks.ContainsKey("w0"));
        // a spawned lootable wreck turned debris: swapped, then respawns as debris
        var d = mine.First(x => x.Debris && !x.Gone);
        d.Entity = 900; d.Shown = "wreck";
        Assert.True(d.NeedsSwap);
        w.HubHullDown(d);
        Assert.Equal(0, d.Entity);
        Assert.True(w.Wrecks.ContainsKey(d.Id));
        d.Entity = 901; d.Shown = "debris";
        Assert.False(d.NeedsSwap);
        Assert.Contains(World.DebrisPrefab(d), World.DebrisHulls);
    }

    [Fact]
    public void A_crew_home_with_loot_stashes_it_oldest_fenced_over_the_cap_and_it_survives_a_save()
    {
        var w = Make(); var rng = new Random(6);
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First(h => h.System == "Alpha Pirate Orbit");
        var p = w.Add(new SimAgent { Id = "p1", Kind = "pirate", Name = "Rust Jaw", System = "Beta Station", State = "warp", Dest = hub.System, Until = 10, Home = hub.Id, LootUntil = 1e6 });
        p.AddLoot("IronOre", 300);
        w.Step(11, rng);
        Assert.Equal("home", p.State);
        Assert.Equal(0, p.LootAmt);
        var s = Assert.Single(hub.Stash);
        Assert.Equal("IronOre", s.Item); Assert.Equal(300, s.Amt);
        w.Unstash(hub, "IronOre", 120);
        Assert.Equal(180, hub.Stash[0].Amt);
        var back = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(180, back.Hubs[hub.Id].Stash.Single().Amt);
        // way over the cap: the oldest goes to a fence
        w.Config.StashCap = World.StashValue(hub) + 1;
        p.AddLoot("GoldIngot", 50);
        w.Stash(p, hub, 20);
        Assert.Equal("GoldIngot", Assert.Single(hub.Stash).Item);
        Assert.Contains(w.Events, e => e.Kind == "fenced" && e.Text.Contains(hub.Name));
        w.Unstash(hub, "GoldIngot", 999);
        Assert.Empty(hub.Stash);
    }

    [Fact]
    public void Over_two_days_hub_crews_win_fights_haul_wrecks_home_and_pile_up_loot()
    {
        var w = Make(); var rng = new Random(11);
        foreach (var h in w.DiscoverHubs(w.Galaxy.Stars.Values, 0)) { }
        w.Focus = new List<string> { "Alpha Station", "Beta Station" }.Concat(w.Hubs.Values.Select(h => h.System)).Distinct().ToList();
        w.Config.PatrolChance = 0;
        w.Populate(rng, 0);
        int hauled = 0, towed = 0, stashed = 0, fenced = 0, wrecks = 0;
        for (var t = 60.0; t < 48 * 3600; t += 60)
        {
            if (t % 3600 < 60) w.Populate(rng, t);
            foreach (var e in w.Step(t, rng))
                switch (e.Kind) { case "hauled": hauled++; break; case "towed": towed++; break; case "stash": stashed++; break; case "fenced": fenced++; break; case "wreck": wrecks++; break; }
        }
        var piles = string.Join(", ", w.Hubs.Values.Select(h => $"{h.Name}: {w.Wrecks.Values.Count(x => x.Hub == h.Id && !x.Debris)} lootable, {w.Wrecks.Values.Count(x => x.Hub == h.Id && x.Debris)} debris, stash {World.StashValue(h):0} cr"));
        foreach (var h in w.Hubs.Values)
            Console.WriteLine($"stash {h.Name}: " + string.Join(", ", h.Stash.Select(x => $"{x.Amt} {x.Item} ({x.Amt * (World.Prices.TryGetValue(x.Item, out var v) ? v : 1):0} cr, {(48 * 3600 - x.Time) / 3600:0.0} h old)")));
        Console.WriteLine($"48 h: wrecks {wrecks}, hauled {hauled}, towed {towed}, stashed {stashed}, fenced {fenced} — {piles}");
        Assert.True(hauled > 0, $"nothing hauled ({piles})");
        Assert.True(stashed > 0, "no loot ever came home");
        Assert.All(w.Hubs.Values, h => Assert.True(World.StashValue(h) <= w.Config.StashCap || h.Stash.Count == 1));
        Assert.All(w.Hubs.Values, h => Assert.True(w.Wrecks.Values.Count(x => x.Hub == h.Id && !x.Debris && !x.Centre) <= w.Config.HubWrecks));   // the centrepiece is extra
    }
}

public class HubSplitTests
{
    [Fact]
    public void A_hub_rich_for_a_day_sends_a_lieutenant_to_found_one_near_its_hunting_ground()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { HubTests.Yaml }) };
        w.Config.Systems = new List<string> { "Alpha Station", "Beta Station" };
        w.SetStations("Alpha Station", new List<SimStation> { new SimStation { Id = 1, Name = "Lambda" } });
        w.SetStations("Beta Station", new List<SimStation> { new SimStation { Id = 2, Name = "Kappa" } });
        w.Config.HubSplit = true; w.Config.StarsPerHub = 0.5;
        var mother = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First();
        foreach (var other in w.Hubs.Values.Where(h => h != mother).ToList()) w.Hubs.Remove(other.Id);
        mother.Cash = 400000; mother.Manned = true;
        for (var i = 0; i < 5; i++) w.Add(new SimAgent { Id = "c" + i, Name = "Crew " + i, Kind = "pirate", System = mother.System, State = "home", Home = mother.Id, Until = 1e9 });
        var child = w.Split(mother, 100, new Random(1));
        Assert.NotNull(child);
        Assert.NotEqual(w.Galaxy.StarOf(mother.System).Name, w.Galaxy.StarOf(child.System).Name);
        Assert.Equal(200000, child.Cash, 0); Assert.Equal(200000, mother.Cash, 0);
        Assert.Equal(2, w.Agents.Values.Count(a => a.Home == child.Id && a.State == "warp" && a.Dest == child.System));
        Assert.Contains(w.Events, e => e.Kind == "split");
        var back = World.Parse(w.Serialize(), out _);
        Assert.Equal(100, back.Hubs[mother.Id].LastSplit, 3);
    }
}

public class HubCentreTests
{
    [Fact]
    public void Every_hub_gets_one_centrepiece_that_is_never_capped_and_takes_the_stash_first()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { HubTests.Yaml }) };
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First();
        var c = Assert.Single(w.Wrecks.Values, x => x.Centre && x.Hub == hub.Id);
        Assert.Equal(World.CentrePrefab, World.WreckPrefab(c));
        Assert.Same(c, w.EnsureCentre(hub, 10));
        for (var i = 0; i < 12; i++) { var wr = new SimWreck { Id = "x" + i, System = hub.System, Name = "Hulk " + i, Hub = hub.Id, Time = i, Until = World.Never }; w.Wrecks[wr.Id] = wr; }
        w.HubCaps(hub, 100);
        Assert.False(c.Debris); Assert.True(w.Wrecks.ContainsKey(c.Id));
        Assert.True(World.Parse(w.Serialize(), out _).Wrecks[c.Id].Centre);
    }
}

public class HubDecorTests
{
    [Fact]
    public void A_new_hub_has_junk_round_its_post_and_a_haul_scatters_more_all_empty_and_capped()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { HubTests.Yaml }) };
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First();
        var decor = w.Wrecks.Values.Where(x => x.Decor && x.Hub == hub.Id).ToList();
        Assert.Single(decor);
        Assert.All(decor, d => { Assert.True(d.Debris); Assert.Equal(0, d.Amt); Assert.StartsWith("Eden_BAOJ_Debris", World.DebrisPrefab(d)); Assert.InRange((d.Pos - hub.Pos).Length, 59, 201); });
        for (var i = 0; i < 12; i++)
        {
            var wr = new SimWreck { Id = "k" + i, System = "Elsewhere", Name = "Hull " + i, Kind = "trader", Role = "bulk", Time = i, Until = 10 };
            w.Wrecks[wr.Id] = wr; w.Haul(wr, hub, 100 + i);
        }
        Assert.True(w.Wrecks.Values.Count(x => x.Hub == hub.Id && x.Debris && !x.Decor && !x.Gone) <= w.Config.HubDebris);
        Assert.True(w.Wrecks.Values.Count(x => x.Hub == hub.Id && x.Decor && !x.Gone) <= World.HubDecorMax);
        Assert.Contains(w.Wrecks.Values, x => x.Decor && x.Time >= 100);
        Assert.True(World.Parse(w.Serialize(), out _).Wrecks.Values.Any(x => x.Decor));
    }
}

public class StrongboxTests
{
    [Fact]
    public void A_broken_hub_drops_its_junk_and_leaves_a_strongbox_by_the_antenna_until_it_reforms()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A" };
        var hub = new SimHub { Id = "h1", System = "C", Name = "Den", Size = 3, Manned = true, Cash = 30000, Pos = new LivingSystems.Core.Flight.Vec3(1000, 0, 0) };
        w.Hubs["h1"] = hub;
        var centre = w.EnsureCentre(hub, 0);
        Assert.Single(w.Wrecks.Values, x => x.Decor);
        var p = w.Add(new SimAgent { Id = "p1", Name = "Last Man", Kind = "pirate", System = "C", State = "home", Home = "h1", Until = 1e9, Live = "x" });
        w.LastTick = 100;
        w.EndLive(p.Id, "dead", 100);
        Assert.True(w.IsBroken(hub));
        Assert.DoesNotContain(w.Wrecks.Values, x => x.Decor);                       // junk gone at once
        var chest = Assert.Single(w.Wrecks.Values, x => x.Chest);
        Assert.InRange((chest.Pos - centre.Pos).Length, 39, 41);                    // right by the listening post
        Assert.Equal(World.ChestPrefab, World.WreckPrefab(chest));
        Assert.Equal(0, w.GhostChance(chest));
        Assert.Contains(hub.Stash, s => s.Item == World.Strongbox);
        Assert.True(World.Parse(w.Serialize(), out _).Wrecks[chest.Id].Chest);
        w.EnsureCentre(hub, 200);
        Assert.DoesNotContain(w.Wrecks.Values, x => x.Decor);                       // cleared junk stays cleared
        w.Step(hub.BrokenUntil + 10, new Random(1));
        Assert.DoesNotContain(w.Wrecks.Values, x => x.Chest);                       // re-formed: the old strongbox goes
    }
}

public class TugTests
{
    static World Broken(out SimHub hub)
    {
        var w = new World(); w.Config.Systems = new List<string> { "Port" };
        w.SetStations("Port", new List<SimStation> { new SimStation { Id = 1, Name = "Yard", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        hub = new SimHub { Id = "h1", System = "Den", Name = "Den", Size = 3, Manned = true, Cash = 30000, Pos = new LivingSystems.Core.Flight.Vec3(1000, 0, 0) };
        w.Hubs["h1"] = hub;
        w.EnsureCentre(hub, 0);
        for (var i = 0; i < 3; i++)
            w.Wrecks["x" + i] = new SimWreck { Id = "x" + i, System = "Den", Name = "Hulk " + i, Kind = "trader", Hub = "h1", Time = i, Until = World.Never, Cargo = "GoldIngot", Amt = 10 };
        var p = w.Add(new SimAgent { Id = "p1", Name = "Last Man", Kind = "pirate", System = "Den", State = "home", Home = "h1", Until = 1e9, Live = "x" });
        w.LastTick = 100;
        w.EndLive(p.Id, "dead", 100);
        return w;
    }

    [Fact]
    public void A_tug_clears_a_broken_hub_sells_the_scrap_and_retires()
    {
        var w = Broken(out var hub);
        Assert.True(w.IsBroken(hub));
        var tug = Assert.Single(w.Agents.Values, a => a.IsTug);                // sent the moment it broke
        Assert.Equal(0, hub.SalvageAt);                                         // taken: not on the board
        var call = Assert.Single(w.Events, e => e.Kind == "tugcall");
        Assert.Contains(tug.Name, call.Text); Assert.Contains("Yard", call.Text); Assert.Contains("ETA ~", call.Text);
        Assert.True(tug.Until - 100 <= 600);                                    // short dock: it's there while you are
        var rng = new Random(1);
        w.Step(800, rng);
        Assert.Equal("Port", tug.Extra["tughome"]);
        Assert.True(w.Wrecks.Values.Where(x => x.Hub == "h1" && !x.Chest).All(x => x.Tug == tug.Id));   // antenna + hulks claimed
        Assert.True(w.Wrecks.Values.Single(x => x.Chest).Tug.Length == 0);                              // the strongbox is the players'
        for (var t = 1300.0; t < 12 * 3600; t += 600) w.Step(t, rng);   // the server steps often
        Assert.False(w.Agents.Values.Any(a => a.IsTug), string.Join(" | ", w.Agents.Values.Where(a => a.IsTug).Select(a => $"{a.State} {a.System} until {a.Until - hub.SalvageAt:0} cargo {a.CargoAmt}")) + " || " + string.Join(" | ", w.Events.Where(e => e.Agent == tug.Id || e.Kind == "tug").Select(e => e.Text)));   // done, home, retired
        Assert.False(w.Wrecks.Values.Any(x => x.Hub == "h1" && !x.Chest), string.Join(" | ", w.Wrecks.Values.Where(x => x.Hub == "h1").Select(x => $"{x.Name} tug={x.Tug} gone={x.Gone} chest={x.Chest} ent={x.Entity}")) + " || " + string.Join(" | ", w.Events.Where(e => e.Agent == tug.Id || e.Kind == "tug").Select(e => e.Text)));   // the pile is gone
        Assert.True(w.Stat("eco|tug|sold") > 0);
        Assert.Equal(4, w.Stat("eco|tug|cut"));                                                         // 3 hulks + the antenna
        Assert.Null(w.EnsureCentre(hub, w.LastTick));                                                    // no new post while broken
    }

    [Fact]
    public void A_lost_tug_is_not_replaced_and_its_list_waits_for_the_next()
    {
        var w = Broken(out var hub);
        var rng = new Random(2);
        w.Step(hub.SalvageAt + 700, rng);
        var tug = w.Agents.Values.Single(a => a.IsTug);
        tug.State = "dead"; tug.Until = w.LastTick;                              // jumped and killed
        w.Step(w.LastTick + 1, rng);
        Assert.DoesNotContain(w.Agents.Values, a => a.Id == tug.Id);
        Assert.True(w.Wrecks.Values.Where(x => x.Hub == "h1" && !x.Chest).All(x => x.Tug.Length == 0));
        Assert.Equal(1, w.Stat("eco|tug|lost"));
        Assert.True(hub.SalvageAt > 0);                                          // the pile is back on the board
        var round = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(hub.SalvageAt, round.Hubs["h1"].SalvageAt, 3);
    }
}

public class PostTests
{
    [Fact]
    public void The_post_is_the_gangs_while_it_stands_and_a_broken_gang_reforms_elsewhere()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { HubTests.Yaml }) };
        w.Config.Systems = new List<string> { "Alpha Station", "Beta Station" };
        w.SetStations("Alpha Station", new List<SimStation> { new SimStation { Id = 1, Name = "Lambda" } });
        w.SetStations("Beta Station", new List<SimStation> { new SimStation { Id = 2, Name = "Kappa" } });
        w.Config.StarsPerHub = 0.5;
        var hub = w.DiscoverHubs(w.Galaxy.Stars.Values, 0).First();
        foreach (var other in w.Hubs.Values.Where(h => h != hub).ToList()) w.Hubs.Remove(other.Id);
        foreach (var x in w.Wrecks.Values.Where(x => x.Hub != hub.Id).ToList()) w.Wrecks.Remove(x.Id);
        hub.Manned = true; hub.Cash = 20000;
        var post = w.Wrecks.Values.Single(x => x.Centre && x.Hub == hub.Id);
        Assert.True(post.Post);                                                // the gang's: spawns with its core in
        post.Entity = 7; post.Shown = "post";
        Assert.False(post.NeedsSwap);
        w.Wrecks["hulk"] = new SimWreck { Id = "hulk", System = hub.System, Name = "Hulk", Kind = "trader", Hub = hub.Id, Time = 1, Until = World.Never };
        var oldSys = hub.System; var oldStar = w.Galaxy.StarOf(oldSys).Name;
        var p = w.Add(new SimAgent { Id = "p1", Name = "Last Man", Kind = "pirate", System = hub.System, State = "home", Home = hub.Id, Until = 1e9, Live = "x" });
        w.LastTick = 100;
        w.EndLive(p.Id, "dead", 100);                                          // the guards are dead: broken
        Assert.True(w.IsBroken(hub));
        Assert.False(post.Post);                                               // core comes out (PfServer strips it)
        Assert.True(World.Parse(w.Serialize(), out _).Wrecks.Values.Any(x => x.Centre && !x.Post));
        w.Step(hub.BrokenUntil + 1, new Random(1));                             // the quiet's over: re-forms
        Assert.False(w.IsBroken(hub));
        Assert.NotEqual(oldStar, w.Galaxy.StarOf(hub.System).Name);           // somewhere else
        Assert.True(!w.Wrecks.TryGetValue("hulk", out var hk) || hk.Hub.Length == 0);   // left behind as an abandoned wreck (or a tug took it)
        Assert.Contains(w.Wrecks.Values, x => x.Centre && x.Hub == hub.Id && x.System == hub.System && x.Post);   // a new post there, theirs
        Assert.Contains(w.Events, e => e.Kind == "hubmoved" && e.System == oldSys);
    }
}

public class TugLiveTests
{
    static (World w, SimAgent tug) AtWork(string hullA = "")
    {
        var w = new World(); w.Config.Systems = new List<string> { "Port" }; w.Config.TugSafeMin = 0;
        w.SetStations("Port", new List<SimStation> { new SimStation { Id = 1, Name = "Yard", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Wrecks["a"] = new SimWreck { Id = "a", System = "Gulch", Name = "A", Kind = "trader", Time = 1, Until = World.Never, Salvage = true, Ship = hullA };
        w.Wrecks["b"] = new SimWreck { Id = "b", System = "Gulch", Name = "B", Kind = "trader", Time = 2, Until = World.Never, Salvage = true };
        var rng = new Random(1);
        w.LastTick = 100; w.Step(800, rng);
        var tug = w.Agents.Values.Single(x => x.IsTug);
        for (var t = 900.0; tug.State != "salvage" && t < 20000; t += 60) w.Step(t, rng);
        Assert.Equal("salvage", tug.State);
        return (w, tug);
    }

    [Fact]
    public void The_share_grows_offline_and_a_wreck_spawns_that_far_cut()
    {
        var (w, tug) = AtWork();
        var job = w.TugJob(tug);
        Assert.Equal("a", job.Id);                                            // oldest first
        var start = tug.Until - w.Config.TugWork;
        Assert.Equal(0.5, w.TugShare(tug, start + w.Config.TugWork / 2), 2);
        Assert.Equal(0.5, w.CutShare(job, start + w.Config.TugWork / 2), 2);
        Assert.Equal(0, w.CutShare(w.Wrecks["b"], start + w.Config.TugWork / 2)); // not its turn yet
    }

    [Fact]
    public void A_tug_keeps_its_real_hold_until_it_sells()
    {
        Assert.Equal("GoldIngot:218,SteelPlate:368", World.FormatHold(World.ParseHold("SteelPlate:368,GoldIngot:200,GoldIngot:18,Bad,X:0")));
        var (w, tug) = AtWork();
        w.SetHold(tug, "SteelPlate:368,GoldIngot:218,OxygenBottleLarge:131,MoneyCard:7221");
        var saved = World.Parse(w.Serialize(), out _);
        Assert.Equal(w.Agents[tug.Id].Extra[World.HoldKey], saved.Agents[tug.Id].Extra[World.HoldKey]);   // survives the save
        // it finishes, goes home, sells: the sale names the real goods, the hold is cleared
        var rng = new Random(2);
        for (var t = tug.Until + 1; t < tug.Until + 50000 && !w.Events.Any(e => e.Kind == "sold" && e.Agent == tug.Id); t += 60) w.Step(t, rng);
        var sold = w.Events.Last(e => e.Kind == "sold" && e.Agent == tug.Id);
        Assert.Contains("Money Card", sold.Text);
        Assert.False(tug.Extra.ContainsKey(World.HoldKey));
        // anyone else: not a tug, no hold
        var trader = w.Add(new SimAgent { Id = "t9", Name = "X", Kind = "trader", System = "Port" });
        w.SetHold(trader, "GoldIngot:1");
        Assert.False(trader.Extra.ContainsKey(World.HoldKey));
    }

    [Fact]
    public void A_bigger_hull_takes_longer_to_cut()
    {
        double Took(string hull) { var (w, tug) = AtWork(hull); return (tug.Until - tug.Since) / w.Config.TugWork; }
        Assert.Equal(0.6, Took("CV_Freighter_SparrowA"), 2);   // small
        Assert.Equal(1.0, Took(""), 2);                        // unknown hull (a hub post…): mid
        Assert.Equal(1.8, Took("CV_Freighter_HarkonA"), 2);    // big
    }

    [Fact]
    public void Back_from_live_mid_cut_the_wreck_keeps_the_share_and_the_rest_takes_the_rest()
    {
        var (w, tug) = AtWork();
        var now = tug.Until - w.Config.TugWork + 10;
        tug.Live = "Gulch";
        w.EndLive(tug.Id, "left", now, 0.6);
        Assert.Equal(0.6, w.Wrecks["a"].Cut, 3);
        Assert.Equal(now + 0.4 * w.Config.TugWork, tug.Until, 1);
        Assert.Equal(0.6, w.TugShare(tug, now), 3);
        Assert.Equal(0.6, World.Parse(w.Serialize(), out _).Wrecks["a"].Cut, 3);   // saved
    }

    [Fact]
    public void A_target_cut_live_is_credited_the_next_comes_then_it_goes_home()
    {
        var (w, tug) = AtWork();
        var now = tug.Until - 100;
        tug.Live = "Gulch";
        Assert.True(w.TugCutLive(tug.Id, "a", 1, now));                       // b next
        Assert.False(w.Wrecks.ContainsKey("a"));
        Assert.Equal("b", w.TugJob(tug).Id);
        Assert.Equal(1, w.Stat("eco|tug|cut"));
        Assert.False(w.TugCutLive(tug.Id, "b", 1, now + 50));                 // list done: home
        w.EndLive(tug.Id, "left", now + 60);
        w.Step(now + 61, new Random(3));
        Assert.Equal("warp", tug.State);
        Assert.Equal("Port", tug.Dest);
        Assert.Equal(2, w.Stat("eco|tug|cut"));                               // nothing cut twice
    }

    [Fact]
    public void With_a_player_there_it_stands_by_before_cutting()
    {
        var w = new World(); w.Config.Systems = new List<string> { "Port" }; w.Config.TugSafeMin = 0;
        w.SetStations("Port", new List<SimStation> { new SimStation { Id = 1, Name = "Yard", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Wrecks["a"] = new SimWreck { Id = "a", System = "Gulch", Name = "A", Kind = "trader", Time = 1, Until = World.Never, Salvage = true };
        w.Occupied = new HashSet<string> { "Gulch" };
        var rng = new Random(1);
        w.LastTick = 100; w.Step(800, rng);
        var tug = w.Agents.Values.Single(x => x.IsTug);
        for (var t = 900.0; tug.State != "salvage" && t < 20000; t += 60) w.Step(t, rng);
        var start = World.WorkStart(tug);
        Assert.Equal(w.Config.TugStandby + w.Config.TugWork, tug.Until - (start - w.Config.TugStandby), 1);
        Assert.Equal(0, w.TugShare(tug, start - 60));                        // standing by: nothing cut yet
        Assert.Equal(0.5, w.TugShare(tug, start + w.Config.TugWork / 2), 2);
        w.Occupied = new HashSet<string>();                                   // the player left before the countdown ran out
        var left = start - 300;
        w.Step(left, rng);
        Assert.Equal(0, World.WorkStart(tug));
        Assert.Equal(left + w.Config.TugWork, tug.Until, 1);                 // cutting from now, no standby
    }

    [Fact]
    public void A_pile_gets_one_standby_later_trips_just_cut()
    {
        foreach (var stood in new[] { false, true })
        {
            var w = new World(); w.Config.Systems = new List<string> { "Port" }; w.Config.TugSafeMin = 0;
            w.SetStations("Port", new List<SimStation> { new SimStation { Id = 1, Name = "Yard", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
            w.Hubs["h"] = new SimHub { Id = "h", System = "Gulch", Name = "Den", BrokenUntil = 1e9, SalvageAt = 1, StoodBy = stood };
            w.Wrecks["a"] = new SimWreck { Id = "a", System = "Gulch", Name = "A", Kind = "trader", Time = 1, Until = World.Never, Hub = "h" };
            w.Occupied = new HashSet<string> { "Gulch" };
            var rng = new Random(1);
            w.LastTick = 100; w.Step(800, rng);
            var tug = w.Agents.Values.Single(x => x.IsTug);
            for (var t = 900.0; tug.State != "salvage" && t < 20000; t += 60) w.Step(t, rng);
            Assert.Equal("salvage", tug.State);
            if (stood) Assert.True(World.WorkStart(tug) <= tug.Since + 1);        // second trip: straight to cutting
            else { Assert.True(World.WorkStart(tug) >= tug.Since + w.Config.TugStandby - 1); Assert.True(w.Hubs["h"].StoodBy); }
        }
    }

    [Fact]
    public void Crews_talk_about_a_tug_at_work_by_name_and_target()
    {
        var (w, tug) = AtWork();
        var other = w.Add(new SimAgent { Id = "t9", Name = "Gossip", Kind = "trader", System = "Gulch", State = "dock", Until = 1e12 });
        var said = w.Rumours(other, new List<string>(), w.LastTick, (from, pf) => pf);
        Assert.Contains(said, r => r.Contains(tug.Name) && r.Contains("the wreck of A") && r.Contains("1 more"));
    }

    [Fact]
    public void A_tug_that_finds_its_pile_gone_says_so_and_goes_home()
    {
        var w = new World(); w.Config.Systems = new List<string> { "Port" }; w.Config.TugSafeMin = 0;
        w.SetStations("Port", new List<SimStation> { new SimStation { Id = 1, Name = "Yard", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Wrecks["a"] = new SimWreck { Id = "a", System = "Gulch", Name = "A", Kind = "trader", Time = 1, Until = World.Never, Salvage = true };
        var rng = new Random(1);
        w.LastTick = 100; w.Step(800, rng);
        var tug = w.Agents.Values.Single(x => x.IsTug);
        for (var t = 900.0; tug.State != "warp" && t < 20000; t += 60) w.Step(t, rng);
        Assert.Equal("Gulch", tug.Dest);
        w.Wrecks.Remove("a");                                                // a player stripped it meanwhile
        for (var t = w.LastTick + 60; tug.System != "Gulch" && t < 40000; t += 60) w.Step(t, rng);
        Assert.Equal("warp", tug.State);
        Assert.Equal("Port", tug.Dest);
        Assert.Contains(w.Events, e => e.Kind == "tugbeaten" && e.Agent == tug.Id);
    }

    [Fact]
    public void A_full_hold_live_leaves_the_rest_cut_for_the_next_tug()
    {
        var (w, tug) = AtWork();
        var now = tug.Until - 100;
        tug.Live = "Gulch";
        Assert.False(w.TugCutLive(tug.Id, "a", 0.7, now));
        w.EndLive(tug.Id, "left", now + 60);
        w.Step(now + 61, new Random(3));
        Assert.Equal("warp", tug.State);
        Assert.Equal(0.7, w.Wrecks["a"].Cut, 3);
        Assert.Equal(0, w.Stat("eco|tug|cut"));
    }
}

public class TugShipTests
{
    static (World w, SimAgent tug) Sent()
    {
        var w = new World(); w.Config.Systems = new List<string> { "Port" }; w.Config.TugSafeMin = 0;
        w.SetStations("Port", new List<SimStation> { new SimStation { Id = 1, Name = "Orbital Refinery", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) },
                                                    new SimStation { Id = 2, Name = "Repair Yard", Pos = new LivingSystems.Core.Flight.Vec3(5000, 0, 0) } });
        w.Wrecks["a"] = new SimWreck { Id = "a", System = "Gulch", Name = "A", Kind = "trader", Time = 1, Until = World.Never, Salvage = true };
        w.LastTick = 100; w.Step(150, new Random(1));                         // the board's first round: sent, still docked
        return (w, w.Agents.Values.Single(x => x.IsTug));
    }

    [Fact]
    public void A_tug_berths_above_or_below_its_yard_out_of_the_lanes()
    {
        var (w, tug) = Sent();
        Assert.Equal(World.TugShip, w.ShipOf(tug, true));
        Assert.Equal(0, tug.Strength);                                           // unarmed, whatever the hull
        Assert.Equal("dock", tug.State);
        Assert.True(w.Track(tug, out var tr));
        Assert.Equal(2, w.StationOf(tug).Id);                                    // the repair yard
        Assert.True(Math.Abs(tr.A.Y) >= 400, $"berth y {tr.A.Y}");               // straight up / down (no box: 300 + 120)
        Assert.Equal(5000, tr.A.X, 1);
    }

    [Fact]
    public void Docking_live_sells_the_scrap_and_a_tug_that_flees_live_goes_home()
    {
        var (w, tug) = Sent();
        tug.System = "Port"; tug.State = "in"; tug.Cargo = "SteelPlate"; tug.CargoAmt = 300; tug.Live = "Port"; tug.Until = 1e9;
        Assert.True(w.DockLive(tug.Id, new Random(1), 2000));
        Assert.Equal("dock", tug.State);
        Assert.Equal(0, tug.CargoAmt);
        Assert.True(w.Stat("eco|tug|sold") > 0);
        tug.System = "Gulch"; tug.State = "salvage"; tug.Live = "Gulch";
        w.EndLive(tug.Id, "jumped", 3000);
        Assert.Equal("warp", tug.State);
        Assert.Equal("Port", tug.Dest);
    }
}

public class TugBoardTests
{
    [Fact]
    public void One_tug_per_site_late_wrecks_join_its_list_and_a_deadly_site_waits()
    {
        var w = new World(); w.Config.Systems = new List<string> { "Port" }; w.Config.TugSafeMin = 0;
        w.SetStations("Port", new List<SimStation> { new SimStation { Id = 1, Name = "Yard", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Wrecks["a"] = new SimWreck { Id = "a", System = "Gulch", Name = "A", Kind = "trader", Time = 1, Until = World.Never, Salvage = true };
        var rng = new Random(1);
        w.LastTick = 100; w.Step(800, rng);
        var tug = Assert.Single(w.Agents.Values, x => x.IsTug);
        w.Wrecks["b"] = new SimWreck { Id = "b", System = "Gulch", Name = "B", Kind = "trader", Time = 2, Until = World.Never, Salvage = true };
        w.Step(1500, rng);
        Assert.Single(w.Agents.Values, x => x.IsTug);                     // no second tug for the same site
        Assert.Equal(tug.Id, w.Wrecks["b"].Tug);                           // the late one joined its list
        tug.State = "dead"; tug.Until = w.LastTick; tug.System = "Gulch";
        w.Step(1600, rng);
        Assert.DoesNotContain(w.Agents.Values, x => x.IsTug);
        Assert.True(World.Parse(w.Serialize(), out _).TugWary.ContainsKey("Gulch"));   // saved
        w.Step(1600 + 3 * 3600, rng);                                       // the board skips Gulch for a while
        Assert.DoesNotContain(w.Agents.Values, x => x.IsTug);
        w.Step(1600 + 13 * 3600, rng);
        Assert.Single(w.Agents.Values, x => x.IsTug);                      // then a new one goes
    }
}
