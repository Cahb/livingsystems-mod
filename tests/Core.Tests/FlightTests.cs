using System.Linq;
using System.Collections.Generic;
using System;
using LivingSystems.Core.Flight;
using Xunit;

public class FlightMathTests
{
    static void Near(Vec3 a, Vec3 b, float tol = 1e-3f) =>
        Assert.True((a - b).Length < tol, $"{a} != {b}");

    [Fact]
    public void LookRotation_points_forward_and_keeps_up()
    {
        var dir = new Vec3(3, 1, -2);
        var q = Quat.LookRotation(dir, Vec3.Up);
        Near(q.Forward, dir.Normalized);
        Assert.True(Vec3.Dot(q.Up, Vec3.Up) > 0.9f);
        Assert.True(Math.Abs(Vec3.Dot(q.Right, Vec3.Up)) < 1e-4f);   // no roll
    }

    [Fact]
    public void LookRotation_matches_unity_for_simple_yaw()
    {
        // Unity: LookRotation(right) = 90° yaw about +Y → (0, 0.7071, 0, 0.7071)
        var q = Quat.LookRotation(Vec3.Right, Vec3.Up);
        Assert.Equal(0.7071f, q.Y, 3);
        Assert.Equal(0.7071f, q.W, 3);
    }

    [Fact]
    public void Rotation_vector_round_trips()
    {
        var r = new Vec3(0.3f, -1.1f, 0.5f);
        Near(Quat.FromRotationVector(r).ToRotationVector(), r);
    }

    [Fact]
    public void Local_frame_conversion_round_trips()
    {
        var q = Quat.LookRotation(new Vec3(1, 2, 3), new Vec3(0, 1, 0.2f));
        var v = new Vec3(5, -4, 7);
        Near(q * (q.Inverse * v), v);
    }
}

public class FlightTests
{
    // Roughly «LS» Spike 5 (757 t): 21.2 MN along the nose, 12 MN back, turn 11/16/22 °/s.
    static readonly ShipCaps Caps = ShipCaps.FromForces(757_000f, 21.2e6f, 12.0e6f, 9e6f, 9e6f, 6e6f, 6e6f, 11, 16, 22);
    const float Dt = 0.05f;   // PfServer frame

    /// <summary>Ideal physics: the push is exactly the commanded acceleration.</summary>
    static ShipState Integrate(ShipState s, FlightOutput o)
    {
        s.Rot = o.Rot;
        s.AngVel = o.AngVel;
        s.Vel = s.Vel + (o.Rot * o.LocalAccel) * Dt;
        s.Pos = s.Pos + s.Vel * Dt;
        return s;
    }

    static ShipState At(Vec3 pos, Vec3 facing) =>
        new ShipState { Pos = pos, Rot = Quat.LookRotation(facing, Vec3.Up) };

    [Fact]
    public void Attack_keeps_the_nose_on_a_moving_target_at_the_standoff()
    {
        var s = At(new Vec3(600, 0, 0), new Vec3(0, 0, 1));   // beside the target, facing along its course
        var target = Vec3.Zero; var tv = new Vec3(0, 0, 20);  // trader cruising +Z at 20 m/s
        for (var t = 0f; t < 120f; t += Dt)
        {
            target = target + tv * Dt;
            var cmd = Behaviours.Attack(s, Caps, target, tv, 300, 60);
            s = Integrate(s, FlightController.Step(s, Caps, cmd, Dt));
        }
        var toTarget = (target - s.Pos).Normalized;
        Assert.True(Vec3.Dot(s.Rot.Forward, toTarget) > 0.97f, "nose on target");
        Assert.InRange((target - s.Pos).Length, 270f, 330f);
    }

    [Fact]
    public void Attack_with_orbit_circles_a_still_target_nose_on_at_the_standoff()
    {
        var s = At(new Vec3(600, 0, 0), new Vec3(-1, 0, 0));
        var target = Vec3.Zero;
        var angles = new List<double>();
        for (var t = 0f; t < 120f; t += Dt)
        {
            var cmd = Behaviours.Attack(s, Caps, target, Vec3.Zero, 300, 60, orbit: 25f);
            s = Integrate(s, FlightController.Step(s, Caps, cmd, Dt));
            if (t > 60f) angles.Add(Math.Atan2(s.Pos.Z, s.Pos.X));
        }
        Assert.True(Vec3.Dot(s.Rot.Forward, (target - s.Pos).Normalized) > 0.95f, "nose on target");
        Assert.InRange(s.Pos.Length, 240f, 360f);
        var swept = Math.Abs(angles.Last() - angles.First());
        Assert.True(swept > 0.5, $"moved round the target ({swept:0.00} rad in the last minute)");
    }

