using System;
using System.Collections.Generic;
using System.Linq;
using Eleon.Modding;

namespace LivingSystems
{
    /// <summary>
    /// Spike for trade through the game's own item-exchange window (Request_Player_ItemExchange).
    /// Placeholder prices; the point is to learn how the window behaves.
    /// </summary>
    sealed class DediTrade
    {
        /// <summary>The exchange window's grid: 7 × 7 slots.</summary>
        public const int WindowSlots = 49;

        /// <summary>An item's own stack size (the game's configs; MoneyCard 50k; unknown → 500).</summary>
        public static int StackOf(string baseName)
        {
            if (baseName == Core.Sim.World.Strongbox) return 50000;
            var per = Core.Sim.World.StackSizeOf?.Invoke(baseName) ?? 0;
            return per > 0 ? per : 500;
        }

        const int SellPrice = 10, BuyPrice = 15;   // credits per item, placeholder

        readonly IModApi api;
        readonly Action<string> log;
        readonly Action<int, string> say;
        Dictionary<string, int> nameToId;
        Dictionary<int, string> idToName;

        sealed class Pending { public string Kind; public List<ItemStack> Offered; public ushort OpenSeq; public bool Acked; public ShipDeal Deal; public string Quoted = ""; public int Ship; public double Opened; public bool Funded, Checking; }

        /// <summary>The box's contents as a key: a quote stands while the box doesn't change.</summary>
        static string BoxKey(IEnumerable<ItemStack> box) => string.Join(",", box.GroupBy(i => i.id).OrderBy(g => g.Key).Select(g => g.Key + "x" + g.Sum(i => i.count)));

        /// <summary>One line of a sim trader's stock: item, how many, unit price to you.</summary>
        public sealed class StockLine { public string Item = ""; public int Amt; public double Price; }

        /// <summary>A sim trader's hold + stores, sold through the item-exchange window (hail → Trade).</summary>
        public sealed class ShipDeal
        {
            public string Ship = "";
            public List<StockLine> Stock = new List<StockLine>();
            /// <summary>What it pays per unit for an item you put in (by item name); 0 = not wanted, it goes back to you.</summary>
            public Func<string, double> BuysAt;
            /// <summary>What it wants this trip: premium price for up to <see cref="WantLeft"/> units ("" = nothing).</summary>
            public string WantItem = "";
            public int WantLeft;
            public double WantPrice;
            public Action<int> Fulfilled;
            /// <summary>Credits it can spend buying from you this deal.</summary>
            public int Cash;
            /// <summary>Window text instead of the trader's (a hired tug's hold: take only, nothing bought).</summary>
            public string Title, Desc;
            /// <summary>The word for taking goods out ("bought" from a trader; "took" from your own tug's hold).</summary>
            public string TakeVerb = "bought";
            /// <summary>Called after the deal: units taken per item, and the credits that changed hands (+ = you paid it).</summary>
            public Action<Dictionary<string, int>, int> Done;
        }

