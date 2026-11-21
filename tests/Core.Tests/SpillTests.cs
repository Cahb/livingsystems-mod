using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Sim;
using Xunit;

public class SpillTests
{
    static int StackOf(string item) => item == "SteelPlate" ? 8000 : item == "IronOre" ? 4000 : item.StartsWith("Gun") ? 1 : 100;

    static Dictionary<string, int> Kinds(int n, int each, string prefix = "Item")
    {
        var d = new Dictionary<string, int>();
        for (var i = 0; i < n; i++) d[prefix + i.ToString("000")] = each;
        return d;
    }

    [Fact]
    public void Twenty_kinds_one_each_is_one_sack()
    {
        var sacks = Spill.Split(Kinds(20, 1), StackOf, out var lost);
        Assert.Single(sacks); Assert.Empty(lost);
        Assert.Equal(20, Spill.Stacks(sacks[0], StackOf));
    }

    [Fact]
    public void Three_hundred_kinds_pack_120_120_60()
    {
        var sacks = Spill.Split(Kinds(300, 1), StackOf, out var lost);
        Assert.Empty(lost);
        Assert.Equal(new[] { 120, 120, 60 }, sacks.Select(s => Spill.Stacks(s, StackOf)).ToArray());
    }

    [Fact]
    public void A_sack_never_holds_more_stacks_than_it_packs()
    {
        var items = new Dictionary<string, int> { ["SteelPlate"] = 70_001, ["IronOre"] = 12_345, ["Gun1"] = 37, ["Gun2"] = 90 };
        foreach (var kv in Kinds(150, 250)) items[kv.Key] = kv.Value;
        var sacks = Spill.Split(items, StackOf, out _);
        Assert.All(sacks, s => Assert.InRange(Spill.Stacks(s, StackOf), 1, Spill.PerSack));
        Assert.True(Spill.PerSack < Spill.SackCrates * Spill.SackSlots);   // the margin for the crates' split
    }

    [Fact]
    public void Every_item_is_in_one_sack_or_lost_never_both_never_twice()
    {
        var items = Kinds(150, 100, "Gun");   // weapons stack to 1: 15,000 stacks — the live runaway's load
        var sacks = Spill.Split(items, StackOf, out var lost);
        Assert.Equal(Spill.MaxSacks, sacks.Count);
        Assert.NotEmpty(lost);
        var placed = sacks.SelectMany(s => s).GroupBy(kv => kv.Key).ToDictionary(g => g.Key, g => g.Sum(kv => kv.Value));
        foreach (var kv in items)
            Assert.Equal(kv.Value, (placed.TryGetValue(kv.Key, out var p) ? p : 0) + (lost.TryGetValue(kv.Key, out var l) ? l : 0));
        Assert.Equal(Spill.MaxSacks * Spill.PerSack, placed.Values.Sum());
    }

    [Fact]
    public void A_big_line_spans_sacks_in_its_own_stack_size()
    {
        var sacks = Spill.Split(new Dictionary<string, int> { ["SteelPlate"] = 8000 * 130 }, StackOf, out var lost);
        Assert.Empty(lost);
        Assert.Equal(2, sacks.Count);
        Assert.Equal(8000 * 120, sacks[0]["SteelPlate"]);
        Assert.Equal(8000 * 10, sacks[1]["SteelPlate"]);
    }

    [Fact]
    public void Keys_with_stack_data_keep_it_and_stack_by_their_base_item()
    {
        var sacks = Spill.Split(new Dictionary<string, int> { ["Gun7#250#0"] = 3, ["SteelPlate"] = 5 }, StackOf, out _);
        Assert.Equal(3, sacks[0]["Gun7#250#0"]);
        Assert.Equal(4, Spill.Stacks(sacks[0], StackOf));   // 3 guns (stack 1) + 1 plate stack
    }

    [Fact]
    public void Nothing_in_nothing_out()
    {
        Assert.Empty(Spill.Split(new Dictionary<string, int>(), StackOf, out var lost));
        Assert.Empty(lost);
        Assert.Empty(Spill.Split(new Dictionary<string, int> { ["X"] = 0, ["Y"] = -4 }, StackOf, out _));
    }
}