    [Fact]
    public void Attack_with_a_tilted_orbit_goes_over_the_target()
    {
        var s = At(new Vec3(600, 0, 0), new Vec3(-1, 0, 0));
        var maxUp = 0f;
        for (var t = 0f; t < 120f; t += Dt)
        {
            var cmd = Behaviours.Attack(s, Caps, Vec3.Zero, Vec3.Zero, 300, 60, orbit: 25f, orbitAxis: new Vec3(0, 0, 1));   // circle round Z: through above / below
            s = Integrate(s, FlightController.Step(s, Caps, cmd, Dt));
            maxUp = Math.Max(maxUp, s.Pos.Y);
        }
        Assert.True(maxUp > 150f, $"went well above the target (max height {maxUp:0} m)");
        Assert.InRange(s.Pos.Length, 240f, 360f);
    }

    [Fact]
    public void Follow_stationary_leader_settles_at_the_gap_without_cutting_inside()
    {
        var s = At(new Vec3(0, 0, -800), new Vec3(1, 0, 0));   // side-on to the leader
        var minDist = float.MaxValue;
        for (var t = 0f; t < 120f; t += Dt)
        {
            var cmd = Behaviours.Follow(s, Caps, Vec3.Zero, Vec3.Zero, 150, 60);
            s = Integrate(s, FlightController.Step(s, Caps, cmd, Dt));
            minDist = Math.Min(minDist, s.Pos.Length);
        }
        Assert.InRange(s.Pos.Length, 145f, 155f);
        Assert.True(s.Vel.Length < 0.5f, $"still moving {s.Vel.Length}");
        Assert.True(minDist > 140f, $"cut inside to {minDist}");
        Assert.True(Vec3.Dot(s.Rot.Forward, (-s.Pos).Normalized) > 0.95f, "not facing the leader");
    }

    [Fact]
    public void Follow_moving_leader_matches_its_velocity_at_the_gap()
    {
        var s = At(new Vec3(0, 0, -400), Vec3.Forward);
        var leader = Vec3.Zero;
        var leaderVel = new Vec3(0, 0, 25);
        for (var t = 0f; t < 120f; t += Dt)
        {
            var cmd = Behaviours.Follow(s, Caps, leader, leaderVel, 150, 60);
            s = Integrate(s, FlightController.Step(s, Caps, cmd, Dt));
            leader = leader + leaderVel * Dt;
        }
        Assert.InRange((s.Pos - leader).Length, 145f, 160f);
        Assert.True((s.Vel - leaderVel).Length < 1f, $"vel {s.Vel}");
    }

    [Fact]
    public void Leader_stops_and_the_follower_stops_short_of_it()
    {
        var s = At(new Vec3(0, 0, -150), Vec3.Forward);
        s.Vel = new Vec3(0, 0, 40);
        var leader = Vec3.Zero;
        var minDist = float.MaxValue;
        for (var t = 0f; t < 60f; t += Dt)
        {
            var cmd = Behaviours.Follow(s, Caps, leader, Vec3.Zero, 150, 60);   // leader just stopped
            s = Integrate(s, FlightController.Step(s, Caps, cmd, Dt));
            minDist = Math.Min(minDist, (s.Pos - leader).Length);
        }
        Assert.True(s.Vel.Length < 0.5f);
        Assert.True(minDist > 30f, $"came within {minDist} m");   // 40 m/s at 12.7 m/s² brake ≈ 63 m overshoot of the gap at most
    }

    [Fact]
    public void Never_exceeds_thrust_or_turn_caps()
    {
        var s = At(new Vec3(500, 200, -300), new Vec3(-1, 0, 0));
        s.Vel = new Vec3(30, -10, 0);
        var leader = Vec3.Zero;
        for (var t = 0f; t < 60f; t += Dt)
        {
            var lv = new Vec3((float)Math.Sin(t / 5) * 30, 0, (float)Math.Cos(t / 5) * 30);   // leader circling
            leader = leader + lv * Dt;
            var o = FlightController.Step(s, Caps, Behaviours.Follow(s, Caps, leader, lv, 150, 80), Dt);
            Assert.InRange(o.LocalAccel.Z, -Caps.AccelBack - 1e-3f, Caps.AccelForward + 1e-3f);
            Assert.InRange(o.LocalAccel.X, -Caps.AccelLeft - 1e-3f, Caps.AccelRight + 1e-3f);
            Assert.InRange(o.LocalAccel.Y, -Caps.AccelDown - 1e-3f, Caps.AccelUp + 1e-3f);
            Assert.True(Math.Abs(o.AngVel.X) <= Caps.TurnPitch * 0.0175f + 1e-4f);
            Assert.True(Math.Abs(o.AngVel.Y) <= Caps.TurnYaw * 0.0175f + 1e-4f);
            Assert.True(Math.Abs(o.AngVel.Z) <= Caps.TurnRoll * 0.0175f + 1e-4f);
            s = Integrate(s, o);
        }
    }