        /// <summary>
        /// Hail → Trade with a sim trader: one box holding its stock. Take what you want (charged per unit), put in what it
        /// buys (paid), anything it doesn't want comes back to you. One button: Deal.
        /// </summary>
        /// <param name="ship">The trader's entity id: one customer at a time (two windows on one hold would sell the same goods twice).</param>
        public bool OpenShip(int pid, int ship, ShipDeal d, Func<CmdId, object, ushort> request)
        {
            EnsureMapping();
            var busy = pending.FirstOrDefault(kv => kv.Key != pid && kv.Value.Kind == "ship" && kv.Value.Ship == ship && clock.Elapsed.TotalSeconds - kv.Value.Opened < 120);
            if (busy.Value != null) { say(pid, $"{d.Ship}: we're serving another captain — give us a minute."); return true; }
            if (!HasMapping) { say(pid, "(no item list yet — it comes from your playfield server; try again in a moment)"); return false; }
            var stock = new List<ItemStack>();
            var offeredById = new Dictionary<string, StockLine>(StringComparer.Ordinal);
            foreach (var line in d.Stock.Where(x => x.Amt > 0))
            {
                var baseName = Core.Sim.World.KeyBase(line.Item, out var ammo, out var decay);   // a token etc.: its own data goes along
                if (!nameToId.TryGetValue(baseName, out var id)) continue;
                offeredById[line.Item] = line;
                var per = StackOf(baseName);
                for (int left = line.Amt; left > 0 && stock.Count < WindowSlots; left -= per)
                    stock.Add(new ItemStack { id = id, count = Math.Min(per, left), slotIdx = (byte)stock.Count, ammo = ammo, decay = decay });
            }
            var list = string.Join(", ", offeredById.Values.Select(x => $"{Core.Sim.World.Humanize(x.Item)} {x.Price:0}"));
            var info = new ItemExchangeInfo
            {
                id = pid,
                title = d.Title ?? $"{d.Ship} — take what you buy, put in what you sell, then Done",   // the client keeps the first button label it saw
                desc = d.Desc ?? (list.Length > 0 ? $"Prices (cr each): {list}. " : "Nothing to sell this trip. ") +
                       $"Put in what you want to sell — we have {d.Cash:N0} cr to spend. Done settles it; what we won't take comes back to you.",
                buttonText = "Done",
                items = stock.ToArray(),
            };
            pending[pid] = new Pending { Kind = "ship", Offered = stock, Deal = d, Quoted = BoxKey(stock), Ship = ship, Opened = clock.Elapsed.TotalSeconds };
            pending[pid].OpenSeq = request(CmdId.Request_Player_ItemExchange, info);
            log($"trade ship open for {pid} with '{d.Ship}': {Describe(stock)} seq={pending[pid].OpenSeq}");
            return true;
        }
        readonly Dictionary<int, Pending> pending = new Dictionary<int, Pending>();
        /// <summary>A ship deal is settled (player, what happened): the hail menu shows the ship page again with it.</summary>
        public Action<int, string> AfterTrade;

        /// <summary>Asks the player's credit balance (async: the answer comes with a later event).</summary>
        readonly Action<int, Action<double>> balance;

        public DediTrade(IModApi api, Action<string> log, Action<int, string> say, Action<int, Action<double>> balance = null) { this.api = api; this.log = log; this.say = say; this.balance = balance; }

        /// <summary>
        /// GetBlockAndItemMapping() is empty on the Dedi (verified); the PfServer sends it over instead.
        /// </summary>
        public void SetMapping(Dictionary<string, int> m)
        {
            nameToId = m;
            idToName = new Dictionary<int, string>();
            foreach (var kv in m) if (!idToName.ContainsKey(kv.Value)) idToName[kv.Value] = kv.Key;
            log($"item mapping from PfServer: {m.Count} names");
        }

        public bool TryId(string name, out int id) { EnsureMapping(); return nameToId.TryGetValue(name, out id); }

        public bool HasMapping => nameToId != null && nameToId.Count > 0;

        void EnsureMapping()
        {
            if (nameToId != null) return;
            nameToId = new Dictionary<string, int>(); idToName = new Dictionary<int, string>();
        }

        public string Name(int id) { EnsureMapping(); return idToName.TryGetValue(id, out var n) ? n : "#" + id; }

        public void Items(int pid, string filter)
        {
            EnsureMapping();
            var hits = nameToId.Where(kv => string.IsNullOrEmpty(filter) || kv.Key.IndexOf(filter, StringComparison.OrdinalIgnoreCase) >= 0)
                               .OrderBy(kv => kv.Key).ToList();
            foreach (var kv in hits.Take(200)) log($"item '{kv.Key}' = {kv.Value}");
            say(pid, $"{hits.Count} match '{filter}': " + string.Join(", ", hits.Take(15).Select(kv => $"{kv.Key}={kv.Value}")) +
                     (hits.Count > 15 ? " … (full list in server log)" : ""));
        }

