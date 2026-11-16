using System;
using System.Collections.Generic;
using System.Text;

namespace LivingSystems.Core
{
    /// <summary>
    /// Top-down map in monospace text for an LCD (system map / star map): you at the centre, nose up — the world turns as
    /// you turn, so whatever you face sits straight above you on the forward line. Rays from you to each place, labels
    /// beside the glyphs where there's room. Pure.
    /// </summary>
    public static class MapAscii
    {
        public sealed class Place
        {
            /// <summary>World offset from you (x east-ish, z north-ish; same axes as the playfield), any unit.</summary>
            public float X, Z;
            public char Glyph = '●';
            public string Color = "#B0BEC5", Label = "";
            /// <summary>Draw a dotted ray from you to it.</summary>
            public bool Ray = true;
        }

        public const string RayColor = "#37474F", AxisColor = "#455A64", SelfColor = "#FFEB3B";

        /// <summary>
        /// <paramref name="yaw"/> = your heading in degrees (atan2 of forward x, z). <paramref name="range"/> = world distance
        /// at the edge; beyond that a place is pinned to the edge in its direction.
        /// </summary>
        public static List<string> Render(IList<Place> places, float yaw, float range, int width, int height, string plainColor = RayColor, bool sqrt = false)
        {
            var ch = new char[height, width];
            var col = new string[height, width];
            for (var r = 0; r < height; r++) for (var c = 0; c < width; c++) { ch[r, c] = ' '; col[r, c] = plainColor; }
            var cx = (width - 1) / 2; var cy = (height - 1) / 2;
            var rx = cx - 1f;
            var rz = Math.Min(cy - 0.5f, rx * RadarAscii.CellAspect);   // a circle on screen (cells are taller than wide)
            var a = yaw * Math.PI / 180;
            var cos = (float)Math.Cos(a); var sin = (float)Math.Sin(a);

            bool In(int r, int c) => r >= 0 && r < height && c >= 0 && c < width;
            void Put(int r, int c, char g, string color) { if (In(r, c)) { ch[r, c] = g; col[r, c] = color; } }
            bool Free(int r, int c) => In(r, c) && (ch[r, c] == ' ' || ch[r, c] == '·' || ch[r, c] == '┊');

            // forward axis: straight up from you — turn until a place sits on it and you're flying at it
            for (var r = cy - 1; r >= 0; r--) Put(r, cx, '┊', AxisColor);

            var spots = new List<int[]>();
            foreach (var p in places)
            {
                var x = p.X * cos - p.Z * sin;   // into your frame: forward = up
                var z = p.X * sin + p.Z * cos;
                var d = (float)Math.Sqrt(x * x + z * z);
                // linear, or √ (close ones spread out, like the radar); past the edge: pinned to it
                var k = d < 1e-6f ? 0f : sqrt ? (float)Math.Sqrt(Math.Min(1f, d / range)) * range / d : d > range ? range / d : 1f;
                spots.Add(new[] { (int)Math.Round(cy - z * k / range * rz), (int)Math.Round(cx + x * k / range * rx) });
            }
            // rays (under everything): a dot every other cell along the line
            for (var i = 0; i < places.Count; i++)
            {
                if (!places[i].Ray) continue;
                int r1 = spots[i][0], c1 = spots[i][1];
                var steps = Math.Max(Math.Abs(r1 - cy) * 2, Math.Abs(c1 - cx));
                for (var t = 2; t < steps; t += 2)
                {
                    var r = (int)Math.Round(cy + (r1 - cy) * (double)t / steps); var c = (int)Math.Round(cx + (c1 - cx) * (double)t / steps);
                    if (Free(r, c)) Put(r, c, '·', RayColor);
                }
            }
            for (var i = 0; i < places.Count; i++) Put(spots[i][0], spots[i][1], places[i].Glyph, places[i].Color);
            Put(cy, cx, '▲', SelfColor);
            // labels: right of the glyph if it fits, else left; skipped where they'd overwrite something
            for (var i = 0; i < places.Count; i++)
            {
                var label = places[i].Label;
                if (label.Length == 0) continue;
                int r = spots[i][0], c = spots[i][1];
                bool Fits(int from) { for (var j = 0; j < label.Length; j++) if (!Free(r, from + j)) return false; return true; }
                var at = Fits(c + 2) ? c + 2 : Fits(c - 1 - label.Length) ? c - 1 - label.Length : -1;
                if (at < 0) continue;
                for (var j = 0; j < label.Length; j++) Put(r, at + j, label[j], places[i].Color);
            }
            return Lines(ch, col, width, height, plainColor);
        }

        /// <summary>Runs of one colour → one short &lt;#RRGGBB&gt; tag; runs in <paramref name="plain"/> (the pane's own
        /// text colour) need none; trailing spaces dropped. The LCD keeps ~2000 characters: every tag counts.</summary>
        public static List<string> Lines(char[,] ch, string[,] col, int width, int height, string plain)
        {
            var lines = new List<string>(height);
            for (var r = 0; r < height; r++)
            {
                var sb = new StringBuilder();
                var c = 0;
                while (c < width)
                {
                    var color = col[r, c];
                    var run = new StringBuilder();
                    while (c < width && (col[r, c] == color || ch[r, c] == ' ')) { run.Append(ch[r, c]); c++; }
                    var text = run.ToString();
                    if (text.Trim().Length == 0 || color == plain) sb.Append(text);
                    else { var body = text.TrimEnd(); sb.Append('<').Append(color).Append('>').Append(body).Append("</color>").Append(text.Substring(body.Length)); }
                }
                lines.Add(sb.ToString().TrimEnd());
            }
            return lines;
        }

        /// <summary>Your heading in degrees from a forward vector (x, z): 0 = +z, 90 = +x.</summary>
        public static float Yaw(float fx, float fz) => (float)(Math.Atan2(fx, fz) * 180 / Math.PI);
    }
}