    [Fact]
    public void GoTo_arrives_without_overshoot()
    {
        var s = At(Vec3.Zero, Vec3.Forward);
        var target = new Vec3(300, 0, 1500);
        var minDist = float.MaxValue;
        for (var t = 0f; t < 120f; t += Dt)
        {
            s = Integrate(s, FlightController.Step(s, Caps, Behaviours.GoTo(s, Caps, target, 50, 80), Dt));
            minDist = Math.Min(minDist, (target - s.Pos).Length);
        }
        Assert.InRange((target - s.Pos).Length, 45f, 55f);
        Assert.True(minDist > 40f, $"overshot to {minDist}");
        Assert.True(s.Vel.Length < 0.5f);
    }

    [Fact]
    public void Hold_brakes_to_a_stop()
    {
        var s = At(Vec3.Zero, Vec3.Forward);
        s.Vel = new Vec3(10, 0, 50);
        for (var t = 0f; t < 30f; t += Dt)
            s = Integrate(s, FlightController.Step(s, Caps, Behaviours.Hold(s), Dt));
        Assert.True(s.Vel.Length < 0.1f);
    }

    [Fact]
    public void Turns_before_it_accelerates()
    {
        // Leader behind the ship: it should turn round, not back up or slide there at speed.
        var s = At(new Vec3(0, 0, 600), Vec3.Forward);
        var maxSideways = 0f;
        for (var t = 0f; t < 90f; t += Dt)
        {
            var cmd = Behaviours.Follow(s, Caps, Vec3.Zero, Vec3.Zero, 150, 60);
            s = Integrate(s, FlightController.Step(s, Caps, cmd, Dt));
            var local = s.Rot.Inverse * s.Vel;
            maxSideways = Math.Max(maxSideways, Math.Max(Math.Abs(local.X), Math.Abs(local.Y)));
        }
        Assert.True(maxSideways < 8f, $"slid sideways at {maxSideways:0.0} m/s");
        Assert.InRange(s.Pos.Length, 140f, 160f);
    }

    [Fact]
    public void A_rolled_ship_levels_out_to_the_leaders_up()
    {
        // ship rolled 90° on its side, facing the leader; leader is level
        var s = At(new Vec3(0, 0, -150), Vec3.Forward);
        s.Rot = s.Rot * Quat.FromRotationVector(new Vec3(0, 0, 1.5708f));
        for (var t = 0f; t < 15f; t += Dt)
            s = Integrate(s, FlightController.Step(s, Caps, Behaviours.Follow(s, Caps, Vec3.Zero, Vec3.Zero, Vec3.Up, 150, 40), Dt));
        Assert.True(Vec3.Dot(s.Rot.Up, Vec3.Up) > 0.99f, $"up·Y = {Vec3.Dot(s.Rot.Up, Vec3.Up)}");
    }

    [Fact]
    public void Rolls_with_a_rolled_leader()
    {
        var s = At(new Vec3(0, 0, -150), Vec3.Forward);
        var leaderUp = new Vec3(1, 0, 0);   // leader on its side
        for (var t = 0f; t < 15f; t += Dt)
            s = Integrate(s, FlightController.Step(s, Caps, Behaviours.Follow(s, Caps, Vec3.Zero, Vec3.Zero, leaderUp, 150, 40), Dt));
        Assert.True(Vec3.Dot(s.Rot.Up, leaderUp) > 0.99f);
    }

    [Fact]
    public void HoldAt_returns_to_its_spot_without_turning_round()
    {
        var s = At(new Vec3(12, 0, 0), Vec3.Forward);   // pushed 12 m sideways off its spot
        var start = s.Rot;
        for (var t = 0f; t < 30f; t += Dt)
            s = Integrate(s, FlightController.Step(s, Caps, Behaviours.HoldAt(s, Caps, Vec3.Zero, 40), Dt));
        Assert.True(s.Pos.Length < 2f, $"{s.Pos.Length:0.0} m off");
        Assert.True(Quat.AngleDeg(start, s.Rot) < 1f, "turned while holding");
        Assert.True(s.Vel.Length < 0.3f);
    }

    [Fact]
    public void HoldAt_flies_back_from_far_away()
    {
        var s = At(new Vec3(0, 0, 400), Vec3.Forward);
        for (var t = 0f; t < 90f; t += Dt)
            s = Integrate(s, FlightController.Step(s, Caps, Behaviours.HoldAt(s, Caps, Vec3.Zero, 40), Dt));
        Assert.True(s.Pos.Length < 3f, $"{s.Pos.Length:0.0} m off");
    }

    [Fact]
    public void Turn_rate_ramps_up_instead_of_snapping()
    {
        var s = At(Vec3.Zero, Vec3.Forward);
        var cmd = new FlightCommand { DesiredRot = Quat.LookRotation(Vec3.Right, Vec3.Up) };
        var o = FlightController.Step(s, Caps, cmd, Dt);
        Assert.True(Math.Abs(o.AngVel.Y) <= Caps.TurnAccel * 0.0175f * Dt + 1e-4f);
    }
}
