using System;

namespace LivingSystems.Core.Flight
{
    /// <summary>
    /// What a ship can physically do. <c>IEntity.Move</c> is an uncapped push that ignores the ship's real
    /// thrusters, so every limit comes from here. Accelerations are per local push direction
    /// (AccelForward = how hard it can push along its nose, i.e. its rear-facing thrusters).
    /// </summary>
    public sealed class ShipCaps
    {
        public float AccelForward, AccelBack, AccelUp, AccelDown, AccelRight, AccelLeft;   // m/s²
        public float TurnPitch, TurnYaw, TurnRoll;                                         // deg/s
        public float TurnAccel = 30f;                                                      // deg/s², all axes
        public float MaxSpeed = 100f;                                                      // m/s (RE2 CV cap)

        /// <summary>
        /// From summed thruster force per push direction (newtons: RE2's <c>ThrusterForce</c> × 1000) and mass (kg).
        /// Turn caps come straight from the ship inspector / torque model.
        /// </summary>
        public static ShipCaps FromForces(float massKg, float fwdN, float backN, float upN, float downN,
            float rightN, float leftN, float pitchDeg, float yawDeg, float rollDeg, float maxSpeed = 100f)
        {
            if (massKg <= 0) throw new ArgumentOutOfRangeException(nameof(massKg));
            return new ShipCaps
            {
                AccelForward = fwdN / massKg, AccelBack = backN / massKg,
                AccelUp = upN / massKg, AccelDown = downN / massKg,
                AccelRight = rightN / massKg, AccelLeft = leftN / massKg,
                TurnPitch = pitchDeg, TurnYaw = yawDeg, TurnRoll = rollDeg,
                MaxSpeed = maxSpeed,
            };
        }

        /// <summary>
        /// From full-thrust accelerations per push direction (m/s², as learned in flight) and mass. Turn rates are
        /// not enforced by the game (we set Rotation), so they come from <see cref="TurnRatesFor"/>.
        /// </summary>
        public static ShipCaps FromAccel(float fwd, float back, float up, float down, float right, float left, float massKg)
        {
            TurnRatesFor(massKg, out var pitch, out var yaw, out var roll);
            return new ShipCaps
            {
                AccelForward = fwd, AccelBack = back, AccelUp = up, AccelDown = down, AccelRight = right, AccelLeft = left,
                TurnPitch = pitch, TurnYaw = yaw, TurnRoll = roll,
            };
        }

        /// <summary>
        /// Placeholder turn rates scaled from the one ship read off the inspector (757 t CV: pitch 11, yaw 16,
        /// roll 22 °/s) by (757 t / mass)^⅓ — heavier turns slower — clamped to 3…60 °/s.
        /// Refine with more inspector readings (or torque from config) when it matters.
        /// </summary>
        public static void TurnRatesFor(float massKg, out float pitch, out float yaw, out float roll)
        {
            var scale = massKg > 0 ? (float)Math.Pow(757_000.0 / massKg, 1.0 / 3.0) : 1f;
            pitch = Clamp(11f * scale, 3f, 60f);
            yaw = Clamp(16f * scale, 3f, 60f);
            roll = Clamp(22f * scale, 3f, 60f);
        }

        /// <summary>Deceleration we plan stops with: the weaker of fore/aft, with a margin.</summary>
        public float BrakeAccel => 0.8f * Math.Min(AccelForward, AccelBack);

        /// <summary>Clamp a local acceleration to what the thrusters on each axis can give.</summary>
        public Vec3 ClampLocalAccel(Vec3 a) => new Vec3(
            Clamp(a.X, -AccelLeft, AccelRight),
            Clamp(a.Y, -AccelDown, AccelUp),
            Clamp(a.Z, -AccelBack, AccelForward));

        static float Clamp(float v, float lo, float hi) => v < lo ? lo : v > hi ? hi : v;
    }
}
