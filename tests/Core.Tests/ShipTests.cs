using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Sim;
using Xunit;

public class ShipTests
{
    [Fact]
    public void Every_agent_keeps_one_hull_and_traders_guns_follow_it()
    {
        var w = new World(); w.Config.Systems = new List<string> { "A" };
        var seen = new HashSet<string>();
        for (var i = 0; i < 300; i++)
        {
            var t = new SimAgent { Id = "t" + i, Name = "T" + i, Role = World.Roles[i % World.Roles.Length].Id };
            var ship = w.ShipOf(t, true);
            Assert.Contains(World.TraderShips[t.Role], s => s.Prefab == ship);
            Assert.Equal(World.ShipInfo(ship).Strength, t.Strength);
            Assert.Equal(ship, w.ShipOf(t, true));   // kept
            seen.Add(ship);
        }
        Assert.True(seen.Count >= 10, $"only {seen.Count} hulls in use");
        // pirates: heavy ones fly the shielded standard raider, the rest the cheap pool; never the frigate / Skaar
        var heavy = new SimAgent { Id = "p1", Kind = "pirate", Strength = 2.5 };
        Assert.Equal("EPH_PirateOPVT1", w.ShipOf(heavy));
        var cheap = new SimAgent { Id = "p2", Kind = "pirate", Strength = 1 };
        Assert.Contains(World.CheapPirates, s => s.Prefab == w.ShipOf(cheap));
        // a hub crew flies the gang's EPH, and an old crew on a cheap hull switches over
        var crew = new SimAgent { Id = "p3", Kind = "pirate", Strength = 1, Home = "h1", Ship = "OPV_PirateCorvetteT1A" };
        Assert.Equal("EPH_PirateOPVT1", w.ShipOf(crew));
        // survives a save
        var a = w.Add(new SimAgent { Id = "x", Kind = "pirate", Strength = 1 }); w.ShipOf(a);
        Assert.Equal(a.Ship, World.Parse(w.Serialize(), out _).Get("x").Ship);
    }

    [Fact]
    public void The_wreck_tells_what_happened()
    {
        var big = new SimWreck { Id = "w1", Ship = "CV_Freighter_HarkonA", Time = 5 };
        Assert.Contains(World.WreckPrefab(big), World.WrecksBig);
        var small = new SimWreck { Id = "w2", Ship = "CV_Freighter_SparrowA", Time = 5 };
        Assert.Contains(World.WreckPrefab(small), World.WrecksSmall);
        var picked = new SimWreck { Id = "w3", Ship = "CV_Freighter_HarkonA", Time = 5, Looted = true };
        Assert.Contains(World.WreckPrefab(picked), World.Wreckage);                       // pirates got the goods
        Assert.Equal("Eden_Wrecked Horizon", World.WreckPrefab(new SimWreck { Id = "w4", Ship = "CV_Freighter_HorizonB", Looted = true }));
        Assert.True(World.IsPrewrecked(World.WreckPrefab(big)));
        Assert.False(World.IsPrewrecked("CV_Freighter_SparrowA"));
        // a kill records the hull and whether it was looted, and it survives a save
        var w = new World(); w.Config.Systems = new List<string> { "A" };
        var t = w.Add(new SimAgent { Id = "t1", Name = "Mule", System = "A", Role = "bulk" });
        var wr = w.Wreck(t, default(LivingSystems.Core.Flight.Vec3), "IronOre", 100, 10, new Random(1), looted: true);
        Assert.Equal(w.ShipOf(t), wr.Ship);
        Assert.True(wr.Looted);
        var back = World.Parse(w.Serialize(), out _).Wrecks[wr.Id];
        Assert.Equal(wr.Ship, back.Ship); Assert.True(back.Looted);
    }

    [Fact]
    public void LiveDeathHullIsOwedItsCashOnce()
    {
        var hull = new SimWreck { Id = "w1", Entity = 42, Cash = 35000 };
        Assert.True(World.CashOwed(hull));                                                          // a live death: never spawned by us
        Assert.False(World.CashOwed(new SimWreck { Id = "w2", Entity = 42, Cash = 35000, Shown = "wreck" }));   // spawned: wreckfill had it
        Assert.False(World.CashOwed(new SimWreck { Id = "w3", Entity = 42, Cash = 35000, Hub = "h1" }));        // hauled home
        Assert.False(World.CashOwed(new SimWreck { Id = "w4", Entity = 0, Cash = 35000 }));                     // no hull yet
        var w = new World(); w.Wrecks[hull.Id] = hull;
        hull.CashAboard = true;
        var back = World.Parse(w.Serialize(), out _).Wrecks["w1"];
        Assert.True(back.CashAboard); Assert.False(World.CashOwed(back));                           // delivered: never twice
    }

    [Fact]
    public void A_stack_with_its_own_data_keeps_it_through_the_hold()
    {
        Assert.Equal("SteelPlate", World.StackKey("SteelPlate", 0, 0));
        var k = World.StackKey("Token", 1011, 0);
        Assert.Equal("Token", World.KeyBase(k, out var ammo, out var decay));
        Assert.Equal(1011, ammo); Assert.Equal(0, decay);
        var hold = World.ParseHold(World.FormatHold(new Dictionary<string, int> { [k] = 1, [World.StackKey("Token", 1234, 0)] = 1, ["SteelPlate"] = 5 }));
        Assert.Equal(3, hold.Count);                                    // two tokens stay two different things
        Assert.Equal("Token", World.Humanize(k).Length > 0 ? World.KeyBase(k, out _, out _) : "");
        Assert.Equal(World.Value("SteelPlate", 1), World.Value(World.StackKey("SteelPlate", 0, 7), 1));   // priced by the item
    }
}
