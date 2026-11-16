using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using LivingSystems.Core.Flight;

namespace LivingSystems.Core.Sim
{
    /// <summary>
    /// The galaxy map from the save's sector files (<c>&lt;save&gt;/Sectors/*.yaml</c>): stars with coordinates (light years),
    /// which playfield sits at which star, and jump gates ("West Rim to Core" ↔ "Core to West Rim"). Routes: direct within
    /// warp range, or star → gate → paired gate → star. Pure; parsed with regexes (the files are regular, we read 3 things).
    /// </summary>
    public sealed class Galaxy
    {
        public sealed class Star
        {
            public string Name = "";
            public Vec3 Pos;
            public bool Gate;
        }

        public readonly Dictionary<string, Star> Stars = new Dictionary<string, Star>(StringComparer.Ordinal);
        readonly Dictionary<string, Star> byPlayfield = new Dictionary<string, Star>(StringComparer.Ordinal);
        /// <summary>Gate star → the gate star it leads to.</summary>
        public readonly Dictionary<string, Star> GatePairs = new Dictionary<string, Star>(StringComparer.Ordinal);
        /// <summary>
        /// Gates NPCs can use. Eden2's gates "require repair before [they] can be used" (gate playfield description): all
        /// closed until a player repairs one (later: read the gate structure's damage). Either end's name opens the pair.
        /// </summary>
        public readonly HashSet<string> OpenGates = new HashSet<string>(StringComparer.Ordinal);

        public int PlayfieldCount => byPlayfield.Count;

        static readonly Regex NameRx = new Regex(@"^\s*-\s*Name:\s*(.+?)\s*$", RegexOptions.Multiline);
        static readonly Regex CoordRx = new Regex(@"Coordinates:\s*\[\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\]");
        static readonly Regex ClassRx = new Regex(@"StarClass:\s*(\S+)");
        static readonly Regex PfRx = new Regex(@"-\s*\[\s*'[^']*'\s*,\s*(?:'([^']*)'|([^,\]]+?))\s*,\s*'?([A-Za-z0-9_]*)");
        /// <summary>Playfield → its playfield type (EdenSpaceG_Orbit, Start_AsteroidField, EdenMoonBarren…).</summary>
        public readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Playfield → its sector's coordinates inside the star system (the sun at 0,0,0; game map units).</summary>
        public readonly Dictionary<string, Vec3> SectorPos = new Dictionary<string, Vec3>(StringComparer.Ordinal);
        /// <summary>Playfield → its sector's SectorMapType (Station, AsteroidFieldFew, WarpTarget, Planet…; "" if none).</summary>
        public readonly Dictionary<string, string> MapTypes = new Dictionary<string, string>(StringComparer.Ordinal);
        static readonly Regex SectorRx = new Regex(@"^\s*-\s*Coordinates:\s*\[\s*(-?\d+)\s*,\s*(-?\d+)\s*,\s*(-?\d+)\s*\]", RegexOptions.Multiline);
        static readonly Regex MapTypeRx = new Regex(@"SectorMapType:\s*(\S+)");

        /// <summary>Parse any number of sector files (Sectors.yaml + Sectors_x,y,z.yaml). Later files don't override earlier stars.</summary>
        public static Galaxy Parse(IEnumerable<string> texts)
        {
            var g = new Galaxy();
            foreach (var text in texts ?? Enumerable.Empty<string>())
            {
                var t = text ?? "";
                var names = NameRx.Matches(t).Cast<Match>().ToList();
                for (var i = 0; i < names.Count; i++)
                {
                    var start = names[i].Index;
                    var end = i + 1 < names.Count ? names[i + 1].Index : t.Length;
                    var block = t.Substring(start, end - start);
                    var name = names[i].Groups[1].Value.Trim().Trim('\'', '"');
                    var c = CoordRx.Match(block);
                    if (!c.Success || name.Length == 0) continue;
                    if (!g.Stars.TryGetValue(name, out var star))
                    {
                        star = new Star { Name = name, Pos = new Vec3(I(c.Groups[1]), I(c.Groups[2]), I(c.Groups[3])) };
                        var cls = ClassRx.Match(block);
                        star.Gate = cls.Success && cls.Groups[1].Value.IndexOf("JumpGate", StringComparison.OrdinalIgnoreCase) >= 0;
                        g.Stars[name] = star;
                    }
                    foreach (Match pm in PfRx.Matches(block))
                    {
                        var pf = (pm.Groups[1].Success ? pm.Groups[1].Value : pm.Groups[2].Value).Trim();
                        if (pf.Length > 0 && !g.byPlayfield.ContainsKey(pf)) { g.byPlayfield[pf] = star; g.Types[pf] = pm.Groups[3].Value; }
                    }
                    // where each playfield sits inside the system: its sector's "- Coordinates: [x,y,z]" (+ map type)
                    var secs = SectorRx.Matches(block).Cast<Match>().ToList();
                    for (var k = 0; k < secs.Count; k++)
                    {
                        var se = k + 1 < secs.Count ? secs[k + 1].Index : block.Length;
                        var sb = block.Substring(secs[k].Index, se - secs[k].Index);
                        var pos = new Vec3(I(secs[k].Groups[1]), I(secs[k].Groups[2]), I(secs[k].Groups[3]));
                        var mt = MapTypeRx.Match(sb);
                        foreach (Match pm in PfRx.Matches(sb))
                        {
                            var pf = (pm.Groups[1].Success ? pm.Groups[1].Value : pm.Groups[2].Value).Trim();
                            if (pf.Length > 0 && !g.SectorPos.ContainsKey(pf)) { g.SectorPos[pf] = pos; g.MapTypes[pf] = mt.Success ? mt.Groups[1].Value : ""; }
                        }
                    }
                }
            }
            // gates come in pairs by name: "A to B" ↔ "B to A"
            foreach (var s in g.Stars.Values.Where(x => x.Gate))
            {
                var m = Regex.Match(s.Name, @"^(.+?) to (.+)$");
                if (m.Success && g.Stars.TryGetValue(m.Groups[2].Value + " to " + m.Groups[1].Value, out var other) && other.Gate) g.GatePairs[s.Name] = other;
            }
            return g;
        }

