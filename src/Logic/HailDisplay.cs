using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using Eleon.Modding;
using LivingSystems.Core;
using LivingSystems.Core.Sim;
using UnityEngine;

namespace LivingSystems
{
    /// <summary>
    /// Writes COMMS screens to LCDs by name (PfServer): <see cref="LcdName"/> = everything on one pane; the split pages
    /// <see cref="RadarLcd"/> (scanner), <see cref="TrafficLcd"/> (transponders), <see cref="ContactsLcd"/>,
    /// <see cref="FeedLcd"/> — one pane each, letters shared between radar and traffic. Rich-text tags; tables in
    /// &lt;mspace&gt; (fixed width, verified in game).
    /// </summary>
    sealed class HailDisplay
    {
        public const string TestLcd = "LS_TEST_LCD", TestLcd2 = "LS_TEST_LCD_2";
        public const string LcdName = "LS_HAIL_LCD", RadarLcd = "LS_RADAR_LCD", TrafficLcd = "LS_TRAFFIC_LCD", ContactsLcd = "LS_CONTACTS_LCD", FeedLcd = "LS_FEED_LCD";
        static readonly string[] Pages = { LcdName, RadarLcd, TrafficLcd, ContactsLcd, FeedLcd, TestLcd, TestLcd2 };

        readonly Action<string> log;
        readonly FlightDriver fly;
        IPlayfield playfield;
        double nextScan, nextDraw;
        /// <summary>Structures carrying our LCDs → which page names they have (rescanned every 10 s).</summary>
        readonly Dictionary<int, List<string>> hosts = new Dictionary<int, List<string>>();
        readonly Dictionary<string, string> lastText = new Dictionary<string, string>();
        readonly Dictionary<string, double> lastWrite = new Dictionary<string, double>();
        readonly HashSet<string> warned = new HashSet<string>();
        readonly Dictionary<int, double> lastLog = new Dictionary<int, double>();

        public HailDisplay(Action<string> log, FlightDriver fly) { this.log = log; this.fly = fly; }

        /// <summary>Sim traders' transponders here (SimLive): name, position, leg, other system, station.</summary>
        public Func<int, IEnumerable<SimLive.Transponder>> Traffic;   // arg: whose band (a player id, or −ship id for its LCD)

        public void OnLoaded(IPlayfield p) { playfield = p; hosts.Clear(); lastText.Clear(); nextScan = 0; nextDraw = 0; }
        public void OnUnloading(IPlayfield p) { if (playfield == p) { playfield = null; hosts.Clear(); lastText.Clear(); } }

        public void OnUpdate(double now)
        {
            if (playfield?.Entities == null) return;
            if (now >= nextScan) { nextScan = now + 10; Scan(); }
            if (now < nextDraw || hosts.Count == 0) return;
            nextDraw = now + 1;
            foreach (var kv in hosts.ToList())
            {
                var id = kv.Key;
                if (!playfield.Entities.TryGetValue(id, out var host) || host?.Structure == null) { hosts.Remove(id); continue; }
                View view = null;
                var wrote = 0;
                foreach (var page in kv.Value)
                {
                    List<VectorInt3> at;
                    try { at = host.Structure.GetDevicePositions(page); } catch { continue; }
                    if (at == null || at.Count == 0) continue;
                    var key = id + "|" + page;
                    // the full screen and the lists change slowly: every 2 s is plenty; the radar every second
                    // a write blinks the LCD: lists every 2 s at most, the radar every 3 s
                    if (lastWrite.TryGetValue(key, out var lw0) && now - lw0 < (page == RadarLcd ? 3 : 2)) continue;
                    if (view == null) view = Look(host, now);
                    if (page == RadarLcd) { lastWrite[key] = now; wrote += WriteRadar(host, view, at, id, now); continue; }
                    if (page == TestLcd2) { lastWrite[key] = now; var t2 = now; wrote += WriteSized(host, at, id, now, TestLcd2, Fg, font => StatusStub(host, font, t2)); continue; }
                    if (page == TestLcd) { lastWrite[key] = now; var gal = ((int)(now / 5)) % 2 == 1; wrote += WriteSized(host, at, id, now, TestLcd, RingFg, font => MapPage(host, font, gal)); continue; }
                    string text;
                    try { text = page == TrafficLcd ? TrafficPage(host, view) : page == ContactsLcd ? ContactsPage(view) : page == FeedLcd ? FeedPage() : Render(host); }
                    catch (Exception e) { if (warned.Add("render:" + page)) log($"LCD: drawing {page} threw: {e.Message}"); continue; }
                    text = Fit(text, page);
                    // only write on change — but at least every 30 s: the game can blank an LCD behind our back (core lost and
                    // re-inserted, power cycled) and nothing tells us
                    if (lastText.TryGetValue(key, out var prev) && prev == text && lastWrite.TryGetValue(key, out var lw) && now - lw < 30) continue;
                    lastWrite[key] = now;
                    foreach (var pos in at)
                    {
                        try
                        {
                            var lcd = host.Structure.GetDevice<ILcd>(pos);
                            if (lcd == null) continue;
                            lcd.SetText(text);
                            Paint(lcd);
                        }
                        catch (Exception e)
                        {
                            if (warned.Add($"{id}:{pos.x},{pos.y},{pos.z}")) log($"LCD: writing {page} on {id} failed: {e.Message}");
                        }
                    }
                    lastText[key] = text;
                    wrote += text.Length;
                }
                if (wrote > 0 && (!lastLog.TryGetValue(id, out var ll) || now - ll >= 300)) { lastLog[id] = now; log($"LCD: wrote {string.Join(", ", kv.Value)} on {id} ({wrote} chars; logged every 5 min)"); }
            }
        }

        static readonly Color Bg = new Color(0.02f, 0.05f, 0.08f), Fg = new Color(0.75f, 0.85f, 0.9f);

