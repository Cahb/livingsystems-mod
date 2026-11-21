using System.Collections.Generic;
using System.IO;
using LivingSystems.Core.Sim;
using Xunit;
using Xunit.Abstractions;

public class RecipeTests
{
    readonly ITestOutputHelper output;
    public RecipeTests(ITestOutputHelper output) { this.output = output; }

    const string Blocks = @"
# header comment
{ Block Id: 403, Name: HullFullLarge
  Material: SteelLarge
  TemplateRoot: HullLargeBlocks   # trailing comment
  HitPoints: 5000, type: int, display: true
  ChildShapes: ""Cube, CutCornerE, Ramp#A""
  { Child DropOnDestroy
    Item: Nothing
  }
}
{ Block Id: 404, Name: HullThinLarge, Ref: HullFullLarge
  UpgradeTo: HullArmoredThinLarge, display: true
}
{ Block Name: HullExtendedLarge7, Ref: HullThinLarge
}
{ +Block Id: 410, Name: HullFullLargeDestroyed, Ref: HullFullLarge
  TemplateRoot: HullLargeDestroyedBlocks
}
{ Block Id: 500, Name: ThrusterBig
  MarketPrice: 900, display: true
}
{ Block Id: 501, Name: ThrusterBigT2, Ref: ThrusterBig
}
{ Block Id: 502, Name: Orphan
}
{ Block Id: 600, Name: WindowBlocks
  ChildBlocks: ""Window_a, Window_b""
}
{ Block Id: 601, Name: Window_a
}
{ Block Id: 602, Name: Window_c
  ParentBlocks: ""WindowBlocks, OtherGroup""
}
# { Block Name: HullFullLarge
#   TemplateRoot: Bogus
# }
";

    const string Templates = @"
{ +Template Name: HullLargeBlocks
  CraftTime: 1
  { Child Inputs
    SteelPlate: 16
  }
}
{ Template Name: HullLargeDestroyedBlocks
  { Child Inputs
	SteelPlate: 10
  }
}
{ +Template Name: ThrusterBig
  { Child Inputs
    Motor: 8
    Computer: 1
  }
}
{ +Template Name: WindowBlocks
  { Child Inputs
    SteelPlate: 4
  }
}
{ +Template Name: PlateBatch
  OutputCount: 4
  { Child Inputs
    IronIngot: 10
  }
}
";

    const string Items = @"
{ +Item Id: 1, Name: ComponentsTemplate
  MarketPrice: 7, display: true
}
{ +Item Id: 214, Name: Motor, Ref: ComponentsTemplate
  MarketPrice: 111, display: true
}
{ +Item Id: 217, Name: SteelPlate, Ref: ComponentsTemplate
  MarketPrice: 10, display: true
}
{ Item Id: 218, Name: Cheapo, Ref: ComponentsTemplate
}
{ Item Id: 219, Name: Freebie
  MarketPrice: 0
}
";

    static Recipes Small() { return Recipes.Parse(Blocks, Templates, Items); }

    [Fact]
    public void Template_root_gives_half_the_inputs_at_tier_one()
    {
        var y = Small().Yield("HullFullLarge", Recipes.SalvageT1);
        Assert.Single(y);
        Assert.Equal(8, y["SteelPlate"]);
        Assert.Equal(12, Small().Yield("HullFullLarge", Recipes.SalvageT2)["SteelPlate"]);
    }

    [Fact]
    public void Ref_children_inherit_template_root_through_the_chain()
    {
        var r = Small();
        Assert.Equal(8, r.Yield("HullThinLarge", 0.5)["SteelPlate"]);
        Assert.Equal(8, r.Yield("HullExtendedLarge7", 0.5)["SteelPlate"]);   // no Id, two Refs deep
    }

    [Fact]
    public void Destroyed_variant_uses_its_own_template_root()
    {
        Assert.Equal(5, Small().Yield("HullFullLargeDestroyed", 0.5)["SteelPlate"]);
    }

