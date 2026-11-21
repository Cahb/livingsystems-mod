using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;
using LivingSystems.Core;

namespace LivingSystems
{
    /// <summary>
    /// Hail: contacts in range → a ship → talk / trade / jobs. Pages are <see cref="MenuPage"/>s drawn as clickable lines in
    /// the dialog body; the 3 buttons are always Back · Main · Close (Back = the page before, Main = the contact list).
    /// </summary>
    sealed partial class DediSpike
    {
        sealed class Contact
        {
            public int Id, Group, FacId, Dist, Blocks; public bool OwnTrader, Nose; public string Name, Type, Core, Status = "", Says = "";
            public static Contact Parse(string s)
            {
                var p = s.Split('~'); if (p.Length < 8) return null;
                int.TryParse(p[0], out var id); int.TryParse(p[2], out var g); int.TryParse(p[3], out var fid);
                int.TryParse(p[4], out var d); int.TryParse(p[7], out var b);
                return new Contact { Id = id, Name = p[1], Group = g, FacId = fid, Dist = d, OwnTrader = p[5] == "1", Type = p[6], Blocks = b, Core = p.Length > 8 ? p[8] : "?", Status = p.Length > 9 ? p[9] : "", Says = p.Length > 10 ? p[10] : "", Nose = p.Length > 11 && p[11] == "1" };
            }
        }
        readonly Dictionary<int, List<Contact>> hailContacts = new Dictionary<int, List<Contact>>();
        readonly Dictionary<int, string> hailPf = new Dictionary<int, string>();
        readonly Dictionary<int, Contact> hailFocus = new Dictionary<int, Contact>();

        static HashSet<int> HostileGroups => Factions.Hostile;
        static string FactionName(int g) => Factions.Name(g);
        static string StatusLine(Contact c) => string.IsNullOrEmpty(c.Status) ? "" : $"\nStatus: {c.Status}.";

        // ---- the menu driver ----------------------------------------------------------------------------------------

        /// <summary>One player's open menu: the page (rebuilt on each show, so its text is live), its page number, the way back.</summary>
        sealed class MenuState
        {
            public Func<MenuPage> Build; public MenuPage Page; public int PageNo, Version;
            public readonly Stack<Func<MenuPage>> Back = new Stack<Func<MenuPage>>();
        }
        readonly Dictionary<int, MenuState> menus = new Dictionary<int, MenuState>();

        MenuState MenuOf(int pid) { if (!menus.TryGetValue(pid, out var m)) menus[pid] = m = new MenuState(); return m; }

        /// <summary>Open a page; <paramref name="push"/> = the current one becomes Back.</summary>
        void ShowMenu(int pid, Func<MenuPage> build, bool push = true)
        {
            var m = MenuOf(pid);
            if (push && m.Build != null) m.Back.Push(m.Build);
            m.Build = build; m.PageNo = 0;
            RenderMenu(pid);
        }

        /// <summary>Show the current page again, rebuilt (live text), same page number.</summary>
        void RefreshMenu(int pid) { if (menus.ContainsKey(pid)) RenderMenu(pid); }

        void RenderMenu(int pid)
        {
            var m = MenuOf(pid);
            if (m.Build == null) return;
            m.Page = m.Build();
            if (m.Page == null) return;
            m.PageNo = Math.Max(0, Math.Min(m.PageNo, m.Page.Pages - 1));
            m.Version++;
            api.Application.ShowDialogBox(pid, new DialogConfig
            {
                TitleText = m.Page.Title,
                BodyText = MenuRender.Body(m.Page, m.PageNo, m.Version),
                ButtonTexts = new[] { "Back", "Main", "Close" },
                ButtonIdxForEsc = 2, CloseOnLinkClick = true,   // a dialog left open is NOT replaced by the next ShowDialogBox (clicks kept coming from v1): close, then open the new page
            }, OnMenu, 110);
        }

        void OnMenu(int button, string link, string input, int pid, int cv)
        {
            log($"menu {pid}: button={button} link='{link}' (showing v{(menus.TryGetValue(pid, out var m0) ? m0.Version : -1)} '{m0?.Page?.Title}')");
            if (!menus.TryGetValue(pid, out var m) || m.Page == null) return;
            if (!string.IsNullOrEmpty(link))
            {
                if (!MenuRender.Parse(link, out var ver, out var id)) return;
                if (ver != m.Version) { log($"menu {pid}: stale click (v{ver}, now v{m.Version}) — re-showing"); RenderMenu(pid); return; }   // a click from a dialog that was replaced since: show what's current
                if (m.Page.Find(id) == null && id != MenuRender.Next && id != MenuRender.Prev) log($"menu {pid}: no line '{id}' on '{m.Page.Title}'");
                if (id == MenuRender.Next) { m.PageNo++; RenderMenu(pid); return; }
                if (id == MenuRender.Prev) { m.PageNo--; RenderMenu(pid); return; }
                var line = m.Page.Find(id);
                try { line?.Do?.Invoke(); } catch (Exception ex) { log($"menu '{m.Page.Title}' line {id} threw: {ex}"); }
                return;
            }
            switch (button)
            {
                case 0:   // Back
                    if (m.Back.Count > 0) { m.Build = m.Back.Pop(); m.PageNo = 0; RenderMenu(pid); }
                    else ShowMain(pid);
                    return;
                case 1: ShowMain(pid); return;
                case 2: menus.Remove(pid); return;   // Close
                // -1 with no link: the old dialog reporting as it's replaced — ignore
            }
        }

        /// <summary>Main: the contact list (the hail list). The way back starts over.</summary>
        void ShowMain(int pid)
        {
            var m = MenuOf(pid); m.Back.Clear(); m.Build = null;
            ShowMenu(pid, () => ContactsPage(pid), false);
        }

