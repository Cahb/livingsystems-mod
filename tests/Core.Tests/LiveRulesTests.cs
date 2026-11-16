using System.Linq;
using LivingSystems.Core.Flight;
using LivingSystems.Core.Sim;
using Xunit;

public class LiveRulesTests
{
    [Fact]
    public void Inbound_ship_stopping_100m_short_of_its_berth_counts_as_docked()
    {
        // the flight controller calls ~100 m "arrived"; a 60 m berth check left ships looping forever
        Assert.True(LiveRules.Arrived("in", 99f));
        Assert.False(LiveRules.Arrived("in", 400f));
        Assert.True(LiveRules.Arrived("out", 240f));
        Assert.False(LiveRules.Arrived("dock", 0f));   // parked ships never "arrive"
        Assert.False(LiveRules.Arrived("", 0f));       // no leg yet (re-adopted, waiting for traffic): never arrived
    }

    [Fact]
    public void Re_adopted_docked_ship_stays_at_its_berth_not_where_it_happens_to_sit()
    {
        var berth = new Vec3(10, 0, 500); var laneEnd = new Vec3(0, 0, 10000); var here = new Vec3(50, 0, 520);
        Assert.Equal("dock", LiveRules.ResumeLeg("dock"));
        Assert.Equal(berth, LiveRules.ResumeDest("dock", berth, laneEnd, here));
        Assert.Equal(here, LiveRules.ResumeDest("dock", default(Vec3), laneEnd, here));   // slot unknown: hold where it is
        Assert.Equal(laneEnd, LiveRules.ResumeDest("out", berth, laneEnd, here));
        Assert.Equal(laneEnd, LiveRules.ResumeDest("in", berth, laneEnd, here));
        Assert.Equal("out", LiveRules.ResumeLeg("arriving"));
        Assert.Equal(laneEnd, LiveRules.ResumeLaneStart("fight", berth, laneEnd));
    }

    [Fact]
    public void A_ship_that_does_not_move_for_90s_is_stuck_one_that_creeps_along_is_not()
    {
        var w = new LiveRules.StuckWatch();
        var p = new Vec3(0, 0, 0);
        Assert.False(w.Update(p, 0, 5000));
        Assert.False(w.Update(p + new Vec3(5, 0, 0), 60, 5000));
        Assert.True(w.Update(p + new Vec3(10, 0, 0), 95, 5000));
        var m = new LiveRules.StuckWatch();
        for (var t = 0; t <= 300; t += 10) Assert.False(m.Update(new Vec3(0, 0, t * 4f), t, 5000));   // 4 m/s: slow but going
        var atPoint = new LiveRules.StuckWatch();
        atPoint.Update(p, 0, 100); Assert.False(atPoint.Update(p, 200, 100));   // waiting at its warp point isn't stuck
    }

    [Fact]
    public void Wrecks_and_coreless_hulls_never_react_live_ships_do()
    {
        Assert.True(LiveRules.IsHulk("Wreck of Lucky Wagon", false));
        Assert.True(LiveRules.IsHulk("Late Barrow", true));
        Assert.False(LiveRules.IsHulk("Late Barrow", false));
    }

    [Fact]
    public void Traders_fly_Trader_colours_except_Polaris_hulls()
    {
        Assert.Equal(18, LiveRules.TraderGroup("CV_Freighter_SparrowA"));
        Assert.Equal(7, LiveRules.TraderGroup("CV_PolarisFreighterP016"));
    }

    [Fact]
    public void Station_control_speaks_every_two_minutes_and_once_when_cleared_never_on_arrival()
    {
        double next = 0; var cleared = false;
        Assert.Equal("", LiveRules.DockChatter(600, 0, ref next, ref cleared));        // arming, not talking
        Assert.Equal("", LiveRules.DockChatter(540, 60, ref next, ref cleared));
        Assert.Equal("loading", LiveRules.DockChatter(480, 120, ref next, ref cleared));
        Assert.Equal("", LiveRules.DockChatter(470, 130, ref next, ref cleared));
        Assert.Equal("cleared", LiveRules.DockChatter(15, 585, ref next, ref cleared));
        Assert.Equal("", LiveRules.DockChatter(10, 590, ref next, ref cleared));      // only once
    }

