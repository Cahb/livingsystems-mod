using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core;
using LivingSystems.Core.Sim;
using Xunit;

public class SimTests
{
    static World NewWorld(params string[] systems)
    {
        var w = new World();
        w.Config.Systems = systems.Length > 0 ? systems.ToList() : new List<string> { "A", "B", "C" };
        w.Config.LegMetres = 10000;   // the arithmetic below assumes 10 km lanes
        return w;
    }

    static List<SimEvent> RunFor(World w, Random rng, double from, double seconds, double step = 30)
    {
        var all = new List<SimEvent>();
        for (var t = from; t <= from + seconds; t += step) all.AddRange(w.Step(t, rng));
        return all;
    }

    [Fact]
    public void A_trader_without_pirates_cycles_dock_out_warp_in_dock()
    {
        var w = NewWorld("A", "B");
        w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "dock", Until = 100, Role = "bulk", Speed = 50 });
        var ev = RunFor(w, new Random(1), 0, 2000);
        var kinds = ev.Select(e => e.Kind).ToList();
        Assert.Equal(new[] { "undock", "jump", "arrive" }, kinds.Take(3));
        var arrive = ev.First(e => e.Kind == "arrive");
        // dock ends at 100, leg 10 km / 50 m/s = 200 s, warp 60 s → arrives at 360, in B
        Assert.Equal(360, arrive.Time, 3);
        Assert.Equal("B", arrive.System);
    }

    [Fact]
    public void Slow_haulers_take_longer()
    {
        var w = NewWorld("A", "B");
        w.Add(new SimAgent { Id = "fast", System = "A", State = "dock", Until = 0, Speed = 100 });
        w.Add(new SimAgent { Id = "slow", System = "A", State = "dock", Until = 0, Speed = 20 });
        var ev = RunFor(w, new Random(1), 0, 3000);
        double Arr(string id) => ev.First(e => e.Agent == id && e.Kind == "arrive").Time;
        Assert.True(Arr("slow") > Arr("fast"));
    }

    [Fact]
    public void A_late_tick_replays_the_same_history_as_frequent_ticks()
    {
        World Make() { var w = NewWorld(); w.Seed(new Random(5), 0, 4, 2); w.LastTick = 0; return w; }
        var a = Make(); var b = Make();
        RunFor(a, new Random(9), 0, 6000, 10);
        b.Step(6000, new Random(9));
        // same schedule, different rng draw order is allowed, but the first event must be identical (no rng before it)
        Assert.Equal(a.Events.First().Text, b.Events.First().Text);
        Assert.Equal(a.Events.First().Time, b.Events.First().Time, 3);
    }

    [Fact]
    public void Pirate_and_trader_in_the_same_system_always_meet_and_the_fight_takes_minutes()
    {
        var w = NewWorld("A", "B");
        w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "out", Until = 1000, Dest = "B", Speed = 10 });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", Name = "Rust Jaw", System = "A", State = "lurk", Until = 5000 });
        var ev = w.Step(10, new Random(3));
        Assert.Contains(ev, e => e.Kind == "attack" && e.System == "A");
        Assert.Equal("fight", w.Get("t1").State);
        Assert.Equal("fight", w.Get("p1").State);
        Assert.Single(w.Fights);
        var after = RunFor(w, new Random(3), 20, 1500);
        Assert.Contains(after, e => e.Kind == "escaped" || e.Kind == "limping" || e.Kind == "destroyed");
        Assert.NotEqual("fight", w.Get("p1").State);
    }

    [Fact]
    public void A_surviving_trader_resumes_its_leg_and_isnt_hit_twice_by_the_same_pirate()
    {
        var rng = new Random(11);
        for (var i = 0; i < 50; i++)
        {
            var w = NewWorld("A", "B");
            w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Until = 1000, Dest = "B", Strength = 50 });   // nearly always survives
            w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6 });
            var ev = RunFor(w, rng, 0, 1500, 10);
            var t = w.Get("t1");
            if (!t.Alive) continue;
            Assert.Equal(1, ev.Count(e => e.Kind == "attack"));
            Assert.Contains(ev, e => e.Kind == "jump" && e.Agent == "t1");
        }
    }

    [Fact]
    public void Outcomes_follow_strength()
    {
        double DeathRate(double traderStr, double pirateStr)
        {
            var rng = new Random(42); var dead = 0; const int n = 2000;
            for (var i = 0; i < n; i++)
            {
                var w = NewWorld("A", "B"); w.Config.MoneyFlow = false;   // fights, not tolls
                w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Until = 1000, Dest = "B", Strength = traderStr });
                w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, Strength = pirateStr });
                w.Step(1, rng); w.Step(1800, rng);   // one fight (rounds of 60 s); longer and the trader may loop back for another
                if (!w.Get("t1").Alive) dead++;
            }
            return dead / (double)n;
        }
        var even = DeathRate(1, 1);
        Assert.True(even < 0.15, $"even {even:P0}");    // equal odds: the pirate breaks off, the trader bails — rarely a kill
        Assert.True(DeathRate(1, 2.5) > even + 0.05);   // a frigate is worse
        Assert.True(DeathRate(3, 1) <= even);           // a well-armed trader fares no worse
        var unarmed = DeathRate(0, 1);
        Assert.InRange(unarmed, 0.15, 0.45);            // can only run, alone vs one pirate (FleeChance 0.45 × slow 0.6)
        Assert.True(DeathRate(0.5, 1) <= unarmed + 0.03);   // light guns never make it worse (it still runs when losing)
        Assert.True(DeathRate(0.5, 2.5) <= DeathRate(0, 2.5) + 0.03);
    }

    [Fact]
    public void An_unarmed_trader_never_drives_pirates_off()
    {
        var rng = new Random(8);
        for (var i = 0; i < 300; i++)
        {
            var w = NewWorld("A", "B");
            w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Until = 1000, Dest = "B", Strength = 0 });
            w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6 });
            var ev = w.Step(1, rng).Concat(w.Step(900, rng)).ToList();
            Assert.Equal(1.0, w.Get("p1").Hull);
            Assert.DoesNotContain(ev, e => e.Kind == "pirate-hurt" || e.Text.Contains("fought off"));
        }
    }

    [Fact]
    public void A_badly_hurt_pirate_goes_to_repair_then_lurks_again()
    {
        var rng = new Random(2);
        for (var i = 0; i < 200; i++)
        {
            var w = NewWorld("A", "B");
            w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Until = 1000, Dest = "B", Strength = 1.5 });
            w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, Hull = 0.45 });
            w.Step(1, rng); w.Step(900, rng);
            var p = w.Get("p1");
            if (p.State != "repair") continue;
            w.Agents.Remove("t1");   // no second round with the same trader on a later leg
            for (var t = 930.0; t <= 900 + w.Config.RepairSeconds + 30; t += 30) w.Step(t, rng);
            Assert.Equal("lurk", p.State);
            Assert.Equal(1.0, p.Hull);
            return;
        }
        Assert.Fail("pirate never went to repair");
    }

    [Fact]
    public void Server_downtime_pauses_the_world()
    {
        var w = NewWorld("A", "B");
        w.Add(new SimAgent { Id = "t1", System = "A", State = "dock", Until = 200 });
        w.Step(100, new Random(1));
        w.Resume(100 + 10 * 3600);   // loaded ten hours later
        var ev = w.Step(100 + 10 * 3600, new Random(1));
        Assert.Empty(ev);
        Assert.Equal(200 + 10 * 3600, w.Get("t1").Until, 3);
    }

    [Fact]
    public void Risk_quote_comes_from_reported_attacks_and_fades()
    {
        var w = NewWorld("A", "B");
        Assert.Equal(Risk.Quiet, w.RiskIn("A", 0));
        w.Events.Add(new SimEvent { Time = 100, System = "A", Kind = "attack" });
        Assert.Equal(Risk.Moderate, w.RiskIn("A", 200));
        Assert.Equal(Risk.Quiet, w.RiskIn("B", 200));
        w.Events.Add(new SimEvent { Time = 150, System = "A", Kind = "attack" });
        Assert.Equal(Risk.Hot, w.RiskIn("A", 200));
        Assert.Equal(Risk.Quiet, w.RiskIn("A", 150 + w.Config.ReportSeconds + 1));
    }

    [Fact]
    public void Adopting_a_departed_live_trader_keeps_one_agent_per_entity()
    {
        var w = NewWorld("A", "B", "C");
        var a = w.Adopt(75061, "Spike 1", "A", 0.8, new Random(1), 1000);
        Assert.Equal("warp", a.State);
        Assert.NotEqual("A", a.Dest);
        Assert.Equal(0.8, a.Hull);
        var again = w.Adopt(75061, "Spike 1", "B", 1, new Random(1), 2000);
        Assert.Same(a, again);
        Assert.Single(w.Agents);
    }

    [Fact]
    public void Round_trips_through_text_including_odd_names_and_unknown_fields()
    {
        var w = NewWorld("SNUO 37444 I Sector", "West Rim to Core Jump Gate");
        w.Seed(new Random(4), 1000, 3, 2);
        w.LastTick = 1234.5;
        w.Agents["t1"].Name = "Brass = Goose|50%";
        w.Step(5000, new Random(4));
        w.Agents["t1"].Extra["future.key"] = "x y";   // after stepping: a ship lost and replaced in the run starts with a clean slate
        var text = w.Serialize();
        var back = World.Parse(text, out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(text, back.Serialize());
        Assert.Equal(w.Config.Systems, back.Config.Systems);
        Assert.Equal("x y", back.Agents["t1"].Extra["future.key"]);
    }

    [Fact]
    public void A_saved_trader_slower_than_its_role_is_brought_up_to_it_on_load()
    {
        var w = World.Parse(World.Header + "\nagent id=t50 kind=trader role=courier speed=28.8\nagent id=t51 kind=trader role=bulk speed=50", out _);
        var courier = World.RoleOf(w.Agents["t50"]);
        Assert.InRange(w.Agents["t50"].Speed, courier.SpeedMin, courier.SpeedMax);
        Assert.Equal(50f, w.Agents["t51"].Speed);   // already in range: untouched
        Assert.Equal(w.Agents["t50"].Speed, World.Parse(World.Header + "\nagent id=t50 kind=trader role=courier speed=28.8", out _).Agents["t50"].Speed);   // same every load
    }

    [Fact]
    public void Unknown_header_gives_an_empty_world()
    {
        var w = World.Parse("something else\nagent id=t1", out var skipped);
        Assert.Empty(w.Agents);
        Assert.Equal(2, skipped);
    }

    [Fact]
    public void Long_run_stays_sane()
    {
        var w = NewWorld("A", "B", "C", "D");
        var rng = new Random(77);
        w.Seed(rng, 0, 8, 3);
        var ev = RunFor(w, rng, 0, 48 * 3600, 60);
        Assert.Contains(ev, e => e.Kind == "attack");
        Assert.Contains(ev, e => e.Kind == "arrive");
        // no pirate stuck in a fight, nobody scheduled in the past
        Assert.DoesNotContain(w.Agents.Values, a => a.State == "fight" && w.FightOf(a) == null);
        Assert.All(w.Fights.Values, f => Assert.Contains(f.Members, m => w.Get(m).IsTrader));
        Assert.DoesNotContain(w.Agents.Values, a => (a.Alive || a.IsTrader) && a.Until < w.LastTick);
    }
}

