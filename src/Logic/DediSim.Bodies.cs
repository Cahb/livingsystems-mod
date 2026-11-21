using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core.Sim;

namespace LivingSystems
{
    /// <summary>
    /// One body, many brains — the Dedi half: one router for the body messages (<see cref="BodyMsg"/>). The spawn is the
    /// same for every kind; what the record does with "back" / "live" / "dead" is the kind's <see cref="Keeper"/>, and so is
    /// what goes into an adopt after a reload.
    /// </summary>
    sealed partial class DediSim
    {
        abstract class Keeper
        {
            protected readonly DediSim D;
            protected Keeper(DediSim d) { D = d; }
            public abstract string Kind { get; }
            /// <summary>May the PfServer's spawn go ahead (the record still wants a hull there)?</summary>
            public virtual bool MaySpawn(string id) => true;
            /// <summary>The spawn the PfServer asked for landed as <paramref name="entity"/>.</summary>
            public virtual void Spawned(string id, int entity, string playfield) { }
            /// <summary>Its old hull taken back (live again).</summary>
            public virtual void Live(string id, int entity, string playfield) { }
            /// <summary>Out of range / view: the sim has it (hold, pos, progress on the record).</summary>
            public virtual void Back(BodyMsg m, string playfield) { }
            /// <summary>Dead by evidence (<see cref="BodyMsg.Why"/>: destroyed / claimed / nospawn); <see cref="BodyMsg.Hold"/> = what it carried.</summary>
            public virtual void Dead(BodyMsg m, string playfield) { }
            /// <summary>After a reload: the bodies of this kind the PfServer on <paramref name="playfield"/> picks up again.</summary>
            public virtual IEnumerable<BodyMsg> Adopts(string playfield) { yield break; }
        }

        Dictionary<string, Keeper> keepers;
        Dictionary<string, Keeper> Keepers
        {
            get
            {
                if (keepers != null) return keepers;
                keepers = new Dictionary<string, Keeper>(StringComparer.Ordinal);
                foreach (var k in new Keeper[] { new HireKeeper(this), new ShipKeeper(this), new WreckKeeper(this) }) keepers[k.Kind] = k;
                return keepers;
            }
        }

        void SendBody(string pf, BodyMsg m) => Send(pf, m.Fields());

        /// <summary>bodyspawn / bodylive / bodyback / bodydead from a PfServer. True if it was one.</summary>
        bool TryBody(string[] f, string playfield)
        {
            if (f.Length < 3 || !f[0].StartsWith("body", StringComparison.Ordinal)) return false;
            var m = BodyMsg.Parse(f);
            if (m == null) return false;
            if (!Keepers.TryGetValue(m.Kind, out var k)) { log($"sim: body message for unknown kind '{m.Kind}' ({m.Verb} {m.Id})"); return true; }
            switch (m.Verb)
            {
                case "bodyspawn":
                {
                    if (!k.MaySpawn(m.Id)) { log($"sim: body {m.Kind} {m.Id}: spawn not wanted any more"); return true; }
                    var pf = playfield;
                    dedi.Spawn(pf, m.Prefab, new PVector3(m.X, m.Y, m.Z), new PVector3(0, m.Yaw, 0), m.Name, (byte)Math.Max(0, Math.Min(255, m.Faction)), id =>
                    {
                        SendBody(pf, BodyMsg.Spawned(m.Kind, m.Id, id));
                        k.Spawned(m.Id, id, pf);
                        log($"sim: body {m.Kind} {m.Id} spawned as {id} in '{pf}'");
                    });
                    return true;
                }
                case "bodylive": k.Live(m.Id, m.Entity, playfield); return true;
                case "bodyback": k.Back(m, playfield); return true;
                case "bodydead": k.Dead(m, playfield); return true;
            }
            return false;
        }

        /// <summary>simhello: every kind's bodies on that playfield, handed back.</summary>
        void AdoptBodies(string pf)
        {
            foreach (var k in Keepers.Values)
                foreach (var m in k.Adopts(pf))
                {
                    SendBody(pf, m);
                    log($"sim: body {m.Kind} {m.Id} re-adopted by '{pf}' {(m.Entity > 0 ? "as " + m.Entity : "out of range")}");
                }
        }