    [Fact]
    public void Patrols_scale_with_protection()
    {
        Assert.Equal(0, LiveRules.Safety(new string[0]));
        Assert.Equal(0.9, LiveRules.Safety(new[] { "Trading Station Lambda", "Orbital Refinery", "Zero Horizons" }), 3);
        Assert.Equal(1, LiveRules.Patrol(0.2).Count);
        Assert.Equal("Eden_OPV_Polaris1", LiveRules.Patrol(0.4).Prefab);
        Assert.Equal(2, LiveRules.Patrol(0.9).Count);
        Assert.True(LiveRules.PatrolDone(0, 10));
        Assert.True(LiveRules.PatrolDone(2, 601));
        Assert.False(LiveRules.PatrolDone(1, 300));
    }

    [Fact]
    public void Live_safety_matches_the_sims()
    {
        var w = new World();
        w.SetStations("A", new System.Collections.Generic.List<SimStation> { new SimStation { Id = 1, Name = "Trading Station Lambda" }, new SimStation { Id = 2, Name = "Orbital Refinery" } });
        Assert.Equal(w.Safety("A"), LiveRules.Safety(new[] { "Trading Station Lambda", "Orbital Refinery" }), 6);
    }
}

public class TollTests
{
    [Fact]
    public void Paying_off_a_gang_ship_costs_more_than_a_raider_and_the_price_holds()
    {
        var raider = LivingSystems.Core.Sim.LiveRules.TollPrice(1, "p1|pid:1");
        var gang = LivingSystems.Core.Sim.LiveRules.TollPrice(2.5, "p1|pid:1");
        Xunit.Assert.InRange(raider, 6000, 10000);
        Xunit.Assert.InRange(gang, 12000, 19000);
        Xunit.Assert.Equal(raider, LivingSystems.Core.Sim.LiveRules.TollPrice(1, "p1|pid:1"));   // no haggling by re-hailing
        Xunit.Assert.Equal(0, raider % 500);
    }
}

public class RequestTests
{
    [Fact]
    public void Some_crews_want_something_at_a_premium_and_it_runs_out_as_you_sell()
    {
        var w = new LivingSystems.Core.Sim.World();
        var rng = new System.Random(4);
        var wanting = 0;
        for (var i = 0; i < 500; i++)
        {
            var a = new LivingSystems.Core.Sim.SimAgent { Id = "t" + i, Kind = "trader", Role = "supply" };
            w.Restock(a, rng);
            var want = w.WantOf(a);
            if (want == null) continue;
            wanting++;
            var basePrice = LivingSystems.Core.Sim.World.Prices[want.Item];
            Xunit.Assert.InRange(want.Price, basePrice * 2 - 1, basePrice * 3 + 1);
            Xunit.Assert.False(string.IsNullOrEmpty(want.Why));
            var left = want.Left;
            w.Fulfil(a, 1);
            if (left > 1) Xunit.Assert.Equal(left - 1, w.WantOf(a).Left); else Xunit.Assert.Null(w.WantOf(a));
            w.Fulfil(a, 999);
            Xunit.Assert.Null(w.WantOf(a));
        }
        Xunit.Assert.InRange(wanting, 150, 250);   // ~40 %
    }
}

public class YardTests
{
    static LivingSystems.Core.Sim.World Make()
    {
        var w = new LivingSystems.Core.Sim.World();
        w.Config.Systems = new System.Collections.Generic.List<string> { "A", "B", "C" };
        w.SetStations("A", new System.Collections.Generic.List<LivingSystems.Core.Sim.SimStation> { new LivingSystems.Core.Sim.SimStation { Id = 1, Name = "Orbital Refinery" } });
        w.SetStations("B", new System.Collections.Generic.List<LivingSystems.Core.Sim.SimStation> { new LivingSystems.Core.Sim.SimStation { Id = 2, Name = "Trading Station Lambda" } });
        w.SetStations("C", new System.Collections.Generic.List<LivingSystems.Core.Sim.SimStation> { new LivingSystems.Core.Sim.SimStation { Id = 3, Name = "Mining Outpost" } });
        return w;
    }