    [Fact]
    public void Block_without_template_root_uses_its_name_then_its_parents()
    {
        var r = Small();
        var y = r.Yield("ThrusterBig", 0.5);
        Assert.Equal(4, y["Motor"]);
        Assert.False(y.ContainsKey("Computer"));   // 0.5 of one: floored away
        Assert.Equal(4, r.Yield("ThrusterBigT2", 0.5)["Motor"]);
        Assert.Empty(r.Yield("Orphan", 0.5));
        Assert.Empty(r.Yield("NoSuchBlock", 0.5));
        Assert.Empty(r.Yield(null, 0.5));
    }

    [Fact]
    public void Block_group_members_use_the_group_template()
    {
        var r = Small();
        Assert.Equal(2, r.Yield("Window_a", 0.5)["SteelPlate"]);   // listed in the group's ChildBlocks
        Assert.Equal(2, r.Yield("Window_c", 0.5)["SteelPlate"]);   // names the group in its ParentBlocks
    }

    [Fact]
    public void Output_count_divides_inputs()
    {
        var r = Small();
        Assert.Equal(2.5, r.PerBlock("PlateBatch")["IronIngot"]);
        Assert.Equal(1, r.Yield("PlateBatch", 0.5)["IronIngot"]);
    }

    [Fact]
    public void Comments_and_display_noise_are_ignored()
    {
        var all = Recipes.ParseEcf(Blocks);
        var hull = all[0];
        Assert.Equal("HullLargeBlocks", hull.Props["TemplateRoot"]);
        Assert.Equal("5000", hull.Props["HitPoints"]);
        Assert.Equal("Cube, CutCornerE, Ramp#A", hull.Props["ChildShapes"]);
        Assert.Equal("DropOnDestroy", hull.Children[0].Name);
        Assert.Equal(10, all.Count);   // the commented-out override is not an entry
    }

    [Fact]
    public void Prices_inherit_through_ref_and_fall_back_to_blocks()
    {
        var r = Small();
        Assert.Equal(111, r.Price("Motor"));
        Assert.Equal(7, r.Price("Cheapo"));
        Assert.True(double.IsNaN(r.Price("Freebie")));
        Assert.True(double.IsNaN(r.Price("Unobtainium")));
        Assert.Equal(900, r.Price("ThrusterBigT2"));
        Assert.Equal(111 * 2 + 10 * 3, r.Value(new Dictionary<string, int> { { "Motor", 2 }, { "SteelPlate", 3 }, { "Unobtainium", 5 } }));
    }

    // the build container doesn't mount the game; LS_ECF_DIR points at a mounted copy when one is available
    static readonly string ConfigDir = System.Environment.GetEnvironmentVariable("LS_ECF_DIR") ?? "/mnt/NVME/steam_apps/steamapps/common/Empyrion - Dedicated Server/Content/Scenarios/Eden2/Content/Configuration";

    static Recipes Real()
    {
        string b = Path.Combine(ConfigDir, "BlocksConfig.ecf"), t = Path.Combine(ConfigDir, "Templates.ecf"), i = Path.Combine(ConfigDir, "ItemsConfig.ecf");
        if (!File.Exists(b) || !File.Exists(t) || !File.Exists(i)) return null;
        return Recipes.Parse(File.ReadAllText(b), File.ReadAllText(t), File.ReadAllText(i));
    }