        // ---- sim ships (kind "ship", id = agent): traders, pirates, salvage tugs -------------------------------------

        sealed class ShipKeeper : Keeper
        {
            public ShipKeeper(DediSim d) : base(d) { }
            public override string Kind => "ship";
            public override bool MaySpawn(string id) => D.world.Get(id) is SimAgent a && a.IsLive;

            public override void Spawned(string id, int entity, string pf)
            {
                var a = D.world.Get(id);
                if (a == null) return;
                a.EntityId = entity; D.asked.Remove(a.Id); D.Save();
                D.log($"sim: {a.Id} '{a.Name}' spawned as {entity} in '{pf}'");
            }

            /// <summary>Back to the sim ("range" / "view" / "nospawn" = left; "arrived", "jumped", "fled", "left" as the brain
            /// said): its lane share, hull and (a tug's) real hold on the record.</summary>
            public override void Back(BodyMsg m, string pf)
            {
                var how = m.Why == "range" || m.Why == "view" || m.Why == "nospawn" ? "left" : m.Why;
                // a hull left behind (out of view, or the PfServer switched playfields under it): nobody's now — the sim spawns
                // a fresh one next time, never two of it
                D.DestroyLeft(m.Ghost, D.world.Get(m.Id)?.System);
                End(m.Id, how, m.Progress, m.Hold, true);
            }

            /// <summary>"destroyed" (core shot out: its hull stays, a wreck) / "claimed" (a player cored it) / "nospawn".</summary>
            public override void Dead(BodyMsg m, string pf) =>
                End(m.Id, m.Why == "destroyed" ? "dead" : m.Why == "claimed" ? "claimed" : "left", "", m.Hold, true);   // a dead tug's wreck gets its real hold

            void End(string id, string how, string progress, string hold, bool withHold)
            {
                var a = D.world.Get(id);
                if (a == null) return;
                var p = BodyProgress.Read(progress);
                var frac = p.ContainsKey("frac") ? BodyProgress.Num(p, "frac") : -1;
                var hull = p.ContainsKey("hull") ? BodyProgress.Num(p, "hull") : -1;
                var before = D.world.Events.Count;
                D.leftLive[a.Id] = Now;
                if (withHold && hold.Length > 0) D.world.SetHold(a, hold);   // a tug: what's really in its hold
                D.world.EndLive(a.Id, how, Now, frac, hull); D.asked.Remove(a.Id);
                var happened = D.world.Events.Skip(before).ToList();   // e.g. "Brass Goose was destroyed near SNUO" — announce it
                foreach (var e in happened) D.log("sim: " + e.Text);
                D.Dispatch(happened, D.PlayerPlayfields());
                D.log($"sim: {a.Id} '{a.Name}' back in the sim ({how}{(frac >= 0 ? $", {frac:P0} along its lane" : "")}{(hull >= 0 ? $", {hull:P0} of its blocks" : "")}{(a.Fight.Length > 0 ? $", still in {a.Fight}" : "")})");
                D.Save();
                if (a.System.Length > 0) D.SendTraffic(a.System);
            }

            /// <summary>After a reload: every live ship with a hull here — its kind, its prey, its spot (lurkers / hub crews,
            /// else it flies off to the origin), its memories.</summary>
            public override IEnumerable<BodyMsg> Adopts(string pf)
            {
                foreach (var a in D.world.Agents.Values.Where(x => x.Live == pf && x.EntityId > 0).ToList())
                {
                    var fight = D.world.FightOf(a);
                    var target = a.IsTrader || fight == null ? 0 : D.world.MembersOf(fight).FirstOrDefault(t => t.IsTrader && t.IsLive && t.EntityId > 0)?.EntityId ?? 0;
                    var spt = default(SimTrack);
                    var tug = a.IsTug && (a.State == "salvage" || (a.State == "fight" && a.After == "salvage"));
                    var spotOk = !tug && !a.IsTrader && (a.State == "lurk" || a.State == "home") && D.world.Track(a, out spt);
                    var kv = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("kind", tug ? "tug" : a.Kind),
                        new KeyValuePair<string, string>("target", tug || target == 0 ? "" : target.ToString(CultureInfo.InvariantCulture)),
                        new KeyValuePair<string, string>("mem", D.MemoryLine(a)),
                    };
                    yield return BodyMsg.Adopt("ship", a.Id, a.EntityId, 0, spotOk, (float)spt.A.X, (float)spt.A.Y, (float)spt.A.Z, "", BodyProgress.Write(kv));
                }
            }
        }

