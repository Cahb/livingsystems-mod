using System;

namespace LivingSystems.Core.Flight
{
    /// <summary>Kinematic state of one ship. AngVel is ship-local (x pitch, y yaw, z roll), rad/s.</summary>
    public struct ShipState
    {
        public Vec3 Pos, Vel;
        public Quat Rot;
        public Vec3 AngVel;
    }

    /// <summary>What a behaviour wants: a world velocity and an orientation.</summary>
    public struct FlightCommand
    {
        public Vec3 DesiredVel;
        public Quat DesiredRot;
    }

    /// <summary>
    /// Result of one controller step. The adapter sets <see cref="Rot"/> directly (there is no torque API)
    /// and turns <see cref="LocalAccel"/> into an <c>IEntity.Move</c> push (ship-local, verified).
    /// </summary>
    public struct FlightOutput
    {
        public Quat Rot;
        public Vec3 AngVel;
        public Vec3 LocalAccel;
    }

    /// <summary>
    /// Turns a command into capped motion: per-axis turn rates that accelerate and ease in (no snapping),
    /// and an acceleration clamped per thruster direction. Pure: same inputs, same outputs.
    /// </summary>
    public static class FlightController
    {
        const float Deg = 0.017453292f;

        /// <summary>Velocity error is closed over roughly this many seconds (then clamped by thrust).</summary>
        public const float VelocityTau = 0.4f;

        public static FlightOutput Step(ShipState s, ShipCaps caps, FlightCommand cmd, float dt)
        {
            if (dt <= 0) return new FlightOutput { Rot = s.Rot, AngVel = s.AngVel, LocalAccel = Vec3.Zero };

            // --- rotation: error in the ship's own frame, then each axis on its own cap ---
            var err = (s.Rot.Inverse * cmd.DesiredRot).ToRotationVector();
            var a = caps.TurnAccel * Deg;
            var w = new Vec3(
                AxisRate(err.X, s.AngVel.X, caps.TurnPitch * Deg, a, dt),
                AxisRate(err.Y, s.AngVel.Y, caps.TurnYaw * Deg, a, dt),
                AxisRate(err.Z, s.AngVel.Z, caps.TurnRoll * Deg, a, dt));
            var rot = (s.Rot * Quat.FromRotationVector(w * dt)).Normalized;

            // --- translation: close the velocity error, clamped per thruster direction ---
            var want = cmd.DesiredVel.ClampLength(caps.MaxSpeed);
            var worldAccel = (want - s.Vel) / Math.Max(VelocityTau, dt);
            var local = caps.ClampLocalAccel(rot.Inverse * worldAccel);

            return new FlightOutput { Rot = rot, AngVel = w, LocalAccel = local };
        }

        /// <summary>
        /// New angular rate on one axis: aim for the rate that still lets us stop on target
        /// (sqrt(2·a·err), capped), change the current rate by at most a·dt, never step past the target.
        /// </summary>
        static float AxisRate(float err, float rate, float cap, float accel, float dt)
        {
            var target = Math.Sign(err) * Math.Min(cap, (float)Math.Sqrt(2f * accel * Math.Abs(err)));
            var step = accel * dt;
            var next = rate + Math.Max(-step, Math.Min(step, target - rate));
            if (Math.Abs(next) > cap) next = Math.Sign(next) * cap;
            if (next * err > 0 && Math.Abs(next * dt) > Math.Abs(err)) next = err / dt;
            return next;
        }
    }
}
