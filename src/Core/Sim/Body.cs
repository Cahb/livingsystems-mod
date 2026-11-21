using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LivingSystems.Core.Sim
{
    /// <summary>
    /// One body, many brains (docs/lifecycles.md): every thing the sim owns that can be real in the game goes sim ↔ live by
    /// these rules, written once. A brain (what the thing does) never spawns, retires, or decides dead / gone.
    /// </summary>
    public static class BodyRules
    {
        /// <summary>Out of every watcher's range past this (for <see cref="OutGrace"/> s) → back to the sim.</summary>
        public const float OutMetres = 2500f;
        /// <summary>Back live within this (hysteresis: never flickers on the line).</summary>
        public const float InMetres = 2000f;
        public const double OutGrace = 5;
        /// <summary>A spawn that never shows up in this long is given up (the sim keeps the body, tries again later).</summary>
        public const double SpawnTimeout = 90;
        /// <summary>A hull missing from the playfield's list this long, with no evidence of death → gone from view.</summary>
        public const double MissingGrace = 60;

        /// <summary>Is the body out of range now? <paramref name="dist"/> = to the nearest watcher (∞ / none = out).</summary>
        public static bool Away(bool watched, float dist, float outMetres = OutMetres) => !watched || dist > outMetres;
        /// <summary>Should a body held by the sim come live? Within <see cref="InMetres"/> of a watcher.</summary>
        public static bool Near(bool watched, float dist, float inMetres = InMetres) => watched && dist <= inMetres;

        /// <summary>
        /// What happened to a live body whose hull is gone from view or reports dead. Gone ≠ dead: only evidence kills.
        /// </summary>
        public static BodyFate Judge(bool listed, bool coreDestroyed, bool playerCore)
        {
            if (playerCore) return BodyFate.Claimed;
            if (coreDestroyed) return BodyFate.Dead;
            return listed ? BodyFate.Here : BodyFate.Gone;
        }
    }

    public enum BodyFate { Here, Gone, Dead, Claimed }

    /// <summary>The away clock: <see cref="BodyRules.OutGrace"/> seconds out of range before the body goes back.</summary>
    public sealed class AwayClock
    {
        double since = -1;
        public double Since => since;
        /// <summary>True once the body has been away for <paramref name="grace"/> s straight (then the clock resets).</summary>
        public bool Tick(bool away, double now, double grace = BodyRules.OutGrace)
        {
            if (!away) { since = -1; return false; }
            if (since < 0) since = now;
            if (now - since < grace) return false;   // grace 0: at once
            since = -1;
            return true;
        }
        public void Reset() => since = -1;
    }

    /// <summary>
    /// The one message set between the PfServer (live) and the Dedi (sim) for bodies: each carries the kind (the brain's
    /// name) and the body's id, then generic fields, then the brain's own payload (one string, ";"-free of '|').
    ///   PfServer → Dedi: bodyspawn | bodylive | bodyback | bodydead
    ///   Dedi → PfServer: body|spawned | body|adopt | body|drop
    /// </summary>
    public sealed class BodyMsg
    {
        public string Verb = "", Kind = "", Id = "";
        /// <summary>back: why it left ("range", "view", "reload"); dead: how ("destroyed", "claimed", "nospawn").</summary>
        public string Why = "";
        public int Entity, Ghost;
        public bool HasPos; public float X, Y, Z;
        /// <summary>The hold (World.FormatHold).</summary>
        public string Hold = "";
        /// <summary>The brain's own progress / payload (opaque here).</summary>
        public string Progress = "";
        /// <summary>bodyspawn: prefab, yaw, name, faction group.</summary>
        public string Prefab = "", Name = ""; public float Yaw; public int Faction = 7;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        static string Fl(float v) => v.ToString("0.0", Inv);
        static float P(string s) => float.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0f;
        static int I(string s) => int.TryParse(s, NumberStyles.Integer, Inv, out var v) ? v : 0;
        /// <summary>A field never carries the wire's '|' (it would split the packet).</summary>
        public static string Clean(string s) => (s ?? "").Replace('|', '/');
        string PosText => HasPos ? $"{Fl(X)},{Fl(Y)},{Fl(Z)}" : "";
        void ReadPos(string s)
        {
            var p = (s ?? "").Split(',');
            if (p.Length != 3) return;
            if (!float.TryParse(p[0], NumberStyles.Float, Inv, out var x) || !float.TryParse(p[1], NumberStyles.Float, Inv, out var y) || !float.TryParse(p[2], NumberStyles.Float, Inv, out var z)) return;
            X = x; Y = y; Z = z; HasPos = true;
        }

        public static BodyMsg Spawn(string kind, string id, string prefab, float x, float y, float z, float yaw, string name, int faction = 7) =>
            new BodyMsg { Verb = "bodyspawn", Kind = kind, Id = id, Prefab = prefab, X = x, Y = y, Z = z, HasPos = true, Yaw = yaw, Name = name, Faction = faction };
        public static BodyMsg Live(string kind, string id, int entity) => new BodyMsg { Verb = "bodylive", Kind = kind, Id = id, Entity = entity };
        public static BodyMsg Dead(string kind, string id, string how, string hold = "") => new BodyMsg { Verb = "bodydead", Kind = kind, Id = id, Why = how, Hold = hold ?? "" };
        public static BodyMsg Spawned(string kind, string id, int entity) => new BodyMsg { Verb = "spawned", Kind = kind, Id = id, Entity = entity };
        /// <summary>Dedi → PfServer: forget this body here (it's ended, or it moved to another playfield); its hull, if any, goes.</summary>
        public static BodyMsg Drop(string kind, string id) => new BodyMsg { Verb = "drop", Kind = kind, Id = id };

        /// <summary>The fields after the channel header, e.g. "bodyback|hire|h12|range|1,2,3|Item:3|…|0".</summary>
        public object[] Fields()
        {
            switch (Verb)
            {
                case "bodyspawn": return new object[] { Verb, Kind, Id, Prefab, Fl(X), Fl(Y), Fl(Z), Fl(Yaw), Clean(Name), Faction };
                case "bodylive": return new object[] { Verb, Kind, Id, Entity };
                case "bodydead": return new object[] { Verb, Kind, Id, Clean(Why), Clean(Hold) };
                case "bodyback": return new object[] { Verb, Kind, Id, Clean(Why), PosText, Clean(Hold), Clean(Progress), Ghost };
                case "spawned": return new object[] { "body", Verb, Kind, Id, Entity };
                case "drop": return new object[] { "body", Verb, Kind, Id };
                case "adopt": return new object[] { "body", Verb, Kind, Id, Entity, Ghost, PosText, Clean(Hold), Clean(Progress) };
            }
            throw new InvalidOperationException("BodyMsg: unknown verb " + Verb);
        }

        /// <summary>Read one from split fields (PfServer → Dedi: starts at the verb; Dedi → PfServer: "body" first). Null = not one.</summary>
        public static BodyMsg Parse(string[] f)
        {
            if (f == null || f.Length < 3) return null;
            var o = 0;
            if (f[0] == "body") o = 1;
            if (f.Length < o + 3) return null;
            var m = new BodyMsg { Verb = f[o], Kind = f[o + 1], Id = f[o + 2] };
            string A(int i) => o + i < f.Length ? f[o + i] : "";
            switch (m.Verb)
            {
                case "bodyspawn": if (o != 0) return null; m.Prefab = A(3); m.X = P(A(4)); m.Y = P(A(5)); m.Z = P(A(6)); m.HasPos = true; m.Yaw = P(A(7)); m.Name = A(8); m.Faction = A(9).Length > 0 ? I(A(9)) : 7; return m;
                case "bodylive": if (o != 0) return null; m.Entity = I(A(3)); return m;
                case "bodydead": if (o != 0) return null; m.Why = A(3); m.Hold = A(4); return m;
                case "bodyback": if (o != 0) return null; m.Why = A(3); m.ReadPos(A(4)); m.Hold = A(5); m.Progress = A(6); m.Ghost = I(A(7)); return m;
                case "spawned": if (o != 1) return null; m.Entity = I(A(3)); return m;
                case "drop": if (o != 1) return null; return m;
                case "adopt": if (o != 1) return null; m.Entity = I(A(3)); m.Ghost = I(A(4)); m.ReadPos(A(5)); m.Hold = A(6); m.Progress = A(7); return m;
            }
            return null;
        }

        public static BodyMsg Back(string kind, string id, string why, bool hasPos, float x, float y, float z, string hold, string progress, int ghost) =>
            new BodyMsg { Verb = "bodyback", Kind = kind, Id = id, Why = why, HasPos = hasPos, X = x, Y = y, Z = z, Hold = hold ?? "", Progress = progress ?? "", Ghost = ghost };
        public static BodyMsg Adopt(string kind, string id, int entity, int ghost, bool hasPos, float x, float y, float z, string hold, string progress) =>
            new BodyMsg { Verb = "adopt", Kind = kind, Id = id, Entity = entity, Ghost = ghost, HasPos = hasPos, X = x, Y = y, Z = z, Hold = hold ?? "", Progress = progress ?? "" };
    }

    /// <summary>A brain's progress as "key=value;key=value" (values never carry ';' '=' '|').</summary>
    public static class BodyProgress
    {
        public static string Write(IEnumerable<KeyValuePair<string, string>> kv) =>
            string.Join(";", kv.Where(x => !string.IsNullOrEmpty(x.Value)).Select(x => x.Key + "=" + Esc(x.Value)));
        public static Dictionary<string, string> Read(string s)
        {
            var d = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var part in (s ?? "").Split(';'))
            {
                var i = part.IndexOf('=');
                if (i <= 0) continue;
                d[part.Substring(0, i)] = Unesc(part.Substring(i + 1));
            }
            return d;
        }
        static string Esc(string v) => (v ?? "").Replace("%", "%25").Replace(";", "%3B").Replace("=", "%3D").Replace("|", "%7C");
        static string Unesc(string v) => (v ?? "").Replace("%7C", "|").Replace("%3D", "=").Replace("%3B", ";").Replace("%25", "%");
        public static string Get(Dictionary<string, string> d, string k) => d.TryGetValue(k, out var v) ? v : "";
        public static int Int(Dictionary<string, string> d, string k) => int.TryParse(Get(d, k), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
        public static double Num(Dictionary<string, string> d, string k) => double.TryParse(Get(d, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }
}