        /// <summary>Our colours — set only when they differ: setting them redraws the LCD from black (a visible blink).</summary>
        static void Paint(ILcd lcd) => Paint(lcd, Fg);

        static void Paint(ILcd lcd, Color fg)
        {
            try
            {
                bool Near(Color a, Color b) => Mathf.Abs(a.r - b.r) < 0.01f && Mathf.Abs(a.g - b.g) < 0.01f && Mathf.Abs(a.b - b.b) < 0.01f;
                if (!Near(lcd.GetBackgroundColor(), Bg)) lcd.SetBackgroundColor(Bg);
                if (!Near(lcd.GetTextColor(), fg)) lcd.SetTextColor(fg);
            }
            catch { }
        }

        /// <summary>The radar pane's own text colour = the ring grey (#37474F): ring dots then need no colour tags.</summary>
        static readonly Color RingFg = new Color(0x37 / 255f, 0x47 / 255f, 0x4F / 255f);

        /// <summary>An LCD keeps ~2000 characters (measured: the text came back cut at 2001) and drops the rest
        /// mid-line. Drop whole lines from the end instead (logged once per page).</summary>
        const int LcdMax = 1990;

        string Fit(string text, string page)
        {
            if (text.Length <= LcdMax) return text;
            var cut = text.LastIndexOf('\n', LcdMax - 1);
            if (warned.Add("long:" + page)) log($"LCD: {page} text {text.Length} chars > {LcdMax}: dropped lines at the end (logged once)");
            return cut > 0 ? text.Substring(0, cut) : text.Substring(0, LcdMax);
        }

        void Scan()
        {
            var found = new Dictionary<int, List<string>>();
            foreach (var e in playfield.Entities.Values.ToList())
            {
                if (e?.Structure == null) continue;
                foreach (var page in Pages)
                    try { var at = e.Structure.GetDevicePositions(page); if (at != null && at.Count > 0) { if (!found.TryGetValue(e.Id, out var l)) found[e.Id] = l = new List<string>(); l.Add(page); } } catch { }
            }
            foreach (var kv in found)
                if (!hosts.TryGetValue(kv.Key, out var had) || !had.SequenceEqual(kv.Value)) log($"LCD: {string.Join(", ", kv.Value)} found on {kv.Key}");
            hosts.Clear();
            foreach (var kv in found) hosts[kv.Key] = kv.Value;
        }

        // ---- split pages ------------------------------------------------------------------------------------------

        /// <summary>One thing on the scope: a contact (entity near us) and/or a transponder (sim trader, any range).</summary>
        sealed class Seen
        {
            public string Name = "", Tag = "", Color = "#A1887F";
            /// <summary>Identity on the scope: "e&lt;entity&gt;" or "t&lt;transponder name&gt;" (two stations can share a name).</summary>
            public string Key = "";
            public char Letter;   // ' ' = no letter (stations)
            public bool Station, Wreck;
            /// <summary>What it is, in listing order: 0 hailable «LS» · 1 pirate/hostile · 2 wreck · 3 player · 4 vanilla
            /// NPC traffic (dim, lower-case letter) · 5 station.</summary>
            public int Kind = 4;
            public int Group = -1;
            public Vector3 Pos, Local, Vel;
            public float Dist;
            public SimLive.Transponder T;
        }
        sealed class View { public List<Seen> All = new List<Seen>(); public int Contacts; }

        /// <summary>Per host: name → letter, kept while the ship stays on the scope (radar and traffic agree).</summary>
        readonly Dictionary<int, Dictionary<string, char>> letters = new Dictionary<int, Dictionary<string, char>>();
        /// <summary>Per host + name: last position and time, for the motion tails.</summary>
        readonly Dictionary<string, KeyValuePair<Vector3, double>> lastPos = new Dictionary<string, KeyValuePair<Vector3, double>>();
        readonly Dictionary<string, Vector3> lastVel = new Dictionary<string, Vector3>();

