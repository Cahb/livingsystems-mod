using System;

namespace LivingSystems.Core.Flight
{
    public enum Responsiveness { Unknown, Responsive, Unresponsive }

    /// <summary>
    /// Learns, per ship and per local push direction, the acceleration at full thrust — and whether the ship
    /// obeys at all. The game flies <c>Move</c> through the ship's real thrusters (measured), so the
    /// measured full-thrust acceleration IS the ship's capability: no thruster scan, no config parsing.
    /// Nothing in the API says when a ship starts obeying (fresh spawns ignore Move for ~16 s with every flag
    /// settled; later: no fuel, no power, thrusters shot off), so responsiveness is measured too.
    ///
    /// Input is a throttle per axis in [-1, 1] (fraction of full thrust); directions are
    /// +X right, −X left, +Y up, −Y down, +Z forward, −Z back.
    /// </summary>
    public sealed class ResponseEstimator
    {
        /// <summary>
        /// Full-thrust acceleration assumed for PLANNING until measured, m/s². Low = safe: brakes early.
        /// </summary>
        public const float PriorAccel = 6f;
        /// <summary>
        /// Full-thrust acceleration assumed for THROTTLE until measured, m/s². High = safe: gentle pushes.
        /// A low guess here makes every correction 4–5× too strong (fore/aft wobble).
        /// </summary>
        public const float ThrottlePriorAccel = 30f;
        /// <summary>The first sample of a direction (which replaces the guess) needs at least this throttle.</summary>
        public const float FirstSampleThrottle = 0.2f;
        /// <summary>
        /// Throttle below this teaches nothing: the sample is accel ÷ throttle, so noise is multiplied by 1/throttle
        /// (at 0.05 the estimates swung 29–56 forward and 18–69 back in game; real ≈ 28 / 16).
        /// </summary>
        public const float MinThrottle = 0.2f;
        /// <summary>Expected acceleration below this (m/s²) teaches nothing: position noise.</summary>
        public const float MinExpectedAccel = 1f;
        /// <summary>Response below this fraction of the expected one counts as "nothing happened".</summary>
        public const float DeadFraction = 0.2f;
        /// <summary>Seconds of thrusting with no response before a ship is called unresponsive.</summary>
        public const float UnresponsiveAfter = 3f;
        /// <summary>Seconds of consistent response before we trust it.</summary>
        public const float ResponsiveAfter = 0.5f;
        /// <summary>Smoothing time constant, seconds.</summary>
        public const float Tau = 1.5f;
        /// <summary>One sample may pull an estimate toward at most this factor of its current value.</summary>
        public const float MaxStep = 1.5f;
        /// <summary>Sane range for full-thrust acceleration, m/s².</summary>
        public const float MinAccel = 0.3f, MaxAccel = 200f;

        public Responsiveness State { get; private set; } = Responsiveness.Unknown;

        // index: axis*2 + (negative ? 1 : 0); axis 0 = X, 1 = Y, 2 = Z
        readonly float[] full = { PriorAccel, PriorAccel, PriorAccel, PriorAccel, PriorAccel, PriorAccel };
        readonly bool[] learned = new bool[6];
        readonly float[] dirDead = new float[6];
        /// <summary>Seconds one direction must stay dead (ship otherwise known to fly) before its estimate decays.</summary>
        public const float DirectionLostAfter = 1.5f;
        /// <summary>Below this measured acceleration (m/s²) the ship counts as not moving at all (frozen), not damaged.</summary>
        public const float FrozenAccel = 0.1f;
        float deadTime, aliveTime;
        bool everResponsive;
        /// <summary>Thrust known up front (the hull's own stats): estimates stay fixed, only responsiveness is still watched.</summary>
        public bool Pinned { get; private set; }

        /// <summary>Fix full thrust per direction (m/s²) — e.g. read off the hull's Statistics panel; nothing is learned.</summary>
        public void Pin(float forward, float back, float up, float down, float right, float left)
        {
            float[] v = { right, left, up, down, forward, back };
            for (var i = 0; i < 6; i++) { full[i] = Math.Max(MinAccel, Math.Min(MaxAccel, v[i])); learned[i] = true; }
            Pinned = true;
        }

        public float FullAccel(int axis, bool negative) => full[axis * 2 + (negative ? 1 : 0)];
        public bool Learned(int axis, bool negative) => learned[axis * 2 + (negative ? 1 : 0)];

