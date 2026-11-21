using System;
using System.Collections.Generic;
using System.Globalization;

namespace LivingSystems.Core.Sim
{
    /// <summary>
    /// Salvage yields and item prices read from the scenario's own config text (BlocksConfig.ecf, Templates.ecf,
    /// ItemsConfig.ecf), so a salvaged block returns what vanilla's multitool would and loot is valued at real prices.
    /// Pure text in, numbers out: the caller reads the files.
    /// </summary>
    public sealed class Recipes
    {
        /// <summary>One ECF entry: <c>{ [+]Kind Id: n, Name: X, Ref: Y</c> with its properties and nested <c>{ Child ...</c> blocks.</summary>
        public sealed class Entry
        {
            public string Kind = "";
            public string Name = "";
            public string Ref = "";
            public readonly Dictionary<string, string> Props = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            public readonly List<Entry> Children = new List<Entry>();

            public Entry Child(string name)
            {
                foreach (var c in Children) if (string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)) return c;
                return null;
            }
        }

        // vanilla multitool salvage ReturnFactor per tier
        public const double SalvageT1 = 0.5;
        public const double SalvageT2 = 0.75;

        readonly Dictionary<string, Entry> blocks = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Entry> templates = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Entry> items = new Dictionary<string, Entry>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, Dictionary<string, double>> perBlock = new Dictionary<string, Dictionary<string, double>>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string> groupOf = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> resolving = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, double> prices = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        public int BlockCount { get { return blocks.Count; } }
        public int TemplateCount { get { return templates.Count; } }
        public int ItemCount { get { return items.Count; } }
        public IEnumerable<string> BlockNames { get { return blocks.Keys; } }

        /// <summary>
        /// The save's config folder: the scenario under <paramref name="contentDir"/>/Scenarios whose GalaxyConfig.ecf is the
        /// save's own copy (a save keeps that one file from its scenario), else the game's <c>Content/Configuration</c>.
        /// </summary>
        public static string FindConfigDir(string contentDir, string saveDir)
        {
            var vanilla = System.IO.Path.Combine(contentDir, "Configuration");
            try
            {
                var mine = System.IO.Path.Combine(saveDir, "Content", "Configuration", "GalaxyConfig.ecf");
                if (!System.IO.File.Exists(mine)) return vanilla;
                var text = System.IO.File.ReadAllText(mine);
                foreach (var sc in System.IO.Directory.GetDirectories(System.IO.Path.Combine(contentDir, "Scenarios")))
                {
                    var dir = System.IO.Path.Combine(sc, "Content", "Configuration");
                    var g = System.IO.Path.Combine(dir, "GalaxyConfig.ecf");
                    if (System.IO.File.Exists(g) && System.IO.File.Exists(System.IO.Path.Combine(dir, "Templates.ecf")) && System.IO.File.ReadAllText(g) == text) return dir;
                }
            }
            catch { }
            return vanilla;
        }

        public static Recipes Parse(string blocksText, string templatesText, string itemsText)
        {
            var r = new Recipes();
            Index(ParseEcf(blocksText), "Block", r.blocks, true);
            Index(ParseEcf(templatesText), "Template", r.templates, false);
            Index(ParseEcf(itemsText), "Item", r.items, true);
            foreach (var g in r.blocks.Values)
            {
                string list;
                if (!g.Props.TryGetValue("ChildBlocks", out list)) continue;
                foreach (var member in list.Split(','))
                {
                    var m = member.Trim();
                    if (m.Length > 0 && !r.groupOf.ContainsKey(m)) r.groupOf[m] = g.Name;
                }
            }
            return r;
        }