        View Look(IEntity host, double now)
        {
            var v = new View();
            var inv = Quaternion.Inverse(host.Rotation);
            var byName = new Dictionary<string, Seen>();
            foreach (var e in playfield.Entities.Values.ToList())
            {
                if (e?.Structure == null || e.Id == host.Id || DockedTo(e, host.Id) || KioskNames.IsKiosk(e.Name)) continue;
                var station = IsStation(e);
                if (!(e.Type == EntityType.CV || e.Type == EntityType.SV || e.Type == EntityType.HV || fly.IsOurs(e.Id) || station)) continue;
                var d = (e.Position - host.Position).magnitude;
                if (d > RadarRange) continue;
                var x = new Seen { Key = "e" + e.Id, Name = StructureTag.Strip(e.Name), Pos = e.Position, Station = station || e.Type == EntityType.BA, Dist = d };
                int g; try { g = (int)e.Faction.Group; } catch { g = -1; }
                x.Group = g;
                x.Tag = fly.IsOurs(e.Id) ? Short(fly.StatusOf(e.Id, -1)) : Factions.Name(g);
                x.Kind = x.Station ? 5 : Factions.Hostile.Contains(g) ? 1 : x.Name.StartsWith("Wreck", StringComparison.OrdinalIgnoreCase) ? 2
                       : fly.IsOurs(e.Id) ? 0 : Factions.IsPlayer(g) ? 3 : 4;
                v.All.Add(x); byName[x.Name + "#" + e.Id] = x;
            }
            v.Contacts = v.All.Count;
            List<SimLive.Transponder> band;
            try { band = Traffic?.Invoke(-host.Id)?.ToList() ?? new List<SimLive.Transponder>(); } catch { band = new List<SimLive.Transponder>(); }
            foreach (var t in band)
            {
                if (t.IsGhost)
                {   // a ghost ping is no ship you can see or hail: its own faint row, never matched to a real contact
                    v.All.Add(new Seen { Key = "g" + t.Name, Name = t.Name, Pos = t.Pos, Dist = (t.Pos - host.Position).magnitude, T = t, Kind = 2 });
                    continue;
                }
                // the transponder name may carry a call sign ("Silk Globe #4F2A"): match on the ship's name in front
                var match = v.All.FirstOrDefault(x => x.T == null && !x.Station && x.Key.StartsWith("e") && (t.Name == x.Name || t.Name.StartsWith(x.Name + " ") || x.Name.StartsWith(t.Name + " ")));
                if (match == null)
                {
                    if (t.Leg == "arriving") continue;   // not here yet
                    match = new Seen { Key = "t" + t.Name, Name = t.Name, Pos = t.Pos, Dist = (t.Pos - host.Position).magnitude };
                    v.All.Add(match);
                }
                match.T = t; match.Wreck = t.IsWreck;
                if (t.IsWreck) match.Kind = 2;
                else if (match.Kind != 1) match.Kind = 0;   // on the band = one of our traders: hailable
            }
            // docked transponders sit inside their station: listed in traffic, not drawn
            foreach (var x in v.All)
            {
                x.Local = inv * (x.Pos - host.Position);
                var key = host.Id + "|" + x.Key;
                if (lastPos.TryGetValue(key, out var lp) && now - lp.Value > 0.5)
                {
                    var vel = (x.Pos - lp.Key) / (float)(now - lp.Value);
                    lastVel[key] = vel.magnitude > 2000 ? Vector3.zero : vel;   // a respawn / teleport is not motion
                }
                if (!lastPos.TryGetValue(key, out lp) || now - lp.Value > 0.5) lastPos[key] = new KeyValuePair<Vector3, double>(x.Pos, now);
                x.Vel = lastVel.TryGetValue(key, out var lv) ? inv * lv : Vector3.zero;
            }
            foreach (var x in v.All) x.Color = KindColor[x.Kind];
            // letters: kept per name while it stays; new ones get the first free letter, nearest first — capitals for what
            // matters (hailable, pirates, wrecks, players), lower case for vanilla NPC traffic (background)
            if (!letters.TryGetValue(host.Id, out var map)) letters[host.Id] = map = new Dictionary<string, char>();
            var named = v.All.Where(x => !x.Station).ToList();
            foreach (var gone in map.Keys.Where(k => named.All(x => x.Key != k)).ToList()) map.Remove(gone);
            foreach (var x in named.OrderBy(x => x.Dist))
            {
                if (!map.TryGetValue(x.Key, out var c) || (x.Kind == 4) != char.IsLower(c))
                {
                    if (map.ContainsKey(x.Key)) map.Remove(x.Key);   // changed kind (e.g. turned hostile): re-letter
                    var from = x.Kind == 4 ? 'a' : 'A';
                    c = Enumerable.Range(from, 26).Select(i => (char)i).FirstOrDefault(l => !map.ContainsValue(l));
                    if (c == default(char)) c = '?';
                    else map[x.Key] = c;
                }
                x.Letter = c;
            }
            foreach (var x in v.All.Where(x => x.Station)) x.Letter = ' ';
            v.All = v.All.OrderBy(x => x.Dist).ToList();
            return v;
        }

        /// <summary>One colour per kind, everywhere (radar, legend, contacts): green hailable, red hostile, amber wreck,
        /// cyan player, dim grey background traffic, white station.</summary>
        static readonly string[] KindColor = { "#66BB6A", "#EF5350", "#FFB300", "#4DD0E1", "#546E7A", "#CFD8DC" };
        static readonly string[] KindLabel = { "hail", "hostile", "wreck", "player", "traffic", "station" };

        /// <summary>
        /// The radar sizes itself to each LCD's font size (the player picks it): measured in game on a 2-wide LCD,
        /// font 7 ≈ 60 columns × 26 lines of &lt;mspace=0.7em&gt; text (the pane ends where the next LCD starts — measured twice) — scales with 1/font.
        /// </summary>
        int WriteRadar(IEntity host, View v, List<VectorInt3> at, int id, double now) =>
            WriteSized(host, at, id, now, RadarLcd, RingFg, font => RadarPage(host, v, font));

        /// <summary>Per LCD: render for its font size (cached per size), write on change, in the pane colour <paramref name="fg"/>.</summary>
        int WriteSized(IEntity host, List<VectorInt3> at, int id, double now, string page, Color fg, Func<int, string> render)
        {
            var byFont = new Dictionary<int, string>();
            var n = 0;
            foreach (var pos in at)
            {
                try
                {
                    var lcd = host.Structure.GetDevice<ILcd>(pos);
                    if (lcd == null) continue;
                    var font = 8; try { font = Math.Max(3, lcd.GetFontSize()); } catch { }
                    if (!byFont.TryGetValue(font, out var text)) byFont[font] = text = Fit(render(font), page);
                    var key = $"{id}|{page}|{pos.x},{pos.y},{pos.z}";
                    if (lastText.TryGetValue(key, out var prev) && prev == text && lastWrite.TryGetValue(key, out var lw) && now - lw < 30) continue;
                    lcd.SetText(text);
                    Paint(lcd, fg);
                    lastText[key] = text; lastWrite[key] = now; n += text.Length;
                }
                catch (Exception e) { if (warned.Add($"{id}:{pos.x},{pos.y},{pos.z}")) log($"LCD: writing {page} on {id} failed: {e.Message}"); }
            }
            return n;
        }

        // ---- LS_TEST_LCD_2: ship status, STUBBED values (LCD driver mod mock-up: %O2% %FUEL% %SHIELD% … as bars) ------