        static float I(Group gr) => float.Parse(gr.Value, CultureInfo.InvariantCulture);

        /// <summary>Every playfield at a star.</summary>
        public IEnumerable<string> PlayfieldsOf(Star star) => star == null ? Enumerable.Empty<string>() : byPlayfield.Where(kv => kv.Value.Name == star.Name).Select(kv => kv.Key);

        /// <summary>
        /// How good a pirate hideout a playfield makes: Eden's pirate gas-giant orbits best, then asteroid fields, then plain
        /// orbits; 0 = no (planet/moon surfaces, the sun, warp targets — a hub needs open space nobody passes through).
        /// </summary>
        public int HideoutScore(string playfield)
        {
            if (!Types.TryGetValue(playfield ?? "", out var t) || t.Length == 0) return 0;
            if (t.IndexOf("WarpTarget", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Sun", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Trade", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Kriel", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Drone", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("JumpGate", StringComparison.OrdinalIgnoreCase) >= 0) return 0;
            if (t.IndexOf("Pirate", StringComparison.OrdinalIgnoreCase) >= 0) return 3;
            if (t.IndexOf("Asteroid", StringComparison.OrdinalIgnoreCase) >= 0) return 2;
            if (t.IndexOf("Orbit", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Space", StringComparison.OrdinalIgnoreCase) >= 0) return 1;
            return 0;
        }

        public Star StarOf(string playfield) => playfield != null && byPlayfield.TryGetValue(playfield, out var s) ? s : null;

        /// <summary>Light years between the stars of two playfields (0 = same star); +inf if either is unknown.</summary>
        public double Distance(string a, string b)
        {
            var sa = StarOf(a); var sb = StarOf(b);
            return sa == null || sb == null ? double.PositiveInfinity : (sa.Pos - sb.Pos).Length;
        }

        /// <summary>
        /// The cheapest way from <paramref name="a"/> to <paramref name="b"/> in light years: straight across, or through a
        /// jump-gate pair (to the gate, <paramref name="gateCost"/>, out of the paired gate). Any distance is flyable: the
        /// sector files only list predefined / generated stars, the game fills the space between with random ones (a gate can
        /// sit 75 LY from the nearest listed star), so <paramref name="range"/> sets the number
        /// of warp hops (time), not whether a route exists. +inf only for an unknown playfield.
        /// </summary>
        public double Route(string a, string b, double range, double gateCost, out bool viaGate) => Route(a, b, range, gateCost, out viaGate, out _);

        public double Route(string a, string b, double range, double gateCost, out bool viaGate, out int hops)
        {
            viaGate = false; hops = 0;
            var sa = StarOf(a); var sb = StarOf(b);
            if (sa == null || sb == null) return double.PositiveInfinity;
            if (sa == sb) return 0;
            int Hops(double ly) => Math.Max(1, (int)Math.Ceiling(ly / Math.Max(1, range)));
            var best = (double)(sa.Pos - sb.Pos).Length;
            hops = Hops(best);
            foreach (var kv in GatePairs)
            {
                if (!OpenGates.Contains(kv.Key) && !OpenGates.Contains(kv.Value.Name)) continue;   // broken until repaired
                var g1 = Stars[kv.Key]; var g2 = kv.Value;
                double d1 = (sa.Pos - g1.Pos).Length, d2 = (g2.Pos - sb.Pos).Length;
                var cost = d1 + d2 + gateCost;
                if (cost < best) { best = cost; viaGate = true; hops = Hops(d1) + 1 + Hops(d2); }
            }
            return best;
        }

        /// <summary>Direction from a's star toward b's star (mostly level), or null for the same / an unknown star.</summary>
        public Vec3? Heading(string a, string b)
        {
            var sa = StarOf(a); var sb = StarOf(b);
            if (sa == null || sb == null || sa == sb) return null;
            var d = sb.Pos - sa.Pos;
            d.Y *= 0.2f;
            var n = d.Normalized;
            return n.Length < 0.5f ? (Vec3?)null : n;
        }
    }
}
