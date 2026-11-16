using System;

namespace LivingSystems.Core
{
    /// <summary>
    /// Where to put spike ships relative to a player, without touching Unity's native math.
    /// Uses Unity's conventions: Y up, Z forward, Euler angles in degrees, yaw about Y.
    /// </summary>
    public static class SpawnGeometry
    {
        public struct V3
        {
            public float X, Y, Z;
            public V3(float x, float y, float z) { X = x; Y = y; Z = z; }
            public override string ToString() => $"({X:0},{Y:0},{Z:0})";
        }

        static double Rad(float deg) => deg * Math.PI / 180.0;

        /// <summary>Horizontal forward vector for a yaw angle (pitch ignored, so ships spawn level).</summary>
        public static V3 FlatForward(float yawDeg) =>
            new V3((float)Math.Sin(Rad(yawDeg)), 0f, (float)Math.Cos(Rad(yawDeg)));

        /// <summary>
        /// Ship i of a batch: <paramref name="distance"/> ahead of the player, each further one 60 m beyond,
        /// facing 90° to the player's right so it crosses the player's view instead of flying away.
        /// </summary>
        /// <summary>Plain struct, not a tuple: the game's Mono runtime has no System.ValueTuple.</summary>
        public struct Placement { public V3 Pos, Rot; }

        public static Placement Place(V3 playerPos, V3 playerRot, float distance, int i)
        {
            var f = FlatForward(playerRot.Y);
            var d = distance + 60f * i;
            var pos = new V3(playerPos.X + f.X * d, playerPos.Y, playerPos.Z + f.Z * d);
            var yaw = (playerRot.Y + 90f) % 360f;
            return new Placement { Pos = pos, Rot = new V3(0f, yaw, 0f) };
        }
    }
}