        /// <summary>Fake but drifting values (so refreshes show), real ship name. Layout only — the driver mod fills it later.</summary>
        string StatusStub(IEntity host, int font, double now)
        {
            var cols = (int)(400f / font);
            var barW = Math.Max(8, cols - 22);
            float Wave(double period, float lo, float hi, double phase = 0) => lo + (hi - lo) * (0.5f + 0.5f * (float)Math.Sin(now / period * 2 * Math.PI + phase));
            string Bar(float frac)
            {
                frac = Mathf.Clamp01(frac);
                var full = (int)Math.Round(frac * barW);
                var c = frac >= 0.5f ? "#66BB6A" : frac >= 0.2f ? "#FFB300" : "#EF5350";
                return $"<{c}>{new string('█', full)}</color><#263238>{new string('░', barW - full)}</color>";
            }
            string Row(string label, float frac, string value) => $"{Pad(label, 8)}{Bar(frac)} {value}\n";
            var sb = new StringBuilder("<mspace=0.7em><line-height=85%><nobr>");
            sb.Append($"<#4FC3F7><b>{Clip(StructureTag.Strip(host.Name ?? "SHIP"), 18)}</b></color> <#607D8B>status · STUB values</color>\n");
            float o2 = Wave(97, 0.55f, 0.92f), fuel = Wave(211, 0.18f, 0.74f, 1), pent = Wave(173, 0.08f, 0.35f, 2), shield = Wave(41, 0.3f, 1f, 3), hull = Wave(300, 0.82f, 0.97f, 4);
            float pwr = Wave(23, 0.25f, 0.85f, 5), cargo = Wave(400, 0.4f, 0.6f, 6), ammo = Wave(130, 0.15f, 0.9f, 7);
            sb.Append(Row("O2", o2, $"{o2 * 4800:0}/4800"));
            sb.Append(Row("FUEL", fuel, $"{fuel * 9600:0}/9600"));
            sb.Append(Row("PENTAXID", pent, $"{pent * 600:0}/600"));
            sb.Append(Row("SHIELD", shield, $"{shield * 38500:0}"));
            sb.Append(Row("HULL", hull, $"{hull * 100:0}%"));
            sb.Append(Row("POWER", 1 - pwr, $"{pwr * 4870:0}/4870 kW"));
            sb.Append(Row("CARGO", cargo, $"{cargo * 64:0.0}/64k"));
            sb.Append(Row("AMMO", ammo, $"{ammo * 100:0}%"));
            sb.Append("<#37474F>").Append(new string('─', Math.Min(cols - 2, 8 + barW + 10))).Append("</color>\n");
            var speed = Wave(29, 0, 140, 8);
            sb.Append($"SPEED   {speed:0} m/s   STOP {speed * speed / (2 * 6.5f):0} m in {speed / 6.5f:0} s\n");
            sb.Append($"MASS    {Wave(500, 1180, 1240, 9):0} t   BLOCKS 1224/1231\n");
            sb.Append("DOCKED  <#66BB6A>2</color> · SV Spike 1, HV Mule\n");
            sb.Append("CREW    pilot Captain · 1 passenger\n");
            sb.Append($"DAMAGED <#FFB300>3</color>  Thruster L ▓▓▓░ · Cargo Box 2 ▓▓░░ · Light ▓░░░\n");
            sb.Append("PROD    <#66BB6A>2 working</color> · 1 idle · 0 off\n");
            return sb.Append("</nobr></line-height></mspace>").ToString();
        }

        // ---- LS_TEST_LCD: system map / star map, 5 s each (trying it out) ---------------------------------------------

        /// <summary>The save's sector files, parsed once on this PfServer (set by LogicEntry).</summary>
        public Func<Core.Sim.Galaxy> GalaxyFor;

        string MapPage(IEntity host, int font, bool galaxy)
        {
            var cols = (int)(400f / font); var rows = (int)(185f / font);
            var w = Math.Max(21, cols - 1) | 1; var h = Math.Max(9, rows - 3) | 1;
            var g = GalaxyFor?.Invoke();
            var pf = playfield?.Name ?? "";
            var star = g?.StarOf(pf);
            var sb = new StringBuilder("<mspace=0.7em><line-height=80%><nobr>");
            if (g == null || star == null) return sb.Append("<#4FC3F7><b>MAP</b></color> no map data for this playfield</nobr></line-height></mspace>").ToString();
            var fwd = host.Rotation * Vector3.forward;
            var yaw = MapAscii.Yaw(fwd.x, fwd.z);
            var places = new List<MapAscii.Place>();
            float range = 1;
            string head;
            if (!galaxy)
            {
                g.SectorPos.TryGetValue(pf, out var me);
                foreach (var other in g.PlayfieldsOf(star))
                {
                    if (other == pf || !g.SectorPos.TryGetValue(other, out var at)) continue;
                    var mt = g.MapTypes.TryGetValue(other, out var m) ? m : ""; var ty = g.Types.TryGetValue(other, out var t) ? t : "";
                    if (mt == "WarpTarget") continue;   // six warp points round the sun: clutter
                    var d = at - me;
                    var sun = other == star.Name || ty.StartsWith("Sun", StringComparison.OrdinalIgnoreCase);
                    var station = mt == "Station" || ty.IndexOf("Station", StringComparison.OrdinalIgnoreCase) >= 0;
                    var rock = mt.StartsWith("Asteroid", StringComparison.OrdinalIgnoreCase) || ty.IndexOf("Asteroid", StringComparison.OrdinalIgnoreCase) >= 0;
                    var moon = ty.IndexOf("Moon", StringComparison.OrdinalIgnoreCase) >= 0;
                    places.Add(new MapAscii.Place
                    {
                        X = d.X, Z = d.Z,
                        Glyph = sun ? '☼' : station ? '■' : rock ? '✧' : moon ? '○' : '●',
                        Color = sun ? "#FFD54F" : station ? "#CFD8DC" : rock ? "#90A4AE" : "#81C784",
                        Label = $"{Clip(World.Short(other), 10)} {d.Length:0}u",
                    });
                    range = Math.Max(range, d.Length);
                }
                head = $"<#4FC3F7><b>SYSTEM</b></color> {Clip(star.Name, 14)} · you: {Clip(World.Short(pf), 12)} · edge {range * 1.15f:0}u";
            }
            else
            {
                var near = g.Stars.Values.Where(x => x != star).Select(x => new { x, d = (x.Pos - star.Pos).Length }).Where(x => x.d <= 90).OrderBy(x => x.d).Take(12).ToList();
                foreach (var x in near)
                {
                    var d = x.x.Pos - star.Pos;
                    var trade = g.PlayfieldsOf(x.x).Any(p => (g.Types.TryGetValue(p, out var t) ? t : "").IndexOf("Trade", StringComparison.OrdinalIgnoreCase) >= 0);
                    places.Add(new MapAscii.Place
                    {
                        X = d.X, Z = d.Z,
                        Glyph = x.x.Gate ? '◈' : trade ? '★' : '✦',
                        Color = x.x.Gate ? "#4DD0E1" : trade ? "#66BB6A" : "#B0BEC5",
                        Label = $"{Clip(x.x.Name, 9)} {x.d:0}",
                    });
                    range = Math.Max(range, (float)x.d);
                }
                head = $"<#4FC3F7><b>STARS</b></color> around {Clip(star.Name, 14)} · LY · ★ trade ◈ gate";
            }
            var grid = MapAscii.Render(places, yaw, range * 1.15f, w, h, MapAscii.RayColor, sqrt: galaxy);
            sb.Append(head).Append('\n');
            foreach (var row in grid) sb.Append(row).Append('\n');
            sb.Append("nose up · ┊ = straight ahead");
            return sb.Append("</nobr></line-height></mspace>").ToString();
        }

