using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Sim;
using Xunit;

/// <summary>One inventory for traders, tugs, pirates and wrecks: nothing lost or doubled when ships die, get robbed or salvaged.</summary>
public class InventoryTests
{
    static World Make()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A", "B" };
        w.SetStations("A", new List<SimStation> { new SimStation { Id = 1, Name = "Dock", Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        w.Config.MoneyFlow = false; w.Config.PatrolChance = 0; w.Config.FleeChance = 0; w.Config.EscapeChance = 0; w.Config.SmartFlee = false;
        w.Config.RoundHit = 50;   // the victim dies in round one
        w.Config.WreckChance = 1; w.Config.LootMin = 0.5; w.Config.LootMax = 0.5;
        w.Config.ReplaceSeconds = 1e9;
        return w;
    }

    static SimAgent Tug(World w, string id, string state)
    {
        var a = w.Add(new SimAgent { Id = id, Name = "Tug " + id, System = "A", State = state, Until = 1e6, Strength = 0, Role = "salvage", StationId = 1, Dest = "B" });
        a.Extra["tug"] = "A"; a.Extra["tughome"] = "A";
        return a;
    }

    static SimAgent Pirate(World w) =>
        w.Add(new SimAgent { Id = "p1", Name = "Rust Jaw", Kind = "pirate", System = "A", State = "lurk", Until = 1e6, Strength = 3, SpotStation = 1, SpotOther = "B", SpotFrac = 0.3 });

    static void Fight(World w, SimAgent victim, SimAgent pirate)
    {
        var rng = new Random(1);
        w.StartFight(victim, pirate, 10, rng);
        for (var t = 20.0; victim.Alive && t < 2000; t += 30) w.Step(t, rng);
        Assert.False(victim.Alive);
    }

    static string Lines(IDictionary<string, int> d) => World.FormatHold(d);

    [Fact]
    public void A_dead_tug_leaves_every_line_in_its_wreck_and_the_pirates_get_a_share_of_each()
    {
        var w = Make();
        var tug = Tug(w, "t1", "out");
        tug.Cargo = "SteelPlate"; tug.CargoAmt = 200;
        tug.Extra[World.HoldKey] = "GoldIngot:100,Token#5#0:4";
        tug.Extra["salvx"] = "5000";
        Assert.Equal("GoldIngot:100,SteelPlate:200,Token#5#0:4", Lines(w.InventoryOf(tug)));
        var p = Pirate(w);
        Fight(w, tug, p);
        var wr = Assert.Single(w.Wrecks.Values);
        Assert.Equal("GoldIngot:50,SteelPlate:100,Token#5#0:2", Lines(World.HoldOf(wr)));   // half of each line
        Assert.Equal("GoldIngot:50,SteelPlate:100,Token#5#0:2", Lines(w.InventoryOf(p)));   // the pirate's half
        Assert.Equal("SteelPlate", wr.Cargo); Assert.Equal(100, wr.Amt);                     // the old one-line view: biggest line
        Assert.Empty(w.InventoryOf(tug));
        Assert.False(tug.Extra.ContainsKey("salvx"));
        Assert.Empty(w.Stats.Keys.Where(k => k.StartsWith("eco|mint|", StringComparison.Ordinal)));   // moving goods mints nothing
    }

    [Fact]
    public void A_tug_salvaging_that_wreck_gets_the_lines_back()
    {
        var w = Make();
        var dead = Tug(w, "t1", "out");
        dead.Cargo = "SteelPlate"; dead.CargoAmt = 200; dead.Extra[World.HoldKey] = "GoldIngot:100,Token#5#0:4";
        Fight(w, dead, Pirate(w));
        var wr = w.Wrecks.Values.Single();
        var tug = Tug(w, "t2", "salvage");
        wr.Tug = tug.Id;
        w.TugCutLive(tug.Id, wr.Id, 1, 3000);
        Assert.False(w.Wrecks.ContainsKey(wr.Id));
        Assert.Equal("SteelPlate", tug.Cargo);
        Assert.Equal(100 + w.Config.ScrapPerWreck, tug.CargoAmt);                            // its plates + the hull's scrap
        Assert.Equal("GoldIngot:50,Token#5#0:2", tug.Extra[World.HoldKey]);                  // the rest in the real hold
        Assert.Equal(World.Value("GoldIngot", 50) + World.Value("Token", 2), double.Parse(tug.Extra["salvx"], System.Globalization.CultureInfo.InvariantCulture), 3);
        Assert.Empty(w.Stats.Keys.Where(k => k.StartsWith("eco|mint|", StringComparison.Ordinal)));   // only the sale at home mints
    }

    [Fact]
    public void A_dead_trader_shares_its_cargo_and_its_stores()
    {
        var w = Make();
        var t = w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", State = "out", Dest = "B", Until = 1e6, Strength = 0, Role = "bulk", Cargo = "IronOre", CargoAmt = 400, StationId = 1 });
        t.Extra["stores"] = "Medikit01:10,FusionCell:6";
        var p = Pirate(w);
        Fight(w, t, p);
        var wr = w.Wrecks.Values.Single();
        Assert.Equal("FusionCell:3,IronOre:200,Medikit01:5", Lines(World.HoldOf(wr)));
        Assert.Equal("FusionCell:3,IronOre:200,Medikit01:5", Lines(w.InventoryOf(p)));
        Assert.Empty(w.InventoryOf(t));
    }

    [Fact]
    public void A_dead_pirate_leaves_all_its_loot_not_just_the_biggest()
    {
        var w = Make();
        var t = w.Add(new SimAgent { Id = "t1", Name = "Brick", System = "A", State = "out", Dest = "B", Until = 1e6, Strength = 5, Role = "bulk", StationId = 1 });
        var p = Pirate(w); p.Strength = 0.1;
        p.AddLoot("IronOre", 300); p.AddLoot("GoldIngot", 40); p.LootUntil = 1e9;   // not fenced meanwhile
        var rng = new Random(1);
        w.StartFight(t, p, 10, rng);
        for (var s = 20.0; p.Alive && s < 4000; s += 30) w.Step(s, rng);
        Assert.False(p.Alive);
        var wr = w.Wrecks.Values.Single();
        Assert.Equal("GoldIngot:40,IronOre:300", Lines(World.HoldOf(wr)));
        Assert.Equal(0, p.LootAmt);
    }

    [Fact]
    public void Shares_and_gives_move_goods_without_losing_or_doubling_any()
    {
        var w = Make();
        var tug = Tug(w, "t1", "dock");
        tug.Cargo = "SteelPlate"; tug.CargoAmt = 7; tug.Extra[World.HoldKey] = "A#1:3,A#2:5";
        var before = w.InventoryOf(tug);
        var taken = w.TakeShare(tug, 0.4);
        var after = w.InventoryOf(tug);
        foreach (var k in before.Keys) Assert.Equal(before[k], (taken.TryGetValue(k, out var x) ? x : 0) + (after.TryGetValue(k, out var y) ? y : 0));
        Assert.Contains("A#1", taken.Keys); Assert.Contains("A#2", taken.Keys);   // keys stay apart
        w.Give(tug, taken);
        Assert.Equal(Lines(before), Lines(w.InventoryOf(tug)));
        var trader = w.Add(new SimAgent { Id = "t2", System = "A", Cargo = "IronOre", CargoAmt = 5 });
        w.Give(trader, new Dictionary<string, int> { ["IronOre"] = 5, ["GoldIngot"] = 2 });
        Assert.Equal(10, trader.CargoAmt); Assert.Equal("GoldIngot:2", trader.Extra["stores"]);
    }

    [Fact]
    public void Wreck_holds_survive_a_save_and_old_wreck_lines_still_load()
    {
        var w = Make();
        var wr = new SimWreck { Id = "w1", System = "A", Name = "X", Time = 1, Until = 100 };
        World.SetWreckHold(wr, new Dictionary<string, int> { ["IronOre"] = 30, ["Token#5#0"] = 2, ["GoldIngot"] = 9 });
        w.Wrecks["w1"] = wr;
        var back = World.Parse(w.Serialize(), out _).Wrecks["w1"];
        Assert.Equal("GoldIngot:9,IronOre:30,Token#5#0:2", Lines(World.HoldOf(back)));
        Assert.Equal("IronOre", back.Cargo); Assert.Equal(30, back.Amt);
        var old = World.Parse(World.Header + "\nwreck id=w2 sys=A name=Y kind=trader role=bulk cargo=CobaltOre amt=120 x=0 y=0 z=0 t=1 until=50 entity=0", out _).Wrecks["w2"];
        Assert.Empty(old.Hold);
        Assert.Equal("CobaltOre:120", Lines(World.HoldOf(old)));
        Assert.Equal("CobaltOre:120", Lines(World.TakeAll(old)));
        Assert.Equal(0, old.Amt); Assert.Equal("", old.Cargo);
    }

    [Fact]
    public void A_spill_is_a_sack_with_its_lot_only_saved_and_off_the_tug_board()
    {
        var w = Make();
        var s = w.AddSpill("A", new LivingSystems.Core.Flight.Vec3(10, 20, 30), "Smelly Wren — spilled cargo", new Dictionary<string, int> { ["SteelPlate"] = 9000, ["Drill#250#0"] = 1 }, 100);
        Assert.True(s.Spill); Assert.True(s.CashAboard); Assert.Equal(0, s.Cash); Assert.Equal(World.Never, s.Until);
        Assert.Equal(World.ChestPrefab, World.WreckPrefab(s));
        Assert.Null(w.AddSpill("A", default, "x", new Dictionary<string, int>(), 100));
        var back = World.Parse(w.Serialize(), out _).Wrecks[s.Id];
        Assert.True(back.Spill);
        Assert.Equal("Drill#250#0:1,SteelPlate:9000", Lines(World.HoldOf(back)));
        Assert.Null(w.TugHere("A", 200));   // nothing a tug may take: the sack is the players'
    }

    [Fact]
    public void A_live_pirate_strips_its_share_of_every_line_off_the_wreck_and_nothing_doubles()
    {
        var w = Make(); var p = Pirate(w);
        var wr = w.Wrecks["w1"] = new SimWreck { Id = "w1", System = "A", Name = "Mule", Kind = "trader", Entity = 900, Until = World.Never };
        World.SetWreckHold(wr, new Dictionary<string, int> { ["IronOre"] = 400, ["Token#7#0"] = 2, ["Motor"] = 3 });
        var got = w.RobWreck("p1", "w1", 100, new Random(1));
        Assert.Equal(200, got["IronOre"]); Assert.Equal(1, got["Token#7#0"]);   // stack data kept on the key
        var left = World.HoldOf(wr);
        Assert.Equal(200, left["IronOre"]); Assert.Equal(1, left["Token#7#0"]);
        var inv = w.InventoryOf(p);
        Assert.Equal(200, inv["IronOre"]);
        Assert.Equal(400 + 3 + 2, inv.Values.Sum() + left.Values.Sum());          // every item once: in the wreck or the pirate
        Assert.Empty(w.RobWreck("nobody", "w1", 100, new Random(1)));
    }
}
