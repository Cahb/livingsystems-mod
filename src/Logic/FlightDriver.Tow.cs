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
    /// Fake tow: a coreless hull held at a fixed spot in front of a tug, placed every frame — thrust can't move a dead hull,
    /// but setting its position / rotation does. Real docking only takes a piloted ship's landing gear (checked 2026-11-18).
    /// </summary>
    sealed partial class FlightDriver
    {
        sealed class Tow { public int Tug; public Vector3 LocalPos; public Quaternion LocalRot; }
        readonly Dictionary<int, Tow> tows = new Dictionary<int, Tow>();
        /// <summary>How fast a towed hull catches up with its spot (m/s cap) and eases into it (per second).</summary>
        const float TowMaxSpeed = 40f, TowEase = 3f, TowTurnEase = 1.5f;

        /// <summary>Hold <paramref name="hullId"/> <paramref name="ahead"/> m in front of the tug's nose, at the angle it has to
        /// the tug now.</summary>
        public bool StartTow(int hullId, int tugId, float ahead)
        {
            if (playfield?.Entities == null || !playfield.Entities.TryGetValue(hullId, out var h) || !playfield.Entities.TryGetValue(tugId, out var t) || h == null || t == null) return false;
            tows[hullId] = new Tow { Tug = tugId, LocalPos = Vector3.forward * ahead, LocalRot = Quaternion.Inverse(t.Rotation) * h.Rotation };
            detours.Remove(tugId);
            log($"FLY tow: {hullId} '{h.Name}' on {tugId}'s beam, {ahead:0} m ahead");
            return true;
        }

        public void StopTow(int hullId) { if (tows.Remove(hullId)) log($"FLY tow: {hullId} let go"); }
        public bool IsTowedBy(int hullId, int tugId) => tows.TryGetValue(hullId, out var w) && w.Tug == tugId;

        void StepTows(float dt)
        {
            foreach (var kv in tows.ToList())
            {
                if (!playfield.Entities.TryGetValue(kv.Key, out var h) || !playfield.Entities.TryGetValue(kv.Value.Tug, out var t) || h == null || t == null)
                { tows.Remove(kv.Key); log($"FLY tow: {kv.Key} dropped (hull or tug gone)"); continue; }
                var spot = t.Position + t.Rotation * kv.Value.LocalPos;
                var to = spot - h.Position;
                var step = to * Math.Min(1f, TowEase * dt);
                if (step.magnitude > TowMaxSpeed * dt) step = step.normalized * TowMaxSpeed * dt;
                var rot = Quaternion.Slerp(h.Rotation, t.Rotation * kv.Value.LocalRot, Math.Min(1f, TowTurnEase * dt));
                Safe(() => { h.Position = h.Position + step; h.Rotation = rot; });
            }
        }
    }
}