        // ---- pages ----------------------------------------------------------------------------------------------------

        /// <summary>A fresh hail: the contact list (or straight to the ship your nose points at).</summary>
        void ShowContacts(int pid)
        {
            var cs = hailContacts.TryGetValue(pid, out var l) ? l : new List<Contact>();
            var m = MenuOf(pid); m.Back.Clear(); m.Build = null; hailFocus.Remove(pid);   // no contacts: the list still has Services
            ShowMenu(pid, () => ContactsPage(pid), false);
        }

        /// <summary>The ship page again, with an answer on top (order replies, trade answers…). Keeps the way back.</summary>
        void ShowShip(int pid, string reply = null)
        {
            var m = MenuOf(pid);
            if (m.Build == null && m.Back.Count == 0) m.Back.Push(() => ContactsPage(pid));   // straight to a ship (nose): Back = the list
            ShowMenu(pid, () => ShipPage(pid, reply), false);
        }

        MenuPage ContactsPage(int pid)
        {
            var cs = hailContacts.TryGetValue(pid, out var l) ? l : new List<Contact>();
            var p = new MenuPage { Title = "COMMS — contacts in range", Body = $"{cs.Count} contact{(cs.Count == 1 ? "" : "s")} within 5 km (as of your scan)." };
            if (hiredTug.ContainsKey(pid))
            {   // your tug, wherever it is (in scan range or not): its page from here
                var tn = hiredName.TryGetValue(pid, out var tnm) ? tnm : "Your tug";
                var st = hiredStat.TryGetValue(pid, out var sx) ? sx : (hiredTug[pid] == 0 ? "on its way" : "no word yet");
                p.Link($"Your tug: {tn} <color=#999>— {st}</color>", () => YourTug(pid));
            }
            p.Link("Services <color=#999>(call-ins)</color>", () => ShowMenu(pid, () => ServicesPage(pid)));
            p.Text("");
            foreach (var c in cs.OrderBy(x => x.Dist))
            {
                var hostile = HostileGroups.Contains(c.Group) ? " <color=#f66>hostile</color>" : "";
                var cc = c;
                p.Link($"{c.Name}  <color=#999>[{FactionName(c.Group)}] {Km(c.Dist)} · {c.Type}</color>{hostile}", () => Hail(pid, cc));
            }
            return p;
        }

        void Hail(int pid, Contact c)
        {
            hailFocus[pid] = c;
            ShowMenu(pid, () => ShipPage(pid, IsAutomated(c) ? $"{c.Name}: automated structure. No crew aboard. No response." : null));
        }

        /// <summary>Hail → Talk: what a sim ship knows, by topic (DediSim.TalkTopics: entity, pid → topics; null = not a sim ship).</summary>
        public Func<int, int, List<KeyValuePair<string, List<string>>>> TopicsFor;

