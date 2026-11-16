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
    /// Escort run v1, live only (PLAN.md START HERE). PfServer side. A trader's trip leaves the system: it flies to its
    /// warp-out point (4–8 km off, open space), spools its warp drive and jumps. The run to the warp point is the
    /// dangerous part. Straight path only:
    ///   hail → "Need an escort?" → offer (fee, quoted risk) → accept: joins your faction, flies to its warp point
    ///   decline: makes the same run alone (a real agent, never spawn/vanish; pirates may still hit it)
    ///   mid-run, if the roll said so: pirates spawn ~1.6 km out (inside the physics bubble) and close on the trader
    ///   warp point → spool → (escorted: credits, back to own colours) → jumps out; record kept with trip.dest
    ///   trader gone → failed · trader fleeing (you shot it) → over.
    /// NOT handled in v1: leaving the escort (no penalty), reload mid-contract (contract forgotten, the trip itself is
    /// a persisted goto), a trader beyond the physics bubble (freezes), appearing in the next system (stages 2–3).
    ///
    /// Wire: Dedi → us  "pid|fly|escort|offer|shipId", "…|accept|shipId", "…|decline|shipId",
    ///                  "0|fly|escort|spawned|traderId|pirateId"
    ///       us → Dedi  "escortoffer|pid|shipId|text", "orderreply|pid|shipId|text|status",
    ///                  "escortspawn|traderId|prefab|x|y|z|yaw", "pay|pid|credits"
    /// </summary>
    sealed class EscortRun
    {
        readonly FlightDriver fly;
        readonly Action<string> log;
        readonly System.Random rng = new System.Random();

        sealed class Trip
        {
            public int Ship, Pid;
            public bool Escorted;
            public double OfferedAt;
            public string PlayerKey = "", Destination = "the next system";
            public Vector3 WarpPoint;
            public float TripLen;
            public EscortTerms Terms;
            public bool PiratesCalled;
            public double SpoolUntil = -1;
            public int Relocations;
            public double AloneSince = -1;   // no player within the bubble since (escorted only)
            public readonly List<int> Pirates = new List<int>();
            public readonly Dictionary<int, double> PendingPirates = new Dictionary<int, double>();   // id → when spawned
        }
        readonly Dictionary<int, Trip> offers = new Dictionary<int, Trip>();   // by ship
        readonly Dictionary<int, Trip> trips = new Dictionary<int, Trip>();    // under way, by ship
        double nextTick;

        const float MinTrip = 4000f, MaxTrip = 8000f;
        const float ArriveRange = 250f;     // at the warp point
        const double SpoolSeconds = 10;
        const float NewPointRange = 2500f;
        const float PirateRange = 1600f;    // spawn distance from the trader: inside the ~2 km physics bubble, so they fly
        const float PirateStandoff = 300f;
        const float AbandonRange = 2500f;   // the physics bubble (SimLive uses the same)
        const double AbandonSeconds = 30;

        /// <summary>Where a ship is really bound (set by SimLive for sim traders); null = unknown.</summary>
        public Func<int, string> BoundFor;

        public EscortRun(FlightDriver fly, Action<string> log) { this.fly = fly; this.log = log; }

        public void Reset() { offers.Clear(); trips.Clear(); }

        /// <summary>
        /// The escort paid off the pirate that was on its own contract ship: the risk it was hired for is gone, so is half
        /// the fee (no "take the escort, buy off the attacker, pocket the difference").
        /// </summary>
        public void AttackerPaidOff(int traderShip, int pid)
        {
            if (!trips.TryGetValue(traderShip, out var t) || !t.Escorted || t.Pid != pid) return;
            t.Terms.Fee = (int)(Math.Round(t.Terms.Fee / 2.0 / 50) * 50);
            fly.Say(pid, $"{Name(traderShip)}: You bought them off? Fine — but we hired guns, not a wallet. Half the fee: {t.Terms.Fee:N0} cr.");
            log($"ESCORT {traderShip}: attacker paid off by {pid}, fee halved to {t.Terms.Fee}");
        }
        /// <summary>An escort trip under way owns this ship — other drivers keep their hands off.</summary>
        public bool Owns(int ship) => trips.ContainsKey(ship);   // an open offer doesn't: the ship keeps flying its own way

        IPlayfield Pf => fly.Playfield;

        /// <summary>Test switch (/ls fly escort hot): the next offer is Hot with a guaranteed attack.</summary>
        bool forceHot;

        public void Handle(int pid, string[] a)
        {
            if (a.Length >= 1 && a[0] == "hot") { forceHot = true; fly.Say(pid, "next escort offer: hot lane, pirates guaranteed"); return; }
            if (a.Length < 2 || !int.TryParse(a[1], out var ship)) return;
            switch (a[0])
            {
                case "offer": Offer(pid, ship); break;
                case "accept": Start(pid, ship, true); break;
                case "decline": Start(pid, ship, false); break;
                case "spawned":
                    if (a.Length >= 3 && int.TryParse(a[2], out var pirate) && trips.TryGetValue(ship, out var t))
                        t.PendingPirates[pirate] = fly.Now;
                    break;
            }
        }

        void Answer(int pid, int ship, string text) =>
            fly.Send("orderreply", pid, ship, text, fly.StatusOf(ship, pid));

        /// <summary>What this ship remembers about a player's escorts ("abandoned", "done", …; null = nothing). Set by SimLive.</summary>
        public Func<int, string, string> Memory;
        /// <summary>A sim trader's real warp point (end of its lane); null = not a sim trader (pick one). Set by SimLive.</summary>
        public Func<int, Vector3?> WarpPointFor;
        /// <summary>A sim trader's answer to "need an escort?" this leg (null = not a sim trader: always open to it). Set by SimLive.</summary>
        public Func<int, EscortAsk> AskFor;
        public string KeyFor(int pid) => "memory.escort." + LeaderKey(pid);

        void Offer(int pid, int ship)
        {
            if (Pf?.Entities == null || !Pf.Entities.TryGetValue(ship, out var e) || e == null || !fly.IsOurs(ship)) { Answer(pid, ship, "We don't take contracts."); return; }
            if (fly.IsFleeing(ship)) { Answer(pid, ship, "Not now!"); return; }
            if (fly.IsCombatant(ship)) { Answer(pid, ship, "Escort? Ha! Keep your nose out of our business."); return; }   // pirates don't take contracts
            // one contract per ship: a second captain asking gets told (no overwriting the first one's terms)
            if (trips.TryGetValue(ship, out var running)) { Answer(pid, ship, running.Escorted && running.Pid != pid ? $"We've got an escort already — {PlayerName(running.Pid)}." : "Already under way."); return; }
            if (offers.TryGetValue(ship, out var pending) && pending.Pid != pid && fly.Now - pending.OfferedAt < 120) { Answer(pid, ship, $"We're settling terms with {PlayerName(pending.Pid)} right now. Ask again in a bit."); return; }
            var mem = Memory?.Invoke(ship, KeyFor(pid));
            if (mem == "abandoned") { Answer(pid, ship, "You? You left us to the pirates last time. Find someone else to fool."); return; }
            if (fly.Distrusts(ship, pid)) { Answer(pid, ship, "After you shot at us? No contract."); return; }
            // situational: a sim trader decides by its leg, hold, guns and the lane's reports (fixed per leg)
            var ask = AskFor?.Invoke(ship);
            if (ask != null && !ask.Wants) { Answer(pid, ship, ask.Line); log($"ESCORT {ship} declines: {ask.Line}"); return; }
            var len = MinTrip + (float)rng.NextDouble() * (MaxTrip - MinTrip);
            var dir = UnityEngine.Random.onUnitSphere; dir.y *= 0.2f; dir.Normalize();   // mostly level
            // the risk is what the sim's traders have reported on this playfield, not a die roll (once the Dedi told us)
            var terms = fly.LaneRisk.HasValue ? Escort.Roll(rng, len, fly.LaneRisk.Value) : Escort.Roll(rng, len);
            if (forceHot) { forceHot = false; do terms = Escort.Roll(rng, len, Risk.Hot); while (!terms.Attack); terms.AttackAt = 0.3; }
            var lane = WarpPointFor?.Invoke(ship);
            if (lane.HasValue) { len = Mathf.Max(500f, (lane.Value - e.Position).magnitude); terms = fly.LaneRisk.HasValue ? Escort.Roll(rng, len, fly.LaneRisk.Value) : Escort.Roll(rng, len); }
            if (ask != null && ask.FeeFactor > 1) terms.Fee = (int)(Math.Round(terms.Fee * ask.FeeFactor / 50.0) * 50);
            var t = new Trip { Ship = ship, Pid = pid, WarpPoint = lane ?? e.Position + dir * len, TripLen = len, Terms = terms };
            var bound = BoundFor?.Invoke(ship);   // a sim trader: its real destination
            if (!string.IsNullOrEmpty(bound)) t.Destination = bound;
            t.OfferedAt = fly.Now;
            offers[ship] = t;
            log($"ESCORT offer {ship} '{e.Name}' → warp point {t.WarpPoint} {len:0} m, {terms.Risk}, {terms.Fee} cr, attack={terms.Attack}" +
                (terms.Attack ? $" at {terms.AttackAt:0.00} by {string.Join(",", terms.Pirates)}" : ""));
            var text = (mem == "done" ? "You again — good. " : "") + (ask != null ? ask.Line + " " : "") + $"We're bound for {t.Destination}. Our warp point is {len / 1000f:0.0} km out. {Escort.RiskLine(terms.Risk)}\n" +
                       $"See us safely to the jump and we pay {terms.Fee:N0} cr. We fly under your colours until then.";
            fly.Send("escortoffer", pid, ship, text);
        }

        void Start(int pid, int ship, bool escorted)
        {
            if (!offers.TryGetValue(ship, out var t) || t.Pid != pid) { if (escorted) Answer(pid, ship, "What contract?"); return; }
            offers.Remove(ship);
            // a sim trader that's turned down just keeps flying its lane (SimLive has it); taking it over left it stranded
            // as a frozen proxy when the player flew off (its fight stuck "live")
            if (!escorted && WarpPointFor?.Invoke(ship) != null) { Answer(pid, ship, "Suit yourself. We're going anyway."); return; }
            if (escorted) fly.Command(pid, ship, "join");
            if (!fly.GoTo(ship, t.WarpPoint, "warp point", 0f)) { Answer(pid, ship, "Lost you. Never mind."); return; }
            t.Escorted = escorted;
            t.PlayerKey = LeaderKey(pid);
            trips[ship] = t;
            if (escorted) fly.AddEvent($"{Name(ship)}: escort contract with {PlayerName(pid)}");
            log($"ESCORT {(escorted ? "accepted" : "declined")} {ship} by {pid}");
            Answer(pid, ship, escorted ? "Deal. Heading for our warp point. Stay close." : "Suit yourself. We're going anyway.");
        }

        public void Tick(double now)
        {
            if (Pf == null || now < nextTick) return;
            nextTick = now + 1;
            try { SweepOrphans(now); } catch (Exception ex) { log($"ESCORT orphan sweep threw: {ex.Message}"); }
            foreach (var t in trips.Values.ToList())
                try { Step(t, now); } catch (Exception ex) { log($"ESCORT {t.Ship} step threw: {ex.Message}"); }
        }

        /// <summary>
        /// Escort pirates outlive their trip after a reload / crash (trips are memory only): a tagged "Raider" no trip owns
        /// is taken down once no player has been within the bubble for <see cref="OrphanSeconds"/> (alive or a hulk).
        /// </summary>
        void SweepOrphans(double now)
        {
            if (now < nextOrphanSweep || Pf?.Entities == null) return;
            nextOrphanSweep = now + 10;
            var owned = new HashSet<int>(trips.Values.SelectMany(t => t.Pirates.Concat(t.PendingPirates.Keys)));
            var seen = new HashSet<int>();
            foreach (var e in Pf.Entities.Values.Where(x => x != null && (x.Type == EntityType.CV || x.Type == EntityType.SV) && StructureTag.IsTagged(x.Name)
                                                          && StructureTag.Strip(x.Name) == "Raider" && !owned.Contains(x.Id)).ToList())
            {
                seen.Add(e.Id);
                if (fly.NearestPlayerDistance(e.Position) <= AbandonRange) { orphanSince.Remove(e.Id); continue; }
                if (!orphanSince.TryGetValue(e.Id, out var since)) { orphanSince[e.Id] = now; continue; }
                if (now - since < OrphanSeconds) continue;
                orphanSince.Remove(e.Id);
                fly.Retire(e.Id);
                log($"ESCORT orphan raider {e.Id} removed (no trip, nobody near)");
            }
            foreach (var id in orphanSince.Keys.Where(k => !seen.Contains(k)).ToList()) orphanSince.Remove(id);
        }
        readonly Dictionary<int, double> orphanSince = new Dictionary<int, double>();
        double nextOrphanSweep;
        const double OrphanSeconds = 120;

        void Step(Trip t, double now)
        {
            // pirates that have appeared and are registered: send them in
            foreach (var kv in t.PendingPirates.ToList())
                if (fly.Intercept(kv.Key, t.Ship, PirateStandoff)) { t.PendingPirates.Remove(kv.Key); t.Pirates.Add(kv.Key); }
                else if (now - kv.Value > 60) { t.PendingPirates.Remove(kv.Key); log($"ESCORT pirate {kv.Key} never became flyable"); }

            if (fly.IsDead(t.Ship))
            {
                // killed: the coreless hull stays with its cargo (a wreck like any other); if YOU let it die, the trader's
                // people remember — memory below
                if (t.Escorted)
                {
                    fly.Say(t.Pid, $"{Name(t.Ship)}'s core is gone — she's a wreck now. Contract failed.");
                    fly.Remember(t.Ship, "memory.escort." + t.PlayerKey, "died");
                }
                fly.AddEvent($"{Name(t.Ship)} destroyed");
                End(t, "trader destroyed");
                return;
            }
            if (!Pf.Entities.TryGetValue(t.Ship, out var e) || e == null)
            {
                if (t.Escorted)
                {
                    fly.Say(t.Pid, "Contact lost with the trader. Contract failed.");
                    fly.Remember(t.Ship, "memory.escort." + t.PlayerKey, "lost");
                }
                fly.AddEvent("Trader lost");
                End(t, "lost");
                return;
            }
            if (fly.IsFleeing(t.Ship)) { End(t, "betrayed"); return; }   // hostility code already spoke and remembered

            // escort walked away: out here alone the ship would freeze (no physics outside a player's bubble). After a
            // minute the contract is void; the ship goes back to its own colours (and, for a sim trader, to the sim).
            if (t.Escorted)
            {
                if (fly.NearestPlayerDistance(e.Position) > AbandonRange) { if (t.AloneSince < 0) t.AloneSince = now; }
                else t.AloneSince = -1;
                if (t.AloneSince >= 0 && now - t.AloneSince > AbandonSeconds)
                {
                    // the captain isn't in this playfield at all (logged off, crashed, warped away): void, no grudge —
                    // "abandoned" is for someone who's here and flew off
                    var here = Pf.Players != null && Pf.Players.ContainsKey(t.Pid);
                    fly.Say(t.Pid, here ? $"{Name(t.Ship)}: You left us out here! Contract's off — no pay." : $"{Name(t.Ship)}: Lost contact with our escort. Contract's off.");
                    fly.Remember(t.Ship, "memory.escort." + t.PlayerKey, here ? "abandoned" : "lost");
                    fly.Command(t.Pid, t.Ship, "leave");
                    fly.AddEvent($"{Name(t.Ship)}: escort abandoned");
                    End(t, "abandoned");
                    return;
                }
            }

            var left = Vector3.Distance(e.Position, t.WarpPoint);
            if (left <= ArriveRange && fly.CombatantNear(e.Position, 2000f, out var pirate))
            {
                // pinned at the jump point: plot a new one ~2.5 km off on the far side and make a run for it (cat and
                // mouse — the pirates chase; repeat until they're dead or gone)
                var away = e.Position - pirate; away.y *= 0.2f;
                if (away.sqrMagnitude < 1f) away = Vector3.right;
                var side = Vector3.Cross(away.normalized, Vector3.up) * (rng.NextDouble() < 0.5 ? -0.5f : 0.5f);
                t.WarpPoint = e.Position + (away.normalized + side).normalized * NewPointRange;
                t.SpoolUntil = -1; t.Relocations++;
                fly.GoTo(t.Ship, t.WarpPoint, "new warp point", 0f, 60f);
                Radio(t, $"{Name(t.Ship)}: They're on us at the jump point! Plotting a new one, {NewPointRange / 1000f:0.0} km out — cover us!");
                fly.AddEvent($"{Name(t.Ship)} runs for a new warp point");
                log($"ESCORT {t.Ship} pinned, new warp point #{t.Relocations} {t.WarpPoint}");
                return;
            }
            if (t.SpoolUntil < 0 && left <= ArriveRange)
            {
                t.SpoolUntil = now + SpoolSeconds;
                Radio(t, $"{Name(t.Ship)}: At the warp point. Spooling the drive — {SpoolSeconds:0} seconds.");
                fly.AddEvent($"{Name(t.Ship)}: warp drive spooling");
            }
            if (t.SpoolUntil >= 0 && now >= t.SpoolUntil)
            {
                if (t.Escorted && Pf.Players != null && !Pf.Players.ContainsKey(t.Pid))
                {   // the captain isn't here any more (logged off just before the jump): no payout to nobody, no grudge
                    fly.Command(t.Pid, t.Ship, "leave");
                    fly.Remember(t.Ship, "memory.escort." + t.PlayerKey, "lost");
                    t.Escorted = false;
                }
                if (t.Escorted)
                {
                    fly.Send("pay", t.Pid, t.Terms.Fee);
                    fly.Command(t.Pid, t.Ship, "leave");
                    fly.Remember(t.Ship, "memory.escort." + t.PlayerKey, "done");
                    fly.Say(t.Pid, $"{Name(t.Ship)}: {t.Terms.Fee:N0} cr transferred. Thanks, {PlayerName(t.Pid)}. Jumping.");
                }
                else Radio(t, $"{Name(t.Ship)}: Jumping.");
                End(t, "jumped");
                fly.Depart(t.Ship, t.Destination);
                return;
            }

            var progress = 1.0 - left / Math.Max(1f, t.TripLen);
            if (t.Terms.Attack && !t.PiratesCalled && progress >= t.Terms.AttackAt) CallPirates(t, e);
        }

        /// <summary>To the escort; a declined trader only radios players within 5 km.</summary>
        void Radio(Trip t, string text)
        {
            if (t.Escorted || PlayerWithin(t, 5000f)) fly.Say(t.Pid, text);
        }

        bool PlayerWithin(Trip t, float range) =>
            Pf?.Players != null && Pf.Players.TryGetValue(t.Pid, out var p) && p != null && Pf.Entities.TryGetValue(t.Ship, out var e) && e != null
            && Vector3.Distance(p.DrivingEntity != null ? p.DrivingEntity.Position : p.Position, e.Position) <= range;

        void CallPirates(Trip t, IEntity trader)
        {
            t.PiratesCalled = true;
            var ahead = (t.WarpPoint - trader.Position).normalized;
            var side = Vector3.Cross(ahead, Vector3.up).normalized;
            if (side.sqrMagnitude < 0.5f) side = Vector3.right;
            var dir = (ahead + side * (rng.NextDouble() < 0.5 ? -0.8f : 0.8f)).normalized;   // ahead and off to one side
            for (var i = 0; i < t.Terms.Pirates.Length; i++)
            {
                var pos = trader.Position + dir * PirateRange + side * (i * 120f);
                var yaw = Mathf.Atan2(-dir.x, -dir.z) * Mathf.Rad2Deg;   // facing the trader
                fly.Send("escortspawn", t.Ship, t.Terms.Pirates[i], F(pos.x), F(pos.y), F(pos.z), F(yaw));
            }
            var bearing = (Mathf.Atan2(dir.x, dir.z) * Mathf.Rad2Deg + 360f) % 360f;
            Radio(t, $"{Name(t.Ship)}: Contact, bearing {bearing:000}, {PirateRange / 1000f:0.0} km — closing fast. Pirates!");
            fly.AddEvent($"Pirates closing on {Name(t.Ship)}");
            log($"ESCORT pirates for {t.Ship}: {string.Join(",", t.Terms.Pirates)}");
        }

        void End(Trip t, string how)
        {
            trips.Remove(t.Ship);
            foreach (var p in t.Pirates) fly.SendAway(p);
            foreach (var p in t.PendingPirates.Keys) fly.SendAway(p);
            log($"ESCORT {t.Ship} ended: {how}");
        }

        string Name(int ship) => Pf?.Entities != null && Pf.Entities.TryGetValue(ship, out var e) && e != null ? StructureTag.Strip(e.Name) : "Trader";
        string PlayerName(int pid) => Pf?.Players != null && Pf.Players.TryGetValue(pid, out var p) && p != null ? S(() => p.Name) : "pilot";
        string LeaderKey(int pid)
        {
            var sid = Pf?.Players != null && Pf.Players.TryGetValue(pid, out var p) && p != null ? S(() => p.SteamId) : "";
            return !string.IsNullOrEmpty(sid) && !sid.StartsWith("!") ? sid : "pid:" + pid.ToString(CultureInfo.InvariantCulture);
        }
        static string F(float v) => v.ToString("0.0", CultureInfo.InvariantCulture);
        static string S<T>(Func<T> f) { try { return Convert.ToString(f(), CultureInfo.InvariantCulture); } catch (Exception ex) { return "!" + ex.GetType().Name; } }
    }
}