public class SimLiveTests
{
    static World W()
    {
        var w = new World(); w.Config.Systems = new System.Collections.Generic.List<string> { "A", "B" }; return w;
    }

    [Fact]
    public void A_live_agent_is_neither_advanced_nor_fought_by_the_sim()
    {
        var w = W();
        w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Until = 100, Dest = "B", Live = "A" });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6 });
        var ev = w.Step(500, new Random(1));
        Assert.Empty(ev);
        Assert.Equal("out", w.Get("t1").State);
        Assert.Equal("lurk", w.Get("p1").State);
    }

    [Fact]
    public void After_leaving_live_it_catches_up_and_is_not_shown_again_this_leg()
    {
        var w = W();
        w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Until = 100, Dest = "B", Live = "A" });
        w.EndLive("t1", "left", 500);
        var t = w.Get("t1");
        Assert.False(t.IsLive); Assert.True(t.Shown);
        Assert.Equal(500, t.Until, 3);
        w.Step(500, new Random(1));
        Assert.Equal("warp", t.State);
        w.Step(600, new Random(1));
        Assert.Equal("in", t.State);
        Assert.False(t.Shown);   // new leg: may be shown again
    }

    [Fact]
    public void Dying_live_is_dying_in_the_sim_and_a_fleeing_pirate_goes_to_repair()
    {
        var w = W();
        w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "out", Until = 1000, Live = "A" });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1000, Live = "A" });
        w.EndLive("t1", "dead", 50);
        w.EndLive("p1", "fled", 50);
        Assert.False(w.Get("t1").Alive);
        Assert.Contains(w.Events, e => e.Kind == "destroyed" && e.Agent == "t1");
        Assert.Equal("repair", w.Get("p1").State);
        Assert.True(w.Get("p1").Hull < 1);
    }

    [Fact]
    public void Live_state_round_trips_and_adopting_clears_it()
    {
        var w = W();
        w.Add(new SimAgent { Id = "t1", EntityId = 5, System = "A", State = "out", Until = 10, Live = "A", Shown = true });
        var back = World.Parse(w.Serialize(), out _);
        Assert.Equal("A", back.Get("t1").Live);
        Assert.True(back.Get("t1").Shown);
        var a = back.Adopt(5, "Mule", "A", 1, new Random(1), 20);
        Assert.False(a.IsLive);
    }

    [Fact]
    public void A_live_trader_meets_pirates_it_hasnt_met_live_or_not()
    {
        var w = W();
        w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Until = 1000, Met = "p1", Live = "A" });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6 });
        Assert.Null(w.NextPass(10, true));
        w.Add(new SimAgent { Id = "p3", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, Live = "A" });
        Assert.Equal("p3", w.NextPass(10, true).Pirate.Id);
        Assert.Null(w.NextPass(10, false));   // the sim doesn't touch live traders
    }
}

public class SimFeedTests
{
    [Fact]
    public void Feed_lines_are_short_and_name_the_short_system()
    {
        var w = new World();
        w.Add(new SimAgent { Id = "t1", Name = "Kestrel", System = "Rogue J-5565", Hull = 0.55 });
        var line = w.FeedLine(new SimEvent { Agent = "t1", System = "Rogue J-5565", Kind = "attack" });
        Assert.StartsWith(World.Formatted, line);
        Assert.Contains("ATTACKED", line);
        Assert.Contains(">Rogue<", line);
        Assert.Contains("HIT 55%", w.FeedLine(new SimEvent { Agent = "t1", System = "Rogue J-5565", Kind = "limping" }));
        var visible = System.Text.RegularExpressions.Regex.Replace(line.Substring(1), "<[^>]+>", "");
        Assert.True(visible.Length <= 24, visible);
    }

    [Theory]
    [InlineData("SNUO 37444 I Sector", "SNUO")]
    [InlineData("West Rim to Core Jump Gate", "West")]
    [InlineData("Rogue J-5565", "Rogue")]
    [InlineData("Ahronari [Sun Right]", "Ahron…")]
    public void Short_system_names(string full, string expected) => Assert.Equal(expected, World.Short(full));
}

public class SimClaimTests
{
    [Fact]
    public void A_claimed_ship_leaves_the_sim_and_its_entity_is_forgotten()
    {
        var w = new World(); w.Config.Systems = new System.Collections.Generic.List<string> { "A", "B" };
        w.Add(new SimAgent { Id = "t1", EntityId = 75069, System = "A", State = "out", Until = 100, Live = "A" });
        w.EndLive("t1", "claimed", 50);
        Assert.Equal(0, w.Get("t1").EntityId);   // never destroyed as a "stale copy"
        Assert.False(w.Get("t1").Alive);
        Assert.Null(w.ByEntity(75069));
    }
}

public class SimAdoptTests
{
    [Fact]
    public void A_sim_trader_adopted_after_an_escort_keeps_its_destination()
    {
        var w = new World(); w.Config.Systems = new System.Collections.Generic.List<string> { "A", "B", "C" };
        w.Add(new SimAgent { Id = "t1", EntityId = 7, System = "A", State = "out", Dest = "B", Live = "A" });
        for (var i = 0; i < 20; i++) Assert.Equal("B", w.Adopt(7, "Mule", "A", 1, new Random(i), 10).Dest);
    }
}

