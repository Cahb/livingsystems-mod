using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;
using LivingSystems.Core.Sim;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// Wrecks and hub hulls as bodies (kind "wreck", id = the sim's wreck id). The Dedi spawns the hull when a player comes
    /// near its beacon ("simwant") and hands it over as an adopt ("fill=1": strip, batter, load the hold); a hull from a live
    /// death is adopted the same way once the Dedi has its record. <see cref="WreckBrain"/>: fill, cash, the gang's post;
    /// the driver: claimed (a player's core), blown up (gone with a player near), cleared (nobody near 5 min — its crates go
    /// back on the record: what's left, never the original load again), out of view (record kept, emptied).
    /// </summary>
    sealed partial class SimLive
    {
        WreckBrain wreckBrain;

        WreckLive AddWreck(WreckLive w)
        {
            var b = w.B;
            b.Id = w.Id; b.Kind = "wreck"; b.Brain = wreckBrain; b.Mind = w; b.Since = fly.Now;
            foreach (var old in bodies.Values.Where(x => x.Kind == "wreck" && x.Entity == w.Entity && x != b).ToList()) bodies.Remove(old.Id);   // a placeholder (live death) gets its name
            bodies[w.Id] = b; wrecks[w.Entity] = w;
            return w;
        }

        /// <summary>Forget a wreck without a word to the sim (a tug's cut retired it: "tugcut" says it).</summary>
        void DropWreck(int entity)
        {
            if (!wrecks.TryGetValue(entity, out var w)) return;
            wrecks.Remove(entity);
            if (bodies.TryGetValue(w.Id, out var b) && b == w.B) bodies.Remove(w.Id);
        }

        void DropWrecks()
        {
            foreach (var k in bodies.Where(kv => kv.Value.Kind == "wreck").Select(kv => kv.Key).ToList()) bodies.Remove(k);
            wrecks.Clear();
        }

        /// <summary>Everything in a structure's containers, by item key (stack data kept).</summary>
        Dictionary<string, int> ReadHold(IStructure st)
        {
            var d = new Dictionary<string, int>(StringComparer.Ordinal);
            var names = new Dictionary<int, string>();
            foreach (var kv in fly.Api.Application.GetBlockAndItemMapping() ?? new Dictionary<string, int>()) names[kv.Value] = kv.Key;
            var list = st.GetDevices(DeviceTypeName.Container);
            for (var i = 0; list != null && i < list.Count; i++)
            {
                var stacks = st.GetDevice<IContainer>(list.GetAt(i))?.GetContent();
                if (stacks == null) continue;
                foreach (var it in stacks)
                    if (it.count > 0 && names.TryGetValue(it.id, out var n)) { var k = World.StackKey(n, it.ammo, it.decay); d[k] = (d.TryGetValue(k, out var have) ? have : 0) + it.count; }
            }
            return d;
        }

        sealed class WreckBrain : Brain, IAdopts
        {
            public WreckBrain(SimLive s) : base(s) { }
            public override string Kind => "wreck";
            static WreckLive W(Body b) => (WreckLive)b.Mind;
            /// <summary>The one exception to the body ranges' 5 s: a wreck stays 5 min after you leave (fly off, come back).</summary>
            public override double OutGrace => WreckCleanup;
            public override bool WaitUnseen => true;
            public override bool CallOffIfNeverSpawned => false;
            public override bool ReadyCheck(Body b, IEntity e, double now) => true;
            /// <summary>Hub hulls stay (the sim swaps them while nobody's near); a hull not filled yet isn't cleared.</summary>
            public override bool Pinned(Body b, IEntity e, double now) => W(b).Keep || !W(b).Filled;
            public override bool StaysOnSwitch(Body b) => W(b).Keep;

            public override bool Claimed(Body b, IEntity e)
            {
                var w = W(b);
                if (!w.Filled) return false;
                string core; try { core = e.Structure?.CoreType.ToString() ?? "None"; } catch { core = "None"; }
                if (w.Mode == "post")
                {   // the gang's own core is in it: only a player's counts
                    var mine = false; try { mine = Factions.IsPlayer((int)e.Faction.Group); } catch { }
                    return mine && core != "None";
                }
                return core != "None" && core != "NoFaction";
            }

            public override void LiveTick(Body b, IEntity e, double now) => S.WreckLiveTick(W(b), e, now);

            /// <summary>Its crates as they are now (what a player left in it), or as last read when the hull's out of view.</summary>
            public override string Hold(Body b, IEntity e)
            {
                var w = W(b);
                if (!w.Filled) return "";
                if (e?.Structure == null) return w.LastRead ? w.LastHold : "";   // out of view: its crates as last read
                try { return World.FormatHold(S.ReadHold(e.Structure)); } catch (Exception ex) { S.log($"SIM wreck {b.Id}: reading its crates threw {ex.Message}"); return w.LastRead ? w.LastHold : ""; }
            }

            public override string OnBack(Body b, IEntity e, string why, double now)
            {
                var w = W(b);
                S.wrecks.Remove(w.Entity); S.refills.Remove(w.Entity);
                S.log($"SIM wreck {w.Id} {w.Entity}{(w.Keep ? " [" + w.Mode + "]" : "")} back to the sim ({why})");
                return BodyProgress.Write(new[] { new KeyValuePair<string, string>("cut", w.Cut.ToString("0.###", CultureInfo.InvariantCulture)), new KeyValuePair<string, string>("read", w.Filled && (e != null || w.LastRead) ? "1" : "") });
            }

            public override void OnDead(Body b, string how)
            {
                var w = W(b);
                S.wrecks.Remove(w.Entity); S.refills.Remove(w.Entity);
                S.log($"SIM wreck {w.Id} {w.Entity} {how}");
            }

            /// <summary>The Dedi's hull: "fill=1" a fresh spawn (strip, batter, load); else one picked up again (filled already).</summary>
            public object Adopt(Body b, BodyMsg m)
            {
                if (m.Entity <= 0) return null;
                var p = BodyProgress.Read(m.Progress);
                var fill = BodyProgress.Get(p, "fill") == "1";
                if (S.wrecks.TryGetValue(m.Entity, out var had) && had.Id == m.Id) return null;   // already ours
                var w = new WreckLive
                {
                    Id = m.Id, Since = S.fly.Now, Filled = !fill, Adopted = !fill, Mode = BodyProgress.Get(p, "mode"),
                    Item = BodyProgress.Get(p, "item"), Amt = BodyProgress.Int(p, "amt"), Hold = m.Hold, Prewrecked = BodyProgress.Get(p, "pre") == "1",
                    Cash = BodyProgress.Int(p, "cash"), Cut = Mathf.Clamp01((float)BodyProgress.Num(p, "cut")), CashDue = BodyProgress.Int(p, "cashdue"), NoStores = BodyProgress.Get(p, "nostores") == "1",
                };
                w.B = b; b.Kind = "wreck"; b.Entity = m.Entity; b.Offline = false; b.Adopted = !fill; b.EverLive = true; b.Seen = false;
                foreach (var old in S.bodies.Values.Where(x => x.Kind == "wreck" && x.Entity == m.Entity && x.Id != m.Id).ToList()) S.bodies.Remove(old.Id);
                S.wrecks[m.Entity] = w;
                S.log(fill ? $"SIM wreck {m.Id} = {m.Entity}: {w.Amt} {w.Item}{(w.Mode.Length > 0 ? " [" + w.Mode + "]" : "")}" : $"SIM wreck {m.Id} re-adopted as {m.Entity}");
                return w;
            }
        }
    }
}
