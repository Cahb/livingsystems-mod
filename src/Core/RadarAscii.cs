using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace LivingSystems.Core
{
    /// <summary>
    /// Elite-style scanner in monospace text for an LCD: the ship's own plane drawn as tilted ellipses (rings), nose up;
    /// every blip stands on a stalk from its spot on the plane up (or down) to its real height. Pure: positions come in
    /// already in the ship's frame (x right, y up, z forward, metres).
    /// </summary>
    public static class RadarAscii
    {
        public sealed class Blip
        {
            /// <summary>Ship frame: x right, y up, z forward (metres).</summary>
            public float X, Y, Z;
            /// <summary>Velocity in the same frame (m/s) — drawn as a short tail behind the blip; 0 = none.</summary>
            public float VX, VY, VZ;
            public char Glyph = '●';
            public string Color = "#A1887F";
        }

        /// <summary>Scope ranges (outer ring, metres): it zooms to the smallest that holds every blip.</summary>
        static readonly float[] Ranges = { 750, 1500, 3000, 6000, 10000, 20000, 30000 };

        /// <summary>Outer ring radius for these blips: the smallest range that holds all of them (capped).</summary>
        public static float PickRange(IEnumerable<Blip> blips)
        {
            var far = 0f;
            foreach (var b in blips) far = Math.Max(far, (float)Math.Sqrt(b.X * b.X + b.Z * b.Z));
            foreach (var r in Ranges) if (far <= r) return r;
            return Ranges[Ranges.Length - 1];
        }

        /// <summary>
        /// Square-root scale: screen radius = sqrt(distance / range), so the close ones (the ones that matter) get most
        /// of the screen. Rings at 1/3, 2/3, 3/3 of the screen are therefore range/9, 4·range/9, range.
        /// </summary>
        static float Scale(float d, float range) => (float)Math.Sqrt(Math.Min(1f, Math.Abs(d) / range));

        /// <summary>Real distance of ring k (1..3).</summary>
        public static float RingDistance(int k, float range) => range * k * k / 9f;

        /// <summary>Cell width ÷ line height on the LCD (measured from screenshots: 0.7em cells at 80 % line height).</summary>
        public const float CellAspect = 0.7f;

        public const string RingColor = "#37474F", TailColor = "#4F6470", SelfColor = "#FFEB3B";

        /// <summary>
        /// Lines of rich text, <paramref name="width"/> × <paramref name="height"/> cells (wrap them in &lt;mspace&gt;).
        /// Blips past the outer ring sit on its edge. Later blips draw over earlier ones (pass the important ones last).
        /// </summary>
        public static List<string> Render(IList<Blip> blips, float range, int width, int height, string plainColor = null)
        {
            var ch = new char[height, width];
            var col = new string[height, width];
            for (var r = 0; r < height; r++) for (var c = 0; c < width; c++) { ch[r, c] = ' '; col[r, c] = RingColor; }
            var cx = (width - 1) / 2f;
            var cy = (height - 1) / 2f;
            var rx = cx - 1;                       // outer ring, half-width in cells
            // half-height from the width, not the row count: a cell is ~0.7 as wide as a line is tall (mspace 0.7em, line-height
            // 80 %), so rx·0.7 rows would be a circle — half of that is a 2:1 ellipse, a plane tilted ~60° at any font size
            var rz = Math.Max(2f, Math.Min(rx * CellAspect * 0.5f, (height - 1) * 0.45f));
            var ry = Math.Max(1f, (height - 1) * 0.45f);   // full range of height = this many rows

            void Put(int r, int c, char g, string color) { if (r >= 0 && r < height && c >= 0 && c < width) { ch[r, c] = g; col[r, c] = color; } }
            bool Free(int r, int c) => r >= 0 && r < height && c >= 0 && c < width && (ch[r, c] == ' ' || ch[r, c] == '·' || ch[r, c] == '˙');

            // rings: dotted ellipses at 1/3, 2/3, 3/3 of the range
            for (var k = 1; k <= 3; k++)
            {
                // a dot every ~2.4 cells of arc, measured with rows counted double (a cell is about twice as tall as wide)
                double a1 = rx * k / 3, b1 = rz * 2 * k / 3;
                var perim = Math.PI * (3 * (a1 + b1) - Math.Sqrt((3 * a1 + b1) * (a1 + 3 * b1)));
                var steps = Math.Max(8, (int)(perim / 2.4));
                for (var i = 0; i < steps; i++)
                {
                    var a = 2 * Math.PI * i / steps;
                    Put((int)Math.Round(cy - Math.Cos(a) * rz * k / 3), (int)Math.Round(cx + Math.Sin(a) * rx * k / 3), '·', RingColor);
                }
            }

            var placed = new List<int[]>();   // per blip: plane row, col, blip row
            foreach (var b in blips)
            {
                var flat = (float)Math.Sqrt(b.X * b.X + b.Z * b.Z);
                var k = flat < 1e-3f ? 0f : Scale(flat, range) / flat;   // past the edge: pinned to the outer ring
                var c = (int)Math.Round(cx + b.X * k * rx);
                var pr = (int)Math.Round(cy - b.Z * k * rz);
                var dy = (int)Math.Round(Math.Sign(b.Y) * Scale(b.Y, range) * ry);
                placed.Add(new[] { pr, c, Math.Max(0, Math.Min(height - 1, pr - dy)) });   // high above the far ring: kept on screen, stalk shorter
            }

            // tails first (under stalks and blips): two cells back along the motion on screen
            for (var i = 0; i < blips.Count; i++)
            {
                var b = blips[i];
                var sx = b.VX * rx; var sy = -(b.VZ * rz) - b.VY * ry;   // screen direction of the motion (length doesn't matter)
                var len = Math.Sqrt(sx * sx + sy * sy);
                if (len < 1e-4 || Math.Sqrt(b.VX * b.VX + b.VY * b.VY + b.VZ * b.VZ) < 5) continue;   // parked
                var ux = sx / len; var uy = sy / len;
                for (var t = 1; t <= 2; t++)
                {
                    var r = (int)Math.Round(placed[i][2] - uy * t); var c = (int)Math.Round(placed[i][1] - ux * t);
                    if (Free(r, c)) Put(r, c, '˙', b.Color);   // the ship's colour: a grey trail read as ring dots wandering
                }
            }

            // stalks: plane foot ┴ (blip above) / ┬ (below), │ between
            for (var i = 0; i < blips.Count; i++)
            {
                int pr = placed[i][0], c = placed[i][1], br = placed[i][2];
                if (br == pr) continue;
                var color = blips[i].Color;
                Put(pr, c, br < pr ? '┴' : '┬', color);
                for (var r = Math.Min(pr, br) + 1; r < Math.Max(pr, br); r++) Put(r, c, '│', color);
            }

            for (var i = 0; i < blips.Count; i++) Put(placed[i][2], placed[i][1], blips[i].Glyph, blips[i].Color);
            Put((int)Math.Round(cy), (int)Math.Round(cx), '▲', SelfColor);   // last: you're never hidden under a close station

            // runs of one colour → one short tag (<#RRGGBB>); runs in plainColor (the LCD's own text colour) need none.
            // The LCD keeps only ~2000 characters: every tag counts. Trailing spaces dropped.
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
                    if (text.Trim().Length == 0 || color == plainColor) sb.Append(text);
                    else
                    {
                        var body = text.TrimEnd(); var pad = text.Substring(body.Length);
                        sb.Append("<").Append(color).Append('>').Append(body).Append("</color>").Append(pad);
                    }
                }
                lines.Add(sb.ToString().TrimEnd());
            }
            return lines;
        }

        /// <summary>"1 km" / "500 m" for ring labels.</summary>
        public static string Km(float m) => m >= 1000 ? (m / 1000f).ToString("0.#", CultureInfo.InvariantCulture) + " km" : m.ToString("0", CultureInfo.InvariantCulture) + " m";
    }
}