    [Fact]
    public void A_yard_fixes_fully_other_stations_patch_and_the_screen_follows_the_stay()
    {
        Xunit.Assert.Equal(1.0, LivingSystems.Core.Sim.LiveRules.HullAfterStay(0.2, "Trading Station Lambda"));
        Xunit.Assert.Equal(0.4, LivingSystems.Core.Sim.LiveRules.HullAfterStay(0.2, "Orbital Refinery"), 6);
        Xunit.Assert.Equal(0.6, LivingSystems.Core.Sim.LiveRules.HullDuringStay(0.2, "Trading Station Lambda", 0.5), 6);
    }

    [Fact]
    public void After_a_repair_detour_the_trip_goes_on_with_its_cargo()
    {
        var w = Make(); var rng = new System.Random(1);
        var a = w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "t1", Name = "Penny", Kind = "trader", Role = "supply", System = "B", StationId = 2, State = "dock", Until = 100, Since = 0, Hull = 0.2, Speed = 40, Cargo = "Fruit", CargoAmt = 50 });
        a.Extra["detour"] = "C";
        w.Step(100, rng);
        Xunit.Assert.Equal("out", a.State);
        Xunit.Assert.Equal("C", a.Dest);          // resumes the interrupted trip
        Xunit.Assert.Equal(1.0, a.Hull);          // the yard fixed it
        Xunit.Assert.Equal("Fruit", a.Cargo);     // same load
        Xunit.Assert.Equal(50, a.CargoAmt);
        Xunit.Assert.False(a.Extra.ContainsKey("detour"));
    }

    [Fact]
    public void A_wrecked_trader_heads_for_the_nearest_yard_unless_its_trip_has_one()
    {
        var w = Make();
        Xunit.Assert.Equal("B", w.YardDest("A"));
        Xunit.Assert.True(w.HasYard("B"));
        Xunit.Assert.False(w.HasYard("C"));
        var rng = new System.Random(2);
        for (var i = 0; i < 30; i++)
        {
            var a = w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "x" + i, Name = "X" + i, Kind = "trader", Role = "supply", System = "A", StationId = 1, State = "dock", Until = 100 + i, Since = 0, Hull = 0.1, Speed = 40 });
            w.Step(100 + i, rng);
            Xunit.Assert.Equal("B", a.Dest);   // C has no yard: detour to B (and to B anyway if B was the plan)
            if (a.Extra.TryGetValue("detour", out var after)) Xunit.Assert.Equal("C", after);
            w.Agents.Remove(a.Id);
        }
    }
}

public class ExtraKeyTests
{
    [Fact]
    public void Trip_notes_survive_save_and_load_and_never_clash_with_saved_fields()
    {
        var w = new LivingSystems.Core.Sim.World();
        var a = w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "t1", Name = "Penny", Kind = "trader", System = "A", State = "fight", After = "out", Resume = 42, Since = 7 });
        foreach (var kv in new[] { "detour=C", "stores=Beer:3", "want=Coffee:5:400", "cash=9000", "via=B" }) { var p = kv.Split('='); a.Extra[p[0]] = p[1]; }
        var back = LivingSystems.Core.Sim.World.Parse(w.Serialize(), out var skipped).Get("t1");
        Xunit.Assert.Equal(0, skipped);
        Xunit.Assert.Equal("out", back.After);
        Xunit.Assert.Equal(42, back.Resume);
        foreach (var kv in a.Extra) Xunit.Assert.Equal(kv.Value, back.Extra[kv.Key]);
    }
}