        const int LegendW = 16;   // "A Name     1.5k"

        string RadarPage(IEntity host, View v, int font)
        {
            var cols = (int)(400f / font); var rows = (int)(185f / font);   // 0.7em cells (digits at 0.6em ran into each other)
            var radarW = Math.Max(21, cols - LegendW - 3) | 1;   // 2 cells gap + 1 spare   // odd: the ship sits on the middle column
            var radarH = Math.Max(9, rows - 3) | 1;
            // docked + transponder only = somewhere inside its station: not drawn; a docked ship we can SEE is drawn
            var drawn = v.All.Where(x => !(x.Key.StartsWith("t") && x.T != null && x.T.Leg == "dock")).ToList();
            var blips = drawn.OrderByDescending(x => x.Dist)   // nearest drawn last: on top
                .Select(x => new RadarAscii.Blip
                {
                    X = x.Local.x, Y = x.Local.y, Z = x.Local.z, VX = x.Vel.x, VY = x.Vel.y, VZ = x.Vel.z,
                    Glyph = x.Station ? '■' : x.Letter, Color = x.Color,   // wrecks too: amber letter, same as the legend
                }).ToList();
            // zoom on what's really around us (contacts ≤ 5 km); far transponders sit on the outer ring in their direction —
            // one trader 8 km out used to squeeze everything close into the middle
            var near = drawn.Where(x => x.Key.StartsWith("e")).Select(x => new RadarAscii.Blip { X = x.Local.x, Z = x.Local.z }).ToList();
            var range = RadarAscii.PickRange(near.Count > 0 ? near : blips);
            var grid = RadarAscii.Render(blips, range, radarW, radarH, RadarAscii.RingColor);
            var legend = new List<string>();
            // grouped by kind (what matters first), by letter inside a group: rows stay put, distances change
            foreach (var x in drawn.OrderBy(x => x.Kind).ThenBy(x => x.Station ? x.Dist : x.Letter).Take(radarH))
                legend.Add($"<{x.Color}>{(x.Station ? '■' : x.Letter)} {Pad(Clip(x.Name, 8), 8)}</color> {Km(x.Dist)}");   // distance in the pane's grey: no tag
            var sb = new StringBuilder("<mspace=0.7em><line-height=80%><nobr>");   // one <nobr>: a row too wide is cut, never wrapped
            sb.Append($"<#4FC3F7><b>RADAR</b></color> {Km(RadarAscii.RingDistance(1, range))}/{Km(RadarAscii.RingDistance(2, range))}/{Km(range)} · {drawn.Count}\n");   // (no Braille in the LCD font: the 2018 symbol list has none)
            var pad = new string(' ', radarW);
            for (var r = 0; r < grid.Count; r++)
            {
                var row = grid[r];
                if (r < legend.Count) row += new string(' ', Math.Max(0, radarW - VisibleLength(row))) + "  " + legend[r];
                sb.Append(row).Append('\n');
            }
            // what's here at a glance, in the kinds' colours
            sb.Append(string.Join(" · ", drawn.GroupBy(x => x.Kind).OrderBy(gr => gr.Key).Select(gr => $"<{KindColor[gr.Key]}>{gr.Count()} {KindLabel[gr.Key]}</color>")));
            return sb.Append("</nobr></line-height></mspace>").ToString();
        }

        /// <summary>Characters shown (rich-text tags don't count).</summary>
        static int VisibleLength(string s) => System.Text.RegularExpressions.Regex.Replace(s, "<[^>]*>", "").Length;