public class SimStageTests
{
    [Fact]
    public void StageHere_puts_a_lurking_pirate_and_an_outbound_trader_in_the_system()
    {
        var w = new World(); w.Config.Systems = new System.Collections.Generic.List<string> { "A", "B" };
        w.Seed(new Random(1), 0, 3, 2);
        w.StageHere("A", 100, new Random(1));
        Assert.Contains(w.Agents.Values, a => !a.IsTrader && a.System == "A" && a.State == "lurk");
        var t = Assert.Single(w.Agents.Values, a => a.IsTrader && a.System == "A" && a.State == "out");
        Assert.Equal("B", t.Dest);
    }
}

public class SimContinueTests
{
    [Fact]
    public void A_live_fight_that_leaves_the_bubble_continues_in_the_sim_in_rounds()
    {
        var w = new World(); w.Config.Systems = new System.Collections.Generic.List<string> { "A", "B" };
        w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Until = 500, Dest = "B", Live = "A" });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 5000, Live = "A" });
        var f = w.StartLiveFight(w.NextPass(100, true), new Random(1));
        Assert.True(f.IsLive);
        Assert.Empty(w.Step(1000, new Random(1)));   // live: the game fights it, the sim waits
        w.EndLive("p1", "left", 1000);
        Assert.True(f.IsLive);                       // trader still live
        w.EndLive("t1", "left", 1001, hull: 0.5);
        Assert.False(f.IsLive);
        Assert.Equal(0.5, w.Get("t1").Hull, 3);      // what it lost live counts
        var ev = w.Step(4000, new Random(1));
        Assert.Contains(ev, e => e.Kind == "escaped" || e.Kind == "limping" || e.Kind == "destroyed");
        Assert.Empty(w.Fights);
    }
}

public class SimJumpTests
{
    [Fact]
    public void A_pirate_seen_jumping_out_live_leaves_the_system_in_the_sim()
    {
        var w = new World(); w.Config.Systems = new System.Collections.Generic.List<string> { "A", "B" };
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 5000, Live = "A" });
        w.EndLive("p1", "jumped", 100);
        var p = w.Get("p1");
        Assert.Equal("warp", p.State);
        Assert.Equal("B", p.Dest);
        w.Step(100 + w.Config.WarpSeconds + 1, new Random(1));
        Assert.Equal("B", p.System);
    }
}

public class SimLaneTests
{
    static World Make()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B", "C" };
        w.SetStations("A", new List<SimStation>
        {
            new SimStation { Id = 10, Name = "Polaris", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) },
            new SimStation { Id = 20, Name = "Halo", Pos = new LivingSystems.Core.Flight.Vec3(30000, 0, 0) },
        });
        return w;
    }

    [Fact]
    public void An_outbound_trader_moves_from_the_station_approach_to_the_warp_point()
    {
        var w = Make();
        var a = w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 1000 });
        Assert.True(w.Track(a, out var tr));
        var dur = w.Config.LegMetres / 50;
        Assert.Equal(1000 - dur, tr.T0, 3);
        Assert.Equal(w.Config.StationClear, (tr.A - tr.Station.Pos).Length, 0);
        Assert.Equal(w.Config.LegMetres, (tr.B - tr.A).Length, 0);
        Assert.Equal(w.Config.StationClear + w.Config.LegMetres / 2, (tr.At(1000 - dur / 2) - tr.Station.Pos).Length, 0);
    }

    [Fact]
    public void The_same_station_and_destination_always_use_the_same_lane_and_inbound_runs_it_backwards()
    {
        var w = Make();
        var o = w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Until = 1000, StationId = 10 });
        var i = w.Add(new SimAgent { Id = "t2", System = "A", State = "in", From = "B", Until = 1000, StationId = 10 });
        w.Track(o, out var to); w.Track(i, out var ti);
        Assert.True((to.A - ti.B).Length < 0.01f && (to.B - ti.A).Length < 0.01f);
        var c = w.Add(new SimAgent { Id = "t3", System = "A", State = "out", Dest = "C", Until = 1000, StationId = 10 });
        w.Track(c, out var tc);
        Assert.True((tc.B - to.B).Length > 100f);   // another destination, another lane
    }

    [Fact]
    public void No_station_known_means_no_place()
    {
        var w = Make();
        Assert.False(w.Track(w.Add(new SimAgent { Id = "t1", System = "B", State = "out", Dest = "A", Until = 1000 }), out _));
        Assert.False(w.Track(w.Add(new SimAgent { Id = "t2", System = "A", State = "warp", Dest = "B", Until = 1000 }), out _));
    }

    [Fact]
    public void A_trader_departs_from_the_station_it_docked_at_and_picks_a_new_one_in_the_next_system()
    {
        var w = Make();
        w.SetStations("B", new List<SimStation> { new SimStation { Id = 99, Name = "Bee", Pos = new LivingSystems.Core.Flight.Vec3(5, 5, 5) } });
        var a = w.Add(new SimAgent { Id = "t1", System = "A", State = "dock", Until = 0, Role = "bulk", Speed = 50 });
        var home = w.StationOf(a).Id;
        w.Step(1, new Random(1));
        Assert.Equal("out", a.State);
        Assert.Equal(home, w.StationOf(a).Id);
        a.Dest = "B";
        w.Step(a.Until + w.Config.WarpSeconds + 1, new Random(1));
        Assert.Equal("in", a.State);
        Assert.Equal("A", a.From);
        Assert.Equal(99, w.StationOf(a).Id);
    }

    [Fact]
    public void Leaving_live_part_way_resyncs_the_leg_and_arriving_live_ends_it()
    {
        var w = Make();
        var a = w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 1000, Live = "A" });
        w.EndLive("t1", "left", 100, 0.75);
        Assert.Equal(100 + 0.25 * w.LegSeconds(a), a.Until, 3);
        a.Live = "A";
        w.EndLive("t1", "arrived", 150);
        Assert.Equal(150, a.Until, 3);
    }

    [Fact]
    public void Stations_survive_a_save()
    {
        var w = Make();
        var back = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(2, back.Stations["A"].Count);
        Assert.Equal("Halo", back.Stations["A"][1].Name);
        Assert.Equal(30000f, back.Stations["A"][1].Pos.X);
        Assert.False(back.SetStations("A", w.Stations["A"].ToList()));
    }
}

