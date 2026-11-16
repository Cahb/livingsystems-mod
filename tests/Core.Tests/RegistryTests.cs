using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using LivingSystems.Core.Registry;
using Xunit;

public class RegistryTests
{
    const string Me = "76561190000000001";

    static ShipRecord Ship(int id, string pf = "West Rim", ShipOrder order = null) =>
        new ShipRecord { EntityId = id, Playfield = pf, Name = "«LS» Spike " + id, Since = "2026-11-13T12:00:00Z", Order = order };

    static ShipOrder Follow(float dist = 500) => new ShipOrder { Kind = "follow", LeaderSteamId = Me, LeaderName = "Captain", Distance = dist, Cruise = 40 };
    static ShipOrder Hold() => new ShipOrder { Kind = "hold", HoldX = -6057.69f, HoldY = 145.95f, HoldZ = -50.38f, Cruise = 40 };

    [Fact]
    public void Round_trips_ships_with_and_without_orders()
    {
        var r = new ShipRegistry();
        r.Set(Ship(1, order: Follow()));
        r.Set(Ship(2, order: Hold()));
        r.Set(Ship(3));
        var back = ShipRegistry.Parse(r.Serialize(), out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(Ship(1, order: Follow()), back.Get(1));
        Assert.Equal(Ship(2, order: Hold()), back.Get(2));
        Assert.Equal(Ship(3), back.Get(3));
        Assert.Null(back.Get(3).Order);
    }

    [Fact]
    public void Awkward_text_survives_escaping()
    {
        var s = Ship(1, pf: "Pipe|Field 100% = \n new", order: Follow());
        s.Order.LeaderName = "a b=c|d%";
        var r = new ShipRegistry(); r.Set(s);
        var line = ShipRecord.ToLine(s);
        Assert.DoesNotContain("|", line);
        Assert.DoesNotContain("\n", line);
        Assert.Equal(s, ShipRegistry.Parse(r.Serialize(), out _).Get(1));
    }

    [Fact]
    public void Unknown_keys_from_newer_versions_survive_a_round_trip()
    {
        var text = ShipRegistry.Header + "\nship id=7 pf=A pilot.skill=0.8 caps.fwd=28 order.kind=follow order.leader=" + Me + " order.dist=250\n";
        var back = ShipRegistry.Parse(text, out _);
        var rec = back.Get(7);
        Assert.Equal("0.8", rec.Extra["pilot.skill"]);
        Assert.Equal(250, rec.Order.Distance);
        var again = ShipRegistry.Parse(back.Serialize(), out _).Get(7);
        Assert.Equal("0.8", again.Extra["pilot.skill"]);
        Assert.Equal("28", again.Extra["caps.fwd"]);
    }

    [Fact]
    public void Missing_keys_mean_defaults()
    {
        var rec = ShipRegistry.Parse(ShipRegistry.Header + "\nship id=7 order.kind=follow\n", out _).Get(7);
        Assert.Equal("", rec.Playfield);
        Assert.Equal(150, rec.Order.Distance);
        Assert.Equal(40, rec.Order.Cruise);
    }

    [Fact]
    public void An_order_kind_we_dont_know_is_kept_but_not_run()
    {
        var text = ShipRegistry.Header + "\nship id=7 pf=A order.kind=mine order.target=123\n";
        var rec = ShipRegistry.Parse(text, out _).Get(7);
        Assert.Null(rec.Order);
        Assert.Equal("mine", rec.Extra["order.kind"]);
        var again = ShipRegistry.Parse(new Func<string>(() => { var r = new ShipRegistry(); r.Set(rec); return r.Serialize(); })(), out _).Get(7);
        Assert.Equal("mine", again.Extra["order.kind"]);
        Assert.Equal("123", again.Extra["order.target"]);
    }

    [Fact]
    public void Merge_keeps_fields_the_sender_did_not_know()
    {
        var r = new ShipRegistry();
        var newer = Ship(7, order: Follow()); newer.Extra["pilot.skill"] = "0.8";
        r.Set(newer);
        r.Set(Ship(7, order: Hold()));   // an older PfServer: knows orders, not pilots
        var rec = r.Get(7);
        Assert.Equal("hold", rec.Order.Kind);
        Assert.Equal("0.8", rec.Extra["pilot.skill"]);
    }

    [Fact]
    public void Merge_keeps_the_first_registration_time()
    {
        var r = new ShipRegistry();
        r.Set(Ship(7));
        var update = Ship(7, order: Follow()); update.Since = "";
        r.Set(update);
        Assert.Equal("2026-11-13T12:00:00Z", r.Get(7).Since);
    }

    [Fact]
    public void Patch_sets_only_the_given_keys_and_empty_deletes()
    {
        var r = new ShipRegistry();
        r.Set(Ship(7, order: Follow()));
        Assert.True(r.Patch("ship id=7 faction.orig=7:0 faction.temp=0:1"));
        var rec = r.Get(7);
        Assert.Equal("7:0", rec.Field("faction.orig"));
        Assert.Equal("follow", rec.Order.Kind);   // the order is untouched
        Assert.True(r.Patch("ship id=7 faction.orig= faction.temp="));
        rec = r.Get(7);
        Assert.Equal("", rec.Field("faction.orig"));
        Assert.False(rec.Extra.ContainsKey("faction.temp"));
        Assert.Equal(500, rec.Order.Distance);
    }

    [Fact]
    public void Patch_can_clear_an_order_and_create_a_record()
    {
        var r = new ShipRegistry();
        Assert.True(r.Patch("ship id=9 pf=A order.kind=hold order.x=1"));
        Assert.Equal("hold", r.Get(9).Order.Kind);
        Assert.True(r.Patch("ship id=9 order.kind= order.x="));
        Assert.Null(r.Get(9).Order);
        Assert.False(r.Patch("ship id=9 order.kind="));   // nothing changed
        Assert.False(r.Patch("ship pf=nobody"));           // no id
    }

    [Fact]
    public void Patched_sections_survive_a_save_round_trip()
    {
        var r = new ShipRegistry(); r.Set(Ship(7));
        r.Patch("ship id=7 faction.orig=7:0");
        Assert.Equal("7:0", ShipRegistry.Parse(r.Serialize(), out _).Get(7).Field("faction.orig"));
    }

    [Fact]
    public void Reads_the_v1_format()
    {
        var v1 = ShipRegistry.HeaderV1 + "\nship|62016|West Rim|follow|" + Me + "|Captain|500|40|0|0|0\nship|62017|West Rim|hold|||150|40|1|2|3\n";
        var back = ShipRegistry.Parse(v1, out var skipped);
        Assert.Equal(0, skipped);
        Assert.Equal(500, back.Get(62016).Order.Distance);
        Assert.Equal(3, back.Get(62017).Order.HoldZ);
        Assert.StartsWith(ShipRegistry.Header, back.Serialize());   // upgraded on the next save
    }

    [Fact]
    public void Damaged_lines_are_skipped_not_fatal()
    {
        var r = new ShipRegistry(); r.Set(Ship(5)); r.Set(Ship(6, order: Hold()));
        var text = r.Serialize() + "ship garbage\nship id=-3 pf=x\nnot a ship line\n";
        var back = ShipRegistry.Parse(text, out var skipped);
        Assert.Equal(2, back.Count);
        Assert.Equal(3, skipped);
    }

    [Fact]
    public void Unknown_header_gives_an_empty_registry()
    {
        var back = ShipRegistry.Parse("something else v9\nship id=5 pf=x\n", out var skipped);
        Assert.Equal(0, back.Count);
        Assert.True(skipped > 0);
    }

    [Fact]
    public void Set_reports_whether_anything_changed()
    {
        var r = new ShipRegistry();
        Assert.True(r.Set(Ship(5, order: Follow())));
        Assert.False(r.Set(Ship(5, order: Follow())));
        Assert.True(r.Set(Ship(5, order: Follow(250))));
        Assert.True(r.Set(Ship(5)));   // order released
        Assert.Null(r.Get(5).Order);
    }

    [Fact]
    public void Returned_records_are_copies()
    {
        var r = new ShipRegistry(); r.Set(Ship(5, order: Follow()));
        r.Get(5).Order.Distance = 1;
        r.Get(5).Extra["x"] = "y";
        Assert.Equal(500, r.Get(5).Order.Distance);
        Assert.False(r.Get(5).Extra.ContainsKey("x"));
    }

    [Fact]
    public void Save_and_load_atomically_with_backup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lsreg-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "Mods", "LivingSystems", "registry.txt");
        try
        {
            var r = new ShipRegistry(); r.Set(Ship(5, order: Follow()));
            r.Save(path);
            r.Set(Ship(6, order: Hold()));
            r.Save(path);   // second save goes through Replace and leaves a .bak
            Assert.True(File.Exists(path + ".bak"));
            Assert.False(File.Exists(path + ".tmp"));
            var back = ShipRegistry.Load(path, out var skipped);
            Assert.Equal(2, back.Count);
            Assert.Equal(0, skipped);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Corrupt_main_file_falls_back_to_backup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "lsreg-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(dir, "registry.txt");
        try
        {
            var r = new ShipRegistry(); r.Set(Ship(5));
            r.Save(path); r.Save(path);   // .bak now holds a good copy
            File.WriteAllText(path, ShipRegistry.Header + "\nship broken\n");
            Assert.Equal(1, ShipRegistry.Load(path, out _).Count);
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }

    [Fact]
    public void Missing_file_is_an_empty_registry()
    {
        var back = ShipRegistry.Load(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N"), "r.txt"), out var skipped);
        Assert.Equal(0, back.Count);
        Assert.Equal(0, skipped);
    }

    [Fact]
    public void Filters_by_playfield()
    {
        var r = new ShipRegistry(); r.Set(Ship(5, pf: "A")); r.Set(Ship(6, pf: "B"));
        Assert.Equal(new[] { 5 }, r.InPlayfield("A").Select(o => o.EntityId));
    }
}

public class ReconcileTests
{
    const string Me = "76561190000000001";
    static ShipRecord Rec(int id, ShipOrder o = null) => new ShipRecord { EntityId = id, Playfield = "P", Order = o };
    static ShipOrder Follow() => new ShipOrder { Kind = "follow", LeaderSteamId = Me, Distance = 500 };
    static ShipOrder Hold() => new ShipOrder { Kind = "hold" };

    static ReconcileDecision One(ShipRecord r, int[] ships, Dictionary<string, int> players, double since) =>
        Reconciler.Decide(new[] { r }, new HashSet<int>(ships), players, since).Single();

    static Dictionary<string, int> NoOne => new Dictionary<string, int>();

    [Fact]
    public void Ship_and_leader_here_resumes_with_the_leaders_current_id()
    {
        var d = One(Rec(5, Follow()), new[] { 5 }, new Dictionary<string, int> { [Me] = 1188 }, 3);
        Assert.Equal(ReconcileAction.Resume, d.Action);
        Assert.Equal(1188, d.LeaderEntityId);
    }

    [Fact]
    public void Leader_away_means_wait_not_drop() =>
        Assert.Equal(ReconcileAction.WaitForLeader, One(Rec(5, Follow()), new[] { 5 }, NoOne, 500).Action);

    [Fact]
    public void Hold_resumes_without_any_player() =>
        Assert.Equal(ReconcileAction.Resume, One(Rec(5, Hold()), new[] { 5 }, NoOne, 3).Action);

    [Fact]
    public void Ship_without_an_order_is_idle() =>
        Assert.Equal(ReconcileAction.Idle, One(Rec(5), new[] { 5 }, NoOne, 3).Action);

    [Fact]
    public void Unknown_order_kind_is_idle()
    {
        var r = Rec(5); r.Extra["order.kind"] = "mine";
        Assert.Equal(ReconcileAction.Idle, One(r, new[] { 5 }, NoOne, 3).Action);
    }

    [Fact]
    public void Missing_ship_is_pending_during_grace_then_dropped()
    {
        Assert.Equal(ReconcileAction.Pending, One(Rec(5), new int[0], NoOne, 10).Action);
        Assert.Equal(ReconcileAction.Drop, One(Rec(5), new int[0], NoOne, Reconciler.GraceSeconds).Action);
    }

    [Fact]
    public void A_departed_agent_is_kept_without_a_ship()
    {
        var r = Rec(5); r.Extra["state"] = "departed";
        Assert.Equal(ReconcileAction.Departed, One(r, new int[0], NoOne, Reconciler.GraceSeconds * 10).Action);
    }

    [Fact]
    public void Follow_without_a_stored_leader_waits()
    {
        var o = Follow(); o.LeaderSteamId = "";
        Assert.Equal(ReconcileAction.WaitForLeader, One(Rec(5, o), new[] { 5 }, new Dictionary<string, int> { [Me] = 1 }, 3).Action);
    }
}