        string TrafficPage(IEntity host, View v)
        {
            var sb = new StringBuilder("<mspace=0.65em><line-height=85%>");
            var band = v.All.Where(x => x.T != null).OrderBy(x => x.Wreck ? 0 : x.T.Leg == "dock" ? 2 : 1).ThenBy(x => x.Dist).ToList();
            sb.Append($"<b><color=#4FC3F7>TRAFFIC</color></b> <color=#4F87A6>transponders · {band.Count} on the band</color>\n");
            if (band.Count == 0) sb.Append("<color=#4F87A6>  no transponders on the band</color>\n");
            foreach (var x in band.Take(14))
            {
                var t = x.T;
                string what;
                switch (t.Leg)
                {
                    case "out": what = (t.Passing ? "<color=#90A4AE>pass</color> » " : "<color=#90A4AE>out</color> » ") + Clip(World.Short(t.Other), 9); break;
                    case "in": what = "<color=#66BB6A>in</color> → " + Clip(t.Station, 10); break;
                    case "fight": what = "<color=#EF5350>MAYDAY</color>"; break;
                    case "arriving": what = $"<color=#4FC3F7>ARRIVING</color> {Math.Max(0, t.EtaSec) / 60:0.0}m"; break;
                    case "wreck": what = $"<color=#FFB300>WRECK</color> {t.AgeMin:0}m"; break;
                    case "home": what = "<color=#EF5350>HUB</color> " + Clip(t.Other, 9); break;
                    case "ghost": what = $"<color=#90A4AE>faint {(t.HeardAgo < 60 ? "ping" : $"{t.HeardAgo / 60:0}m ago")}</color>"; break;
                    default: what = "<color=#4F87A6>docked" + (t.EtaSec > 0 ? $" {Math.Max(1, Math.Ceiling(t.EtaSec / 60)):0}m" : "") + "</color>"; break;
                }
                var where = t.Leg == "dock" || t.Leg == "arriving" ? Pad("", 16) : $"<color=#4F87A6>{Pad(Km(x.Dist), 5)}</color> <color=#FFD54F>{Pad(Dir(x.Local), 10)}</color>";
                var hull = t.IsWreck || t.Hull >= 0.95f ? "" : $" <color={(t.Hull >= 0.35f ? "#FFB300" : "#EF5350")}>{t.Hull * 100:0}%</color>";
                var cargo = t.IsGhost ? "" : t.Amt > 0 ? $"{Abbrev(t.Cargo)}{t.Amt}" : "empty";
                sb.Append($"<color={x.Color}>{x.Letter}</color> {Pad(Clip(x.Name, 11), 11)} {where} {what}{hull} <color=#607D8B>{Clip(t.Role, 6)} {cargo}</color>\n");
            }
            if (band.Count > 14) sb.Append($"<color=#4F87A6>  +{band.Count - 14} more</color>");
            return sb.Append("</line-height></mspace>").ToString();
        }

        string ContactsPage(View v)
        {
            var list = v.All.Where(x => x.Tag.Length > 0 || x.Station).Take(v.Contacts).ToList();
            var sb = new StringBuilder("<mspace=0.65em><line-height=85%>");
            sb.Append($"<b><color=#4FC3F7>CONTACTS</color></b> <color=#4F87A6>{list.Count} within {Km(RadarRange)}</color>\n");
            if (list.Count == 0) sb.Append("<color=#4F87A6>  nothing on scope</color>\n");
            foreach (var x in list.Take(14))
                sb.Append($"<color={x.Color}>{(x.Station ? '■' : x.Letter)}</color> {Pad(Clip(x.Name, 16), 16)} <color=#4F87A6>{Pad(Km(x.Dist), 5)}</color> <color={x.Color}>{Clip(x.Tag, 10)}</color>\n");
            if (list.Count > 14) sb.Append($"<color=#4F87A6>  +{list.Count - 14} further out</color>");
            return sb.Append("</line-height></mspace>").ToString();
        }

        string FeedPage()
        {
            var sb = new StringBuilder("<line-height=85%><b><color=#4FC3F7>FEED</color></b>\n");
            var feed = fly.Feed.Skip(Math.Max(0, fly.Feed.Count - 14)).Reverse().ToList();
            if (feed.Count == 0) sb.Append("<color=#4F87A6>  quiet</color>");
            foreach (var f in feed)
                sb.Append($"<color=#4F87A6>{f.Key:HH:mm}</color> ")
                  .Append(f.Value.StartsWith(World.Formatted) ? f.Value.Substring(World.Formatted.Length) : Clip(f.Value, 34)).Append('\n');
            return sb.Append("</line-height>").ToString();
        }

        /// <summary>"←136° ↓8°" — turn that way to put it on the nose (ahead / behind when close to the axis).</summary>
        static string Dir(Vector3 local)
        {
            var yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            var h = Mathf.Abs(yaw) < 6 ? "ahead" : Mathf.Abs(yaw) > 170 ? "behind" : $"{(yaw > 0 ? "→" : "←")}{Mathf.Abs(yaw):0}°";
            var e = Elev(local);
            return e.Length > 0 ? h + " " + e : h;
        }

        /// <summary>"↑20°" / "↓8°" / "" (level).</summary>
        static string Elev(Vector3 local)
        {
            var pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            return Mathf.Abs(pitch) < 4 ? "" : $"{(pitch > 0 ? "↑" : "↓")}{Mathf.Abs(pitch):0}°";
        }

        /// <summary>"1.0k" / "640m": 5 cells.</summary>
        static string Km(float m) => m >= 1000 ? (m / 1000f).ToString(m >= 10000 ? "0" : "0.0", CultureInfo.InvariantCulture) + "k" : m.ToString("0", CultureInfo.InvariantCulture) + "m";

        static string Pad(string s, int n) => s.Length >= n ? s : s + new string(' ', n - s.Length);

        /// <summary>"IronOre" → "IrO", "Cobalt Ore" → "CoO": short cargo tag for one table row.</summary>
        static string Abbrev(string item)
        {
            var h = World.Humanize(item ?? "");
            var words = h.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
            if (words.Length == 0) return "";
            return words.Length == 1 ? Clip(words[0], 5) + " " : string.Concat(words.Take(2).Select((w, i) => i == 0 ? (w.Length > 2 ? w.Substring(0, 2) : w) : w.Substring(0, 1))) + " ";
        }

        /// <summary>Right column starts here (TextMeshPro &lt;pos&gt;; the LCD is wide and the fleet list is narrow).</summary>
        const string Col2 = "<pos=50%>";