public class SimEngagementTests
{
    static World Make()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B", "C" };
        w.SetStations("A", new List<SimStation> { new SimStation { Id = 10, Name = "Polaris", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Config.LegMetres = 10000;   // "10 km in 200 s" below
        w.Config.LegAmbush = false;   // the ambush-spot geometry (what live play uses), not the sim-only leg roll
        return w;
    }

    [Fact]
    public void A_pirate_ambushes_on_its_lane_only_when_the_trader_gets_there()
    {
        var w = Make();
        var tr = w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 200, Since = 0, StationId = 10 });   // 10 km in 200 s
        var p = w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 10, SpotOther = "B", SpotFrac = 0.5 });
        w.Step(90, new Random(1));
        Assert.Equal("out", tr.State);              // not there yet (passes at 100 s)
        w.Step(101, new Random(1));
        Assert.Equal("fight", tr.State);
        var f = Assert.Single(w.Fights.Values);
        Assert.True(f.HasPos);
        Assert.Equal(100, f.Start, 3);
        Assert.True(w.Track(p, out var pt) && (pt.A - f.Pos).Length < 1);
    }

    [Fact]
    public void A_pirate_on_another_lane_never_meets_the_trader()
    {
        var w = Make();
        var tr = w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 200, StationId = 10 });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 10, SpotOther = "C", SpotFrac = 0.5 });
        var ev = w.Step(250, new Random(1));
        Assert.DoesNotContain(ev, e => e.Kind == "attack");
        Assert.Equal("warp", tr.State);
    }

    [Fact]
    public void Pirates_lurking_near_each_other_fight_together()
    {
        var w = Make();
        w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 200, StationId = 10 });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 10, SpotOther = "B", SpotFrac = 0.5 });
        w.Add(new SimAgent { Id = "p2", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 10, SpotOther = "B", SpotFrac = 0.52 });
        var ev = w.Step(101, new Random(1));
        var f = Assert.Single(w.Fights.Values);
        Assert.Equal(3, f.Members.Count);
        Assert.Contains(ev, e => e.Kind == "pirate-join");
    }

    [Fact]
    public void A_second_trader_running_into_an_ongoing_fight_joins_it()
    {
        var w = Make();
        w.Config.EscapeChance = 0; w.Config.EscapeHurt = 0; w.Config.RoundHit = 0.001;   // a long, even fight
        w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 200, StationId = 10 });
        w.Add(new SimAgent { Id = "t2", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 260, Since = 60, StationId = 10 });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 10, SpotOther = "B", SpotFrac = 0.5 });
        w.Step(170, new Random(1));
        var f = Assert.Single(w.Fights.Values);
        Assert.Equal(new[] { "p1", "t1", "t2" }, f.Members.OrderBy(x => x).ToArray());
    }

    [Fact]
    public void Two_pirates_on_one_trader_kill_more_often_than_one()
    {
        double DeathRate(int pirates)
        {
            var rng = new Random(5); var dead = 0; const int n = 1000;
            for (var i = 0; i < n; i++)
            {
                var w = Make();
                w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 200, StationId = 10 });
                for (var k = 0; k < pirates; k++)
                    w.Add(new SimAgent { Id = "p" + k, Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 10, SpotOther = "B", SpotFrac = 0.5 + k * 0.02 });
                w.Step(101, rng); w.Step(5000, rng);
                if (!w.Get("t1").Alive) dead++;
            }
            return dead / (double)n;
        }
        Assert.True(DeathRate(2) > DeathRate(1) + 0.1);
    }

    [Fact]
    public void Pirate_spots_are_out_of_the_stations_guns_and_stable()
    {
        var w = Make();
        var p = w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, Since = 5 });
        Assert.True(w.SpotOf(p, out var a));
        Assert.True(w.SpotOf(p, out var b));
        Assert.True((a - b).Length < 0.01f);
        Assert.True(a.Length > 2500f, $"spot {a.Length:0} m from the station");
        var none = w.Add(new SimAgent { Id = "p2", Kind = "pirate", System = "B", State = "lurk", Until = 1e6 });
        Assert.False(w.SpotOf(none, out _));
    }

    [Fact]
    public void Fights_round_trip_through_text()
    {
        var w = Make();
        w.Config.EscapeChance = 0; w.Config.EscapeHurt = 0; w.Config.RoundHit = 0.001;
        w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Role = "bulk", Speed = 50, Until = 200, StationId = 10 });
        w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 10, SpotOther = "B", SpotFrac = 0.5 });
        w.Step(101, new Random(1));
        var text = w.Serialize();
        var back = World.Parse(text, out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(text, back.Serialize());
        Assert.Equal("e1", back.Get("t1").Fight);
    }

    [Fact]
    public void An_old_save_with_a_stray_fight_state_is_repaired()
    {
        var w = Make();
        w.Add(new SimAgent { Id = "t1", System = "A", State = "fight", After = "out", Resume = 50, Until = 1e15, Dest = "B" });
        w.Step(10, new Random(1));
        Assert.NotEqual("fight", w.Get("t1").State);
    }
}

public class SimReplaceTests
{
    [Fact]
    public void A_destroyed_trader_is_replaced_by_a_new_ship_later()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        var a = w.Add(new SimAgent { Id = "t1", Name = "Mule", EntityId = 5, System = "A", State = "out", Until = 1000, Live = "A" });
        w.EndLive("t1", "dead", 100);
        w.Step(100 + w.Config.ReplaceSeconds - 1, new Random(1));
        Assert.Equal("dead", a.State);
        var ev = w.Step(100 + w.Config.ReplaceSeconds + 1, new Random(1));
        Assert.Equal("dock", a.State);
        Assert.Equal(0, a.EntityId);
        Assert.Equal(1.0, a.Hull);
        Assert.Contains(ev, e => e.Kind == "new");
    }
}

public class SimSeedAtTests
{
    [Fact]
    public void A_system_joining_the_network_gets_docked_traders_with_unused_names()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        w.Seed(new Random(1), 0, 3, 1);
        var added = w.SeedAt("B", 2, new Random(2), 100);
        Assert.Equal(2, added.Count);
        Assert.All(added, a => { Assert.Equal("B", a.System); Assert.Equal("dock", a.State); Assert.True(a.Until > 100); });
        Assert.Equal(w.Agents.Values.Count(a => a.IsTrader), w.Agents.Values.Where(a => a.IsTrader).Select(a => a.Name).Distinct().Count());
    }
}

public class GalaxyTests
{
    const string Yaml = @"SolarSystems:
  - Name: Alpha
    Coordinates: [0, 0, 0]
    StarClass: G
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Alpha Orbit, SpaceX, '']
          - ['0,0,0', 'Alpha [Sun Right]', SpaceWarpTargetFixed, '']
  - Name: Beta
    Coordinates: [30, 0, 0]
    StarClass: K
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Beta Station, SpaceY, '']
  - Name: Far
    Coordinates: [700, 0, 0]
    StarClass: K
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0, 0, 0', Far Hub, SpaceZ,'', '', '', '']
  - Name: West to Core
    Coordinates: [-40, 0, 0]
    StarClass: JumpGate
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', West to Core Jump Gate, EdenSpace_JumpGate_X,'', '', '', '']
  - Name: Core to West
    Coordinates: [690, 0, 0]
    StarClass: JumpGate
    Sectors:
      - Coordinates: [0, 0, 0]
        Playfields:
          - ['0,0,0', Core to West Jump Gate, EdenSpace_JumpGate_Y,'', '', '', '']
";

    [Fact]
    public void Parses_stars_playfields_and_gate_pairs()
    {
        var g = Galaxy.Parse(new[] { Yaml });
        Assert.Equal("Alpha", g.StarOf("Alpha [Sun Right]").Name);
        Assert.Equal("Far", g.StarOf("Far Hub").Name);
        Assert.Equal(30, g.Distance("Alpha Orbit", "Beta Station"), 3);
        Assert.Equal("Core to West", g.GatePairs["West to Core"].Name);
    }

    [Fact]
    public void Routes_go_direct_within_range_or_through_the_gate()
    {
        var g = Galaxy.Parse(new[] { Yaml });
        Assert.Equal(700, g.Route("Alpha Orbit", "Far Hub", 50, 10, out var closed), 3);   // gates start broken: the long way
        Assert.False(closed);
        g.OpenGates.Add("West to Core");
        Assert.Equal(30, g.Route("Alpha Orbit", "Beta Station", 50, 10, out var gate1), 3);
        Assert.False(gate1);
        var far = g.Route("Alpha Orbit", "Far Hub", 50, 10, out var gate2);
        Assert.True(gate2);
        Assert.Equal(40 + 10 + 10, far, 3);   // to the gate, out of the paired gate, + gate cost
        g.Route("Alpha Orbit", "Far Hub", 5, 10, out _, out var hops);   // short hops: same route, more of them
        Assert.Equal(8 + 1 + 2, hops);
    }

    [Fact]
    public void Traders_trade_locally_and_warp_time_follows_distance()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { Yaml }) };
        w.Galaxy.OpenGates.Add("West to Core");
        w.Config.Systems = new List<string> { "Alpha Orbit", "Beta Station", "Far Hub" };
        var rng = new Random(3);
        var picks = Enumerable.Range(0, 400).Select(_ => w.TraderDest(rng, "Alpha Orbit")).ToList();
        Assert.True(picks.Count(p => p == "Beta Station") > picks.Count(p => p == "Far Hub"));   // near beats far
        Assert.True(w.WarpTime("Alpha Orbit", "Beta Station") > w.Config.WarpBaseSeconds);
        Assert.True(w.WarpTime("Alpha Orbit", "Far Hub") > w.WarpTime("Alpha Orbit", "Beta Station"));
    }

    [Fact]
    public void Lanes_point_toward_the_destination_star()
    {
        var w = new World { Galaxy = Galaxy.Parse(new[] { Yaml }) };
        w.Config.Systems = new List<string> { "Alpha Orbit", "Beta Station" };
        var st = new SimStation { Id = 1, Name = "Dock", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) };
        w.SetStations("Alpha Orbit", new List<SimStation> { st });
        w.Lane(w.Stations["Alpha Orbit"][0], "Beta Station", out var near, out var far);
        Assert.True((far - near).Normalized.X > 0.99f);   // Beta is at +x
    }
}