        // A later entry with the same name (e.g. a `{ Block Name: X` override) merges its properties over the earlier
        // one for blocks/items; for templates the later one replaces the earlier wholesale.
        static void Index(List<Entry> entries, string kind, Dictionary<string, Entry> into, bool merge)
        {
            foreach (var e in entries)
            {
                if (!string.Equals(e.Kind, kind, StringComparison.OrdinalIgnoreCase) || e.Name.Length == 0) continue;
                Entry old;
                if (merge && into.TryGetValue(e.Name, out old))
                {
                    if (e.Ref.Length > 0) old.Ref = e.Ref;
                    foreach (var kv in e.Props) old.Props[kv.Key] = kv.Value;
                    old.Children.AddRange(e.Children);
                }
                else into[e.Name] = e;
            }
        }

        /// <summary>
        /// Items returned by salvaging one block: its crafting template's inputs × factor, floored per item (an input
        /// worth less than one whole unit gives nothing). Template = TemplateRoot (Ref-inherited), else the first name
        /// up the block's Ref chain that has a template, else its block group's (ChildBlocks / ParentBlocks). A template's OutputCount divides the inputs. Unknown → empty.
        /// </summary>
        public Dictionary<string, int> Yield(string blockName, double factor)
        {
            var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var kv in PerBlock(blockName))
            {
                int n = (int)Math.Floor(kv.Value * factor + 1e-9);
                if (n > 0) result[kv.Key] = n;
            }
            return result;
        }

        /// <summary>Unscaled template inputs per one block (fractional when OutputCount &gt; 1). Cached per block name.</summary>
        public Dictionary<string, double> PerBlock(string blockName)
        {
            Dictionary<string, double> cached;
            if (blockName == null) return new Dictionary<string, double>();
            if (perBlock.TryGetValue(blockName, out cached)) return cached;
            var inputs = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
            var t = TemplateFor(blockName);
            var child = t == null ? null : t.Child("Inputs");
            if (child != null)
            {
                double outCount = Num(Prop(t, "OutputCount", templates));
                if (double.IsNaN(outCount) || outCount < 1) outCount = 1;
                foreach (var kv in child.Props)
                {
                    double n = Num(kv.Value);
                    if (!double.IsNaN(n) && n > 0) inputs[kv.Key] = n / outCount;
                }
            }
            perBlock[blockName] = inputs;
            return inputs;
        }

        /// <summary>The template a block crafts from (see <see cref="Yield"/>), or null.</summary>
        public Entry TemplateFor(string blockName)
        {
            Entry t;
            Entry b;
            if (!blocks.TryGetValue(blockName, out b))
                return templates.TryGetValue(blockName, out t) ? t : null;
            string root = Prop(b, "TemplateRoot", blocks);
            if (root != null && templates.TryGetValue(root, out t)) return t;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (var e = b; e != null && seen.Add(e.Name); e = Parent(e, blocks))
                if (templates.TryGetValue(e.Name, out t)) return t;
            // block-group members (windows, antennas, ...) craft from the group block: listed in the group's
            // ChildBlocks, or naming it in their own ParentBlocks (Ref-inherited); with several groups the first wins
            string group;
            if (!groupOf.TryGetValue(b.Name, out group))
            {
                var parents = Prop(b, "ParentBlocks", blocks);
                if (parents != null) group = parents.Split(',')[0].Trim();
            }
            if (string.IsNullOrEmpty(group) || !resolving.Add(b.Name)) return null;
            try { return TemplateFor(group); }
            finally { resolving.Remove(b.Name); }
        }

        /// <summary>An item's volume in litres (ItemsConfig / BlocksConfig "Volume", Ref-inherited); NaN = unknown.</summary>
        public double Volume(string item)
        {
            double v;
            if (item == null) return double.NaN;
            if (volumes.TryGetValue(item, out v)) return v;
            Entry e;
            v = double.NaN;
            if (items.TryGetValue(item, out e)) v = Num(Prop(e, "Volume", items));
            if ((double.IsNaN(v) || v <= 0) && blocks.TryGetValue(item, out e)) v = Num(Prop(e, "Volume", blocks));
            if (v <= 0) v = double.NaN;
            volumes[item] = v;
            return v;
        }
        readonly Dictionary<string, double> volumes = new Dictionary<string, double>(StringComparer.Ordinal);

        /// <summary>An item's mass in kg (ItemsConfig / BlocksConfig "Mass", Ref-inherited); NaN = unknown.</summary>
        public double Mass(string item)
        {
            if (item == null) return double.NaN;
            Entry e;
            var v = double.NaN;
            if (items.TryGetValue(item, out e)) v = Num(Prop(e, "Mass", items));
            if ((double.IsNaN(v) || v <= 0) && blocks.TryGetValue(item, out e)) v = Num(Prop(e, "Mass", blocks));
            return v <= 0 ? double.NaN : v;
        }

        /// <summary>A block's cargo space in litres (BlocksConfig "VolumeCapacity", Ref-inherited: a Container Controller 8000,
        /// each Container Extension 8000 more); 0 = not a container.</summary>
        public double BlockCapacity(string block)
        {
            Entry e;
            if (block == null || !blocks.TryGetValue(block, out e)) return 0;
            var v = Num(Prop(e, "VolumeCapacity", blocks));
            return double.IsNaN(v) || v <= 0 ? 0 : v;
        }

        /// <summary>An item's own stack size (ItemsConfig / BlocksConfig "StackSize", Ref-inherited); 0 = unknown.</summary>
        public int StackSize(string item)
        {
            if (item == null) return 0;
            Entry e;
            var v = double.NaN;
            if (items.TryGetValue(item, out e)) v = Num(Prop(e, "StackSize", items));
            if ((double.IsNaN(v) || v <= 0) && blocks.TryGetValue(item, out e)) v = Num(Prop(e, "StackSize", blocks));
            return double.IsNaN(v) || v <= 0 ? 0 : (int)v;
        }

        /// <summary>MarketPrice of an item (ItemsConfig, Ref-inherited), else of a block (BlocksConfig); NaN when unknown or 0.</summary>
        public double Price(string item)
        {
            double p;
            if (item == null) return double.NaN;
            if (prices.TryGetValue(item, out p)) return p;
            Entry e;
            p = double.NaN;
            if (items.TryGetValue(item, out e)) p = Num(Prop(e, "MarketPrice", items));
            if ((double.IsNaN(p) || p <= 0) && blocks.TryGetValue(item, out e)) p = Num(Prop(e, "MarketPrice", blocks));
            if (p <= 0) p = double.NaN;
            prices[item] = p;
            return p;
        }

        /// <summary>Sum of count × price over a bag of items; items without a known price count as 0.</summary>
        public double Value(Dictionary<string, int> bag)
        {
            double sum = 0;
            foreach (var kv in bag)
            {
                double p = Price(kv.Key);
                if (!double.IsNaN(p)) sum += p * kv.Value;
            }
            return sum;
        }

        static Entry Parent(Entry e, Dictionary<string, Entry> table)
        {
            Entry p;
            return e.Ref.Length > 0 && table.TryGetValue(e.Ref, out p) ? p : null;
        }

        /// <summary>A property looked up through the Ref chain (cycle-safe), or null.</summary>
        static string Prop(Entry e, string key, Dictionary<string, Entry> table)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            for (; e != null && seen.Add(e.Name); e = Parent(e, table))
            {
                string v;
                if (e.Props.TryGetValue(key, out v)) return v;
            }
            return null;
        }

        static double Num(string s)
        {
            double d;
            if (s != null && double.TryParse(s.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return d;
            return double.NaN;
        }

        // ---- ECF text ------------------------------------------------------------------------------------------------

        /// <summary>
        /// Tolerant line parser: `#` starts a comment (outside quotes); `{ [+]Kind k: v, k: v` opens an entry (Kind
        /// "Child" nests); `}` closes; `Key: value, display: true, ...` keeps only the first value (quoted values whole).
        /// Malformed lines are skipped.
        /// </summary>
        public static List<Entry> ParseEcf(string text)
        {
            var top = new List<Entry>();
            var stack = new List<Entry>();
            if (string.IsNullOrEmpty(text)) return top;
            foreach (var raw in text.Split('\n'))
            {
                string line = StripComment(raw).Trim().TrimStart('﻿').Trim();
                while (line.Length > 0)
                {
                    if (line[0] == '{')
                    {
                        string head = line.Substring(1);
                        int close = IndexOutsideQuotes(head, '}');
                        string rest = "";
                        if (close >= 0) { rest = head.Substring(close); head = head.Substring(0, close); }
                        var e = OpenEntry(head.Trim());
                        if (stack.Count > 0) stack[stack.Count - 1].Children.Add(e); else top.Add(e);
                        stack.Add(e);
                        line = rest.Trim();
                    }
                    else if (line[0] == '}')
                    {
                        if (stack.Count > 0) stack.RemoveAt(stack.Count - 1);
                        line = line.Substring(1).Trim();
                    }
                    else
                    {
                        int close = IndexOutsideQuotes(line, '}');
                        string prop = close >= 0 ? line.Substring(0, close) : line;
                        if (stack.Count > 0) AddProp(stack[stack.Count - 1], prop);
                        line = close >= 0 ? line.Substring(close).Trim() : "";
                    }
                }
            }
            return top;
        }

        static Entry OpenEntry(string head)
        {
            var e = new Entry();
            head = head.TrimStart('+').Trim();
            int sp = 0;
            while (sp < head.Length && !char.IsWhiteSpace(head[sp])) sp++;
            e.Kind = head.Substring(0, sp);
            string rest = head.Substring(sp).Trim();
            if (string.Equals(e.Kind, "Child", StringComparison.OrdinalIgnoreCase))
            {
                e.Name = rest;
                return e;
            }
            foreach (var part in SplitOutsideQuotes(rest, ','))
            {
                int c = part.IndexOf(':');
                if (c <= 0) continue;
                string k = part.Substring(0, c).Trim(), v = Unquote(part.Substring(c + 1).Trim());
                if (k.Equals("Name", StringComparison.OrdinalIgnoreCase)) e.Name = v;
                else if (k.Equals("Ref", StringComparison.OrdinalIgnoreCase)) e.Ref = v;
                else e.Props[k] = v;
            }
            return e;
        }

        static void AddProp(Entry e, string line)
        {
            int c = line.IndexOf(':');
            if (c <= 0) return;
            string k = line.Substring(0, c).Trim();
            string v = line.Substring(c + 1).Trim();
            var parts = SplitOutsideQuotes(v, ',');
            v = parts.Count > 0 ? Unquote(parts[0].Trim()) : "";
            if (k.Equals("Ref", StringComparison.OrdinalIgnoreCase) && e.Ref.Length == 0) e.Ref = v;
            else if (k.Equals("Name", StringComparison.OrdinalIgnoreCase) && e.Name.Length == 0) e.Name = v;
            else e.Props[k] = v;
        }

        static string StripComment(string s)
        {
            int i = IndexOutsideQuotes(s, '#');
            return i >= 0 ? s.Substring(0, i) : s;
        }

        static int IndexOutsideQuotes(string s, char ch)
        {
            bool q = false;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') q = !q;
                else if (!q && s[i] == ch) return i;
            }
            return -1;
        }

        static List<string> SplitOutsideQuotes(string s, char sep)
        {
            var parts = new List<string>();
            bool q = false;
            int start = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (s[i] == '"') q = !q;
                else if (!q && s[i] == sep) { parts.Add(s.Substring(start, i - start)); start = i + 1; }
            }
            parts.Add(s.Substring(start));
            return parts;
        }

        static string Unquote(string s)
        {
            return s.Length >= 2 && s[0] == '"' && s[s.Length - 1] == '"' ? s.Substring(1, s.Length - 2) : s;
        }
    }
}
