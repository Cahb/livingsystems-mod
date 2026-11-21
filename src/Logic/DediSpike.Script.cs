using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;

namespace LivingSystems
{
    /// <summary>
    /// Hooks for the test script runner (<see cref="DediScript"/>): the private request / menu plumbing it needs, kept in
    /// one place so the chat and hail code stay untouched.
    /// </summary>
    sealed partial class DediSpike
    {
        public bool LegacyReady => legacy != null;

        /// <summary>A legacy request; the returned seq is matched against Event_Ok / Event_Error by the caller.</summary>
        public ushort ScriptRequest(CmdId c, object d) => Request(c, d);

        public void ScriptPlayerInfo(int pid, Action<PlayerInfo> then) => RequestPlayerInfo(pid, then);

        /// <summary>
        /// A plain game spawn (not registered as one of our flown ships, no «LS» tag): a test fixture such as a wreck.
        /// Faction group is set by the game from <paramref name="group"/>; no PfServer SetFaction follow-up.
        /// </summary>
        public void ScriptSpawn(string playfield, string prefab, PVector3 pos, PVector3 rot, string name, byte group, Action<int, ushort> spawned)
        {
            var type = LooksLikeBase(prefab) ? EntityType.BA : prefab.StartsWith("SV", StringComparison.OrdinalIgnoreCase) ? EntityType.SV : EntityType.CV;
            RequestNewEntityId(newId =>
            {
                var ownDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(typeof(LivingSystemsMod).Assembly.Location) ?? ".", "prefabs");
                var useOwn = System.IO.File.Exists(System.IO.Path.Combine(ownDir, prefab + ".epb"));
                var s = Request(CmdId.Request_Entity_Spawn, new EntitySpawnInfo
                {
                    forceEntityId = newId, playfield = playfield, pos = pos, rot = rot, name = name, prefabDir = useOwn ? ownDir : null,
                    type = (byte)type, entityTypeName = "", prefabName = prefab, factionGroup = group, factionId = 0,
                });
                log($"script spawn seq={s} id={newId} '{name}' prefab={prefab} type={type} group={group} pf='{playfield}' at {pos.x:0},{pos.y:0},{pos.z:0}");
                spawned(newId, s);
            });
        }

        static string StripTags(string s) => System.Text.RegularExpressions.Regex.Replace(s ?? "", "<[^>]+>", "").Trim();

        /// <summary>The player's open hail page as one line: title, body, numbered links (what <see cref="MenuClick"/> takes).</summary>
        public string MenuDump(int pid)
        {
            if (!menus.TryGetValue(pid, out var m) || m.Page == null) return "no menu open";
            var links = m.Page.Lines.Where(l => l.Id != null).ToList();
            var parts = new List<string> { $"'{StripTags(m.Page.Title)}'" };
            var body = StripTags(m.Page.Body).Replace("\n", " / ");
            if (body.Length > 0) parts.Add("body: " + body);
            for (int i = 0; i < links.Count; i++) parts.Add($"[{i + 1}] {StripTags(links[i].Text)}");
            var plain = m.Page.Lines.Where(l => l.Id == null && StripTags(l.Text).Length > 0).Select(l => StripTags(l.Text)).ToList();
            if (plain.Count > 0) parts.Add("text: " + string.Join(" / ", plain));
            return string.Join(" | ", parts);
        }

        /// <summary>
        /// Click on the open hail page: "back" / "main" / "close" = the buttons, a number = that link (1-based, as in
        /// <see cref="MenuDump"/>), anything else = the first link whose text contains it (case-insensitive, tags ignored).
        /// Null = done, else why not.
        /// </summary>
        public string MenuClick(int pid, string sel)
        {
            var low = (sel ?? "").Trim().ToLowerInvariant();
            if (low == "back" || low == "main" || low == "close")
            {
                if (!menus.ContainsKey(pid) && low != "main") return "no menu open";
                OnMenu(low == "back" ? 0 : low == "main" ? 1 : 2, null, null, pid, 0);
                return null;
            }
            if (!menus.TryGetValue(pid, out var m) || m.Page == null) return "no menu open";
            var links = m.Page.Lines.Where(l => l.Id != null).ToList();
            MenuLine line = null;
            if (int.TryParse(low, out var n)) { if (n >= 1 && n <= links.Count) line = links[n - 1]; }
            else line = links.FirstOrDefault(l => StripTags(l.Text).ToLowerInvariant().Contains(low));
            if (line == null) return $"no link '{sel}' on '{StripTags(m.Page.Title)}' ({links.Count} links)";
            log($"script menu {pid}: click '{StripTags(line.Text)}' on '{StripTags(m.Page.Title)}'");
            try { line.Do?.Invoke(); } catch (Exception ex) { log($"script menu line threw: {ex}"); return "the line threw: " + ex.Message; }
            return null;
        }
    }
}
