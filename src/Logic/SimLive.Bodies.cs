using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using LivingSystems.Core.Sim;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// One body, many brains (docs/lifecycles.md), the PfServer half: the one live driver. It spawns, adopts ghosts, judges
    /// dead / claimed / gone, takes a body out of range back to the sim and brings it back in — the same way for every kind.
    /// A <see cref="Brain"/> only says what the thing does while it's live (and what it carries back).
    /// Wire: <see cref="BodyMsg"/> (bodyspawn / bodylive / bodyback / bodydead up; body|spawned / body|adopt down).
    /// </summary>
    sealed partial class SimLive
    {
        sealed class Body
        {
            public string Id = "", Kind = "";
            /// <summary>The playfield it lives in. One PfServer process can host several: while the process is on another one
            /// the body's hull and target aren't in view — it goes back to the sim as out of range (never "view", never dead).</summary>
            public string Playfield = "";
            public Brain Brain;
            /// <summary>Its hull here (0 = none: spawning, or held by the sim).</summary>
            public int Entity;
            /// <summary>An old hull the game may still have (the playfield went with it in it): taken back before spawning anew.</summary>
            public int Ghost;
            /// <summary>Held by the sim (no hull) while it waits for a watcher to come within range.</summary>
            public bool Offline;
            /// <summary>Its hull is in and the brain has settled it (fitted, its hold written).</summary>
            public bool Ready;
            /// <summary>Came in from the sim (not a first spawn, not a reload): its hold goes into the crates once ready.</summary>
            public bool CameBack;
            /// <summary>Ever had a hull here (a first spawn that never lands is called off; a later one is retried).</summary>
            public bool EverLive;
            /// <summary>Picked up again after a reload (its hold is what's in its crates).</summary>
            public bool Adopted;
            /// <summary>Its hull has been in the playfield's list since it was (re)taken: only then can it go missing.</summary>
            public bool Seen;
            public Vector3 Pos; public bool HasPos;
            public double Since, RetryAt;
            public readonly AwayClock Away = new AwayClock();
            /// <summary>The brain's own state.</summary>
            public object Mind;
        }

        abstract class Brain
        {
            protected readonly SimLive S;
            protected Brain(SimLive s) { S = s; }
            public abstract string Kind { get; }
            /// <summary>The player whose range keeps it live (0 = any player in the playfield).</summary>
            public virtual int Watcher(Body b) => 0;
            /// <summary>Kept on this PfServer while the sim has it (it comes back here when its watcher's near); false = the
            /// body is the Dedi's to send again.</summary>
            public virtual bool HeldHere => false;
            public virtual string Prefab(Body b) => "";
            public virtual string Name(Body b) => b.Id;
            public virtual int Faction(Body b) => 7;
            /// <summary>Its hull is in: fit it. False = not readable yet, ask again next second.</summary>
            public virtual bool Settle(Body b, IEntity e, double now) => true;
            public virtual void LiveTick(Body b, IEntity e, double now) { }
            /// <summary>Each second while its hull is in but not settled yet (status lines).</summary>
            public virtual void Arriving(Body b, IEntity e, double now) { }
            /// <summary>Going back to the sim (<paramref name="e"/> null = out of view, the hull isn't ours to touch): its
            /// progress for the record.</summary>
            public virtual string OnBack(Body b, IEntity e, string why, double now) => "";
            /// <summary>What it carries (World.FormatHold); <paramref name="e"/> = its hull when it's still there (the crates).</summary>
            public virtual string Hold(Body b, IEntity e) => "";
            /// <summary>Claimed by a player (their core in it) — checked each second, for kinds the flight driver doesn't watch.</summary>
            public virtual bool Claimed(Body b, IEntity e) => false;
            /// <summary>A body picked up after a reload may be out of sight for good reasons: never "missing" until seen once.</summary>
            public virtual bool WaitUnseen => false;
            public virtual void OnDead(Body b, string how) { }
            /// <summary>Left alone when the process switches playfields (a hub hull: the sim swaps it while nobody's near).</summary>
            public virtual bool StaysOnSwitch(Body b) => false;
            /// <summary>Each second while the sim holds it here (status lines).</summary>
            public virtual void OfflineTick(Body b, Vector3? watcherAt, double now) { }
            /// <summary>Coming back in: <paramref name="ghost"/> = its old hull was still there.</summary>
            public virtual void OnComeIn(Body b, bool ghost) { }
            /// <summary>A spawn that never landed.</summary>
            public virtual void OnSpawnFailed(Body b) { }
            /// <summary>The Dedi's spawn landed (its entity is set).</summary>
            public virtual void OnSpawned(Body b) { }
            /// <summary>Seconds out of range before it goes back; a spawn's wait; a hull missing from view this long = gone.</summary>
            public virtual double OutGrace => BodyRules.OutGrace;
            public virtual double SpawnTimeout => BodyRules.SpawnTimeout;
            public virtual double MissingGrace => BodyRules.MissingGrace;
            /// <summary>A first spawn that never lands ends the body ("nospawn": the hire is refunded) instead of handing it back.</summary>
            public virtual bool CallOffIfNeverSpawned => true;
            /// <summary>Its hull can be settled now (default: its blocks read, 5 s in, and <see cref="Settle"/> says so).</summary>
            public virtual bool ReadyCheck(Body b, IEntity e, double now) => e.Structure != null && now - b.Since >= 5 && Settle(b, e, now);
            /// <summary>The spot its range is judged from (default its hull).</summary>
            public virtual Vector3 RangeFrom(Body b, IEntity e) => e.Position;
            /// <summary>Kept live whatever the range right now (being set up, escorted, mid-jump…).</summary>
            public virtual bool Pinned(Body b, IEntity e, double now) => false;
            /// <summary>The kind's own word for why it went back (e.g. a pirate that "jumped" on its own "fled").</summary>
            public virtual string Why(Body b, string why) => why;
        }

        readonly Dictionary<string, Body> bodies = new Dictionary<string, Body>(StringComparer.Ordinal);
        readonly Dictionary<string, Brain> brains = new Dictionary<string, Brain>(StringComparer.Ordinal);

        void AddBrain(Brain b) => brains[b.Kind] = b;

        void SendBody(BodyMsg m) => fly.Send(m.Fields());

        /// <summary>Where a body's watcher is (their ship, else them): one player, or the nearest of everyone.</summary>
        bool WatcherAt(Body b, Vector3 from, out Vector3 at, out float dist)
        {
            at = Vector3.zero; dist = float.MaxValue;
            var w = b.Brain.Watcher(b);
            if (w != 0)
            {
                if (!PlayerShipAt(w, out at)) return false;
                dist = (at - from).magnitude;
                return true;
            }
            dist = fly.NearestPlayerDistance(from);
            if (dist == float.MaxValue || float.IsInfinity(dist)) return false;
            return PlayerSpot(out at);
        }

        /// <summary>Where the player is (their ship, else them), when they're in this playfield.</summary>
        bool PlayerShipAt(int pid, out Vector3 at)
        {
            at = Vector3.zero;
            if (Pf?.Players == null || !Pf.Players.TryGetValue(pid, out var me) || me == null) return false;
            at = me.DrivingEntity != null ? me.DrivingEntity.Position : me.Position;
            return true;
        }

        /// <summary>A new body, spawning at <paramref name="pos"/> (the Dedi answers "body|spawned").</summary>
        Body NewBody(Brain brain, string id, object mind)
        {
            var b = new Body { Id = id, Kind = brain.Kind, Brain = brain, Mind = mind, Since = fly.Now };
            bodies[id] = b;
            return b;
        }

        void SpawnBody(Body b, Vector3 pos, float yaw)
        {
            b.Entity = 0; b.Ready = false; b.Offline = false; b.Since = fly.Now; b.Pos = pos; b.HasPos = true;
            SendBody(BodyMsg.Spawn(b.Kind, b.Id, b.Brain.Prefab(b), pos.x, pos.y, pos.z, yaw, StructureTag.Apply(b.Brain.Name(b)), b.Brain.Faction(b)));
            log($"BODY {b.Kind} {b.Id} '{b.Brain.Name(b)}' spawning at ({pos.x:0}, {pos.y:0}, {pos.z:0})");
        }

        /// <summary>Back to the sim: hold + pos + progress on the record. <paramref name="e"/> null = out of view (kept as the ghost).</summary>
        void BodyBack(Body b, IEntity e, string why, double now, bool retire = false)
        {
            var progress = "";
            why = b.Brain.Why(b, why);
            var hold = "";
            try { hold = b.Brain.Hold(b, e) ?? ""; } catch (Exception ex) { log($"BODY {b.Id} Hold threw {ex.Message}"); }
            try { progress = b.Brain.OnBack(b, e, why, now) ?? ""; } catch (Exception ex) { log($"BODY {b.Id} OnBack threw {ex.Message}"); }
            var ghost = e == null && !retire ? b.Entity : 0;
            if (e != null) fly.Retire(e.Id);
            else if (retire && b.Entity > 0) fly.Retire(b.Entity);
            SendBody(BodyMsg.Back(b.Kind, b.Id, why, b.HasPos, b.Pos.x, b.Pos.y, b.Pos.z, hold, progress, ghost));
            log($"BODY {b.Kind} {b.Id} ({b.Entity}) back to the sim ({why}){(ghost > 0 ? $", hull {ghost} kept as its ghost" : "")}");
            b.Entity = 0; b.Ready = false; b.CameBack = false; b.Away.Reset();
            if (b.Brain.HeldHere) { b.Offline = true; b.Ghost = ghost; b.Since = now; b.RetryAt = now + 5; }
            else bodies.Remove(b.Id);
        }

        /// <summary>Dead by evidence (core destroyed, claimed, never spawned): the body ends; its hull (if any) stays.</summary>
        void BodyDead(Body b, string how)
        {
            if (!bodies.Remove(b.Id)) return;
            var hold = "";
            IEntity e = null; if (b.Entity > 0) Pf?.Entities?.TryGetValue(b.Entity, out e);
            try { hold = b.Brain.Hold(b, e) ?? ""; b.Brain.OnDead(b, how); } catch (Exception ex) { log($"BODY {b.Id} OnDead threw {ex.Message}"); }
            SendBody(BodyMsg.Dead(b.Kind, b.Id, how, hold));
            log($"BODY {b.Kind} {b.Id} ({b.Entity}) dead: {how}");
        }

        /// <summary>A held body's watcher is near again: its old hull if the game still has it, else a new one where it was.</summary>
        void ComeIn(Body b, Vector3 watcherAt, double now)
        {
            b.Offline = false; b.Ready = false; b.CameBack = true; b.Since = now;
            if (b.Ghost > 0 && Pf.Entities.TryGetValue(b.Ghost, out var g) && g != null && !fly.IsDead(b.Ghost))
            {
                b.Entity = b.Ghost; b.Ghost = 0;
                SendBody(BodyMsg.Live(b.Kind, b.Id, b.Entity));
                log($"BODY {b.Kind} {b.Id} back in range — its hull {b.Entity} is still here");
                b.Brain.OnComeIn(b, true);
                return;
            }
            if (b.Ghost > 0 && Pf.Entities.TryGetValue(b.Ghost, out var old) && old != null) fly.Retire(old.Id);   // a dead copy: never two of it
            b.Ghost = 0;
            var pos = b.HasPos ? ClearSpot(b.Pos, 60f) : ClearSpot(watcherAt + Level(UnityEngine.Random.onUnitSphere) * ArriveMetres, 60f);
            b.Brain.OnComeIn(b, false);
            SpawnBody(b, pos, Yaw(watcherAt - pos));
        }

        string PfName { get { try { return Pf?.Name ?? ""; } catch { return ""; } } }

        /// <summary>Each second: every body through the one lifecycle.</summary>
        void BodyStep(double now)
        {
            if (Pf?.Entities == null || bodies.Count == 0) return;
            string here; try { here = Pf.Name ?? ""; } catch { here = ""; }
            foreach (var b in bodies.Values.ToList())
            {
                try
                {
                    if (b.Playfield.Length == 0) b.Playfield = here;
                    if (here.Length > 0 && b.Playfield != here)
                    {   // the process is on another playfield now: none of this body's world is in view — first thing, before any brain
                        if (!b.Offline && b.Entity > 0) BodyBack(b, null, "range", now);
                        continue;
                    }
                    if (b.Offline)
                    {
                        var watched = WatcherAt(b, b.HasPos ? b.Pos : Vector3.zero, out var wat, out var wd);
                        b.Brain.OfflineTick(b, watched ? wat : (Vector3?)null, now);
                        if (now >= b.RetryAt && watched && (!b.HasPos || BodyRules.Near(true, wd))) ComeIn(b, wat, now);
                        continue;
                    }
                    if (b.Entity == 0)
                    {
                        if (now - b.Since <= b.Brain.SpawnTimeout) continue;
                        b.Brain.OnSpawnFailed(b);
                        if (!b.EverLive && b.Brain.CallOffIfNeverSpawned) { log($"BODY {b.Kind} {b.Id} never spawned — called off"); BodyDead(b, "nospawn"); }
                        else if (b.Brain.HeldHere) { b.Offline = true; b.RetryAt = now + 60; log($"BODY {b.Kind} {b.Id} respawn never came — still the sim's, trying again"); }
                        else { log($"BODY {b.Kind} {b.Id} never spawned"); BodyBack(b, null, "nospawn", now); }
                        continue;
                    }
                    if (fly.IsDead(b.Entity)) { BodyDead(b, "destroyed"); continue; }
                    if (!Pf.Entities.TryGetValue(b.Entity, out var e) || e == null)
                    {
                        if ((!b.Seen && b.Adopted && b.Brain.WaitUnseen) || now - b.Since <= b.Brain.MissingGrace) continue;
                        // gone with a player right there = blown up / scrapped (evidence); else out of view, not destroyed: the sim
                        // has it (the hull may still be there: the ghost)
                        if (b.HasPos && fly.NearestPlayerDistance(b.Pos) <= BodyRules.OutMetres) BodyDead(b, "destroyed");
                        else BodyBack(b, null, "view", now);
                        continue;
                    }
                    b.EverLive = true; b.Seen = true;
                    if (b.Brain.Claimed(b, e)) { BodyDead(b, "claimed"); continue; }
                    b.Pos = e.Position; b.HasPos = true;
                    if (!b.Ready)
                    {
                        b.Brain.Arriving(b, e, now);
                        if (!b.Brain.ReadyCheck(b, e, now)) continue;
                        b.Ready = true; b.Since = now; b.CameBack = false;
                    }
                    else
                    {
                        var watched = WatcherAt(b, b.Brain.RangeFrom(b, e), out _, out var dist);
                        var away = BodyRules.Away(watched, dist) && !b.Brain.Pinned(b, e, now);
                        // the last player left the playfield: back NOW, while it's still loaded (a destroy sent once it's unloaded
                        // can silently fail — the hull would linger in the save)
                        var grace = Pf.Players == null || Pf.Players.Count == 0 ? 0 : b.Brain.OutGrace;
                        if (b.Away.Tick(away, now, grace)) { BodyBack(b, e, "range", now); continue; }
                    }
                    b.Brain.LiveTick(b, e, now);
                }
                catch (Exception ex) { ShipThrew(b.Id, ex); }
            }
        }

        /// <summary>The Dedi's half: a spawn landed, or (after a reload) a body to pick up again.</summary>
        void BodyHandle(BodyMsg m)
        {
            if (!brains.TryGetValue(m.Kind, out var brain)) { log($"BODY: no brain for '{m.Kind}' ({m.Verb} {m.Id})"); return; }
            if (m.Verb == "spawned")
            {
                if (!bodies.TryGetValue(m.Id, out var b) || b.Entity != 0 || b.Offline) { fly.Retire(m.Entity); log($"BODY {m.Kind} {m.Id}: spawn {m.Entity} not wanted any more — retired"); return; }
                b.Entity = m.Entity; b.Since = fly.Now;
                b.Brain.OnSpawned(b);
                log($"BODY {m.Kind} {m.Id} spawned as {m.Entity}");
                return;
            }
            if (m.Verb == "drop")
            {   // the sim ended it, or it lives on another playfield now: no hull of it stays here
                if (!bodies.TryGetValue(m.Id, out var gone)) return;
                bodies.Remove(m.Id);
                foreach (var ent in new[] { gone.Entity, gone.Ghost }.Where(x => x > 0))
                    if (Pf?.Entities != null && Pf.Entities.ContainsKey(ent)) fly.Retire(ent);
                log($"BODY {m.Kind} {m.Id} dropped here (the sim has it elsewhere)");
                return;
            }
            if (m.Verb == "adopt")
            {
                if (bodies.TryGetValue(m.Id, out var had))
                {   // one PfServer process can host several playfields: a body held offline for the one it left is stale —
                    // the Dedi's adopt is the truth. A live one (hull here) stays.
                    if (!had.Offline && had.Entity > 0 && Pf?.Entities != null && Pf.Entities.ContainsKey(had.Entity)) return;
                    bodies.Remove(m.Id); HireIndex();
                    log($"BODY {m.Kind} {m.Id}: a stale record replaced by the Dedi's adopt");
                }
                var b = new Body { Id = m.Id, Kind = m.Kind, Brain = brain, Since = fly.Now, Adopted = true, EverLive = true, Playfield = PfName };
                if (m.HasPos) { b.Pos = new Vector3(m.X, m.Y, m.Z); b.HasPos = true; }
                if (m.Entity > 0) b.Entity = m.Entity;
                else { b.Offline = true; b.Ghost = m.Ghost; b.RetryAt = fly.Now; }
                b.Mind = AdoptMind(brain, b, m);
                if (b.Mind == null) { log($"BODY {m.Kind} {m.Id}: adopt refused by its brain"); return; }
                bodies[m.Id] = b;
                log($"BODY {m.Kind} {m.Id} re-adopted {(b.Offline ? $"out of range at ({b.Pos.x:0}, {b.Pos.y:0}, {b.Pos.z:0})" : "as " + b.Entity)}");
            }
        }

        /// <summary>A brain that can be picked up after a reload makes its state from the record (<see cref="IAdopts"/>).</summary>
        object AdoptMind(Brain brain, Body b, BodyMsg m) => brain is IAdopts a ? a.Adopt(b, m) : new object();

        interface IAdopts { object Adopt(Body b, BodyMsg m); }

        /// <summary>The flight driver says one of ours is gone: a body's hull → dead / claimed / out of view.</summary>
        bool BodyGone(int entity, string how)
        {
            var b = bodies.Values.FirstOrDefault(x => x.Entity == entity && entity > 0);
            if (b == null) return false;
            var fate = BodyRules.Judge(false, how == "dead", how == "claimed");   // the driver only calls when it's off our books
            if (fate == BodyFate.Dead) BodyDead(b, "destroyed");
            else if (fate == BodyFate.Claimed) BodyDead(b, "claimed");
            else BodyBack(b, null, how == "jumped" ? "jumped" : "view", fly.Now);
            return true;
        }
    }
}
