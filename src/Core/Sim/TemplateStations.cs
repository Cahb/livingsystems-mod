using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using LivingSystems.Core.Flight;

namespace LivingSystems.Core.Sim
{
    /// <summary>
    /// Stations known before anyone visits: a save's Templates/&lt;playfield&gt;/playfield.yaml holds the POIs the seed
    /// resolved when the solar system was generated (prefab, name, faction, position). Space stations from that list seed
    /// the trade network from minute one, so the offline world runs while players are still planet-side; a visit replaces
    /// them with what the game really reports.
    /// </summary>
    /// <summary>A static point of interest the sim knows from the map (not an agent).</summary>
    public sealed class SimPoi { public string Name = ""; public Vec3 Pos; }

    public static class TemplateStations
    {
        /// <summary>
        /// Prefabs that ARE stations traders dock at — calibrated against LSTest (template POI names matched to the stations
        /// the game reported). Same-faction satellites, billboards, defence posts, mission POIs and junk are not.
        /// </summary>
        static readonly Regex StationPrefab = new Regex(
            @"^(RE2_TSO_|RE2_BAO_Pol(Refinery|Foreman)|R2BAO_PolarisShipyard|RE2_BAO_UCHResearchShip|R2BAO_IndustrialSpace|R2BAO_AsteroidStation|R2BAO_CloudMiner|Eden_BAO_ColHabitat|RE2_BAO_ColScrapYard|R2BAO_ColonistStation)",
            RegexOptions.CultureInvariant);

        public static bool IsStationPrefab(string prefab) => prefab != null && StationPrefab.IsMatch(prefab);

        /// <summary>Half-size (m) of a template station's box until a PfServer measures the real hull.</summary>
        public const float PlaceholderHalf = 120f;

        /// <summary>
        /// Bandit (Warlord, Eden2 faction 27) POIs in one playfield template: sentries and bases, static, run by the game —
        /// the sim reads them as map data (hub turf, attrition on legs, rumours), never as agents.
        /// </summary>
        public static List<SimPoi> Bandits(string yaml)
        {
            var list = new List<SimPoi>();
            if (string.IsNullOrEmpty(yaml)) return list;
            string type = null, name = null, faction = null; var pos = new List<float>(); var inPos = false;
            void Flush()
            {
                if (faction == "Warlord" && type != null && type.StartsWith("BA", StringComparison.Ordinal))
                    list.Add(new SimPoi { Name = string.IsNullOrEmpty(name) ? "bandit outpost" : name, Pos = pos.Count >= 3 ? new Vec3(pos[0], pos[1], pos[2]) : new Vec3(0, 0, 0) });
                type = name = faction = null; pos.Clear(); inPos = false;
            }
            foreach (var raw in yaml.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                var t = Regex.Match(l, @"^\s{2}- Type:\s*(\S+)");
                if (t.Success) { Flush(); type = t.Groups[1].Value; continue; }
                var m = Regex.Match(l, @"^\s{4}(Name|Faction|Pos):\s*(.*)$");
                if (m.Success)
                {
                    inPos = false; var v = m.Groups[2].Value.Trim();
                    if (m.Groups[1].Value == "Name" && name == null) name = v;
                    else if (m.Groups[1].Value == "Faction" && faction == null) faction = v.Split(' ', '#')[0];
                    else if (m.Groups[1].Value == "Pos")
                    {
                        if (v.StartsWith("[", StringComparison.Ordinal)) { foreach (var part in v.Trim('[', ']').Split(',')) if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) pos.Add(f); }
                        else inPos = true;
                    }
                    continue;
                }
                if (inPos)
                {
                    var mp = Regex.Match(l, @"^\s{4}-\s*(\S+)\s*$");
                    if (mp.Success && float.TryParse(mp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) { pos.Add(f); continue; }
                    inPos = false;
                }
            }
            Flush();
            return list;
        }

        /// <summary>Placeholder ids are negative (never a real entity id), stable per playfield + name + prefab.</summary>
        public static bool IsPlaceholder(SimStation s) => s != null && s.Id < 0;

        /// <summary>The station POIs in one playfield template (empty if none).</summary>
        public static List<SimStation> Parse(string playfield, string yaml)
        {
            var list = new List<SimStation>();
            if (string.IsNullOrEmpty(yaml)) return list;
            string prefab = null, name = null; var pos = new List<float>(); var inPos = false;
            void Flush()
            {
                if (IsStationPrefab(prefab))
                {
                    var p = pos.Count >= 3 ? new Vec3(pos[0], pos[1], pos[2]) : new Vec3(0, 0, 0);
                    var label = string.IsNullOrEmpty(name) ? prefab : name;
                    var id = -(int)(Fnv(playfield + "|" + label + "|" + prefab) & 0x3FFFFFFF) - 1;
                    // unmeasured yet: a conservative box (the biggest measured station is ~105 m half-size) so first
                    // berths are ~200 m out — not the 1 km default, never inside a real hull; the visit measures it
                    list.Add(new SimStation { Id = id, Name = label, Pos = p, HasBox = true, Box = p, Half = new Vec3(PlaceholderHalf, PlaceholderHalf, PlaceholderHalf), Rot = new Quat(0, 0, 0, 1) });
                }
                prefab = null; name = null; pos.Clear(); inPos = false;
            }
            foreach (var raw in yaml.Split('\n'))
            {
                var l = raw.TrimEnd('\r');
                if (Regex.IsMatch(l, @"^\s{2}- Type:")) { Flush(); continue; }   // next POI entry
                var m = Regex.Match(l, @"^\s{4}(Prefab|Name|Pos):\s*(.*)$");
                if (m.Success)
                {
                    inPos = false;
                    var v = m.Groups[2].Value.Trim();
                    if (m.Groups[1].Value == "Prefab" && prefab == null) prefab = v;
                    else if (m.Groups[1].Value == "Name" && name == null) name = v;
                    else if (m.Groups[1].Value == "Pos")
                    {
                        if (v.StartsWith("[", StringComparison.Ordinal))
                            foreach (var part in v.Trim('[', ']').Split(','))
                            { if (float.TryParse(part.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) pos.Add(f); }
                        else inPos = true;   // block list: "    - 0" lines follow
                    }
                    continue;
                }
                if (inPos)
                {
                    var mp = Regex.Match(l, @"^\s{4}-\s*(\S+)\s*$");
                    if (mp.Success && float.TryParse(mp.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var f)) { pos.Add(f); continue; }
                    inPos = false;
                }
            }
            Flush();
            return list;
        }

        static uint Fnv(string s)
        {
            var h = 2166136261u;
            foreach (var c in s) { h ^= c; h *= 16777619u; }
            return h;
        }
    }
}