        string Render(IEntity host)
        {
            // left column: fleet; right column: feed — built as rows, then joined line by line
            // left column: contacts — every ship near us, ours or not, nearest first (a local radar)
            var left = new List<string>();
            var contacts = playfield.Entities.Values
                .Where(e => e?.Structure != null && e.Id != host.Id && !DockedTo(e, host.Id) && !KioskNames.IsKiosk(e.Name)
                            && (e.Type == EntityType.CV || e.Type == EntityType.SV || e.Type == EntityType.HV || fly.IsOurs(e.Id) || IsStation(e)))
                .Select(e => new { e, d = (e.Position - host.Position).magnitude })
                .Where(x => x.d <= RadarRange)
                .OrderBy(x => x.d).ToList();
            left.Add($"<b><color=#4FC3F7>CONTACTS</color></b>  <color=#4F87A6>{contacts.Count} within {Dist(RadarRange)}</color>");
            if (contacts.Count == 0) left.Add("<color=#4F87A6>  nothing on scope</color>");
            foreach (var x in contacts.Take(MaxShips))   // one line each, nearest first: the screen holds ~5
            {
                string tag, c;
                if (fly.IsOurs(x.e.Id)) { var status = fly.StatusOf(x.e.Id, -1); tag = Short(status); c = StatusColor(status); }
                else
                {
                    var g = (int)x.e.Faction.Group;
                    tag = Factions.Name(g);
                    c = Factions.Hostile.Contains(g) ? "#EF5350" : Factions.IsPlayer(g) ? "#4DD0E1" : "#A1887F";
                }
                var mark = x.e.Type == EntityType.BA ? "■" : "●";   // stations vs ships
                left.Add($"<color={c}>{mark}</color> <b>{Clip(StructureTag.Strip(x.e.Name), 12)}</b> <color=#4F87A6>{Dist(x.d)}</color> <color={c}>{Clip(tag, 12)}</color>");
            }
            if (contacts.Count > MaxShips) left.Add($"<color=#4F87A6>  +{contacts.Count - MaxShips} more further out</color>");

            var right = new List<string> { "<b><color=#4FC3F7>FEED</color></b>" };
            var feed = fly.Feed.Skip(Math.Max(0, fly.Feed.Count - FeedLines)).Reverse().ToList();   // newest first
            if (feed.Count == 0) right.Add("<color=#4F87A6>  quiet</color>");
            foreach (var f in feed)
                right.Add($"<color=#4F87A6>{f.Key:HH:mm}</color> " +
                          (f.Value.StartsWith(Core.Sim.World.Formatted) ? f.Value.Substring(Core.Sim.World.Formatted.Length) : Clip(f.Value, FeedChars)));

            var sb = new StringBuilder();
            sb.Append("<b><size=+6><color=#4FC3F7>COMMS</color></size></b>   <color=#4F87A6>")
              .Append(DateTime.UtcNow.ToString("HH:mm", CultureInfo.InvariantCulture)).Append(" UTC</color>\n");   // all colour syntaxes work (tested)
            sb.Append(Rule);
            for (var i = 0; i < Math.Max(left.Count, right.Count); i++)
                sb.Append(i < left.Count ? left[i] : "").Append(Col2).Append(i < right.Count ? right[i] : "").Append('\n');
            sb.Append(Rule);
            AppendTraffic(sb, host);
            return sb.ToString();
        }

        /// <summary>
        /// Transponder chatter: traders announce where they are (any range — that's the point of a transponder). Your
        /// radar sees ships; this tells you where to go looking. Nearest first.
        /// </summary>
        void AppendTraffic(StringBuilder sb, IEntity host)
        {
            var from = host.Position;
            List<SimLive.Transponder> all;
            try { all = Traffic?.Invoke(-host.Id)?.ToList() ?? new List<SimLive.Transponder>(); }
            catch { all = new List<SimLive.Transponder>(); }
            sb.Append("<b><color=#4FC3F7>TRAFFIC</color></b>  <color=#4F87A6>transponders</color>\n");
            if (all.Count == 0) { sb.Append("<color=#4F87A6>  no transponders on the band</color>"); return; }
            var rows = all.Select(t => new { t, d = t.Pos - from }).OrderBy(x => x.t.IsWreck ? 0 : x.t.Leg == "dock" ? 2 : 1).ThenBy(x => x.d.magnitude).ToList();   // wrecks first: they don't wait
            foreach (var x in rows.Take(TrafficLines))
            {
                var t = x.t;
                var dir = Relative(host.Rotation, x.d);
                string what;
                switch (t.Leg)
                {
                    case "out": what = t.Passing ? $"<color=#90A4AE>passing</color> » {World.Short(t.Other)}" : $"<color=#90A4AE>outbound</color> » {World.Short(t.Other)}"; break;
                    case "in": what = $"<color=#66BB6A>inbound</color> to {Clip(t.Station, 14)}"; break;
                    case "fight": what = "<color=#EF5350>MAYDAY</color>"; break;
                    case "arriving": what = $"<color=#4FC3F7>ARRIVING</color> <color=#4F87A6>{Math.Max(0, t.EtaSec) / 60:0.0} min</color>"; break;
                    case "wreck": what = $"<color=#FFB300>WRECK</color> <color=#4F87A6>{t.AgeMin:0} min</color>"; break;
                    case "home": what = $"<color=#EF5350>HUB</color> {Clip(t.Other, 14)}"; break;   // test phase (SimLive.ShowHubs)
                    case "ghost": what = $"<color=#90A4AE>faint ping{(t.HeardAgo < 60 ? "" : $", {t.HeardAgo / 60:0} min ago")}</color>"; break;
                    default: what = $"<color=#4F87A6>docked at {Clip(t.Station, 14)}{(t.EtaSec > 0 ? $", out {Math.Max(1, Math.Ceiling(t.EtaSec / 60)):0} min" : "")}</color>"; break;
                }
                var where = t.Leg == "dock" ? "" : $"<color=#4F87A6>{Dist(x.d.magnitude)}</color> <color=#FFD54F>{dir}</color> ";
                var hc = t.Hull >= 0.7f ? "#66BB6A" : t.Hull >= 0.35f ? "#FFB300" : "#EF5350";
                var hull = t.IsWreck || t.IsGhost ? "" : $"<color={hc}>[{t.Hull * 100:0}%]</color> ";
                sb.Append($"<color=#A1887F>{(t.IsWreck ? "✕" : t.IsGhost ? "·" : "●")}</color> <b>{Clip(t.Name, 12)}</b> {hull}{where}{what} <color=#4F87A6>{Clip(t.IsGhost ? t.Role + ", lost" : t.What, 28)}</color>\n");
            }
            if (rows.Count > TrafficLines) sb.Append($"<color=#4F87A6>  +{rows.Count - TrafficLines} more</color>");
        }

