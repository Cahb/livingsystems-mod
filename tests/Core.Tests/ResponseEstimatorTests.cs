using System;
using LivingSystems.Core.Flight;
using Xunit;

public class ResponseEstimatorTests
{
    const float Dt = 0.05f;

    // Measured on the «LS» Spike CV (757 t): forward 28, up 11.9, down 7.9 m/s² at full thrust.
    const float Fwd = 28f, Up = 11.9f, Down = 7.9f;

    /// <summary>A ship that ignores throttle until <paramref name="graceSeconds"/>, then gives throttle × full accel.</summary>
    static Vec3 Ship(Vec3 th, float t, float graceSeconds = 0) =>
        t < graceSeconds ? Vec3.Zero
        : new Vec3(th.X * 7.9f, th.Y * (th.Y >= 0 ? Up : Down), th.Z * (th.Z >= 0 ? Fwd : 15.9f));

    static ResponseEstimator Run(Vec3 throttle, float seconds, float grace = 0)
    {
        var r = new ResponseEstimator();
        for (float t = 0; t < seconds; t += Dt) r.Observe(throttle, Ship(throttle, t, grace), Dt);
        return r;
    }

    [Fact]
    public void Starts_unknown_with_a_cautious_prior()
    {
        var r = new ResponseEstimator();
        Assert.Equal(Responsiveness.Unknown, r.State);
        Assert.Equal(ResponseEstimator.PriorAccel, r.FullAccel(2, false));
        Assert.False(r.Learned(2, false));
    }

    [Fact]
    public void Fresh_spawn_reads_unresponsive_then_responsive_when_the_grace_ends()
    {
        var r = new ResponseEstimator();
        var th = new Vec3(0, 0, 0.5f);
        float t = 0;
        for (; t < 10; t += Dt) r.Observe(th, Ship(th, t, 16), Dt);
        Assert.Equal(Responsiveness.Unresponsive, r.State);
        for (; t < 20; t += Dt) r.Observe(th, Ship(th, t, 16), Dt);
        Assert.Equal(Responsiveness.Responsive, r.State);
    }

    [Fact]
    public void Learns_full_thrust_per_direction_from_partial_throttle()
    {
        var r = Run(new Vec3(0, 1f, 0.3f), 20);   // on/off lift at full, forward at 30 % throttle
        Assert.Equal(Fwd, r.FullAccel(2, false), 0);
        Assert.Equal(Up, r.FullAccel(1, false), 0);
        Assert.False(r.Learned(0, false));
        Assert.Equal(ResponseEstimator.PriorAccel, r.FullAccel(2, true));   // back never used
    }

    [Fact]
    public void Down_is_learned_separately_from_up()
    {
        var r = Run(new Vec3(0, -1f, 0), 20);
        Assert.Equal(Down, r.FullAccel(1, true), 0);
        Assert.Equal(ResponseEstimator.PriorAccel, r.FullAccel(1, false));
    }

    [Fact]
    public void Braking_frames_teach_nothing()
    {
        var r = new ResponseEstimator();
        for (float t = 0; t < 10; t += Dt) r.Observe(Vec3.Zero, new Vec3(0, 0, -12), Dt);
        Assert.Equal(Responsiveness.Unknown, r.State);
        Assert.False(r.Learned(2, false));
    }

    [Fact]
    public void Tiny_throttle_with_unrelated_acceleration_does_not_teach()
    {
        var r = new ResponseEstimator();
        for (float t = 0; t < 10; t += Dt) r.Observe(new Vec3(0, 0.1f, 0), new Vec3(0, 8f, 0), Dt);
        Assert.False(r.Learned(1, false));
    }

