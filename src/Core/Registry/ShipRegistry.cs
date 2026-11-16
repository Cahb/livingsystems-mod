using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

namespace LivingSystems.Core.Registry
{
    /// <summary>A ship's standing order — one optional section of its <see cref="ShipRecord"/>.</summary>
    public sealed class ShipOrder
    {
        /// <summary>"follow", "hold", or "goto" (a trip: Hold* = destination, LeaderName = its name, Distance = stop short).</summary>
        public string Kind = "";
        /// <summary>Follow: the leader, by Steam id (stable across sessions; entity ids are looked up live).</summary>
        public string LeaderSteamId = "";
        /// <summary>Leader's name when the order was given — for "waiting for …" while they're away.</summary>
        public string LeaderName = "";
        public float Distance = 150f, Cruise = 40f;
        /// <summary>Hold: the spot to keep (world coordinates in the record's playfield).</summary>
        public float HoldX, HoldY, HoldZ;

        public ShipOrder Clone() => (ShipOrder)MemberwiseClone();
    }

    /// <summary>
    /// Everything we keep about one of our ships. The game save keeps the ship itself (entity ids and «LS» names
    /// survive save/load, spike Q3); this keeps what the game doesn't: what it is to us and what it's doing.
    ///
    /// Stored as key=value fields with namespaced keys (<c>order.kind</c>, later <c>pilot.skill</c>, <c>caps.fwd</c> …).
    /// Backward compatible: a missing key means its default. Forward compatible: keys this version doesn't know are
    /// kept in <see cref="Extra"/> and written back unchanged, so older code never destroys newer data.
    /// </summary>
    public sealed class ShipRecord
    {
        public int EntityId;
        public string Playfield = "";
        public string Name = "";
        /// <summary>When we first registered it (UTC, ISO 8601); informational.</summary>
        public string Since = "";
        /// <summary>Null = no standing order.</summary>
        public ShipOrder Order;
        /// <summary>Fields written by other (newer) versions: preserved verbatim.</summary>
        public SortedDictionary<string, string> Extra = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public ShipRecord Clone()
        {
            var c = (ShipRecord)MemberwiseClone();
            c.Order = Order?.Clone();
            c.Extra = new SortedDictionary<string, string>(Extra, StringComparer.Ordinal);
            return c;
        }

        public override bool Equals(object o) => o is ShipRecord r && ToLine(this) == ToLine(r);
        public override int GetHashCode() => ToLine(this).GetHashCode();

        // ---- line format: "ship k=v k=v …"; values percent-escaped (space, '=', '%', '|', newlines) ----
        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>One line, no newline, no '|' — also the wire format between PfServer and Dedi.</summary>
        /// <summary>Raw value of a key from <see cref="Extra"/> (sections this record type has no typed field for); "" if absent.</summary>
        public string Field(string key) => Extra.TryGetValue(key, out var v) ? v : "";

        public static string ToLine(ShipRecord r)
        {
            var kv = new SortedDictionary<string, string>(r.Extra, StringComparer.Ordinal)
            {
                ["id"] = r.EntityId.ToString(Inv),
                ["pf"] = r.Playfield ?? "",
            };
            if (!string.IsNullOrEmpty(r.Name)) kv["name"] = r.Name;
            if (!string.IsNullOrEmpty(r.Since)) kv["since"] = r.Since;
            if (r.Order != null)
            {
                // our order replaces any order.* fields carried in Extra (e.g. an older unknown kind)
                foreach (var k in kv.Keys.Where(k => k.StartsWith("order.", StringComparison.Ordinal)).ToList()) kv.Remove(k);
                var o = r.Order;
                kv["order.kind"] = o.Kind ?? "";
                if (o.Kind == "follow")
                {
                    kv["order.leader"] = o.LeaderSteamId ?? "";
                    kv["order.leaderName"] = o.LeaderName ?? "";
                    kv["order.dist"] = F(o.Distance);
                }
                if (o.Kind == "hold") { kv["order.x"] = F(o.HoldX); kv["order.y"] = F(o.HoldY); kv["order.z"] = F(o.HoldZ); }
                kv["order.cruise"] = F(o.Cruise);
            }
            return BuildLine(kv);
        }

        /// <summary>Raw fields of a v2 line, values unescaped (empty values kept — they mean "delete" in a patch); null if not a ship line.</summary>
        public static Dictionary<string, string> ParseFields(string line)
        {
            line = (line ?? "").Trim();
            if (!line.StartsWith("ship ", StringComparison.Ordinal)) return null;
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tok in line.Substring(5).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var i = tok.IndexOf('=');
                if (i > 0) kv[tok.Substring(0, i)] = Unesc(tok.Substring(i + 1));
            }
            return kv;
        }