        MenuPage ShipPage(int pid, string reply)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return ContactsPage(pid);
            if (hiredTug.TryGetValue(pid, out var mine) && mine == c.Id) return HiredPage(pid, c, reply);
            if (StructureTag.IsTagged(c.Name) && TollFor?.Invoke(c.Id, pid) is DediSim.Toll toll) return PiratePage(pid, c, toll, reply);
            if (StructureTag.IsTagged(c.Name) && CollectFor != null && legacy != null && TradeLineFor?.Invoke(c.Id) != null)   // traders pay, not wrecks / patrols / raiders
            {   // a bounty settled since: any trader pays it
                var paid = CollectFor(pid, out var credits);
                if (paid != null && credits > 0) { Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = credits }); reply = paid + (reply != null ? "\n" + reply : ""); }
            }
            var ours = StructureTag.IsTagged(c.Name);
            var said = reply ?? (string.IsNullOrEmpty(c.Says) ? "Channel open." : $"\"{c.Says}\"");
            Echo(pid, c.Name, reply ?? c.Says);
            var p = new MenuPage { Title = $"{c.Name} <color=#999>[{FactionName(c.Group)}]</color>", Body = said + StatusLine(c) + $"\n<color=#999>{c.Type}, {c.Blocks} blocks · {Km(c.Dist)}</color>" };
            if (IsAutomated(c))
            {   // a hulk / rock: your hired tug can have it
                SalvageLine(p, pid, c);
                return p;
            }
            var yard = !ours && c.Type == "BA" && !HostileGroups.Contains(c.Group) && Core.Sim.LiveRules.IsYard(c.Name);
            if (!ours && !c.OwnTrader && !yard) { p.Body = "No response." + $"\n<color=#999>{c.Type}, {c.Blocks} blocks · {Km(c.Dist)}</color>"; SalvageLine(p, pid, c); return p; }   // nobody home to talk to
            var tradeLine = ours ? TradeLineFor?.Invoke(c.Id) : null;
            if (tradeLine != null) p.Text("").Text(tradeLine);
            p.Text("");
            if (ours) p.Link("Talk", () => ShowMenu(pid, () => TalkPage(pid)));
            if (tradeLine != null) p.Link($"Trade <color=#999>(within {ApproveRange} m)</color>", () => RequestTrade(pid, c));
            else if (!ours && c.OwnTrader) p.Text("<color=#999>Trader aboard — dock with them to trade.</color>");
            if (ours) p.Link("Jobs", () => ShowMenu(pid, () => JobsPage(pid)));
            if (yard && !hiredTug.ContainsKey(pid))
                p.Link($"Hire a salvage tug <color=#999>({HireFee:N0} cr up front)</color>", () => Hire(pid, c));
            else if (yard)
                p.Text($"<color=#999>Hire a salvage tug: no — you've already got {(hiredName.TryGetValue(pid, out var hn) ? hn : "one")} on hire (one per captain).</color>");
            return p;
        }

        /// <summary>
        /// Anything nobody's aboard (hulks, ruins, rocks, empty bases) always shows the salvage line — clickable, or greyed
        /// with the reason (no tug yet, a core in it, a player's). Crewed ships and stations never get it.
        /// </summary>
        void SalvageLine(MenuPage p, int pid, Contact c)
        {
            var core = c.Core ?? "";
            var rock = (c.Name ?? "").ToLowerInvariant().Contains("asteroid");
            string why = null;
            if (core.Contains("Player")) why = "it's a player's — their core is in it";
            else if (!Workable(c)) why = $"it still has a core ({FactionName(c.Group)} owns it). Shoot the core out first";
            else if (!hiredTug.ContainsKey(pid)) why = "you need a tug — hire one at a yard or Main → Services";
            p.Text("");
            if (why != null) { p.Text($"<color=#999>Salvage {(rock ? "this rock" : "this")}: no — {why}.</color>"); return; }
            p.Link($"Have {hiredName[pid]} cut this up", () => { var sent = SendHire(pid, "cut", c.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)); ShowShip(pid, sent ? $"Passed it to {hiredName[pid]}." : $"{hiredName[pid]} isn't here — out of range. Ask again once it's within 2 km."); });
        }

        // ---- a salvage tug for hire (one per player; a fleet tug, kept on the sim agent across reloads) ----------------

        const int HireFee = 10000;
        /// <summary>Taking an item out of a hired tug's hold: this share of its value, per unit (at least 1 cr).</summary>
        const double HoldFeeShare = 0.10;
        /// <summary>Sent home to sell: the player's share of what's aboard (the yard keeps the rest, a sink) — the hire share
        /// clock's (DediSim.HireShare: less the longer the hire runs); 50 % with no sim behind it.</summary>
        double PayoutShare(int pid) => HireShareFor?.Invoke(pid) ?? 0.5;
        /// <summary>The share clock's line for the tug's page ("yard's cut 50 % → 60 % in 25 min").</summary>
        public Func<int, string> HireClockFor;
        public Func<int, double> HireShareFor;
        static readonly string[] TugNames = { "Iron Mule", "Patch Kettle", "Slow Ox", "Rust Hen", "Busy Gull", "Old Crane", "Tin Badger", "Stubborn Goat", "Grey Heron", "Fat Barnacle" };
        readonly Dictionary<int, int> hiredTug = new Dictionary<int, int>();
        readonly Dictionary<int, string> hiredName = new Dictionary<int, string>();
        readonly Dictionary<int, bool> hiredFollow = new Dictionary<int, bool>();
        /// <summary>What the player paid for the hire (a refund gives back exactly this).</summary>
        readonly Dictionary<int, int> hiredFee = new Dictionary<int, int>();
        /// <summary>The hired tug's latest status from its PfServer ("cutting X (40 %) · hold 12 % · 1.2 km, 30° left").</summary>
        readonly Dictionary<int, string> hiredStat = new Dictionary<int, string>();
        public void HireStat(int pid, string text) { hiredStat[pid] = text; }
        /// <summary>The sim's tug fleet: (home system, pid, playfield, fee) → the tug's name, null = none idle there,
        /// "" = no fleet (a tug made for the hire). And a hire's end: (pid, "dismiss" | "release" | "lost", hold).</summary>
        public Func<string, int, string, int, DediSim.HireResult> HireTugFor;
        public Func<int, string, string, double, bool> HireEndFor;
        /// <summary>Dismiss a hired tug the sim holds out of range (true = done; false = it's live, its PfServer does it).</summary>
        public Func<int, bool> HireDismissOffline;
        public Func<int, bool> HireIsOffline;
        readonly System.Random hireRng = new System.Random();

        /// <summary>What a hired tug can take: a hulk with no core or an unowned one (NoFaction: wild POI ruins — nobody
        /// can fly or claim them back; the tug's own check agrees), wreck, debris, or a resource rock.</summary>
        static bool Workable(Contact c)
        {
            var n = (c.Name ?? "").ToLowerInvariant();
            var core = c.Core ?? "";
            return core == "None" || core == "NoFaction" || new[] { "wreck", "debris", "asteroid" }.Any(n.Contains);
        }

        /// <summary>An order to the hired tug's PfServer; false = not sent (no contact, or the sim holds it out of range).</summary>
        bool SendHire(int pid, string sub, params object[] rest)
        {
            if (!hailPf.TryGetValue(pid, out var pf)) { Say(pid, "Lost contact."); return false; }
            if (sub != "start" && (HireIsOffline?.Invoke(pid) ?? false))
            {   // the sim holds it (out of range, maybe another playfield): orders wait until it's live again — dismiss works
                Echo(pid, hiredName.TryGetValue(pid, out var hn) ? hn : "Your tug", "…(out of range — come within 2 km of us; we can still be sent home from here)");
                return false;
            }
            api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(new object[] { pid, "fly", "hire", sub }.Concat(rest).ToArray()));
            log($"hire {pid}: {sub} {string.Join(" ", rest)}");
            return true;
        }

        void Hire(int pid, Contact station)
        {
            if (legacy == null) { ShowShip(pid, "(hiring needs the legacy API)"); return; }
            RequestPlayerInfo(pid, info =>
            {
                if (hiredTug.ContainsKey(pid)) { ShowShip(pid, "You've got a tug already."); return; }
                if (info == null || info.credits < HireFee) { ShowShip(pid, $"It's {HireFee:N0} cr up front — you've got {(info?.credits ?? 0):N0}."); return; }
                var pf = hailPf.TryGetValue(pid, out var hp) ? hp : "";
                var got = HireTugFor != null ? HireTugFor(pf, pid, pf, HireFee) : new DediSim.HireResult();
                if (got == null) { ShowShip(pid, "All our tugs are out on jobs. Try another yard (Main → Services)."); return; }
                var name = got.Name.Length > 0 ? got.Name : TugNames[hireRng.Next(TugNames.Length)];
                Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = -HireFee });
                hiredTug[pid] = got.Entity; hiredName[pid] = name; hiredFollow[pid] = true; hiredFee[pid] = HireFee;
                if (got.Entity > 0) { ShowShip(pid, $"Done — {HireFee:N0} cr. {name} is at our berth — undocking now, coming to you."); return; }
                SendHire(pid, "start", name.Replace(' ', '_'), station.Name);
                ShowShip(pid, $"Done — {HireFee:N0} cr. {name} is on its way to you. Hail it when it's in: follow, cut what you point at, open its hold, or send it home to sell (you get {PayoutShare(pid):P0} — less the longer you keep it).");
            });
        }

        // ---- Services: call-ins over COMMS, from anywhere ------------------------------------------------------------------

        /// <summary>Stations that can send a tug to a playfield (DediSim.TugOffers).</summary>
        public Func<string, double, List<DediSim.TugOffer>> TugOffersFor;
        /// <summary>Calling one in: the station-side fee plus this per light-year; it takes a minute plus this per light-year.</summary>
        const int CallPerLy = 40;
        const double CallBaseSeconds = 60, CallSecondsPerLy = 10, CallMaxSeconds = 900, CallRangeLy = 60;
        sealed class TugCall { public int Pid; public string Name = "", Station = "", Pf = ""; public double At; }
        readonly List<TugCall> calls = new List<TugCall>();
        readonly System.Diagnostics.Stopwatch callClock = System.Diagnostics.Stopwatch.StartNew();

        /// <summary>Over the radio costs more than at the station's desk (+50 % base) — being there stays the better deal.</summary>
        static int CallPrice(double ly) => (int)Math.Round(HireFee * 1.5 + CallPerLy * ly);
        static double CallEta(double ly) => Math.Min(CallMaxSeconds, CallBaseSeconds + CallSecondsPerLy * ly);
        static string Mins(double sec) => sec < 90 ? $"{sec:0} s" : $"{sec / 60:0} min";

        MenuPage ServicesPage(int pid)
        {
            var p = new MenuPage { Title = "COMMS — services", Body = "Call-ins: paid up front, they come to you." };
            if (hiredTug.ContainsKey(pid) || calls.Any(x => x.Pid == pid)) p.Text("<color=#999>Salvage tug — you've got one already (or one's on its way).</color>");
            else p.Link("Call for a salvage tug", () => ShowMenu(pid, () => CallTugPage(pid)));
            return p;
        }

        MenuPage CallTugPage(int pid)
        {
            var pf = hailPf.TryGetValue(pid, out var x) ? x : "";
            var offers = TugOffersFor?.Invoke(pf, CallRangeLy) ?? new List<DediSim.TugOffer>();
            var p = new MenuPage { Title = "COMMS — call for a salvage tug",
                Body = offers.Count == 0 ? $"No yard within {CallRangeLy:0} LY can send one out here." : "Who'll send one, what it costs, how long it takes:" };
            foreach (var o in offers)
            {
                var oo = o;
                var where = o.Ly <= 0 ? "this system" : $"{o.Ly:0} LY";
                if (o.Idle <= 0) { p.Text($"<color=#999>{o.Station} ({o.System}, {where}) — all tugs out</color>"); continue; }
                p.Link($"{o.Station} <color=#999>({o.System}, {where}) — {CallPrice(o.Ly):N0} cr, ~{Mins(CallEta(o.Ly))}, {o.Idle} free</color>", () => CallTug(pid, oo));
            }
            return p;
        }

        void CallTug(int pid, DediSim.TugOffer o)
        {
            if (legacy == null || !hailPf.TryGetValue(pid, out var pf)) { Say(pid, "Lost contact."); return; }
            var price = CallPrice(o.Ly); var eta = CallEta(o.Ly);
            RequestPlayerInfo(pid, info =>
            {
                if (hiredTug.ContainsKey(pid) || calls.Any(c => c.Pid == pid)) { ShowMenu(pid, () => ServicesPage(pid), false); return; }
                if (info == null || info.credits < price) { Echo(pid, o.Station, $"That's {price:N0} cr up front — you've got {(info?.credits ?? 0):N0}."); return; }
                var got = HireTugFor != null ? HireTugFor(o.System, pid, pf, price) : new DediSim.HireResult();
                if (got == null) { Echo(pid, o.Station, "Sorry — every tug we've got is out. Try another yard."); return; }
                var name = got.Name.Length > 0 ? got.Name : TugNames[hireRng.Next(TugNames.Length)];
                Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = -price });
                hiredFee[pid] = price;
                if (got.Entity > 0)
                {   // docked at that yard right here: it flies over itself
                    hiredTug[pid] = got.Entity; hiredName[pid] = name; hiredFollow[pid] = true;
                    Echo(pid, o.Station, $"Paid, {price:N0} cr. {name} is at our berth — undocking now, coming to you.");
                    menus.Remove(pid); return;
                }
                calls.Add(new TugCall { Pid = pid, Name = name, Station = o.Station, Pf = pf, At = callClock.Elapsed.TotalSeconds + eta });
                Echo(pid, o.Station, $"Paid, {price:N0} cr. {name} is on its way from {o.System} — about {Mins(eta)}. Stay where you are, or it'll find you anyway.");
                log($"hire {pid}: called '{name}' from {o.Station} ({o.System}, {o.Ly:0} LY) for {price} cr, eta {eta:0} s");
                menus.Remove(pid);
            });
        }

        /// <summary>Calls whose time has come: the tug arrives where the player is now (hired as at a station from here on).</summary>
        void PumpCalls()
        {
            var now = callClock.Elapsed.TotalSeconds;
            foreach (var c in calls.Where(x => x.At <= now).ToList())
            {
                calls.Remove(c);
                var pf = hailPf.TryGetValue(c.Pid, out var cur) ? cur : c.Pf;   // the last playfield we knew them in
                hiredTug[c.Pid] = 0; hiredName[c.Pid] = c.Name; hiredFollow[c.Pid] = true;
                api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(c.Pid, "fly", "hire", "start", c.Name.Replace(' ', '_'), c.Station));
                log($"hire {c.Pid}: '{c.Name}' arriving in '{pf}'");
            }
        }

        /// <summary>Credits the hired tug found: straight to the balance, no cut (the tug only carried them).</summary>
        public void HireCash(int pid, int credits)
        {
            if (credits <= 0 || legacy == null) return;
            Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = credits });
            Echo(pid, hiredName.TryGetValue(pid, out var n) ? n : "Your tug", $"Found {credits:N0} cr aboard — wired to your account.");
            log($"hire {pid}: {credits} cr found, wired in full");
        }

        /// <summary>After a reload: a hire the sim still has (entity 0 = a call-in that hadn't arrived: it's called again).</summary>
        public void RestoreHire(int pid, string name, int entity, int fee, string pf, string home, bool offline = false)
        {
            if (pid <= 0) return;
            hiredFee[pid] = fee; hiredName[pid] = name; hiredFollow[pid] = true;
            // out of range (held by the sim, maybe with no hull at all): it's there, not on its way — never called twice
            if (entity > 0 || offline) { hiredTug[pid] = Math.Max(0, entity); hiredStat[pid] = offline ? "out of range — back live within 2 km of it" : ""; log($"hire {pid}: '{name}' ({entity}{(offline ? ", out of range" : "")}) restored"); return; }
            hiredName.Remove(pid);
            calls.Add(new TugCall { Pid = pid, Name = name, Station = home + "'s yard", Pf = pf, At = callClock.Elapsed.TotalSeconds + 30 });
            log($"hire {pid}: '{name}' was on its way — called again");
        }

        /// <summary>The PfServer reports the hired tug's entity.</summary>
        public void OnHired(int pid, int entity) { if (hiredTug.ContainsKey(pid)) hiredTug[pid] = entity; }

        MenuPage HiredPage(int pid, Contact c, string reply)
        {
            var follow = !hiredFollow.TryGetValue(pid, out var f) || f;
            var name = hiredName.TryGetValue(pid, out var n) ? n : c.Name;
            var stat = hiredStat.TryGetValue(pid, out var hs) ? hs : Km(c.Dist);
            var p = new MenuPage { Title = $"{name} <color=#999>[your tug]</color>",
                Body = (reply ?? "\"Boss?\"") + StatusLine(c) + $"\n<color=#999>{stat}</color>" };
            p.Link(follow ? "Follow me <color=#4f4>(following)</color>" : "Follow me <color=#f84>(holding)</color>", () => { hiredFollow[pid] = true; SendHire(pid, "follow"); RefreshMenu(pid); });
            p.Link("Hold here", () => { hiredFollow[pid] = false; SendHire(pid, "stay"); RefreshMenu(pid); });
            p.Link($"Open the hold <color=#999>(take what you want: {HoldFeeShare:P0} of its value per item)</color>", () => SendHire(pid, "hold"));
            p.Link("Clear the work list", () => SendHire(pid, "clear"));
            // what it could cut from here: one click puts it on the list (same as hailing the wreck / rock)
            var cs = hailContacts.TryGetValue(pid, out var l) ? l : new List<Contact>();
            var work = cs.Where(x => x.Id != c.Id && IsAutomated(x) && Workable(x)).OrderBy(x => x.Dist).Take(5).ToList();
            p.Text("").Text(work.Count > 0 ? "<color=#999>Could cut — click to add to the list (what your last scan saw nearby):</color>" : "<color=#999>Nothing to cut in range — point at a wreck / rock and hail it.</color>");
            foreach (var w in work)
            {
                var ww = w;
                p.Link($"{w.Name} <color=#999>{Km(w.Dist)}</color>", () => { var sent = SendHire(pid, "cut", ww.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)); ShowShip(pid, sent ? $"\"{ww.Name} — on the list.\"" : $"{hiredName[pid]} isn't here — out of range. Ask again once it's within 2 km."); });
            }
            var clock = HireClockFor?.Invoke(pid) ?? "";
            p.Text("").Link($"<color=#f84>Take it home and sell</color> <color=#999>(you get {PayoutShare(pid):P0}; ENDS the hire)</color>", () => ShowMenu(pid, () => DismissPage(pid, name)));
            if (clock.Length > 0) p.Text($"<color=#999>{clock}</color>");
            return p;
        }

        /// <summary>Main → Your tug: its page even out of scan range (orders go to its PfServer either way).</summary>
        void YourTug(int pid)
        {
            if (!hiredTug.TryGetValue(pid, out var ent) || (ent == 0 && !(HireIsOffline?.Invoke(pid) ?? false)))
            {   // on its way (a held-out-of-range one still gets its page: it can be sent home from anywhere)
                var st = hiredStat.TryGetValue(pid, out var s0) && s0.Length > 0 ? s0 : "Still on its way to you.";
                ShowMenu(pid, () => new MenuPage { Title = "Your tug", Body = st }); return;
            }
            var cs = hailContacts.TryGetValue(pid, out var l) ? l : new List<Contact>();
            var c = cs.FirstOrDefault(x => x.Id == ent) ?? new Contact { Id = ent, Name = hiredName.TryGetValue(pid, out var n) ? n : "Your tug", Type = "CV", Core = "Npc", Dist = 0 };
            hailFocus[pid] = c;
            ShowMenu(pid, () => HiredPage(pid, c, null));
        }

        MenuPage DismissPage(int pid, string name)
        {
            return new MenuPage { Title = $"{name} — send it home?", Body = $"It flies home and sells what's aboard; you get {PayoutShare(pid):P0}. The hire ends — a new tug costs a new fee." }
                .Text("").Link("No, keep it", () => ShowShip(pid))
                .Link("<color=#f84>Yes, send it home</color>", () => { if (!(HireDismissOffline?.Invoke(pid) ?? false)) SendHire(pid, "dismiss"); menus.Remove(pid); });
        }

        /// <summary>The hired tug's hold as a trade window: take what you want, a fee per item; it buys nothing.</summary>
        public void OpenHiredHold(int pid, int entity, string manifest)
        {
            var items = Core.Sim.World.ParseHold(manifest);
            var name = hiredName.TryGetValue(pid, out var n) ? n : "Your tug";
            // credits it found (a dead crew's strongbox): wired to you, the crew's cut off the top — never sold back to you item by item
            var found = items.TryGetValue(Core.Sim.World.Strongbox, out var cards) ? cards : 0;
            string wired = null;
            if (found > 0 && legacy != null)
            {
                var pay = found;   // money isn't salvage: in full
                Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = pay });
                SendHire(pid, "took", Core.Sim.World.FormatHold(new Dictionary<string, int> { [Core.Sim.World.Strongbox] = found }));
                items.Remove(Core.Sim.World.Strongbox);
                wired = $"Found {found:N0} cr aboard — wired to your account.";
                Echo(pid, name, wired);
                log($"hire {pid}: {found} cr found aboard, {pay} wired");
            }
            if (items.Count == 0) { ShowShip(pid, wired ?? "Hold's empty."); return; }
            var deal = new DediTrade.ShipDeal
            {
                Ship = name, Cash = 0, BuysAt = _ => 0,
                Title = $"{name} — the hold: take what you want, then Done",
                TakeVerb = "took (handling fee)",
                // the fee is a share of what you take, summed then rounded — no per-item minimum (a 3 cr part costs 0.3 cr)
                // the window shows 49 stacks: the dearest per item first, then the biggest stacks by value; the rest waits for next time
                Stock = items.OrderByDescending(kv => Core.Sim.World.Value(kv.Key, 1)).ThenByDescending(kv => Core.Sim.World.Value(kv.Key, kv.Value)).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                             .Select(kv => new DediTrade.StockLine { Item = kv.Key, Amt = kv.Value, Price = Core.Sim.World.Value(kv.Key, 1) * HoldFeeShare }).ToList(),
                Done = (taken, paid) => { if (taken.Count > 0) SendHire(pid, "took", Core.Sim.World.FormatHold(taken)); },
            };
            var shown = 0; var kinds = 0;
            foreach (var x in deal.Stock)
            {   // what the window really shows (DediTrade fills it the same way): a kind counts once any of it is in
                if (shown >= DediTrade.WindowSlots) break;
                shown += (int)Math.Ceiling(x.Amt / (double)DediTrade.StackOf(Core.Sim.World.KeyBase(x.Item, out _, out _))); kinds++;
            }
            var more = deal.Stock.Count - kinds;
            deal.Desc = (wired != null ? wired + "\n" : "") + (more > 0 ? $"+{more} more kind{(more > 1 ? "s" : "")} aboard, cheaper ones — take some, then open the hold again. " : "") + $"Handling fee: {HoldFeeShare:P0} of the market value of what you take (worth aboard: {deal.Stock.Sum(x => x.Price * x.Amt / HoldFeeShare):N0} cr). Take what you want; Done settles it. We buy nothing.";
            if (!trade.OpenShip(pid, entity, deal, Request)) ShowShip(pid, "Can't open the hold right now — try again in a moment.");
            else { trade.AfterTrade = OnTradeDone; menus.Remove(pid); }
        }

        /// <summary>Dismissed: the tug flies home and sells; the player's share is wired on the sale (<see cref="PayOwedTo"/>, from
        /// the sim's owed list — later if they're offline then). Without a sim tug (no fleet) it's paid now, as before.</summary>
        public void HireDone(int pid, string manifest)
        {
            var name = hiredName.TryGetValue(pid, out var n) ? n : "Your tug";
            var worth = Core.Sim.World.ParseHold(manifest).Sum(kv => Core.Sim.World.Value(kv.Key, kv.Value));
            var share = PayoutShare(pid);   // read before the hire ends (the clock goes with it)
            hiredTug.Remove(pid); hiredName.Remove(pid); hiredFollow.Remove(pid); hiredFee.Remove(pid); hiredStat.Remove(pid);
            var later = HireEndFor?.Invoke(pid, "dismiss", manifest, share) ?? false;
            if (later)
            {
                Echo(pid, name, worth > 0 ? $"Heading home with about {worth:N0} cr of salvage. Your {share:P0} is wired when it sells — if we get there." : "Heading home — nothing much worth selling aboard. Call us again.");
                log($"hire {pid}: dismissed, {manifest} worth {worth:0} — paid on the sale");
                return;
            }
            var pay = (int)Math.Round(worth * share);
            if (pay > 0 && legacy != null) Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = pay });
            Echo(pid, name, pay > 0 ? $"Sold the lot ({worth:N0} cr) — {pay:N0} cr wired to you. Call us again." : "Nothing worth selling aboard. Call us again.");
            log($"hire {pid}: done, {manifest} worth {worth:0} → paid {pay}");
        }

        /// <summary>Money the sim owed a player (a hired tug's share on its sale): to their balance, and why.</summary>
        public void PayOwedTo(int pid, int credits, string why)
        {
            if (credits > 0 && legacy != null) Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = credits });
            Say(pid, credits > 0 ? $"{why}: {credits:N0} cr wired to your account." : why + ".");
        }

        /// <summary>The hired tug is gone (destroyed / left with the playfield), or never arrived (<paramref name="refund"/>: fee back).</summary>
        public void HireLost(int pid, bool refund)
        {
            var name = hiredName.TryGetValue(pid, out var n) ? n : "Your tug";
            var fee = hiredFee.TryGetValue(pid, out var hf) ? hf : HireFee;
            if (refund && legacy != null) { Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = fee }); Echo(pid, name, $"Couldn't get a tug out to you — {fee:N0} cr refunded."); }
            hiredTug.Remove(pid); hiredName.Remove(pid); hiredFollow.Remove(pid); hiredFee.Remove(pid); hiredStat.Remove(pid);
            HireEndFor?.Invoke(pid, refund ? "release" : "lost", "", 0);
            log($"hire {pid}: the tug is gone{(refund ? " (never spawned, refunded)" : "")}");
        }

        /// <summary>A pirate answers a hail: its price to look the other way. Paying makes it break off and leave you be.</summary>
        MenuPage PiratePage(int pid, Contact c, DediSim.Toll toll, string reply)
        {
            var text = reply ?? toll.Line;
            Echo(pid, c.Name, text);
            var p = new MenuPage { Title = $"{c.Name} <color=#f66>[Pirates]</color>", Body = $"\"{text}\"\n<color=#999>{Km(c.Dist)}</color>" };
            if (toll.Price > 0) p.Text("").Link($"Pay {toll.Price:N0} cr", () => PayToll(pid, c, toll.Price));
            return p;
        }

        void PayToll(int pid, Contact c, int price)
        {
            if (legacy == null || !hailPf.TryGetValue(pid, out var pf)) { Say(pid, "Lost contact."); return; }
            RequestPlayerInfo(pid, info =>
            {
                // the dialog may have sat open: still alive, still the same price? (no paying a dead pirate)
                var now = TollFor?.Invoke(c.Id, pid);
                if (now == null || now.Price <= 0) { Echo(pid, c.Name, "(no answer — they're gone)"); return; }
                if (now.Price != price) { Echo(pid, c.Name, $"Price changed: {now.Price:N0} cr. Hail again."); return; }
                if (info == null || info.credits < price) { Echo(pid, c.Name, "You haven't got that kind of money. Shame."); return; }
                Request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = -price });
                api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, Wire.Pack(0, "fly", "sim", "paid", c.Id, pid));
                TollPaid?.Invoke(c.Id, price);   // the pirate pockets it (banked at its hub, or in its wreck if you shoot it after all)
                Echo(pid, c.Name, $"Pleasure doing business. ({price:N0} cr paid)");
                log($"toll: {pid} paid {price} cr to {c.Id} '{c.Name}'");
            });
        }

        /// <summary>Talk: the topics this crew has something on (with how much), each its own page.</summary>
        MenuPage TalkPage(int pid)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return ContactsPage(pid);
            var topics = TopicsFor?.Invoke(c.Id, pid);
            var p = new MenuPage { Title = $"{c.Name} — talk" };
            if (topics == null) { p.Body = string.IsNullOrEmpty(c.Says) ? "Not much to say." : $"\"{c.Says}\""; return p; }
            if (topics.Count == 0) { p.Body = "\"Quiet out there lately. Nothing worth telling.\""; return p; }
            p.Body = "\"What do you want to know?\"";
            foreach (var t in topics)
            {
                var topic = t.Key;
                p.Link($"{topic} <color=#999>({t.Value.Count})</color>", () => ShowMenu(pid, () => TopicPage(pid, topic)));
            }
            return p;
        }

        /// <summary>One topic: everything the crew says about it (rebuilt when shown — news moves on).</summary>
        MenuPage TopicPage(int pid, string topic)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return ContactsPage(pid);
            var said = TopicsFor?.Invoke(c.Id, pid)?.FirstOrDefault(t => t.Key == topic).Value;
            var text = said != null && said.Count > 0 ? string.Join("\n\n", said.Select(x => $"\"{x}\"")) : "\"Nothing more on that.\"";
            if (said != null) foreach (var x in said) Echo(pid, c.Name, x);
            return new MenuPage { Title = $"{c.Name} — {topic.ToLowerInvariant()}", Body = text };
        }

        /// <summary>Jobs: work this ship offers. Escort today (it decides per leg whether it wants one); bounties.</summary>
        MenuPage JobsPage(int pid)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return ContactsPage(pid);
            var bounty = BountyFor?.Invoke(c.Id, pid);
            var p = new MenuPage { Title = $"{c.Name} — jobs", Body = "Work we might have for you:" }
                .Link("Escort <color=#999>— see us safely to our jump</color>", () => SendOrder(pid, c, "escort", 0));
            if (bounty != null) p.Link("Bounty <color=#999>— there's a pirate we want dead</color>", () => ShowMenu(pid, () => BountyPage(pid, c, bounty)));
            return p;
        }

        MenuPage BountyPage(int pid, Contact c, string text)
        {
            Echo(pid, c.Name, text);
            return new MenuPage { Title = $"{c.Name} — bounty", Body = $"\"{text}\"" }
                .Text("").Link("Accept", () => ShowShip(pid, AcceptBountyFor?.Invoke(c.Id, pid) ?? "That job's gone."));
        }

        /// <summary>The trader's escort offer (text from its PfServer): Accept | Decline.</summary>
        void ShowEscortOffer(int pid, int shipId, string text)
        {
            if (!hailFocus.TryGetValue(pid, out var c) || c.Id != shipId) { Say(pid, text); return; }
            Echo(pid, c.Name, text);
            ShowMenu(pid, () => new MenuPage { Title = $"{c.Name} — escort contract", Body = text }
                .Text("").Link("Accept", () => SendOrder(pid, c, "accept", 0)).Link("Decline", () => SendOrder(pid, c, "decline", 0)));
        }

        /// <summary>Orders (parked — not on the ship page): follow at a distance, hold, join / leave your faction, release.</summary>
        MenuPage OrdersPage(int pid)
        {
            if (!hailFocus.TryGetValue(pid, out var c)) return ContactsPage(pid);
            var joined = (c.Status ?? "").Contains("under your colours");
            return new MenuPage { Title = $"{c.Name} — orders", Body = "Orders?" + StatusLine(c) }
                .Link("Follow me <color=#999>(100 m)</color>", () => SendOrder(pid, c, "follow", 100))
                .Link("Follow me <color=#999>(250 m)</color>", () => SendOrder(pid, c, "follow", 250))
                .Link("Follow me <color=#999>(500 m)</color>", () => SendOrder(pid, c, "follow", 500))
                .Link("Hold position", () => SendOrder(pid, c, "hold", 0))
                .Link(joined ? "Leave my faction" : "Join my faction", () => SendOrder(pid, c, joined ? "leave" : "join", 0))
                .Link("Release", () => SendOrder(pid, c, "release", 0));
        }

        static string Km(int m) => m >= 1000 ? $"{m / 1000.0:0.0} km" : $"{m} m";

        /// <summary>Directed order to one ship; its PfServer answers with "orderreply", shown on the ship page.</summary>
        void SendOrder(int pid, Contact c, string kind, int distance)
        {
            if (!hailPf.TryGetValue(pid, out var pf)) { Say(pid, "Lost contact."); return; }
            api.Network.SendToPlayfieldServer(LivingSystemsMod.Channel, pf, kind == "escort" || kind == "accept" || kind == "decline"
                ? Wire.Pack(pid, "fly", "escort", kind == "escort" ? "offer" : kind, c.Id)
                : Wire.Pack(pid, "fly", "order", c.Id, kind, distance));
            log($"order {pid} -> {c.Id} '{c.Name}': {kind} {distance}");
        }

        /// <summary>Hail → Trade with a sim trader: its hold (DediSim fills it in; null = not a sim trader).</summary>
        public Func<int, int, DediTrade.ShipDeal> ShipDealFor;   // (ship entity, player)
        /// <summary>Hail screen: "Selling: … Buys: …" for a sim trader (null = not one).</summary>
        public Func<int, string> TradeLineFor;
        /// <summary>A pirate's price to look the other way (null = not one of our pirates).</summary>
        public Func<int, int, DediSim.Toll> TollFor;
        public Action<int, int> TollPaid;
        /// <summary>Bounties: what this ship offers (text, null = none), taking it, and paying out settled ones on any hail.</summary>
        public Func<int, int, string> BountyFor, AcceptBountyFor;
        public CollectDelegate CollectFor;
        public delegate string CollectDelegate(int pid, out int credits);

        /// <summary>A ship trade settled: back to that ship's page with the outcome on top.</summary>
        void OnTradeDone(int pid, string outcome) { if (hailFocus.ContainsKey(pid)) ShowShip(pid, outcome); }

        void RequestTrade(int pid, Contact c)
        {
            trade.AfterTrade = OnTradeDone;
            if (StructureTag.IsTagged(c.Name) && ShipDealFor?.Invoke(c.Id, pid) is DediTrade.ShipDeal deal)
            {   // our traders sell from their own hold — heave-to range, no TRADE_PAD needed
                if (c.Dist > ApproveRange) { ShowShip(pid, $"You're {c.Dist:0} m out. Come within {ApproveRange} m and we'll open the hold."); return; }
                if (legacy == null) { ShowShip(pid, "(trade needs the legacy API)"); return; }
                if (!trade.OpenShip(pid, c.Id, deal, Request)) ShowShip(pid, "Can't open the hold right now — try again in a moment.");
                else menus.Remove(pid);   // the trade window replaces the dialog
                return;
            }
            string answer;
            if (IsAutomated(c)) answer = "No response. It's an automated structure.";
            else if (HostileGroups.Contains(c.Group)) answer = "Not dealing with you. Channel closed.";
            else if (c.OwnTrader) answer = "Our trader is aboard — dock with us and talk to them in person.";
            else if (c.Dist > ApproveRange) answer = $"You're {c.Dist:0} m out (as of your scan). Come within {ApproveRange} m and hail again.";
            else answer = "Nothing to sell you from out here — dock with us and see our trader.";   // the old TRADE_PAD kiosk is retired
            log($"trade request {pid} -> {c.Id} '{c.Name}': {answer}");
            ShowShip(pid, answer);
        }

        const int ApproveRange = 1100;   // a ship's hold opens for trade within this

        /// <summary>Unmanned = small bases and obvious automata. They answer, but can't trade.</summary>
        static bool IsAutomated(Contact c)
        {
            var n = (c.Name ?? "").ToLowerInvariant();
            if (new[] { "satellite", "beacon", "relay", "drone", "wreck", "debris", "buoy", "sign", "gate", "jump", "asteroid", "destroyed", "abandoned", "derelict", "ruin" }.Any(n.Contains)) return true;
            if ((c.Core ?? "").Contains("None")) return true;   // coreless = a derelict (a ship killed live keeps its own name)
            // Admin core + no trader aboard = infrastructure, any size (trading stations have a trader, so they're excluded).
            if ((c.Core ?? "").Contains("Admin") && !c.OwnTrader) return true;
            return c.Type == "BA" && c.Blocks > 0 && c.Blocks < 300;
        }
    }
}