public class SimScaleTests
{
    [Fact]
    public void A_galaxy_sized_world_steps_fast()
    {
        var w = new World();
        var rng = new Random(5);
        for (var i = 0; i < 58; i++)
        {
            var sys = "S" + i;
            w.Config.Systems.Add(sys);
            w.SetStations(sys, new List<SimStation> { new SimStation { Id = 1000 + i, Name = "St" + i, Pos = new LivingSystems.Core.Flight.Vec3(i * 100, 0, 0) },
                                                      new SimStation { Id = 2000 + i, Name = "Tr" + i, Pos = new LivingSystems.Core.Flight.Vec3(i * 100, 0, 5000) } });
        }
        foreach (var sys in w.Config.Systems.ToList()) w.SeedAt(sys, 1 + rng.Next(2), rng, 0, rng.NextDouble() < 1 / 3.0 ? 1 : 0);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var worst = 0.0;
        for (var t = 15.0; t <= 24 * 3600; t += 15)
        {
            var s0 = sw.Elapsed.TotalMilliseconds;
            w.Step(t, rng);
            worst = Math.Max(worst, sw.Elapsed.TotalMilliseconds - s0);
        }
        Assert.True(w.Events.Count > 0);
        Assert.True(worst < 250, $"worst step {worst:0} ms, total {sw.Elapsed.TotalSeconds:0.0} s for 24 h of 15 s ticks");
    }
}

public class SimFocusTests
{
    static World Make()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B", "C" };
        foreach (var s in w.Config.Systems)
            w.SetStations(s, new List<SimStation> { new SimStation { Id = s[0], Name = "Trading " + s, Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Config.LegAmbush = false;   // the ambush-spot geometry (what live play uses), not the sim-only leg roll
        return w;
    }

    [Fact]
    public void Warm_start_puts_traders_on_lanes_and_pirates_mid_lurk()
    {
        var w = Make();
        var added = w.SeedAt("A", 30, new Random(4), 1000, 5, warm: true);
        Assert.True(added.Count(a => a.IsTrader && (a.State == "out" || a.State == "in")) >= 12);
        Assert.All(added.Where(a => a.State == "out" || a.State == "in"), a => Assert.True(a.Since < 1000 && a.Until > 1000));
        Assert.All(added.Where(a => !a.IsTrader), a => Assert.True(a.Since <= 1000));
    }

    [Fact]
    public void Populate_fills_the_players_neighbourhood_and_recycle_retires_the_furthest_first()
    {
        var w = Make();
        w.Focus = new List<string> { "A" };
        w.Populate(new Random(1), 1000);
        Assert.True(w.Agents.Values.Count(a => a.IsTrader && a.System == "A") >= 1);
        Assert.True(w.Agents.Values.Count(a => !a.IsTrader && a.System == "A") >= 1);
        w.Config.TraderBudget = 2;
        var far = w.Add(new SimAgent { Id = "tf", System = "C", State = "dock", Until = 2000 });
        var met = w.Add(new SimAgent { Id = "tm", System = "C", State = "dock", Until = 2000 });
        w.LastTick = 5000;
        w.Remember(met, "memory.escort.pid:1", "done");
        w.Recycle(s => s == "C" ? 500 : 0);
        Assert.Null(w.Get("tf"));            // furthest, nobody knows it: gone
        Assert.NotNull(w.Get("tm"));         // remembers a player: kept…
        w.LastTick = 5000 + World.MetPinSeconds + 1;
        w.Config.TraderBudget = 0;
        w.Recycle(s => s == "C" ? 500 : 0);
        Assert.Null(w.Get("tm"));            // …for a while, not forever
        // and only so many at once: the ones met longest ago go first
        w.Config.TraderBudget = 0;
        for (var i = 0; i < World.MetPinMax + 5; i++) { var x = w.Add(new SimAgent { Id = "m" + i, System = "C", State = "dock", Until = 1e9 }); w.LastTick = 100000 + i; w.Remember(x, "memory.hurt.pid:1", "x"); }
        w.Recycle(s => s == "C" ? 500 : 0);
        Assert.Equal(World.MetPinMax, w.Agents.Values.Count(a => a.Id.StartsWith("m")));
        Assert.Null(w.Get("m0"));            // met first → released first
        Assert.NotNull(w.Get("m" + (World.MetPinMax + 4)));
    }

    [Fact]
    public void Recycle_retires_ships_that_were_live_once_but_never_fighters_or_live_ones()
    {
        var w = Make(); w.Config.TraderBudget = 0; w.Config.PirateBudget = 0;
        var seen = w.Add(new SimAgent { Id = "ts", System = "C", State = "dock", Until = 2000, EntityId = 4242 });   // live once, back in the sim
        var live = w.Add(new SimAgent { Id = "tl", System = "C", State = "out", Until = 2000, EntityId = 4243, Live = "C" });
        var tr = w.Add(new SimAgent { Id = "tf", System = "C", State = "out", Dest = "B", Until = 1000, StationId = 1 });
        var p = w.Add(new SimAgent { Id = "pf", Kind = "pirate", System = "C", State = "lurk", Until = 1e6, SpotStation = 1, SpotOther = "B", SpotFrac = 0.3 });
        var f = w.StartFight(tr, p, 900, new Random(1));
        var removed = new List<SimAgent>();
        w.Recycle(s => 500, removed);
        Assert.Null(w.Get("ts"));
        Assert.Contains(removed, x => x.Id == "ts" && x.EntityId == 4242);   // the Dedi destroys a hull still on record
        Assert.NotNull(w.Get("tl"));
        Assert.NotNull(w.Get("tf")); Assert.NotNull(w.Get("pf"));           // mid-fight: never pulled out of it
        Assert.Equal(2, w.MembersOf(f).Count);
    }

    [Fact]
    public void Recycle_never_eats_the_players_neighbourhood_even_when_everyone_far_is_unrecyclable()
    {
        var w = Make(); w.Config.TraderBudget = 1;
        w.Add(new SimAgent { Id = "tf1", System = "C", State = "dock", Until = 2000, Live = "C" });   // far, can't go
        w.Add(new SimAgent { Id = "tf2", System = "C", State = "dock", Until = 2000, Live = "C" });
        w.Focus = new List<string> { "A" };
        var near = w.Add(new SimAgent { Id = "tn", System = "A", State = "dock", Until = 2000 });
        var inbound = w.Add(new SimAgent { Id = "tw", System = "B", Dest = "A", State = "warp", Until = 2000 });
        w.Recycle(s => s == "A" ? 0 : 500);
        Assert.NotNull(w.Get("tn")); Assert.NotNull(w.Get("tw"));
    }

    [Fact]
    public void Patrols_in_safe_systems_sometimes_maul_or_kill_pirates()
    {
        var w = Make();
        var rng = new Random(2); var patrols = 0;
        for (var i = 0; i < 300; i++)
        {
            w.Agents.Clear(); w.Fights.Clear(); w.Events.Clear();
            w.Add(new SimAgent { Id = "t1", System = "A", State = "out", Dest = "B", Until = 1000 });
            w.Add(new SimAgent { Id = "p1", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 'A', SpotOther = "B", SpotFrac = 0.3 });
            var ev = w.Step(1, rng).Concat(w.Step(1800, rng)).ToList();
            Assert.Contains(ev, e => e.Kind == "attack");
            if (ev.Any(e => e.Kind == "patrol")) patrols++;
        }
        Assert.InRange(patrols, 5, 200);   // a real chance, not a sure thing
    }
}

public class SimBerthTests
{
    static World Make(bool box, LivingSystems.Core.Flight.Quat rot)
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        var st = new SimStation { Id = 7, Name = "Lambda", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) };
        if (box) { st.HasBox = true; st.Box = new LivingSystems.Core.Flight.Vec3(10, 0, 0); st.Half = new LivingSystems.Core.Flight.Vec3(200, 50, 100); st.Rot = rot; }
        w.SetStations("A", new List<SimStation> { st });
        return w;
    }

