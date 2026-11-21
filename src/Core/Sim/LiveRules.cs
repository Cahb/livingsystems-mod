using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Flight;

namespace LivingSystems.Core.Sim
{
    /// <summary>
    /// Decisions the PfServer makes about live sim ships, kept here (pure, unit-tested) so the live code only moves ships
    /// and talks to the game: has a trader arrived, is it stuck, which leg does a re-adopted ship resume, who reacts to
    /// damage, which faction a hull flies, when station control speaks, whether a patrol comes and what it is.
    /// </summary>
    public static class LiveRules
    {
        // ---- lanes ----------------------------------------------------------------------------------------------

        /// <summary>Inbound: within this of its berth = docked. The flight controller stops ~100 m short and calls that arrived.</summary>
        public const float BerthRange = 150f;
        /// <summary>Outbound / passing: within this of its warp point = spool and jump.</summary>
        public const float WarpPointRange = 250f;

        public static bool Arrived(string leg, float distanceToDest) =>
            leg != "dock" && leg != "" && distanceToDest < (leg == "in" ? BerthRange : WarpPointRange);

        /// <summary>
        /// A ship re-adopted after a reload resumes the leg the traffic list gives it ("arriving" can't be live: treated as
        /// outbound). Its target: the berth slot when docked (where it sits if the slot is unknown), else the lane's end.
        /// </summary>
        public static string ResumeLeg(string trafficLeg) => trafficLeg == "arriving" ? "out" : trafficLeg ?? "";

        public static Vec3 ResumeDest(string leg, Vec3 laneA, Vec3 laneB, Vec3 here) =>
            leg == "dock" ? (laneA.Length > 1 ? laneA : here) : laneB;

        /// <summary>Where "how far along" is measured from: the lane's start; in a fight A is the ship, B the leg's end.</summary>
        public static Vec3 ResumeLaneStart(string leg, Vec3 laneA, Vec3 laneB) => leg == "fight" ? laneB : laneA;

        /// <summary>
        /// Watches a live trader's progress on a lane: no <see cref="Distance"/> m of movement in <see cref="Seconds"/> s
        /// (and not at its point yet) = stuck — thrusters shot off, wedged on something. It then ends the leg where it is.
        /// </summary>
        public sealed class StuckWatch
        {
            public const float Distance = 30f;
            public const double Seconds = 90;
            double since = -1;
            Vec3 at;

            /// <summary>Feed the position each tick; true once it's stuck.</summary>
            public bool Update(Vec3 pos, double now, float distanceToDest)
            {
                if (since < 0 || (pos - at).Length > Distance) { at = pos; since = now; return false; }
                return now - since > Seconds && distanceToDest > WarpPointRange;
            }

            public void Reset() => since = -1;
        }

        // ---- who's who --------------------------------------------------------------------------------------------

        /// <summary>A hulk never reacts (no fleeing, no taunts): one of our wrecks, or anything without a core.</summary>
        public static bool IsHulk(string strippedName, bool coreless) =>
            coreless || (strippedName ?? "").StartsWith("Wreck of", StringComparison.Ordinal);

        /// <summary>Faction group a trader hull spawns under: Polaris-branded hulls Polaris (7), every other trader Trader (18).</summary>
        public static int TraderGroup(string prefab) => (prefab ?? "").IndexOf("Polaris", StringComparison.OrdinalIgnoreCase) >= 0 ? 7 : 18;

        // ---- docked chatter ---------------------------------------------------------------------------------------

        public const double ChatterEvery = 120, ClearedWithin = 20;   // the sim steps every 15 s: "cleared" a minute early left ships sitting

        /// <summary>
        /// Station control for a docked trader with a player near: "loading — N min" every <see cref="ChatterEvery"/> s
        /// (never the moment you arrive: the first call only arms it), once "cleared" in the last minute.
        /// Returns "" (quiet), "loading" or "cleared"; updates the schedule.
        /// </summary>
        public static string DockChatter(double secondsLeft, double now, ref double nextChat, ref bool clearedSaid)
        {
            if (secondsLeft <= ClearedWithin && secondsLeft > 0 && !clearedSaid) { clearedSaid = true; return "cleared"; }
            if (secondsLeft <= ClearedWithin || now < nextChat) return "";
            var armed = nextChat > 0;
            nextChat = now + ChatterEvery;
            return armed ? "loading" : "";
        }

        // ---- repairs at the dock ------------------------------------------------------------------------------------

        /// <summary>Hull a trader gets back per stay at a station (added when it undocks).</summary>
        public const double DockRepair = 0.2;