    [Fact]
    public void Container_blocks_have_their_config_capacity()
    {
        var r = Recipes.Parse(@"
{ Block Id: 1682, Name: ContainerControllerLarge
  Class: ContainerController
  VolumeCapacity: 8000, type: float, display: true, formatter: Liter
}
{ Block Id: 1683, Name: ContainerExtensionLarge
  VolumeCapacity: 8000, type: float, display: true, formatter: Liter
}
{ Block Name: ContainerExtensionLargeB, Ref: ContainerExtensionLarge
}", "", "");
        Assert.Equal(8000, r.BlockCapacity("ContainerControllerLarge"));
        Assert.Equal(8000 * 12, r.BlockCapacity("ContainerControllerLarge") + 11 * r.BlockCapacity("ContainerExtensionLargeB"));   // LS_Tug: the 96,000 L the game shows
        Assert.Equal(0, r.BlockCapacity("HullFullLarge"));
        var real = Real();
        if (real != null) Assert.Equal(8000, real.BlockCapacity("ContainerExtensionLarge"));
    }

    [Fact]
    public void Real_scenario_files_give_vanilla_salvage()
    {
        var r = Real();
        if (r == null) return;   // config not mounted (e.g. CI)
        output.WriteLine("blocks " + r.BlockCount + ", templates " + r.TemplateCount + ", items " + r.ItemCount);
        var bare = new List<string>();
        foreach (var name in r.BlockNames) if (r.TemplateFor(name) == null) bare.Add(name);
        output.WriteLine(bare.Count + " blocks without a template, e.g. " + string.Join(", ", bare.GetRange(0, System.Math.Min(15, bare.Count))));
        Assert.Equal(8, r.Yield("HullThinLarge", 0.5)["SteelPlate"]);
        Assert.Equal(5, r.Yield("HullFullLargeDestroyed", 0.5)["SteelPlate"]);
        var th = r.Yield("ThrusterMSDirectional", 0.5);
        foreach (var kv in th) output.WriteLine("ThrusterMSDirectional " + kv.Key + " " + kv.Value);
        Assert.Equal(4, th["Motor"]);
        Assert.Equal(8, th["SteelPlate"]);
        Assert.Equal(16, th["MechanicalComponents"]);
        Assert.Equal(12, th["Electronics"]);
        Assert.Equal(2, th["Computer"]);
        Assert.Equal(8, th["OpticalFiber"]);
        Assert.Equal(111, r.Price("Motor"));
        Assert.Equal(10, r.Price("SteelPlate"));
        Assert.Equal(1000, r.StackSize("SteelPlate"));
        Assert.Equal(8000, r.StackSize("IronIngot"));
        Assert.Equal(0, r.StackSize("Unobtainium"));
        Assert.Equal("WindowSmallBlocks", r.TemplateFor("Window_v1x1").Name);   // group member, via ChildBlocks
        Assert.Empty(r.Yield("Window_v1x1", 0.5));   // 1 SteelPlate + 1 GlassPlate: half of one floors to nothing
        output.WriteLine("Window_v1x1 at 1.0: " + string.Join(", ", Pairs(r.Yield("Window_v1x1", 1.0))));
    }

    [Fact]
    public void Real_red_ledger_wreck_is_worth_tens_of_thousands()
    {
        var r = Real();
        if (r == null) return;
        var wreck = new Dictionary<string, int>
        {
            { "HullFullLarge", 94 }, { "HullThinLarge", 76 }, { "HullExtendedLarge2", 25 }, { "HullExtendedLarge6", 18 },
            { "HullExtendedLarge", 10 }, { "ThrusterMSDirectional", 7 }, { "HullExtendedLarge5", 6 },
        };
        var bag = new Dictionary<string, int>();
        foreach (var kv in wreck)
        {
            var one = r.Yield(kv.Key, Recipes.SalvageT1);
            Assert.NotEmpty(one);
            output.WriteLine(kv.Key + " x" + kv.Value + " -> " + string.Join(", ", Pairs(one)) + " = " + r.Value(one) + " cr each");
            foreach (var it in one) bag[it.Key] = (bag.ContainsKey(it.Key) ? bag[it.Key] : 0) + it.Value * kv.Value;
        }
        double total = r.Value(bag);
        output.WriteLine("total " + string.Join(", ", Pairs(bag)) + " = " + total + " cr");
        Assert.True(total > 10000, "Red Ledger wreck value " + total);
    }

    static IEnumerable<string> Pairs(Dictionary<string, int> d)
    {
        foreach (var kv in d) yield return kv.Key + " " + kv.Value;
    }
}