public class BountyTests
{
    static LivingSystems.Core.Sim.World Make(out LivingSystems.Core.Sim.SimAgent trader, out LivingSystems.Core.Sim.SimAgent pirate)
    {
        var w = new LivingSystems.Core.Sim.World();
        pirate = w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "A", State = "lurk", Until = 1e9, Strength = 1 });
        trader = w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "t1", Name = "Penny", Kind = "trader", System = "A", State = "dock", Until = 1e9 });
        trader.Extra["hitby"] = "p1:1000:Rust Jaw";
        return w;
    }

    [Fact]
    public void Offered_only_by_a_ship_that_was_hit_recently_by_a_pirate_still_alive()
    {
        var w = Make(out var t, out var p);
        var o = w.BountyOffer(t, "pid:1", 2000);
        Xunit.Assert.NotNull(o);
        Xunit.Assert.Equal("p1", o.Pirate);
        Xunit.Assert.InRange(o.Reward, 7000, 11000);
        Xunit.Assert.Null(w.BountyOffer(t, "pid:1", 1000 + w.Config.BountyOfferSeconds + 1));   // too long ago
        p.State = "dead";
        Xunit.Assert.Null(w.BountyOffer(t, "pid:1", 2000));                                    // already dead
        p.State = "lurk"; p.Name = "Gutter Saint";
        Xunit.Assert.Null(w.BountyOffer(t, "pid:1", 2000));                                    // id reused by someone else
    }

    [Fact]
    public void Dies_anyhow_then_paid_once_by_any_trader_and_it_survives_a_reload()
    {
        var w = Make(out var t, out var p); var rng = new System.Random(1);
        var b = w.Accept(w.BountyOffer(t, "pid:1", 2000));
        Xunit.Assert.Null(w.BountyOffer(t, "pid:1", 2000));                                    // one contract per pirate
        w = LivingSystems.Core.Sim.World.Parse(w.Serialize(), out _);                          // restart
        Xunit.Assert.Equal("open", w.Bounties[b.Id].State);
        w.Get("p1").State = "dead"; w.Get("p1").Until = 1e15;                                  // killed off-screen, by anyone
        w.Recycle(s => 1000);
        Xunit.Assert.NotNull(w.Get("p1"));                                                     // held until settled
        w.Step(3000, rng);
        Xunit.Assert.Equal("done", w.Bounties[b.Id].State);
        w = LivingSystems.Core.Sim.World.Parse(w.Serialize(), out _);
        var paid = w.Collect(new[] { "pid:1" }, 3100);
        Xunit.Assert.Single(paid);
        Xunit.Assert.Empty(w.Collect(new[] { "pid:1" }, 3200));                                // only once
        Xunit.Assert.Empty(w.Collect(new[] { "pid:2" }, 3200));
    }

    [Fact]
    public void A_contract_runs_out_and_a_vanished_target_lapses()
    {
        var w = Make(out var t, out var p); var rng = new System.Random(1);
        var b = w.Accept(w.BountyOffer(t, "pid:1", 2000));
        w.Step(2000 + w.Config.BountySeconds + 10, rng);
        Xunit.Assert.Equal("expired", w.Bounties[b.Id].State);
        var w2 = Make(out var t2, out var p2);
        var b2 = w2.Accept(w2.BountyOffer(t2, "pid:1", 2000));
        w2.Agents.Remove("p1");
        w2.Step(2100, rng);
        Xunit.Assert.Equal("lapsed", w2.Bounties[b2.Id].State);
    }
}

public class SharedBountyTests
{
    [Fact]
    public void Two_hunters_share_one_pot()
    {
        var w = new LivingSystems.Core.Sim.World(); var rng = new System.Random(1);
        w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "A", State = "lurk", Until = 1e9, Strength = 1 });
        var t1 = w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "t1", Name = "Penny", Kind = "trader", System = "A", State = "dock", Until = 1e9 });
        var t2 = w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "t2", Name = "Mule", Kind = "trader", System = "B", State = "dock", Until = 1e9 });
        t1.Extra["hitby"] = "p1:1000:Rust Jaw"; t2.Extra["hitby"] = "p1:1500:Rust Jaw";
        var a = w.Accept(w.BountyOffer(t1, "pid:1", 2000));
        var o2 = w.BountyOffer(t2, "pid:2", 2100);
        Xunit.Assert.Equal(2, o2.Hunters);
        Xunit.Assert.Equal(a.Reward, o2.Reward);   // the same pot, not a second one
        Xunit.Assert.Equal(a.Until, o2.Until);
        w.Accept(o2);
        Xunit.Assert.Contains("split 2 ways", w.Bounties[a.Id].Note);   // the first hunter hears about it
        Xunit.Assert.Equal("", w.Bounties[o2.Id].Note);
        w = LivingSystems.Core.Sim.World.Parse(w.Serialize(), out _);
        Xunit.Assert.Contains("split 2 ways", w.Bounties[a.Id].Note);   // still owed after a restart
        w.Get("p1").State = "dead"; w.Get("p1").Until = 1e15;
        w.Step(3000, rng);
        var p1 = w.Collect(new[] { "pid:1" }, 3100); var p2 = w.Collect(new[] { "pid:2" }, 3100);
        Xunit.Assert.Equal(a.Reward / 2.0, p1[0].Payout, 0);
        Xunit.Assert.Equal(p1[0].Payout, p2[0].Payout);
        Xunit.Assert.Equal(2, p1[0].Hunters);
    }
}

