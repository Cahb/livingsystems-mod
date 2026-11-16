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