    [Fact]
    public void The_berth_sits_just_outside_the_measured_hull_along_the_lane()
    {
        var w = Make(true, LivingSystems.Core.Flight.Quat.Identity);
        var st = w.Stations["A"][0];
        w.Lane(st, "B", out var near, out var far);
        var dir = (far - near).Normalized;
        var d = (near - st.Box).Length;
        // ray from the box centre leaves the box somewhere between 50 (thinnest half) and ~230 (corner), then + margin
        Assert.InRange(d, 50 + w.Config.BerthMargin - 1, 240 + w.Config.BerthMargin);
        Assert.True(d < w.Config.StationClear);
        var unmeasured = Make(false, LivingSystems.Core.Flight.Quat.Identity);
        unmeasured.Lane(unmeasured.Stations["A"][0], "B", out var n2, out _);
        Assert.Equal(unmeasured.Config.StationClear, (n2 - unmeasured.Stations["A"][0].Pos).Length, 1);
    }

    [Fact]
    public void Generated_names_dont_run_out_after_a_dozen()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A" };
        var added = w.SeedAt("A", 60, new Random(1), 0);
        Assert.Equal(60, added.Select(x => x.Name).Distinct().Count());
        Assert.DoesNotContain(added, x => x.Name.StartsWith("Hauler "));
    }

    [Fact]
    public void Docked_ships_get_separate_slots_and_a_live_docked_one_still_undocks_on_time()
    {
        var w = Make(true, LivingSystems.Core.Flight.Quat.Identity);
        var a = w.Add(new SimAgent { Id = "t1", System = "A", State = "dock", From = "B", Until = 500, StationId = 7 });
        var b = w.Add(new SimAgent { Id = "t2", System = "A", State = "dock", From = "B", Until = 500, StationId = 7 });
        Assert.True(w.Track(a, out var ta) && w.Track(b, out var tb));
        Assert.True((ta.A - w.Stations["A"][0].Box).Length < w.Config.StationClear);
        var c = w.Add(new SimAgent { Id = "t3", System = "A", State = "in", From = "B", Until = 400, Live = "A", StationId = 7, Role = "bulk", Speed = 50 });
        Assert.True(w.DockLive("t3", new Random(1), 300));
        Assert.Equal("dock", c.State);
        Assert.True(c.IsLive);
        w.Step(c.Until + 1, new Random(1));
        Assert.Equal("out", c.State);   // the sim undocked it, still live: the PfServer flies it out
        Assert.True(c.IsLive);
    }
}

public class SimLootTests
{
    static World Make()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        w.SetStations("A", new List<SimStation> { new SimStation { Id = 1, Name = "Dock", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Config.LegAmbush = false;   // the ambush-spot geometry (what live play uses), not the sim-only leg roll
        return w;
    }

    [Fact]
    public void Traders_load_their_lines_cargo_at_undock_and_deliver_it_at_the_dock()
    {
        var w = Make(); var rng = new Random(3); var loaded = 0; var rare = 0;
        for (var i = 0; i < 200; i++)
        {
            var a = new SimAgent { Id = "t" + i, Role = "courier" };
            w.Load(a, rng);
            if (a.CargoAmt == 0) continue;
            loaded++;
            if (a.Cargo == "Eden_TradePolarisT1") { rare++; Assert.Equal(1, a.CargoAmt); continue; }
            var value = a.CargoAmt * World.Prices[a.Cargo];
            Assert.InRange(value, 5000, 21000);   // a load is rolled by value (~40 % of a good Eden POI wreck)
        }
        Assert.InRange(loaded, 170, 200);   // couriers almost always fly loaded
        Assert.InRange(rare, 3, 30);        // the 100k package is a rare jackpot, not every courier
        foreach (var role in World.Roles) foreach (var item in role.Items) Assert.True(World.Prices.ContainsKey(item), item);
        var t = w.Add(new SimAgent { Id = "x", System = "A", State = "in", From = "B", Until = 10, Role = "bulk", Cargo = "IronOre", CargoAmt = 300 });
        w.Step(11, rng);
        Assert.Equal("dock", t.State);
        Assert.Equal(0, t.CargoAmt);
    }

    [Fact]
    public void A_kill_leaves_a_wreck_and_the_pirate_carries_its_share_until_it_fences_it()
    {
        var w = Make(); w.Config.FleeChance = 0; w.Config.PatrolChance = 0; w.Config.RoundHit = 5;   // the hauler dies in round one
        w.Config.MoneyFlow = false;   // a fight, not a toll
        w.Config.WreckChance = 1;
        w.Config.ReplaceSeconds = 1e9;   // no replacement hauler to get robbed by the same pirate before the fence check
        var t = w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "out", Dest = "B", Until = 1000, Strength = 0, Role = "bulk", Cargo = "IronOre", CargoAmt = 400, StationId = 1 });
        var p = w.Add(new SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 1, SpotOther = "B", SpotFrac = 0.3 });
        var ev = w.Step(1, new Random(1)).Concat(w.Step(2000, new Random(1))).ToList();
        Assert.False(t.Alive);
        var wr = Assert.Single(w.Wrecks.Values);
        Assert.Equal("IronOre", p.Loot);
        Assert.Equal(400, p.LootAmt + wr.Amt);
        Assert.InRange(p.LootAmt, 200, 320);
        Assert.Contains(ev, e => e.Kind == "wreck");
        // the beacon runs out (towed) and the loot gets fenced
        var towed = w.Step(2000 + w.Config.WreckMax + 10, new Random(1));
        Assert.Empty(w.Wrecks);
        Assert.Contains(towed, e => e.Kind == "towed" && e.Text.Contains("Mule"));
        Assert.Contains("wreck towed", w.FeedLine(towed.First(e => e.Kind == "towed")));
        Assert.Contains(World.Parse(w.Serialize(), out _).Events, e => e.Kind == "towed" && e.Name == "Mule");
        w.Step(2000 + w.Config.FenceSeconds + 100, new Random(1));   // the kill was ~t=885 (lane pass + first round)
        Assert.Equal(0, p.LootAmt);
    }

    [Fact]
    public void A_pirate_keeps_every_kind_of_loot_and_it_survives_a_save()
    {
        var p = new SimAgent { Id = "p1", Kind = "pirate", Name = "Rust Jaw" };
        p.AddLoot("IronOre", 300); p.AddLoot("GoldIngot", 40); p.AddLoot("IronOre", 100);
        Assert.Equal(2, p.Hold.Count);
        Assert.Equal(440, p.LootAmt);
        Assert.Equal("IronOre", p.Loot); Assert.Equal(400, p.LootMainAmt);
        var back = World.Parse(World.Header + "\n" + p.ToLine(), out _).Agents["p1"];
        Assert.Equal(new[] { "IronOre:400", "GoldIngot:40" }, back.Hold.Select(x => x.Item + ":" + x.Amt));
        var old = World.Parse(World.Header + "\nagent id=p2 kind=pirate loot=CobaltOre lamt=120", out _).Agents["p2"];   // older saves: one kind
        Assert.Equal(120, old.LootAmt);
    }

    [Fact]
    public void Wrecks_are_capped_per_system_and_only_near_players()
    {
        var w = Make(); var rng = new Random(1);
        var a = new SimAgent { Id = "t1", Name = "X", System = "A" };
        for (var i = 0; i < 5; i++) w.Wreck(a, new LivingSystems.Core.Flight.Vec3(i, 0, 0), "IronOre", 10, i, rng);
        Assert.Equal(w.Config.WrecksPerSystem, w.Wrecks.Count);
        w.Focus = new List<string> { "B" };
        Assert.Null(w.Wreck(new SimAgent { Id = "t2", System = "A" }, default(LivingSystems.Core.Flight.Vec3), "IronOre", 10, 9, rng));
        var back = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(w.Wrecks.Count, back.Wrecks.Count);
        Assert.Equal("Iron Ore", World.Humanize("IronOre"));
        // spawned hulls past their beacon time (maybe orphaned in an unloaded playfield) don't hold the slots
        var w2 = Make();
        for (var i = 0; i < 2; i++) { var wr = w2.Wreck(a, new LivingSystems.Core.Flight.Vec3(i * 1000, 0, 0), "IronOre", 10, i, rng); wr.Entity = 100 + i; }
        Assert.Null(w2.Wreck(a, default(LivingSystems.Core.Flight.Vec3), "IronOre", 10, 100, rng));   // both spawned, beacons running: full
        Assert.NotNull(w2.Wreck(a, default(LivingSystems.Core.Flight.Vec3), "IronOre", 10, w2.Config.WreckMax + 10, rng));
        Assert.Equal(3, w2.Wrecks.Count);   // the orphans stay on record until their playfield's PfServer clears them
    }
}

