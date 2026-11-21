using System;
using System.Collections.Generic;
using System.Linq;

namespace LivingSystems.Core.Sim
{
    /// <summary>
    /// Spilled cargo: what found no crate is split into loot-sack lots. A sack (World.ChestPrefab) is 2 crates × 64 slots
    /// shown; volume is only a gauge there (measured in game). Lots are packed short of full: spreading a load over the
    /// crates can cost a stack per split (a partial stack in each crate). One spill never makes more than
    /// <see cref="MaxSacks"/> — past that the rest is lost (said in the log), so nothing can flood a playfield.
    /// </summary>
    public static class Spill
    {
        public const int SackCrates = 2, SackSlots = 64;
        /// <summary>Stacks packed into one sack (of <see cref="SackCrates"/> × <see cref="SackSlots"/> = 128 shown).</summary>
        public const int PerSack = 120;
        public const int MaxSacks = 8;

        /// <summary>
        /// The sacks for <paramref name="items"/> (item key → count; stack data rides in the key), biggest line first, each
        /// line in its own stack size (<paramref name="stackOf"/>, by the key's base item). <paramref name="lost"/>: what didn't fit
        /// in <paramref name="maxSacks"/> sacks.
        /// </summary>
        public static List<Dictionary<string, int>> Split(IDictionary<string, int> items, Func<string, int> stackOf, out Dictionary<string, int> lost,
                                                         int perSack = PerSack, int maxSacks = MaxSacks)
        {
            var sacks = new List<Dictionary<string, int>>();
            lost = new Dictionary<string, int>(StringComparer.Ordinal);
            if (items == null || perSack <= 0 || maxSacks <= 0) return sacks;
            var lot = new Dictionary<string, int>(StringComparer.Ordinal); var stacks = 0;
            foreach (var kv in items.Where(x => x.Value > 0).OrderByDescending(x => x.Value).ThenBy(x => x.Key, StringComparer.Ordinal))
            {
                var size = Math.Max(1, stackOf?.Invoke(World.KeyBase(kv.Key, out _, out _)) ?? 1);
                var left = kv.Value;
                while (left > 0)
                {
                    if (stacks >= perSack) { sacks.Add(lot); lot = new Dictionary<string, int>(StringComparer.Ordinal); stacks = 0; }
                    if (sacks.Count >= maxSacks) { lost[kv.Key] = (lost.TryGetValue(kv.Key, out var l) ? l : 0) + left; left = 0; break; }
                    var take = Math.Min(left, (perSack - stacks) * size);
                    lot[kv.Key] = (lot.TryGetValue(kv.Key, out var had) ? had : 0) + take;
                    stacks += (take + size - 1) / size; left -= take;
                }
            }
            if (lot.Count > 0 && sacks.Count < maxSacks) sacks.Add(lot);
            return sacks;
        }

        /// <summary>Stacks a lot takes in its items' own stack sizes.</summary>
        public static int Stacks(IDictionary<string, int> lot, Func<string, int> stackOf) =>
            lot?.Where(kv => kv.Value > 0).Sum(kv => { var s = Math.Max(1, stackOf?.Invoke(World.KeyBase(kv.Key, out _, out _)) ?? 1); return (kv.Value + s - 1) / s; }) ?? 0;
    }
}
