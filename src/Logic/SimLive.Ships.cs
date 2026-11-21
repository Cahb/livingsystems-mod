using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using LivingSystems.Core.Sim;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// Sim ships (traders, pirates, salvage tugs) as bodies (kind "ship", id = the agent): <see cref="ShipBrain"/> flies
    /// them (<see cref="ShipTick"/>, <see cref="TugTick"/>); the body driver spawns, judges range / dead / gone and hands
    /// them back. The Dedi holds them between spawns (not <see cref="Brain.HeldHere"/>): it sends one again when a player's
    /// near its lane ("simwant" → "trader" / "pirate" / "tug").
    /// </summary>
    sealed partial class SimLive
    {
        ShipBrain shipBrain;

        Live AddShip(Live l)
        {
            var b = l.B;
            b.Id = l.Agent; b.Kind = "ship"; b.Brain = shipBrain; b.Mind = l; b.Since = fly.Now;
            if (l.Entity > 0) b.EverLive = true;
            bodies[l.Agent] = b; live[l.Agent] = l;
            return l;
        }

        void DropShip(string agent)
        {
            live.Remove(agent);
            if (bodies.TryGetValue(agent, out var b) && b.Kind == "ship") bodies.Remove(agent);
        }

        void DropShips()
        {
            foreach (var k in bodies.Where(kv => kv.Value.Kind == "ship").Select(kv => kv.Key).ToList()) bodies.Remove(k);
            live.Clear();
        }

        void SpawnShip(Live l, string prefab, Vector3 pos, float yaw, string name, int group)
        {
            var b = l.B;
            b.Entity = 0; b.Ready = false; b.Since = fly.Now; b.Pos = pos; b.HasPos = true;
            SendBody(BodyMsg.Spawn("ship", l.Agent, prefab, pos.x, pos.y, pos.z, yaw, StructureTag.Apply(name), group));
        }

        static string ShipProgress(float frac, float hull) => BodyProgress.Write(new[]
        {
            new KeyValuePair<string, string>("frac", frac.ToString("0.###", CultureInfo.InvariantCulture)),
            new KeyValuePair<string, string>("hull", hull.ToString("0.###", CultureInfo.InvariantCulture)),
        });

        /// <summary>The ship's own bookkeeping when it leaves the live game (any way).</summary>
        void ForgetShip(string agent)
        {
            DropShip(agent);
            if (traffic.TryGetValue(agent, out var t)) t.Live = false;
            wanted[agent] = fly.Now;   // don't ask for it again right away (the Dedi re-sends its track first)
        }

        /// <summary>A ship's brain ends its live run itself (its leg's end, a spawn it can't place, prey gone with nobody
        /// watching): the hull, if any, is already retired by the caller. "left" / "arrived" / "jumped" / "fled".</summary>
        void End(string agent, string how, float frac = -1, float hull = -1)
        {
            // a tug takes its real hold back to the sim (the goods stay the goods, not just "plates")
            var hold = live.TryGetValue(agent, out var gone) && gone.Cut != null ? World.FormatHold(Manifest(gone.Cut)) : "";
            var b = gone?.B;
            ForgetShip(agent);
            SendBody(BodyMsg.Back("ship", agent, how, b != null && b.HasPos, b?.Pos.x ?? 0, b?.Pos.y ?? 0, b?.Pos.z ?? 0, hold, ShipProgress(frac, hull), 0));
            log($"SIM {agent} back to the sim: {how}");
        }

        sealed class ShipBrain : Brain, IAdopts
        {
            public ShipBrain(SimLive s) : base(s) { }
            public override string Kind => "ship";
            static Live L(Body b) => (Live)b.Mind;
            public override bool CallOffIfNeverSpawned => false;
            /// <summary>Ships fly from the moment they're seen (a far proxy has no blocks yet, the flight driver still steers it).</summary>
            public override bool ReadyCheck(Body b, IEntity e, double now) => true;
            public override void OnSpawned(Body b) => L(b).Since = S.fly.Now;

            /// <summary>A pirate at home / lurking / on patrol is judged by its SPOT (why it spawned), not where its ship is: a
            /// patrol orbit's far side or a short chase mustn't drop it out of the bubble and pop it back in; a tug at work by its
            /// spot at the wrecks (flying in from 3.5 km mustn't drop it out); leaving ones go by position.</summary>
            public override Vector3 RangeFrom(Body b, IEntity e)
            {
                var l = L(b);
                if (l.Kind == "pirate" && l.Target == 0 && !l.Departing && l.Chase == 0) return l.Dest;
                if (l.Kind == "tug" && !l.Departing) return l.Dest;
                return e.Position;
            }

            /// <summary>Stays live whatever the range: still being set up, escorted, spooling, or a pirate whose prey is still in
            /// someone's bubble (it's on its way in).</summary>
            public override bool Pinned(Body b, IEntity e, double now)
            {
                var l = L(b);
                if (l.Kind == "tug" && !l.Departing) return !l.Steered || l.Cut == null || !l.Cut.Ready;
                if (!l.Steered) return true;
                if (l.Kind == "trader" && l.LegKind.Length == 0) return true;
                if (S.fly.Escort != null && S.fly.Escort.Owns(l.Entity)) return true;   // escort contract: the run owns it now
                if (l.SpoolAt >= 0) return true;
                if (l.Kind == "pirate" && l.Target != 0 && S.Pf.Entities.TryGetValue(l.Target, out var prey) && prey != null && S.fly.NearestPlayerDistance(prey.Position) <= Bubble) return true;
                return false;
            }

            public override void LiveTick(Body b, IEntity e, double now) => S.ShipTick(L(b), e, now);

            public override string Why(Body b, string why)
            {
                var l = L(b);
                // escorted traders: the run's Dedi side adopts them mid-warp; a trader that fled on its own really left the
                // system — the sim sends it on; a pirate that jumped on its own (not sent away) fled
                if (why == "jumped") return l.Kind == "pirate" ? (l.Released ? "jumped" : "fled") : S.fly.Escort != null && S.fly.Escort.Owns(l.Entity) ? "left" : "jumped";
                return why;
            }

            public override string OnBack(Body b, IEntity e, string why, double now)
            {
                var l = L(b);
                var frac = l.Frac;
                if (l.Kind == "tug" && !l.Departing && l.Cut != null)
                {   // nobody watching: the sim has it, with the share this target really lost
                    if (e != null) S.Beam(l.Cut, e, false);
                    frac = l.Cut.Wreck != 0 ? l.Cut.Share : -1f;
                }
                else if (why == "range" && e != null)
                {
                    var n = StructureTag.Strip(e.Name);
                    if (l.Kind == "trader" && l.LegKind == "out")   // outran on its way to its jump: it jumps early (the story behind it vanishing)
                        S.Tell($"{n} lit its warp drive early and is gone — bound for {(l.Bound.Length > 0 ? l.Bound : "the next system")}.", $"{n} jumped out");
                    else S.Tell($"{n} is out of sensor range{(l.Kind == "trader" && l.Station.Length > 0 ? $" — last seen heading for {l.Station}" : "")}.", $"{n} out of range");
                }
                S.ForgetShip(l.Agent);
                return ShipProgress(frac, l.Hull);
            }

            public override string Hold(Body b, IEntity e) => L(b).Cut != null ? World.FormatHold(Manifest(L(b).Cut)) : "";

            public override void OnDead(Body b, string how)
            {
                var l = L(b);
                if (how == "destroyed")
                {   // the hull stays, coreless, with whatever is in its holds — a wreck (beacon + cleanup), pirates too
                    if (S.Pf?.Entities != null && S.Pf.Entities.TryGetValue(l.Entity, out var he) && he != null)
                    {
                        S.fly.Send("wreckfromlive", l.Agent, l.Entity, F(he.Position.x), F(he.Position.y), F(he.Position.z));
                        S.AddWreck(new WreckLive { Id = "~e" + l.Entity.ToString(CultureInfo.InvariantCulture), Entity = l.Entity, Since = S.fly.Now, Filled = true, Adopted = true });   // ours until the Dedi names its record (adopt)
                    }
                    // a pirate killed by players: its crew remembers who (kept on its hub — new recruits inherit the grudge)
                    if (l.Kind == "pirate")
                        foreach (var pid in l.HitBy) S.fly.Send("simmem", l.Agent, "memory.killed." + S.fly.PlayerKey(pid), StructureTag.Strip(S.Pf?.Entities != null && S.Pf.Entities.TryGetValue(l.Entity, out var pe) && pe != null ? pe.Name : l.Agent));
                }
                S.ForgetShip(l.Agent);
            }

            /// <summary>After a reload: a live ship we already have in the playfield; keep flying it. Its spot (lurkers / hub
            /// crews) comes as the body's pos; none → it holds where it is now, never the playfield origin.</summary>
            public object Adopt(Body b, BodyMsg m)
            {
                if (S.live.ContainsKey(m.Id) || m.Entity <= 0) return null;
                var p = BodyProgress.Read(m.Progress);
                var l = new Live { Agent = m.Id, Kind = BodyProgress.Get(p, "kind"), Target = BodyProgress.Int(p, "target"), Since = S.fly.Now };
                if (l.Kind.Length == 0) l.Kind = "trader";
                l.B = b; b.Kind = "ship"; b.Entity = m.Entity; b.Offline = false;
                if (m.HasPos) l.Dest = new Vector3(m.X, m.Y, m.Z);
                else if (S.Pf?.Entities != null && S.Pf.Entities.TryGetValue(m.Entity, out var here) && here != null) l.Dest = here.Position;
                foreach (var kv in BodyProgress.Get(p, "mem").Split(';').Select(x => x.Split(new[] { '=' }, 2)).Where(x => x.Length == 2)) l.Memory[kv[0]] = kv[1];
                S.live[m.Id] = l;
                S.log($"SIM {m.Id} ({l.Kind}) re-adopted as {m.Entity}, spot {l.Dest}");
                return l;
            }
        }
    }
}
