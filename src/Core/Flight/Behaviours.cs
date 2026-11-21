using System;

namespace LivingSystems.Core.Flight
{
    /// <summary>
    /// Behaviours produce a <see cref="FlightCommand"/>; <see cref="FlightController"/> makes it physical.
    /// Roll: ships roll toward an up reference — the leader's up when following (formation look), the
    /// playfield's up (+Y, ships spawn level) otherwise — at their roll rate.
    /// </summary>
    public static class Behaviours
    {
        /// <summary>Below this speed a ship faces its point of interest instead of its travel direction.</summary>
        public const float FaceTravelSpeed = 5f;

        /// <summary>Stop where you are, keep facing.</summary>
        public static FlightCommand Hold(ShipState s) =>
            new FlightCommand { DesiredVel = Vec3.Zero, DesiredRot = s.Rot };

        /// <summary>Within this distance of its hold spot a ship keeps its heading and only nudges back.</summary>
        public const float HoldRadius = 30f;

        /// <summary>
        /// Keep a spot. Near it (within <see cref="HoldRadius"/>): keep facing, drift back slowly (at most strafe
        /// speed, braking curve included) — no turning to face every metre of drift. Further: fly back like GoTo.
        /// </summary>
        public static FlightCommand HoldAt(ShipState s, ShipCaps caps, Vec3 spot, float cruise)
        {
            var rel = spot - s.Pos;
            if (rel.Length > HoldRadius) return GoTo(s, caps, spot, 0f, cruise);
            var speed = Math.Min(FaceTravelSpeed, Steering.ApproachSpeed(Ahead(rel, s.Vel), FaceTravelSpeed, 1f, caps.BrakeAccel));
            return new FlightCommand { DesiredVel = rel.Normalized * speed, DesiredRot = s.Rot };
        }

        /// <summary>Fly to a point and stop <paramref name="stopDistance"/> short of it.</summary>
        public static FlightCommand GoTo(ShipState s, ShipCaps caps, Vec3 target, float stopDistance, float cruise)
        {
            var rel = target - s.Pos;
            var speed = Steering.ApproachSpeed(Ahead(rel, s.Vel), Math.Min(cruise, caps.MaxSpeed), stopDistance, caps.BrakeAccel);
            var dir = rel.Normalized;
            var vel = NoseFirst(s, dir * speed);
            return new FlightCommand { DesiredVel = vel, DesiredRot = Face(s, speed > FaceTravelSpeed ? dir : rel, Vec3.Up) };
        }

        /// <summary>
        /// Tail a leader at <paramref name="distance"/>: keep the current bearing to it (no swinging round
        /// behind it), match its velocity, close or open the gap with a speed we can still brake from.
        /// When the leader stops, its velocity term goes to zero and the ship settles at the gap.
        /// </summary>
        public static FlightCommand Follow(ShipState s, ShipCaps caps, Vec3 leaderPos, Vec3 leaderVel,
            float distance, float cruise, float deadband = 2f) =>
            Follow(s, caps, leaderPos, leaderVel, Vec3.Up, distance, cruise, deadband);

        /// <summary>As above, rolling to match <paramref name="leaderUp"/> (the leader's own up vector).</summary>
        public static FlightCommand Follow(ShipState s, ShipCaps caps, Vec3 leaderPos, Vec3 leaderVel, Vec3 leaderUp,
            float distance, float cruise, float deadband = 2f)
        {
            var fromLeader = s.Pos - leaderPos;
            var bearing = fromLeader.Length > 1e-3f ? fromLeader.Normalized : -s.Rot.Forward;
            var anchor = leaderPos + bearing * distance;
            var rel = anchor - s.Pos;
            var closing = Steering.ApproachSpeed(Ahead(rel, s.Vel - leaderVel), Math.Min(cruise, caps.MaxSpeed), deadband, caps.BrakeAccel);
            var dir = rel.Normalized;
            var vel = leaderVel + NoseFirst(s, dir * closing);   // match the leader now; extra speed nose-first
            var face = vel.Length > FaceTravelSpeed ? vel : leaderPos - s.Pos;
            return new FlightCommand { DesiredVel = vel, DesiredRot = Face(s, face, leaderUp) };
        }

        /// <summary>
        /// Distance left once the velocity loop has caught up (it lags by about
        /// <see cref="FlightController.VelocityTau"/>): plan the brake from there, or the ship overshoots.
        /// </summary>
        static float Ahead(Vec3 rel, Vec3 relVel)
        {
            var closingNow = Vec3.Dot(relVel, rel.Normalized);
            return rel.Length - Math.Max(0f, closingNow) * FlightController.VelocityTau;
        }