        /// <summary>/ls trade sell  |  /ls trade buy [name count]...</summary>
        public void Open(int pid, IReadOnlyList<string> args, Func<CmdId, object, ushort> request)
        {
            EnsureMapping();
            if (!HasMapping) say(pid, "no item list yet (it comes from your playfield server); stock may be empty");
            var kind = args.Count > 0 ? args[0].ToLowerInvariant() : "sell";
            var info = new ItemExchangeInfo { id = pid };
            if (kind == "buy")
            {
                var stock = new List<ItemStack>();
                for (int i = 1; i + 1 < args.Count; i += 2)
                    if (nameToId.TryGetValue(args[i], out var id) && int.TryParse(args[i + 1], out var n) && n > 0)
                        stock.Add(new ItemStack { id = id, count = n, slotIdx = (byte)stock.Count });
                    else say(pid, $"unknown item '{args[i]}' (try /ls items <part of name>)");
                if (stock.Count == 0)   // something that surely exists: the first few mapped ids above the block range
                    foreach (var kv in nameToId.Where(kv => kv.Value >= 4000).OrderBy(kv => kv.Value).Take(4))
                        stock.Add(new ItemStack { id = kv.Value, count = 5, slotIdx = (byte)stock.Count });
                info.title = "«LS» Kestrel — for sale";
                info.desc = $"Take what you want. {BuyPrice} cr per item, charged for what's missing when you press Buy.";
                info.buttonText = "Buy";
                info.items = stock.ToArray();
                pending[pid] = new Pending { Kind = "buy", Offered = stock };
            }
            else
            {
                info.title = "«LS» Kestrel — buying";
                info.desc = $"Put in what you want to sell. {SellPrice} cr per item.";
                info.buttonText = "Sell";
                info.items = new ItemStack[0];
                pending[pid] = new Pending { Kind = "sell", Offered = new List<ItemStack>() };
            }
            pending[pid].OpenSeq = request(CmdId.Request_Player_ItemExchange, info);
            log($"trade {kind} open for {pid} seq={pending[pid].OpenSeq}: offered {Describe(pending[pid].Offered)}");
        }