        // ---- wrecks and hub hulls (kind "wreck", id = the wreck record) ---------------------------------------------

        static string WreckMode(SimWreck w) => w.IsHub ? (w.Debris ? "debris" : w.Centre && w.Shown == "post" ? "post" : w.Chest ? "chest" : "hub") : "";

        /// <summary>A fresh hull the Dedi just spawned for a wreck record: strip it, batter it, load its hold.</summary>
        BodyMsg WreckFill(SimWreck wr, int entity, bool debris, bool prewrecked, string mode)
        {
            var kv = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("fill", "1"),
                new KeyValuePair<string, string>("item", debris ? "" : wr.Cargo),
                new KeyValuePair<string, string>("amt", debris ? "" : wr.Amt.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("pre", prewrecked ? "1" : ""),
                new KeyValuePair<string, string>("mode", mode),
                new KeyValuePair<string, string>("cash", debris || wr.Cash <= 0 ? "" : wr.Cash.ToString(CultureInfo.InvariantCulture)),
                new KeyValuePair<string, string>("cut", world.CutShare(wr, Now).ToString("0.###", CultureInfo.InvariantCulture)),
                // been live and cleared: its own stores are in its hold already (fresh ones each respawn = a farm)
                new KeyValuePair<string, string>("nostores", wr.CashAboard ? "1" : ""),
            };
            return BodyMsg.Adopt("wreck", wr.Id, entity, 0, true, wr.Pos.X, wr.Pos.Y, wr.Pos.Z, debris ? "" : World.FormatHold(World.HoldOf(wr)), BodyProgress.Write(kv));
        }

        /// <summary>A hull already there (after a reload, or a live death): tracked as this record; money it's still owed.</summary>
        BodyMsg WreckAdopt(SimWreck w, bool readopt)
        {
            var kv = new List<KeyValuePair<string, string>>
            {
                new KeyValuePair<string, string>("mode", WreckMode(w)),
                new KeyValuePair<string, string>("cashdue", World.CashOwed(w) ? w.Cash.ToString(CultureInfo.InvariantCulture) : ""),
                new KeyValuePair<string, string>("cut", w.Cut > 0 ? w.Cut.ToString("0.###", CultureInfo.InvariantCulture) : ""),
            };
            return BodyMsg.Adopt("wreck", w.Id, w.Entity, 0, true, w.Pos.X, w.Pos.Y, w.Pos.Z, "", BodyProgress.Write(kv));
        }

        sealed class WreckKeeper : Keeper
        {
            public WreckKeeper(DediSim d) : base(d) { }
            public override string Kind => "wreck";