        /// <summary>Below this a trader drops its plans and heads for a yard.</summary>
        public const double CriticalHull = 0.5;
        /// <summary>A yard keeps a ship this much longer per missing hull (×(1 − hull)) — a full rebuild takes a while.</summary>
        public const double YardSecondsPerHull = 900;

        /// <summary>A station that builds ships (Eden2: Industrial Shipyard, New Hope Shipyard).</summary>
        public static bool IsShipyard(string stationName) => (stationName ?? "").IndexOf("Shipyard", StringComparison.OrdinalIgnoreCase) >= 0;

        /// <summary>A station that fixes ships fully: trading stations, starports, shipyards, repair docks.</summary>
        public static bool IsYard(string stationName)
        {
            var n = stationName ?? "";
            return new[] { "Trad", "Starport", "Shipyard", "Repair" }.Any(k => n.IndexOf(k, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>Hull after a stay: a yard fixes it fully, any other station patches <see cref="DockRepair"/>.</summary>
        public static double HullAfterStay(double hullAtDock, string stationName) =>
            IsYard(stationName) ? 1.0 : Math.Min(1.0, hullAtDock + DockRepair);

        /// <summary>
        /// A docked ship's hull on screen, <paramref name="progress"/> (0…1) through its stay: from what it docked with to
        /// what it'll undock with.
        /// </summary>
        public static double HullDuringStay(double hullAtDock, string stationName, double progress) =>
            hullAtDock + (HullAfterStay(hullAtDock, stationName) - hullAtDock) * Math.Max(0, Math.Min(1, progress));

        // ---- patrols ----------------------------------------------------------------------------------------------

        /// <summary>
        /// How well a system is protected, from its station names (0…0.9): 0.2 per station up to three, +0.3 for a trading
        /// station. The same rule as <see cref="World.Safety"/>.
        /// </summary>
        public static double Safety(IEnumerable<string> stationNames)
        {
            var names = (stationNames ?? Enumerable.Empty<string>()).ToList();
            if (names.Count == 0) return 0;
            return Math.Min(0.9, 0.2 * Math.Min(3, names.Count) + (names.Any(n => (n ?? "").IndexOf("Trad", StringComparison.OrdinalIgnoreCase) >= 0) ? 0.3 : 0));
        }

        /// <summary>Per minute of a live attack: the chance a patrol comes (the sim's per-round patrol chance × safety).</summary>
        public const double PatrolChance = 0.12;

        public sealed class PatrolPlan
        {
            public int Count;
            public string Prefab = "";
        }

        /// <summary>What answers the call: one light Polaris OPV, or two heavier ones at a well-protected hub (safety ≥ 0.6).</summary>
        public static PatrolPlan Patrol(double safety) =>
            safety >= 0.6 ? new PatrolPlan { Count = 2, Prefab = "Eden_OPV_Polaris2" } : new PatrolPlan { Count = 1, Prefab = "Eden_OPV_Polaris1" };

        /// <summary>
        /// A pirate's price to look the other way: 3k + 5k per strength (~8k a raider, ~15.5k a shielded gang ship), ±20 %
        /// by mood — stable per pirate and player (<paramref name="seed"/>), rounded to 500.
        /// </summary>
        public static int TollPrice(double strength, string seed)
        {
            uint h = 2166136261; unchecked { foreach (var c in seed ?? "") { h ^= c; h *= 16777619; } }
            return (int)(Math.Round((3000 + 5000 * strength) * (0.8 + (h % 400) / 1000.0) / 500) * 500);
        }

        /// <summary>
        /// A bounty on a pirate: 5k + 4k per strength (~9k a raider, ~15k a shielded gang ship), ×1.25 for a hub crew
        /// member, ±15 % (stable per <paramref name="seed"/>), rounded to 500.
        /// </summary>
        public static int BountyPrice(double strength, bool hubCrew, string seed)
        {
            uint h = 2166136261; unchecked { foreach (var c in (seed ?? "") + "|bounty") { h ^= c; h *= 16777619; } }
            return (int)(Math.Round((5000 + 4000 * strength) * (hubCrew ? 1.25 : 1) * (0.85 + (h % 300) / 1000.0) / 500) * 500);
        }

        /// <summary>A patrol goes home when the pirates are gone or after this long.</summary>
        public const double PatrolSeconds = 600;

        public static bool PatrolDone(int attackersLeft, double secondsOut) => attackersLeft == 0 || secondsOut > PatrolSeconds;
    }
}
