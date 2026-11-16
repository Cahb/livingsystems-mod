using System;

namespace LivingSystems.Core
{
    public enum Risk { Quiet, Moderate, Hot }

    /// <summary>A crew's answer to "need an escort?" for this leg, and what it says.</summary>
    public sealed class EscortAsk
    {
        public bool Wants;
        public string Line = "";
        /// <summary>Fee multiplier: a hurt ship pays more to get somewhere safe.</summary>
        public double FeeFactor = 1;
        public EscortAsk(bool wants, string line) { Wants = wants; Line = line ?? ""; }
    }

    /// <summary>What a trader offers for an escort: risk it quotes, fee, and (hidden) whether pirates come and when.</summary>
    public sealed class EscortTerms
    {
        public Risk Risk;
        public int Fee;
        /// <summary>Pirates will show up on this trip (rolled once, at the offer).</summary>
        public bool Attack;
        /// <summary>Fraction of the trip (0…1) at which they show up.</summary>
        public double AttackAt;
        /// <summary>Pirate prefabs to spawn (empty if no attack).</summary>
        public string[] Pirates = new string[0];
    }

    /// <summary>
    /// Escort run v1 (PLAN.md START HERE): the risk is quoted honestly, a higher fee means a likelier attack, and an
    /// attack is a roll, never a script. v1 rolls the risk itself; later the offline sim decides it (IDEAS "World
    /// simulation": pirates as agents on lanes).
    /// </summary>
    public static class Escort
    {
        public static double AttackChance(Risk r) => r == Risk.Quiet ? 0.15 : r == Risk.Moderate ? 0.5 : 0.85;

        static double FeeFactor(Risk r) => r == Risk.Quiet ? 1.0 : r == Risk.Moderate ? 2.0 : 3.5;

        /// <summary>Fee in credits, rounded to 50: a base plus distance, scaled by risk.</summary>
        public static int Fee(Risk r, float tripMetres)
        {
            var raw = (800.0 + 0.5 * Math.Max(0f, tripMetres)) * FeeFactor(r);
            return (int)(Math.Round(raw / 50.0) * 50);
        }

        // Eden2 prefabs (Content/Scenarios/Eden2/Prefabs). Balance untested — a knob, set by playtest.
        public static readonly string[] Light = { "OPV_PirateCorvetteT1A", "OPV_PirateCorvetteT1B", "OPV_PirateCorvetteT1C", "OPV_PirateCorvetteT1D" };
        public static readonly string[] Heavy = { "OPV_PirateFrigate" };

        public static EscortTerms Roll(Random rng, float tripMetres)
        {
            var w = rng.NextDouble();
            var risk = w < 0.5 ? Risk.Quiet : w < 0.85 ? Risk.Moderate : Risk.Hot;
            return Roll(rng, tripMetres, risk);
        }

        public static EscortTerms Roll(Random rng, float tripMetres, Risk risk)
        {
            var t = new EscortTerms { Risk = risk, Fee = Fee(risk, tripMetres) };
            t.Attack = rng.NextDouble() < AttackChance(risk);
            if (!t.Attack) return t;
            t.AttackAt = 0.3 + rng.NextDouble() * 0.4;
            switch (risk)
            {
                case Risk.Quiet: t.Pirates = new[] { Pick(rng, Light) }; break;
                case Risk.Moderate: t.Pirates = rng.NextDouble() < 0.5 ? new[] { Pick(rng, Light) } : new[] { Pick(rng, Light), Pick(rng, Light) }; break;
                default: t.Pirates = rng.NextDouble() < 0.5 ? new[] { Pick(rng, Heavy) } : new[] { Pick(rng, Light), Pick(rng, Light) }; break;
            }
            return t;
        }

        static string Pick(Random rng, string[] pool) => pool[rng.Next(pool.Length)];

        /// <summary>
        /// Does this crew want an escort for this leg? Outbound only; more with a hot
        /// lane and a valuable hold, rarely with an empty hold or its own guns; a cagey crew declines. The answer is fixed per
        /// leg: <paramref name="legSeed"/> (ship + leg start) decides the roll, so re-hailing doesn't reroll it.
        /// </summary>
        public static EscortAsk Ask(string legSeed, bool outbound, Risk risk, double cargoValue, double guns, bool cagey, double hull = 1)
        {
            if (!outbound) return new EscortAsk(false, "We're heading in — the station's guns cover us from here. Thanks anyway.");
            // badly hurt: almost always wants guns along, and pays extra (even a cagey crew)
            if (hull < 0.5 && Roll01(legSeed + "|hurt") < 0.9)
                return new EscortAsk(true, $"Hull's at {hull * 100:0} % — one more hit and we're done. Get us to a yard and we'll make it worth it.") { FeeFactor = 1.5 };
            if (cagey) return new EscortAsk(false, "We'll manage on our own, thanks.");
            var p = risk == Risk.Hot ? 0.8 : risk == Risk.Moderate ? 0.55 : 0.25;
            if (cargoValue <= 0) p *= 0.2;
            else if (cargoValue >= 20000) p = Math.Min(0.95, p + 0.2);
            if (guns >= 0.5) p *= 0.3;
            var roll = Roll01(legSeed);
            if (roll < p)
                return new EscortAsk(true, cargoValue >= 20000 ? "We're carrying something worth taking — we'd be glad of guns."
                                         : risk != Risk.Quiet ? "Pirates have been about. We'd be glad of guns." : "Can't be too careful out here.");
            if (cargoValue <= 0) return new EscortAsk(false, "Flying empty — nothing worth stealing. Save your guns.");
            if (guns >= 0.5) return new EscortAsk(false, "We've got guns of our own. We'll be fine.");
            if (risk == Risk.Quiet) return new EscortAsk(false, "Lane's quiet. Not worth the fee.");
            return new EscortAsk(false, "We'll chance it. Can't afford the fee this run.");
        }

        static double Roll01(string seed)
        {
            unchecked { uint h = 2166136261; foreach (var c in (seed ?? "") + "|escort") { h ^= c; h *= 16777619; } return (h % 10000) / 10000.0; }
        }

        /// <summary>What the trader says about the lane.</summary>
        public static string RiskLine(Risk r) =>
            r == Risk.Quiet ? "Lane's been quiet lately."
            : r == Risk.Moderate ? "There's been pirate talk on this lane."
            : "Pirates hit two ships on this lane this week.";
    }
}
