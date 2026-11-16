using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Sim;
using Xunit;

public class TransitTests
{
    const string Yaml = @"SolarSystems:
  - Name: Alpha
    Coordinates: [0, 0, 0]
    StarClass: G
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Alpha Station, EdenSpaceG_PolTradeStation, '']
          - ['0,0,0', Alpha Moon, EdenMoonBarren, '']
  - Name: Beta
    Coordinates: [20, 0, 0]
    StarClass: K
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Beta Station, EdenSpaceG_PolTradeStation, '']
  - Name: Gamma
    Coordinates: [10, 0, 10]
    StarClass: K
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', 'Gamma [Sun Left]', SpaceWarpTarget, '']
          - ['0,0,0', Gamma Rock, EdenMoonBarren, '']
  - Name: Far
    Coordinates: [500, 0, 0]
    StarClass: K
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Far Orbit, EdenSpaceD_Orbit, '']
";

    static World Make()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { Yaml }) };
        w.Config.Systems = new List<string> { "Alpha Station", "Beta Station" };
        w.SetStations("Alpha Station", new List<SimStation> { new SimStation { Id = 1, Name = "Lambda", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.SetStations("Beta Station", new List<SimStation> { new SimStation { Id = 2, Name = "Kappa", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Config.PassChance = 1; w.Config.HubViaChance = 0;
        return w;
    }

    static SimAgent Trader(World w, double now) => w.Add(new SimAgent { Id = "t1", Name = "Penny", Kind = "trader", Role = World.Roles[0].Id, System = "Alpha Station", Dest = "Beta Station", State = "out", Until = now, Since = now - 100, Speed = 40, Strength = 0 });

    [Fact]
    public void A_jump_stops_over_where_a_player_is_crosses_and_jumps_on()
    {
        var w = Make(); var rng = new Random(1);
        w.Occupied = new HashSet<string> { "Gamma [Sun Left]" };
        var a = Trader(w, 1000);
        w.Step(1000, rng);
        Assert.Equal("warp", a.State);
        Assert.Equal("Gamma [Sun Left]", a.Extra["via"]);
        w.Step(a.Until, rng);
        Assert.Equal("pass", a.State);
        Assert.Equal("Gamma [Sun Left]", a.System);
        Assert.Equal("Beta Station", a.Dest);
        Assert.Equal(w.Config.PassMetres / 40, a.Until - a.Since, 3);
        Assert.True(w.Track(a, out var tr));
        Assert.Equal(w.Config.PassMetres, (tr.B - tr.A).Length, 0);
        Assert.True(w.PassRelative(a));
        w.Step(a.Until, rng);
        Assert.Equal("warp", a.State);
        Assert.False(a.Extra.ContainsKey("via"));
        w.Step(a.Until, rng);
        Assert.Equal("in", a.State);
        Assert.Equal("Beta Station", a.System);
        Assert.Equal("Gamma [Sun Left]", a.From);
    }

    [Fact]
    public void No_stopover_on_a_planet_far_off_the_route_or_right_after_another()
    {
        var w = Make(); var rng = new Random(1);
        var a = Trader(w, 1000);
        w.Occupied = new HashSet<string> { "Gamma Rock" };   // a surface: nobody crosses that
        Assert.Null(w.PickVia(a, rng, 1000));
        w.Occupied = new HashSet<string> { "Far Orbit" };   // 500 LY off
        Assert.Null(w.PickVia(a, rng, 1000));
        w.Occupied = new HashSet<string> { "Gamma [Sun Left]" };
        w.LastPass["Gamma [Sun Left]"] = 990;   // one just went through
        Assert.Null(w.PickVia(a, rng, 1000));
        Assert.Equal("Gamma [Sun Left]", w.PickVia(a, rng, 990 + w.Config.PassGapSeconds));
    }

    [Fact]
    public void A_passer_at_a_hub_can_be_jumped_and_carries_on_afterwards()
    {
        var w = Make(); var rng = new Random(3);
        w.Config.HubJumpChance = 1; w.Config.HubViaChance = 1;   // a player at a hub no longer pulls passers in: this one comes by the hub route
        w.Hubs["h1"] = new SimHub { Id = "h1", Name = "Knife Point", System = "Gamma [Sun Left]", Size = 1, Manned = true, Pos = new LivingSystems.Core.Flight.Vec3(5000, 0, 0) };
        var p = w.Add(new SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "Gamma [Sun Left]", State = "home", Home = "h1", Until = 1e9, Since = 0, Strength = 1 });
        w.Occupied = new HashSet<string> { "Gamma [Sun Left]" };
        var a = Trader(w, 1000);
        w.Step(1000, rng);
        w.Step(a.Until, rng);
        Assert.Equal("fight", a.State);
        Assert.Equal("pass", a.After);
        Assert.False(w.PassRelative(a));
        Assert.True(w.Track(a, out var tr));
        Assert.True((tr.A - w.Hubs["h1"].Pos).Length < 300);   // the fight is at the hub
        for (var i = 0; i < 40 && a.Alive && a.State == "fight"; i++) w.Step(w.Fights.Values.First().Next, rng);
        if (a.Alive) Assert.Contains(a.State, new[] { "pass", "warp", "in", "dock" });
    }
}

public class RumourTests
{
    [Fact]
    public void Crews_tell_of_attacks_wrecks_hubs_and_grudges_nearby()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { @"SolarSystems:
  - Name: Alpha
    Coordinates: [0, 0, 0]
    StarClass: G
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Alpha Station, EdenSpaceG_PolTradeStation, '']
          - ['0,0,0', Alpha Belt, EdenSpaceD_AsteroidT1, '']
  - Name: Far
    Coordinates: [900, 0, 0]
    StarClass: K
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Far Orbit, EdenSpaceD_Orbit, '']
" }) };
        var me = w.Add(new SimAgent { Id = "t1", Name = "Penny", Kind = "trader", System = "Alpha Station", State = "dock" });
        w.Events.Add(new SimEvent { Time = 900, Agent = "t2", Name = "Kestrel", System = "Alpha Belt", Kind = "attack", Text = "x" });
        w.Events.Add(new SimEvent { Time = 900, Agent = "t3", Name = "Faraway", System = "Far Orbit", Kind = "attack", Text = "x" });
        w.Wrecks["w1"] = new SimWreck { Id = "w1", Name = "Old Rope", System = "Alpha Belt", Time = 500, Until = 5000, Amt = 10 };
        var h = new SimHub { Id = "h1", Name = "Knife Point", System = "Alpha Belt" };
        h.Memory["memory.killed.steam1"] = "Rust Jaw";
        w.Hubs["h1"] = h;
        var r = w.Rumours(me, "steam1", 1000, (a, b) => b);
        Assert.Contains(r, x => x.Contains("Knife Point") && x.Contains("wants you dead"));
        Assert.Contains(r, x => x.Contains("Kestrel") && x.Contains("Alpha Belt"));
        Assert.DoesNotContain(r, x => x.Contains("Faraway"));   // 900 LY off: not local news
        Assert.Contains(r, x => x.Contains("Old Rope"));
        Assert.DoesNotContain(w.Rumours(me, "steam2", 1000, (a, b) => b), x => x.Contains("wants you dead"));
        // hub hideouts: about half the crews know a given one, and the same crew always gives the same answer
        var knows = Enumerable.Range(0, 200).Count(i => w.Rumours(new SimAgent { Id = "c" + i, Kind = "trader", System = "Alpha Station" }, "", 1000, (a, b) => b).Any(x => x.Contains("pirate den") || x.Contains("hang around")));
        Assert.InRange(knows, 60, 140);
    }
}