public class ReviewFixTests
{
    [Fact]
    public void Ids_never_come_back_and_survive_a_save()
    {
        var w = new LivingSystems.Core.Sim.World();
        w.Config.Systems = new System.Collections.Generic.List<string> { "A" };
        w.SetStations("A", new System.Collections.Generic.List<LivingSystems.Core.Sim.SimStation> { new LivingSystems.Core.Sim.SimStation { Id = 1, Name = "Lambda" } });
        var first = w.SeedAt("A", 2, new System.Random(1), 0);
        var gone = first[1].Id;
        w.Agents.Remove(gone);
        w = LivingSystems.Core.Sim.World.Parse(w.Serialize(), out _);
        var next = w.SeedAt("A", 1, new System.Random(2), 0);
        Xunit.Assert.NotEqual(gone, next[0].Id);
    }

    [Fact]
    public void A_replacement_ship_starts_with_a_clean_slate_and_the_dock_stay_has_a_start()
    {
        var w = new LivingSystems.Core.Sim.World();
        w.Config.Systems = new System.Collections.Generic.List<string> { "A" };
        w.SetStations("A", new System.Collections.Generic.List<LivingSystems.Core.Sim.SimStation> { new LivingSystems.Core.Sim.SimStation { Id = 1, Name = "Lambda" } });
        w.Focus = new System.Collections.Generic.List<string> { "A" };
        var a = w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "t1", Name = "Old", Kind = "trader", Role = "supply", System = "A", State = "dead", Until = 100 });
        a.Extra["memory.escort.pid:1"] = "abandoned"; a.Extra["hitby"] = "p1:50:X";
        w.Step(100, new System.Random(1));
        Xunit.Assert.Equal("dock", a.State);
        Xunit.Assert.Empty(a.Extra.Keys.Where(k => k.StartsWith("memory.") || k == "hitby"));
        Xunit.Assert.True(w.Track(a, out var tr));
        Xunit.Assert.True(tr.T1 > tr.T0);   // repairs on screen need to know how far through the stay it is
    }
}

public class QuietHubTests
{
    [Fact]
    public void A_hub_far_from_every_player_stays_home_and_wakes_when_someone_comes_near()
    {
        var w = new LivingSystems.Core.Sim.World(); var rng = new System.Random(3);
        w.Config.Systems = new System.Collections.Generic.List<string> { "A" };
        w.SetStations("A", new System.Collections.Generic.List<LivingSystems.Core.Sim.SimStation> { new LivingSystems.Core.Sim.SimStation { Id = 1, Name = "Lambda" } });
        w.Hubs["h1"] = new LivingSystems.Core.Sim.SimHub { Id = "h1", Name = "Knife Point", System = "H", Size = 4, Manned = true };
        var crew = new System.Collections.Generic.List<LivingSystems.Core.Sim.SimAgent>();
        for (var i = 0; i < 4; i++) crew.Add(w.Add(new LivingSystems.Core.Sim.SimAgent { Id = "p" + i, Name = "P" + i, Kind = "pirate", System = "H", State = "home", Home = "h1", Until = 100 + i, Strength = 1 }));
        w.Focus = new System.Collections.Generic.List<string> { "A" };   // players far from the hub
        for (var t = 100.0; t < 48 * 3600; t += 60) w.Step(t, rng);
        Xunit.Assert.All(crew, c => Xunit.Assert.Equal("home", c.State));
        w.Focus = new System.Collections.Generic.List<string> { "A", "H" };   // someone came near
        for (var t = 48 * 3600.0; t < 96 * 3600; t += 60) w.Step(t, rng);
        Xunit.Assert.Contains(crew, c => c.State != "home" || c.System != "H");
    }
}