public class SimRaiderTests
{
    static World Make()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B", "C", "D" };
        foreach (var s in w.Config.Systems)
            w.SetStations(s, new List<SimStation> { new SimStation { Id = s[0], Name = "Trading " + s, Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Focus = new List<string>(w.Config.Systems);
        return w;
    }

    [Fact]
    public void Pirate_kills_damp_the_raiders_and_survive_a_reload()
    {
        var w = Make(); w.LastTick = 1000;
        for (var i = 0; i < 8; i++)
        {
            var p = w.Add(new SimAgent { Id = "p" + i, Name = "Rust Jaw " + i, Kind = "pirate", System = "A", State = "lurk", Until = 1e6, Live = "x" });
            w.EndLive(p.Id, "dead", 1000);
        }
        var back = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Contains("pkills list=", w.Serialize());
        Assert.Equal(w.Serialize(), back.Serialize());
    }

    [Fact]
    public void A_crew_stays_home_while_enough_raiders_are_out()
    {
        var w = Make(); w.Config.RaidersPerSystem = 0.25;   // 4 lanes → 1 raider
        w.Hubs["h1"] = new SimHub { Id = "h1", System = "A", Name = "Den", Size = 5 };
        w.Add(new SimAgent { Id = "r1", Name = "Out", Kind = "pirate", System = "B", State = "lurk", Until = 1e6, Home = "h1" });
        for (var i = 0; i < 4; i++) w.Add(new SimAgent { Id = "g" + i, Name = "Home " + i, Kind = "pirate", System = "A", State = "home", Until = 100, Home = "h1" });
        w.Step(200, new Random(1));
        Assert.All(w.Agents.Values.Where(a => a.Id.StartsWith("g")), a => Assert.Equal("home", a.State));
        w.Config.RaiderCap = false;
        foreach (var a in w.Agents.Values.Where(a => a.Id.StartsWith("g"))) a.Until = 300;
        w.Step(400, new Random(1));
        Assert.Contains(w.Agents.Values, a => a.Id.StartsWith("g") && a.State != "home");   // without the cap the spare crews go raiding
    }
}

public class SimStatsTests
{
    [Fact]
    public void A_kill_is_counted_for_the_system_the_hub_and_the_trader_and_survives_a_reload()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        w.SetStations("A", new List<SimStation> { new SimStation { Id = 1, Name = "Dock", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Config.LegAmbush = false; w.Config.FleeChance = 0; w.Config.PatrolChance = 0; w.Config.RoundHit = 5; w.Config.ReplaceSeconds = 1e9;
        w.Config.MoneyFlow = false;   // a fight, not a toll
        w.Hubs["h1"] = new SimHub { Id = "h1", System = "C", Name = "Den", Size = 3, Manned = true };
        var t = w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "out", Dest = "B", Until = 1000, Strength = 0, Role = "bulk", Cargo = "IronOre", CargoAmt = 400, StationId = 1 });
        w.Add(new SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, Home = "h1", SpotStation = 1, SpotOther = "B", SpotFrac = 0.3 });
        w.Step(1, new Random(1)); w.Step(2000, new Random(1));
        Assert.False(t.Alive);
        Assert.Equal(1, w.Stat("sys|A|atk"));
        Assert.Equal(1, w.Stat("sys|A|kill"));
        Assert.Equal(1, w.Stat("hub|h1|kills"));
        Assert.True(w.Stat("hub|h1|taken") > 0);
        Assert.Equal(World.Value("IronOre", 400), World.Tallied(t, "g.lost"));
        var back = World.Parse(w.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(w.Stats, back.Stats);
        Assert.Contains(w.StatsReport(), l => l.StartsWith("Den: took"));
    }
}

public class SimWealthTests
{
    [Fact]
    public void A_rich_hub_keeps_more_crews_and_pays_its_cash_out_when_the_last_one_dies()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        var hub = new SimHub { Id = "h1", System = "C", Name = "Den", Size = 3, Manned = true, Cash = 250000 };
        w.Hubs["h1"] = hub;
        Assert.Equal(2, w.HubTier(hub));
        Assert.Equal(5, w.HubSize(hub));
        var p = w.Add(new SimAgent { Id = "p1", Name = "Last Man", Kind = "pirate", System = "C", State = "home", Home = "h1", Until = 1e9, Live = "x" });
        w.LastTick = 100;
        w.EndLive(p.Id, "dead", 100);
        Assert.Equal(0, hub.Cash);
        var box = Assert.Single(hub.Stash, s => s.Item == World.Strongbox);
        Assert.Equal(250000, box.Amt);
        Assert.Contains(w.Events, e => e.Kind == "broken");
        Assert.Equal(0, World.StashValue(hub));   // the strongbox isn't fenced over the stash cap
        var back = World.Parse(w.Serialize(), out _);
        Assert.Equal(250000, back.Hubs["h1"].Stash.Single(s => s.Item == World.Strongbox).Amt);
    }

    [Fact]
    public void A_gang_spends_its_cash_over_time()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A" };
        var hub = new SimHub { Id = "h1", System = "C", Name = "Den", Size = 3, Manned = true, Cash = 100000 };
        w.Hubs["h1"] = hub; w.LastTick = 0;
        for (var t = 3600; t <= 24 * 3600; t += 3600) w.Step(t, new Random(1));
        Assert.InRange(hub.Cash, 45000, 52000);   // ~3 %/h (no crews alive, so no upkeep)
    }
}

public class SimBreakTests
{
    static World Make(out SimHub hub)
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        hub = new SimHub { Id = "h1", System = "C", Name = "Den", Size = 3, Manned = true, Cash = 80000 };
        w.Hubs["h1"] = hub; w.LastTick = 100; w.Focus = new List<string> { "A", "B", "C" };
        return w;
    }

    [Fact]
    public void Killing_the_crews_at_home_with_a_player_there_breaks_the_hub_even_with_a_roamer_out()
    {
        var w = Make(out var hub);
        w.Occupied = new HashSet<string> { "C" };
        var roamer = w.Add(new SimAgent { Id = "r1", Name = "Away", Kind = "pirate", System = "A", State = "lurk", Home = "h1", Until = 1e9 });
        var g = w.Add(new SimAgent { Id = "g1", Name = "Guard", Kind = "pirate", System = "C", State = "home", Home = "h1", Until = 1e9, Live = "x" });
        w.EndLive(g.Id, "dead", 100);
        Assert.True(w.IsBroken(hub));
        Assert.Equal(0, hub.Cash);
        Assert.Equal(80000, hub.Stash.Single(s => s.Item == World.Strongbox).Amt);
        var box = Assert.Single(w.Wrecks.Values, x => x.Hub == "h1");   // the guaranteed box
        Assert.Equal("Den hoard", box.Name);
        Assert.InRange(hub.BrokenUntil - 100, 21 * 3600 * 0.75, 21 * 3600 * 1.25);
        Assert.Null(w.HubOf(roamer));   // no home to go back to while it's quiet
        Assert.True(roamer.Alive);
        // no recruits while quiet
        Assert.DoesNotContain(w.Populate(new Random(1), 200), a => a.Home == "h1");
        var back = World.Parse(w.Serialize(), out _);
        Assert.Equal(hub.BrokenUntil, back.Hubs["h1"].BrokenUntil, 3);
    }

    [Fact]
    public void Without_a_player_there_a_roamer_out_keeps_the_hub_standing()
    {
        var w = Make(out var hub);
        w.Add(new SimAgent { Id = "r1", Name = "Away", Kind = "pirate", System = "A", State = "lurk", Home = "h1", Until = 1e9 });
        var g = w.Add(new SimAgent { Id = "g1", Name = "Guard", Kind = "pirate", System = "C", State = "home", Home = "h1", Until = 1e9, Live = "x" });
        w.EndLive(g.Id, "dead", 100);
        Assert.False(w.IsBroken(hub));
        Assert.Equal(80000, hub.Cash);
    }

    [Fact]
    public void A_rich_hub_buys_its_recruits_but_not_below_the_floor_or_with_a_player_there()
    {
        var w = Make(out var hub); hub.NextRecruit = 0;
        w.Populate(new Random(1), 200);
        Assert.Equal(70000, hub.Cash);   // one bought
        Assert.InRange(hub.NextRecruit - 200, 2 * 3600, 4 * 3600);
        hub.Cash = 40000; hub.NextRecruit = 0;
        w.Populate(new Random(1), 300);
        Assert.Equal(40000, hub.Cash);   // 40k − 10k < 35k floor: a free one, slow
        Assert.InRange(hub.NextRecruit - 300, 6 * 3600, 12 * 3600);
    }
}

