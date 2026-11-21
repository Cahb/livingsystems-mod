using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Flight;

namespace LivingSystems.Core
{
    /// <summary>A block cell in a structure's own grid.</summary>
    public struct Cell : IEquatable<Cell>
    {
        public int X, Y, Z;
        public Cell(int x, int y, int z) { X = x; Y = y; Z = z; }
        public bool Equals(Cell o) => X == o.X && Y == o.Y && Z == o.Z;
        public override bool Equals(object o) => o is Cell c && Equals(c);
        public override int GetHashCode() => unchecked((X * 73856093) ^ (Y * 19349663) ^ (Z * 83492791));
        public override string ToString() => $"{X},{Y},{Z}";
    }

    /// <summary>
    /// Where a structure's grid sits in the world: cell (0,0,0)'s centre and one cell step along each grid axis (the
    /// game's own StructToGlobalPos gives these — rotation and block size are in them).
    /// </summary>
    public struct GridPose
    {
        public Vec3 Origin, StepX, StepY, StepZ;
        public Vec3 World(Cell c) => Origin + StepX * c.X + StepY * c.Y + StepZ * c.Z;
        /// <summary>Block edge length, metres.</summary>
        public float Size => StepX.Length;
    }

    /// <summary>
    /// A structure as blocks: every filled cell (with its block name), and the outer shell — filled cells that touch empty
    /// space reachable from outside the bounds. Rooms and corridors inside don't count: nothing from outside can reach them.
    /// </summary>
    public sealed class VoxelModel
    {
        public readonly Dictionary<Cell, string> Filled = new Dictionary<Cell, string>();
        public HashSet<Cell> Shell = new HashSet<Cell>();

        static readonly Cell[] Nb = { new Cell(1, 0, 0), new Cell(-1, 0, 0), new Cell(0, 1, 0), new Cell(0, -1, 0), new Cell(0, 0, 1), new Cell(0, 0, -1) };

        public void Add(int x, int y, int z, string name) => Filled[new Cell(x, y, z)] = name ?? "?";

        /// <summary>Flood the empty space from a corner of the padded bounds; filled cells next to it are the shell.</summary>
        public void BuildShell()
        {
            Shell = new HashSet<Cell>();
            if (Filled.Count == 0) return;
            int x0 = int.MaxValue, y0 = int.MaxValue, z0 = int.MaxValue, x1 = int.MinValue, y1 = int.MinValue, z1 = int.MinValue;
            foreach (var c in Filled.Keys)
            {
                x0 = Math.Min(x0, c.X); y0 = Math.Min(y0, c.Y); z0 = Math.Min(z0, c.Z);
                x1 = Math.Max(x1, c.X); y1 = Math.Max(y1, c.Y); z1 = Math.Max(z1, c.Z);
            }
            x0--; y0--; z0--; x1++; y1++; z1++;
            var outside = new HashSet<Cell>();
            var todo = new Queue<Cell>();
            var start = new Cell(x0, y0, z0);
            outside.Add(start); todo.Enqueue(start);
            while (todo.Count > 0)
            {
                var c = todo.Dequeue();
                foreach (var d in Nb)
                {
                    var n = new Cell(c.X + d.X, c.Y + d.Y, c.Z + d.Z);
                    if (n.X < x0 || n.Y < y0 || n.Z < z0 || n.X > x1 || n.Y > y1 || n.Z > z1) continue;
                    if (Filled.ContainsKey(n) || !outside.Add(n)) continue;
                    todo.Enqueue(n);
                }
            }
            foreach (var c in Filled.Keys)
                foreach (var d in Nb)
                    if (outside.Contains(new Cell(c.X + d.X, c.Y + d.Y, c.Z + d.Z))) { Shell.Add(c); break; }
        }

        /// <summary>
        /// The closest pair of shell blocks between two placed models: surface gap in metres (centre distance less half of
        /// each block), and which cells. Brute force — a few hundred shell cells each is fine for a once-a-second check.
        /// </summary>
        public static float Gap(VoxelModel a, GridPose pa, VoxelModel b, GridPose pb, out Cell ca, out Cell cb)
        {
            ca = default; cb = default;
            if (a.Shell.Count == 0 || b.Shell.Count == 0) return float.MaxValue;
            var wb = b.Shell.Select(c => new KeyValuePair<Cell, Vec3>(c, pb.World(c))).ToList();
            var best = float.MaxValue;
            foreach (var c in a.Shell)
            {
                var p = pa.World(c);
                foreach (var kv in wb)
                {
                    var d = (kv.Value - p).Length;
                    if (d < best) { best = d; ca = c; cb = kv.Key; }
                }
            }
            return Math.Max(0, best - (pa.Size + pb.Size) / 2);
        }

        /// <summary>The shell block of a placed model closest to a world point, and the gap to its surface.</summary>
        public float Gap(GridPose pose, Vec3 point, out Cell cell)
        {
            cell = default;
            var best = float.MaxValue;
            foreach (var c in Shell)
            {
                var d = (pose.World(c) - point).Length;
                if (d < best) { best = d; cell = c; }
            }
            return Shell.Count == 0 ? float.MaxValue : Math.Max(0, best - pose.Size / 2);
        }
    }
}