        /// <summary>
        /// Ships go fast only along their nose (in-game: a CV used its side thrusters at full and slid 40 m/s
        /// sideways while turning). The part of <paramref name="v"/> along the nose passes (forward only); the
        /// rest is capped at strafe speed (<see cref="FaceTravelSpeed"/>). While the ship turns, the fast part
        /// follows the nose, so it flies an arc instead of crabbing; target behind → it turns almost in place.
        /// Only shapes the wanted velocity: braking still uses every thruster.
        /// </summary>
        /// <summary>
        /// Hold a standoff from a target like <see cref="Follow"/>, but keep the NOSE ON THE TARGET (forward guns) and
        /// move by strafing; roll stays as it is. For pirates: Follow faces the travel direction, so they would fly alongside
        /// the trader pointing nowhere.
        /// </summary>
        /// <param name="orbit">Sideways speed (m/s) around the target once near the standoff; sign = direction. 0 = park at
        /// the standoff (a parked attacker is an easy target and, against a still player, never brings fixed guns round).</param>
        /// <param name="orbitAxis">The axis to circle round (default world up = a level orbit). Tilt it over time and the
        /// attacker passes above and below the target (belly turrets, upward guns get their turn).</param>
        public static FlightCommand Attack(ShipState s, ShipCaps caps, Vec3 targetPos, Vec3 targetVel, float standoff, float cruise, float deadband = 5f, float orbit = 0f, Vec3? orbitAxis = null)
        {
            var fromTarget = s.Pos - targetPos;
            var bearing = fromTarget.Length > 1e-3f ? fromTarget.Normalized : -s.Rot.Forward;
            var rel = targetPos + bearing * standoff - s.Pos;
            var closing = Steering.ApproachSpeed(Ahead(rel, s.Vel - targetVel), Math.Min(cruise, caps.MaxSpeed), deadband, caps.BrakeAccel);
            var vel = targetVel + rel.Normalized * closing;
            if (orbit != 0f)
            {   // circle the target (level, round world up; the ship's own up when straight above/below), fading in from 2× the
                // standoff; the radial part above keeps the distance, Face keeps the nose on it
                var want = orbitAxis ?? Vec3.Up;
                var axis = Math.Abs(Vec3.Dot(bearing, want.Normalized)) < 0.9f ? want : s.Rot.Up;
                var tangent = Vec3.Cross(axis, bearing);
                var near = Math.Max(0f, Math.Min(1f, 2f - fromTarget.Length / Math.Max(1f, standoff)));
                if (tangent.Length > 1e-3f) vel = vel + tangent.Normalized * (orbit * near);
            }
            return new FlightCommand { DesiredVel = vel, DesiredRot = Face(s, targetPos - s.Pos, s.Rot.Up) };
        }

        static Vec3 NoseFirst(ShipState s, Vec3 v)
        {
            var nose = s.Rot.Forward;
            var along = Vec3.Dot(v, nose);
            var perp = v - nose * along;
            // speed along the nose only once it is roughly lined up, or a still-turning ship
            // turns its fresh momentum into sideways drift
            var aligned = Vec3.Dot(v.Normalized, nose);
            var ramp = Math.Max(0f, Math.Min(1f, (aligned - AlignStart) / (1f - AlignStart)));
            // backing up is allowed at strafe speed (too close to a leader we face: back off, don't park)
            var alongSpeed = along >= 0 ? along * ramp : Math.Max(along, -FaceTravelSpeed);
            return nose * alongSpeed + perp.ClampLength(FaceTravelSpeed);
        }

        /// <summary>cos 45°: no speed along the nose until the wanted direction is within 45° of it.</summary>
        const float AlignStart = 0.7071f;

        /// <summary>
        /// Face <paramref name="dir"/> with up as close to <paramref name="up"/> as possible. Looking (nearly) along
        /// the up reference leaves roll undefined: keep the ship's own up then, so it doesn't spin.
        /// </summary>
        /// <summary>Turn to look along <paramref name="dir"/>, keeping the ship's own up (an aimed hold: a salvage tug's beam).</summary>
        public static Quat Facing(ShipState s, Vec3 dir) => Face(s, dir, s.Rot.Up);

        static Quat Face(ShipState s, Vec3 dir, Vec3 up)
        {
            if (dir.Length < 1e-3f) return s.Rot;
            var d = dir.Normalized;
            var u = up.Length > 1e-3f && Math.Abs(Vec3.Dot(d, up.Normalized)) < 0.95f ? up : s.Rot.Up;
            return Quat.LookRotation(d, u);
        }
    }
}
