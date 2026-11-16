using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using LivingSystems.Core;
using LivingSystems.Core.Sim;
using Xunit;

public class MapTests
{
    static string Plain(string line) => Regex.Replace(line, "<[^>]+>", "");

    [Fact]
    public void Facing_a_place_puts_it_straight_above_you()
    {
        // a station 10 units east; facing east (yaw 90) → straight up the forward line
        var lines = MapAscii.Render(new List<MapAscii.Place> { new MapAscii.Place { X = 10, Z = 0, Glyph = 'S' } }, 90, 12, 31, 15).Select(Plain).ToList();
        var row = lines.FindIndex(l => l.Contains('S'));
        Assert.True(row < 7);
        Assert.Equal(15, lines[row].IndexOf('S'));
        // facing north (yaw 0): east is to the right, level with you
        lines = MapAscii.Render(new List<MapAscii.Place> { new MapAscii.Place { X = 10, Z = 0, Glyph = 'S' } }, 0, 12, 31, 15).Select(Plain).ToList();
        Assert.Equal(7, lines.FindIndex(l => l.Contains('S')));
        Assert.True(lines[7].IndexOf('S') > 15);
    }

    [Fact]
    public void Sector_positions_are_parsed()
    {
        var g = Galaxy.Parse(new[] { "SolarSystems:\n  - Name: Getama\n    Coordinates: [-487, 14, 96]\n    Sectors:\n      - Coordinates: [42,3,5]\n        SectorMapType: Station\n        Playfields:\n          - ['0,0,0', QAMT 41494 II, EdenSpaceG_PolTradeStation,'', '', '', '']\n" });
        Assert.Equal(42, g.SectorPos["QAMT 41494 II"].X);
        Assert.Equal("Station", g.MapTypes["QAMT 41494 II"]);
    }

    /// <summary>Eyeball the real save's maps (only when .cache/sectors holds a copy of the sector files).</summary>
    [Fact]
    public void Preview_real_save()
    {
        var dir = Path.Combine(Directory.GetCurrentDirectory(), "../../../../../.cache/sectors");
        if (!Directory.Exists(dir)) return;
        var g = Galaxy.Parse(Directory.GetFiles(dir, "*.yaml").Select(File.ReadAllText));
        var pf = "QAMT 41494 II"; var star = g.StarOf(pf); var me = g.SectorPos[pf];
        var places = g.PlayfieldsOf(star).Where(p => p != pf && g.MapTypes.GetValueOrDefault(p) != "WarpTarget")
            .Select(p => new MapAscii.Place { X = g.SectorPos[p].X - me.X, Z = g.SectorPos[p].Z - me.Z, Glyph = p == star.Name ? '☼' : '●', Label = p }).ToList();
        var range = places.Max(p => (float)System.Math.Sqrt(p.X * p.X + p.Z * p.Z)) * 1.15f;
        System.Console.WriteLine($"SYSTEM {star.Name}, facing north:");
        foreach (var l in MapAscii.Render(places, 0, range, 55, 21)) System.Console.WriteLine(Plain(l));
        var stars = g.Stars.Values.Where(x => x != star).Select(x => new { x, d = (x.Pos - star.Pos).Length }).Where(x => x.d <= 90).OrderBy(x => x.d).Take(12)
            .Select(x => new MapAscii.Place { X = x.x.Pos.X - star.Pos.X, Z = x.x.Pos.Z - star.Pos.Z, Glyph = x.x.Gate ? '◈' : '✦', Label = $"{x.x.Name} {x.d:0}" }).ToList();
        range = stars.Max(p => (float)System.Math.Sqrt(p.X * p.X + p.Z * p.Z)) * 1.15f;
        System.Console.WriteLine("STARS, facing north:");
        foreach (var l in MapAscii.Render(stars, 0, range, 55, 21, sqrt: true)) System.Console.WriteLine(Plain(l));
    }
}