        /// <summary>Event_Player_ItemExchange: what is in the box when the player pressed the button.</summary>
        public void OnExchange(ushort seqNr, ItemExchangeInfo r, Func<CmdId, object, ushort> request)
        {
            var box = (r?.items ?? new ItemStack[0]).ToList();
            log($"exchange event seq={seqNr} from {r?.id}: items {Describe(box)}");
            if (r == null || !pending.TryGetValue(r.id, out var p)) { log("  (no pending trade for that player)"); return; }
            // Both events carry the open request's seq: the first is the window-opened ack, the second the button press.
            if (!p.Acked) { p.Acked = true; log("  (window-opened ack, ignored)"); return; }
            if (p.Checking) { log("  (balance check still running, press ignored)"); return; }
            pending.Remove(r.id);
            int credits;
            if (p.Kind == "ship")
            {   // two steps: Quote (window re-opens with the same items + the offer) → Accept with the box unchanged = settle
                var d = p.Deal;
                // by item + its stack data: two tokens with different ids are different goods
                string Key(ItemStack x) => Core.Sim.World.StackKey(Name(x.id), x.ammo, x.decay);
                var offered = p.Offered.GroupBy(Key).ToDictionary(g => g.Key, g => g.Sum(o => o.count), StringComparer.Ordinal);
                var inBox = box.GroupBy(Key).ToDictionary(g => g.Key, g => g.Sum(i => i.count), StringComparer.Ordinal);
                var idOf = box.Concat(p.Offered).GroupBy(Key).ToDictionary(g => g.Key, g => g.First().id, StringComparer.Ordinal);
                double pay = 0, spent = 0, cost = 0;
                var bought = new List<string>(); var sold = new List<string>(); var refusedTxt = new List<string>();
                var refused = new List<KeyValuePair<int, int>>();
                var taken = new Dictionary<string, int>();
                // you buy first (that money is its to spend), then it buys from you until its purse is empty
                foreach (var kv in offered)
                {
                    var name = kv.Key;
                    var line = d.Stock.FirstOrDefault(x => x.Item == name);
                    var left = inBox.TryGetValue(kv.Key, out var l) ? l : 0;
                    if (left < kv.Value) { var n = kv.Value - left; cost += n * (line?.Price ?? 0); taken[name] = n; bought.Add($"{Core.Sim.World.Humanize(name)} {n}"); }
                }
                pay = -cost;
                var purse = d.Cash + cost;
                var wantLeft = d.WantLeft; var wanted = 0;
                void Offer(string key, int n)
                {
                    var id = idOf[key];
                    var name = Core.Sim.World.KeyBase(key, out _, out _);   // what it buys goes by the item, whatever its data
                    if (name == d.WantItem && wantLeft > 0)
                    {   // what it asked for: the premium price, up to how many it wants (the rest at the usual price below)
                        var w = (int)Math.Min(Math.Min(n, wantLeft), Math.Floor((purse - spent) / Math.Max(1, d.WantPrice)));
                        if (w > 0) { spent += w * d.WantPrice; pay += w * d.WantPrice; wantLeft -= w; wanted += w; n -= w; sold.Add($"{Core.Sim.World.Humanize(name)} {w} — just what we needed ({w * d.WantPrice:N0} cr)"); }
                        if (n <= 0) return;
                    }
                    var at = d.BuysAt?.Invoke(name) ?? 0;
                    var can = at > 0 ? (int)Math.Min(n, Math.Floor((purse - spent) / at)) : 0;
                    if (can > 0) { spent += can * at; pay += can * at; sold.Add($"{Core.Sim.World.Humanize(name)} {can} ({can * at:N0} cr)"); }
                    if (n - can > 0) { refused.Add(new KeyValuePair<int, int>(id, n - can)); refusedTxt.Add($"{Core.Sim.World.Humanize(name)} {n - can} ({(at > 0 ? "out of credits" : "not buying")})"); }
                }
                foreach (var kv in offered) { var left = inBox.TryGetValue(kv.Key, out var l) ? l : 0; if (left > kv.Value) Offer(kv.Key, left - kv.Value); }
                foreach (var kv in inBox.Where(kv => !offered.ContainsKey(kv.Key))) Offer(kv.Key, kv.Value);
                credits = (int)Math.Round(pay);
                if (credits < 0 && balance != null && !p.Funded)
                {   // you owe it: check the balance first (the items are already in your inventory, so the deal can't just fail)
                    var owed = -credits; var pid = r.id;
                    p.Checking = true;
                    balance(pid, have =>
                    {
                        p.Checking = false;
                        if (have >= owed) { p.Funded = true; pending[pid] = p; OnExchange(seqNr, r, request); return; }
                        // short: never re-open the window from here — closing it reports an empty box (the goods are in your
                        // inventory already), and re-opening on every close was an endless loop the player couldn't get out of.
                        // You pay what you have; the trader lets the rest slide this once.
                        pending.Remove(pid);   // the deal is over: later window events are ignored
                        var paid = Math.Max(0, (int)Math.Floor(have));
                        if (paid > 0) request(CmdId.Request_Player_AddCredits, new IdCredits { id = pid, credits = -paid });
                        say(pid, $"{d.Ship}: you're {owed - paid:N0} cr short — we'll take the {paid:N0} you've got and call it square. Don't make a habit of it.");
                        try { d.Done?.Invoke(taken, paid); } catch (Exception ex) { log("trade: updating the trader after a short deal threw: " + ex.Message); }
                        log($"trade: {pid} short {owed - paid} cr on {d.Ship}: paid {paid}, no re-open");
                        AfterTrade?.Invoke(pid, $"You were {owed - paid:N0} cr short — we took the {paid:N0} you had and called it square.");
                    });
                    p.Acked = true; pending[r.id] = p;   // held until the balance comes back (a second press meanwhile is ignored)
                    return;
                }
                foreach (var x in refused) GiveBack(r.id, x.Key, x.Value, request, "returned");
                var parts = new List<string>();
                if (bought.Count > 0) parts.Add(d.TakeVerb + " " + string.Join(", ", bought));
                if (sold.Count > 0) parts.Add("sold " + string.Join(", ", sold));
                var outcome = parts.Count == 0 ? "No deal." : $"{char.ToUpper(string.Join("; ", parts)[0])}{string.Join("; ", parts).Substring(1)} — {(credits >= 0 ? "+" : "")}{credits:N0} cr.";
                say(r.id, $"{d.Ship}: {outcome}");
                AfterTrade?.Invoke(r.id, outcome);
                if (credits != 0) request(CmdId.Request_Player_AddCredits, new IdCredits { id = r.id, credits = credits });   // money first: the deal is done
                try { d.Done?.Invoke(taken, -credits); if (wanted > 0) d.Fulfilled?.Invoke(wanted); }
                catch (Exception ex) { log("trade: updating the trader after the deal threw: " + ex.Message); }
                return;
            }
            if (p.Kind == "sell")
            {
                var n = box.Sum(i => i.count);
                credits = n * SellPrice;
                say(r.id, $"sold {n} item(s) for {credits} cr");
            }
            else
            {
                var left = box.GroupBy(i => i.id).ToDictionary(g => g.Key, g => g.Sum(i => i.count));
                var taken = p.Offered.Sum(o => Math.Max(0, o.count - (left.TryGetValue(o.id, out var l) ? l : 0)));
                credits = -taken * BuyPrice;
                say(r.id, $"bought {taken} item(s) for {-credits} cr");
            }
            if (credits != 0) request(CmdId.Request_Player_AddCredits, new IdCredits { id = r.id, credits = credits });
        }