        /// <summary>
        /// One frame of evidence: the throttle in effect since last frame and the acceleration measured over
        /// that frame (m/s², ship-local).
        /// </summary>
        public void Observe(Vec3 throttle, Vec3 accel, float dt)
        {
            if (dt <= 0) return;
            bool any = false, alive = false;
            var k = Math.Min(1f, dt / Tau);
            for (var axis = 0; axis < 3; axis++)
            {
                var th = Get(throttle, axis);
                if (Math.Abs(th) < MinThrottle) continue;
                var i = axis * 2 + (th < 0 ? 1 : 0);
                var basis = learned[i] ? full[i] : ThrottlePriorAccel;
                // noise guard; never assume less than the prior here, or a direction whose estimate decayed after
                // damage would stop counting as "commanded" and a dead ship would never read unresponsive
                if (Math.Abs(th) * Math.Max(basis, PriorAccel) < MinExpectedAccel) continue;
                any = true;
                var sample = Get(accel, axis) / th;   // full-thrust accel implied by this frame; ≥ 0 when it obeys
                if (sample < DeadFraction * Math.Min(basis, full[i]))
                {
                    // Too little response. Two very different causes:
                    //  * NOTHING at all (|accel| ≈ 0): the whole ship is frozen — the game stops simulating ships far
                    //    from players (a ship 2 km out freezes, then becomes a proxy), or no fuel / power. Not damage:
                    //    don't touch the estimate (it would decay towards nothing).
                    //  * SOME response, far below learned: thrusters lost in this direction → let it fall toward what
                    //    is measured, or damage would never be noticed.
                    dirDead[i] += dt;
                    var weakButAlive = Math.Abs(Get(accel, axis)) >= FrozenAccel;
                    if (!Pinned && weakButAlive && learned[i] && everResponsive && dirDead[i] >= DirectionLostAfter)
                        full[i] = Math.Max(MinAccel, full[i] + (Math.Max(0f, sample) - full[i]) * k);
                    continue;
                }
                dirDead[i] = 0;
                alive = true;
                if (Pinned) continue;
                if (!learned[i])
                {
                    // first real sample replaces the guess — but only from a solid throttle, and bounded
                    if (Math.Abs(th) < FirstSampleThrottle) continue;
                    full[i] = Math.Max(MinAccel, Math.Min(ThrottlePriorAccel * MaxStep, sample));
                    learned[i] = true;
                    continue;
                }
                sample = Math.Min(sample, full[i] * MaxStep);
                full[i] = Math.Max(MinAccel, Math.Min(MaxAccel, full[i] + (sample - full[i]) * k));
            }
            if (!any) return;   // braking / idle frames say nothing

            if (alive)
            {
                deadTime = 0; aliveTime += dt;
                if (aliveTime >= ResponsiveAfter) { State = Responsiveness.Responsive; everResponsive = true; }
            }
            else
            {
                aliveTime = 0; deadTime += dt;
                if (deadTime >= UnresponsiveAfter) State = Responsiveness.Unresponsive;
            }
        }

        /// <summary>Throttle per axis (each in [-1, 1]) that should produce <paramref name="accel"/> (m/s², local).</summary>
        public Vec3 ThrottleFor(Vec3 accel) => new Vec3(
            Clamp1(accel.X / ForThrottle(0, accel.X < 0)),
            Clamp1(accel.Y / ForThrottle(1, accel.Y < 0)),
            Clamp1(accel.Z / ForThrottle(2, accel.Z < 0)));

        /// <summary>Planning-side estimate (cautious prior until learned) — use for on/off axes' key decisions.</summary>
        public Vec3 FractionOfPlanned(Vec3 accel) => new Vec3(
            Clamp1(accel.X / FullAccel(0, accel.X < 0)),
            Clamp1(accel.Y / FullAccel(1, accel.Y < 0)),
            Clamp1(accel.Z / FullAccel(2, accel.Z < 0)));

        float ForThrottle(int axis, bool negative) => Learned(axis, negative) ? FullAccel(axis, negative) : ThrottlePriorAccel;

        /// <summary>
        /// Planning limits for the controller: learned thrust per direction plus turn caps
        /// (turn rates are ours to choose — the game does not limit a set Rotation).
        /// </summary>
        public ShipCaps Caps(float massKg) => ShipCaps.FromAccel(
            FullAccel(2, false), FullAccel(2, true), FullAccel(1, false), FullAccel(1, true),
            FullAccel(0, false), FullAccel(0, true), massKg);

        static float Clamp1(float v) => v < -1 ? -1 : v > 1 ? 1 : v;
        static float Get(Vec3 v, int axis) => axis == 0 ? v.X : axis == 1 ? v.Y : v.Z;
    }
}
