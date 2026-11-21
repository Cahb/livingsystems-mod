using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LivingSystems.Core
{
    /// <summary>One line of a menu page: plain text, or a link that runs <see cref="Do"/> when clicked.</summary>
    public sealed class MenuLine
    {
        public string Text;
        public string Id;      // null = plain text
        public Action Do;
    }

    /// <summary>
    /// A dialog page drawn in the body text: the game's dialog has 3 buttons at most (kept fixed: Back · Main · Close),
    /// but any number of clickable &lt;link&gt; lines (verified 2026-11-18). Long pages split into pages of
    /// <see cref="PerPage"/> lines with a ◀ Prev · n / m · Next ▶ footer.
    /// </summary>
    public sealed class MenuPage
    {
        public string Title = "";
        /// <summary>Text above the lines (a reply, a description).</summary>
        public string Body = "";
        public readonly List<MenuLine> Lines = new List<MenuLine>();
        public int PerPage = 10;

        public MenuPage Text(string text) { Lines.Add(new MenuLine { Text = text }); return this; }
        public MenuPage Link(string text, Action act) { Lines.Add(new MenuLine { Text = text, Id = "l" + Lines.Count, Do = act }); return this; }

        public int Pages => Math.Max(1, (Lines.Count + PerPage - 1) / PerPage);

        public MenuLine Find(string id) => Lines.FirstOrDefault(l => l.Id == id);
    }

    public static class MenuRender
    {
        public const string LinkColour = "#4af", DimColour = "#999";
        public const string Next = "pg:next", Prev = "pg:prev";

        /// <summary>
        /// The dialog body for page <paramref name="page"/> (0-based, clamped). Every link id carries <paramref name="version"/>
        /// so a click from a dialog that was replaced since can be told apart (<see cref="Parse"/>).
        /// </summary>
        public static string Body(MenuPage p, int page, int version)
        {
            page = Math.Max(0, Math.Min(page, p.Pages - 1));
            var sb = new StringBuilder();
            if (!string.IsNullOrEmpty(p.Body)) sb.Append(p.Body).Append("\n\n");
            foreach (var l in p.Lines.Skip(page * p.PerPage).Take(p.PerPage))
            {
                if (l.Id == null) sb.Append(l.Text).Append('\n');
                else sb.Append(Ref(version, l.Id, $"<color={LinkColour}>▶ {l.Text}</color>")).Append('\n');
            }
            if (p.Pages > 1)
            {
                sb.Append('\n');
                sb.Append(page > 0 ? Ref(version, Prev, $"<color={LinkColour}>◀ Prev</color>") : $"<color={DimColour}>◀ Prev</color>");
                sb.Append($"<pos=42%>{page + 1} / {p.Pages}<pos=80%>");
                sb.Append(page < p.Pages - 1 ? Ref(version, Next, $"<color={LinkColour}>Next ▶</color>") : $"<color={DimColour}>Next ▶</color>");
            }
            return sb.ToString().TrimEnd('\n');
        }

        static string Ref(int version, string id, string inner) => $"<link=\"{version}:{id}\">{inner}</link>";

        /// <summary>A clicked link id back into (version, line id); false if it isn't one of ours.</summary>
        public static bool Parse(string link, out int version, out string id)
        {
            version = -1; id = null;
            if (string.IsNullOrEmpty(link)) return false;
            var i = link.IndexOf(':');
            if (i <= 0 || !int.TryParse(link.Substring(0, i), out version)) return false;
            id = link.Substring(i + 1);
            return id.Length > 0;
        }
    }
}