        // Items handed back the moment the window closes get lost (the client's inventory sync overwrites them): wait 3 s.
        // Saved to <save>/Mods/LivingSystems/handbacks.txt: a reload / restart doesn't eat what a player is owed.
        sealed class Later { public double At; public int Pid, Item, Count; }
        readonly List<Later> later = new List<Later>();
        readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();
        string laterPath;
        bool laterLoaded;

        string LaterPath()
        {
            if (laterPath == null)
                try { laterPath = System.IO.Path.Combine(api.Application.GetPathFor(AppFolder.SaveGame), "Mods", "LivingSystems", "handbacks.txt"); }
                catch { laterPath = ""; }
            return laterPath;
        }

        void LoadLater()
        {
            if (laterLoaded) return;
            laterLoaded = true;
            var path = LaterPath();
            if (path.Length == 0 || !System.IO.File.Exists(path)) return;
            try
            {
                foreach (var line in System.IO.File.ReadAllLines(path))
                {
                    var f = line.Split(';');
                    if (f.Length == 3 && int.TryParse(f[0], out var pid) && int.TryParse(f[1], out var item) && int.TryParse(f[2], out var n) && n > 0)
                        later.Add(new Later { At = 0, Pid = pid, Item = item, Count = n });
                }
                if (later.Count > 0) log($"trade: {later.Count} hand-back(s) still owed, from '{path}'");
            }
            catch (Exception e) { log("trade: reading hand-backs failed: " + e.Message); }
        }

        void SaveLater()
        {
            var path = LaterPath();
            if (path.Length == 0) return;
            try
            {
                System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
                System.IO.File.WriteAllLines(path, later.Select(x => $"{x.Pid};{x.Item};{x.Count}"));
            }
            catch (Exception e) { log("trade: saving hand-backs failed: " + e.Message); }
        }

        void GiveBack(int pid, int item, int count, Func<CmdId, object, ushort> request, string why)
        {
            LoadLater();
            later.Add(new Later { At = clock.Elapsed.TotalSeconds + 3, Pid = pid, Item = item, Count = count });
            SaveLater();
            say(pid, $"({why}: {Core.Sim.World.Humanize(Name(item))} {count} — handing it back in a few seconds)");
        }

        /// <summary>Called every frame on the Dedi: delayed hand-backs.</summary>
        public void Pump(Func<CmdId, object, ushort> request)
        {
            LoadLater();
            if (later.Count == 0) return;
            var now = clock.Elapsed.TotalSeconds;
            var done = false;
            foreach (var x in later.Where(x => x.At <= now).ToList())
            {
                if (api.Application.GetPlayerDataFor(x.Pid)?.IsOnline != true) continue;   // logged off: kept until they're back (items to an offline player are lost)
                later.Remove(x); done = true;
                request(CmdId.Request_Player_AddItem, new IdItemStack { id = x.Pid, itemStack = new ItemStack { id = x.Item, count = x.Count } });
                log($"trade: handed back {Name(x.Item)}x{x.Count} to {x.Pid}");
            }
            if (done) SaveLater();
        }

        string Describe(IEnumerable<ItemStack> items) =>
            items == null || !items.Any() ? "nothing" : string.Join(", ", items.Select(i => $"{Name(i.id)}x{i.count}"));
    }
}