            /// <summary>Its hull is down. Gone ≠ dead: the record stays (a hub hull respawns as what it should be; a beacon
            /// wreck is a beacon again, with what its crates really held — or, gone from view unseen, emptied).</summary>
            public override void Back(BodyMsg m, string pf)
            {
                if (!D.world.Wrecks.TryGetValue(m.Id, out var w)) return;
                var why = m.Why; var ent = w.Entity;
                var p = BodyProgress.Read(m.Progress);
                if (w.Centre && w.Post && w.IsHub && D.world.Hubs.TryGetValue(w.Hub, out var ph) && !D.world.IsBroken(ph))
                {   // an active gang's post: a whole one comes back (only while nobody's near)
                    w.Entity = 0; w.Shown = "";
                    D.Save(); D.log($"sim: {ph.Name}'s listening post {ent} down ({why}) — a whole one comes back"); D.SendTraffic(pf);
                    return;
                }
                if (w.IsHub)
                {
                    D.world.HubHullDown(w, lost: why == "view");
                    if (D.world.Hubs.TryGetValue(w.Hub, out var hh0)) D.world.HubCaps(hh0, Now);
                    D.Save(); D.log($"sim: hub wreck {w.Id} '{w.Name}' hull {ent} down ({why}) — {(D.world.Wrecks.ContainsKey(w.Id) ? "respawns as " + (w.Debris ? "debris" : "wreck") : "gone")}"); D.SendTraffic(pf);
                    return;
                }
                w.Entity = 0; w.Shown = "";
                if (w.Spill)
                {   // a sack: what's in it is all it ever holds (no stores to farm) — read: that's the record; emptied: gone
                    D.DestroyLeft(m.Ghost, w.System);
                    if (BodyProgress.Get(p, "read") == "1") World.SetWreckHold(w, World.ParseHold(m.Hold));
                    if (World.HoldOf(w).Count == 0) { D.world.Wrecks.Remove(w.Id); D.Save(); D.log($"sim: spill {w.Id} '{w.Name}' emptied — gone"); D.SendTraffic(w.System); return; }
                    D.Save(); D.log($"sim: spill {w.Id} '{w.Name}' hull {ent} down ({why}) — {World.HoldOf(w).Count} line(s) still in it");
                    D.SendTraffic(w.System);
                    return;
                }
                // out of view / a playfield switch: the old hull may linger in the save — gone, so a return spawns one hull, not two
                D.DestroyLeft(m.Ghost, w.System);
                if (p.ContainsKey("cut")) w.Cut = Math.Max(w.Cut, BodyProgress.Num(p, "cut"));
                if (BodyProgress.Get(p, "read") == "1")
                {   // cleared (or out of view, last read): what its crates held is the record now (cash included, as MoneyCard)
                    var before = World.HoldOf(w).Sum(kv => World.Value(kv.Key, kv.Value)) + w.Cash;
                    World.SetWreckHold(w, World.ParseHold(m.Hold));
                    w.Cash = 0; w.CashAboard = true;
                    var after = World.HoldOf(w).Sum(kv => World.Value(kv.Key, kv.Value));
                    if (before > after) D.world.Count("eco|burn|wreck loot taken live", before - after);
                }
                else if (why == "view" && w.Tug.Length == 0)
                {   // out of view before its crates were ever read: never back with its whole load (a loot farm)
                    D.world.Count("eco|burn|wrecks gone live (looted or cleared)", World.HoldOf(w).Sum(kv => World.Value(kv.Key, kv.Value)) + w.Cash);
                    World.SetWreckHold(w, null); w.Cash = 0;
                }
                D.Save(); D.log($"sim: wreck {w.Id} '{w.Name}' hull {ent} down ({why}) — a beacon again{(w.Tug.Length > 0 ? ", a tug's on it" : "")}, {World.HoldOf(w).Count} line(s) aboard");
                D.SendTraffic(w.System);
            }

            /// <summary>"claimed" (a player's core) / "destroyed" (blown up with a player there): the record goes for good.</summary>
            public override void Dead(BodyMsg m, string pf)
            {
                var id = m.Id; var how = m.Why;
                if (!D.world.Wrecks.TryGetValue(id, out var w)) return;
                var ent = w.Entity;
                if (w.Centre && w.Post && w.IsHub && D.world.Hubs.TryGetValue(w.Hub, out var ph) && !D.world.IsBroken(ph))
                {   // an active gang's post is never lost: a whole one back, not in front of whoever took it (half an hour)
                    w.Entity = 0; w.Shown = ""; w.NotBefore = Now + 1800;
                    D.Save(); D.log($"sim: {ph.Name}'s listening post {ent} {how} — a whole one comes back in 30 min"); D.SendTraffic(pf);
                    return;
                }
                D.world.Wrecks.Remove(w.Id); D.world.Count("eco|burn|wrecks gone live (looted or cleared)", w.Cash);
                D.Save(); D.log($"sim: wreck {w.Id} '{w.Name}' gone ({how})");
                if (w.Tug.Length > 0 && D.world.Get(w.Tug) is SimAgent tug)
                {   // a tug's target taken by a player (cored / blown up): next on its list, or nothing left here → home
                    if (tug.IsLive && D.world.TugJob(tug) == null && D.world.TugSendHome(tug, Now)) D.Send(tug.Live, "tugleave", tug.Id, D.Where(tug.System, tug.Extra.TryGetValue("tughome", out var th) ? th : ""), "beaten");
                    D.SendTraffic(w.System);
                }
            }

            public override IEnumerable<BodyMsg> Adopts(string pf)
            {
                foreach (var w in D.world.Wrecks.Values.Where(x => x.System == pf && x.Entity > 0).ToList()) yield return D.WreckAdopt(w, true);
            }
        }

