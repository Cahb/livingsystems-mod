using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using LivingSystems.Core;
using Xunit;

public class RadarTests
{
    static string Plain(string line) => Regex.Replace(line, "<[^>]+>", "").PadRight(39);   // rows come without trailing spaces

    static List<string> Sample() => RadarAscii.Render(new List<RadarAscii.Blip>
    {
        new RadarAscii.Blip { X = 0, Y = 900, Z = 2500, Glyph = 'C', Color = "#66BB6A" },        // far ahead, above
        new RadarAscii.Blip { X = 2000, Y = -900, Z = -1500, Glyph = 'A', Color = "#66BB6A", VX = -40 },   // behind right, below, moving left
        new RadarAscii.Blip { X = -2600, Y = 0, Z = 0, Glyph = '■', Color = "#CFD8DC" },          // station, level, left
    }, 3000, 39, 15);

    [Fact]
    public void Grid_has_the_asked_size_and_the_ship_in_the_middle()
    {
        var lines = Sample().Select(Plain).ToList();
        Assert.Equal(15, lines.Count);
        Assert.All(lines, l => Assert.Equal(39, l.Length));
        Assert.Equal('▲', lines[7][19]);
    }

    [Fact]
    public void Above_stands_on_a_foot_below_hangs_under_one()
    {
        var lines = Sample().Select(Plain).ToList();
        int Row(char g) => lines.FindIndex(l => l.Contains(g));
        var c = Row('C'); var a = Row('A');
        Assert.True(c < 7, "ahead = upper half");
        Assert.Contains('┴', string.Concat(lines.Skip(c + 1).Select(l => l[lines[c].IndexOf('C')])));   // foot below C
        Assert.Contains('┬', string.Concat(lines.Take(a).Select(l => l[lines[a].IndexOf('A')])));       // foot above A
        Assert.Equal(7, Row('■'));                                                                        // level: on the plane row
        Assert.True(lines[7].IndexOf('■') < 19, "left of us");
    }

    [Fact]
    public void Range_zooms_to_fit()
    {
        Assert.Equal(750, RadarAscii.PickRange(new[] { new RadarAscii.Blip { X = 600 } }));
        Assert.Equal(3000, RadarAscii.PickRange(new[] { new RadarAscii.Blip { X = 2500 } }));
        Assert.Equal(1000, RadarAscii.RingDistance(2, 2250), 1);
        Assert.Equal(30000, RadarAscii.PickRange(new[] { new RadarAscii.Blip { Z = 99999 } }));
    }

    [Fact]
    public void Preview()
    {
        foreach (var l in Sample()) System.Console.WriteLine(Plain(l));
    }
}
