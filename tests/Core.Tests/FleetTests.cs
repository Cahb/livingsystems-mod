using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Sim;
using Xunit;

public class FleetTests
{
    static World Yard(double now = 100)
    {
        var w = new World(); w.Config.Systems = new List<string> { "Port" }; w.Config.TugSafeMin = 0; w.Config.TugFleet = true;
        w.SetStations("Port", new List<SimStation> { new SimStation { Id = 1, Name = "Trading Station Port", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.LastTick = now;
        return w;
    }

    static SimWreck Beacon(World w, string id, double t) =>
        w.Wrecks[id] = new SimWreck { Id = id, System = "Gulch", Name = id.ToUpperInvariant(), Kind = "trader", Time = t, Until = World.Never, Salvage = true };

    [Fact]
    public void A_yard_has_its_fleet_at_once_and_the_board_never_takes_the_duty_tug()
    {
        var w = Yard(); var rng = new Random(1);
        w.Step(150, rng);
        Assert.Equal(2, w.IdleAt("Port").Count);                                   // a yard new to the sim: its tugs are there
        Beacon(w, "a", 1);
        w.Step(800, rng);
        var sent = Assert.Single(w.Agents.Values, a => a.IsTug && a.State != "idle");
        Assert.Equal("Gulch", sent.Extra["tug"]);
        Assert.Single(w.IdleAt("Port"));                                           // the duty tug stays
        var job = w.Wrecks["a"];
        Beacon(w, "b", 2); w.Wrecks["b"].System = "Far";                          // another site; the sent tug still has its own job
        w.Step(1500, rng);
        Assert.Equal("", w.Wrecks["b"].Tug);                                       // nobody to spare: the job waits
        Assert.True(w.Stat("fleet|board|none") >= 1);
    }

    [Fact]
    public void A_tug_back_from_its_job_waits_at_the_yard_instead_of_retiring()
    {
        var w = Yard(); var rng = new Random(1);
        w.Step(150, rng); Beacon(w, "a", 1);
        w.Step(800, rng);
        var tug = w.Agents.Values.Single(a => a.IsTug && a.State != "idle");
        for (var t = 900.0; tug.State != "idle" && t < 40000; t += 60) w.Step(t, rng);
        Assert.Equal("idle", tug.State);
        Assert.Equal("Port", tug.System);
        Assert.False(w.Wrecks.ContainsKey("a"));
        Assert.Equal(2, w.IdleAt("Port").Count);
        Assert.True(w.Stat("eco|tug|sold") > 0);
    }

    [Fact]
    public void A_hire_takes_a_real_tug_and_comes_back_through_the_sim()
    {
        var w = Yard(); var rng = new Random(1);
        w.Step(150, rng);
        var a = w.HireTug("Port", 1189, "Beta", 4500, 200);
        var b = w.HireTug("Port", 1190, "Beta", 3000, 200);
        Assert.NotNull(a); Assert.NotNull(b);                                       // players get the duty tug too
        Assert.Null(w.HireTug("Port", 1191, "Beta", 3000, 200));                   // none left: no magic tug
        Assert.Same(a, w.HiredBy(1189));
        Assert.Equal(4500, World.HireFee(a));
        a.Extra["hireent"] = "777";
        var back = World.Parse(w.Serialize(), out _);                               // a hire survives a reload
        Assert.Equal(777, World.HireEntity(back.HiredBy(1189)));

        w.HireEnds(a, "dismiss", "SteelPlate:140,Motor:3", 300);
        Assert.Equal("warp", a.State);
        Assert.True(a.IsLive);                                                      // its hull flies out live first
        Assert.Null(w.HiredBy(1189));
        w.EndLive(a.Id, "left", 330);                                               // out of range: the sim's warp home goes on
        for (var t = 360.0; a.State != "idle" && t < 40000; t += 60) w.Step(t, rng);
        Assert.Equal("idle", a.State);
        Assert.Contains(w.Events, e => e.Agent == a.Id && e.Text.Contains("sold") && e.Text.Contains("Steel Plate"));

        w.HireEnds(b, "release", "", 400);
        Assert.Equal("idle", b.State);                                              // never got there: back on duty
        b.State = "dock"; b.System = "Port";                                        // back at its berth with nothing to do: free
        Assert.Contains(b, w.IdleAt("Port"));
    }

    [Fact]
    public void A_dismissed_hire_pays_the_player_on_the_sale_not_at_the_click()
    {
        var w = Yard(); var rng = new Random(1);
        w.Step(150, rng);
        var a = w.HireTug("Port", 1189, "Beta", 4500, 200);
        w.HireEnds(a, "dismiss", "SteelPlate:140,Motor:3", 300, 0.5);
        Assert.Empty(w.Owed);                                                       // nothing yet: it has to get home and sell
        var worth = World.Value("SteelPlate", 140) + World.Value("Motor", 3);
        w.EndLive(a.Id, "left", 330);
        for (var t = 360.0; a.State != "idle" && t < 40000; t += 60) w.Step(t, rng);
        var o = Assert.Single(w.Owed);
        Assert.Equal(1189, o.Key);
        Assert.Equal((int)Math.Round(worth * 0.5), o.Value.Credits);
        Assert.Contains("sold the haul", o.Value.Note);
        Assert.False(a.Extra.ContainsKey("payto"));                                // paid once
        var back = World.Parse(w.Serialize(), out _);                               // owed money survives a restart
        Assert.Equal(o.Value.Credits, back.Owed[1189].Credits);
    }

    [Fact]
    public void A_hired_tug_out_of_range_stays_hired_with_its_hold_and_its_cut_goes_on()
    {
        var w = Yard(); var rng = new Random(1);
        w.Step(150, rng);
        var a = w.HireTug("Port", 1189, "Beta", 10000, 200);
        a.Extra["hireent"] = "777";
        w.HireOffline(a, "SteelPlate:900,Motor:3", "10,20,30", 4242, "Selene Battlecruiser", 0.4, "51,52", 0, 1000);
        Assert.Same(a, w.HiredBy(1189));                                            // still theirs
        Assert.True(World.HireOff(a));
        Assert.Equal(0, World.HireEntity(a));                                       // its hull went
        Assert.Equal(0.4, World.HireShare(a, 1000), 6);
        Assert.Equal(0.7, World.HireShare(a, 1000 + World.HireOffSeconds / 2), 6);   // half the bar: half the rest
        Assert.Equal(1.0, World.HireShare(a, 1000 + World.HireOffSeconds * 3), 6);
        for (var t = 1060.0; t < 9000; t += 600) w.Step(t, rng);
        var back = World.Parse(w.Serialize(), out _).HiredBy(1189);                  // a restart keeps all of it
        Assert.True(World.HireOff(back));
        Assert.Equal(4242, World.HireTarget(back));
        Assert.Equal(900, World.ParseHold(back.Extra[World.HoldKey])["SteelPlate"]);
        Assert.Equal("10,20,30", back.Extra["hirepos"]);
        Assert.Equal("51,52", back.Extra["hirework"]);

        w.HireOnline(a, 888);
        Assert.False(World.HireOff(a));
        Assert.Equal(888, World.HireEntity(a));
        Assert.False(a.Extra.ContainsKey(World.HoldKey));                          // live: the crates hold it, not the record
        w.HireEnds(a, "dismiss", "", 9100);
        Assert.False(a.Extra.ContainsKey(World.HoldKey));                          // nothing aboard: nothing sold twice
    }

    [Fact]
    public void A_tug_lost_on_a_hire_is_rebuilt_at_its_yard()
    {
        var w = Yard(); var rng = new Random(1);
        w.Step(150, rng);
        var a = w.HireTug("Port", 1189, "Beta", 3000, 200);
        w.HireEnds(a, "lost", "", 300);
        Assert.False(w.Agents.ContainsKey(a.Id));
        w.Step(1000, rng);
        var slip = Assert.Single(w.Agents.Values, x => x.IsTug && x.State == "build");   // laid down, not there yet
        Assert.Single(w.IdleAt("Port"));
        for (var t = 1060.0; slip.State == "build" && t < 1000 + w.Config.TugBuildSeconds + 1200; t += 120) w.Step(t, rng);
        Assert.Equal("idle", slip.State);
        Assert.Equal(2, w.IdleAt("Port").Count);
        Assert.Equal(1, w.Stat("fleet|built"));
    }
    [Fact]
    public void The_yard_cut_is_half_for_two_hours_then_ten_more_an_hour_up_to_eighty()
    {
        var w = Yard(); w.Step(150, new Random(1));
        var a = w.HireTug("Port", 1189, "Beta", 4500, 200);
        Assert.Equal(0.5, w.HirePayout(a), 6);
        void Minutes(int m) { for (var i = 0; i < m; i++) World.HireTick(a, 60); }
        Minutes(119); Assert.Equal(0.5, w.HirePayout(a), 6);                        // call in, cut, send home: 50 %
        Minutes(1); Assert.Equal(0.4, w.HirePayout(a), 6);                          // 2 h: the first step
        Minutes(60); Assert.Equal(0.3, w.HirePayout(a), 6);
        Minutes(600); Assert.Equal(0.2, w.HirePayout(a), 6);                        // the cap: the yard takes 80 %, never more
        Assert.True(double.IsPositiveInfinity(w.NextCutIn(a, out _)));
    }

    [Fact]
    public void The_next_step_is_announced_ahead_and_counts_down()
    {
        var w = Yard(); w.Step(150, new Random(1));
        var a = w.HireTug("Port", 1189, "Beta", 4500, 200);
        Assert.Equal(7200, w.NextCutIn(a, out var next), 3); Assert.Equal(0.6, next, 6);
        for (var i = 0; i < 115; i++) World.HireTick(a, 60);                        // 1 h 55 min
        Assert.Equal(300, w.NextCutIn(a, out next), 3); Assert.Equal(0.6, next, 6);
        for (var i = 0; i < 10; i++) World.HireTick(a, 60);                         // 2 h 5 min
        Assert.Equal(3300, w.NextCutIn(a, out next), 3); Assert.Equal(0.7, next, 6);
    }

    [Fact]
    public void The_clock_is_the_hires_saved_with_the_tug_and_starts_over_on_a_new_hire()
    {
        var w = Yard(); w.Step(150, new Random(1));
        var a = w.HireTug("Port", 1189, "Beta", 4500, 200);
        for (var i = 0; i < 200; i++) World.HireTick(a, 60);                        // 3 h 20 min
        World.HireTick(a, 1e6);                                                      // a gap (server down) counts 2 min at most
        Assert.Equal(12120, World.HireClock(a), 3);
        var back = World.Parse(w.Serialize(), out _);
        var b = back.HiredBy(1189);
        Assert.Equal(12120, World.HireClock(b), 3);                                 // reloads, restarts: the same hire, the same clock
        Assert.Equal(0.3, back.HirePayout(b), 6);
        back.HireEnds(b, "release", "", 300);
        World.HireTick(b, 60);                                                       // not hired: nothing counts
        Assert.Equal(0, World.HireClock(b), 3);
        var again = back.HireTug("Port", 1190, "Beta", 4500, 400);
        Assert.Equal(0.5, back.HirePayout(again), 6);                               // a new hire starts at 50 %
    }
}