        /// <summary>A v2 line from raw fields (escaped, sorted by key).</summary>
        public static string BuildLine(IDictionary<string, string> fields) =>
            "ship " + string.Join(" ", fields.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => p.Key + "=" + Esc(p.Value)));

        /// <summary>Parse one line (v2 key=value, or the v1 positional format); null if unusable.</summary>
        public static ShipRecord FromLine(string line)
        {
            line = (line ?? "").Trim();
            if (line.StartsWith("ship|", StringComparison.Ordinal)) return FromV1(line);
            var kv = ParseFields(line);
            if (kv == null) return null;
            foreach (var k in kv.Where(p => p.Value.Length == 0).Select(p => p.Key).ToList()) kv.Remove(k);
            if (!kv.TryGetValue("id", out var ids) || !int.TryParse(ids, NumberStyles.Integer, Inv, out var id) || id <= 0) return null;

            var r = new ShipRecord { EntityId = id };
            r.Playfield = Take(kv, "pf");
            r.Name = Take(kv, "name");
            r.Since = Take(kv, "since");
            kv.Remove("id");
            var kind = Take(kv, "order.kind");
            if (kind == "follow" || kind == "hold")
            {
                var o = new ShipOrder { Kind = kind, LeaderSteamId = Take(kv, "order.leader"), LeaderName = Take(kv, "order.leaderName") };
                o.Distance = TakeF(kv, "order.dist", 150f);
                o.Cruise = TakeF(kv, "order.cruise", 40f);
                o.HoldX = TakeF(kv, "order.x", 0); o.HoldY = TakeF(kv, "order.y", 0); o.HoldZ = TakeF(kv, "order.z", 0);
                r.Order = o;
            }
            else if (kind.Length > 0) kv["order.kind"] = kind;   // an order kind we don't know: keep it, don't run it
            foreach (var p in kv) r.Extra[p.Key] = p.Value;
            return r;
        }

        /// <summary>v1 (oldest format): ship|id|pf|kind|leaderSteam|leaderName|dist|cruise|x|y|z.</summary>
        static ShipRecord FromV1(string line)
        {
            var p = line.Split('|');
            if (p.Length < 11 || !int.TryParse(p[1], NumberStyles.Integer, Inv, out var id) || id <= 0) return null;
            string U(string s) => s.Replace("%0D", "\r").Replace("%0A", "\n").Replace("%7C", "|").Replace("%25", "%");
            float G(string s) => float.TryParse(s, NumberStyles.Float, Inv, out var v) ? v : 0f;
            var kind = U(p[3]);
            var r = new ShipRecord { EntityId = id, Playfield = U(p[2]) };
            if (kind == "follow" || kind == "hold")
                r.Order = new ShipOrder { Kind = kind, LeaderSteamId = U(p[4]), LeaderName = U(p[5]), Distance = G(p[6]), Cruise = G(p[7]), HoldX = G(p[8]), HoldY = G(p[9]), HoldZ = G(p[10]) };
            return r;
        }

        static string Take(Dictionary<string, string> kv, string k) { if (!kv.TryGetValue(k, out var v)) return ""; kv.Remove(k); return v; }
        static float TakeF(Dictionary<string, string> kv, string k, float dflt)
        {
            var s = Take(kv, k);
            return float.TryParse(s, NumberStyles.Float, Inv, out var v) && !float.IsNaN(v) && !float.IsInfinity(v) ? v : dflt;
        }
        static string F(float v) => v.ToString("R", Inv);
        internal static string Esc(string s) => (s ?? "").Replace("%", "%25").Replace(" ", "%20").Replace("=", "%3D").Replace("|", "%7C")
                                               .Replace("\n", "%0A").Replace("\r", "%0D").Replace("\t", "%09");
        internal static string Unesc(string s) => (s ?? "").Replace("%09", "\t").Replace("%0D", "\r").Replace("%0A", "\n").Replace("%7C", "|")
                                                  .Replace("%3D", "=").Replace("%20", " ").Replace("%25", "%");
    }

    /// <summary>
    /// Every ship we own, keyed by entity id. Owned by the Dedi (one writer; PfServers come and go).
    /// </summary>
    public sealed class ShipRegistry
    {
        public const string Header = "LivingSystems registry v2";
        public const string HeaderV1 = "LivingSystems registry v1";

        readonly Dictionary<int, ShipRecord> ships = new Dictionary<int, ShipRecord>();

        public int Count => ships.Count;
        public IEnumerable<ShipRecord> All => ships.Values.Select(r => r.Clone());
        public IEnumerable<ShipRecord> InPlayfield(string playfield) =>
            ships.Values.Where(r => r.Playfield == playfield).Select(r => r.Clone());
        public ShipRecord Get(int entityId) => ships.TryGetValue(entityId, out var r) ? r.Clone() : null;

        /// <summary>
        /// Add or update. The sender's known fields win; <see cref="ShipRecord.Extra"/> fields it didn't send are kept
        /// (an older PfServer can't wipe what a newer one wrote). Returns true when something changed.
        /// </summary>
        public bool Set(ShipRecord r)
        {
            if (r == null || r.EntityId <= 0) return false;
            var merged = r.Clone();
            if (ships.TryGetValue(r.EntityId, out var cur))
            {
                foreach (var p in cur.Extra) if (!merged.Extra.ContainsKey(p.Key)) merged.Extra[p.Key] = p.Value;
                if (string.IsNullOrEmpty(merged.Since)) merged.Since = cur.Since;
                if (cur.Equals(merged)) return false;
            }
            ships[r.EntityId] = merged;
            return true;
        }

        public bool Remove(int entityId) => ships.Remove(entityId);

        /// <summary>
        /// Partial update: set only the keys in <paramref name="patchLine"/> ("ship id=… key=value …"); an EMPTY value
        /// deletes the key. Sections don't clobber each other (a faction change can't wipe an order and vice versa),
        /// and keys this version doesn't know work the same way. Creates the record if new. True if anything changed.
        /// </summary>
        public bool Patch(string patchLine)
        {
            var patch = ShipRecord.ParseFields(patchLine);
            if (patch == null || !patch.TryGetValue("id", out var ids) || !int.TryParse(ids, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id <= 0)
                return false;
            var fields = ships.TryGetValue(id, out var cur) ? ShipRecord.ParseFields(ShipRecord.ToLine(cur)) : new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var p in patch)
                if (p.Value.Length == 0) fields.Remove(p.Key); else fields[p.Key] = p.Value;
            fields["id"] = id.ToString(CultureInfo.InvariantCulture);
            var rec = ShipRecord.FromLine(ShipRecord.BuildLine(fields));
            if (rec == null) return false;
            if (cur != null && cur.Equals(rec)) return false;
            ships[id] = rec;   // replace, not merge: deletions must stick
            return true;
        }

        public string Serialize()
        {
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            foreach (var r in ships.Values.OrderBy(x => x.EntityId)) sb.Append(ShipRecord.ToLine(r)).Append('\n');
            return sb.ToString();
        }

        /// <summary>
        /// Parse a saved registry (v2, or v1 which is upgraded on the next save). Bad lines are skipped and counted;
        /// an unknown header gives an empty registry — never guess at a format we didn't write.
        /// </summary>
        public static ShipRegistry Parse(string text, out int skipped)
        {
            skipped = 0;
            var r = new ShipRegistry();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            var head = lines.Length > 0 ? lines[0].Trim() : "";
            if (head != Header && head != HeaderV1) { skipped = lines.Count(l => l.Trim().Length > 0); return r; }
            foreach (var l in lines.Skip(1))
            {
                if (l.Trim().Length == 0) continue;
                var rec = ShipRecord.FromLine(l);
                if (rec == null) { skipped++; continue; }
                r.ships[rec.EntityId] = rec;
            }
            return r;
        }

        /// <summary>
        /// Write atomically: a temp file next to the target, then swap it in. A crash mid-write leaves the old
        /// file intact; the previous version is kept as <c>.bak</c>.
        /// </summary>
        public void Save(string path)
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, Serialize(), new UTF8Encoding(false));
            if (File.Exists(path)) File.Replace(tmp, path, path + ".bak");
            else File.Move(tmp, path);
        }

        /// <summary>Load; a missing file is an empty registry. Falls back to <c>.bak</c> if the main file is unreadable.</summary>
        public static ShipRegistry Load(string path, out int skipped)
        {
            skipped = 0;
            foreach (var p in new[] { path, path + ".bak" })
            {
                if (!File.Exists(p)) continue;
                try
                {
                    var r = Parse(File.ReadAllText(p, Encoding.UTF8), out skipped);
                    if (r.Count > 0 || skipped == 0) return r;
                }
                catch (IOException) { }
            }
            return new ShipRegistry();
        }
    }
}
