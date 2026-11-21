using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// Salvage tugs, live: the cut cycle (park by the voxel models, aim the mining beam, cut a slab, beam off) for the sim's
    /// tugs (<see cref="TugTick"/>) and the spike commands (<c>/ls fly prefab salvage</c>, <c>/ls fly tug …</c>).
    /// </summary>
    sealed partial class SimLive
    {
        /// <summary>One tug at work on one target. The cut order is the wreck's blocks sorted along its longest axis (one end
        /// to the other): the same order for the same hull, so a share cut offline is the same first slabs live.</summary>
        sealed class Cutter
        {
            public int Tug, Wreck;
            /// <summary>The sim's wreck id of the target (live tugs; "" = the spike).</summary>
            public string WreckId = "";
            public bool Ready, HasBeam, PerBlock, StandbySaid, FightSaid, FarSaid;
            /// <summary>The target's crates are aboard (done at the first cut, in position — never from afar).</summary>
            public bool CratesDone;
            public VectorInt3 BeamCell;
            public Core.VoxelModel TugModel, WreckModel;
            public List<VectorInt3> Order; public int Done, Step;
            /// <summary>Share of the wreck's blocks gone before <see cref="Order"/> was made; share to cut at once (the
            /// sim's progress while nobody watched).</summary>
            public float Base, CatchUp;
            /// <summary>Back live after a long spell offline: double slabs until then (the crew's in its stride).</summary>
            public double BoostUntil;
            public bool VolumeUnknownSaid, CrowdedSaid;
            public double At = -1, CheckAt, PhaseAt, GapLoggedAt;
            public HashSet<string> Crates = new HashSet<string>();
            public int Phase; public Vector3? LastPos; public Vector3 AimAt, FaceAt, ParkAt;
            /// <summary>Its parking spot for this cut is set (fixed until the next cut).</summary>
            public bool Parked;
            /// <summary>What it carries, kept across targets: the crates' leftovers; blocks cut (scrap = half).</summary>
            public readonly Dictionary<string, int> Cargo = new Dictionary<string, int>(StringComparer.Ordinal);
            public int BlocksCut;   // hull blocks with no recipe known (→ scrap plates); ore blocks go into Cargo as ore
            /// <summary>Salvaged parts not whole yet (a window is half a steel plate): carried until they make one.</summary>
            public readonly Dictionary<string, double> Frac = new Dictionary<string, double>(StringComparer.Ordinal);
            public readonly Dictionary<string, int> CutNames = new Dictionary<string, int>(StringComparer.Ordinal);   // what was cut, by block name (log)
            /// <summary>Targets it finished (or lost) this spawn: never taken again while the traffic list catches up.</summary>
            public readonly HashSet<string> Finished = new HashSet<string>(StringComparer.Ordinal);
            public Action<string> Say = _ => { };
            /// <summary>A hired tug: credits it finds go straight to the player's balance (never into a crate slot).</summary>
            public Action<int> OnCash;
            /// <summary>The share of the target's blocks gone so far.</summary>
            public float Share => Order == null || Order.Count == 0 ? Base : Base + (1 - Base) * Done / (float)Order.Count;
            public void NewTarget(int entity, string id, float baseShare, float catchUp)
            {
                Wreck = entity; WreckId = id; Base = baseShare; CatchUp = catchUp;
                Order = null; Done = 0; Phase = 0; At = -1; WreckModel = null; Crates = new HashSet<string>(); CratesDone = false;
            }
        }

        enum CutResult { Working, Scrapped, Lost, Full }

        const float SalvageShare = 0.1f;
        /// <summary>A cut never takes more blocks than this: big hulls and asteroids take longer, not bigger bites.</summary>
        const int MaxCutBlocks = 60;
        /// <summary>The boost: a cut that got this far offline (share of the hull) cuts double slabs live for <see cref="BoostSeconds"/>.</summary>
        const float BoostAfter = 0.1f;
        const double BoostSeconds = 300;
        /// <summary>The mining beam's visible length from its block on a large-block (CV / base) hull, measured in game: ~35–40 m
        /// (a small-block SV / HV may draw it smaller — re-measure before using one). The tug parks
        /// with the beam's tip a little INTO the cut, never closer than <see cref="SalvageMinGap"/> hull to hull.</summary>
        const float BeamLength = 35f, BeamReachIn = 3f, SalvageMinGap = 6f;
        /// <summary>How much closer than its full length the cut may be and the tug still stays where it is (re-aims only).</summary>
        const float BeamSlack = 15f;
        /// <summary>A tug's top turn rate (deg/s; it eases in at 1.5× that per second²).</summary>
        const float TugTurn = 8f, TugAccel = 3f;
        /// <summary>Eden's mining-beam effects block, and its rotation code that fires along a +Z nose (read off a block the
        /// user placed and named LS_BEAM, 2026-11-17).</summary>
        const int BeamBlock = 3101, BeamRotPlusZ = 10;
        /// <summary>The tugs' hull: the spike's ship (+Z nose, so the beam's rotation code is right).</summary>
        /// <summary>The tug hull this server uses: the server owner's own <c>prefabs/LS_Tug.epb</c> if it's there, else the
        /// vanilla cargo tug (<see cref="Core.Sim.World.TugShip"/>; both processes decide the same way).</summary>
        public static string TugPrefab => TugHull.Name;
        /// <summary>Must match the sim's <c>SimConfig.TugStandby</c> (the arrival fly-in is judged by it).</summary>
        const double TugStandby = 600;

        /// <summary>The spike's tug (prefab salvage / tug commands): one at a time, owned by <see cref="probe"/>.</summary>
        Cutter spikeCut;

        // ---- the sim's tugs ----------------------------------------------------------------------------------------

        /// <summary>The Dedi made a salvage tug live: it spawns beside its target (which is live already — it only asks once
        /// the target's hull is filled) and works from the sim's share.</summary>
        void SpawnTug(string agent, string name, string prefab)
        {
            if (live.ContainsKey(agent)) return;
            if (string.IsNullOrEmpty(prefab)) prefab = TugPrefab;
            if (Pf?.Entities == null || !traffic.TryGetValue(agent, out var t) || (t.Leg != "salvage" && t.Leg != "fight")) { End(agent, "left"); return; }
            if (t.Leg == "fight")
            {   // jumped at the job: right where the fight is (the pirates spawn on it), held there
                var fp = ClearSpot(t.A, 40f);
                var fl = AddShip(new Live { Agent = agent, Kind = "tug", Dest = fp, Since = fly.Now, Prefab = prefab });
                t.Live = true;
                SpawnShip(fl, prefab, fp, (float)(rng.NextDouble() * 360), name, TraderGroup(prefab));
                log($"SIM tug {agent} '{name}' live in its fight at {fp}");
                return;
            }
            var wl = wrecks.Values.FirstOrDefault(w => w.Id == t.Other && w.Filled);
            if (wl == null || !Pf.Entities.TryGetValue(wl.Entity, out var we) || we == null) { End(agent, "left"); return; }   // target gone meanwhile: the sim retries
            var dir = Level(UnityEngine.Random.onUnitSphere);
            var pos = ClearSpot(we.Position + dir * (PfSpike.HullRadius(we) + 80f), 40f);
            // just in (the standby has barely begun): it drops out of warp out there and crawls in, in front of you
            var justIn = t.T0 - Unix > 0 && Unix - (t.T0 - TugStandby) < 120;
            var from = justIn ? ClearSpot(pos + dir * ArriveMetres, 60f) : pos;
            var tl = AddShip(new Live { Agent = agent, Kind = "tug", Dest = pos, Since = fly.Now, Prefab = prefab, Arriving = justIn });
            t.Live = true;
            SpawnShip(tl, prefab, from, Yaw(we.Position - from), name, TraderGroup(prefab));
            if (justIn) SayNear(pos, $"{name} (salvage tug) dropped out of warp — {(from - pos).magnitude / 1000f:0.0} km out, coming in.");
            log($"SIM tug {agent} '{name}' live {(justIn ? "arriving at" : "beside")} wreck {wl.Id} ({wl.Entity}), {t.Share:P0} cut by the sim");
        }

        /// <summary>A live sim tug, each second: fit the beam, take its target from the traffic list (the sim's oldest on its
        /// list), cut; a finished target goes to the Dedi ("tugcut"), which answers with the next (traffic) or "tugleave".
        /// Out of every player's bubble → back to the sim with the share actually cut.</summary>
        void TugTick(Live l, IEntity e, double now)
        {
            var c = l.Cut;
            if (c == null) l.Cut = c = new Cutter { Tug = e.Id };
            var name = StructureTag.Strip(e.Name);
            c.Say = s => SayNear(e.Position, $"{name}: {s}");
            if (!l.Steered) { l.Steered = fly.GoTo(e.Id, l.Dest, "the wrecks", 0f, l.Arriving ? 25f : 20f); return; }   // registers a moment after it appears
            if (!c.Ready)
            {
                if (now - l.Since < 5 || e.Structure == null) return;   // a frozen proxy (out of physics range) has no blocks to fit yet
                PlaceBeam(c, e); c.Ready = true; PinTug(e);
                SeedCargo(c, e);   // picked up again (a reload): what's already aboard stays aboard — the hold is rewritten from this
                if (c.Cargo.Count == 0 && traffic.TryGetValue(l.Agent, out var tt) && tt.Hold.Length > 0)
                {   // a fresh hull for a tug that was live before: its hold as the sim kept it
                    foreach (var kv in Core.Sim.World.ParseHold(tt.Hold)) c.Cargo[kv.Key] = kv.Value;
                    log($"SIM tug {l.Agent}: hold from the sim — {tt.Hold}");
                }   // sim tugs all fly the server's tug hull (a re-adopted one has no prefab name)
                log($"SIM tug {l.Agent} ready, beam {(c.HasBeam ? "fitted" : "NONE")}");
            }
            // its spot must be at its wreck: a re-adopted tug (after a reload) only knows where it sat — re-aim and fly in
            if (!l.Arriving && traffic.TryGetValue(l.Agent, out var ta) && ta.Leg == "salvage"
                && wrecks.Values.FirstOrDefault(w => w.Id == ta.Other && w.Filled) is WreckLive wa
                && Pf.Entities.TryGetValue(wa.Entity, out var wae) && wae != null
                && (l.Dest - wae.Position).magnitude > PfSpike.HullRadius(wae) + 400f)
            {
                var side = Level(l.Dest - wae.Position); if (side.sqrMagnitude < 0.01f) side = Vector3.forward;
                l.Dest = ClearSpot(wae.Position + side.normalized * (PfSpike.HullRadius(wae) + 80f), 40f);
                l.Arriving = true; l.Steered = false;
                log($"SIM tug {l.Agent}: spot re-aimed at wreck {wa.Id} ({wa.Entity}), flying in");
                return;
            }
            // out of range: the body driver, judged by its spot at the wrecks (ShipBrain.RangeFrom), share via OnBack
            if (traffic.TryGetValue(l.Agent, out var tf) && tf.Leg == "fight")
            {   // pirates on it: beam off, it sits and takes it (unarmed) — the fight's over when the sim says so
                Beam(c, e, false);
                if (!c.FightSaid) { c.FightSaid = true; c.Say("pirates on us! We're unarmed — anyone out there with guns, now's the time."); }
                return;
            }
            c.FightSaid = false;
            if (l.Arriving && (e.Position - l.Dest).magnitude > 150f) return;   // still flying in
            l.Arriving = false;
            if (c.Wreck == 0)
            {
                if (!traffic.TryGetValue(l.Agent, out var t) || t.Leg != "salvage" || t.Other.Length == 0 || c.Finished.Contains(t.Other)) return;   // waiting on the sim
                var wl = wrecks.Values.FirstOrDefault(w => w.Id == t.Other);
                if (wl == null || !wl.Filled) return;   // its hull spawns by its own rule (a player near it)
                c.NewTarget(wl.Entity, wl.Id, wl.Cut, Math.Max(wl.Cut, t.Share));
                Beam(c, e, false);
                log($"SIM tug {l.Agent} on wreck {wl.Id} ({wl.Entity}): {wl.Cut:P0} gone, the sim says {t.Share:P0}");
            }
            if (traffic.TryGetValue(l.Agent, out var tw) && tw.T0 > Unix + 1)
            {   // standing by: players get their minutes with the pile first
                if (!c.StandbySaid) { c.StandbySaid = true; c.Say($"salvage tug, standing by. We start cutting in {Math.Ceiling((tw.T0 - Unix) / 60):0} min — grab what you want before then."); }
                return;
            }
            var res = SalvageStep(c, e, now);
            var wlive = wrecks.Values.FirstOrDefault(w => w.Id == c.WreckId);
            if (wlive != null) wlive.Cut = c.Share;
            switch (res)
            {
                case CutResult.Scrapped:
                    DropWreck(c.Wreck); refills.Remove(c.Wreck);   // retired by the cut: no body message — the sim hears "tugcut"
                    fly.Send("tugcut", l.Agent, c.WreckId, "1");
                    c.Finished.Add(c.WreckId); c.Wreck = 0;
                    break;
                case CutResult.Full:
                    fly.Send("tugcut", l.Agent, c.WreckId, c.Share.ToString("0.###", CultureInfo.InvariantCulture));   // the sim sends it home
                    c.Finished.Add(c.WreckId); c.Wreck = 0;
                    break;
                case CutResult.Lost:   // claimed / gone: the wreck cleanup tells the sim; its list moves on
                    c.Finished.Add(c.WreckId); c.Wreck = 0;
                    break;
            }
        }

        /// <summary>The sim says the tug's done here (list empty or hold full): beam off, fly out, spool, jump.</summary>
        void TugLeave(string agent, string bound, bool beaten = false)
        {
            if (!live.TryGetValue(agent, out var l) || l.Kind != "tug" || Pf?.Entities == null || !Pf.Entities.TryGetValue(l.Entity, out var e) || e == null) return;
            if (l.Cut != null) Beam(l.Cut, e, false);
            var away = PlayerSpot(out var pp) ? Level(e.Position - pp) : Level(UnityEngine.Random.onUnitSphere);
            l.Departing = true; l.LegKind = "out"; l.Bound = bound; l.Steered = false;
            l.Dest = e.Position + away * DepartMetres;
            var load = l.Cut != null ? Manifest(l.Cut).Sum(kv => kv.Value) : 0;
            SayNear(e.Position, $"{StructureTag.Strip(e.Name)}: " + (beaten && load == 0 ? $"someone beat us to it — stripped bare. Nothing for us here; back to {bound}."
                                                                     : beaten ? $"someone beat us to the rest. Taking what we've got home to {bound}."
                                                                     : $"that's the lot — {load} items of scrap aboard, taking it home to {bound}."));
            log($"SIM tug {agent} leaves live for {bound}");
        }

        /// <summary>A live tug's answer to a hail: whose yard, what it's cutting, how far along, what's left.</summary>
        string TugSays(Live l)
        {
            traffic.TryGetValue(l.Agent, out var t);
            var yard = t != null && t.Station.Length > 0 ? t.Station : "the yards";
            if (l.Departing) return $"Salvage tug, done here — heading home to {(l.Bound.Length > 0 ? l.Bound : yard)} with the scrap.";
            var c = l.Cut;
            if (t != null && t.T0 > Unix + 1) return $"Salvage tug out of {yard}. Standing by — we start cutting in {Math.Ceiling((t.T0 - Unix) / 60):0} min. Anything you want off those hulks, take it now.";
            if (c == null || c.Wreck == 0 || !traffic.TryGetValue(c.WreckId, out var wt))
                return $"Salvage tug out of {yard}. Waiting on our next job here — keep clear of the beam when it's on.";
            var aboard = Manifest(c).Sum(kv => kv.Value);
            return $"Salvage tug out of {yard}. Cutting up {StructureTag.Strip(wt.Name)} — {c.Share * 100:0} % done{(aboard > 0 ? $", {aboard} items aboard" : "")}. Keep clear of the beam.";
        }

        /// <summary>A wreck spawning with part of it already cut (a tug worked on it while nobody watched): the first
        /// <paramref name="share"/> of its blocks in the cut order, gone at once (crates in them too). Returns blocks removed.</summary>
        int CutAhead(IStructure st, float share)
        {
            var order = CutOrder(st);
            if (order == null) return 0;
            var n = Mathf.Clamp(Mathf.RoundToInt(order.Count * share), 0, order.Count);
            var gone = 0;
            foreach (var v in order.Take(n)) { try { st.GetBlock(v.x, v.y, v.z)?.Set(0, null, null, null); gone++; } catch { } }
            return gone;
        }

        /// <summary>Every block of a structure (grid y +128), sorted along its longest axis: the order tugs cut in.</summary>
        List<VectorInt3> CutOrder(IStructure st, Vector3? from = null)
        {
            var cut = new List<VectorInt3>();
            VectorInt3 mn, mx;
            try
            {
                mn = st.MinPos; mx = st.MaxPos;
                for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y + GridY; y <= mx.y + GridY; y++) for (var z = mn.z; z <= mx.z; z++)
                {
                    var b = st.GetBlock(x, y, z);
                    if (b == null) continue;
                    b.Get(out var type, out _, out _, out _);
                    if (type != 0) cut.Add(new VectorInt3(x, y, z));
                }
            }
            catch (Exception ex) { log("SIM salvage: block scan threw " + ex.Message); return null; }
            if (from.HasValue && cut.Count > 0)
            {   // peel from the side facing the tug: 1-block layers, frontmost first, each layer from its middle outward — the
                // beam always lands on the face you see (a slice sweep along the long axis cut a solid rock from inside)
                var pose = PoseOf(st);
                var mid = Vector3.zero; foreach (var v in cut) mid += new Vector3(v.x, v.y, v.z); mid /= cut.Count;
                var midW = pose.World(new Core.Cell((int)mid.x, (int)mid.y - GridY, (int)mid.z));
                var w = from.Value - new Vector3(midW.X, midW.Y, midW.Z);
                float G(Core.Flight.Vec3 step) { var l = step.Length; return l > 1e-4f ? (w.x * step.X + w.y * step.Y + w.z * step.Z) / l : 0f; }
                var d = new Vector3(G(pose.StepX), G(pose.StepY), G(pose.StepZ)).normalized;   // toward the tug, in grid axes
                if (d.sqrMagnitude > 0.5f)
                    return cut.OrderBy(v => -Mathf.Round(Vector3.Dot(new Vector3(v.x, v.y, v.z), d)))
                              .ThenBy(v => { var r = new Vector3(v.x, v.y, v.z) - mid; return (r - Vector3.Dot(r, d) * d).sqrMagnitude; })
                              .ToList();
            }
            int dx = mx.x - mn.x, dy = mx.y - mn.y, dz = mx.z - mn.z;
            return dx >= dy && dx >= dz ? cut.OrderBy(v => v.x).ThenBy(v => v.y).ThenBy(v => v.z).ToList() : dz >= dy ? cut.OrderBy(v => v.z).ThenBy(v => v.y).ThenBy(v => v.x).ToList() : cut.OrderBy(v => v.y).ThenBy(v => v.x).ThenBy(v => v.z).ToList();
        }

        // ---- the cut cycle -------------------------------------------------------------------------------------------

        /// <summary>
        /// One step of the cycle: 1 close in + aim → 2 beam on, cut a slab (10 %, or a block), beam off → 3 cool down → 1 …
        /// Crates' leftovers go aboard; the whole cargo is written into the tug's crates after each slab (no room = Full,
        /// before cutting what it can't carry). Never cuts a cored structure (Lost).
        /// </summary>
        /// <summary>After the 45 s wait the beam fires anyway only this close to the parking spot (m): slightly off is fine, afar never.</summary>
        const float FireSlack = 25f;

        CutResult SalvageStep(Cutter c, IEntity tug, double now)
        {
            if (c.Wreck == 0 || tug?.Structure == null) return CutResult.Working;
            var tname = StructureTag.Strip(tug.Name);
            if (!Pf.Entities.TryGetValue(c.Wreck, out var wr) || wr?.Structure == null || Cored(wr))
            {   // gone (someone salvaged it first) or cored meanwhile (claimed): drop it, keep the cargo
                c.Say(wr?.Structure != null ? $"{StructureTag.Strip(wr.Name)} has a core in it now — someone's claimed it. Leaving it be." : "someone beat us to it — that one's gone.");
                Beam(c, tug, false); c.Wreck = 0;
                return CutResult.Lost;
            }
            var st = wr.Structure;
            if (c.Order == null)
            {
                var order = CutOrder(st, fly.CentreOf(tug));
                if (order == null) { c.Say("couldn't read the wreck's blocks."); c.Wreck = 0; return CutResult.Lost; }
                c.Order = order; c.At = now;
                c.Step = c.PerBlock ? 1 : Math.Min(MaxCutBlocks, Math.Max(1, (int)Math.Ceiling(order.Count * SalvageShare / Math.Max(0.1f, 1 - c.Base))));   // slabs of 10 % of the hull, at most MaxCutBlocks (a 34k-block asteroid isn't 10 cuts)
                c.WreckModel = ModelOf(st);   // close in: hull to hull (tug model ↔ wreck model), re-checked before each cut
                c.Crates = new HashSet<string>();
                try { var cl = st.GetDevices(DeviceTypeName.Container); for (var i = 0; cl != null && i < cl.Count; i++) { var cp = cl.GetAt(i); c.Crates.Add($"{cp.x},{cp.y},{cp.z}"); } } catch { }
                if (c.CatchUp > c.Base + 0.01f)
                {   // the sim's progress while nobody watched: those slabs are gone already (its crates first, as at a cut)
                    if (!c.CratesDone && !EmptyCrates(c, st, tug, wr)) { c.Order = null; Beam(c, tug, false); return CutResult.Full; }
                    c.CratesDone = true;
                    var n = Mathf.Clamp(Mathf.RoundToInt(order.Count * (c.CatchUp - c.Base) / Math.Max(0.01f, 1 - c.Base)), 0, order.Count);
                    var cut = 0; var full = false;
                    while (cut < n)
                    {   // slab by slab, as it would have: no slab the hold can't take
                        if (VolumeFull(c, st, tug, wr)) { full = true; break; }
                        var k = Math.Min(n - cut, Math.Max(1, c.Step));
                        CutBlocks(c, st, k); cut += k;
                    }
                    c.WreckModel?.BuildShell();
                    var aboard = Manifest(c);
                    if (aboard.Count > 0) Hold(tug.Structure, aboard);
                    log($"SIM salvage: caught up {cut} of {n} blocks ({c.Share:P0} of {StructureTag.Strip(wr.Name)}){(full ? " — hold full" : "")}");
                    if (!full && c.CatchUp - c.Base >= BoostAfter && !c.PerBlock)
                    {   // a long spell on its own: the crew's in its stride — double slabs for a while
                        c.BoostUntil = now + BoostSeconds;
                        log($"SIM salvage: boost — double slabs for {BoostSeconds / 60:0} min");
                    }
                    if (full) { c.Base = c.Share; c.CatchUp = c.Base; Beam(c, tug, false); c.Order = null; return CutResult.Full; }
                }
                if (c.Done >= c.Order.Count) return Finish(c, tug, wr, now);
                c.Phase = 1; c.PhaseAt = now; c.LastPos = null; NextAim(c, tug, st);
                c.Say($"salvage crew on {StructureTag.Strip(wr.Name)} — {order.Count} blocks, {c.Share:P0} cut, {(c.PerBlock ? "block by block" : c.Step + " blocks a cut")}.");
                log($"SIM salvage: tug {tug.Id} on wreck {wr.Id}, {order.Count} blocks, beam {(c.HasBeam ? "fitted" : "NONE")}");
                return CutResult.Working;
            }
            var beamHold = c.PerBlock ? 0.5 : 1.5;
            switch (c.Phase)
            {
                case 1:   // close in, stop, aim — the beam only once all three hold (or after 45 s, logged)
                {
                    if (now < c.CheckAt) return CutResult.Working;
                    c.CheckAt = now + 0.5;
                    var move = CloseIn(c, tug, st);
                    var mid = fly.CentreOf(tug);
                    var speed = c.LastPos.HasValue ? (mid - c.LastPos.Value).magnitude / 0.5f : 99f;
                    c.LastPos = mid;
                    var err = Vector3.Angle(tug.Forward, c.FaceAt - mid);
                    var ready = Math.Abs(move) < 6f && speed < 1f && err < 4f;   // the hold stalls ~5 m short; the beam has slack to spare
                    if (!ready && now - c.PhaseAt < 45) return CutResult.Working;
                    if (!ready && Math.Abs(move) > FireSlack)
                    {   // not there yet (blocked, detouring, stalled): never beam from afar — keep closing in
                        if (!c.FarSaid) { c.FarSaid = true; log($"SIM salvage: not in reach after 45 s ({move:0.0} m to go, {speed:0.0} m/s) — holding fire until close"); c.Say("can't get in close yet — working our way round."); }
                        return CutResult.Working;
                    }
                    if (!ready) log($"SIM salvage: not settled after 45 s ({move:0.0} m to go, {speed:0.0} m/s, {err:0}° off) — firing anyway");
                    c.FarSaid = false;
                    Beam(c, tug, true); c.Phase = 2; c.PhaseAt = now;
                    return CutResult.Working;
                }
                case 2:   // beam on: let it be seen (kept on the cut), then the cut
                {
                    if (now - c.PhaseAt < beamHold) return CutResult.Working;
                    if (!c.CratesDone)
                    {   // in position, beam on: the cargo boxes come aboard first; no room → nothing cut, the rest stays in them
                        if (!EmptyCrates(c, st, tug, wr)) { Beam(c, tug, false); c.Order = null; return CutResult.Full; }
                        c.CratesDone = true;
                    }
                    if (VolumeFull(c, st, tug, wr)) { Beam(c, tug, false); c.Order = null; return CutResult.Full; }
                    CutBlocks(c, st, now < c.BoostUntil ? Math.Min(MaxCutBlocks * 2, c.Step * 2) : c.Step);
                    if (c.WreckModel != null && (!c.PerBlock || c.Done % 20 == 0)) c.WreckModel.BuildShell();
                    Beam(c, tug, false); c.Phase = 3; c.PhaseAt = now;
                    if (!c.PerBlock || c.Done % 20 == 0 || c.Done >= c.Order.Count)
                    {   // the whole cargo into the tug's crates (SetContent replaces: always write all of it)
                        var aboard = Manifest(c);
                        var want = aboard.Sum(kv => kv.Value);
                        var held = want > 0 ? Hold(tug.Structure, aboard) : 0;
                        if (held < want && !c.CrowdedSaid)
                        {   // out of crate slots (16 a crate through the API), not of room: volume alone stops the cut. What's not
                            // racked stays on the record — it sells, it counts for the share, it shows in the hold window.
                            c.CrowdedSaid = true;
                            c.Say($"crates crowded — {want - held} items logged, not racked. Still room aboard: cutting on.");
                            log($"SIM salvage: crates crowded ({held}/{want} racked) — cutting on, the rest on the record");
                        }
                        if (c.WreckId.Length == 0 || c.Done >= c.Order.Count)
                            c.Say($"cutting {StructureTag.Strip(wr.Name)} — {c.Share:P0}, aboard: {string.Join(", ", aboard.OrderByDescending(kv => kv.Value).Take(4).Select(kv => $"{kv.Value} {kv.Key}"))}{(aboard.Count > 4 ? " …" : "")}.");
                    }
                    if (c.Done >= c.Order.Count) return Finish(c, tug, wr, now);
                    return CutResult.Working;
                }
                default:   // cooling: a beat between cuts, then aim at the next
                    if (now - c.PhaseAt < (c.PerBlock ? 0.2 : 0.5)) return CutResult.Working;
                    NextAim(c, tug, st); c.Phase = 1; c.PhaseAt = now; c.LastPos = null;
                    return CutResult.Working;
            }
        }

        CutResult Finish(Cutter c, IEntity tug, IEntity wr, double now)
        {
            Beam(c, tug, false);
            fly.Retire(wr.Id);   // whatever the cut left floating
            c.Say($"{StructureTag.Strip(wr.Name)} is scrap, {now - c.At:0} s — {Manifest(c).Sum(kv => kv.Value)} items aboard in all.");
            c.Wreck = 0;
            return CutResult.Scrapped;
        }

        /// <summary>An ore block's ore: "&lt;X&gt;Resource" → "&lt;X&gt;Ore" when that item exists (the game's own block names), else null.</summary>
        static string OreOf(string blockName, Dictionary<int, string> names)
        {
            if (blockName == null || !blockName.EndsWith("Resource", StringComparison.Ordinal) || blockName.Length <= 8) return null;
            var ore = blockName.Substring(0, blockName.Length - 8) + "Ore";
            return names.ContainsValue(ore) ? ore : null;
        }

        // ---- what a cut block gives: its recipe × the vanilla salvage share (the scenario's own configs) -------------

        /// <summary>Share of a block's recipe a cut gives back: the multitool's T1 Salvage ReturnFactor.</summary>
        const double SalvageReturn = 0.5;
        static Core.Sim.Recipes recipes;
        static bool recipesTried;

        /// <summary>The active scenario's BlocksConfig / Templates / ItemsConfig, read once per process (null = unreadable:
        /// blocks fall back to scrap plates).</summary>
        Core.Sim.Recipes Recipes()
        {
            if (recipesTried) return recipes;
            recipesTried = true;
            {
                try
                {
                    var dir = Core.Sim.Recipes.FindConfigDir(fly.Api.Application.GetPathFor(AppFolder.Content), fly.Api.Application.GetPathFor(AppFolder.SaveGame));
                    string F(string n) => System.IO.File.ReadAllText(System.IO.Path.Combine(dir, n));
                    if (!System.IO.File.Exists(System.IO.Path.Combine(dir, "Templates.ecf"))) { log($"SIM salvage: no Templates.ecf in {dir}"); return null; }
                    recipes = Core.Sim.Recipes.Parse(F("BlocksConfig.ecf"), F("Templates.ecf"), F("ItemsConfig.ecf"));
                    log($"SIM salvage: recipes from {dir} — {recipes.BlockCount} blocks, {recipes.TemplateCount} templates, {recipes.ItemCount} items");
                    return recipes;
                }
                catch (Exception ex) { log($"SIM salvage: reading recipes threw {ex.Message}"); }
            }
            log("SIM salvage: no recipes found — cut blocks give scrap plates");
            return null;
        }

        /// <summary>Litres of a hold (unknown items count 1 L; money 0 — it never stays aboard).</summary>
        double Litres(IEnumerable<KeyValuePair<string, int>> hold)
        {
            var r = Recipes(); double sum = 0;
            foreach (var kv in hold)
            {
                var name = Core.Sim.World.KeyBase(kv.Key, out _, out _);
                if (name == Core.Sim.World.Strongbox) continue;
                var v = r?.Volume(name) ?? double.NaN;
                sum += kv.Value * (double.IsNaN(v) ? 1 : v);
            }
            return sum;
        }

        /// <summary>The tug's cargo space in litres: the API's sum of its container devices (96,500 for LS_Tug: the 96,000 L
        /// the game shows + a small box) when it gives one; else the cargo blocks × their BlocksConfig VolumeCapacity (that scan
        /// counts a multi-cell crate once per cell: 240,000 for the same hull). Both logged once per hull.</summary>
        double CapacityOf(IEntity tug)
        {
            if (capacityOf.TryGetValue(tug.Id, out var known)) return known;
            double cap = 0, api = 0; var blocks = 0;
            try
            {
                var r = Recipes();
                var st = tug.Structure;
                if (r != null && st != null)
                {
                    var names = (fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key);
                    VectorInt3 mn = st.MinPos, mx = st.MaxPos;
                    for (var x = mn.x; x <= mx.x; x++) for (var y = mn.y + GridY; y <= mx.y + GridY; y++) for (var z = mn.z; z <= mx.z; z++)
                    {
                        var b = st.GetBlock(x, y, z); if (b == null) continue;
                        b.Get(out var type, out _, out _, out _);
                        if (type == 0 || !names.TryGetValue(type, out var bn)) continue;
                        if (!IsCargoBlock(bn)) continue;   // ammo box, tanks, constructor, fridge, lockers: not the hold
                        var c = r.BlockCapacity(bn);
                        if (c > 0) { cap += c; blocks++; }
                    }
                }
                var list = st?.GetDevices(DeviceTypeName.Container);
                for (var i = 0; list != null && i < list.Count; i++) { try { api += st.GetDevice<IContainer>(list.GetAt(i))?.VolumeCapacity ?? 0; } catch { } }
            }
            catch (Exception ex) { log($"SIM salvage: hold volume scan threw {ex.Message}"); }
            var scanned = cap;
            if (api >= 8000) cap = api;   // the game's own sum per device; the block scan counts a multi-cell crate once per cell
            else if (cap <= 0) cap = api;
            if (cap > 0) capacityOf[tug.Id] = cap;
            if (cap > 0 || capacityNoted.Add(tug.Id)) log($"SIM salvage: tug {tug.Id} hold {cap:N0} L (the API says {api:N0}; {blocks} cargo cell(s) scanned = {scanned:N0})");
            return cap;
        }
        /// <summary>The cargo hold proper: container controllers, their extensions, cargo boxes.</summary>
        static bool IsCargoBlock(string name) =>
            name.StartsWith("ContainerController", StringComparison.Ordinal) || name.StartsWith("ContainerExtension", StringComparison.Ordinal) || name.StartsWith("CargoContainer", StringComparison.Ordinal);
        readonly Dictionary<int, double> capacityOf = new Dictionary<int, double>();
        readonly HashSet<int> capacityNoted = new HashSet<int>();

        /// <summary>Would the next slab's parts overflow the tug by volume? Then it stops before cutting (says so once).</summary>
        bool VolumeFull(Cutter c, IStructure st, IEntity tug, IEntity wr)
        {
            var r = Recipes(); var cap = CapacityOf(tug);
            if (r == null || cap <= 0)
            {   // nothing to judge by: say so once (a hold that never fills was this, unseen)
                if (!c.VolumeUnknownSaid) { c.VolumeUnknownSaid = true; log($"SIM salvage: no volume check for tug {tug.Id} ({(r == null ? "no recipes" : "no capacity")})"); }
                return false;
            }
            Dictionary<int, string> names; try { names = (fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()).GroupBy(kv => kv.Value).ToDictionary(g => g.Key, g => g.First().Key); } catch { return false; }
            double slab = 0;
            foreach (var v in c.Order.Skip(c.Done).Take(c.Step))
            {
                try
                {
                    var blk = st.GetBlock(v.x, v.y, v.z); if (blk == null) continue;
                    blk.Get(out var bt, out _, out _, out _);
                    if (!names.TryGetValue(bt, out var bn)) continue;
                    var per = r.PerBlock(bn); if (per == null) continue;
                    foreach (var kv in per) { var vol = r.Volume(kv.Key); slab += kv.Value * SalvageReturn * (double.IsNaN(vol) ? 1 : vol); }
                }
                catch { }
            }
            var have = Litres(Manifest(c));
            if (have + slab <= cap) return false;
            c.Say($"hold full by volume — {have:N0} of {cap:N0} L aboard, the next cut would add {slab:N0} L. Take what you want or send us home.");
            log($"SIM salvage: volume full ({have:0}+{slab:0} > {cap:0} L)");
            return true;
        }

        /// <summary>A cut block's parts into the hold (fractions carried); false = no recipe for it.</summary>
        bool Salvaged(Cutter c, string block)
        {
            var r = Recipes();
            if (r == null) return false;
            var per = r.PerBlock(block);
            if (per == null || per.Count == 0) return false;
            foreach (var kv in per)
            {
                var have = (c.Frac.TryGetValue(kv.Key, out var f) ? f : 0) + kv.Value * SalvageReturn;
                var whole = (int)Math.Floor(have + 1e-9);
                if (whole > 0) c.Cargo[kv.Key] = (c.Cargo.TryGetValue(kv.Key, out var n) ? n : 0) + whole;
                c.Frac[kv.Key] = have - whole;
            }
            return true;
        }

        /// <summary>
        /// Before a single block goes: every crate on the target emptied into the tug, stack by stack as it is (tokens keep
        /// their ids). A crate that doesn't fit stays full and nothing is cut — cutting a cargo box's extensions shrinks it
        /// and the game destroys what no longer fits, so the wreck is only cut once its crates are empty. False = no room.
        /// </summary>
        bool EmptyCrates(Cutter c, IStructure st, IEntity tug, IEntity wr)
        {
            refills.Remove(wr.Id);   // the wreck's hold is the tug's now: no late re-send may fill it again
            Dictionary<int, string> names = null;
            var left = 0; var moved = 0;
            IDevicePosList list; try { list = st.GetDevices(DeviceTypeName.Container); } catch { return true; }
            for (var i = 0; list != null && i < list.Count; i++)
            {
                var pos = list.GetAt(i);
                IContainer box; List<ItemStack> stacks;
                try { box = st.GetDevice<IContainer>(pos); stacks = box?.GetContent(); } catch { continue; }
                if (stacks == null || stacks.Count == 0 || stacks.All(x => x.count <= 0)) continue;
                if (names == null) { names = new Dictionary<int, string>(); try { foreach (var kv in fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()) names[kv.Value] = kv.Key; } catch { } }
                var trial = new Dictionary<string, int>(c.Cargo, StringComparer.Ordinal);
                var unknown = 0;
                var cash = 0;
                foreach (var it in stacks.Where(x => x.count > 0))
                {
                    if (!names.TryGetValue(it.id, out var n)) { unknown += it.count; continue; }
                    if (c.OnCash != null && n == Core.Sim.World.Strongbox) { cash += it.count; continue; }   // money: to the balance, not the hold
                    var k = Core.Sim.World.StackKey(n, it.ammo, it.decay);
                    trial[k] = (trial.TryGetValue(k, out var have) ? have : 0) + it.count;
                }
                if (unknown > 0) { left += stacks.Sum(x => x.count); log($"SIM salvage: crate at {pos.x},{pos.y - GridY},{pos.z} has {unknown} item(s) with no name — left in the wreck"); continue; }
                var before = c.Cargo.ToDictionary(kv => kv.Key, kv => kv.Value, StringComparer.Ordinal);
                c.Cargo.Clear(); foreach (var kv in trial) c.Cargo[kv.Key] = kv.Value;
                var aboard = Manifest(c); var want = aboard.Sum(kv => kv.Value);
                var held = Hold(tug.Structure, aboard);
                if (held < want)
                {   // doesn't fit: back as it was, this crate stays full
                    c.Cargo.Clear(); foreach (var kv in before) c.Cargo[kv.Key] = kv.Value;
                    Hold(tug.Structure, Manifest(c));
                    left += stacks.Sum(x => x.count);
                    continue;
                }
                try { box.SetContent(new List<ItemStack>()); } catch (Exception ex) { log("SIM salvage: emptying a crate threw " + ex.Message); }
                if (cash > 0) c.OnCash(cash);
                moved += stacks.Sum(x => x.count);
                log($"SIM salvage: crate at {pos.x},{pos.y - GridY},{pos.z} emptied into the tug: {string.Join(", ", stacks.Where(x => x.count > 0).Select(x => (names.TryGetValue(x.id, out var n0) ? Core.Sim.World.StackKey(n0, x.ammo, x.decay) : "#" + x.id) + "×" + x.count))}");
            }
            if (left > 0)
            {
                c.Say($"hold full — {left} item(s) still in {StructureTag.Strip(wr.Name)}'s cargo boxes, left as they are. Take what you want and send us again.");
                log($"SIM salvage: {left} item(s) left in {wr.Id}'s crates (no room); {moved} moved; nothing cut");
                return false;
            }
            if (moved > 0) c.Say($"{StructureTag.Strip(wr.Name)}'s cargo boxes are aboard ({moved} items) — cutting now.");
            return true;
        }

        /// <summary>The next <paramref name="n"/> blocks in the cut order, gone; a cargo box's contents go aboard first.</summary>
        void CutBlocks(Cutter c, IStructure st, int n)
        {
            var gone = 0;
            Dictionary<int, string> itemNames = null;
            foreach (var v in c.Order.Skip(c.Done).Take(n))
            {
                if (c.Crates.Remove($"{v.x},{v.y},{v.z}"))
                {   // a cargo box: what the players left in it goes aboard, not into space
                    try
                    {
                        var stacks = st.GetDevice<IContainer>(v)?.GetContent();
                        if (stacks != null && stacks.Count > 0)
                        {
                            if (itemNames == null) { itemNames = new Dictionary<int, string>(); try { foreach (var kv in fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()) itemNames[kv.Value] = kv.Key; } catch { } }
                            foreach (var it in stacks)
                                if (it.count > 0 && itemNames.TryGetValue(it.id, out var iname))
                                {
                                    if (c.OnCash != null && iname == Core.Sim.World.Strongbox) { c.OnCash(it.count); continue; }
                                    var k = Core.Sim.World.StackKey(iname, it.ammo, it.decay); c.Cargo[k] = (c.Cargo.TryGetValue(k, out var have) ? have : 0) + it.count;
                                }
                            log($"SIM salvage: crate at {v.x},{v.y - GridY},{v.z} emptied into the tug: {string.Join(", ", stacks.Select(x => (itemNames.TryGetValue(x.id, out var n0) ? n0 : x.id.ToString()) + "×" + x.count))}");
                        }
                    }
                    catch (Exception ex) { log("SIM salvage: crate read threw " + ex.Message); }
                }
                try
                {
                    var blk = st.GetBlock(v.x, v.y, v.z);
                    if (blk == null) continue;
                    blk.Get(out var bt, out _, out _, out _);
                    if (itemNames == null) { itemNames = new Dictionary<int, string>(); try { foreach (var kv in fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()) itemNames[kv.Value] = kv.Key; } catch { } }
                    var bname = itemNames.TryGetValue(bt, out var bn) ? bn : "#" + bt;
                    blk.Set(0, null, null, null); gone++;
                    c.CutNames[bname] = (c.CutNames.TryGetValue(bname, out var cn) ? cn : 0) + 1;
                    if (OreOf(bname, itemNames) is string ore) c.Cargo[ore] = (c.Cargo.TryGetValue(ore, out var oh) ? oh : 0) + 1;   // ~the game's own drop: 1–3 at 50 %
                    else if (!Salvaged(c, bname)) c.BlocksCut++;   // no recipe known → scrap
                }
                catch { }
                c.WreckModel?.Filled.Remove(new Core.Cell(v.x, v.y - GridY, v.z));   // the model loses it too
            }
            c.Done = Math.Min(c.Order.Count, c.Done + n);
            if (c.CutNames.Count > 0 && (c.Done >= c.Order.Count || c.Done % 600 < n))
                log($"SIM salvage: cut so far by block — {string.Join(", ", c.CutNames.OrderByDescending(kv => kv.Value).Take(8).Select(kv => kv.Key + "×" + kv.Value))}");
        }

        /// <summary>
        /// Park for the beam: its origin <see cref="BeamLength"/> − <see cref="BeamReachIn"/> m from the cut's middle (the tip
        /// lands in it), but the hull never closer than <see cref="SalvageMinGap"/> m to the wreck's (both voxel models).
        /// The spot is fixed ONCE per cut (re-picking it from where the ship is every half second chased its own momentum —
        /// it wobbled back and forth); only a hull closer than the minimum gap re-parks it, straight back out.
        /// Returns how far the ship still is from its spot (≈ 0 = in place).
        /// </summary>
        float CloseIn(Cutter c, IEntity tug, IStructure wreck)
        {
            var gap = float.MaxValue;
            if (c.TugModel != null && c.WreckModel != null && c.WreckModel.Shell.Count > 0)
                gap = Core.VoxelModel.Gap(c.TugModel, PoseOf(tug.Structure), c.WreckModel, PoseOf(wreck), out _, out _);
            if (!c.Parked)
            {
                var mid = fly.CentreOf(tug);
                var from = mid;
                if (c.HasBeam) { try { from = tug.Structure.StructToGlobalPos(c.BeamCell); } catch { } }
                var reach = (c.AimAt - from).magnitude;
                var move = reach - (BeamLength - BeamReachIn);
                if (gap < float.MaxValue) move = Math.Min(move, gap - SalvageMinGap);   // the hull first: never into the wreck for a better shot
                // the beam already reaches the next cut from here (tip inside it, hull clear): stay put, just re-aim
                if (reach <= BeamLength + 1f && reach >= BeamLength - BeamSlack && gap >= SalvageMinGap) move = 0;
                c.ParkAt = mid + (c.AimAt - mid).normalized * move;
                c.Parked = true;
                fly.MoveHold(tug.Id, c.ParkAt);   // (a goto under 100 m counts as arrived at once; a hold point flies, then creeps)
                log($"SIM salvage: beam {(c.AimAt - from).magnitude:0} m from the cut, hull gap {(gap < float.MaxValue ? gap.ToString("0.0") : "?")} m → parking {move:0.0} m along");
            }
            else if (gap < SalvageMinGap - 1f)
            {   // drifted in too close: straight back out to a safe gap
                var mid = fly.CentreOf(tug);
                c.ParkAt = mid + (mid - c.AimAt).normalized * (SalvageMinGap - gap + 2f);
                fly.MoveHold(tug.Id, c.ParkAt);
                if (fly.Now - c.GapLoggedAt >= 3) { c.GapLoggedAt = fly.Now; log($"SIM salvage: hull gap {gap:0.0} m — backing off"); }
            }
            return (fly.CentreOf(tug) - c.ParkAt).magnitude;
        }

        /// <summary>Everything the tug carries: the crates' leftovers it picked up + scrap (a SteelPlate per 2 blocks cut, a
        /// placeholder until blocks map to materials).</summary>
        static List<KeyValuePair<string, int>> Manifest(Cutter c)
        {
            var m = new Dictionary<string, int>(c.Cargo, StringComparer.Ordinal);
            if (c.BlocksCut >= 2) m["SteelPlate"] = (m.TryGetValue("SteelPlate", out var p0) ? p0 : 0) + c.BlocksCut / 2;
            return m.Select(kv => new KeyValuePair<string, int>(kv.Key, kv.Value)).ToList();
        }

        /// <summary>The middle of the next cut (where the beam must land).</summary>
        void NextAim(Cutter c, IEntity tug, IStructure wreck)
        {
            var next = c.Order.Skip(c.Done).Take(c.Step).ToList();
            if (next.Count == 0) return;
            var sum = Vector3.zero; foreach (var v in next) sum += wreck.StructToGlobalPos(v);
            c.AimAt = sum / next.Count;
            c.Parked = false;   // a new cut: a new spot
            AimFromBeam(c, tug);
        }

        /// <summary>
        /// The beam fires along the nose from where the beam block sits, off the ship's centre. Solved ONCE per cut (re-aiming
        /// every tick chased its own lever arm — 40° of wiggle): the beam's sideways / up offset in the ship's own frame is
        /// subtracted from the target, and the ship's centre is pointed at that fixed point. Stays put while it turns.
        /// </summary>
        void AimFromBeam(Cutter c, IEntity tug)
        {
            c.FaceAt = c.AimAt;
            if (c.HasBeam)
            {
                try
                {
                    var off = Quaternion.Inverse(tug.Rotation) * (tug.Structure.StructToGlobalPos(c.BeamCell) - fly.CentreOf(tug));
                    off.z = 0;   // along the nose doesn't matter: the beam is long
                    c.FaceAt = c.AimAt - tug.Rotation * off;
                }
                catch { }
            }
            fly.Aim(tug.Id, c.FaceAt);
        }

        void Beam(Cutter c, IEntity tug, bool on)
        {
            if (!c.HasBeam) return;
            try { tug.Structure.GetBlock(c.BeamCell)?.Set(null, null, null, on); } catch (Exception ex) { log("SIM beam toggle threw " + ex.Message); }
        }

        /// <summary>
        /// Fit the mining beam on the tug's nose: on its FRONT FACE (any block whose cell straight ahead is empty, within a few
        /// blocks of the very front) nearest the ship's centre line — flush with the face and centred, not on whichever hull
        /// block sticks out furthest (a sloped nose put it at the top edge, a panel in front pushed it a cell out into space).
        /// A beam already there (after a reload) is reused. Only right for a +Z nose (the rotation code); else it says so.
        /// </summary>
        void PlaceBeam(Cutter c, IEntity tug)
        {
            var model = ModelOf(tug.Structure); c.TugModel = model;
            if (model == null || model.Shell.Count == 0) { log("SIM salvage: tug blocks unreadable, no beam"); return; }
            PowerUp(tug, model);
            fly.SetTurnCap(tug.Id, TugTurn); fly.SetAccelCap(tug.Id, TugAccel);   // eases onto each cut instead of snapping round (snaps read as wobble)
            var pose = PoseOf(tug.Structure); var fw = tug.Forward;
            float Along(Core.Cell cl) { var w = pose.World(cl); return w.X * fw.x + w.Y * fw.y + w.Z * fw.z; }
            var centre = Vector3.zero; foreach (var cl in model.Filled.Keys) { var w = pose.World(cl); centre += new Vector3(w.X, w.Y, w.Z); } centre /= model.Filled.Count;
            Pivot(tug, centre);
            float Off(Core.Cell cl) { var w = pose.World(cl); var d = new Vector3(w.X, w.Y, w.Z) - centre; return (d - Vector3.Dot(d, fw) * fw).magnitude; }
            // which grid axis is the nose, and which way
            var steps = new[] { new KeyValuePair<string, Core.Flight.Vec3>("X", pose.StepX), new KeyValuePair<string, Core.Flight.Vec3>("Y", pose.StepY), new KeyValuePair<string, Core.Flight.Vec3>("Z", pose.StepZ) };
            var ax = steps.OrderByDescending(k => Math.Abs(k.Value.X * fw.x + k.Value.Y * fw.y + k.Value.Z * fw.z)).First();
            var sign = ax.Value.X * fw.x + ax.Value.Y * fw.y + ax.Value.Z * fw.z < 0 ? -1 : 1;
            int ddx = ax.Key == "X" ? sign : 0, ddy = ax.Key == "Y" ? sign : 0, ddz = ax.Key == "Z" ? sign : 0;
            if (ax.Key != "Z" || sign < 0) log($"SIM salvage: tug nose is grid {(sign < 0 ? "-" : "+")}{ax.Key}, the beam's rotation code is for +Z — it may point off");
            Core.Cell Ahead(Core.Cell cl) => new Core.Cell(cl.X + ddx, cl.Y + ddy, cl.Z + ddz);
            // already fitted (a reload, a re-spawn): keep the frontmost beam, take out any extras (no second projector)
            var fitted = model.Filled.Where(kv => kv.Value.Contains("MiningBeam")).Select(kv => kv.Key).OrderByDescending(Along).ToList();
            if (fitted.Count > 0)
            {
                for (var i = 1; i < fitted.Count; i++) { try { tug.Structure.GetBlock(new VectorInt3(fitted[i].X, fitted[i].Y + GridY, fitted[i].Z))?.Set(0, null, null, null); } catch { } }
                var keep = new VectorInt3(fitted[0].X, fitted[0].Y + GridY, fitted[0].Z);
                try { tug.Structure.GetBlock(keep)?.Set(null, null, null, false); } catch { }
                c.BeamCell = keep; c.HasBeam = true;
                log($"SIM salvage: beam already fitted at {fitted[0]}{(fitted.Count > 1 ? $", {fitted.Count - 1} extra removed" : "")}");
                return;
            }
            var front = model.Filled.Keys.Max(Along);
            var face = model.Filled.Keys.Where(cl => !model.Filled.ContainsKey(Ahead(cl)) && Along(cl) >= front - pose.Size * 3f).OrderBy(Off).ThenByDescending(Along).ToList();
            foreach (var nose in face.Take(6))
            {
                var a = Ahead(nose);
                var cell = new VectorInt3(a.X, a.Y + GridY, a.Z);
                try
                {
                    var b = tug.Structure.GetBlock(cell);
                    if (b == null) continue;
                    b.Get(out var was, out _, out _, out _);
                    if (was == BeamBlock) { b.Set(null, null, null, false); c.BeamCell = cell; c.HasBeam = true; return; }   // fitted before a reload
                    if (was != 0) continue;
                    b.Set(BeamBlock, 0, BeamRotPlusZ, false);
                    c.BeamCell = cell; c.HasBeam = true;
                    log($"SIM salvage: beam fitted at {a.X},{a.Y},{a.Z} on the nose face, ahead of {model.Filled[nose]} {nose} ({Off(nose):0.0} m off the centre line)");
                    return;
                }
                catch (Exception ex) { log("SIM salvage: beam place threw " + ex.Message); return; }
            }
            log("SIM salvage: no free cell on the nose face, no beam");
        }

        /// <summary>
        /// The tug hull on any leg (at the job, docked, on a lane): Eden's "free" tugboat comes up player-owned, dry and
        /// switched off — ours under its own flag (Trader), tank filled, generators on. Once, as soon as its blocks read.
        /// </summary>
        bool FitTugHull(Live l, IEntity e)
        {
            if (e.Structure == null) return false;
            try { if (Factions.IsPlayer((int)e.Faction.Group)) { e.Structure.SetFaction((FactionGroup)TraderGroup(l.Prefab), 0); log($"SIM {l.Agent}: tug hull spawned player-owned — set to faction {TraderGroup(l.Prefab)}"); } }
            catch (Exception ex) { log($"SIM {l.Agent}: faction fix threw {ex.Message}"); }
            var model = ModelOf(e.Structure);
            if (model == null || model.Filled.Count == 0) return false;   // not readable yet: next tick
            PowerUp(e, model);
            if ((l.Prefab ?? "") == TugPrefab) PinTug(e);
            var pose = PoseOf(e.Structure); var centre = Vector3.zero;
            foreach (var cl in model.Filled.Keys) { var w = pose.World(cl); centre += new Vector3(w.X, w.Y, w.Z); }
            Pivot(e, centre / model.Filled.Count);
            return true;
        }

        /// <summary>The tug's crates as they are (scrap + crate leftovers from before a reload) → its tally, so the next
        /// write of the hold keeps them instead of replacing them.</summary>
        void SeedCargo(Cutter c, IEntity tug)
        {
            try
            {
                var names = new Dictionary<int, string>();
                foreach (var kv in fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()) names[kv.Value] = kv.Key;
                var list = tug.Structure.GetDevices(DeviceTypeName.Container);
                for (var i = 0; list != null && i < list.Count; i++)
                {
                    var stacks = tug.Structure.GetDevice<IContainer>(list.GetAt(i))?.GetContent();
                    if (stacks == null) continue;
                    foreach (var it in stacks)
                        if (it.count > 0 && names.TryGetValue(it.id, out var n)) { var k = Core.Sim.World.StackKey(n, it.ammo, it.decay); c.Cargo[k] = (c.Cargo.TryGetValue(k, out var have) ? have : 0) + it.count; }
                }
                if (c.Cargo.Count > 0) log($"SIM tug {tug.Id}: already aboard {string.Join(", ", c.Cargo.Select(kv => $"{kv.Value} {kv.Key}"))}");
            }
            catch (Exception ex) { log("SIM tug: reading its hold threw " + ex.Message); }
        }

        /// <summary>The owner's thrust numbers for the tug hull (prefabs/&lt;hull&gt;.thrust, off its Statistics panel), if given.</summary>
        void PinTug(IEntity e) { if (TugHull.Thrust != null) fly.PinThrust(e.Id, TugHull.Thrust); }

        /// <summary>The flight driver steers this ship by its hull's middle (its origin can sit metres off the blocks).</summary>
        void Pivot(IEntity e, Vector3 centre)
        {
            var local = Quaternion.Inverse(e.Rotation) * (centre - e.Position);
            fly.SetPivot(e.Id, local);
            log($"SIM {StructureTag.Strip(e.Name)}: origin {local.magnitude:0.0} m off the hull's middle (pivot mode {fly.PivotMode})");
        }

        /// <summary>
        /// Some hulls spawn dry and switched off (the Eden tugboat: fuel 0, unpowered) and can't fly: fill the tank to 70 %
        /// and switch every generator block on (the API has no power switch; a block's on/off is its 4th Set value).
        /// </summary>
        void PowerUp(IEntity ship, Core.VoxelModel model)
        {
            var st = ship.Structure;
            bool before = false; try { before = st.IsPowered; } catch { }
            try { var t = st.FuelTank; if (t != null && t.Capacity > 0 && t.Content < t.Capacity * 0.3f) t.AddContent(t.Capacity * 0.7f - t.Content); } catch (Exception ex) { log("SIM salvage: refuel threw " + ex.Message); }
            var on = 0;
            foreach (var kv in model.Filled.Where(x => x.Value.IndexOf("Generator", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                try { st.GetBlock(kv.Key.X, kv.Key.Y + GridY, kv.Key.Z)?.Set(null, null, null, true); on++; } catch { }
            }
            float fuel = -1; try { fuel = st.FuelTank?.Content ?? -1; } catch { }
            log($"SIM salvage: {StructureTag.Strip(ship.Name)} powered {before} → generators on: {on}, fuel {fuel:0}");
        }

        // ---- the spike (admin / test) ----------------------------------------------------------------------------------

        /// <summary>Spike: a tug spawned alongside the nearest «LS» wreck cuts it up (10 % slabs, or "block" by block).</summary>
        void SalvageSpike(int pid, List<string> rest)
        {
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var sp) || sp == null || Pf.Entities == null) return;
            var perBlock = rest.Count > 0 && rest[0] == "block"; if (perBlock) rest.RemoveAt(0);
            var from = sp.DrivingEntity != null ? sp.DrivingEntity.Position : sp.Position;
            var wr = Pf.Entities.Values.Where(x => x != null && x.Structure != null && (x.Name ?? "").Contains("Wreck of ") && (x.Position - from).magnitude < 3000)
                                       .OrderBy(x => (x.Position - from).magnitude).FirstOrDefault();
            if (wr == null) { fly.Say(pid, "Salvage test: no «LS» wreck within 3 km."); return; }
            var tug = rest.Count > 0 ? string.Join(" ", rest) : TugPrefab;
            var away = from - wr.Position; if (away.sqrMagnitude < 1) away = Vector3.forward;
            var tpos = ClearSpot(wr.Position + away.normalized * (PfSpike.HullRadius(wr) + 25f), 30f);
            if (probe?.Entity > 0) fly.Retire(probe.Entity);
            probe = new Probe { Pid = pid, Prefab = tug, Group = "salvage", Index = -1, Since = fly.Now };
            spikeCut = new Cutter { PerBlock = perBlock, Say = s => fly.Say(pid, s) };
            spikeCut.NewTarget(wr.Id, "", 0, 0);
            fly.Send("wreckspawn", tug, F(tpos.x), F(tpos.y), F(tpos.z), F(Yaw(wr.Position - tpos)), StructureTag.Apply("Probe salvage " + tug));
            fly.Say(pid, $"Salvage test: {tug} spawning alongside {StructureTag.Strip(wr.Name)} ({wr.Id}) — aim, beam, cut {(perBlock ? "ONE BLOCK" : "10 %")} at a time. The wreck is GONE after this.");
        }

        /// <summary>The spike's tug each second: settle, ours (Polaris, flown by us), beam fitted; then the cycle.</summary>
        void SpikeStep(double now)
        {
            if (probe.Entity == 0 || Pf?.Entities == null || spikeCut == null) return;
            if (!Pf.Entities.TryGetValue(probe.Entity, out var tug) || tug?.Structure == null) return;
            var c = spikeCut; c.Tug = tug.Id;
            var tname = StructureTag.Strip(tug.Name);
            c.Say = s => fly.Say(probe.Pid, $"{tname}: {s}");
            if (!c.Ready)
            {
                if (now - probe.Since < 5) return;
                try { tug.Structure.SetFaction(FactionGroup.Polaris, 0); } catch (Exception ex) { log("SIM salvage test: tug faction threw " + ex.Message); }
                fly.Adopt(tug.Id);
                PlaceBeam(c, tug);
                PinTug(tug);   // same hull as the sim's tugs: without the pin a weak lift command doesn't move it
                c.Ready = true;
                if (probe.Group == "tug") fly.Say(probe.Pid, $"{tname}: salvage tug ready{(c.HasBeam ? ", beam fitted" : " (no beam)")}. /ls fly tug follow | wreck [id] | stop | clear");
            }
            var res = SalvageStep(c, tug, now);
            if (res != CutResult.Working && probe.Group == "tug") fly.Command(probe.Pid, tug.Id, "hold", 0);
            if (res == CutResult.Full) c.Wreck = 0;
        }

        /// <summary>
        /// Demo: your own salvage tug. spawn (beside you) · follow (you) · wreck [id] (cut up the nearest structure that isn't
        /// you or the tug — any faction — or that id; holds when done) · stop · clear.
        /// </summary>
        /// <summary>The hull the demo tug holds in its beam (fake tow), 0 = none.</summary>
        int towing;

        void TugCmd(int pid, string[] args)
        {
            var sub = args.Length > 0 ? args[0] : "";
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var me) || me == null || Pf.Entities == null) return;
            var at = me.DrivingEntity != null ? me.DrivingEntity.Position : me.Position;
            var tugId = probe != null && probe.Group == "tug" ? probe.Entity : 0;
            IEntity tug = null; if (tugId > 0) Pf.Entities.TryGetValue(tugId, out tug);
            switch (sub)
            {
                case "spawn":
                {
                    if (probe?.Entity > 0) fly.Retire(probe.Entity);
                    var side = (me.DrivingEntity != null ? me.DrivingEntity.Rotation : me.Rotation) * Vector3.right;
                    var pos = ClearSpot(at + side * 120f, 40f);
                    probe = new Probe { Pid = pid, Prefab = TugPrefab, Group = "tug", Index = -1, Since = fly.Now };
                    spikeCut = new Cutter();
                    fly.Send("wreckspawn", TugPrefab, F(pos.x), F(pos.y), F(pos.z), F(Yaw(at - pos)), StructureTag.Apply("Salvage tug"));
                    fly.Say(pid, "Salvage tug spawning 120 m to your right…");
                    return;
                }
                case "clear":
                    if (towing != 0) { fly.StopTow(towing); towing = 0; }
                    if (tugId > 0) fly.Retire(tugId);
                    probe = null; spikeCut = null; fly.Say(pid, "Tug gone."); return;
            }
            var c = spikeCut;
            if (tug == null || c == null || !c.Ready) { fly.Say(pid, "No tug ready: /ls fly tug spawn (then give it ~5 s)."); return; }
            switch (sub)
            {
                case "follow":
                    if (c.Wreck != 0) { Beam(c, tug, false); c.Wreck = 0; }
                    fly.Say(pid, fly.Command(pid, tug.Id, "follow", 120f) ?? "It won't take orders.");
                    return;
                case "tow":
                {   // fake tow: the hull held in the beam ahead of the nose; then follow / stop as usual, "drop" lets go
                    IEntity t = null;
                    if (args.Length > 1 && int.TryParse(args[1], out var wid)) Pf.Entities.TryGetValue(wid, out t);
                    if (t?.Structure == null || t.Id == tug.Id) { fly.Say(pid, "/ls fly tug tow <id> — a coreless hull within 1.5 km."); return; }
                    if (Cored(t)) { fly.Say(pid, $"{StructureTag.Strip(t.Name)} has a core — someone owns it. Not touching it."); return; }
                    if ((t.Position - tug.Position).magnitude > 1500) { fly.Say(pid, "Too far — bring the tug within 1.5 km."); return; }
                    Beam(c, tug, false); c.Wreck = 0;
                    if (towing != 0) fly.StopTow(towing);
                    var ahead = PfSpike.HullRadius(tug) + PfSpike.HullRadius(t) + 10f;
                    if (!fly.StartTow(t.Id, tug.Id, ahead)) { fly.Say(pid, "Couldn't get a grip on it."); return; }
                    towing = t.Id; Beam(c, tug, true);
                    fly.Say(pid, $"{StructureTag.Strip(tug.Name)}: got {StructureTag.Strip(t.Name)} in the beam, {ahead:0} m ahead. /ls fly tug follow — it comes along; /ls fly tug drop to let go.");
                    return;
                }
                case "drop":
                    if (towing != 0) { fly.StopTow(towing); towing = 0; }
                    Beam(c, tug, false);
                    fly.Say(pid, $"{StructureTag.Strip(tug.Name)}: let go.");
                    return;
                case "stop":
                    Beam(c, tug, false); c.Wreck = 0;
                    fly.Say(pid, fly.Command(pid, tug.Id, "hold", 0) ?? "It won't take orders.");
                    return;
                case "wreck":
                {
                    IEntity t = null;
                    if (args.Length > 1 && int.TryParse(args[1], out var tid)) Pf.Entities.TryGetValue(tid, out t);
                    else t = Pf.Entities.Values.Where(x => x != null && x.Structure != null && x.Id != tug.Id && x.Id != me.DrivingEntity?.Id
                                                        && x.Type != EntityType.AstVoxel && !Cored(x) && (x.Position - tug.Position).magnitude < 1500)
                                               .OrderBy(x => (x.Position - tug.Position).magnitude).FirstOrDefault();
                    if (t?.Structure == null) { fly.Say(pid, "Nothing coreless to cut within 1.5 km of the tug."); return; }
                    if (Cored(t)) { fly.Say(pid, $"{StructureTag.Strip(t.Name)} has a core — someone owns it. Not touching it."); return; }
                    Beam(c, tug, false);
                    c.PerBlock = args.Length > 1 && args[args.Length - 1] == "block";
                    c.NewTarget(t.Id, "", 0, 0);
                    fly.Say(pid, $"{StructureTag.Strip(tug.Name)}: on it — cutting up {StructureTag.Strip(t.Name)} ({t.Id}){(c.PerBlock ? ", block by block" : "")}.");
                    return;
                }
                default:
                    fly.Say(pid, "/ls fly tug spawn | follow | wreck [id] [block] | tow <id> | drop | stop | clear");
                    return;
            }
        }

        /// <summary>A structure someone owns: it has a core (a player's or an NPC's). Salvage never touches those — a wreck
        /// a player cored is theirs (the same rule as the wreck cleanup's "claimed").</summary>
        static bool Cored(IEntity e)
        {
            string core; try { core = e?.Structure?.CoreType.ToString() ?? "None"; } catch { core = "None"; }
            return core != "None" && core != "NoFaction";
        }
    }
}