public class SimMoneyTests
{
    static World Make()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        w.SetStations("A", new List<SimStation> { new SimStation { Id = 1, Name = "Dock", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.SetStations("B", new List<SimStation> { new SimStation { Id = 2, Name = "Pier", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Config.LegAmbush = false; w.Config.TollChance = 1; w.Config.PatrolChance = 0;
        return w;
    }

    [Fact]
    public void A_trader_that_can_pay_buys_its_way_past_a_lone_pirate_who_banks_it_at_home()
    {
        var w = Make();
        w.Hubs["h1"] = new SimHub { Id = "h1", System = "C", Name = "Den", Size = 3, Manned = true };
        var t = w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "out", Dest = "B", Until = 1000, Strength = 0, Role = "bulk", Cargo = "IronOre", CargoAmt = 100, StationId = 1 });
        t.Extra["cash"] = "20000";
        var p = w.Add(new SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, Home = "h1", SpotStation = 1, SpotOther = "B", SpotFrac = 0.3 });
        var ev = w.Step(1, new Random(1)).Concat(w.Step(1000, new Random(1))).ToList();
        Assert.Contains(ev, e => e.Kind == "toll");
        Assert.DoesNotContain(ev, e => e.Kind == "attack");
        Assert.Equal(18000, w.Cash(t));
        Assert.Equal(2000, w.Cash(p));
        w.Stash(p, w.Hubs["h1"], 2000);
        Assert.Equal(0, w.Cash(p));
        Assert.Equal(2000, w.Hubs["h1"].Cash);   // whole, no fence cut
    }

    [Fact]
    public void A_broke_trader_cannot_pay_and_a_delivery_pays_the_purse()
    {
        var w = Make();
        var t = w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "out", Dest = "B", Until = 1000, Strength = 0, Role = "bulk", Cargo = "IronOre", CargoAmt = 100, StationId = 1 });
        t.Extra["cash"] = "100";
        w.Add(new SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, SpotStation = 1, SpotOther = "B", SpotFrac = 0.3 });
        var ev = w.Step(1, new Random(1)).Concat(w.Step(1000, new Random(1))).ToList();
        Assert.Contains(ev, e => e.Kind == "attack");   // nothing to pay with
        var d = w.Add(new SimAgent { Id = "t2", Name = "Ox", System = "B", State = "in", Until = 1001, Strength = 0, Role = "bulk", Cargo = "IronOre", CargoAmt = 500, StationId = 2 });
        d.Extra["cash"] = "1000";
        w.Step(1001, new Random(1));   // docks (delivered); the stay is ≥ 5 min, so no second trip yet
        Assert.Equal(1000 + (int)(World.Value("IronOre", 500) * w.Config.TradeMargin), w.Cash(d));
    }

    [Fact]
    public void A_dead_pirates_pocket_goes_into_its_wreck_and_survives_a_reload()
    {
        var w = Make();
        var p = w.Add(new SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "A", State = "lurk", Until = 1e6 });
        p.Extra["cash"] = "7000";
        var wr = w.Wreck(p, new LivingSystems.Core.Flight.Vec3(0, 0, 0), "", 0, 10, new Random(1));
        Assert.Equal(7000, wr.Cash);
        Assert.Equal(0, w.Cash(p));
        Assert.Equal(7000, World.Parse(w.Serialize(), out _).Wrecks[wr.Id].Cash);
    }
}

public class TemplateStationTests
{
    const string Yaml = "PlayfieldType: Space\nPOIs:\n  - Type: BA_Alien\n    Prefab: Eden_BA_DummyPOI\n    Name: Dummy\n    Faction: Civilian\n    Pos:\n    - 0\n    - 0\n    - 0\n" +
                        "  - Type: BA_Alien\n    Prefab: RE2_TSO_Polaris02\n    Mode: Survival\n    Name: Trading Station Iota\n    Faction: Polaris\n    Properties:\n    - Key: Discovery\n      Value: False\n    Pos:\n    - 120\n    - -5\n    - 3000\n    Rot:\n    - 0\n    - 0\n    - 0\n" +
                        "  - Type: BA_Alien\n    Prefab: Eden_Territory_Polaris\n    Name: Territory\n    Faction: Polaris\n    Pos: [1, 2, 3]\n" +
                        "  - Type: BA_Alien\n    Prefab: RE2_BAO_PolRefinery\n    Name: Orbital Refinery\n    Faction: Polaris\n    Pos: [10, 20, 30]\n";

    [Fact]
    public void Template_stations_are_the_whitelisted_prefabs_with_their_names_and_positions()
    {
        var l = TemplateStations.Parse("Trade Station Alpha", Yaml);
        Assert.Equal(new[] { "Trading Station Iota", "Orbital Refinery" }, l.Select(x => x.Name).ToArray());
        Assert.Equal(3000, l[0].Pos.Z, 3); Assert.Equal(-5, l[0].Pos.Y, 3);
        Assert.Equal(20, l[1].Pos.Y, 3);
        Assert.All(l, x => Assert.True(TemplateStations.IsPlaceholder(x)));
        Assert.Equal(l[0].Id, TemplateStations.Parse("Trade Station Alpha", Yaml)[0].Id);   // stable across reads
        Assert.NotEqual(l[0].Id, TemplateStations.Parse("Trade Station Beta", Yaml)[0].Id);
    }
}

public class BanditTests
{
    [Fact]
    public void Warlord_pois_are_read_from_a_template_and_hubs_keep_off_their_turf()
    {
        var yaml = "POIs:\n  - Type: BA_Alien\n    Prefab: Eden_BAO_BanditDefT1_1\n    Name: Sentry II\n    Faction: Warlord\n    Pos: [1000, 0, 0]\n" +
                   "  - Type: BA_Alien\n    Prefab: RE2_TSO_Polaris02\n    Name: Trading Station Iota\n    Faction: Polaris\n    Pos: [0, 0, 0]\n";
        var guns = TemplateStations.Bandits(yaml);
        var g = Assert.Single(guns);
        Assert.Equal("Sentry II", g.Name);
        var w = new World();
        w.Bandits["Belt"] = guns;
        var h = new SimHub { Id = "h1", System = "Belt", Name = "Den", Pos = new LivingSystems.Core.Flight.Vec3(1500, 0, 0) };
        Assert.True(w.KeepOffTurf(h));
        Assert.True((h.Pos - g.Pos).Length >= w.Config.BanditTurf);
        Assert.False(w.KeepOffTurf(h));
    }

    [Fact]
    public void A_sim_leg_through_bandit_space_sometimes_takes_fire_but_never_dies_of_it()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" }; w.Config.BanditFire = 1;
        w.SetStations("A", new List<SimStation> { new SimStation { Id = 1, Name = "Dock", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Bandits["A"] = new List<SimPoi> { new SimPoi { Name = "Sentry II", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } };
        var t = w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "dock", Until = 10, Role = "bulk", StationId = 1, Hull = 0.1 });
        var ev = w.Step(20, new Random(1));
        Assert.Contains(ev, e => e.Kind == "bandits" && e.Text.Contains("Sentry II"));
        Assert.True(t.Alive); Assert.True(t.Hull >= 0.05);
        w.Occupied = new HashSet<string> { "A" };   // a player there: live rules, no dice
        t.State = "dock"; t.Until = 30; t.Hull = 1;
        Assert.DoesNotContain(w.Step(40, new Random(1)), e => e.Kind == "bandits");
    }
}

public class SimFledTests
{
    [Fact]
    public void A_trader_that_fled_and_jumped_live_warps_on_to_another_system()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B", "C" };
        var t = w.Add(new SimAgent { Id = "t1", Name = "Bent Herring", System = "A", State = "dock", Until = 5000, Live = "A", Role = "bulk", Cargo = "IronOre", CargoAmt = 100, StationId = 1 });
        w.EndLive("t1", "jumped", 100);
        Assert.Equal("warp", t.State);
        Assert.NotEqual("A", t.Dest);
        Assert.Equal(100, t.CargoAmt);
        Assert.False(t.IsLive);
    }
}
