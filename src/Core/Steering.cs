using System;

namespace LivingSystems.Core
{
    /// <summary>Speed profile for flying to a point and stopping there without overshoot.</summary>
    public static class Steering
    {
        /// <summary>
        /// Desired speed toward a target: cruise at <paramref name="cruise"/>, then slow so that braking at
        /// <paramref name="decel"/> m/s² ends exactly at <paramref name="stopDistance"/> from the target.
        /// </summary>
        public static float ApproachSpeed(float distance, float cruise, float stopDistance, float decel)
        {
            var remaining = distance - stopDistance;
            if (remaining <= 0f || cruise <= 0f || decel <= 0f) return 0f;
            return Math.Min(cruise, (float)Math.Sqrt(2.0 * decel * remaining));
        }

        /// <summary>
        /// Scale forward speed by how well the nose points at the target (dot of forward and direction),
        /// so a ship turns before it accelerates instead of flying sideways. Never negative.
        /// </summary>
        public static float AlignmentFactor(float dot) => dot <= 0f ? 0f : dot * dot;
    }
}
