using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using LivingSystems.Core.Flight;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// Obstacle avoidance: the ship flown as a tube along its path, other structures as solid boxes (the shape doesn't matter
    /// for keeping clear). A blocked line gets a waypoint beside the obstacle; stuck → back off and re-path; boxed in → warp out.
    /// </summary>
    sealed partial class FlightDriver
    {
        sealed class DetourState { public Vector3 Wp; public bool Has; public double NextCheck; public float Radius = -1; }
        readonly Dictionary<int, DetourState> detours = new Dictionary<int, DetourState>();
        /// <summary>Closer than this to the goal, no detours (parking / holding is the hold behaviour's job).</summary>
        const float DetourNear = 60f, DetourMargin = 10f;
        /// <summary>Extra clearance on top of the ship's own radius (a block layer plus slack).</summary>
        const float DetourPad = 3f;
        const double DetourEvery = 1.0;
        public bool AvoidOn = true;

        /// <summary>
        /// The waypoint to fly to instead of <paramref name="goal"/> when another structure sits across the straight line,
        /// else null. Re-checked once a second; a reached waypoint is dropped and the line checked again from there.
        /// </summary>
        Vector3? Detoured(IEntity e, Vector3 here, Vector3 goal, double now)
        {
            if (!AvoidOn || (goal - here).magnitude < DetourNear) { detours.Remove(e.Id); return null; }
            if (!detours.TryGetValue(e.Id, out var d)) detours[e.Id] = d = new DetourState();
            if (d.Has && (d.Wp - here).magnitude < Math.Max(20f, d.Radius)) { d.Has = false; d.NextCheck = 0; }   // reached: look again
            if (now < d.NextCheck) return d.Has ? d.Wp : (Vector3?)null;
            d.NextCheck = now + DetourEvery;
            try
            {
                if (d.Radius < 0 && int.TryParse(S(() => e.Structure.BlockCount), out var nb) && nb >= 20)
                {   // the ship's own size (its box), once its blocks are in — a fresh spawn reads as 1 cell: that made a 1 m tube
                    var own = BoxModel(e.Structure.MinPos, e.Structure.MaxPos);
                    var pose0 = PoseOf(e.Structure);
                    d.Radius = new Obstacle(own.Shell.Select(c => pose0.World(c)).ToList(), pose0.Size * 2).Radius;
                    log($"FLY {e.Id} '{e.Name}': tube {d.Radius:0} m");
                }
                var radius = (d.Radius > 0 ? d.Radius : 20f) + DetourPad;
                var span = (goal - here).magnitude;
                var obs = new List<Obstacle>();
                foreach (var o in playfield.Entities.Values.ToList())
                {
                    if (o == null || o.Id == e.Id || o.Structure == null || IsTowedBy(o.Id, e.Id)) continue;
                    if (o.Type != EntityType.CV && o.Type != EntityType.SV && o.Type != EntityType.HV && o.Type != EntityType.BA) continue;
                    // only what could be near the line: within the path's length of both ends
                    if ((o.Position - here).magnitude > span + 400f || (o.Position - goal).magnitude > span + 400f) continue;
                    // a solid box (its bounds, turned with it): the shape doesn't matter for keeping clear, and no gaps or
                    // pockets to snag on; no block scan either
                    var pose = PoseOf(o.Structure);
                    var box = BoxModel(o.Structure.MinPos, o.Structure.MaxPos);
                    if (box.Shell.Count == 0) continue;
                    obs.Add(new Obstacle(box.Shell.Select(c => pose.World(c)).ToList(), pose.Size * 2));   // lattice every 4 cells: each point covers ±2
                }
                var up = V(e.Rotation * Vector3.up);
                var w = Avoid.Detour(V(here), V(goal), radius, obs, up, DetourMargin, ends: radius + 15f);
                var had = d.Has;
                d.Has = w.HasValue;
                if (w.HasValue)
                {
                    d.Wp = U(w.Value);
                    if (!had) log($"FLY {e.Id} '{e.Name}': path blocked (tube {radius:0} m, {obs.Count} structures near) — detour via ({d.Wp.x:0}, {d.Wp.y:0}, {d.Wp.z:0})");
                }
                else if (had) log($"FLY {e.Id} '{e.Name}': path clear again");
            }
            catch (Exception ex) { log($"FLY {e.Id} detour check threw {ex.Message}"); d.Has = false; d.NextCheck = now + 10; }
            return d.Has ? d.Wp : (Vector3?)null;
        }

        /// <summary>
        /// Stuck on the way somewhere far (wedged on a hull, boxed in): no 5 m of progress in <see cref="StuckAfter"/> s → back
        /// off <see cref="BackOff"/> m away from the nearest structure and re-path; no 20 m of progress in
        /// <see cref="TrappedAfter"/> s → it warps out (leaves live; the sim carries on from there).
        /// </summary>
        sealed class StuckState { public float Best = float.MaxValue; public double BestAt, Since; public float Long = float.MaxValue; public double LongAt; public Vector3 BackTo; public double BackUntil; }
        readonly Dictionary<int, StuckState> stuck = new Dictionary<int, StuckState>();
        const double StuckAfter = 5, BackFor = 8, TrappedAfter = 120;
        const float BackOff = 30f;

        Vector3? Unstick(IEntity e, Vector3 here, Vector3 goal, double now, out bool jumped)
        {
            jumped = false;
            var dist = (goal - here).magnitude;
            if (dist < DetourNear) { stuck.Remove(e.Id); return null; }
            if (!stuck.TryGetValue(e.Id, out var k)) { stuck[e.Id] = k = new StuckState { BestAt = now, LongAt = now, Since = now }; }
            if (dist < k.Best - 5f) { k.Best = dist; k.BestAt = now; }
            if (dist < k.Long - 20f) { k.Long = dist; k.LongAt = now; }
            if (now - k.LongAt > TrappedAfter)
            {
                log($"FLY {e.Id} '{e.Name}': no headway in {TrappedAfter:0} s ({dist:0} m to go) — boxed in, warping out");
                SayNear(e, 3000f, "We're boxed in — spooling the drive, we're out of here.");
                Event($"{N(e)} warped out (boxed in)");
                stuck.Remove(e.Id); detours.Remove(e.Id);
                JumpOut(e, now); jumped = true;
                return null;
            }
            if (now < k.BackUntil && (k.BackTo - here).magnitude > 8f) return k.BackTo;
            if (now - k.BestAt < StuckAfter) return null;
            var aimAt = detours.TryGetValue(e.Id, out var dd0) && dd0.Has ? dd0.Wp : goal;
            if (Vector3.Angle(e.Rotation * Vector3.forward, aimAt - here) > 30f) { k.BestAt = now; return null; }   // still turning round, not stuck
            // stuck: back away from whatever is closest (structure centres), a little up, then look again
            var away = -(e.Rotation * Vector3.forward);
            var best = float.MaxValue;
            foreach (var o in playfield.Entities.Values.ToList())
            {
                if (o == null || o.Id == e.Id || o.Structure == null || IsTowedBy(o.Id, e.Id)) continue;
                var dd = (o.Position - here).magnitude;
                if (dd < best && dd > 1f) { best = dd; away = (here - o.Position).normalized; }
            }
            k.BackTo = here + (away + (e.Rotation * Vector3.up) * 0.3f).normalized * BackOff;
            k.BackUntil = now + BackFor; k.BestAt = now; k.Best = dist;
            detours.Remove(e.Id);   // re-path from wherever the back-off leaves it
            log($"FLY {e.Id} '{e.Name}': stuck ({dist:0} m to go) — backing off {BackOff:0} m");
            return k.BackTo;
        }

        /// <summary>The surface of a bounds box, every 4th cell: the stand-in for a structure too big to scan (the tube covers the gaps).</summary>
        static VoxelModel BoxModel(VectorInt3 mn, VectorInt3 mx)
        {
            var m = new VoxelModel();
            IEnumerable<int> Steps(int a, int b) { for (var v = a; v < b; v += 4) yield return v; yield return b; }   // always the far face too
            foreach (var x in Steps(mn.x, mx.x)) foreach (var y in Steps(mn.y, mx.y)) foreach (var z in Steps(mn.z, mx.z))
                if (x == mn.x || y == mn.y || z == mn.z || x == mx.x || y == mx.y || z == mx.z) m.Add(x, y, z, "box");
            m.Shell = new HashSet<Cell>(m.Filled.Keys);
            return m;
        }

        /// <summary>A structure's blocks as a model (y without the +128), its shell built; block names only if asked. Null if unreadable.</summary>
        public VoxelModel ModelOf(IStructure st, bool names)
        {
            var map = new Dictionary<int, string>();
            if (names) try { foreach (var kv in api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()) map[kv.Value] = kv.Key; } catch { }
            var m = new VoxelModel();
            try
            {
                VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y; y <= mx.y; y++) for (var z = mn.z; z <= mx.z; z++)
                {
                    var b = st.GetBlock(x, y + GridY, z);
                    if (b == null) continue;
                    b.Get(out var t, out _, out _, out _);
                    if (t != 0) m.Add(x, y, z, map.TryGetValue(t, out var n) ? n : "?");
                }
            }
            catch { return null; }
            m.BuildShell();
            return m;
        }

        /// <summary>Block grid y = MinPos/MaxPos y + this.</summary>
        public const int GridY = 128;

        /// <summary>Where a structure's grid is right now, from the game's own StructToGlobalPos (rotation + block size in it).</summary>
        public static GridPose PoseOf(IStructure st)
        {
            var o = st.StructToGlobalPos(new VectorInt3(0, GridY, 0));
            return new GridPose { Origin = V(o), StepX = V(st.StructToGlobalPos(new VectorInt3(1, GridY, 0)) - o),
                                  StepY = V(st.StructToGlobalPos(new VectorInt3(0, GridY + 1, 0)) - o), StepZ = V(st.StructToGlobalPos(new VectorInt3(0, GridY, 1)) - o) };
        }
    }
}
