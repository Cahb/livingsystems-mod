using System;
using System.Collections.Generic;

namespace LivingSystems.Core.Flight
{
    /// <summary>A structure in the way, as world points (its outer-shell block centres) plus a bounding ball for a cheap first test.</summary>
    public sealed class Obstacle
    {
        public readonly List<Vec3> Points;
        public readonly Vec3 Centre;
        public readonly float Radius;
        /// <summary>Half a block: a point stands for a cube this far round its centre.</summary>
        public readonly float Half;

        public Obstacle(List<Vec3> points, float half)
        {
            Points = points; Half = half;
            var sum = Vec3.Zero;
            foreach (var p in points) sum += p;
            Centre = points.Count > 0 ? sum / points.Count : Vec3.Zero;
            var r = 0f;
            foreach (var p in points) r = Math.Max(r, (p - Centre).Length);
            Radius = r + half;
        }
    }

    /// <summary>
    /// Path check for a ship flown as a tube: the straight line from here to the goal, as wide as the ship's radius plus a
    /// margin. A block of another structure inside the tube blocks it; the way round is one waypoint beside the obstacle.
    /// Rough on purpose — no threading needles (landing on gear will need real geometry).
    /// </summary>
    public static class Avoid
    {
        /// <summary>Distance from p to the segment a→b, and where along it (0…1).</summary>
        public static float SegDist(Vec3 a, Vec3 b, Vec3 p, out float t)
        {
            var ab = b - a; var len2 = Vec3.Dot(ab, ab);
            t = len2 < 1e-6f ? 0f : Math.Max(0f, Math.Min(1f, Vec3.Dot(p - a, ab) / len2));
            return (a + ab * t - p).Length;
        }

        /// <summary>
        /// Is a→b blocked for a tube of radius <paramref name="r"/>? Blocks within <paramref name="ends"/> metres of either end
        /// don't count (the ship is already next to a hull, or parks beside one). The obstacles that block, if any.
        /// </summary>
        public static List<Obstacle> Blocking(Vec3 a, Vec3 b, float r, IList<Obstacle> obs, float ends)
        {
            var hit = new List<Obstacle>();
            var len = (b - a).Length;
            if (len < 1e-3f) return hit;
            foreach (var o in obs)
            {
                if (SegDist(a, b, o.Centre, out _) > o.Radius + r) continue;   // the whole ball is clear
                foreach (var p in o.Points)
                {
                    var d = SegDist(a, b, p, out var t);
                    if (d > r + o.Half) continue;
                    var along = t * len;
                    if (along < ends || along > len - ends) continue;
                    hit.Add(o); break;
                }
            }
            return hit;
        }

        /// <summary>
        /// A waypoint that takes the tube round what blocks a→b, or null if the line is clear. Tries 8 directions square
        /// to the path (up first), each pushed out past the blocking obstacles' extent that way; keeps the shortest
        /// detour whose two legs are both clear, else the shortest one (the next check goes on from there).
        /// </summary>
        public static Vec3? Detour(Vec3 a, Vec3 b, float r, IList<Obstacle> obs, Vec3 up, float margin = 10f, float ends = 0f)
        {
            var block = Blocking(a, b, r, obs, ends);
            if (block.Count == 0) return null;
            var d = (b - a).Normalized;
            var u = (up - d * Vec3.Dot(up, d)).Normalized;
            if (u.Length < 0.5f) u = Math.Abs(d.Y) < 0.9f ? (Vec3.Up - d * d.Y).Normalized : (Vec3.Right - d * d.X).Normalized;
            var v = Vec3.Cross(d, u).Normalized;
            // where along the path the blockers sit: the waypoint goes abreast of their middle
            float tSum = 0; var n = 0;
            foreach (var o in block) { SegDist(a, b, o.Centre, out var tc); tSum += tc; n++; }
            var mid = a + (b - a) * (tSum / n);

            Vec3? best = null, fallback = null; float bestLen = float.MaxValue, fbLen = float.MaxValue;
            const float s = 0.70710678f;
            var dirs = new[] { u, -u, v, -v, (u + v) * s, (u - v) * s, (-u + v) * s, (-u - v) * s };
            foreach (var k in dirs)
            {
                var reach = 0f;   // how far out this way the blockers go, measured from the path
                foreach (var o in block) foreach (var p in o.Points) reach = Math.Max(reach, Vec3.Dot(p - mid, k) + o.Half);
                var w = mid + k * (reach + r + margin);
                var total = (w - a).Length + (b - w).Length;
                if (total < fbLen) { fbLen = total; fallback = w; }
                if (total >= bestLen) continue;
                if (Blocking(a, w, r, obs, ends).Count == 0 && Blocking(w, b, r, obs, ends).Count == 0) { bestLen = total; best = w; }
            }
            return best ?? fallback;
        }
    }
}