    [Fact]
    public void Losing_thrust_later_is_noticed()
    {
        var r = new ResponseEstimator();
        var th = new Vec3(0, 0, 0.5f);
        float t = 0;
        for (; t < 5; t += Dt) r.Observe(th, Ship(th, t), Dt);
        Assert.Equal(Responsiveness.Responsive, r.State);
        for (; t < 10; t += Dt) r.Observe(th, Vec3.Zero, Dt);   // out of fuel
        Assert.Equal(Responsiveness.Unresponsive, r.State);
    }

    [Fact]
    public void A_wild_first_sample_is_bounded()
    {
        var r = new ResponseEstimator();
        r.Observe(new Vec3(0, 0, 1), new Vec3(0, 0, 500), Dt);
        Assert.True(r.FullAccel(2, false) <= ResponseEstimator.ThrottlePriorAccel * ResponseEstimator.MaxStep);
    }

    [Fact]
    public void Once_learned_a_single_outlier_moves_the_estimate_only_a_little()
    {
        var r = Run(new Vec3(0, 0, 0.5f), 10);
        r.Observe(new Vec3(0, 0, 1), new Vec3(0, 0, 500), Dt);
        Assert.True(r.FullAccel(2, false) < Fwd * 1.15f, $"{r.FullAccel(2, false)}");
    }

    [Fact]
    public void Throttle_uses_a_high_guess_until_learned_planning_a_low_one()
    {
        var r = new ResponseEstimator();
        Assert.Equal(10f / ResponseEstimator.ThrottlePriorAccel, r.ThrottleFor(new Vec3(0, 0, 10)).Z, 3);
        Assert.Equal(ResponseEstimator.PriorAccel, r.Caps(757_000f).AccelForward);
    }

    [Fact]
    public void Thrusters_shot_off_in_one_direction_lower_that_estimate()
    {
        var r = new ResponseEstimator();
        float t = 0;
        var fwd = new Vec3(0, 0, 0.5f);
        for (; t < 5; t += Dt) r.Observe(fwd, Ship(fwd, t), Dt);
        Assert.Equal(Fwd, r.FullAccel(2, false), 0);
        for (; t < 15; t += Dt) r.Observe(fwd, new Vec3(0, 0, 0.5f * 2f), Dt);   // rear thrusters gone: 2 m/s² left
        Assert.True(r.FullAccel(2, false) < 4f, $"{r.FullAccel(2, false)}");
        Assert.Equal(ResponseEstimator.PriorAccel, r.FullAccel(1, true));
    }

    [Fact]
    public void A_frozen_ship_does_not_lose_its_learned_thrust()
    {
        // the game stops simulating ships far from players: zero response is "frozen", not "damaged"
        var r = new ResponseEstimator();
        float t = 0;
        var fwd = new Vec3(0, 0, 0.5f);
        for (; t < 5; t += Dt) r.Observe(fwd, Ship(fwd, t), Dt);
        for (; t < 30; t += Dt) r.Observe(fwd, Vec3.Zero, Dt);
        Assert.Equal(Fwd, r.FullAccel(2, false), 0);
        Assert.Equal(Responsiveness.Unresponsive, r.State);
    }

    [Fact]
    public void ThrottleFor_inverts_and_clamps()
    {
        var r = Run(new Vec3(0, 0, 0.5f), 20);
        Assert.Equal(0.25f, r.ThrottleFor(new Vec3(0, 0, Fwd / 4)).Z, 2);
        Assert.Equal(1f, r.ThrottleFor(new Vec3(0, 0, 1000)).Z);
    }

    [Fact]
    public void Caps_come_from_what_was_learned_and_turn_slower_when_heavier()
    {
        var r = Run(new Vec3(0, 1f, 0.5f), 20);
        var light = r.Caps(757_000f);
        var heavy = r.Caps(757_000f * 8);
        Assert.Equal(Fwd, light.AccelForward, 0);
        Assert.Equal(Up, light.AccelUp, 0);
        Assert.Equal(16f, light.TurnYaw, 1);
        Assert.Equal(8f, heavy.TurnYaw, 1);   // 8× mass → half the turn rate
    }
}