        /// <summary>
        /// Where <paramref name="d"/> is from the ship's nose: "ahead", "behind", or "35° right, 10° up" — turn until it says
        /// ahead. (A compass bearing means nothing in space: no north, no up.)
        /// </summary>
        internal static string Relative(Quaternion facing, Vector3 d)
        {
            var local = Quaternion.Inverse(facing) * d;
            var yaw = Mathf.Atan2(local.x, local.z) * Mathf.Rad2Deg;
            var pitch = Mathf.Atan2(local.y, new Vector2(local.x, local.z).magnitude) * Mathf.Rad2Deg;
            if (Mathf.Abs(yaw) < 8 && Mathf.Abs(pitch) < 8) return "ahead";
            if (Mathf.Abs(yaw) > 165 && Mathf.Abs(pitch) < 15) return "behind";
            var h = Mathf.Abs(yaw) < 4 ? "" : $"{Mathf.Abs(yaw):0}° {(yaw > 0 ? "right" : "left")}";
            var v = Mathf.Abs(pitch) < 4 ? "" : $"{Mathf.Abs(pitch):0}° {(pitch > 0 ? "up" : "down")}";
            return h.Length > 0 && v.Length > 0 ? h + ", " + v : h + v;
        }

        const int FeedLines = 7;
        const int TrafficLines = 4;
        /// <summary>Radar range for the contacts list, metres (the hail scan uses 5 km too).</summary>
        const float RadarRange = 5000f;
        /// <summary>Feed text per line after the time: longer wraps into the left column (30 is too many).</summary>
        const int FeedChars = 22;
        /// <summary>Full-width rule: two halves (48 '─' measured ≈ 58 % of the width, so 40 ≈ 48 %), the second at the column.</summary>
        static readonly string Rule = "<color=#37474F>" + new string('─', 40) + Col2 + new string('─', 40) + "</color>\n";

        /// <summary>Ships listed on the screen (nearest first); more would scroll off the LCD.</summary>
        const int MaxShips = 5;

        /// <summary>Status squeezed for one LCD line ("following Name at 250 m" → "follow 250 m").</summary>
        static string Short(string s)
        {
            s = s ?? "";
            var tail = s.Contains("doesn't trust") ? " ⚠" : "";
            if (s.StartsWith("following")) { var i = s.LastIndexOf(" at "); return "follow" + (i > 0 ? s.Substring(i + 3) : "") + tail; }
            if (s.StartsWith("holding")) return "hold" + tail;
            if (s.StartsWith("waiting")) return "waiting" + tail;
            if (s.StartsWith("fleeing")) return "fleeing";
            if (s.StartsWith("awaiting")) return "idle" + tail;
            if (s.StartsWith("resuming")) return "resuming";
            return Clip(s, 12) + tail;
        }

        /// <summary>Docked to the LCD's ship (directly or via another docked ship) — part of us, not a contact.</summary>
        bool DockedTo(IEntity e, int hostId)
        {
            for (var i = 0; i < 4 && e != null; i++)   // short chain: SV on CV on CV
            {
                int d; try { d = e.DockedTo; } catch { return false; }
                if (d <= 0) return false;
                if (d == hostId) return true;
                if (!playfield.Entities.TryGetValue(d, out e)) return false;
            }
            return false;
        }

        /// <summary>
        /// A base worth showing on the scope: a station, not clutter. Same rule as the hail menu's automated check —
        /// skip satellites/beacons/relays/drones/wrecks/gates by name and anything under 300 blocks.
        /// </summary>
        internal static bool IsStation(IEntity e)
        {
            if (e.Type != EntityType.BA) return false;
            int blocks; try { blocks = e.Structure.BlockCount; } catch { return false; }
            try { if ((int)e.Faction.Group == WreckFaction) return false; } catch { }
            return StationRule(e.Name, blocks);
        }

        /// <summary>Eden2's junk / wreck POIs (faction Eden_Wreck): innocent names ("Freighter", "Light Freighter"), never stations.</summary>
        internal const int WreckFaction = 68;

        /// <summary>The station rule on name + size alone (also used on the Dedi's global structure list).</summary>
        internal static bool StationRule(string name, int blocks)
        {
            var n = (name ?? "").ToLowerInvariant();
            if (new[] { "satellite", "beacon", "relay", "drone", "wreck", "debris", "buoy", "sign", "gate", "jump", "asteroid", "dummy", "crashed", "derelict", "abandoned", "ruin", "junk", "salvage", "destroyed", "broken", "damaged", "remains", "hulk", "placeholder" }.Any(n.Contains)) return false;
            return blocks >= 300;
        }

        static string StatusColor(string s)
        {
            s = s ?? "";
            if (s.StartsWith("fleeing") || s.Contains("doesn't trust")) return "#EF5350";
            if (s.StartsWith("waiting")) return "#FFB300";
            if (s.StartsWith("following")) return "#66BB6A";
            if (s.StartsWith("holding")) return "#42A5F5";
            return "#6A7FB0";   // idle — LCD text glows: UNSATURATED colours wash out to white, so tint them
        }

        static string Dist(float m) => m >= 1000 ? (m / 1000f).ToString("0.0", CultureInfo.InvariantCulture) + " km" : m.ToString("0", CultureInfo.InvariantCulture) + " m";
        static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");
    }
}