        // ---- hired tugs (kind "hire", id "h<pid>") -----------------------------------------------------------------

        sealed class HireKeeper : Keeper
        {
            public HireKeeper(DediSim d) : base(d) { }
            public override string Kind => "hire";
            static int Pid(string id) => id.Length > 1 && int.TryParse(id.Substring(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out var p) ? p : 0;

            public override void Spawned(string id, int entity, string pf)
            {
                var pid = Pid(id);
                D.dedi.OnHired(pid, entity);
                var a = D.world.HiredBy(pid);
                if (a != null) { D.world.HireOnline(a, entity); D.Save(); }
            }

            public override void Live(string id, int entity, string pf)
            {
                var pid = Pid(id);
                var a = D.world.HiredBy(pid);
                if (a == null) return;
                D.world.HireOnline(a, entity); D.Save();
                D.dedi.OnHired(pid, entity);
                D.log($"sim: hired tug {a.Id} '{a.Name}' of {pid} live again as {entity}");
            }

            public override void Back(BodyMsg m, string pf)
            {
                var pid = Pid(m.Id);
                var a = D.world.HiredBy(pid);
                if (a == null) return;
                var p = BodyProgress.Read(m.Progress);
                var tgt = BodyProgress.Int(p, "tgt"); var share = BodyProgress.Num(p, "share"); var tname = BodyProgress.Get(p, "tname");
                var pos = m.HasPos ? string.Join(",", new[] { m.X, m.Y, m.Z }.Select(v => v.ToString("0.0", CultureInfo.InvariantCulture))) : "";
                D.world.HireOffline(a, m.Hold, pos, tgt, tname, share, BodyProgress.Get(p, "work"), m.Ghost, Now);
                if (BodyProgress.Get(p, "follow") == "1") a.Extra["hirefollow"] = "1"; else a.Extra.Remove("hirefollow");
                D.Save();
                D.dedi.HireStat(pid, tgt > 0 ? $"out of range — still cutting {tname} ({share:P0} when you left); back live within 2 km" : "out of range — waiting where you left it; back live within 2 km");
                D.log($"sim: hired tug {a.Id} '{a.Name}' of {pid} out of range ({m.Why}) at {pos} — the sim holds it{(tgt > 0 ? $", cutting {tname} from {share:P0}" : "")}{(m.Ghost > 0 ? $" (hull {m.Ghost} may still be there)" : "")}");
            }

            public override void Dead(BodyMsg m, string pf) => D.dedi.HireLost(Pid(m.Id), m.Why == "nospawn");

            public override IEnumerable<BodyMsg> Adopts(string pf)
            {
                foreach (var a in D.world.Agents.Values.Where(x => x.IsTug && x.State == "hired" && x.System == pf && (World.HireEntity(x) > 0 || World.HireOff(x))).ToList())
                {
                    string X(string k) => a.Extra.TryGetValue(k, out var v) ? v : "";
                    var off = World.HireOff(a);
                    var kv = new List<KeyValuePair<string, string>>
                    {
                        new KeyValuePair<string, string>("pid", World.HirePid(a).ToString(CultureInfo.InvariantCulture)),
                        new KeyValuePair<string, string>("name", a.Name),
                        new KeyValuePair<string, string>("home", X("tughome")),
                    };
                    if (off)
                    {
                        kv.Add(new KeyValuePair<string, string>("tgt", World.HireTarget(a) != 0 ? World.HireTarget(a).ToString(CultureInfo.InvariantCulture) : ""));
                        kv.Add(new KeyValuePair<string, string>("share", World.HireTarget(a) != 0 ? World.HireShare(a, Now).ToString("R", CultureInfo.InvariantCulture) : ""));
                        kv.Add(new KeyValuePair<string, string>("work", X("hirework")));
                        kv.Add(new KeyValuePair<string, string>("tname", X("hiretgtname")));
                    }
                    var pos = X("hirepos").Split(',');
                    var hasPos = off && pos.Length == 3;
                    float P(int i) => hasPos && float.TryParse(pos[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;
                    yield return BodyMsg.Adopt("hire", "h" + World.HirePid(a).ToString(CultureInfo.InvariantCulture), off ? 0 : World.HireEntity(a), off ? World.HireEntity(a) : 0,
                                               hasPos, P(0), P(1), P(2), off ? X(World.HoldKey) : "", BodyProgress.Write(kv));
                }
            }
        }

        /// <summary>
        /// Out-of-range hired tugs whose player warped away while it was following them (nothing to cut): it follows — the
        /// warp takes as long as any ship's, then it's dropped from the old playfield and comes in near the player in the new
        /// one (its hold aboard). Checked each sim step.
        /// </summary>
        void FollowHires()
        {
            foreach (var a in world.Agents.Values.Where(x => x.IsTug && x.State == "hired" && World.HireOff(x) && x.Extra.ContainsKey("hirefollow")).ToList())
            {
                var pid = World.HirePid(a);
                string pf; try { pf = api.Application.GetPlayerDataFor(pid)?.PlayfieldName ?? ""; } catch { pf = ""; }
                string X(string k) => a.Extra.TryGetValue(k, out var v) ? v : "";
                if (pf.Length == 0 || pf == a.System) { if (X("hirewarp").Length > 0) { a.Extra.Remove("hirewarp"); a.Extra.Remove("hirewarpto"); Save(); } continue; }
                if (X("hirewarpto") != pf)
                {
                    var eta = world.WarpTime(a.System, pf); if (double.IsNaN(eta) || double.IsInfinity(eta) || eta <= 0) eta = 120;
                    a.Extra["hirewarpto"] = pf; a.Extra["hirewarp"] = (Now + eta).ToString("R", CultureInfo.InvariantCulture);
                    Save();
                    dedi.Say(pid, $"{a.Name}: you jumped — we're right behind you. Out of warp at {Where(a.System, pf)} in about {Math.Max(1, Math.Round(eta / 60)):0} min.");
                    log($"sim: hired tug {a.Id} '{a.Name}' follows {pid} from '{a.System}' to '{pf}' ({eta:0} s)");
                    continue;
                }
                if (!double.TryParse(X("hirewarp"), NumberStyles.Float, CultureInfo.InvariantCulture, out var at) || Now < at) continue;
                var from = a.System;
                SendBody(from, BodyMsg.Drop("hire", "h" + pid.ToString(CultureInfo.InvariantCulture)));
                DestroyLeft(World.HireEntity(a), from);
                foreach (var k in new[] { "hirewarp", "hirewarpto", "hirepos", "hiretgt", "hiretgtname", "hireshare", "hirework", "hireent" }) a.Extra.Remove(k);
                a.System = pf;
                Save();
                var kv = new List<KeyValuePair<string, string>>
                {
                    new KeyValuePair<string, string>("pid", pid.ToString(CultureInfo.InvariantCulture)),
                    new KeyValuePair<string, string>("name", a.Name),
                    new KeyValuePair<string, string>("home", X("tughome")),
                };
                SendBody(pf, BodyMsg.Adopt("hire", "h" + pid.ToString(CultureInfo.InvariantCulture), 0, 0, false, 0, 0, 0, X(World.HoldKey), BodyProgress.Write(kv)));
                dedi.Say(pid, $"{a.Name}: out of warp — coming in to you.");
                log($"sim: hired tug {a.Id} '{a.Name}' followed {pid} from '{from}' to '{pf}'");
            }
        }

        double hireClockAt;

        /// <summary>
        /// The hire share clock (World.HireTick): each hire's seconds run while its hirer is online; one heads-up in chat
        /// before each step of the yard's cut (10 min ahead), never repeated for the same step.
        /// </summary>
        void HireClocks()
        {
            var now = Now; var dt = hireClockAt > 0 ? now - hireClockAt : 0; hireClockAt = now;
            if (dt <= 0) return;
            var changed = false;
            List<int> online; try { online = (api.Application.GetPlayerEntityIds() ?? new List<int>()).ToList(); } catch { return; }
            foreach (var a in world.Agents.Values.Where(x => x.IsTug && x.State == "hired").ToList())
            {
                var pid = World.HirePid(a);
                if (pid <= 0 || !online.Contains(pid)) continue;
                World.HireTick(a, dt); changed = true;
                var inS = world.NextCutIn(a, out var next);
                if (double.IsInfinity(inS) || inS > 600) continue;
                var step = next.ToString("0.00", CultureInfo.InvariantCulture);
                if (a.Extra.TryGetValue("hirestep", out var said) && said == step) continue;
                a.Extra["hirestep"] = step;
                var keep = world.HirePayout(a);
                dedi.Say(pid, $"{a.Name}: heads-up — the yard's cut of the sale goes {1 - keep:P0} → {next:P0} in {Math.Max(1, Math.Round(inS / 60)):0} min. Send us home before then and you keep {keep:P0}.");
                log($"sim: hire {a.Id} '{a.Name}' of {pid}: yard's cut {1 - keep:P0} → {next:P0} in {inS:0} s (said)");
            }
            if (changed && (long)(now / 60) != (long)((now - dt) / 60)) Save();   // the clock is saved about once a minute
        }

        /// <summary>The hirer's share of the sale if their tug went home now (the clock; base when there's no sim tug).</summary>
        public double HireShare(int pid) => world.HiredBy(pid) is SimAgent a ? world.HirePayout(a) : 1 - world.Config.HireCutBase;

        /// <summary>The clock for the tug's page: "yard's cut 50 % → 60 % in 25 min" ("" = no hire).</summary>
        public string HireClockLine(int pid)
        {
            if (!(world.HiredBy(pid) is SimAgent a)) return "";
            var cut = 1 - world.HirePayout(a);
            var inS = world.NextCutIn(a, out var next);
            return double.IsInfinity(inS) ? $"yard's cut {cut:P0} (the most it takes)"
                                          : $"yard's cut {cut:P0} → {next:P0} in {Math.Max(1, Math.Round(inS / 60)):0} min";
        }

        /// <summary>The player's hired tug is held by the sim (out of range) right now.</summary>
        public bool HireIsOffline(int pid) => world.HiredBy(pid) is SimAgent a && World.HireOff(a);

        /// <summary>Dismissed while out of range (or in another playfield): the sim has its hold — home it goes from where it
        /// is; the playfield holding it forgets it. False = it's live (its PfServer handles the dismiss).</summary>
        public bool DismissOffline(int pid)
        {
            var a = world.HiredBy(pid);
            if (a == null || !World.HireOff(a)) return false;
            var hold = a.Extra.TryGetValue(World.HoldKey, out var h) ? h : "";
            SendBody(a.System, BodyMsg.Drop("hire", "h" + pid.ToString(CultureInfo.InvariantCulture)));
            DestroyLeft(World.HireEntity(a), a.System);
            log($"sim: hired tug {a.Id} '{a.Name}' dismissed out of range with {hold}");
            dedi.HireDone(pid, hold);   // → HireEnd → World.HireEnds: home to sell, the share on the sale
            return true;
        }

    }
}
