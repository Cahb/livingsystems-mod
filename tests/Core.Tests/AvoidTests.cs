using System.Collections.Generic;
using LivingSystems.Core.Flight;
using Xunit;

public class AvoidTests
{
    /// <summary>A solid box of block centres, 2 m blocks, from min to max (inclusive) — a hull stand-in.</summary>
    static Obstacle Box(float x0, float y0, float z0, float x1, float y1, float z1)
    {
        var pts = new List<Vec3>();
        for (var x = x0; x <= x1; x += 2) for (var y = y0; y <= y1; y += 2) for (var z = z0; z <= z1; z += 2) pts.Add(new Vec3(x, y, z));
        return new Obstacle(pts, 1f);
    }

    static readonly Vec3 A = new Vec3(0, 0, 0), B = new Vec3(0, 0, 400);

    [Fact]
    public void Clear_path_needs_no_detour()
    {
        var off = Box(100, -10, 180, 140, 10, 220);   // 100 m to the side
        Assert.Null(Avoid.Detour(A, B, 15, new[] { off }, Vec3.Up));
    }

    [Fact]
    public void Hull_across_the_path_gets_a_waypoint_whose_legs_are_clear()
    {
        var wall = Box(-40, -10, 190, 40, 10, 210);   // 80 m wide, 20 m tall, across the line at z=200
        var obs = new[] { wall };
        var w = Avoid.Detour(A, B, 15, obs, Vec3.Up);
        Assert.NotNull(w);
        Assert.Empty(Avoid.Blocking(A, w.Value, 15, obs, 0));
        Assert.Empty(Avoid.Blocking(w.Value, B, 15, obs, 0));
        Assert.True(w.Value.Y > 20, $"short way is over the top (wall is 20 m tall, 80 m wide): {w.Value}");   // up beats the sides
    }

    [Fact]
    public void Long_hull_is_passed_round_its_short_side()
    {
        var hull = Box(-20, -150, 190, 20, 150, 210);   // tall and narrow (a cruiser standing on end)
        var w = Avoid.Detour(A, B, 15, new[] { hull }, Vec3.Up);
        Assert.NotNull(w);
        Assert.True(System.Math.Abs(w.Value.X) > 30 && System.Math.Abs(w.Value.Y) < 150, $"goes round the side, not over 150 m: {w.Value}");
    }

    [Fact]
    public void Hull_next_to_the_goal_does_not_block_parking_beside_it()
    {
        var wreck = Box(-10, -10, 410, 10, 10, 440);   // the wreck the ship parks 10 m short of
        Assert.Null(Avoid.Detour(A, B, 15, new[] { wreck }, Vec3.Up, ends: 30));
        Assert.NotNull(Avoid.Detour(A, B, 15, new[] { wreck }, Vec3.Up, ends: 0));
    }

    [Fact]
    public void Gap_wider_than_the_tube_is_used_narrower_is_not()
    {
        // two hulls either side of the line: 60 m gap fits a 15 m tube, a 20 m gap does not
        var wide = new[] { Box(-60, -10, 190, -30, 10, 210), Box(30, -10, 190, 60, 10, 210) };
        Assert.Empty(Avoid.Blocking(A, B, 15, wide, 0));
        var narrow = new[] { Box(-40, -10, 190, -10, 10, 210), Box(10, -10, 190, 40, 10, 210) };
        Assert.NotEmpty(Avoid.Blocking(A, B, 15, narrow, 0));
    }
}
