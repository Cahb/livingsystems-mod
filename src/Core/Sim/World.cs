using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using LivingSystems.Core.Flight;
using LivingSystems.Core.Registry;

namespace LivingSystems.Core.Sim
{
    /// <summary>
    /// One NPC ship in the offline sim (IDEAS "World simulation"). No positions, only timestamps: each state ends at
    /// <see cref="Until"/> and the next one is scheduled from there, so a late tick replays exactly what would have happened.
    ///
    /// Trader: dock → out (to its warp point) → warp → in (to the station) → dock …
    /// Pirate: lurk → warp → lurk elsewhere …; badly hurt → repair → lurk.
    /// Either: fight (interrupts a leg, then resumes it) · dead.
    /// </summary>
    public sealed class SimAgent
    {
        public string Id = "";
        /// <summary>The live structure this agent is / last was (0 = never materialized). Re-keyed on respawn.</summary>
        public int EntityId;
        public string Kind = "trader";
        public string Name = "";
        public string System = "";
        public string State = "dock";
        public double Until;
        /// <summary>Trader: system it's bound for; pirate: system it's warping to.</summary>
        public string Dest = "";
        /// <summary>Trader: the system it came from (its "in" lane points back there).</summary>
        public string From = "";
        /// <summary>Trader: the station (entity id) it docks at / departs from in <see cref="System"/>; 0 = not picked yet.</summary>
        public int StationId;
        /// <summary>0…1. Below 1 = damaged ("limping 40 %").</summary>
        public double Hull = 1.0;
        /// <summary>Guns (class, size, turrets). 1 = a light armed freighter / a T1 corvette; 0 = unarmed — it can only run.</summary>
        public double Strength = 1.0;
        public float Speed = 40f;
        /// <summary>The engagement (<see cref="SimFight"/>) it's in, while State == "fight".</summary>
        public string Fight = "";
        /// <summary>Leg: the pirates already met on this leg (';'-joined), so a pirate hits a leg once.</summary>
        public string Met = "";
        /// <summary>When the current state (leg, lurk) started or resumed — passes before it don't count.</summary>
        public double Since;
        /// <summary>Pirate: its ambush spot — on the lane (station <see cref="SpotStation"/> ↔ <see cref="SpotOther"/>), this
        /// far along it (0 = station approach point, 1 = warp point). 0 station = not picked yet.</summary>
        public int SpotStation;
        public string SpotOther = "";
        public double SpotFrac;
        /// <summary>Fight: the state to go back to and the seconds that were left of it.</summary>
        public string After = "";
        public double Resume;
        /// <summary>Playfield it is materialized in right now ("" = only simulated). While live, the game owns it: the sim
        /// neither advances nor fights it (its timer keeps running; it catches up when it goes back to the sim).</summary>
        public string Live = "";
        /// <summary>Already shown live on this leg (left the player's bubble) — don't pop it back in until the next leg.</summary>
        public bool Shown;
        /// <summary>Trader: its line of work (<see cref="World.Roles"/>) and what's in the hold this leg ("" / 0 = empty).</summary>
        public string Role = "", Cargo = "";
        public int CargoAmt;
        /// <summary>Pirate: what it took from its kills (several kinds; merged per item), and when it all gets fenced (gone)
        /// unless it gets home to the hub's stash first.</summary>
        public List<SimStash> Hold = new List<SimStash>();
        public double LootUntil;
        /// <summary>The biggest load aboard (what a live pirate's boxes and the transponder show) and everything together.</summary>
        public string Loot => Hold.OrderByDescending(x => x.Amt).FirstOrDefault()?.Item ?? "";
        public int LootMainAmt => Hold.OrderByDescending(x => x.Amt).FirstOrDefault()?.Amt ?? 0;
        public int LootAmt => Hold.Sum(x => x.Amt);
        public void AddLoot(string item, int amt)
        {
            if (string.IsNullOrEmpty(item) || amt <= 0) return;
            var h = Hold.FirstOrDefault(x => x.Item == item);
            if (h == null) Hold.Add(new SimStash { Item = item, Amt = amt }); else h.Amt += amt;
        }
        /// <summary>Pirate: its home hub (<see cref="SimHub"/> id) — where it chills, repairs and fences between raids. "" = a drifter.</summary>
        public string Home = "";
        /// <summary>Its hull (Eden prefab), picked once and kept — the same ship every time it spawns ("" = not picked yet).</summary>
        public string Ship = "";
        public SortedDictionary<string, string> Extra = new SortedDictionary<string, string>(StringComparer.Ordinal);

        public bool IsTrader => Kind == "trader";
        public bool Alive => State != "dead";
        public bool OnLeg => State == "out" || State == "in";
        public bool IsLive => Live.Length > 0;

        static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        public string ToLine()
        {
            var kv = new SortedDictionary<string, string>(Extra, StringComparer.Ordinal)
            {
                ["id"] = Id, ["kind"] = Kind, ["sys"] = System, ["state"] = State, ["until"] = D(Until),
                ["hull"] = D(Hull), ["str"] = D(Strength), ["speed"] = Speed.ToString("R", Inv),
            };
            if (EntityId != 0) kv["entity"] = EntityId.ToString(Inv);
            if (Name.Length > 0) kv["name"] = Name;
            if (Dest.Length > 0) kv["dest"] = Dest;
            if (From.Length > 0) kv["from"] = From;
            if (StationId != 0) kv["stn"] = StationId.ToString(Inv);
            if (Fight.Length > 0) kv["fight"] = Fight;
            if (Since != 0) kv["since"] = D(Since);
            if (SpotStation != 0) { kv["sst"] = SpotStation.ToString(Inv); kv["sot"] = SpotOther; kv["sfr"] = D(SpotFrac); }
            if (Role.Length > 0) kv["role"] = Role;
            if (CargoAmt > 0) { kv["cargo"] = Cargo; kv["camt"] = CargoAmt.ToString(Inv); }
            if (LootAmt > 0) { kv["loot"] = string.Join(";", Hold.Select(x => x.Item)); kv["lamt"] = string.Join(";", Hold.Select(x => x.Amt.ToString(Inv))); kv["luntil"] = D(LootUntil); }
            if (Met.Length > 0) kv["met"] = Met;
            if (After.Length > 0) { kv["after"] = After; kv["resume"] = D(Resume); }
            if (Live.Length > 0) kv["live"] = Live;
            if (Shown) kv["shown"] = "1";
            if (Home.Length > 0) kv["home"] = Home;
            if (Ship.Length > 0) kv["ship"] = Ship;
            return "agent " + string.Join(" ", kv.Select(p => p.Key + "=" + ShipRecord.Esc(p.Value)));
        }

        public static SimAgent FromLine(string line)
        {
            var kv = World.Fields(line, "agent ");
            if (kv == null || !kv.TryGetValue("id", out var id) || id.Length == 0) return null;
            string T(string k) { if (!kv.TryGetValue(k, out var v)) return ""; kv.Remove(k); return v; }
            double N(string k, double d) => double.TryParse(T(k), NumberStyles.Float, Inv, out var v) && !double.IsNaN(v) ? v : d;
            var a = new SimAgent { Id = T("id") };
            int.TryParse(T("entity"), NumberStyles.Integer, Inv, out a.EntityId);
            var kind = T("kind"); if (kind.Length > 0) a.Kind = kind;
            a.Live = T("live"); a.Shown = T("shown") == "1"; a.Home = T("home"); a.Ship = T("ship");
            int.TryParse(T("stn"), NumberStyles.Integer, Inv, out a.StationId);
            a.Name = T("name"); a.System = T("sys"); a.Dest = T("dest"); a.From = T("from"); a.Fight = T("fight"); T("foe"); a.Met = T("met");
            int.TryParse(T("sst"), NumberStyles.Integer, Inv, out a.SpotStation);
            a.Role = T("role"); a.Cargo = T("cargo"); int.TryParse(T("camt"), NumberStyles.Integer, Inv, out a.CargoAmt);
            var li = T("loot").Split(';'); var la = T("lamt").Split(';');
            for (var i = 0; i < li.Length && i < la.Length; i++) if (int.TryParse(la[i], NumberStyles.Integer, Inv, out var n)) a.AddLoot(li[i], n);
            a.LootUntil = N("luntil", 0); a.SpotOther = T("sot"); a.After = T("after");
            var st = T("state"); if (st.Length > 0) a.State = st;
            a.Until = N("until", 0); a.Hull = N("hull", 1); a.Strength = N("str", 1); a.Speed = (float)N("speed", 40); a.Resume = N("resume", 0); a.Since = N("since", 0); a.SpotFrac = N("sfr", 0);
            foreach (var p in kv) a.Extra[p.Key] = p.Value;
            return a;
        }

        static string D(double v) => v.ToString("R", Inv);
    }

    /// <summary>Something that happened in the sim. Kind: undock, jump, arrive, attack, escaped, limping, destroyed, pirate-hurt.</summary>
    public sealed class SimEvent
    {
        public double Time;
        public string Agent = "", System = "", Kind = "", Text = "";
        /// <summary>Who, when there's no agent to look up (a towed wreck).</summary>
        public string Name = "";
        /// <summary>Attack reports: the lane it happened on ("stationId>other system"), so traders can steer clear of it.</summary>
        public string Lane = "";
        public override string ToString() => Text;
    }

    /// <summary>A real station in a system: the anchor traffic lanes start from (reported by the PfServer on load).</summary>
    public sealed class SimStation
    {
        public int Id;
        public string Name = "";
        public Vec3 Pos;
        /// <summary>Measured bounding box (a PfServer near the station reports it): centre, half size, rotation. Berths
        /// sit just outside it; without it lanes keep the safe 1 km clearance.</summary>
        public bool HasBox;
        public Vec3 Box, Half;
        public Quat Rot = Quat.Identity;
    }

    /// <summary>A trader's line of work: what it hauls (Eden2 item names), how much, how often it flies loaded.</summary>
    public sealed class SimRole
    {
        public string Id = "", Label = "";
        public string[] Items = new string[0];
        /// <summary>A load is rolled by VALUE (Eden2 MarketPrice, <see cref="World.Prices"/>), so a hold of cheap ore and one
        /// of fusion cells are worth about the same. Target: a wreck ≈ 40 % of a good Eden POI wreck
        /// (p75 ≈ 66k cr) → ~8–26k.</summary>
        public double ValueMin = 8000, ValueMax = 26000;
        /// <summary>A rare jackpot item instead of the usual load (couriers: one Polaris trade package, 100k).</summary>
        public string Rare = ""; public double RareChance;
        public double Loaded;
        /// <summary>Cruise speed range, m/s (sim legs AND live flight): bulk haulers lumber, couriers run.</summary>
        public float SpeedMin = 30, SpeedMax = 45;
        public float Speed(Random rng) => SpeedMin + (float)rng.NextDouble() * (SpeedMax - SpeedMin);
    }

    /// <summary>
    /// A pirate hub: a hideout in a playfield without stations (a pirate gas-giant orbit, an asteroid field, a quiet orbit)
    /// where its pirates chill, repair and fence between raids. Permanent: rolled once per star when the
    /// star is first near a player, never recycled — come back and the same crews are at the same spot.
    /// </summary>
    public sealed class SimHub
    {
        public string Id = "", System = "", Name = "";
        public Vec3 Pos;
        public double Since;
        /// <summary>How many pirates call it home (3–5): <see cref="SimConfig.HubGuards"/> stay to guard it, the rest roam.</summary>
        public int Size = 3;
        /// <summary>The Eden pirate base it moved next to (entity id; 0 = none yet). Anchored once: coring or destroying that
        /// base later doesn't make the hub jump to the next one — the gang stays put.</summary>
        public int Den;
        /// <summary>Crewed once already (the first crews come all at once; after that, recruits trickle in).</summary>
        public bool Manned;
        /// <summary>When the next recruit may turn up (one at a time, <see cref="SimConfig.RecruitMin"/>–Max apart).</summary>
        public double NextRecruit;
        /// <summary>What the whole crew remembers ("memory.killed.&lt;player&gt;" = the crewmate they killed) — every pirate
        /// living here carries it, recruits included.</summary>
        public SortedDictionary<string, string> Memory = new SortedDictionary<string, string>(StringComparer.Ordinal);
        /// <summary>Loot the crews brought home, oldest first, not yet in a real box (<see cref="World.Stash"/>): flushed
        /// into the hub wrecks' boxes while the hub is live; over <see cref="SimConfig.StashCap"/> the oldest is fenced.</summary>
        public List<SimStash> Stash = new List<SimStash>();
        /// <summary>Credits from fenced loot, minus crew upkeep (<see cref="SimConfig.HubWealth"/>): grows the gang; paid
        /// out into its crates as MoneyCard when the last crew dies.</summary>
        public double Cash;
        /// <summary>Overrun / broken: until then no recruits, and its crews out raiding can't come home (no stash, no
        /// fence into it) — they roam like drifters; at the end the survivors are the new gang's first crews.</summary>
        public double BrokenUntil;
        /// <summary>Hub splitting: since when it has sat at full size (0 = not), and its last split.</summary>
        public double RichSince, LastSplit;
    }

    /// <summary>One load a crew brought home to its hub.</summary>
    public sealed class SimStash
    {
        public string Item = "";
        public int Amt;
        public double Time;
    }

    /// <summary>
    /// A wreck: a trader (or pirate) that died in the sim near players, with what was left in its hold. A distress beacon
    /// for a while; spawned for real (coreless hull + cargo) when a player comes near; "towed" when it runs out.
    /// </summary>
    public sealed class SimWreck
    {
        public string Id = "", System = "", Name = "", Kind = "trader", Role = "", Cargo = "";
        public int Amt;
        public Vec3 Pos;
        public double Time, Until;
        /// <summary>The spawned hull (0 = only a beacon so far).</summary>
        public int Entity;
        /// <summary>The dead ship's hull (picks the wreck's size) and whether pirates picked it over (wreckage, not a whole hulk).</summary>
        public string Ship = "";
        public bool Looted;
        /// <summary>The crew that won the fight (its hub id): the beacon runs out unclaimed → they haul the wreck home.</summary>
        public string By = "";
        /// <summary>Hauled to this hub ("" = a distress beacon where it died). Hub wrecks have no beacon and never expire.</summary>
        public string Hub = "";
        /// <summary>Hub wreck over the cap: should stand as stripped debris (contents gone), not a lootable hull.</summary>
        public bool Debris;
        /// <summary>Over the debris cap: the record goes as soon as its hull is gone.</summary>
        public bool Gone;
        /// <summary>What the spawned hull is ("wreck" / "debris"; "" = not spawned): differs from what it should be → swap it.</summary>
        public string Shown = "";
        /// <summary>Credits aboard (a pirate's pocket, what's left of a trader's purse): into a crate as MoneyCard.</summary>
        public int Cash;
        /// <summary>The hub's centrepiece (its listening post): always there, never capped / turned to debris. It has no
        /// crates (seen in game): the stash and strongbox go into the hauled wrecks, as before.</summary>
        public bool Centre;
        /// <summary>Scenery round a hub (small Eden space junk): empty, debris for the caps, never lootable.</summary>
        public bool Decor;
        public bool IsHub => Hub.Length > 0;
        /// <summary>The spawned hull no longer matches the record (made debris, or over the cap): take it down while
        /// nobody's near, then it respawns as what it should be (or the record goes).</summary>
        public bool NeedsSwap => IsHub && Entity > 0 && (Gone || Shown != (Debris ? "debris" : "wreck"));
    }

    /// <summary>
    /// An engagement: any number of traders against any number of pirates at one place, fought in rounds. Ships join
    /// (a pirate lurking nearby, a trader whose lane runs through it) and leave (escape, break off, die) between rounds.
    /// It goes live as a whole when a player comes near (<see cref="Live"/>); the game fights it then, the sim waits.
    /// </summary>
    /// <summary>
    /// A bounty a player took on a pirate (hail → Jobs → Bounty?). Done when the pirate dies — anyone's kill, live or in
    /// the sim; paid by any trader the player hails afterwards. States: open → done → paid, or expired / lapsed (gone).
    /// </summary>
    public sealed class SimBounty
    {
        public string Id = "", Pirate = "", PirateName = "", GiverName = "", Player = "", Where = "", State = "open";
        /// <summary>The pot (shared by everyone hunting the same pirate) and this hunter's share once he's dead.</summary>
        public int Reward, Payout;
        /// <summary>How many hunters split the pot when he died.</summary>
        public int Hunters = 1;
        public double Since, Until, Changed;
        public bool Told;   // the player has been told about its end (done / expired / lapsed)
        /// <summary>A note still to tell this hunter (e.g. someone joined the hunt) — sent when they're online, then cleared.</summary>
        public string Note = "";
    }

    public sealed class SimFight
    {
        public string Id = "", System = "", Live = "";
        public Vec3 Pos;
        /// <summary>False in a system with no known stations (paper fight, never shown live).</summary>
        public bool HasPos;
        public double Start, Next;
        public List<string> Members = new List<string>();
        public bool IsLive => Live.Length > 0;
    }

    /// <summary>
    /// Where a sim ship is this leg: it moves from <see cref="A"/> (at <see cref="T0"/>) to <see cref="B"/> (at
    /// <see cref="T1"/>) in a straight line; A == B and T0 == T1 for a ship that's sitting still (docked, fighting).
    /// </summary>
    public struct SimTrack
    {
        public Vec3 A, B;
        public double T0, T1;
        public SimStation Station;
        /// <summary>Trader: where its leg ends (warp point / station approach) — also while held up in a fight.</summary>
        public Vec3 Dest;
        public Vec3 At(double now)
        {
            if (T1 <= T0) return B;
            var f = (float)Math.Max(0, Math.Min(1, (now - T0) / (T1 - T0)));
            return A + (B - A) * f;
        }
    }

    public sealed class SimConfig
    {
        /// <summary>Playfields the lanes run between. v1: any to any (no galaxy graph yet).</summary>
        public List<string> Systems = new List<string>();
        /// <summary>Station → warp point, metres. Leg time = this ÷ the ship's speed (slow haulers take longer).</summary>
        public float LegMetres = 6000f;
        /// <summary>Lanes start this far out from the station's centre (undock / docking approach point) — when its box isn't
        /// measured yet. Measured: the berth is <see cref="BerthMargin"/> outside the box, slots <see cref="BerthSpacing"/> apart.</summary>
        public float StationClear = 1000f, BerthMargin = 80f, BerthSpacing = 60f;
        public double WarpSeconds = 60, DockMin = 300, DockMax = 900, LurkMin = 1200, LurkMax = 2400, RepairSeconds = 1800;
        /// <summary>Warp: reachable stars within this many light years (or through a jump gate pair, +GateLy).</summary>
        /// <summary>33 LY = the basic player warp drive; an advanced drive does ~66.</summary>
        public double WarpRangeLy = 33, GateLy = 10;
        /// <summary>Warp time = base per hop + per light year (same star: one base).</summary>
        public double WarpBaseSeconds = 30, WarpPerLy = 2;
        /// <summary>
        /// Protection without players: in a safe system (big stations, a trading hub — <see cref="World.Safety"/>) a patrol
        /// shows up in a fight round with this chance × safety, mauls the pirates, and sometimes kills one (× PatrolKill).
        /// A pirate whose lurk ends in a safe system is chased off with LurkChase × safety (sometimes killed).
        /// </summary>
        public double PatrolChance = 0.12, PatrolKill = 0.25, LurkChase = 0.35;
        /// <summary>The players' neighbourhood: systems within this many LY of a player's star are kept populated.</summary>
        public double FocusLy = 150;
        /// <summary>Near players: traders per system (more at hubs) and pirates per system.</summary>
        public double TradersPerSystem = 1.5, PiratesPerSystem = 0.3;   // 0.5 pirates: 1 trip in 4 ambushed — too many
        /// <summary>Whole galaxy: beyond these the agents furthest from any player are retired (the sim moves with players).</summary>
        public int TraderBudget = 70, PirateBudget = 25;
        /// <summary>Hull a trader gets back per docking (partly: damage stays visible for a stop or two).</summary>
        public double DockRepair = LiveRules.DockRepair;
        /// <summary>Wrecks: beacon lifetime, at most this many per system, only near players.</summary>
        public double WreckMin = 1200, WreckMax = 1800;
        public int WrecksPerSystem = 2;
        /// <summary>Pirate hubs: chance a star gets one (a star with an Eden pirate orbit always does); time at home between raids;
        /// chance a pirate heads home after a lurk.</summary>
        public double HubChance = 0.4, HomeMin = 1800, HomeMax = 5400, GoHome = 0.5;
        /// <summary>Stars this close to a player get their hub roll (a couple of hops: discovery, not the whole cluster at once).</summary>
        public double HubDiscoverLy = 60;
        /// <summary>Crews that always stay home on guard (never fewer than one roamer); a lost crew is replaced by one
        /// recruit every 6–12 h — killing pirates weakens a hub for real.</summary>
        public int HubGuards = 2;
        public double RecruitMin = 6 * 3600, RecruitMax = 12 * 3600;
        /// <summary>Chance a kill leaves a hull worth a beacon; otherwise it's blown to debris (a wreck per death piles up
        /// dozens at a hot system).</summary>
        public double WreckChance = 0.6;
        /// <summary>Per hub: hauled wrecks kept lootable (older ones turn to debris), debris kept (older ones removed), and
        /// the most loot (market value, cr) waiting in the stash before the oldest is fenced.</summary>
        public int HubWrecks = 6, HubDebris = 10;
        public double StashCap = 60000;
        /// <summary>Pirates take this share of a kill's cargo (if they weren't driven off), and fence it after this long.</summary>
        public double LootMin = 0.5, LootMax = 0.8, FenceSeconds = 3 * 3600;
        /// <summary>A destroyed trader is replaced by a new one (same slot, new ship) this long after.</summary>
        public double ReplaceSeconds = 1800;
        /// <summary>Fights are resolved in rounds this long.</summary>
        public double RoundSeconds = 60;   // long enough for a player to fly over before it's decided
        /// <summary>Hull a side's power knocks off the other side per round (spread over its ships), and how well a
        /// trader shoots back compared to a pirate of the same strength.</summary>
        public double RoundHit = 0.35, TraderFire = 0.85;
        /// <summary>Per round: a trader's chance to break away (plus more when hurt); a pirate breaks off below this hull.</summary>
        public double EscapeChance = 0.12, EscapeHurt = 0.05, PirateBreak = 0.4;
        /// <summary>Per round: an unarmed trader's chance to get away (it doesn't fight, it only runs).</summary>
        public double FleeChance = 0.45;   // target: ~35 % of caught haulers die (0.3 gave ~50 %)
        /// <summary>Traders decide each round (<see cref="World"/>.StaysToFight): an armed one stays only while it's winning and
        /// above <see cref="BailHull"/>; everyone else runs, with odds scaled by speed vs <see cref="FleeRefSpeed"/> (m/s).
        /// Off = the old fixed rolls (unarmed FleeChance, armed EscapeChance: armed haulers fought to the death).</summary>
        public bool SmartFlee = true;
        /// <summary>Escorts (trial, sim only — no live ship yet): a trader leaving a station gets a cop along with
        /// EscortChance × the station system's safety. A pirate that runs into an escorted trader lets it pass with
        /// EscortDeter (per pack: everyone joining counts, packs aren't deterred); in a fight the escort shoots
        /// (EscortStrength) and draws its share of fire, breaks off below EscortBreak hull; the trader runs as usual.</summary>
        public double EscortChance = 0, EscortDeter = 0.6, EscortStrength = 1.5, EscortBreak = 0.3;
        public double BailHull = 0.35, FleeRefSpeed = 75;   // ≈ the role-weighted mean: unarmed odds average the old 0.45
        /// <summary>Share of new traders that carry guns. 0 while every sim trader spawns as an unarmed freighter prefab
        /// (sim and live must agree: a hull armed only in the sim wins fights it would lose live). Raise with armed prefabs.</summary>
        public double ArmedShare = 0;
        /// <summary>Below this <see cref="SimAgent.Strength"/> a trader counts as unarmed.</summary>
        public double Unarmed = 0.3;
        /// <summary>Ambush spots sit this far along a lane (0 = station approach, 1 = warp point): out of the stations' guns.</summary>
        public double SpotMin = 0.2, SpotMax = 0.55;
        /// <summary>Pirates lurking this close to a fight join it.</summary>
        public float JoinRange = 2000f;
        /// <summary>How long an attack report keeps a lane "hot" (what traders quote as risk).</summary>
        public double ReportSeconds = 3600;
        /// <summary>
        /// Transit (empty systems feel dead otherwise): a jump can stop over
        /// in a stationless system on the way — drop in, cross <see cref="PassMetres"/>, jump on. Stopovers go where players
        /// are (within <see cref="PassDetourLy"/> of the straight route, at most one passer per <see cref="PassGapSeconds"/> per
        /// system, taken with <see cref="PassChance"/>), and now and then through a pirate hub on the way
        /// (<see cref="HubViaChance"/>), where a crew at home jumps it with <see cref="HubJumpChance"/>.
        /// </summary>
        /// <summary>Bounties: a trader offers one on the pirate that hit it this long after; a contract runs this long; per player at most.</summary>
        public double BountyOfferSeconds = 6 * 3600, BountySeconds = 12 * 3600;
        public int BountiesPerPlayer = 3;
        public float PassMetres = 8000f;   // ~3–4 min live: time to find and hail it
        public double PassChance = 0.8, PassGapSeconds = 240, PassDetourLy = 45, HubViaChance = 0.1, HubJumpChance = 0.4;
        /// <summary>Every <see cref="RerouteSeconds"/> per player system with nobody passing: with this chance a ship already
        /// in warp on a fitting route drops in there within 20–90 s (else the first minute or two after a jump is always empty).</summary>
        public double RerouteChance = 0.2, RerouteSeconds = 60;
        /// <summary>Raider cap (Space Rangers style): pirates out raiding (not at home) are counted against
        /// <see cref="RaidersPerSystem"/> × the station systems near players; above it hub crews stay home / head home after
        /// a lurk, and no system takes more than <see cref="MaxRaidersPerSystem"/>. Danger follows lanes, not hubs found.</summary>
        public bool RaiderCap = true;
        public double RaidersPerSystem = 0.25;
        public int MaxRaidersPerSystem = 2;
        /// <summary>Players dent a region (Space Rangers: spawn × (1 − kills ÷ 15)): the raider target and the drifter refill
        /// shrink by pirates killed near players, each kill fading with <see cref="KillFadeSeconds"/> (e-folding), never
        /// below <see cref="KillDampFloor"/>. Clearing a few hubs quiets a region for hours and it heals within about a day.</summary>
        public double KillDamp = 15, KillFadeSeconds = 8 * 3600, KillDampFloor = 0.2;
        /// <summary>Drifters (no hub) only top the raiders up to half the target, only in systems no hub reaches, and come
        /// back slowly: <see cref="DriftRefillPerHour"/> × the kill damping.</summary>
        public bool DriftTopUp = true;
        public double DriftRefillPerHour = 1;
        /// <summary>Sim-only legs (no player in the system): each lurker there catches a trader's leg with
        /// <see cref="AmbushPerLeg"/> × (1 − 0.7 × safety) (Freelancer's tradelane_attack) instead of the one-lane coincidence
        /// of its ambush spot — a 3-station system isn't immune, a 1-station one isn't a death trap. Live keeps the real spots.</summary>
        public bool LegAmbush = true;
        public double AmbushPerLeg = 0.35;
        /// <summary>Hub wealth: fenced loot (stash overflow, crews selling their hold) pays <see cref="FenceShare"/> of its value
        /// into the hub's cash; each crew costs <see cref="UpkeepPerCrewHour"/>; every <see cref="HubGrowStep"/> of cash adds a
        /// crew (up to <see cref="HubGrowMax"/>) and toughens recruits; the last crew dying pays the cash into its crates.</summary>
        public bool HubWealth = true;
        /// <summary><see cref="SpendPerHour"/>: share of its cash a gang blows each hour (drink, bribes, parts) — a ceiling
        /// that follows what it takes, so a den levels off instead of snowballing.</summary>
        public double FenceShare = 0.6, UpkeepPerCrewHour = 100, HubGrowStep = 100000, SpendPerHour = 0.03;
        public int HubGrowMax = 3;
        /// <summary>A broken hub stays quiet this long (± <see cref="HubQuietJitter"/>, hashed per break: no camping a timer);
        /// <see cref="BreakPayoutFraction"/> of the cash goes into the strongbox. A recruit costs <see cref="RecruitCost"/> from
        /// the cash and comes in <see cref="PaidRecruitMin"/>–Max (else free, 6–12 h) — only above <see cref="RecruitCashFloor"/>
        /// and never with a player in the hub's system (a siege drains the purse over days, a single raid still pays in full).</summary>
        public double HubQuietHours = 21, HubQuietJitter = 0.25, BreakPayoutFraction = 1.0;
        public double RecruitCost = 10000, RecruitCashFloor = 35000, PaidRecruitMin = 2 * 3600, PaidRecruitMax = 4 * 3600;
        /// <summary>Money flows (trader purses ↔ pirates ↔ hubs): a trader's purse persists, each delivery pays
        /// <see cref="TradeMargin"/> of the cargo's value into it (capped at <see cref="PurseCap"/> × its base); a lone pirate
        /// catching a weaker trader is paid off with <see cref="TollChance"/> — <see cref="TollShare"/> of the purse, at least
        /// <see cref="TollMin"/> (a broke trader can't pay: it fights); a kill grabs ~<see cref="PurseGrab"/> of the purse too. Pirates
        /// carry the cash home (banked in full) or drop it in their wreck when they die.</summary>
        public bool MoneyFlow = true;
        public double TradeMargin = 0.08, PurseCap = 2, TollChance = 0.4, TollShare = 0.1, TollMin = 500, PurseGrab = 0.3;
        /// <summary>Hub splitting: a hub at full size (top wealth step) for <see cref="SplitHoldHours"/> sends a lieutenant with
        /// <see cref="SplitCrews"/> crews and <see cref="SplitCashShare"/> of its cash to found a hub at the hideout nearest its
        /// best hunting ground (a star within <see cref="SplitRangeLy"/> with no hub; quieter first — fewer pirates killed
        /// there lately), at most every <see cref="SplitGapHours"/>, and only while hubs within 60 LY stay under one per
        /// <see cref="StarsPerHub"/> stars. Lane danger doesn't climb with it (the raider cap is per station system).</summary>
        public bool HubSplit = false;
        public double SplitHoldHours = 24, SplitGapHours = 72, SplitRangeLy = 30, StarsPerHub = 2.5, SplitCashShare = 0.5;
        public int SplitCrews = 2;
        /// <summary>Bandit (Warlord) POIs from the map: a sim-only leg (no player there) through a playfield holding them
        /// takes fire with <see cref="BanditFire"/> (hull −10–30 %, never below 5 %); a new hub keeps its hideout at least
        /// <see cref="BanditTurf"/> m from their guns.</summary>
        public double BanditFire = 0.06, BanditTurf = 3000;
        /// <summary>A hub's crews jump passers at most once per this many seconds (0 = no limit).</summary>
        public double HubAmbushGap = 3600;
    }

    /// <summary>The offline world: agents + recent events. Pure; the Dedi ticks it and stores it.</summary>
    public sealed class World
    {
        public const string Header = "LivingSystems sim v1";
        public const double Never = 1e15;

        public SimConfig Config = new SimConfig();
        public readonly Dictionary<string, SimAgent> Agents = new Dictionary<string, SimAgent>(StringComparer.Ordinal);
        public readonly List<SimEvent> Events = new List<SimEvent>();
        public double LastTick;
        const int KeepEvents = 200;
        /// <summary>Systems with a player in them (set by the Dedi before each step). Informational: fights near a player go
        /// live through <see cref="SimFight.Live"/>, the rest of the system keeps rolling.</summary>
        public HashSet<string> Occupied = new HashSet<string>(StringComparer.Ordinal);
        /// <summary>Transit: when the last passer was sent through a system (spacing; not saved — a restart just allows one).</summary>
        public readonly Dictionary<string, double> LastPass = new Dictionary<string, double>(StringComparer.Ordinal);
        readonly Dictionary<string, double> nextReroute = new Dictionary<string, double>(StringComparer.Ordinal);
        /// <summary>Pirate deaths (time, system) of the last day: drifter refill slows where players hunt. Not saved.</summary>
        readonly List<KeyValuePair<double, string>> pirateDeaths = new List<KeyValuePair<double, string>>();
        readonly Dictionary<string, double> hubAmbushAt = new Dictionary<string, double>(StringComparer.Ordinal);
        /// <summary>
        /// Lifetime counters (saved; never steer the sim — balancing, /ls stats, real-reference talk). Keys:
        /// st|system|station|in / out / docks (cargo value delivered / loaded, dockings); hub|id|taken / kills / stashed /
        /// fenced / hauled / lost / recruits; sys|system|arr / atk / kill / pkill. Values in credits at <see cref="Prices"/>.
        /// Per trader (lifetime of that ship, in Extra): g.moved (value delivered), g.trips, g.hit, g.lost (cargo value lost).
        /// </summary>
        /// <summary>Bandit sentries / bases per playfield (map data from the save's templates; rebuilt each start, not saved).</summary>
        public readonly Dictionary<string, List<SimPoi>> Bandits = new Dictionary<string, List<SimPoi>>(StringComparer.Ordinal);

        double nextSplitCheck;

        /// <summary>Hubs at full size long enough split (<see cref="SimConfig.HubSplit"/>).</summary>
        void Splits(double now, Random rng)
        {
            foreach (var h in Hubs.Values.OrderBy(x => x.Id, StringComparer.Ordinal).ToList())
            {
                if (IsBroken(h) || HubTier(h) < Config.HubGrowMax) { h.RichSince = 0; continue; }
                if (h.RichSince <= 0) { h.RichSince = now; continue; }
                if (now - h.RichSince < Config.SplitHoldHours * 3600 || (h.LastSplit > 0 && now - h.LastSplit < Config.SplitGapHours * 3600)) continue;
                Split(h, now, rng);
            }
        }

        /// <summary>A lieutenant takes crews and cash to a new hideout near the gang's best hunting ground. Null if none fits.</summary>
        public SimHub Split(SimHub mother, double now, Random rng)
        {
            if (Galaxy == null) return null;
            var pre = "hunt|" + mother.Id + "|";
            var ground = Stats.Where(kv => kv.Key.StartsWith(pre, StringComparison.Ordinal)).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal)
                              .Select(kv => kv.Key.Substring(pre.Length)).FirstOrDefault() ?? mother.System;
            var gs = Galaxy.StarOf(ground) ?? Galaxy.StarOf(mother.System);
            if (gs == null) return null;
            var hubStars = new HashSet<string>(Hubs.Values.Select(x => Galaxy.StarOf(x.System)?.Name).Where(x => x != null), StringComparer.Ordinal);
            int HubsNear(Galaxy.Star st) => Hubs.Values.Count(x => Galaxy.StarOf(x.System) is Galaxy.Star o && (o.Pos - st.Pos).Length <= 60);
            int StarsNear(Galaxy.Star st) => Galaxy.Stars.Values.Count(o => (o.Pos - st.Pos).Length <= 60);
            pirateDeaths.RemoveAll(x => x.Key < now - 4 * Config.KillFadeSeconds);
            int Heat(Galaxy.Star st) => pirateDeaths.Count(d => Galaxy.StarOf(d.Value) is Galaxy.Star o && (o.Pos - st.Pos).Length <= 15 && now - d.Key < 24 * 3600);
            var pick = Galaxy.Stars.Values.Where(st => !hubStars.Contains(st.Name) && (st.Pos - gs.Pos).Length <= Config.SplitRangeLy && HubsNear(st) < StarsNear(st) / Config.StarsPerHub)
                .Select(st => new { st, pfs = Galaxy.PlayfieldsOf(st).Where(pf => !Stations.ContainsKey(pf) && Galaxy.HideoutScore(pf) > 0).OrderByDescending(Galaxy.HideoutScore).ThenBy(pf => pf, StringComparer.Ordinal).ToList() })
                .Where(x => x.pfs.Count > 0)
                .OrderBy(x => Heat(x.st)).ThenBy(x => (x.st.Pos - gs.Pos).Length).ThenBy(x => x.st.Name, StringComparer.Ordinal).FirstOrDefault();
            if (pick == null) { mother.LastSplit = now; return null; }   // nowhere to go: try again after the gap
            var pf0 = pick.pfs[0];
            var id = "h1";
            for (var n = 1; Hubs.ContainsKey(id); n++) id = "h" + n.ToString(CultureInfo.InvariantCulture);
            var used = new HashSet<string>(Hubs.Values.Select(x => x.Name));
            var hh = Fnv("hub|" + pick.st.Name);
            var name = HubNames.Skip((int)(hh % (uint)HubNames.Length)).Concat(HubNames).FirstOrDefault(x => !used.Contains(x)) ?? "Hideout " + id;
            var child = new SimHub { Id = id, System = pf0, Name = name, Since = now, Size = 3, Manned = true, NextRecruit = now + Config.RecruitMin,
                                     Pos = LaneDir("hubpos|" + pf0) * (float)(4000 + hh % 5000) };
            KeepOffTurf(child);
            EnsureCentre(child, now);
            var share = mother.Cash * Config.SplitCashShare; mother.Cash -= share; child.Cash = share;
            Hubs[id] = child; RolledStars.Add(pick.st.Name);
            var crew = Agents.Values.Where(a => !a.IsTrader && a.Alive && !a.IsLive && a.Home == mother.Id && a.State != "fight")
                                    .OrderByDescending(a => a.State == "home" ? 1 : 0).ThenBy(a => a.Id, StringComparer.Ordinal).Take(Config.SplitCrews).ToList();
            foreach (var c in crew)
            {   // off to the new place: they arrive "home" there
                c.Home = id; c.Dest = pf0; c.State = "warp"; c.Until = now + WarpTime(c.System, pf0); c.Since = now; c.SpotStation = 0;
            }
            mother.LastSplit = now; mother.RichSince = 0;
            Count("hub|" + mother.Id + "|splits");
            var lt = crew.FirstOrDefault();
            Events.Add(new SimEvent { Time = now, Agent = lt?.Id ?? "", Name = child.Name, System = pf0, Kind = "split",
                Text = $"{(lt != null ? lt.Name : "A lieutenant")} took a crew from {mother.Name} and set up at {pf0} — they call it {child.Name}" });
            return child;
        }

        /// <summary>Scatter <paramref name="n"/> pieces of junk 60–200 m round <paramref name="at"/> (hub scenery, debris records).</summary>
        void Scatter(SimHub h, Vec3 at, string near, int n, double now)
        {
            for (var i = 0; i < n; i++)
            {
                var key = near + "|" + i.ToString(CultureInfo.InvariantCulture);
                var pos = at;
                for (var tries = 0; tries < 8; tries++)
                {
                    pos = at + LaneDir("decor|" + key + "|" + tries.ToString(CultureInfo.InvariantCulture)) * (float)(60 + Fnv("decord|" + key + "|" + tries.ToString(CultureInfo.InvariantCulture)) % 140);
                    if (Wrecks.Values.All(x => x.System != h.System || (x.Pos - pos).Length >= 50)) break;
                }
                var d = new SimWreck { Id = NewWreckId(), System = h.System, Name = "junk", Kind = "debris", Role = "", Hub = h.Id, Debris = true, Decor = true,
                                       Looted = true, Time = now, Until = Never, Pos = pos };
                Wrecks[d.Id] = d;
            }
        }

        /// <summary>Every hub has its centrepiece (made once; it moves with the hub until it first spawns). Returns it.</summary>
        public SimWreck EnsureCentre(SimHub h, double now)
        {
            var c = Wrecks.Values.FirstOrDefault(x => x.Centre && x.Hub == h.Id);
            if (c != null)
            {
                if (c.Entity == 0) c.Pos = h.Pos;
                if (!Wrecks.Values.Any(x => x.Decor && x.Hub == h.Id)) Scatter(h, c.Pos, "centre|" + h.Id, 1, now);   // hubs from before the junk
                return c;
            }
            c = new SimWreck { Id = NewWreckId(), System = h.System, Name = h.Name, Kind = "base", Role = "hub", Ship = CentrePrefab, Hub = h.Id, Centre = true,
                               Looted = true, Time = now, Until = Never, Pos = h.Pos };
            Wrecks[c.Id] = c;
            Scatter(h, h.Pos, "centre|" + h.Id, 1, now);   // a camp with a bit of junk round it from day one
            return c;
        }

        /// <summary>A hub's hideout too close to bandit guns moves off (their turf); false if nothing to do.</summary>
        public bool KeepOffTurf(SimHub h)
        {
            if (h == null || !Bandits.TryGetValue(h.System, out var guns) || guns.Count == 0) return false;
            var moved = false;
            for (var round = 0; round < 8; round++)
            {
                var near = guns.OrderBy(g => (g.Pos - h.Pos).Length).First();
                var d = h.Pos - near.Pos;
                if (d.Length >= Config.BanditTurf) break;
                var dir = d.Length < 1 ? LaneDir("turf|" + h.Id) : d.Normalized;
                h.Pos = near.Pos + dir * (float)(Config.BanditTurf + 300);
                moved = true;
            }
            return moved;
        }

        public readonly SortedDictionary<string, double> Stats = new SortedDictionary<string, double>(StringComparer.Ordinal);
        public void Count(string key, double v = 1) { if (v != 0) Stats[key] = (Stats.TryGetValue(key, out var x) ? x : 0) + v; }
        /// <summary>Money appearing from nowhere / vanishing (faucets and sinks — the economy is open on purpose; this shows
        /// whether it inflates). Transfers (tolls, kills, banking, strongbox, trades) are not counted here.</summary>
        void Mint(string source, double v) { if (v > 0) Count("eco|mint|" + source, v); }
        void Burn(string sink, double v) { if (v > 0) Count("eco|burn|" + sink, v); }
        public double Stat(string key) => Stats.TryGetValue(key, out var x) ? x : 0;
        public static double Value(string item, int amt) => amt <= 0 ? 0 : amt * (Prices.TryGetValue(item ?? "", out var p) ? p : 1);
        static void Tally(SimAgent a, string key, double v = 1)
        {
            var have = a.Extra.TryGetValue(key, out var x) && double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
            a.Extra[key] = Math.Round(have + v).ToString(CultureInfo.InvariantCulture);
        }
        /// <summary>The counters as a few short lines (chat / harness): totals, busiest stations, hubs by take, top traders.</summary>
        public List<string> StatsReport(int top = 5)
        {
            string K(double v) => v >= 1e6 ? (v / 1e6).ToString("0.0", CultureInfo.InvariantCulture) + "M" : v >= 1e3 ? (v / 1e3).ToString("0", CultureInfo.InvariantCulture) + "k" : v.ToString("0", CultureInfo.InvariantCulture);
            double Sum(string prefix, string suffix) => Stats.Where(kv => kv.Key.StartsWith(prefix, StringComparison.Ordinal) && kv.Key.EndsWith(suffix, StringComparison.Ordinal)).Sum(kv => kv.Value);
            var lines = new List<string>
            {
                $"Trade: {K(Sum("st|", "|in"))} cr delivered, {K(Sum("st|", "|out"))} cr loaded, {Sum("st|", "|docks"):0} dockings",
                $"Lanes: {Sum("sys|", "|arr"):0} arrivals, {Sum("sys|", "|atk"):0} attacks, {Sum("sys|", "|kill"):0} traders lost, {Sum("sys|", "|pkill"):0} pirates down"
                    + (Config.MoneyFlow ? $", tolls paid {K(Sum("sys|", "|toll"))} cr by traders, {K(Sum("sys|", "|ptoll"))} cr by players" : ""),
            };
            var minted = Sum("eco|mint|", ""); var burned = Sum("eco|burn|", "");
            if (minted + burned > 0)
            {
                string Parts(string pre) => string.Join(", ", Stats.Where(kv => kv.Key.StartsWith(pre, StringComparison.Ordinal) && kv.Value >= 1).OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Key.Substring(pre.Length)} {K(kv.Value)}"));
                lines.Add($"Money: minted {K(minted)} ({Parts("eco|mint|")}), burned {K(burned)} ({Parts("eco|burn|")}); net {(minted >= burned ? "+" : "−")}{K(Math.Abs(minted - burned))}");
                var held = Agents.Values.Where(a => a.Alive).Sum(a => (double)Cash0(a)) + Hubs.Values.Sum(h => h.Cash + h.Stash.Where(x => x.Item == Strongbox).Sum(x => x.Amt)) + Wrecks.Values.Sum(x => (double)x.Cash);
                lines.Add($"Held now: {K(held)} cr (purses, pockets, hub cash, strongboxes, wrecks)");
            }
            var stations = Stats.Keys.Where(k => k.StartsWith("st|", StringComparison.Ordinal)).Select(k => k.Split('|')).Where(p => p.Length == 4)
                .GroupBy(p => p[1] + "|" + p[2]).Select(g => new { g.Key, v = Stat("st|" + g.Key + "|in") + Stat("st|" + g.Key + "|out") })
                .OrderByDescending(x => x.v).Take(top).ToList();
            if (stations.Count > 0) lines.Add("Busiest: " + string.Join(", ", stations.Select(x => $"{x.Key.Split('|')[1]} ({Short(x.Key.Split('|')[0])}) {K(x.v)}")));
            foreach (var h in Hubs.Values.OrderByDescending(h => Stat("hub|" + h.Id + "|taken")).Take(top))
            {
                string H(string k) => K(Stat("hub|" + h.Id + "|" + k));
                if (Stat("hub|" + h.Id + "|taken") + Stat("hub|" + h.Id + "|lost") == 0) continue;
                lines.Add($"{h.Name}: took {H("taken")} cr, {H("kills")} kills, stashed {H("stashed")}, fenced {H("fenced")}, hauled {H("hauled")}, lost {H("lost")} crew, stash now {K(StashValue(h))}{(Config.HubWealth ? $", cash {K(h.Cash)} (tier {HubTier(h)}, {HubSize(h)} crews), paid out {H("paid")}" : "")}");
            }
            var traders = Agents.Values.Where(a => a.IsTrader && a.Alive && Tallied(a, "g.moved") > 0).OrderByDescending(a => Tallied(a, "g.moved")).Take(top).ToList();
            if (traders.Count > 0) lines.Add("Top haulers: " + string.Join(", ", traders.Select(a => $"{a.Name} {K(Tallied(a, "g.moved"))}/{Tallied(a, "g.trips"):0} trips")));
            return lines;
        }

        public static double Tallied(SimAgent a, string key) => a != null && a.Extra.TryGetValue(key, out var x) && double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;
        string StKey(SimAgent a) => "st|" + a.System + "|" + (StationOf(a)?.Name ?? "?") + "|";
        double lastPopulate = double.NaN;
        /// <summary>Network systems near players (set by the Dedi): replacements and new agents go here.</summary>
        public List<string> Focus = new List<string>();
        /// <summary>Real stations per system (anchors). Systems nobody has visited have none — and need none.</summary>
        public readonly Dictionary<string, List<SimStation>> Stations = new Dictionary<string, List<SimStation>>(StringComparer.Ordinal);
        public readonly Dictionary<string, SimFight> Fights = new Dictionary<string, SimFight>(StringComparer.Ordinal);
        public readonly Dictionary<string, SimWreck> Wrecks = new Dictionary<string, SimWreck>(StringComparer.Ordinal);
        public readonly Dictionary<string, SimHub> Hubs = new Dictionary<string, SimHub>(StringComparer.Ordinal);
        public readonly Dictionary<string, SimBounty> Bounties = new Dictionary<string, SimBounty>(StringComparer.Ordinal);
        /// <summary>Stars that already had their hub roll (yes or no) — a star is rolled once, ever.</summary>
        public readonly SortedSet<string> RolledStars = new SortedSet<string>(StringComparer.Ordinal);
        /// <summary>Players who got the one-time "how to scan and hail" tip (player keys).</summary>
        public readonly SortedSet<string> Tipped = new SortedSet<string>(StringComparer.Ordinal);

        /// <summary>Lines of work (Eden2 item names). Amounts are first guesses: tune for "worth a detour".</summary>
        // ---- hulls (PLAN "Prefab survey") ------------------------------------------------------------------------

        public sealed class SimShip
        {
            public string Prefab = "", Size = "small";
            /// <summary>Guns for the sim's fights (0 = unarmed, can only run).</summary>
            public double Strength;
            public double Weight = 1;
            public SimShip(string prefab, string size, double strength, double weight) { Prefab = prefab; Size = size; Strength = strength; Weight = weight; }
        }

        /// <summary>Trader hulls per role. A/B pairs are colour variants (two liveries for free). Armed haulers count 0.5 (a few
        /// lasers, mostly inside; at 1.0 they kill hub crews faster than recruits come).</summary>
        public static readonly Dictionary<string, SimShip[]> TraderShips = new Dictionary<string, SimShip[]>(StringComparer.Ordinal)
        {
            ["courier"] = new[] { new SimShip("CV_Freighter_SparrowA", "small", 0, 3), new SimShip("CV_Freighter_SparrowB", "small", 0, 3),
                                  new SimShip("CV_Freighter_Pharos-Small-Freighter", "small", 0.5, 1), new SimShip("CV_Freighter_Warhound-Transport", "small", 0.5, 1),
                                  new SimShip("CV_PolarisFreighterP016", "mid", 0.5, 1) },
            ["supply"] = new[] { new SimShip("CV_Freighter_SparrowA", "small", 0, 2), new SimShip("CV_Freighter_SparrowB", "small", 0, 2),
                                 new SimShip("CV_Freighter_HorizonA", "small", 0, 2), new SimShip("CV_Freighter_HorizonB", "small", 0, 2),
                                 new SimShip("CV_Freighter_Stalos-MK4", "mid", 0.5, 1), new SimShip("CV_PolarisFreighterP016", "mid", 0.5, 1) },
            ["tanker"] = new[] { new SimShip("CV_Freighter_HorizonA", "small", 0, 3), new SimShip("CV_Freighter_HorizonB", "small", 0, 3),
                                 new SimShip("CV_Freighter_Stalos-MK4", "mid", 0.5, 1), new SimShip("CV_PolarisFreighterP017", "mid", 0.5, 1) },
            ["bulk"] = new[] { new SimShip("CV_Freighter_PatronA", "mid", 0, 3), new SimShip("CV_Freighter_PatronB", "mid", 0, 3),
                               new SimShip("CV_Freighter_HarkonA", "big", 0, 1), new SimShip("CV_Freighter_HarkonB", "big", 0, 1),
                               new SimShip("CV_Freighter_HarkonC", "big", 0, 1), new SimShip("CV_Freighter_HarkonD", "big", 0, 1),
                               new SimShip("CV_Freighter_Mule-Class-Bulk-Frgt", "big", 0.3, 1), new SimShip("CV_PolarisFreighterP017", "mid", 0.5, 1),
                               new SimShip("CV_Freighter_AlbatrossA", "big", 0, 0.15) },   // the rare superfreighter
        };

        /// <summary>Pirate hulls: cheap (Eden bandit OPVs + the old T1 corvettes, unshielded) and standard (the shielded
        /// EPH_PirateOPVT1 — Eden's "Placeholder Patrol Vessel"). The Skaar Raider (31 guns) and the barge stay out.</summary>
        public static readonly SimShip[] CheapPirates = { new SimShip("Eden_OPV_BanditT1_1", "small", 1, 2), new SimShip("Eden_OPV_BanditT1_2", "small", 1, 2),
                                                          new SimShip("Eden_OPV_BanditRT1_1", "small", 1, 1), new SimShip("OPV_PirateCorvetteT1A", "small", 1, 1),
                                                          new SimShip("OPV_PirateCorvetteT1B", "small", 1, 1) };
        public static readonly SimShip[] StandardPirates = { new SimShip("EPH_PirateOPVT1", "small", 2.5, 1) };

        /// <summary>Wreck hulls: STANDALONE (the ship got destroyed, cargo mostly aboard) by size, and WRECKAGE (destroyed and
        /// picked over by pirates). Eden's own wrecked ships — no more cratering a live trader prefab.</summary>
        public static readonly string[] WrecksSmall = { "Eden_Hauler Wreck", "Eden_Starduster Wreck", "Eden_CVJ_Wreck1", "Eden_CVJ_Wreck2" };
        public static readonly string[] WrecksBig = { "CVJ_FrigateWreck", "CVJ_Pack-Mule", "Eden_CVJ_Wreck3", "Eden_CVJ_Wreck4", "CV_LightCarrierWreck" };
        public static readonly string[] Wreckage = { "CVJ_OsanWreck", "CVJ_StardusterWreck", "Eden_Scout Wreck" };
        /// <summary>Hulls that are already wrecks (no craters/breach on top — they come chewed up).</summary>
        public static bool IsPrewrecked(string prefab) => WrecksSmall.Concat(WrecksBig).Concat(Wreckage).Contains(prefab) || prefab == "Eden_Wrecked Horizon";

        static SimShip PickShip(SimShip[] pool, string seed)
        {
            var total = pool.Sum(s => s.Weight);
            var r = Fnv("ship|" + seed) % 100000 / 100000.0 * total;
            foreach (var s in pool) { r -= s.Weight; if (r < 0) return s; }
            return pool[pool.Length - 1];
        }

        public static SimShip ShipInfo(string prefab) =>
            TraderShips.Values.SelectMany(x => x).Concat(CheapPirates).Concat(StandardPirates).FirstOrDefault(s => s.Prefab == prefab);

        /// <summary>
        /// The agent's hull, picked once (by role / tier, deterministic per agent) and kept: a pirate you met flies the same
        /// ship next time. A trader's guns follow its hull (sim fights match what spawns).
        /// </summary>
        public string ShipOf(SimAgent a) => ShipOf(a, false);

        /// <param name="arm">true where a ship is made or loaded: its guns follow its hull. The tick only names hulls.</param>
        public string ShipOf(SimAgent a, bool arm)
        {
            if (a == null) return "";
            // hub crews fly the gang's ship — the shielded EPH raider, like the ones Eden parks at its smuggler bases (11k
            // shield: a fair fight). Older crews switch over once.
            if (!a.IsTrader && a.Home.Length > 0 && StandardPirates.All(x => x.Prefab != a.Ship)) a.Ship = "";
            if (a.Ship.Length == 0)
            {
                var pool = a.IsTrader ? (TraderShips.TryGetValue(a.Role, out var p) ? p : TraderShips["supply"]) : a.Strength >= 2 || a.Home.Length > 0 ? StandardPirates : CheapPirates;
                a.Ship = PickShip(pool, a.Id + "|" + a.Name).Prefab;
            }
            if (arm && a.IsTrader && ShipInfo(a.Ship) is SimShip info) a.Strength = info.Strength;
            return a.Ship;
        }

        /// <summary>Hub debris: stripped hulks piled up past the hub's lootable wrecks (decor, containers left empty).</summary>
        public static readonly string[] DebrisHulls = { "CVJ_ArgosNGWreck", "Eden_Thunder Wreck", "Eden_Planken Wreck", "Eden_Refuge Wreck", "Eden_Arashi Wreck", "Eden_Blade Wreck", "CVJ_CerberusFrontWreck", "CVJ_CerberusBackWreck" };
        /// <summary>At most this many pieces of junk per hub (on top of the demoted-wreck debris cap).</summary>
        public const int HubDecorMax = 3;   // each piece is a structure on the game's HUD / detector: keep it to a few

        /// <summary>Small Eden space junk scattered round a hub (its listening post and hauled wrecks).</summary>
        public static readonly string[] DecorPrefabs = Enumerable.Range(1, 20).Select(i => "Eden_BAOJ_DebrisT1_" + i.ToString("00", CultureInfo.InvariantCulture))
            .Concat(Enumerable.Range(1, 11).Select(i => "Eden_BAOJ_DebrisT2_" + i.ToString("00", CultureInfo.InvariantCulture))).ToArray();

        public static string DebrisPrefab(SimWreck w) => w.Decor ? DecorPrefabs[(int)(Fnv("decor|" + w.Id + "|" + w.Name) % (uint)DecorPrefabs.Length)] : DebrisHulls[(int)(Fnv("debris|" + w.Id + "|" + w.Name) % (uint)DebrisHulls.Length)];

        /// <summary>The wreck hull for a dead ship: picked over by pirates → wreckage; else a whole hulk sized like the ship.</summary>
        /// <summary>A hub's centrepiece: an Eden comms post the gang listens to traffic from (picked by the user in game).</summary>
        public const string CentrePrefab = "Eden_BAO_Antenna2";

        public static string WreckPrefab(SimWreck w)
        {
            if (w.Centre) return CentrePrefab;
            var h = Fnv("wreck|" + w.Id + "|" + w.Time.ToString("R", CultureInfo.InvariantCulture));
            if (w.Looted && w.Ship.StartsWith("CV_Freighter_Horizon", StringComparison.Ordinal)) return "Eden_Wrecked Horizon";   // the same ship, gutted
            string[] pool;
            if (w.Looted) pool = Wreckage;
            else
            {
                var size = ShipInfo(w.Ship)?.Size ?? "small";
                pool = size == "big" ? WrecksBig : size == "mid" ? WrecksSmall.Concat(WrecksBig).ToArray() : WrecksSmall;
            }
            return pool[(int)(h % (uint)pool.Length)];
        }

        /// <summary>Eden2 MarketPrice of everything a hold can carry (BlocksConfig/ItemsConfig, `.cache/poiloot/survey.py`).</summary>
        public static readonly Dictionary<string, double> Prices = new Dictionary<string, double>(StringComparer.Ordinal)
        {
            ["IronOre"] = 40, ["CopperOre"] = 60, ["SiliconOre"] = 50, ["CobaltOre"] = 80, ["NeodymiumOre"] = 120,
            ["FusionCell"] = 1250, ["EnergyCellLarge"] = 625, ["EnergyCell"] = 125, ["HydrogenBottle"] = 72, ["OxygenBottleLarge"] = 75,
            ["Computer"] = 200, ["EnergyMatrix"] = 220, ["PowerCoil"] = 2998, ["GoldIngot"] = 100, ["Eden_TradePolarisT1"] = 100000,
            ["CannedMeat"] = 354, ["CannedVegetables"] = 185, ["Fruit"] = 20, ["Medikit01"] = 1427, ["SteelPlate"] = 10,
            ["PentaxidCrystal"] = 100, ["MoneyCard"] = 1,
            // wanted goods (trader requests): Eden2 MarketPrice
            ["Beer"] = 125, ["AkuaWine"] = 85, ["Coffee"] = 156, ["Eden_SpaceVodka"] = 1282, ["Liquors"] = 162, ["Steak"] = 90,
            ["CivilianClothing"] = 200, ["WorkClothing"] = 260, ["LuxuryClothing"] = 440, ["Jewellery"] = 610, ["SpareParts"] = 1900,
        };

        /// <summary>Something a crew wants this trip, why, and how many (hail → shown; Trade → paid 2–3× base).</summary>
        public sealed class Request
        {
            public string Item = "", Why = "";
            public int Min, Max;
            public Request(string item, int min, int max, string why) { Item = item; Min = min; Max = max; Why = why; }
        }

        public static readonly Request[] Requests =
        {
            new Request("Beer", 10, 30, "The party's dry — the crew would kill for some beer."),
            new Request("AkuaWine", 5, 15, "It's the captain's birthday. Got any Akua wine?"),
            new Request("Coffee", 10, 25, "We ran out of coffee three jumps ago. Morale's in the gutter."),
            new Request("Eden_SpaceVodka", 2, 6, "Our engineer wants space vodka. Don't ask."),
            new Request("Liquors", 10, 20, "We're throwing a party and there's no booze."),
            new Request("Steak", 10, 20, "Real meat. Anything but canned."),
            new Request("CivilianClothing", 5, 15, "Half the crew is in rags. Bring clothes."),
            new Request("WorkClothing", 5, 12, "Our work gear is falling apart — got coveralls?"),
            new Request("LuxuryClothing", 2, 6, "Our passenger wants something fancy to wear. Pays well."),
            new Request("Jewellery", 1, 4, "Anniversary coming up. Need jewellery, no questions asked."),
            new Request("SpareParts", 2, 6, "The drive's held together with tape. Spare parts?"),
        };

        /// <summary>Share of trips a crew wants something.</summary>
        public const double RequestChance = 0.4;

        /// <summary>A trader's want this trip: item, how many still, price each — Extra "want" = "Item:n:price". Null = none.</summary>
        public sealed class Want { public string Item = "", Why = ""; public int Left; public double Price; }

        public Want WantOf(SimAgent a)
        {
            if (a == null || !a.Extra.TryGetValue("want", out var w) || w.Length == 0) return null;
            var p = w.Split(':');
            if (p.Length < 3 || !int.TryParse(p[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) || n <= 0) return null;
            double.TryParse(p[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var price);
            return new Want { Item = p[0], Left = n, Price = price, Why = Requests.FirstOrDefault(r => r.Item == p[0])?.Why ?? "" };
        }

        /// <summary>A player brought a trader what it asked for: news (rumours), and it remembers them.</summary>
        public void Helped(SimAgent a, string playerName, string item, double now)
        {
            if (a == null) return;
            Events.Add(new SimEvent { Time = now, Agent = a.Id, Name = a.Name, System = a.System, Kind = "helped",
                Text = $"{(string.IsNullOrEmpty(playerName) ? "Some captain" : playerName)} got {a.Name} the {Humanize(item)} they were after" });
        }

        /// <summary>A player sold it <paramref name="n"/> of what it wanted.</summary>
        public void Fulfil(SimAgent a, int n)
        {
            var w = WantOf(a);
            if (w == null || n <= 0) return;
            var left = w.Left - n;
            if (left > 0) a.Extra["want"] = $"{w.Item}:{left.ToString(CultureInfo.InvariantCulture)}:{w.Price.ToString("0", CultureInfo.InvariantCulture)}";
            else a.Extra.Remove("want");
        }

        public static readonly SimRole[] Roles =
        {
            new SimRole { Id = "bulk", SpeedMin = 45, SpeedMax = 60, Label = "bulk hauler", Items = new[] { "IronOre", "CopperOre", "SiliconOre", "CobaltOre", "NeodymiumOre" }, Loaded = 0.6 },
            new SimRole { Id = "tanker", SpeedMin = 60, SpeedMax = 80, Label = "tanker", Items = new[] { "FusionCell", "EnergyCellLarge", "HydrogenBottle", "OxygenBottleLarge" }, Loaded = 0.7 },
            new SimRole { Id = "courier", SpeedMin = 90, SpeedMax = 110, Label = "courier", Items = new[] { "Computer", "EnergyMatrix", "PowerCoil", "GoldIngot" }, ValueMin = 6000, ValueMax = 20000, Rare = "Eden_TradePolarisT1", RareChance = 0.08, Loaded = 0.95 },
            new SimRole { Id = "supply", SpeedMin = 60, SpeedMax = 80, Label = "supply freighter", Items = new[] { "CannedMeat", "CannedVegetables", "Fruit", "Medikit01", "SteelPlate" }, ValueMin = 4000, ValueMax = 16000, Loaded = 0.75 },
        };
        public static SimRole RoleOf(SimAgent a) => Roles.FirstOrDefault(r => r.Id == a?.Role) ?? Roles[0];
        /// <summary>The galaxy map (null = no map: any system to any, flat warp time — tests and old saves).</summary>
        public Galaxy Galaxy;

        public SimAgent Get(string id) => id != null && Agents.TryGetValue(id, out var a) ? a : null;
        public SimAgent ByEntity(int entityId) => entityId == 0 ? null : Agents.Values.FirstOrDefault(a => a.EntityId == entityId);

        // ---- population ---------------------------------------------------------------------

        static readonly string[] TraderNames = { "Mule", "Kestrel", "Long Haul", "Brass Goose", "Tallow", "Second Wind", "Ostrich", "Penny", "Old Rope", "Cobble" };
        static readonly string[] TraderA = {
            "Brass", "Old", "Salt", "Quiet", "Grey", "Lucky", "Slow", "Patient", "Iron", "Copper", "Dusty", "Late",
            "Ashen", "Rusty", "Painted", "Wooden", "Glass", "Silk", "Velvet", "Leather", "Rubber", "Stone", "Marble",
            "Granite", "Slate", "Chalk", "Clay", "Sand", "Soot", "Smoke", "Brook", "Creek", "River", "Rain", "Storm",
            "Cloud", "Fog", "Mist", "Steam", "Breeze", "Gust", "Zephyr", "Whisper", "Murmur", "Drizzle", "Hail", "Sleet",
            "Snow", "Frost", "Chill", "Warm", "Ember", "Coal", "Black", "Dim", "Pale", "White", "Silver", "Gold",
            "Bronze", "Steel", "Lead", "Zinc", "Chrome", "Nickel", "Mercury", "Soft", "Snug", "Cozy", "Dull", "Faded",
            "Worn", "Ancient", "Aged", "Vintage", "Fresh", "Clean", "Grubby", "Smelly", "Ripe", "Toasted", "Burnt",
            "Crisp", "Spicy", "Sugar", "Sweet", "Sour", "Lemon", "Vinegar", "Humble", "Stubborn", "Weary", "Merry",
            "Tidy", "Honest", "Lazy", "Plucky", "Sturdy", "Gentle", "Thrifty", "Faithful", "Jolly", "Grumpy", "Lonely",
            "Little", "Big", "Fat", "Proud", "Rattling", "Creaky", "Squeaky", "Leaky", "Wobbly", "Tired", "Sleepy",
            "Steady", "Brave", "Hasty", "Cheerful", "Muddy", "Foggy", "Windy", "Rainy", "Sunny", "Misty", "Frosty",
            "Mossy", "Sandy", "Tin", "Pewter", "Amber", "Ochre", "Rosy", "Ruddy", "Tawny", "Dapple", "Speckled",
            "Spotted", "Crooked", "Bent", "Patched", "Mended", "Second", "Third", "Last", "Fair", "Kind", "Wise", "Mild",
            "Calm", "Idle", "Wandering", "Roving", "Drifting", "Far", "Northern", "Southern", "Eastern", "Western",
            "Midnight", "Morning", "Evening", "Autumn", "Winter", "Summer", "Spring", "Harvest", "Honey", "Butter",
            "Pepper", "Ginger", "Cinnamon", "Mint", "Clover", "Thistle", "Hazel", "Willow", "Birch", "Oak", "Elm",
            "Cedar"
        };
        static readonly string[] TraderB = {
            "Goose", "Kettle", "Barrow", "Lantern", "Ox", "Heron", "Wagon", "Biscuit", "Anchor", "Thimble", "Pail",
            "Juniper", "Oar", "Rope", "Hammer", "Anvil", "Stove", "Mug", "Pot", "Ladle", "Whisk", "Sieve", "Spoon",
            "Bowl", "Lamp", "Plug", "Spool", "Bobbin", "Reel", "Wheel", "Gear", "Pulley", "Chain", "Latch", "Hinge",
            "Key", "Button", "Comb", "Brush", "Broom", "Mop", "Sponge", "Rag", "Quill", "Map", "Atlas", "Globe",
            "Spyglass", "Lens", "Mirror", "Prism", "Jade", "Ruby", "Opal", "Quartz", "Flint", "Agate", "Pebble", "Twine",
            "Lace", "Ribbon", "Linen", "Wool", "Felt", "Feather", "Plume", "Shell", "Antler", "Horn", "Tusk", "Tallow",
            "Wax", "Putty", "Brick", "Tile", "Beam", "Rafter", "Fence", "Ladder", "Crane", "Winch", "Capstan", "Mast",
            "Boom", "Jib", "Mule", "Badger", "Otter", "Beaver", "Mole", "Hedgehog", "Tortoise", "Turtle", "Pelican",
            "Albatross", "Gull", "Puffin", "Magpie", "Wren", "Robin", "Owl", "Hen", "Duck", "Swan", "Stork", "Walrus",
            "Seal", "Whale", "Carp", "Trout", "Salmon", "Herring", "Cod", "Hound", "Pony", "Donkey", "Goat", "Ram",
            "Sow", "Hog", "Yak", "Camel", "Llama", "Moth", "Beetle", "Snail", "Cricket", "Bumblebee", "Dumpling",
            "Pudding", "Pickle", "Turnip", "Potato", "Cabbage", "Onion", "Parsnip", "Pretzel", "Cracker", "Crumpet",
            "Teapot", "Teacup", "Saucer", "Jug", "Flask", "Barrel", "Crate", "Basket", "Satchel", "Kite", "Boot",
            "Mitten", "Scarf", "Bonnet", "Slipper", "Clog", "Cart", "Barge", "Tug", "Dory", "Skiff", "Pram"
        };
        static readonly string[] PirateA = {
            "Rust", "Gutter", "Black", "Bitter", "Cold", "Red", "Hollow", "Grin", "Rotten", "Broken", "Sharp", "Grey",
            "Iron", "Steel", "Brass", "Bone", "Blood", "Skull", "Gut", "Gore", "Filth", "Sludge", "Grim", "Dark", "Pale",
            "Sick", "Dead", "Lost", "Cracked", "Ragged", "Torn", "Scar", "Scarred", "Flayed", "Empty", "Void", "Null",
            "Ghost", "Wraith", "Phantom", "Shade", "Shroud", "Veil", "Mask", "Glare", "Spark", "Cinder", "Smog", "Fume",
            "Ash", "Storm", "Frost", "Howl", "Scream", "Rasp", "Snarl", "Stab", "Slit", "Hack", "Smash", "Crack",
            "Marrow"
        };
        static readonly string[] PirateB = {
            "Jaw", "Saint", "Fang", "Ledger", "Knife", "Debt", "Hook", "Teeth", "Scab", "Ruin", "Wreck", "Carrion",
            "Grave", "Rot", "Cage", "Shard", "Razor", "Grit", "Spike", "Blade", "Specter", "Abyss", "Tooth", "Claw",
            "Maw", "Widow", "Mourner", "Butcher", "Vulture", "Jackal", "Hyena", "Leech", "Tick", "Rat", "Crow", "Raven",
            "Vandal", "Reaper", "Hangman", "Gallows", "Noose", "Shackle", "Brand", "Cleaver", "Hatchet", "Cutlass",
            "Dagger", "Harpoon", "Gaff", "Toll", "Tithe", "Ransom", "Bounty", "Grudge", "Curse", "Hex", "Omen", "Plague",
            "Fever", "Blight", "Famine", "Wolf", "Shark", "Viper", "Adder", "Scorpion", "Mantis", "Wasp", "Hornet",
            "Barb", "Thorn", "Nail", "Splinter"
        };

        /// <summary>A trader / pirate name nobody alive has: the classic names first, then generated pairs ("Salt Heron").</summary>
        string FreeName(bool trader)
        {
            // taken while alive — or while its wreck is still out there ("Mule" the wreck and "Mule" the new hauler confused)
            // …or lost recently: a new ship under a dead one's name reads as the same ship
            // one pass over agents/wrecks/events per call (a scan per candidate name went quadratic with 1,000+ agents)
            var used = new HashSet<string>(Agents.Values.Where(x => x.Alive).Select(x => x.Name), StringComparer.Ordinal);
            used.UnionWith(Wrecks.Values.Where(w => !w.IsHub).Select(w => w.Name));
            used.UnionWith(Events.Where(e => e.Kind == "destroyed" && LastTick - e.Time < 6 * 3600).Select(e => e.Name));
            bool Used(string n) => used.Contains(n);
            foreach (var n in trader ? TraderNames : PirateNames) if (!Used(n)) return n;
            var a = trader ? TraderA : PirateA; var b = trader ? TraderB : PirateB;
            // every pair, spread out (i*7 % n walks all of them when n is coprime with 7) — "Salt Heron", "Old Kettle", …
            var n2 = a.Length * b.Length;
            // no shared word with a ship that's flying (look-alike names read as one ship); relaxed if the pool runs dry
            var words = new HashSet<string>(Agents.Values.Where(x => x.Alive && x.IsTrader == trader).SelectMany(x => x.Name.Split(' ')), StringComparer.Ordinal);
            int Gcd(int x, int y) => y == 0 ? x : Gcd(y, x % y);
            var step = new[] { 7, 11, 13, 17, 19, 23 }.First(q => Gcd(q, n2) == 1);   // i*step % n walks every pair when coprime
            var start = Fnv("name|" + Agents.Count + "|" + LastTick.ToString("R", CultureInfo.InvariantCulture)) % (uint)n2;   // a different spot each time
            for (var pass = 0; pass < 2; pass++)
                for (var i = 0; i < n2; i++)
                {
                    var k = (int)((start + (long)i * step) % n2); var w1 = a[k % a.Length]; var w2 = b[k / a.Length]; var n = w1 + " " + w2;
                    if (Used(n) || (pass == 0 && (words.Contains(w1) || words.Contains(w2)))) continue;
                    return n;
                }
            return (trader ? "Hauler " : "Raider ") + (Agents.Count + 1).ToString(CultureInfo.InvariantCulture);
        }
        static readonly string[] PirateNames = { "Rust Jaw", "Gutter Saint", "Nine Teeth", "Red Ledger", "Hookwing" };

        /// <summary>Fill an empty world: traders docked at random systems (staggered), pirates lurking.</summary>
        public void Seed(Random rng, double now, int traders, int pirates)
        {
            if (Config.Systems.Count == 0) return;
            for (var i = 0; i < traders; i++)
            {
                var t = Add(new SimAgent
                {
                    Id = NextId("t"), Kind = "trader", Name = TraderNames[i % TraderNames.Length], System = Pick(rng, Config.Systems),
                    State = "dock", Until = now + rng.NextDouble() * Config.DockMax,
                    Strength = rng.NextDouble() < Config.ArmedShare ? 1 : 0,   // unarmed haulers can only run
                });
                t.Role = Roles[(int)(Fnv(t.Id) % (uint)Roles.Length)].Id;
                FitSpeed(t);
            }
            for (var i = 0; i < pirates; i++)
                Add(new SimAgent
                {
                    Id = NextId("p"), Kind = "pirate", Name = PirateNames[i % PirateNames.Length], System = Pick(rng, Config.Systems),
                    State = "lurk", Until = now + Between(rng, Config.LurkMin, Config.LurkMax), Strength = rng.NextDouble() < 0.25 ? 2.5 : 1.0, Since = now,
                });
        }

        /// <summary>New traders docked in <paramref name="system"/> (a system that just joined the network), staggered undocks.</summary>
        public List<SimAgent> SeedAt(string system, int traders, Random rng, double now, int pirates = 0, bool warm = false)
        {
            var added = new List<SimAgent>();
            for (var i = 0; i < pirates; i++)
            {
                var lurk = Between(rng, Config.LurkMin, Config.LurkMax);
                var into = warm ? rng.NextDouble() * lurk : 0;   // warm: it's been there a while
                added.Add(Add(new SimAgent
                {
                    Id = NextId("p"), Kind = "pirate", Name = FreeName(false), System = system, State = "lurk", Since = now - into,
                    Until = now + lurk - into, Strength = rng.NextDouble() < 0.25 ? 2.5 : 1.0,
                }));
            }
            for (var i = 0; i < traders; i++)
            {
                var a = Add(new SimAgent
                {
                    Id = NextId("t"), Kind = "trader", Name = FreeName(true), System = system, State = "dock", Since = now,
                    Until = now + 60 + rng.NextDouble() * Config.DockMin,
                    Strength = rng.NextDouble() < Config.ArmedShare ? 1 : 0, Role = Roles[rng.Next(Roles.Length)].Id,
                });
                ShipOf(a, true);
                a.Speed = RoleOf(a).Speed(rng);
                // warm start: a snapshot of a system that's been running — most traders already somewhere on a lane
                var r = rng.NextDouble();
                if (warm && r < 0.65)
                {
                    var leg = r < 0.33 ? "out" : "in";
                    if (leg == "out") a.Dest = TraderDest(rng, system); else a.From = TraderDest(rng, system);
                    Load(a, rng);
                    var dur = LegSeconds(a); var done = rng.NextDouble();
                    a.State = leg; a.Since = now - done * dur; a.Until = now + (1 - done) * dur;
                }
                else if (warm) a.Until = now + rng.NextDouble() * Config.DockMax;
                added.Add(a);
            }
            // warm: the place has a past — sometimes a recent report someone filed here
            if (warm && added.Any(x => x.IsTrader) && rng.NextDouble() < 0.4)
            {
                var who = added.First(x => x.IsTrader);
                Events.Add(new SimEvent { Time = now - Between(rng, 600, 3000), Agent = who.Id, System = system, Kind = "attack", Text = $"{who.Name} reported pirates near {system}" });
            }
            return added;
        }

        // ---- the sim moves with the players ------------------------------------------------------

        /// <summary>How protected a system is (0…0.9): big stations and a trading hub mean patrols nearby.</summary>
        public double Safety(string system)
        {
            return Stations.TryGetValue(system ?? "", out var l) ? LiveRules.Safety(l.Select(x => x.Name)) : 0;
        }

        /// <summary>
        /// Keep the players' neighbourhood (<see cref="Focus"/>) populated: traders where it's thin (warm-started, more at
        /// hubs), pirates in the least protected systems. Returns what was added.
        /// </summary>
        public List<SimAgent> Populate(Random rng, double now)
        {
            var added = new List<SimAgent>();
            if (Focus.Count == 0) return added;
            // hub residents first: every hub near players has its crews (Focus also holds hub playfields)
            foreach (var hub in Hubs.Values.Where(h => Focus.Contains(h.System)).OrderBy(h => h.Id, StringComparer.Ordinal))
            {
                EnsureCentre(hub, now);   // older saves: hubs without one get theirs
                if (IsBroken(hub)) continue;   // quiet after a break: nobody signs up
                var have = Agents.Values.Count(a => !a.IsTrader && a.Alive && a.Home == hub.Id);
                // a rich gang (above the floor, nobody hitting it right now) buys its replacements, and they come sooner
                var pays = Config.HubWealth && Config.RecruitCost > 0 && hub.Cash - Config.RecruitCost >= Config.RecruitCashFloor && !Occupied.Contains(hub.System);
                if (have >= HubSize(hub)) { hub.NextRecruit = now + (pays ? Config.PaidRecruitMin : Config.RecruitMin); continue; }   // full: a loss waits for its first recruit
                // first time: the whole gang; after that one recruit at a time, hours apart
                var want = hub.Manned ? (now >= hub.NextRecruit ? have + 1 : have) : HubSize(hub);
                if (hub.Manned && want > have)
                {
                    if (pays) { hub.Cash -= Config.RecruitCost; Count("hub|" + hub.Id + "|bought"); Burn("recruits", Config.RecruitCost); }
                    hub.NextRecruit = now + (pays ? Between(rng, Config.PaidRecruitMin, Config.PaidRecruitMax) : Between(rng, Config.RecruitMin, Config.RecruitMax));
                    Count("hub|" + hub.Id + "|recruits", want - have);
                }
                hub.Manned = true;
                for (var i = have; i < want; i++)
                {
                    var stay = Between(rng, Config.HomeMin, Config.HomeMax); var into = rng.NextDouble() * stay;
                    added.Add(Add(new SimAgent
                    {
                        Id = NextId("p"), Kind = "pirate", Name = FreeName(false), System = hub.System, State = "home", Home = hub.Id,
                        Since = now - into, Until = now + stay - into, Strength = rng.NextDouble() < 0.25 + 0.15 * HubTier(hub) ? 2.5 : 1.0,
                    }));
                }
            }
            var lanes = Focus.Where(s => Config.Systems.Contains(s)).ToList();   // traffic and lane pirates: station systems only
            if (lanes.Count == 0) return added;
            int Traders(string s) => Agents.Values.Count(a => a.IsTrader && a.Alive && (a.System == s || a.Dest == s));
            var wantT = (int)Math.Round(Config.TradersPerSystem * lanes.Count);
            var haveT = Agents.Values.Count(a => a.IsTrader && a.Alive && (lanes.Contains(a.System) || (a.State == "pass" && lanes.Contains(a.Dest))));
            for (var guard = 0; haveT < wantT && guard < 50; guard++, haveT++)
            {
                var sys = lanes.OrderBy(s => Traders(s) / (1.0 + Safety(s))).ThenBy(s => s, StringComparer.Ordinal).First();   // thinnest, hubs want more
                added.AddRange(SeedAt(sys, 1, rng, now, 0, warm: true));
            }
            var sincePop = double.IsNaN(lastPopulate) ? double.PositiveInfinity : now - lastPopulate;
            lastPopulate = now;
            if (Config.DriftTopUp) { TopUpDrifters(lanes, sincePop, rng, now, added); return added; }
            var wantP = (int)Math.Ceiling(Config.PiratesPerSystem * lanes.Count);
            // hub crews nearby count toward it: they're the raiders here, drifters only fill the gap (crews on top of the
            // drifter quota double the attacks)
            var nearHubs = new HashSet<string>(Hubs.Values.Where(h => Focus.Contains(h.System)).Select(h => h.Id));
            var haveP = Agents.Values.Count(a => !a.IsTrader && a.Alive && ((a.Home.Length == 0 && lanes.Contains(a.System)) || nearHubs.Contains(a.Home)));
            for (var guard = 0; haveP < wantP && guard < 20; guard++, haveP++)
            {
                var sys = lanes.OrderBy(s => Safety(s) + 0.3 * Agents.Values.Count(a => !a.IsTrader && a.Alive && a.System == s)).ThenBy(s => s, StringComparer.Ordinal).First();
                added.AddRange(SeedAt(sys, 0, rng, now, 1, warm: true));
            }
            return added;
        }

        /// <summary>
        /// Drifters (<see cref="SimConfig.DriftTopUp"/>): at most half the raider target, only where no known hub is within
        /// warp range, and back slowly (a first call fills at once: a fresh world starts populated).
        /// </summary>
        void TopUpDrifters(List<string> lanes, double sinceLast, Random rng, double now, List<SimAgent> added)
        {
            var damp = KillDampNow();
            var odds = double.IsInfinity(sinceLast) ? 1 : 1 - Math.Exp(-Config.DriftRefillPerHour * damp * sinceLast / 3600);
            bool Reached(string s) => Galaxy != null && Hubs.Values.Any(h => Galaxy.Route(h.System, s, Config.WarpRangeLy, Config.GateLy, out var gate) <= Config.WarpRangeLy && !gate);
            var open = lanes.Where(s => !Reached(s)).ToList();
            if (open.Count == 0) return;
            var target = RaiderTarget(lanes.Count);
            var drifters = Agents.Values.Count(a => !a.IsTrader && a.Alive && a.Home.Length == 0 && lanes.Contains(a.State == "warp" ? a.Dest : a.System));
            for (var guard = 0; guard < 20; guard++)
            {
                if (drifters >= Math.Ceiling(target / 2) || RaidersNear(lanes) >= Math.Ceiling(target)) return;
                if (rng.NextDouble() >= odds) return;
                var sys = open.Where(s => RaidersIn(s) < Config.MaxRaidersPerSystem).OrderBy(s => Safety(s) + 0.3 * RaidersIn(s)).ThenBy(s => s, StringComparer.Ordinal).FirstOrDefault();
                if (sys == null) return;
                added.AddRange(SeedAt(sys, 0, rng, now, 1, warm: true)); drifters++;
            }
        }

        /// <summary>
        /// Over budget: retire the agents furthest from any player (<paramref name="distance"/> = LY from a system to the
        /// nearest player). Never live ones, fighting ones, or ones that remember a player. Dead pirates are cleared.
        /// </summary>
        /// <remarks>A ship that was live once (EntityId set, not live now) can be retired too — its hull was destroyed when it
        /// went back to the sim; pinning those lets every ship a player ever met pile up past the budget.
        /// <paramref name="removed"/> collects the retired agents (the Dedi destroys any entity still on record).</remarks>
        public int Recycle(Func<string, double> distance, List<SimAgent> removed = null)
        {
            bool KeptAnyway(SimAgent x) => x.IsLive || x.State == "fight" || x.State == "pass" || Focus.Contains(x.System) || (x.Home.Length > 0 && Hubs.ContainsKey(x.Home));
            pinnedByMemory = new HashSet<string>(Agents.Values.Where(x => x.Alive && !KeptAnyway(x) && MetAt(x) >= 0 && LastTick - MetAt(x) < MetPinSeconds)
                                                              .OrderByDescending(MetAt).Take(MetPinMax).Select(x => x.Id), StringComparer.Ordinal);
            var gone = 0;
            bool Hunted(SimAgent a) => Bounties.Values.Any(b => b.State == "open" && b.Pirate == a.Id);   // a contract holds him (alive or dead) until it's settled
            foreach (var p in Agents.Values.Where(a => !a.IsTrader && !a.Alive && !a.IsLive && !Hunted(a)).ToList()) { Agents.Remove(p.Id); removed?.Add(p); gone++; Burn("lost with ships", Cash0(p)); }
            // never anyone near players either: with everyone far away unrecyclable (fighting, live, remembering) the budget
            // would eat the ships Populate just put next to the player — the budget is soft instead
            bool Near(SimAgent a) => Focus.Contains(a.System) || (a.State == "warp" && Focus.Contains(a.Dest));
            // hub crews are as permanent as their hub (come back, the same pirates are chilling at the same spot)
            bool Keep(SimAgent a) => a.IsLive || a.State == "fight" || a.State == "pass" || Hunted(a) || a.Extra.ContainsKey("via") || RecentlyMet(a) || Near(a) || (a.Home.Length > 0 && Hubs.ContainsKey(a.Home));
            void Trim(bool traders, int budget)
            {
                var pool = Agents.Values.Where(a => a.IsTrader == traders && (a.Alive || traders) && a.Home.Length == 0).ToList();   // residents: own pool
                var over = pool.Count - budget;
                if (over <= 0) return;
                foreach (var a in pool.Where(a => !Keep(a)).OrderByDescending(a => distance(a.State == "warp" && a.Dest.Length > 0 ? a.Dest : a.System)).ThenBy(a => a.Id, StringComparer.Ordinal).Take(over).ToList())
                { Agents.Remove(a.Id); removed?.Add(a); gone++; Burn("retired far away", Cash0(a)); }
            }
            Trim(true, Config.TraderBudget);
            Trim(false, Config.PirateBudget);
            return gone;
        }

        /// <summary>
        /// A memory for an agent: its own, or — "killed" (a crewmate a player killed) of a hub pirate — the whole hub's.
        /// </summary>
        /// <summary>Memories pin an agent (not retired far away) this long after its last contact with a player — not forever.</summary>
        public const double MetPinSeconds = 24 * 3600;
        /// <summary>At most this many agents held by memory at once: the ones met longest ago are released first.</summary>
        public const int MetPinMax = 15;

        static double MetAt(SimAgent a) => a.Extra.TryGetValue("metat", out var m) && double.TryParse(m, NumberStyles.Float, CultureInfo.InvariantCulture, out var t) ? t : -1;

        HashSet<string> pinnedByMemory;
        bool RecentlyMet(SimAgent a) => pinnedByMemory != null && pinnedByMemory.Contains(a.Id);

        public void Remember(SimAgent a, string key, string value)
        {
            if (a == null || !key.StartsWith("memory.", StringComparison.Ordinal)) return;
            a.Extra["metat"] = LastTick.ToString("0", CultureInfo.InvariantCulture);   // last contact: keeps it around a while
            if (key.StartsWith("memory.killed.", StringComparison.Ordinal) && HubOf(a) is SimHub h) h.Memory[key] = value;
            else a.Extra[key] = value;
        }

        /// <summary>Everything an agent remembers: its own memories + its hub's.</summary>
        public IEnumerable<KeyValuePair<string, string>> MemoriesOf(SimAgent a)
        {
            var own = a.Extra.Where(kv => kv.Key.StartsWith("memory.", StringComparison.Ordinal));
            return HubOf(a) is SimHub h ? own.Concat(h.Memory.Where(kv => !a.Extra.ContainsKey(kv.Key))) : own;
        }

        /// <summary>
        /// A player is in the hub's system and <paramref name="dying"/> was the last crew at home (guarding, or fighting
        /// there): the hub is overrun — roamers away don't keep it standing.
        /// </summary>
        bool Overrun(SimHub h, SimAgent dying) =>
            Occupied.Contains(h.System) && !Agents.Values.Any(x => x != dying && !x.IsTrader && x.Alive && x.Home == h.Id && x.System == h.System
                                                                  && (x.State == "home" || x.State == "fight" || x.State == "repair"));

        /// <summary>
        /// The gang is broken (overrun, or every crew dead): its cash goes into the crates as credits — and there IS a crate:
        /// if no lootable hub wreck stands, one is parked at the hub for it (a box you can't find is no reward).
        /// </summary>
        void Break(SimHub h, double t)
        {
            var paid = Config.HubWealth ? (int)(h.Cash * Config.BreakPayoutFraction) : 0;
            h.Cash = 0;
            var jitter = 1 + Config.HubQuietJitter * ((Fnv("quiet|" + h.Id + "|" + t.ToString("R", CultureInfo.InvariantCulture)) % 2001) / 1000.0 - 1);
            h.BrokenUntil = t + Config.HubQuietHours * 3600 * jitter;
            h.NextRecruit = h.BrokenUntil;   // the new gang's first free recruit comes when the quiet ends
            if (paid >= 1) { h.Stash.Add(new SimStash { Item = Strongbox, Amt = paid, Time = t }); Count("hub|" + h.Id + "|paid", paid); }
            Count("hub|" + h.Id + "|broken");
            if (h.Stash.Count > 0 && !Wrecks.Values.Any(x => x.Hub == h.Id && !x.Centre && !x.Debris && !x.Gone))   // the listening post has no crates
            {
                var box = new SimWreck { Id = NewWreckId(), Name = h.Name + " hoard", Kind = "trader", Role = "supply", Ship = "CV_Freighter_SparrowA", Time = t };
                Wrecks[box.Id] = box;
                ParkAtHub(box, h, t);
            }
            Events.Add(new SimEvent { Time = t, Agent = "", Name = h.Name, System = h.System, Kind = "broken",
                Text = paid >= 1 ? $"{h.Name} is broken — its strongbox ({paid} cr) is up for grabs" : $"{h.Name} is broken — whatever they hoarded is up for grabs" });
        }

        /// <summary>Wealth steps a hub has grown (0 without <see cref="SimConfig.HubWealth"/>).</summary>
        public int HubTier(SimHub h) => !Config.HubWealth || h == null ? 0 : Math.Min(Config.HubGrowMax, (int)(Math.Max(0, h.Cash) / Config.HubGrowStep));
        /// <summary>What folk call a hub by its wealth step: camp → hideout → outpost → stronghold.</summary>
        public string HubRank(SimHub h) => new[] { "camp", "hideout", "outpost", "stronghold" }[Math.Min(3, HubTier(h))];

        /// <summary>Crews a hub keeps: its rolled size plus one per wealth step.</summary>
        public int HubSize(SimHub h) => h.Size + HubTier(h);

        public SimHub HubOf(SimAgent a) => a != null && a.Home.Length > 0 && Hubs.TryGetValue(a.Home, out var h) && !IsBroken(h) ? h : null;
        public bool IsBroken(SimHub h) => h != null && h.BrokenUntil > LastTick;

        static readonly string[] HubNames = { "the Boneyard", "Rust Hollow", "Dead Man's Reach", "the Pit", "Scrapper's Rest", "the Graveyard",
                                              "Black Anchorage", "Gutter Row", "the Den", "Cold Harbour", "Knife Point", "the Drift" };

        /// <summary>
        /// Stars near players get their one hub roll (kept forever, deterministic by star name — a reset rolls the same):
        /// a star with an Eden pirate orbit always gets a hub there; otherwise <see cref="SimConfig.HubChance"/>, in its best
        /// hideout (asteroid field, then a quiet orbit) without a station. Returns the new hubs.
        /// </summary>
        public List<SimHub> DiscoverHubs(IEnumerable<Galaxy.Star> stars, double now)
        {
            var made = new List<SimHub>();
            if (Galaxy == null) return made;
            foreach (var star in stars.Where(x => x != null).Distinct().OrderBy(x => x.Name, StringComparer.Ordinal))
            {
                if (!RolledStars.Add(star.Name)) continue;
                var cands = Galaxy.PlayfieldsOf(star).Where(pf => !Stations.ContainsKey(pf) && Galaxy.HideoutScore(pf) > 0).OrderBy(pf => pf, StringComparer.Ordinal).ToList();
                if (cands.Count == 0) continue;
                var h = Fnv("hub|" + star.Name);
                var best = cands.Max(pf => Galaxy.HideoutScore(pf));
                if (best < 3 && h % 1000 >= Config.HubChance * 1000) continue;
                var top = cands.Where(pf => Galaxy.HideoutScore(pf) == best).ToList();
                var pfName = top[(int)(h / 1000 % (uint)top.Count)];
                var id = "h1";
                for (var n = 1; Hubs.ContainsKey(id); n++) id = "h" + n.ToString(CultureInfo.InvariantCulture);
                var used = new HashSet<string>(Hubs.Values.Select(x => x.Name));
                var name = HubNames.Skip((int)(h % (uint)HubNames.Length)).Concat(HubNames).FirstOrDefault(x => !used.Contains(x)) ?? "Hideout " + id;
                var hub = new SimHub { Id = id, System = pfName, Name = name, Since = now, Size = 3 + (int)(h / 7 % 3), Pos = LaneDir("hubpos|" + pfName) * (float)(4000 + h % 5000) };
                KeepOffTurf(hub);
                Hubs[id] = hub; made.Add(hub);
                EnsureCentre(hub, now);
            }
            return made;
        }

        /// <summary>
        /// A live trader just jumped out of <paramref name="from"/> (escort run end, or fleeing): it carries on in the
        /// sim, mid-warp to another system. Same agent if we already know the entity.
        /// </summary>
        public SimAgent Adopt(int entityId, string name, string from, double hull, Random rng, double now)
        {
            var a = ByEntity(entityId) ?? Add(new SimAgent { Id = NextId("t"), EntityId = entityId, Kind = "trader" });
            a.Name = name ?? a.Name; a.System = from ?? ""; a.Hull = Clamp01(hull);
            // a sim trader keeps the destination it was flying to; a ship new to the sim gets one
            if (a.Dest.Length == 0 || a.Dest == a.System) a.Dest = TraderDest(rng, a.System);
            a.State = "warp"; a.Until = now + WarpTime(a.System, a.Dest); LeaveFightQuiet(a); a.Met = a.After = a.Live = ""; a.Resume = 0; a.Shown = false; a.StationId = 0; a.Since = now + Config.WarpSeconds;
            Emit(now, a, "jump", $"{a.Name} jumped out of {a.System}, bound for {a.Dest}");
            return a;
        }

        public SimAgent Add(SimAgent a) { Agents[a.Id] = a; return a; }

        /// <summary>Ids only go up (saved): a reused id would hand an old ship's call sign, events and references to a new one.</summary>
        public int IdSeq;

        string NextId(string prefix)
        {
            if (IdSeq == 0) IdSeq = Agents.Keys.Select(k => int.TryParse(new string(k.SkipWhile(c => !char.IsDigit(c)).ToArray()), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0).DefaultIfEmpty(0).Max();
            for (;;) { var id = prefix + (++IdSeq).ToString(CultureInfo.InvariantCulture); if (!Agents.ContainsKey(id)) return id; }
        }

        // ---- the tick -----------------------------------------------------------------------

        /// <summary>
        /// The server was down since <see cref="LastTick"/>: pause the world over the gap (nothing happened meanwhile).
        /// Call once after loading. Gaps while the server runs (nobody online, no ticks) are replayed by <see cref="Step"/>.
        /// </summary>
        public void Resume(double now)
        {
            if (LastTick <= 0 || now <= LastTick) return;
            var shift = now - LastTick;
            foreach (var a in Agents.Values)
            {
                if (a.Until < Never) a.Until += shift; if (a.Since > 0) a.Since += shift;
                var met = MetAt(a); if (met > 0) a.Extra["metat"] = (met + shift).ToString("0", CultureInfo.InvariantCulture);   // downtime doesn't eat the memory pin
            }
            foreach (var f in Fights.Values) { f.Start += shift; f.Next += shift; }
            foreach (var e in Events) e.Time += shift;   // reports age only while the world runs
            foreach (var w in Wrecks.Values) { w.Time += shift; if (w.Until < Never) w.Until += shift; }
            foreach (var h in Hubs.Values) { foreach (var s in h.Stash) s.Time += shift; if (h.BrokenUntil > 0) h.BrokenUntil += shift; if (h.RichSince > 0) h.RichSince += shift; if (h.LastSplit > 0) h.LastSplit += shift; }
            foreach (var a in Agents.Values.Where(x => x.LootAmt > 0)) a.LootUntil += shift;
            LastTick = now;
        }

        /// <summary>Advance to <paramref name="now"/>. Returns what happened (also kept in <see cref="Events"/>).</summary>
        public List<SimEvent> Step(double now, Random rng)
        {
            var before = Events.Count;
            var dt = Math.Max(0, Math.Min(3600, now - LastTick));
            LastTick = now;
            if (Config.HubWealth && dt > 0)
                foreach (var h in Hubs.Values.Where(x => x.Cash > 0))
                {
                    var spent = h.Cash * (1 - Math.Exp(-Config.SpendPerHour * dt / 3600));
                    var upkeep = Math.Min(h.Cash - spent, Config.UpkeepPerCrewHour * dt / 3600 * Agents.Values.Count(a => !a.IsTrader && a.Alive && a.Home == h.Id));
                    h.Cash = Math.Max(0, h.Cash - spent - upkeep);
                    Burn("hub spending", spent); Burn("crew upkeep", upkeep);
                }

            if (Config.HubSplit && Galaxy != null && now >= nextSplitCheck) { nextSplitCheck = now + 600; Splits(now, rng); }
            Tidy(now);
            Reroute(now, rng);
            foreach (var a in Agents.Values.Where(x => x.IsTrader && x.Role.Length == 0))
            {   // old saves: a role, and a speed that fits it
                a.Role = Roles[(int)(Fnv(a.Id) % (uint)Roles.Length)].Id;
                var r = RoleOf(a); a.Speed = Math.Max(r.SpeedMin, Math.Min(r.SpeedMax, a.Speed));
            }
            // every agent has its hull (traders' guns follow it) before anyone fights
            foreach (var a in Agents.Values.Where(x => x.Alive && x.Ship.Length == 0)) ShipOf(a);
            foreach (var p in Agents.Values.Where(x => x.LootAmt > 0 && x.LootUntil <= now && !x.IsLive).ToList())
            {
                if (p.Home.Length > 0) { var fv = p.Hold.Sum(x => Value(x.Item, x.Amt)); Count("hub|" + p.Home + "|fenced", fv); if (Config.HubWealth && Hubs.TryGetValue(p.Home, out var fh) && !IsBroken(fh)) { fh.Cash += fv * Config.FenceShare; Mint("fencing", fv * Config.FenceShare); } }
                Emit(p.LootUntil, p, "fenced", $"{p.Name} fenced its {string.Join(" and ", p.Hold.Select(x => Humanize(x.Item)))}"); p.Hold.Clear();
            }
            foreach (var w in Wrecks.Values.Where(x => x.Entity == 0 && !x.IsHub && x.Until <= now).OrderBy(x => x.Until).ToList())
            {   // the winners haul it home; else towed / cleared — said out loud, so a beacon doesn't just blink out
                if (Hubs.TryGetValue(w.By, out var hh)) { Haul(w, hh, w.Until); continue; }
                Wrecks.Remove(w.Id); Burn("towed wrecks", w.Cash);
                Events.Add(new SimEvent { Time = w.Until, Agent = "", Name = w.Name, System = w.System, Kind = "towed", Text = $"The wreck of {w.Name} near {w.System} was towed away" });
            }
            for (var guard = 0; guard < 20000; guard++)
            {
                // the earliest thing that happens next: a state change, a trader passing an ambush, a fight round
                // live agents belong to the game — except a docked trader parked live: the sim still says when it leaves
                // single-pass minimum (a full sort per event went quadratic at 1,000+ agents); same order: Until, traders first, Id
                SimAgent due = null;
                foreach (var a in Agents.Values)
                {
                    // live agents belong to the game — except a docked trader parked live and a pirate resting at home / lurking:
                    // the sim still says when they leave (the PfServer flies them out), else a player watching froze them for good
                    if (!((a.Alive || a.IsTrader) && (!a.IsLive || (a.IsTrader && a.State == "dock") || (!a.IsTrader && (a.State == "home" || a.State == "lurk"))) && a.State != "fight" && a.Until <= now)) continue;
                    if (due == null || a.Until < due.Until || (a.Until == due.Until && (a.IsTrader ? 0 : 1) < (due.IsTrader ? 0 : 1))
                        || (a.Until == due.Until && a.IsTrader == due.IsTrader && string.CompareOrdinal(a.Id, due.Id) < 0)) due = a;
                }
                SimFight fr = null;
                foreach (var f in Fights.Values)
                    if (!f.IsLive && f.Next <= now && (fr == null || f.Next < fr.Next || (f.Next == fr.Next && string.CompareOrdinal(f.Id, fr.Id) < 0))) fr = f;
                var pass = NextPass(now, false);
                var tDue = due?.Until ?? double.MaxValue; var tFr = fr?.Next ?? double.MaxValue; var tPass = pass?.Time ?? double.MaxValue;
                if (due == null && fr == null && pass == null) break;
                if (tPass <= tDue && tPass <= tFr)
                {
                    if (Escort(pass.Trader) > 0 && FightOf(pass.Pirate) == null && rng.NextDouble() < Config.EscortDeter)
                    {   // a lone lurker eyes the cop and lets it go
                        pass.Trader.Met = pass.Trader.Met.Length > 0 ? pass.Trader.Met + ";" + pass.Pirate.Id : pass.Pirate.Id;
                        Emit(pass.Time, pass.Trader, "deterred", $"{pass.Pirate.Name} let {pass.Trader.Name} pass — it had an escort");
                    }
                    else if (!PaysToll(pass.Trader, pass.Pirate, pass.Time, rng)) StartFight(pass.Trader, pass.Pirate, pass.Time, rng);
                }
                else if (tDue <= tFr) Advance(due, rng);
                else Round(fr, fr.Next, rng);
            }

            UpdateBounties(now);
            var news = Events.Skip(before).ToList();
            if (Events.Count > KeepEvents) Events.RemoveRange(0, Events.Count - KeepEvents);
            return news;
        }

        void Advance(SimAgent a, Random rng)
        {
            var t = a.Until;
            if (a.IsTrader)
                switch (a.State)
                {
                    case "dock":
                    {
                        var arrivedWith = a.Hull;
                        a.Hull = LiveRules.HullAfterStay(a.Hull, StationOf(a)?.Name);   // patched up over the stay (a yard: fully)
                        // back from a repair detour: the trip it interrupted goes on, same cargo
                        var resume = a.Extra.TryGetValue("detour", out var after) && after.Length > 0 && after != a.System ? after : null;
                        a.Extra.Remove("detour");
                        a.Dest = resume ?? TraderDest(rng, a.System, StationOf(a)?.Id ?? 0);
                        if (resume == null) { Load(a, rng); Count(StKey(a) + "out", Value(a.Cargo, a.CargoAmt)); }
                        // judged by the hull it limped in with: a +20 % patch doesn't make a wreck seaworthy
                        if (arrivedWith < LiveRules.CriticalHull && a.Hull < 0.95 && !HasYard(a.Dest) && YardDest(a.System) is string yard && yard != a.Dest)
                        {   // falling apart, and nowhere on the way to fix it: the trip waits — a yard first, cargo kept
                            a.Extra["detour"] = a.Dest;
                            a.Dest = yard;
                            Emit(t, a, "repair-seek", $"{a.Name} is limping to {yard} for repairs");
                        }
                        a.Extra.Remove("esc");
                        if (Config.EscortChance > 0 && rng.NextDouble() < Config.EscortChance * Safety(a.System)) a.Extra["esc"] = "1";
                        StartLeg(a, "out", t);
                        Emit(t, a, "undock", $"{a.Name} undocked in {a.System}, bound for {a.Dest}{(a.Extra.ContainsKey("esc") ? " under escort" : "")}");
                        return;
                    }
                    case "out":
                        a.State = "warp"; a.Met = "";
                        var via = PickVia(a, rng, t);
                        if (via != null) { a.Extra["via"] = via; LastPass[via] = t; a.Until = t + WarpTime(a.System, via); }
                        else { a.Extra.Remove("via"); a.Until = t + WarpTime(a.System, a.Dest); }
                        Emit(t, a, "jump", $"{a.Name} jumped out of {a.System}, bound for {a.Dest}");
                        return;
                    case "warp":
                        if (a.Extra.TryGetValue("via", out var stop) && stop.Length > 0)
                        {   // a stopover on the way: drop in, cross the system, jump on (Dest stays the final one)
                            a.Extra.Remove("via");
                            a.From = a.System; a.System = stop; a.StationId = 0;
                            StartLeg(a, "pass", t);
                            Emit(t, a, "pass", $"{a.Name} dropped out of warp in {a.System}, passing through to {a.Dest}");
                            HubAmbush(a, t, rng);
                            return;
                        }
                        a.From = a.System; a.System = a.Dest.Length > 0 ? a.Dest : a.System; a.Dest = ""; a.StationId = 0;
                        // inbound: dock somewhere whose approach (from where it came) hasn't been hit lately, if it can
                        if (Stations.TryGetValue(a.System, out var arriveAt) && arriveAt.Count > 1)
                        {
                            var hotIn = HotLanes(t);
                            var calm = arriveAt.Where(x => !hotIn.Contains(LaneKey(x.Id, a.From))).ToList();
                            if (calm.Count > 0 && calm.Count < arriveAt.Count) a.StationId = calm[rng.Next(calm.Count)].Id;
                        }
                        // a wreck of a ship docks where it can be fixed
                        if (a.Hull < LiveRules.CriticalHull && Stations.TryGetValue(a.System, out var yards) && yards.FirstOrDefault(x => LiveRules.IsYard(x.Name)) is SimStation yd)
                            a.StationId = yd.Id;
                        StartLeg(a, "in", t);
                        Count("sys|" + a.System + "|arr");
                        Emit(t, a, "arrive", $"{a.Name} arrived in {a.System}" + (a.Hull < 0.95 ? $", hull {a.Hull:P0}" : ""));
                        return;
                    case "dead":   // lost: some time later a new hauler takes its place on the lanes
                        a.Name = FreeName(true);
                        a.EntityId = 0; a.Hull = 1; a.Strength = rng.NextDouble() < Config.ArmedShare ? 1 : 0; a.Role = Roles[rng.Next(Roles.Length)].Id; a.Cargo = ""; a.CargoAmt = 0; a.Speed = RoleOf(a).Speed(rng);
                        var lanesNow = Focus.Where(x => Config.Systems.Contains(x)).ToList();
                        if (lanesNow.Count == 0 && Config.Systems.Count == 0) { a.Until = t + Config.ReplaceSeconds; return; }   // no network yet: try again later
                        a.System = Pick(rng, lanesNow.Count > 0 ? lanesNow : Config.Systems);
                        a.Ship = ""; ShipOf(a, true);   // a new ship takes the slot
                        a.From = a.Dest = a.Met = a.Live = a.Fight = a.After = ""; a.StationId = 0; a.Shown = false;
                        a.Extra.Clear();   // a new ship and crew: the old one's plans, purse, memories and grudges went down with it
                        a.State = "dock"; a.Until = t + Between(rng, Config.DockMin, Config.DockMax); a.Since = t;
                        Emit(t, a, "new", $"{a.Name} enters service in {a.System}");
                        return;
                    case "pass":   // crossed: on to where it was going
                        a.State = "warp"; a.Met = ""; a.Until = t + WarpTime(a.System, a.Dest);
                        return;
                    case "in":
                        a.State = "dock"; a.Until = t + Between(rng, Config.DockMin, Config.DockMax) + YardTime(a); a.Met = ""; a.Since = t;
                        a.Extra.Remove("esc");   // the escort peels off at the station
                        Count(StKey(a) + "docks");
                        if (!a.Extra.TryGetValue("detour", out var detour) || detour.Length == 0)
                        {   // delivered (not at a repair stop on the way)
                            var worth = Value(a.Cargo, a.CargoAmt);
                            Count(StKey(a) + "in", worth); Tally(a, "g.moved", worth); Tally(a, "g.trips");
                            if (Config.MoneyFlow)
                            {
                                var full = (int)(worth * Config.TradeMargin);
                                var pay = (int)Math.Min(full, Math.Max(0, Config.PurseCap * CashBase(a) - Cash(a)));
                                AddCash(a, pay); Tally(a, "g.earned", pay); Mint("delivery", pay);   // over the cap: banked ashore, gone from the sim
                            }
                            a.Cargo = ""; a.CargoAmt = 0;
                        }
                        // repairs happen during the stay: the hull goes up when it undocks (a live ship heals on screen meanwhile)
                        return;
                    default: a.Until = Never; return;
                }

            switch (a.State)
            {
                case "lurk":
                    var safe = a.IsLive ? 0 : Safety(a.System);   // live: real patrols / players do the chasing
                    if (safe > 0 && rng.NextDouble() < safe * Config.LurkChase)
                    {   // a patrol found its hideout: chased off, hurt — sometimes for good
                        if (rng.NextDouble() < safe * Config.PatrolKill)
                        { a.State = "dead"; a.Until = Never; a.Hull = 0; Burn("lost with ships", Cash0(a)); a.Extra.Remove("cash"); Emit(t, a, "destroyed", $"{a.Name} (pirate) was hunted down by a patrol near {a.System}"); return; }
                        a.Hull = Math.Max(0.1, a.Hull - Between(rng, 0.2, 0.5));
                        Emit(t, a, "patrol", $"A patrol chased {a.Name} out of {a.System}");
                    }
                    // a hub crew goes home now and then (or after a haul) — otherwise on to the next hunting ground
                    a.Dest = HubOf(a) is SimHub home && home.System != a.System && (a.LootAmt > 0 || (Config.MoneyFlow && Cash(a) > 0) || rng.NextDouble() < Config.GoHome || RaidersFull()) ? home.System : PirateDest(rng, a.System);
                    a.State = "warp"; a.Until = t + WarpTime(a.System, a.Dest);
                    return;
                case "warp":
                    a.System = a.Dest.Length > 0 ? a.Dest : a.System; a.Dest = ""; a.SpotStation = 0;
                    if (HubOf(a) is SimHub at && at.System == a.System) { a.State = "home"; a.Until = t + Between(rng, Config.HomeMin, Config.HomeMax); a.Since = t; Stash(a, at, t); return; }
                    a.State = "lurk"; a.Until = t + Between(rng, Config.LurkMin, Config.LurkMax); a.Since = t;
                    return;
                case "repair":
                    // a hub crew limps home to patch up (the repair time is spent there); drifters patch up where they are
                    if (HubOf(a) is SimHub rh && rh.System != a.System) { a.Dest = rh.System; a.State = "warp"; a.Until = t + WarpTime(a.System, a.Dest); a.Hull = Math.Max(a.Hull, 0.5); return; }
                    // already at its hub: it patches up THERE — at home, on guard, visible (a separate repair state would leave
                    // the hub looking empty)
                    if (HubOf(a) is SimHub here && here.System == a.System) { a.State = "home"; a.Until = t + Math.Max(Config.HomeMin, Config.RepairSeconds); a.Since = t; return; }
                    a.Hull = 1.0; a.State = "lurk"; a.Until = t + Between(rng, Config.LurkMin, Config.LurkMax); a.Since = t; a.SpotStation = 0;
                    return;
                case "home":   // done chilling: patched up, out raiding — unless it's needed on guard
                    a.Hull = 1.0;
                    // a hub far from every player goes quiet: crews sit at home (no endless raiding where nobody plays;
                    // they wake up when someone comes near again)
                    if (Focus.Count > 0 && !Focus.Contains(a.System)) { a.Until = t + Between(rng, Config.HomeMin, Config.HomeMax); a.Since = t; return; }
                    if (HubOf(a) is SimHub gh)
                    {
                        var guards = Math.Min(Config.HubGuards, Math.Max(0, HubSize(gh) - 1));
                        var atHome = Agents.Values.Count(x => x != a && !x.IsTrader && x.Alive && x.Home == gh.Id && x.State == "home");
                        if (atHome < guards) { a.Until = t + Between(rng, Config.HomeMin, Config.HomeMax); a.Since = t; return; }
                    }
                    if (RaidersFull()) { a.Until = t + Between(rng, 600, 1800); a.Since = t; return; }   // enough out raiding: look again soon
                    a.Dest = PirateDest(rng, a.System);
                    a.State = "warp"; a.Until = t + WarpTime(a.System, a.Dest);
                    return;
                default:
                    a.Until = Never; return;
            }
        }

        void StartLeg(SimAgent a, string leg, double t)
        {
            a.State = leg; a.Met = ""; a.Shown = false; a.Since = t;
            a.Until = t + LegSeconds(a);
            a.Extra.Remove("amb");
            if (a.IsTrader && !a.IsLive && Config.BanditFire > 0 && !Occupied.Contains(a.System) && Bandits.TryGetValue(a.System, out var guns) && guns.Count > 0
                && Fnv("bandit|" + a.Id + "|" + t.ToString("R", CultureInfo.InvariantCulture)) % 10000 < Config.BanditFire * 10000)
            {   // bandit sentries on the way (map data, dice — no agents): scorched, not killed
                var gun = guns[(int)(Fnv("gun|" + a.Id + "|" + t.ToString("R", CultureInfo.InvariantCulture)) % (uint)guns.Count)];
                var hit = 0.1 + (Fnv("hit|" + a.Id + "|" + t.ToString("R", CultureInfo.InvariantCulture)) % 200) / 1000.0;
                a.Hull = Math.Max(0.05, a.Hull - hit);
                Count("sys|" + a.System + "|bandit");
                Emit(t, a, "bandits", $"{a.Name} took fire from bandit guns ({gun.Name}) near {a.System} — hull {a.Hull:P0}");
            }
        }

        public double LegSeconds(SimAgent a) => ((a.State == "pass" || (a.State == "fight" && a.After == "pass")) ? Config.PassMetres : Config.LegMetres) / Math.Max(5f, a.Speed);

        // ---- transit (stopovers) ------------------------------------------------------------------

        /// <summary>Extra time at a yard: the worse the damage, the longer the rebuild.</summary>
        double YardTime(SimAgent a) => LiveRules.IsYard(StationOf(a)?.Name) && a.Hull < 0.95 ? (1 - a.Hull) * LiveRules.YardSecondsPerHull : 0;

        public bool HasYard(string system) => Stations.TryGetValue(system ?? "", out var l) && l.Any(x => LiveRules.IsYard(x.Name));

        /// <summary>The nearest other reachable system with a yard, or null.</summary>
        public string YardDest(string from) =>
            Config.Systems.Where(s => s != from && HasYard(s) && !double.IsInfinity(RouteLy(from, s)))
                          .OrderBy(s => RouteLy(from, s)).ThenBy(s => s, StringComparer.Ordinal).FirstOrDefault();

        /// <summary>A system ships cross in the open: not a planet surface (orbits, asteroid fields, deep space, warp targets).</summary>
        public bool IsSpace(string pf)
        {
            if (Galaxy == null || !Galaxy.Types.TryGetValue(pf ?? "", out var t) || t.Length == 0) return false;
            if (t.IndexOf("Sun", StringComparison.OrdinalIgnoreCase) >= 0) return false;
            return t.IndexOf("Space", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("Orbit", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Asteroid", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("WarpTarget", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("Nebula", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// A stopover for a jump from a.System to a.Dest, or null: a stationless space system a player is in (spaced out,
        /// not far off the route) — else, sometimes, a pirate hub on the way.
        /// </summary>
        bool OnTheWay(SimAgent a, string pf)
        {
            if (pf == a.System || pf == a.Dest || Galaxy.StarOf(pf) == null) return false;
            if (Galaxy.StarOf(pf) == Galaxy.StarOf(a.System) && Galaxy.StarOf(pf) == Galaxy.StarOf(a.Dest)) return false;
            var direct = RouteLy(a.System, a.Dest);
            if (double.IsInfinity(direct)) return false;
            var via = RouteLy(a.System, pf) + RouteLy(pf, a.Dest);
            return via - direct <= Math.Max(Config.PassDetourLy, 0.5 * direct);
        }

        /// <summary>A player system that can take a passer now: stationless open space, nobody passing / bound there, spaced out.</summary>
        // never a pirate hub's system: nobody routes past a den on purpose (hub passers come by HubViaChance only)
        bool WantsPasser(string pf, double t, SimAgent except = null) =>
            !Stations.ContainsKey(pf) && IsSpace(pf) && !Hubs.Values.Any(h => h.System == pf) && (!LastPass.TryGetValue(pf, out var last) || t - last >= Config.PassGapSeconds)
            && !Agents.Values.Any(x => x != except && x.IsTrader && x.Alive && (x.System == pf && (x.State == "pass" || (x.State == "fight" && x.After == "pass")) || (x.Extra.TryGetValue("via", out var v) && v == pf)));

        public string PickVia(SimAgent a, Random rng, double t)
        {
            if (Galaxy == null || a == null || a.Dest.Length == 0) return null;
            var players = Occupied.Where(pf => WantsPasser(pf, t, a) && OnTheWay(a, pf)).OrderBy(pf => pf, StringComparer.Ordinal).ToList();
            if (players.Count > 0 && rng.NextDouble() < Config.PassChance) return players[rng.Next(players.Count)];
            var hubs = Hubs.Values.Where(h => OnTheWay(a, h.System) && !Stations.ContainsKey(h.System)).Select(h => h.System).Distinct().OrderBy(x => x, StringComparer.Ordinal).ToList();
            if (hubs.Count > 0 && rng.NextDouble() < Config.HubViaChance) return hubs[rng.Next(hubs.Count)];
            return null;
        }

        /// <summary>
        /// Players' systems with nobody passing: now and then a ship already in warp on a fitting route drops in there soon
        /// (it reaches the stopover in 20–90 s; the rest of its trip is the warp on from there).
        /// </summary>
        void Reroute(double now, Random rng)
        {
            if (Galaxy == null) return;
            foreach (var pf in Occupied.OrderBy(x => x, StringComparer.Ordinal))
            {
                if (nextReroute.TryGetValue(pf, out var next) && now < next) continue;
                nextReroute[pf] = now + Config.RerouteSeconds;
                if (!WantsPasser(pf, now) || rng.NextDouble() >= Config.RerouteChance) continue;
                var pick = Agents.Values.Where(a => a.IsTrader && a.Alive && !a.IsLive && a.State == "warp" && a.Dest.Length > 0 && !a.Extra.ContainsKey("via")
                                                    && a.Until - now > 60 && OnTheWay(a, pf)).OrderBy(a => a.Id, StringComparer.Ordinal).ToList();
                if (pick.Count == 0) continue;
                var tr = pick[rng.Next(pick.Count)];
                tr.Extra["via"] = pf; LastPass[pf] = now;
                tr.Until = now + Between(rng, 20, 90);
            }
        }

        /// <summary>A passer dropped into a hub's system: a crew at home may jump it (fight at the hub).</summary>
        void HubAmbush(SimAgent tr, double t, Random rng)
        {
            var crew = Agents.Values.Where(p => !p.IsTrader && p.Alive && !p.IsLive && p.State == "home" && p.System == tr.System && HubOf(p) != null)
                                    .OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            if (crew.Count == 0 || rng.NextDouble() >= Config.HubJumpChance) return;
            var hubId = crew[0].Home;
            if (Config.HubAmbushGap > 0 && hubAmbushAt.TryGetValue(hubId, out var last) && t - last < Config.HubAmbushGap) return;
            hubAmbushAt[hubId] = t;
            StartFight(tr, crew[rng.Next(crew.Count)], t, rng);
        }

        // ---- engagements ------------------------------------------------------------------------

        public sealed class Pass { public SimAgent Trader, Pirate; public double Time; }

        /// <summary>
        /// The earliest moment (≤ <paramref name="now"/>) a trader on a leg reaches a pirate: its lane runs through the
        /// pirate's ambush spot (lurking, or already fighting there). In a system with no known stations there are no
        /// places: a trader on a leg and a lurking pirate meet as soon as both are there. <paramref name="live"/>: passes
        /// of live traders (the Dedi makes those real) instead of sim ones.
        /// </summary>
        public Pass NextPass(double now, bool live)
        {
            Pass best = null;
            // pirates by system, once per call (in Agents order: ties resolve as before)
            var bySys = new Dictionary<string, List<SimAgent>>(StringComparer.Ordinal);
            foreach (var x in Agents.Values)
                if (!x.IsTrader && x.Alive && (live || !x.IsLive))
                { if (!bySys.TryGetValue(x.System, out var l)) bySys[x.System] = l = new List<SimAgent>(); l.Add(x); }
            if (bySys.Count == 0) return null;
            foreach (var tr in Agents.Values.Where(x => x.IsTrader && x.Alive && x.OnLeg && x.IsLive == live))
            {
                if (!bySys.TryGetValue(tr.System, out var here)) continue;
                var met = tr.Met.Length == 0 ? null : new HashSet<string>(tr.Met.Split(';'), StringComparer.Ordinal);
                var placed = Track(tr, out var track);
                if (placed && Config.LegAmbush && !live && !Occupied.Contains(tr.System))
                {
                    var amb = LegAmbushOf(tr, here, met);
                    if (amb != null && amb.Time <= now && (best == null || amb.Time < best.Time)) best = amb;
                    continue;
                }
                foreach (var p in here)
                {
                    var pf = FightOf(p);
                    var fighting = p.State == "fight" && pf != null && !pf.IsLive && pf.HasPos;
                    if (p.State != "lurk" && !fighting) continue;
                    if (met != null && met.Contains(p.Id)) continue;
                    double tp;
                    if (!placed) { if (fighting) continue; tp = Math.Max(tr.Since, p.Since); }
                    else
                    {
                        if (!SpotOf(p, out var spot) || p.SpotStation != tr.StationId || p.SpotOther != (tr.State == "out" ? tr.Dest : tr.From)) continue;
                        if (fighting && (pf.Pos - spot).Length > Config.JoinRange) continue;   // fighting somewhere else
                        var f = tr.State == "out" ? p.SpotFrac : 1 - p.SpotFrac;
                        tp = track.T0 + f * (track.T1 - track.T0);
                        if (tp < tr.Since || tp < (fighting ? pf.Start : p.Since)) continue;   // passed before either was there
                    }
                    if (tp > now) continue;
                    if (best == null || tp < best.Time) best = new Pass { Trader = tr, Pirate = p, Time = tp };
                }
            }
            return best;
        }

        /// <summary>
        /// The leg's one ambush roll (<see cref="SimConfig.LegAmbush"/>), made the first time lurkers are in the system
        /// during the leg and kept in Extra["amb"] ("-" = got through; else pirate id | time). Hashed, not rng: NextPass is
        /// asked many times per step and must answer the same.
        /// </summary>
        Pass LegAmbushOf(SimAgent tr, List<SimAgent> here, HashSet<string> met)
        {
            bool Ready(SimAgent p) => (p.State == "lurk" || (p.State == "fight" && FightOf(p) is SimFight f && !f.IsLive && f.HasPos)) && (met == null || !met.Contains(p.Id));
            if (tr.Extra.TryGetValue("amb", out var got))
            {
                if (got == "-") return null;
                var bits = got.Split('|');
                var p = Get(bits[0]);
                if (p == null || p.System != tr.System || !Ready(p)) { tr.Extra["amb"] = "-"; return null; }
                return new Pass { Trader = tr, Pirate = p, Time = double.Parse(bits[1], CultureInfo.InvariantCulture) };
            }
            var cands = here.Where(Ready).OrderBy(p => p.Id, StringComparer.Ordinal).ToList();
            if (cands.Count == 0) return null;
            var per = Config.AmbushPerLeg * (1 - 0.7 * Safety(tr.System));
            var odds = 1 - Math.Pow(1 - Math.Max(0, per), cands.Count);
            var h = Fnv("amb|" + tr.Id + "|" + tr.Since.ToString("R", CultureInfo.InvariantCulture) + "|" + tr.State);
            if (h % 10000 >= odds * 10000) { tr.Extra["amb"] = "-"; return null; }
            var pick = cands[(int)(h / 10000 % (uint)cands.Count)];
            var from = Math.Max(tr.Since, pick.Since);
            if (from >= tr.Until) { tr.Extra["amb"] = "-"; return null; }
            var at = from + (h / 7919 % 1000) / 1000.0 * (tr.Until - from);
            tr.Extra["amb"] = pick.Id + "|" + at.ToString("R", CultureInfo.InvariantCulture);
            return new Pass { Trader = tr, Pirate = pick, Time = at };
        }

        /// <summary>
        /// <see cref="SimConfig.MoneyFlow"/>: a lone pirate (not already fighting) and a trader it outguns — sometimes the trader
        /// pays to pass. A broke one can't. The pirate pockets it (banked at home, or dropped in its wreck).
        /// </summary>
        bool PaysToll(SimAgent tr, SimAgent p, double t, Random rng)
        {
            if (!Config.MoneyFlow || FightOf(p) != null || tr.IsLive || p.IsLive || tr.Strength * Math.Max(0.1, tr.Hull) >= p.Strength * Math.Max(0.1, p.Hull)) return false;
            var toll = (int)Math.Max(Config.TollMin, Cash(tr) * Config.TollShare);
            if (Cash(tr) < toll || rng.NextDouble() >= Config.TollChance) return false;
            AddCash(tr, -toll); AddCash(p, toll);
            tr.Met = tr.Met.Length > 0 ? tr.Met + ";" + p.Id : p.Id;
            Count("sys|" + tr.System + "|toll", toll); if (HubOf(p) is SimHub h) { Count("hub|" + h.Id + "|tolls", toll); Count("hunt|" + h.Id + "|" + tr.System); } Tally(tr, "g.tolls", toll);
            Emit(t, tr, "toll", $"{tr.Name} paid {p.Name} {toll:N0} cr to pass near {tr.System}");
            return true;
        }

        public SimFight FightOf(SimAgent a) => a != null && a.Fight.Length > 0 && Fights.TryGetValue(a.Fight, out var f) ? f : null;

        /// <summary>
        /// <paramref name="tr"/> runs into <paramref name="p"/> at <paramref name="t"/>: it joins p's fight, or a new one
        /// starts at p's spot. Lurking pirates near the place join too. Returns the fight.
        /// </summary>
        public SimFight StartFight(SimAgent tr, SimAgent p, double t, Random rng)
        {
            tr.Met = tr.Met.Length > 0 ? tr.Met + ";" + p.Id : p.Id;
            var f = FightOf(p);
            if (f == null)
            {
                f = new SimFight { Id = NextFightId(), System = p.System, Start = t, Next = t + Config.RoundSeconds };
                if (SpotOf(p, out var at)) { f.Pos = at; f.HasPos = true; }
                Fights[f.Id] = f;
                Join(f, p, t);
            }
            Join(f, tr, t);
            // it remembers who (bounty offers): id + name — ids get reused once an agent is gone, the name says it's the same one
            tr.Extra["hitby"] = p.Id + ":" + t.ToString("0", CultureInfo.InvariantCulture) + ":" + p.Name.Replace(":", " ");
            Emit(t, tr, "attack", $"{tr.Name} reports pirates near {tr.System}!");
            Count("sys|" + tr.System + "|atk"); Tally(tr, "g.hit");
            var leg = tr.After.Length > 0 ? tr.After : "out";
            Events[Events.Count - 1].Lane = LaneKey(tr.StationId, leg == "in" ? tr.From : tr.Dest);
            Gather(f, t);
            return f;
        }

        void Join(SimFight f, SimAgent a, double t)
        {
            if (f.Members.Contains(a.Id)) return;
            f.Members.Add(a.Id);
            a.After = a.IsTrader ? (a.OnLeg || a.State == "pass" ? a.State : "out") : a.State == "home" ? "home" : "lurk";
            a.Resume = Math.Max(0, a.Until - t);
            a.State = "fight"; a.Until = Never; a.Fight = f.Id;
            if (f.IsLive && !a.IsLive) a.Live = f.Live;   // joining a fight someone is watching: it's shown too
        }

        /// <summary>Lurking pirates within <see cref="SimConfig.JoinRange"/> of the fight join it (a pack).</summary>
        void Gather(SimFight f, double t)
        {
            if (!f.HasPos) return;
            foreach (var p in Agents.Values.Where(x => !x.IsTrader && x.Alive && x.State == "lurk" && x.System == f.System && !x.IsLive).OrderBy(x => x.Id, StringComparer.Ordinal).ToList())
                if (SpotOf(p, out var at) && (at - f.Pos).Length <= Config.JoinRange)
                {
                    Join(f, p, t);
                    Emit(t, p, "pirate-join", $"{p.Name} joins the attack near {f.System}");
                }
        }

        /// <summary>One round: both sides trade hull by power; the dead drop out, hurt traders try to break away, hurt pirates break off.</summary>
        void Round(SimFight f, double t, Random rng)
        {
            var traders = Members(f).Where(a => a.IsTrader).ToList();
            var pirates = Members(f).Where(a => !a.IsTrader).ToList();
            if (traders.Count > 0 && pirates.Count > 0)
            {
                double Power(List<SimAgent> side) => side.Sum(a => a.Strength * Math.Max(0.1, a.Hull) + Config.EscortStrength * Escort(a));
                var escorts = traders.Count(a => Escort(a) > 0);
                var toTraders = Power(pirates) * Config.RoundHit * Between(rng, 0.5, 1.5) / (traders.Count + escorts);   // escorts draw their share
                var toPirates = Power(traders) * Config.RoundHit * Config.TraderFire * Between(rng, 0.5, 1.5) / pirates.Count;
                foreach (var a in traders.Where(a => Escort(a) > 0))
                {
                    var eh = Escort(a) - toTraders / Config.EscortStrength;
                    if (eh < Config.EscortBreak) { a.Extra.Remove("esc"); Emit(t, a, "escort-hurt", $"{a.Name}'s escort broke off, badly hit"); }
                    else a.Extra["esc"] = eh.ToString("0.###", CultureInfo.InvariantCulture);
                }
                var killers = string.Join(", ", pirates.Select(x => x.Name));
                // toughness: a big armed ship shrugs off more; an unarmed hauler takes it as it comes
                foreach (var a in traders) a.Hull -= toTraders / Math.Max(1.0, a.Strength);
                foreach (var a in pirates) a.Hull -= toPirates / Math.Max(1.0, a.Strength);
                // protection without players: in a safe system a patrol sometimes turns up — mauls the pirates, rarely kills
                var safety = Safety(f.System);
                if (safety > 0 && rng.NextDouble() < safety * Config.PatrolChance)
                {
                    foreach (var p in pirates) { p.Hull -= Between(rng, 0.3, 0.7); if (rng.NextDouble() < safety * Config.PatrolKill) p.Hull = 0; }
                    Emit(t, pirates[0], "patrol", $"A patrol turned up near {f.System} — {killers} under fire");
                }

                foreach (var a in traders.Concat(pirates).Where(a => a.Hull <= 0))
                {
                    Leave(f, a); a.State = "dead"; a.Until = a.IsTrader ? t + Config.ReplaceSeconds : Never; a.Hull = 0; a.After = ""; a.Extra.Remove("esc");
                    Emit(t, a, "destroyed", a.IsTrader ? $"{a.Name} was destroyed by {killers} near {a.System}" : $"{a.Name} (pirate) was destroyed near {a.System}");
                    // the hold: pirates still standing take their share; the rest stays in the wreck
                    var left = a.IsTrader ? a.CargoAmt : a.LootMainAmt;   // a dead pirate's wreck keeps its biggest load
                    var what = a.IsTrader ? a.Cargo : a.Loot;
                    var looted = false;
                    // pirates still standing won: the strongest takes the loot, and its crew may haul the wreck home later
                    var thief = a.IsTrader ? pirates.Where(p => p.Hull >= Config.PirateBreak).OrderByDescending(p => p.Strength).FirstOrDefault() : null;
                    if (a.IsTrader) { Count("sys|" + a.System + "|kill"); Tally(a, "g.lost", Value(a.Cargo, a.CargoAmt)); if (HubOf(thief) is SimHub kh) { Count("hub|" + kh.Id + "|kills"); Count("hunt|" + kh.Id + "|" + a.System); } }
                    if (thief != null && left > 0)
                    {
                        var take = (int)Math.Round(left * Between(rng, Config.LootMin, Config.LootMax));
                        if (HubOf(thief) is SimHub th) Count("hub|" + th.Id + "|taken", Value(what, take));
                        thief.AddLoot(what, take); thief.LootUntil = t + Config.FenceSeconds;
                        left -= take; looted = take > 0;
                    }
                    if (Config.MoneyFlow && thief != null && a.IsTrader && Cash(a) > 0)
                    {   // the purse too
                        var grab = (int)Math.Round(Cash(a) * Config.PurseGrab * Between(rng, 0.7, 1.3));
                        AddCash(a, -grab); AddCash(thief, grab);
                        if (HubOf(thief) is SimHub ch) Count("hub|" + ch.Id + "|taken", grab);
                    }
                    if (f.HasPos && rng.NextDouble() < Config.WreckChance) Wreck(a, f.Pos, what, left, t, rng, looted, HubOf(thief)?.Id ?? "");
                    if (a.IsTrader) { a.Cargo = ""; a.CargoAmt = 0; } else a.Hold.Clear();
                    if (Config.MoneyFlow) { Burn("lost with ships", Cash0(a)); a.Extra["cash"] = "0"; }   // what didn't make it into a wreck
                }
                foreach (var a in traders.Where(a => a.Alive))
                    if (Config.SmartFlee ? !StaysToFight(a, traders, pirates.Where(p => p.Alive).ToList()) && rng.NextDouble() < FleeOdds(a)
                                         : rng.NextDouble() < (a.Strength < Config.Unarmed ? Config.FleeChance : Config.EscapeChance + (a.Hull < 0.6 ? Config.EscapeHurt : 0)))
                    {
                        LeaveFight(a, t);
                        if (a.Hull < 0.7) Emit(t, a, "limping", $"{a.Name} broke away from {killers}, limping — hull {a.Hull:P0}");
                        else Emit(t, a, "escaped", $"{a.Name} got away from {killers} near {a.System}");
                    }
                foreach (var p in pirates.Where(a => a.Alive && a.Hull < Config.PirateBreak))
                {
                    // hurt at its own hub: patches up right there, at home on guard (the repair step routes it)
                    LeaveFight(p, t); p.State = "repair"; p.Until = HubOf(p) is SimHub hh && hh.System == p.System ? t : t + Config.RepairSeconds;
                    Emit(t, p, "pirate-hurt", $"{p.Name} limps off to lick its wounds");
                }
            }
            f.Next = t + Config.RoundSeconds;
            EndIfOver(f, t);
        }

        /// <summary>
        /// A trader's call each round: armed, above <see cref="SimConfig.BailHull"/>, and the pirates would break off (expected
        /// damage, this round's hulls) before it goes down → it stays and shoots. Anything else: it runs.
        /// </summary>
        bool StaysToFight(SimAgent a, List<SimAgent> traders, List<SimAgent> pirates)
        {
            if (a.Strength < Config.Unarmed || a.Hull < Config.BailHull || pirates.Count == 0) return false;
            double Power(IEnumerable<SimAgent> side) => side.Sum(x => x.Strength * Math.Max(0.1, x.Hull) + Config.EscortStrength * Escort(x));
            var alive = traders.Where(x => x.Alive).ToList();
            var taken = Power(pirates) * Config.RoundHit / Math.Max(1, alive.Count) / Math.Max(1.0, a.Strength);
            var dealt = Power(alive) * Config.RoundHit * Config.TraderFire / pirates.Count;
            if (dealt <= 0) return false;
            var pirateRounds = pirates.Max(p => Math.Max(0, p.Hull - Config.PirateBreak) / (dealt / Math.Max(1.0, p.Strength)));
            var ownRounds = taken <= 0 ? double.MaxValue : (a.Hull - Config.BailHull) / taken;
            return pirateRounds < ownRounds;
        }

        /// <summary>Escort hull (0 = none) — Extra "esc", 1 when fresh.</summary>
        public static double Escort(SimAgent a) => a != null && a.Extra.TryGetValue("esc", out var e) && double.TryParse(e, NumberStyles.Float, CultureInfo.InvariantCulture, out var h) ? h : 0;

        /// <summary>Per round, running: base odds × speed vs <see cref="SimConfig.FleeRefSpeed"/> (a courier outruns a bulk hauler), more when hurt.</summary>
        double FleeOdds(SimAgent a) => Config.FleeChance * Math.Max(0.6, Math.Min(1.5, a.Speed / Config.FleeRefSpeed)) + (a.Hull < 0.6 ? Config.EscapeHurt : 0);

        /// <summary>Undocking: loaded with its line's cargo (by chance), or flying empty.</summary>
        public void Load(SimAgent a, Random rng)
        {
            if (a.Role.Length == 0) a.Role = Roles[rng.Next(Roles.Length)].Id;
            var r = RoleOf(a);
            if (rng.NextDouble() < r.Loaded)
            {
                if (r.Rare.Length > 0 && rng.NextDouble() < r.RareChance) { a.Cargo = r.Rare; a.CargoAmt = 1; }
                else
                {
                    a.Cargo = r.Items[rng.Next(r.Items.Length)];
                    var price = Prices.TryGetValue(a.Cargo, out var pr) && pr > 0 ? pr : 50;
                    a.CargoAmt = Math.Max(1, (int)Math.Round(Between(rng, r.ValueMin, r.ValueMax) / price));
                }
            }
            else { a.Cargo = ""; a.CargoAmt = 0; }
            Restock(a, rng);   // after the cargo: stores never repeat it
        }

        // ---- ship's stores + what a trader buys (hail → Trade) -----------------------------------------------

        /// <summary>Small useful things every trader sells besides its cargo.</summary>
        public static readonly string[] StoreItems = { "OxygenBottleLarge", "HydrogenBottle", "Medikit01", "CannedVegetables", "CannedMeat", "EnergyCell", "EnergyCellLarge", "FusionCell" };

        /// <summary>New stores for this trip: 2–3 kinds, ~1.5–4k cr worth each (saved in Extra "stores" = "Item:n,Item:n").</summary>
        public void Restock(SimAgent a, Random rng)
        {
            var pool = StoreItems.Where(x => x != a.Cargo).OrderBy(x => rng.Next()).Take(2 + rng.Next(2)).ToList();
            var parts = new List<string>();
            foreach (var item in pool)
            {
                var price = Prices.TryGetValue(item, out var p) && p > 0 ? p : 50;
                var n = Math.Max(1, Math.Min(40, (int)Math.Round(Between(rng, 1500, 4000) / price)));
                parts.Add(item + ":" + n.ToString(CultureInfo.InvariantCulture));
            }
            a.Extra["stores"] = string.Join(",", parts);
            if (!Config.MoneyFlow || !a.Extra.ContainsKey("cash"))   // money flows: the purse is earned and lost, not re-rolled
            {
                if (Config.MoneyFlow) Burn("purse reset", Cash0(a));
                var roll = (int)Math.Round(Between(rng, 0.6, 1.4) * CashBase(a));
                a.Extra["cash"] = roll.ToString(CultureInfo.InvariantCulture); Mint("new purses", roll);
            }
            if (rng.NextDouble() < RequestChance)
            {
                var r = Requests[rng.Next(Requests.Length)];
                var n = r.Min + rng.Next(r.Max - r.Min + 1);
                var price = Math.Round((Prices.TryGetValue(r.Item, out var bp) ? bp : 100) * Between(rng, 2.0, 3.0));
                a.Extra["want"] = $"{r.Item}:{n.ToString(CultureInfo.InvariantCulture)}:{price.ToString("0", CultureInfo.InvariantCulture)}";
            }
            else a.Extra.Remove("want");
        }

        /// <summary>A trader's purse per trip: it won't buy more than it can pay for (bigger ships, deeper pockets).</summary>
        static double CashBase(SimAgent a) => !a.IsTrader ? 0 : a.Role == "bulk" ? 25000 : a.Role == "courier" ? 15000 : 10000;

        /// <summary>Credits it has left to buy with this trip (made up for one that has none yet).</summary>
        /// <summary>The purse without creating one (0 if none yet).</summary>
        static int Cash0(SimAgent a) => a != null && a.Extra.TryGetValue("cash", out var c) && int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? Math.Max(0, n) : 0;

        public int Cash(SimAgent a)
        {
            if (a == null) return 0;
            if (!a.Extra.TryGetValue("cash", out var c) || !int.TryParse(c, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n))
            { n = (int)CashBase(a); a.Extra["cash"] = n.ToString(CultureInfo.InvariantCulture); Mint("new purses", n); }
            return Math.Max(0, n);
        }

        public void AddCash(SimAgent a, int delta) { if (a != null) a.Extra["cash"] = Math.Max(0, Cash(a) + delta).ToString(CultureInfo.InvariantCulture); }

        /// <summary>What's left of a trader's stores (made up on the spot for one that has none yet).</summary>
        public List<KeyValuePair<string, int>> Stores(SimAgent a)
        {
            if (a == null || !a.IsTrader) return new List<KeyValuePair<string, int>>();
            if (!a.Extra.ContainsKey("stores")) Restock(a, new Random((int)(Fnv("stores|" + a.Id + "|" + a.Since.ToString("R", CultureInfo.InvariantCulture)) & 0x7FFFFFFF)));
            return a.Extra["stores"].Split(',').Select(x => x.Split(':')).Where(x => x.Length == 2 && int.TryParse(x[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n > 0)
                    .Select(x => new KeyValuePair<string, int>(x[0], int.Parse(x[1], CultureInfo.InvariantCulture))).ToList();
        }

        /// <summary>A player bought <paramref name="n"/> of <paramref name="item"/>: out of the cargo first, then the stores.</summary>
        public void Sell(SimAgent a, string item, int n)
        {
            if (a == null || n <= 0) return;
            if (item == a.Cargo) { var c = Math.Min(n, a.CargoAmt); a.CargoAmt -= c; n -= c; if (a.CargoAmt <= 0) { a.Cargo = ""; a.CargoAmt = 0; } }
            if (n <= 0) return;
            var st = Stores(a).Select(kv => kv.Key == item ? new KeyValuePair<string, int>(kv.Key, Math.Max(0, kv.Value - n)) : kv).Where(kv => kv.Value > 0);
            a.Extra["stores"] = string.Join(",", st.Select(kv => kv.Key + ":" + kv.Value.ToString(CultureInfo.InvariantCulture)));
        }

        /// <summary>What a trader sells an item for (1.25× base).</summary>
        public static double SellPrice(string item) => Math.Max(1, Math.Round((Prices.TryGetValue(item ?? "", out var p) ? p : 0) * 1.25));

        /// <summary>What a trader pays for an item: its own line of work 0.8× base, anything else with a known price 0.5×; 0 = won't buy.</summary>
        public static double BuyPrice(SimAgent a, string item)
        {
            if (!Prices.TryGetValue(item ?? "", out var p) || p <= 0) return 0;
            return Math.Round(p * (RoleOf(a).Items.Contains(item) ? 0.8 : 0.5));
        }

        /// <summary>
        /// A wreck where a ship died near players (far away it's just news). At most <see cref="SimConfig.WrecksPerSystem"/>
        /// beacons per system: a new one tows the oldest unspawned one away.
        /// </summary>
        public SimWreck Wreck(SimAgent a, Vec3 pos, string cargo, int amt, double t, Random rng, bool looted = false, string by = "")
        {
            if (Focus.Count > 0 && !Focus.Contains(a.System)) return null;
            // a spawned hull past its beacon time doesn't hold a slot: it may be orphaned in an unloaded playfield until a
            // player returns (then the PfServer clears it) — two of those blocked a system from ever getting wrecks again.
            // Hub wrecks (hauled home) have caps of their own.
            var here = Wrecks.Values.Where(w => w.System == a.System && !w.IsHub && !(w.Entity != 0 && w.Until <= t)).OrderBy(w => w.Time).ToList();
            if (here.Count >= Config.WrecksPerSystem)
            {
                var old = here.FirstOrDefault(w => w.Entity == 0);
                if (old == null) return null;
                Wrecks.Remove(old.Id); Burn("wrecks bumped", old.Cash);
            }
            // drift off the kill spot (100–300 m), and never into another hull (two kills at one ambush spot would put one
            // wreck inside the other)
            for (var tries = 0; tries < 12; tries++)
            {
                var off = LaneDir("wreck|" + a.Id + "|" + t.ToString("R", CultureInfo.InvariantCulture) + "|" + tries.ToString(CultureInfo.InvariantCulture)) * (float)Between(rng, 100, 300);
                var p = pos + off;
                if (Wrecks.Values.All(w => w.System != a.System || (w.Pos - p).Length >= 150)) { pos = p; break; }
            }
            var id = NewWreckId();
            var wr = new SimWreck
            {
                Id = id, System = a.System, Name = a.Name, Kind = a.Kind, Role = a.IsTrader ? a.Role : "pirate", Cargo = amt > 0 ? cargo : "", Amt = Math.Max(0, amt),
                Pos = pos, Time = t, Until = t + Between(rng, Config.WreckMin, Config.WreckMax), Ship = ShipOf(a), Looted = looted, By = by ?? "",
                Cash = Config.MoneyFlow ? (a.IsTrader ? Cash(a) / 2 : Cash(a)) : 0,   // a pirate's pocket; half a trader's purse survives
            };
            if (Config.MoneyFlow) AddCash(a, -wr.Cash);   // moved into the wreck (not lost)
            Wrecks[id] = wr;
            Emit(t, a, "wreck", $"Distress beacon: {a.Name} went down near {a.System}" + (wr.Amt > 0 ? $" — {wr.Amt} {Humanize(wr.Cargo)} aboard" : ""));
            return wr;
        }

        string NewWreckId()
        {
            var id = "w1";
            for (var n = 1; Wrecks.ContainsKey(id); n++) id = "w" + n.ToString(CultureInfo.InvariantCulture);
            return id;
        }

        /// <summary>
        /// Nobody came for the wreck before its beacon died: the crew that won drags it home — picked over, parked
        /// 300–800 m from the hub's centre (past the crews' spots), kept for good (no beacon). Then the hub's caps.
        /// </summary>
        public void Haul(SimWreck w, SimHub hub, double t)
        {
            ParkAtHub(w, hub, t);
            Scatter(hub, w.Pos, "haul|" + w.Id + "|" + w.Name, 1, t);   // a bit torn off in the tow
            Count("hub|" + hub.Id + "|hauled");
            var boss = Agents.Values.Where(x => !x.IsTrader && x.Alive && x.Home == hub.Id).OrderBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
            Events.Add(new SimEvent { Time = t, Agent = "", Name = w.Name, System = hub.System, Kind = "hauled",
                Text = $"{(boss != null ? boss.Name + "'s lot" : "Pirates")} dragged the {w.Name} back to {hub.Name}" });
            HubCaps(hub, t);
        }

        /// <summary>A hub wreck's spot and state: in the pile by the hub, picked over, kept (no beacon).</summary>
        void ParkAtHub(SimWreck w, SimHub hub, double t)
        {
            for (var tries = 0; tries < 16; tries++)
            {
                // a pile, not a scatter: 120–370 m from the hub's centre (crews park 80–250 m), ~110 m apart (the PfServer
                // still nudges the final spot clear of any hull or asteroid it can see)
                var p = hub.Pos + LaneDir("hubwreck|" + w.Id + "|" + w.Name + "|" + tries.ToString(CultureInfo.InvariantCulture)) * (float)(120 + Fnv("hubwr|" + w.Id + "|" + tries.ToString(CultureInfo.InvariantCulture)) % 250);
                w.Pos = p;
                if (Wrecks.Values.All(x => x == w || x.System != hub.System || (x.Pos - p).Length >= 110)) break;
            }
            w.System = hub.System; w.Hub = hub.Id; w.Looted = true; w.Time = t; w.Until = Never; w.Shown = "";
        }

        /// <summary>
        /// The last <see cref="SimConfig.HubWrecks"/> hauled wrecks stay lootable; older ones become debris (contents gone),
        /// debris over <see cref="SimConfig.HubDebris"/> goes (oldest first). A spawned hull is swapped by the PfServer
        /// while nobody's near (<see cref="SimWreck.NeedsSwap"/>); an unspawned one just changes on paper.
        /// </summary>
        public void HubCaps(SimHub hub, double t)
        {
            var loot = Wrecks.Values.Where(x => x.Hub == hub.Id && !x.Centre && !x.Debris && !x.Gone).OrderBy(x => x.Time).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
            foreach (var w in loot.Take(Math.Max(0, loot.Count - Config.HubWrecks))) { w.Debris = true; w.Cargo = ""; w.Amt = 0; w.Time = t; Burn("wrecks to debris", w.Cash); w.Cash = 0; }
            var debris = Wrecks.Values.Where(x => x.Hub == hub.Id && x.Debris && !x.Decor && !x.Gone).OrderBy(x => x.Time).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
            foreach (var w in debris.Take(Math.Max(0, debris.Count - Config.HubDebris)))
                if (w.Entity == 0) Wrecks.Remove(w.Id); else w.Gone = true;
            // scenery has its own cap: the oldest junk goes (a spawned piece waits until nobody's near)
            var junk = Wrecks.Values.Where(x => x.Hub == hub.Id && x.Decor && !x.Gone).OrderBy(x => x.Time).ThenBy(x => x.Id, StringComparer.Ordinal).ToList();
            foreach (var w in junk.Take(Math.Max(0, junk.Count - HubDecorMax)))
                if (w.Entity == 0) Wrecks.Remove(w.Id); else w.Gone = true;
        }

        /// <summary>A hub wreck's hull is down (swapped / vanished): gone for good if it was over the cap, else it respawns
        /// as what it should be next time a player comes near.</summary>
        public void HubHullDown(SimWreck w, bool lost = false)
        {
            if (w.Gone) { Wrecks.Remove(w.Id); return; }
            // vanished on its own (nobody near): it may come back — as stripped debris only, never with its cargo again
            // (a respawn with the original hold was a loot farm: scrap it, leave, come back)
            if (lost && !w.Centre) { w.Debris = true; w.Cargo = ""; w.Amt = 0; Burn("wrecks to debris", w.Cash); w.Cash = 0; }
            w.Entity = 0; w.Shown = "";
        }

        /// <summary>A crew home with loot: into the hub's stash (fenced oldest-first over the cap).</summary>
        public void Stash(SimAgent p, SimHub hub, double t)
        {
            if (IsBroken(hub)) return;
            if (Config.MoneyFlow && Cash(p) > 0)
            {   // cash goes in whole — no fence's cut
                var c = Cash(p); AddCash(p, -c);
                if (Config.HubWealth) hub.Cash += c; else Burn("pirate cash", c);
                Count("hub|" + hub.Id + "|banked", c);
            }
            if (p.LootAmt <= 0) return;
            foreach (var h in p.Hold) hub.Stash.Add(new SimStash { Item = h.Item, Amt = h.Amt, Time = t });
            Count("hub|" + hub.Id + "|stashed", p.Hold.Sum(x => Value(x.Item, x.Amt)));
            Emit(t, p, "stash", $"{p.Name} brought {string.Join(", ", p.Hold.Select(x => $"{x.Amt} {Humanize(x.Item)}"))} home to {hub.Name}");
            p.Hold.Clear();
            while (hub.Stash.Count(x => x.Item != Strongbox) > 1 && StashValue(hub) > Config.StashCap)
            {
                var old = hub.Stash.First(x => x.Item != Strongbox); hub.Stash.Remove(old);
                Count("hub|" + hub.Id + "|fenced", Value(old.Item, old.Amt));
                if (Config.HubWealth) { hub.Cash += Value(old.Item, old.Amt) * Config.FenceShare; Mint("fencing", Value(old.Item, old.Amt) * Config.FenceShare); }
                Events.Add(new SimEvent { Time = t, Agent = "", Name = hub.Name, System = hub.System, Kind = "fenced", Text = $"{hub.Name} fenced {old.Amt} {Humanize(old.Item)}" });
            }
        }

        public static double StashValue(SimHub hub) => hub.Stash.Where(s => s.Item != Strongbox).Sum(s => s.Amt * (Prices.TryGetValue(s.Item, out var v) ? v : 1));
        /// <summary>Credits as an item (a stack of N = N credits): a broken gang's cash goes into its crates as this.</summary>
        public const string Strongbox = "MoneyCard";

        /// <summary>Part of the stash made it into a real box (the game keeps it from there): take it off the queue.</summary>
        public void Unstash(SimHub hub, string item, int amt)
        {
            foreach (var s in hub.Stash.Where(x => x.Item == item).ToList())
            {
                if (amt <= 0) break;
                var n = Math.Min(amt, s.Amt); s.Amt -= n; amt -= n;
                if (s.Amt <= 0) hub.Stash.Remove(s);
            }
        }

        /// <summary>"IronOre" → "Iron Ore", "Eden_TradePolarisT1" → "courier packages".</summary>
        public static string Humanize(string item)
        {
            if (string.IsNullOrEmpty(item)) return "";
            if (item.StartsWith("Eden_Trade", StringComparison.Ordinal)) return "courier packages";
            // "SiliconOre" → "Silicon Ore", "Medikit01" → "Medikit" (a variant number at the end goes), "5.56mmBullet" → "5.56mm Bullet"
            var t = System.Text.RegularExpressions.Regex.Replace(item, @"(?<=[a-z0-9])(?=[A-Z])", " ");
            t = System.Text.RegularExpressions.Regex.Replace(t, @"(?<=[A-Za-z])\d+$", "");
            return System.Text.RegularExpressions.Regex.Replace(t.Replace('_', ' '), @"\s+", " ").Trim();
        }

        IEnumerable<SimAgent> Members(SimFight f) => f.Members.Select(Get).Where(a => a != null && a.Alive && a.Fight == f.Id);

        /// <summary>One side is gone: the rest go back to what they were doing (traders that held out "fought them off").</summary>
        void EndIfOver(SimFight f, double t)
        {
            // also while live: a side that's gone is gone (the pirates left behind go back to lurking, still live)
            if (!Fights.ContainsKey(f.Id)) return;
            var left = Members(f).ToList();
            if (left.Any(a => a.IsTrader) && left.Any(a => !a.IsTrader)) return;
            foreach (var a in left)
            {
                LeaveFight(a, t);
                if (a.IsTrader) Emit(t, a, "escaped", $"{a.Name} fought off the pirates near {a.System}");
            }
            Fights.Remove(f.Id);
        }

        void Leave(SimFight f, SimAgent a) { f?.Members.Remove(a.Id); a.Fight = ""; }

        /// <summary>Out of the fight, back to the leg / the lurk it was on (the time it had left of it).</summary>
        void LeaveFight(SimAgent a, double t)
        {
            Leave(FightOf(a), a);
            if (a.State == "fight")
            {
                a.State = a.After.Length > 0 ? a.After : (a.IsTrader ? "out" : "lurk");
                a.Until = t + (a.IsTrader ? a.Resume : Math.Max(60, a.Resume));
                a.Since = t;
            }
            a.After = ""; a.Resume = 0;
            StashIfHome(a, t);
        }

        /// <summary>A crew at its own hub with loot aboard puts it in the stash — also when it won a fight right there
        /// (it never "arrives" home then; the loot used to wait in its hold until fenced).</summary>
        void StashIfHome(SimAgent a, double t)
        {
            if (!a.IsTrader && a.Alive && !a.IsLive && a.State == "home" && a.LootAmt > 0 && HubOf(a) is SimHub hub && hub.System == a.System) Stash(a, hub, t);
        }

        void LeaveFightQuiet(SimAgent a) => LeaveFight(a, LastTick);

        /// <summary>Repairs bad references (an old save's fights, a member that vanished): nobody stays stuck in a fight.</summary>
        void Tidy(double now)
        {
            foreach (var a in Agents.Values.Where(x => x.State == "fight" && FightOf(x) == null).ToList()) { a.Fight = ""; LeaveFight(a, now); }
            foreach (var a in Agents.Values.Where(x => x.State == "home" && x.LootAmt > 0).ToList()) StashIfHome(a, now);   // older saves: loot left in the hold at home
            foreach (var f in Fights.Values.ToList())
            {
                f.Members.RemoveAll(id => { var a = Get(id); return a == null || !a.Alive || a.Fight != f.Id; });
                if (f.Members.Count == 0) { Fights.Remove(f.Id); continue; }
                if (f.IsLive && !Members(f).Any(a => a.IsLive)) { f.Live = ""; if (f.Next < now) f.Next = now; }
                EndIfOver(f, now);
            }
        }

        string NextFightId()
        {
            for (var n = 1; ; n++) { var id = "e" + n.ToString(CultureInfo.InvariantCulture); if (!Fights.ContainsKey(id)) return id; }
        }

        /// <summary>
        /// A pirate's ambush spot (picked once per visit): on a lane out of one of the system's stations, out of the
        /// stations' guns. Some pirates pick a spot next to another one (a pack). False if the system has no stations.
        /// </summary>
        public bool SpotOf(SimAgent p, out Vec3 at)
        {
            at = default(Vec3);
            if (p != null && !p.IsTrader && p.State == "home" && HubOf(p) is SimHub hub && hub.System == p.System)
            {   // chilling at the hub: each crew parked at its own spot 150–330 m from the hub's centre (its listening post)
                at = hub.Pos + LaneDir("hubspot|" + p.Id) * (float)(150 + Fnv("hubr|" + p.Id) % 180);   // round the centrepiece, clear of it
                return true;
            }
            if (p == null || p.IsTrader || !Stations.TryGetValue(p.System, out var list) || list.Count == 0) return false;
            if (p.SpotStation == 0 || list.All(x => x.Id != p.SpotStation))
            {
                var h = Fnv(p.Id + "|" + p.System + "|" + p.Since.ToString("R", CultureInfo.InvariantCulture));
                var mate = Agents.Values.Where(x => x != p && !x.IsTrader && x.Alive && x.System == p.System && x.SpotStation != 0 && list.Any(s => s.Id == x.SpotStation))
                                 .OrderBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
                if (mate != null && h % 10 < 3)
                { p.SpotStation = mate.SpotStation; p.SpotOther = mate.SpotOther; p.SpotFrac = Math.Min(Config.SpotMax, mate.SpotFrac + 0.03); }
                else
                {
                    p.SpotStation = list[(int)(h % (uint)list.Count)].Id;
                    // a lane traders actually fly: picked the way a trader picks its next stop (a random lane is mostly a
                    // dead one)
                    p.SpotOther = TraderDest(new Random((int)(h & 0x7FFFFFFF)), p.System);
                    p.SpotFrac = Config.SpotMin + (h / 101 % 1000) / 1000.0 * (Config.SpotMax - Config.SpotMin);
                }
            }
            at = Spot(p);
            return true;
        }

        Vec3 Spot(SimAgent p)
        {
            var s = Stations.TryGetValue(p.System, out var list) ? list.FirstOrDefault(x => x.Id == p.SpotStation) : null;
            if (s == null) return default(Vec3);
            Lane(s, p.SpotOther, out var near, out var far);
            return near + (far - near) * (float)p.SpotFrac;
        }

        // ---- live (stage 3) --------------------------------------------------------------------

        /// <summary>
        /// The live copy is gone; the sim takes the agent back. Outcome: "left" (out of the players' bubble / reached its
        /// point — not shown again this leg), "dead" (killed in a live fight), "fled" (a pirate that broke off, hurt),
        /// "reset" (playfield unloaded / reloaded — may show again).
        /// </summary>
        public void EndLive(string id, string outcome, double now, double frac = -1, double hull = -1)
        {
            var a = Get(id);
            if (a == null || !a.IsLive) return;
            var where = a.Live;
            a.Live = "";
            if (hull >= 0 && hull < 1) a.Hull = Math.Max(0.05, a.Hull * hull);   // blocks lost live
            var f = FightOf(a);
            switch (outcome)
            {
                case "dead":
                    Leave(f, a);
                    a.State = "dead"; a.Until = a.IsTrader ? now + Config.ReplaceSeconds : Never; a.Hull = 0; a.After = "";
                    Emit(now, a, "destroyed", a.IsTrader ? $"{a.Name} was destroyed near {where}" : $"{a.Name} (pirate) was destroyed near {where}");
                    break;
                case "fled":
                    LeaveFight(a, now);
                    a.Hull = Math.Max(0.05, a.Hull - 0.4); a.Shown = true;
                    // hub crews head home to patch up right away (the repair step routes them); drifters patch up in place
                    if (!a.IsTrader && a.Alive) { a.State = "repair"; a.Until = HubOf(a) != null ? now : now + Config.RepairSeconds; a.After = ""; }
                    break;
                case "left":
                    a.Shown = true;
                    // the live ship went its own pace (or was held up): the leg goes on from where it really was
                    if ((a.OnLeg || a.State == "pass") && frac >= 0 && frac <= 1) a.Until = now + (1 - frac) * LegSeconds(a);
                    if (a.State == "fight" && a.IsTrader && frac >= 0 && frac <= 1) a.Resume = (1 - frac) * LegSeconds(a);
                    break;
                case "arrived":   // reached the end of its lane live (out of any fight it was in): the leg is over now
                    LeaveFight(a, now);
                    a.Until = now;
                    break;
                case "jumped":   // seen warping out live: it really left the system (a pirate whose prey is gone, a trader that fled)
                    Leave(f, a);
                    if (a.IsTrader)
                    {   // on to somewhere it trades — its cargo still aboard; it arrives and docks like any trip
                        var dest = TraderDest(new Random((int)(Fnv("fled|" + a.Id + "|" + now.ToString("R", CultureInfo.InvariantCulture)) & 0x7FFFFFFF)), a.System);
                        a.Dest = dest != a.System ? dest : PickOther(new Random(), a.System);
                        a.Extra.Remove("via"); a.StationId = 0;
                    }
                    else a.Dest = PirateDest(new Random(), a.System);
                    a.State = "warp"; a.Until = now + WarpTime(a.System, a.Dest); a.After = ""; a.Resume = 0;
                    break;
                case "claimed":   // a player cored it: it's theirs now — the agent is gone, and we never touch that entity again
                    Leave(f, a);
                    a.State = "dead"; a.Until = a.IsTrader ? now + Config.ReplaceSeconds : Never; a.EntityId = 0; a.After = "";
                    Emit(now, a, "claimed", $"{a.Name} was taken over near {where}");
                    break;
            }
            if (a.Alive && a.Until < now) a.Until = now;   // its timer ran out while live: catch up on the next step
            // the fight it was in: back to the sim once nobody in it is live; over if a side is gone
            if (f != null && Fights.ContainsKey(f.Id))
            {
                if (!Members(f).Any(m => m.IsLive)) { f.Live = ""; if (f.Next < now) f.Next = now + Config.RoundSeconds; }
                EndIfOver(f, now);
            }
        }

        /// <summary>A fight a player came near: it and everyone in it go live in <paramref name="playfield"/>. Returns the members.</summary>
        public List<SimAgent> GoLive(SimFight f, string playfield)
        {
            f.Live = playfield;
            var list = Members(f).ToList();
            foreach (var a in list) a.Live = playfield;
            return list;
        }

        /// <summary>A live trader just reached a pirate (live or not) on its lane: a fight starts, live.</summary>
        public SimFight StartLiveFight(Pass pass, Random rng)
        {
            var f = StartFight(pass.Trader, pass.Pirate, pass.Time, rng);
            f.Live = pass.Trader.Live;
            foreach (var a in Members(f)) a.Live = f.Live;
            return f;
        }

        public List<SimAgent> MembersOf(SimFight f) => Members(f).ToList();

        /// <summary>
        /// Test switch (/ls sim here): bring a pirate to lurk in <paramref name="system"/> and start a trader flying in there.
        /// Picks agents that are idle in the sim (not live, not fighting, alive). Returns what it did.
        /// </summary>
        public string StageHere(string system, double now, Random rng)
        {
            bool Free(SimAgent a) => a.Alive && !a.IsLive && a.State != "fight";
            var p = Agents.Values.Where(a => !a.IsTrader && Free(a)).OrderBy(a => a.System == system ? 0 : 1).ThenBy(a => a.Id, StringComparer.Ordinal).FirstOrDefault();
            var t = Agents.Values.Where(a => a.IsTrader && Free(a)).OrderBy(a => a.Hull < 0.95 ? 1 : 0).ThenBy(a => a.Id, StringComparer.Ordinal).FirstOrDefault();
            if (p == null || t == null) return "no free pirate or trader";
            LeaveFightQuiet(p); LeaveFightQuiet(t);
            p.System = system; p.State = "lurk"; p.Dest = ""; p.Until = now + Config.LurkMax; p.Hull = Math.Max(p.Hull, 0.6); p.Since = now;
            t.System = system; t.Dest = TraderDest(rng, system); StartLeg(t, "out", now);
            if (StationOf(t) is SimStation st)
            {   // the pirate waits on the trader's lane, ~4 km out from the station
                p.SpotStation = st.Id; p.SpotOther = t.Dest; p.SpotFrac = 0.3;
            }
            return $"{p.Name} lurks in {system}; {t.Name} heading out through it";
        }

        // ---- positions (anchors + lanes) -----------------------------------------------------------

        /// <summary>Replace the stations known in <paramref name="system"/>. True if anything changed.</summary>
        public bool SetStations(string system, List<SimStation> list)
        {
            list = (list ?? new List<SimStation>()).OrderBy(s => s.Id).ToList();
            if (Stations.TryGetValue(system, out var old) && old.Count == list.Count &&
                old.Zip(list, (x, y) => x.Id == y.Id && x.Name == y.Name && (x.Pos - y.Pos).Length < 50 && x.HasBox == y.HasBox && (x.Box - y.Box).Length < 20).All(b => b))
                return false;
            if (list.Count == 0) Stations.Remove(system); else Stations[system] = list;
            return true;
        }

        /// <summary>The station a trader is tied to in its current system (picked once per visit, stable); null = none known.</summary>
        public SimStation StationOf(SimAgent a)
        {
            if (a == null || !Stations.TryGetValue(a.System, out var list) || list.Count == 0) return null;
            var s = list.FirstOrDefault(x => x.Id == a.StationId);
            if (s != null) return s;
            s = list[(int)(Fnv(a.Id + "|" + a.From + "|" + a.System) % (uint)list.Count)];
            a.StationId = s.Id;
            return s;
        }

        /// <summary>
        /// The lane between a station and another system: a fixed direction per (station, system) pair, so the same lanes
        /// stay busy and a player can learn them. Near = the station's approach point, Far = the warp point.
        /// </summary>
        public void Lane(SimStation s, string other, out Vec3 near, out Vec3 far)
        {
            // toward the destination's star when the map knows both (learnable routes); else a stable hashed direction
            var dir = (Galaxy != null && SystemOf(s) is string sys ? Galaxy.Heading(sys, other) : null) ?? LaneDir(s.Name + "|" + other);
            // the berth: just outside the measured hull along the lane (every station type, its own shape); else 1 km out
            near = s.HasBox ? s.Box + dir * (ExitDistance(s, dir) + Config.BerthMargin) : s.Pos + dir * Config.StationClear;
            far = near + dir * Config.LegMetres;
        }

        /// <summary>
        /// A trader in warp toward <see cref="SimAgent.Dest"/>: where it will drop out (the warp point of the inbound lane
        /// to the station it'll dock at — same pick as on arrival) and which station. False if no station there.
        /// </summary>
        public bool Arrival(SimAgent a, out Vec3 warpPoint, out SimStation station)
        {
            warpPoint = default(Vec3); station = null;
            if (a == null || !a.IsTrader || a.State != "warp" || a.Dest.Length == 0) return false;
            if (!Stations.TryGetValue(a.Dest, out var list) || list.Count == 0) return false;
            // the hash StationOf will use once it's there (From = here, System = there)
            station = list[(int)(Fnv(a.Id + "|" + a.System + "|" + a.Dest) % (uint)list.Count)];
            Lane(station, a.System, out _, out warpPoint);
            return true;
        }

        /// <summary>Distance from the box centre to where a ray along <paramref name="dir"/> leaves the station's box.</summary>
        static float ExitDistance(SimStation s, Vec3 dir)
        {
            var l = s.Rot.Inverse * dir;
            float T(float h, float d) => Math.Abs(d) < 1e-4f ? float.MaxValue : h / Math.Abs(d);
            return Math.Min(T(s.Half.X, l.X), Math.Min(T(s.Half.Y, l.Y), T(s.Half.Z, l.Z)));
        }

        /// <summary>A ship's own parking slot at a berth: side by side across the lane, so parked ships don't stack.</summary>
        Vec3 Slot(SimAgent a, Vec3 berth, Vec3 laneDir)
        {
            var side = Vec3.Cross(laneDir.Normalized, Vec3.Up).Normalized;
            if (side.Length < 0.5f) side = Vec3.Right;
            var k = (int)(Fnv(a.Id) % 5) - 2;   // -2…2
            return berth + side * (k * Config.BerthSpacing);
        }

        /// <summary>
        /// A live inbound trader reached its berth while a player watched: it's docked now, and stays real, parked —
        /// the sim undocks it when its time is up (the PfServer flies it out). True if it was inbound.
        /// </summary>
        public bool DockLive(string id, Random rng, double now)
        {
            var a = Get(id);
            if (a == null || !a.IsLive || !a.IsTrader || (a.State != "in" && !(a.State == "fight" && a.After == "in"))) return false;
            LeaveFightQuiet(a);
            a.State = "dock"; a.Until = now + Between(rng, Config.DockMin, Config.DockMax) + YardTime(a); a.Met = ""; a.Since = now;
            return true;
        }

        /// <summary>Unit direction from a stable hash: any yaw, pitched at most ±10° (lanes stay roughly in the plane).</summary>
        public static Vec3 LaneDir(string key)
        {
            var h = Fnv(key);
            var yaw = (h % 3600) / 10.0 * Math.PI / 180;
            var pitch = ((h / 3600) % 201 - 100) / 10.0 * Math.PI / 180;
            return new Vec3((float)(Math.Sin(yaw) * Math.Cos(pitch)), (float)Math.Sin(pitch), (float)(Math.Cos(yaw) * Math.Cos(pitch)));
        }

        /// <summary>
        /// Where a trader is this leg (see <see cref="SimTrack"/>). False if it has no place: not a trader, dead, in warp,
        /// or no station known in its system. Out: approach point → warp point; in: warp point → approach point.
        /// </summary>
        public bool Track(SimAgent a, out SimTrack tr)
        {
            tr = default(SimTrack);
            if (a == null || !a.Alive) return false;
            if (!a.IsTrader)
            {   // pirates: their spot while lurking, the fight's place while fighting
                var pf = FightOf(a);
                if (a.State == "fight" && pf != null && pf.HasPos) { tr.A = tr.B = tr.Dest = pf.Pos; tr.T0 = tr.T1 = pf.Next; return true; }
                // T0 = when it got there (the PfServer flies a crew in only if that was moments ago), T1 = when it leaves
                if ((a.State == "lurk" || a.State == "home") && SpotOf(a, out var spot)) { tr.A = tr.B = tr.Dest = spot; tr.T0 = Math.Min(a.Since, a.Until); tr.T1 = a.Until; return true; }
                return false;
            }
            var leg = a.State == "fight" ? a.After : a.State;
            if (leg == "pass")
            {   // crossing a system: a straight line, heading for the destination's star (see PassLine)
                PassLine(a, out var pa, out var pb);
                tr.Dest = pb;
                if (a.State == "fight")
                {
                    var pff = FightOf(a);
                    var pp = pff != null && pff.HasPos ? pff.Pos : pa + (pb - pa) * (float)Math.Max(0, Math.Min(1, 1 - a.Resume / LegSeconds(a)));
                    tr.A = tr.B = pp; tr.T0 = tr.T1 = pff?.Next ?? a.Until; return true;
                }
                tr.A = pa; tr.B = pb; tr.T1 = a.Until; tr.T0 = a.Until - LegSeconds(a);
                return true;
            }
            if (leg != "out" && leg != "in" && leg != "dock") return false;
            var s = StationOf(a);
            if (s == null) return false;
            tr.Station = s;
            if (leg == "dock")
            {   // parked at its berth on the lane it came in on (or leaves on)
                Lane(s, a.From.Length > 0 ? a.From : a.Dest, out var bn, out var bf);
                tr.A = tr.B = tr.Dest = Slot(a, bn, bf - bn); tr.T0 = Math.Min(a.Since, a.Until); tr.T1 = a.Until; return true;   // T0 = docked at (how far through the stay)
            }
            Lane(s, leg == "out" ? a.Dest : a.From, out var near, out var far);
            if (leg == "in") near = Slot(a, near, far - near);   // inbound: straight to its own slot at the berth
            Vec3 from = leg == "out" ? near : far, to = leg == "out" ? far : near;
            tr.Dest = to;
            var dur = LegSeconds(a);
            if (a.State == "fight")
            {   // held up mid-lane: sits where the fight is
                var ff = FightOf(a);
                var p = ff != null && ff.HasPos ? ff.Pos : from + (to - from) * (float)Math.Max(0, Math.Min(1, 1 - a.Resume / dur));
                tr.A = tr.B = p; tr.T0 = tr.T1 = ff?.Next ?? a.Until; return true;
            }
            tr.A = from; tr.B = to; tr.T1 = a.Until; tr.T0 = a.Until - dur;
            return true;
        }

        /// <summary>
        /// A passer's line through a stationless system. At a hub: past the hub, 300–900 m off its centre (the crews see it).
        /// Elsewhere the line is RELATIVE (around 0,0,0): the PfServer centres it on a player (<see cref="PassRelative"/>) —
        /// an empty system has no anchor, and a passer nobody sees is just a log line.
        /// </summary>
        public void PassLine(SimAgent a, out Vec3 from, out Vec3 to)
        {
            var dir = (Galaxy != null ? Galaxy.Heading(a.From, a.Dest) : null) ?? LaneDir("pass|" + a.Id);
            var side = Vec3.Cross(dir, Vec3.Up).Normalized;
            if (side.Length < 0.5f) side = Vec3.Right;
            var h = Fnv("passoff|" + a.Id + "|" + a.System);
            var hub = Hubs.Values.FirstOrDefault(x => x.System == a.System);
            // past a den: 3–6 km out (in sight of its crews, not under its guns); elsewhere 300–900 m off the player
            var off = (float)(hub != null ? 3000 + h % 3000 : 300 + h % 600) * ((h / 3000) % 2 == 0 ? 1 : -1);
            var centre = hub?.Pos ?? default(Vec3);
            from = centre - dir * (Config.PassMetres / 2) + side * off;
            to = centre + dir * (Config.PassMetres / 2) + side * off;
        }

        /// <summary>A passer's line is relative to a player (no hub in its system to pass by).</summary>
        public bool PassRelative(SimAgent a) => a != null && !Hubs.Values.Any(x => x.System == a.System);

        public static uint Fnv(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (var c in s ?? "") { h ^= c; h *= 16777619; }
                return h;
            }
        }

        // ---- routes (galaxy) ----------------------------------------------------------------------

        string SystemOf(SimStation s)
        {
            foreach (var kv in Stations) if (kv.Value.Contains(s)) return kv.Key;
            return null;
        }

        /// <summary>Route cost in light years between two network systems (+inf = unreachable). No map: 1 for any pair.</summary>
        public double RouteLy(string a, string b) =>
            Galaxy == null ? (a == b ? 0 : 1) : Galaxy.Route(a, b, Config.WarpRangeLy, Config.GateLy, out _);

        /// <summary>Warp time between two systems: base + per light year; flat <see cref="SimConfig.WarpSeconds"/> without a map.</summary>
        public double WarpTime(string a, string b)
        {
            if (Galaxy == null) return Config.WarpSeconds;
            var ly = Galaxy.Route(a, b, Config.WarpRangeLy, Config.GateLy, out _, out var hops);
            if (double.IsInfinity(ly)) { ly = Math.Min(Galaxy.Distance(a, b), 200); hops = 1; }   // a stranded ship still gets somewhere
            if (double.IsInfinity(ly)) { ly = Config.WarpRangeLy; hops = 1; }
            return Config.WarpBaseSeconds * Math.Max(1, hops) + ly * Config.WarpPerLy;
        }

        /// <summary>
        /// Where a trader goes next: a reachable system, weighted by what's there (more stations, a trading station),
        /// how far (local trade), and recent attack reports (it avoids hot systems). Nothing reachable: the nearest one.
        /// </summary>
        public static string LaneKey(int stationId, string other) => stationId.ToString(CultureInfo.InvariantCulture) + ">" + (other ?? "");

        /// <summary>Lanes with an attack reported in the last <see cref="SimConfig.ReportSeconds"/>.</summary>
        HashSet<string> HotLanes(double now) =>
            new HashSet<string>(Events.Where(e => e.Kind == "attack" && e.Lane.Length > 0 && now - e.Time <= Config.ReportSeconds).Select(e => e.Lane), StringComparer.Ordinal);

        public string TraderDest(Random rng, string from) => TraderDest(rng, from, 0);

        /// <param name="stationId">The station it leaves from: lanes out of it with a recent attack report are avoided.</param>
        public string TraderDest(Random rng, string from, int stationId)
        {
            var cands = Config.Systems.Where(x => x != from).ToList();
            if (cands.Count == 0) return from;
            if (Galaxy == null) return Pick(rng, cands);
            var starCount = Config.Systems.GroupBy(x => Galaxy.StarOf(x)?.Name ?? x).ToDictionary(x => x.Key, x => x.Count());
            var hotLanes = stationId != 0 ? HotLanes(LastTick) : null;
            var now = LastTick;
            var weights = cands.Select(c =>
            {
                var ly = RouteLy(from, c);
                if (double.IsInfinity(ly)) return 0.0;
                var st = Stations.TryGetValue(c, out var l) ? l : new List<SimStation>();
                var pull = Math.Min(4, Math.Max(1, st.Count)) + (st.Any(x => x.Name.IndexOf("Trad", StringComparison.OrdinalIgnoreCase) >= 0) ? 2 : 0);
                var hot = RiskIn(c, now) != Risk.Quiet ? 0.3 : 1.0;
                // a star with many station playfields (Epsilon: 19) shares one star's pull among them — else it swallows
                // half the galaxy's traffic
                var crowd = Math.Sqrt(Math.Max(1, starCount.TryGetValue(Galaxy.StarOf(c)?.Name ?? c, out var k) ? k : 1));
                var far = 1 + ly / 15;
                // a lane out of here with a recent attack report: traders steer clear (pirates there go hungry, move on)
                var lane = hotLanes != null && hotLanes.Contains(LaneKey(stationId, c)) ? 0.15 : 1.0;
                return pull * hot * lane / crowd / (far * far);   // squared: long hauls (the gate) stay rare, trade stays local
            }).ToList();
            if (weights.Sum() <= 0) return cands.OrderBy(c => Galaxy.Distance(from, c)).First();
            return Weighted(rng, cands, weights);
        }

        /// <summary>A pirate moves on to a reachable system with traffic (traders there or bound there).</summary>
        public string PirateDest(Random rng, string from)
        {
            var cands = Config.Systems.Where(x => x != from).ToList();
            if (cands.Count == 0) return from;
            if (Galaxy == null) return Pick(rng, cands);
            var weights = cands.Select(c =>
            {
                // pirates don't take the jump gates (guarded): each cluster keeps its own — else they all drift to the
                // busiest cluster
                var ly = Galaxy.Route(from, c, Config.WarpRangeLy, Config.GateLy, out var gate);
                if (double.IsInfinity(ly) || gate) return 0.0;
                if (Config.RaiderCap && RaidersIn(c) >= Config.MaxRaidersPerSystem) return 0.0;
                var traffic = Agents.Values.Count(x => x.IsTrader && x.Alive && (x.System == c || x.Dest == c));
                return (1 + traffic) / (1 + ly / 15);
            }).ToList();
            if (weights.Sum() <= 0) return cands.OrderBy(c => Galaxy.Distance(from, c)).First();
            return Weighted(rng, cands, weights);
        }

        static string Weighted(Random rng, List<string> items, List<double> weights)
        {
            var r = rng.NextDouble() * weights.Sum();
            for (var i = 0; i < items.Count; i++) { r -= weights[i]; if (r <= 0 && weights[i] > 0) return items[i]; }
            return items[weights.IndexOf(weights.Max())];
        }

        // ---- compact feed line (LCD) -------------------------------------------------------------

        /// <summary>LCD feed lines starting with this are pre-formatted rich text: the display must not clip them.</summary>
        public const string Formatted = "\u0001";

        /// <summary>
        /// One short, colour-coded feed line: name, what, where (short system name). Fits the LCD's ~22-character feed
        /// column instead of being cut off ("Kestrel reports pirat…").
        /// </summary>
        public string FeedLine(SimEvent e)
        {
            var a = Get(e.Agent);
            var name = Clip(a?.Name ?? (e.Name.Length > 0 ? e.Name : e.Agent), 9);
            var sys = Short(e.System);
            switch (e.Kind)
            {
                case "undock": return Line(name, "#90A4AE", "undock", sys);
                case "jump": return Line(name, "#90A4AE", "»", Short(a?.Dest.Length > 0 ? a.Dest : e.System));
                case "arrive": return Line(name, "#66BB6A", "@", sys);
                case "pass": return Line(name, "#90A4AE", "passing »", Short(a?.Dest.Length > 0 ? a.Dest : e.System));
                case "attack": return Line(name, "#EF5350", "ATTACKED", sys);
                case "escaped": return Line(name, "#66BB6A", "ESCAPED", sys);
                case "limping": return Line(name, "#FFB300", $"HIT {(a?.Hull ?? 0) * 100:0}%", sys);
                case "destroyed": return Line(name, "#EF5350", "LOST", sys);
                case "pirate-hurt": return Line(name, "#FFB300", "retreats", sys);
                case "pirate-join": return Line(name, "#EF5350", "JOINS", sys);
                case "patrol": return Line(name, "#42A5F5", "PATROL", sys);
                case "wreck": return Line(name, "#FFB300", "WRECK", sys);
                case "towed": return Line(name, "#90A4AE", "wreck towed", sys);
                case "hauled": return Line(name, "#FFB300", "hauled off", sys);
                case "new": return Line(name, "#66BB6A", "new", sys);
                default: return Formatted + Clip(e.Text, 22);
            }
        }

        static string Line(string name, string color, string what, string sys) =>
            $"{Formatted}<b>{name}</b> <color={color}>{what}</color> <color=#4F87A6>{sys}</color>";

        /// <summary>"SNUO 37444 I Sector" → "SNUO", "West Rim to Core Jump Gate" → "West", "Rogue J-5565" → "Rogue".</summary>
        public static string Short(string system)
        {
            var first = (system ?? "").Split(' ').FirstOrDefault(x => x.Length > 0) ?? "";
            return Clip(first, 6);
        }

        static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "" : (s.Length <= n ? s : s.Substring(0, n - 1) + "…");

        // ---- what traders know ----------------------------------------------------------------

        /// <summary>The risk a trader quotes for <paramref name="system"/>: attacks reported there lately (no invented numbers).</summary>
        public Risk RiskIn(string system, double now)
        {
            var n = Events.Count(e => e.System == system && e.Kind == "attack" && now - e.Time <= Config.ReportSeconds);
            return n == 0 ? Risk.Quiet : n == 1 ? Risk.Moderate : Risk.Hot;
        }

        /// <summary>One line per agent, for /ls sim.</summary>
        public IEnumerable<string> Status(double now) =>
            Agents.Values.OrderBy(a => a.Kind).ThenBy(a => a.Id, StringComparer.Ordinal).Select(a =>
                $"{a.Id} {a.Name}: {Describe(a)}" + (a.IsTrader && a.Alive && StationOf(a) is SimStation st ? $" [{st.Name}]" : "") + (a.Hull < 0.95 && a.Alive ? $", hull {a.Hull:P0}" : "") +
                (a.Alive && a.Until < Never ? $" ({Math.Max(0, a.Until - now) / 60:0.0} min)" : "") +
            (a.Fight.Length > 0 && FightOf(a) is SimFight sf ? $" [{sf.Id}: {sf.Members.Count} ships{(sf.IsLive ? ", LIVE" : "")}]" : ""));

        public string Describe(SimAgent a)
        {
            switch (a.State)
            {
                case "dock": return $"docked in {a.System}";
                case "out": return $"{a.System}, heading out for {a.Dest}";
                case "warp": return a.IsTrader ? $"in warp, {a.System} → {a.Dest}" : $"warping to {a.Dest}";
                case "in": return $"{a.System}, inbound to the station";
                case "lurk": return $"lurking in {a.System}";
                case "home": return $"chilling at {HubOf(a)?.Name ?? "its hideout"} in {a.System}";
                case "repair": return $"repairing ({a.System})";
                case "fight": return $"FIGHTING in {a.System}";
                case "dead": return $"destroyed ({a.System})";
                default: return a.State;
            }
        }

        // ---- storage --------------------------------------------------------------------------

        // ---- bounties (hail → Jobs → Bounty?) -----------------------------------------------------------------

        /// <summary>
        /// What a trader offers <paramref name="player"/>: a bounty on the pirate that hit it in the last
        /// <see cref="SimConfig.BountyOfferSeconds"/>, if that pirate's alive and the player has room and no contract on him yet.
        /// Not stored until accepted. Null = nothing on offer.
        /// </summary>
        public SimBounty BountyOffer(SimAgent trader, string player, double now)
        {
            if (trader == null || !trader.IsTrader || !trader.Alive || !trader.Extra.TryGetValue("hitby", out var hb)) return null;
            var p = hb.Split(':');
            if (p.Length < 2 || !double.TryParse(p[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var when) || now - when > Config.BountyOfferSeconds) return null;
            var pirate = Get(p[0]);
            if (pirate == null || pirate.IsTrader || !pirate.Alive || (p.Length > 2 && pirate.Name.Replace(":", " ") != p[2])) return null;   // gone, or a new pirate under a reused id
            var mine = Bounties.Values.Where(b => b.Player == player && b.State == "open").ToList();
            if (mine.Count >= Config.BountiesPerPlayer || mine.Any(b => b.Pirate == pirate.Id)) return null;
            // someone's already after him: join their hunt — same pot, same deadline, split at the end
            var hunt = Bounties.Values.Where(b => b.Pirate == pirate.Id && b.State == "open").OrderBy(b => b.Since).FirstOrDefault();
            return new SimBounty
            {
                Pirate = pirate.Id, PirateName = pirate.Name, GiverName = trader.Name, Player = player, Where = trader.System, Since = now,
                Until = hunt?.Until ?? now + Config.BountySeconds,
                Reward = hunt?.Reward ?? LiveRules.BountyPrice(pirate.Strength, pirate.Home.Length > 0, pirate.Id + "|" + trader.Id),
                Hunters = Bounties.Values.Count(b => b.Pirate == pirate.Id && b.State == "open") + 1,
            };
        }

        public SimBounty Accept(SimBounty offer)
        {
            // the hunters already on him hear about it: the pot will be split one more way
            var others = Bounties.Values.Where(b => b.Pirate == offer.Pirate && b.State == "open" && b.Player != offer.Player).ToList();
            foreach (var b in others)
                b.Note = $"Bounty update: another captain is now after {offer.PirateName} too — the {b.Reward:N0} cr pot will be split {others.Count + 1} ways.";
            for (var n = 1; ; n++) { var id = "b" + n.ToString(CultureInfo.InvariantCulture); if (!Bounties.ContainsKey(id)) { offer.Id = id; break; } }
            Bounties[offer.Id] = offer;
            return offer;
        }

        /// <summary>Open contracts settle: target dead → done; target gone (not in the world) → lapsed; too old → expired. Old settled ones are dropped.</summary>
        void UpdateBounties(double now)
        {
            foreach (var b in Bounties.Values.Where(x => x.State == "open"))
            {
                var p = Get(b.Pirate);
                var state = p == null ? "lapsed" : !p.Alive ? "done" : now > b.Until ? "expired" : "open";
                if (state != "open") { b.State = state; b.Changed = now; b.Told = false; }
            }
            // a kill splits the pot between everyone who was hunting him
            foreach (var g in Bounties.Values.Where(x => x.State == "done" && x.Payout == 0).GroupBy(x => x.Pirate))
            {
                var n = g.Count();
                foreach (var b in g) { b.Hunters = n; b.Payout = (int)(Math.Round(b.Reward / (double)n / 50) * 50); }
            }
            foreach (var b in Bounties.Values.Where(x => x.State != "open" && x.State != "done" && now - x.Changed > 24 * 3600).ToList()) Bounties.Remove(b.Id);
            foreach (var b in Bounties.Values.Where(x => x.State == "done" && now - x.Changed > 7 * 24 * 3600).ToList()) Bounties.Remove(b.Id);   // never collected
        }

        /// <summary>Done contracts of a player: paid now (marked paid). Returns them.</summary>
        public List<SimBounty> Collect(IEnumerable<string> playerKeys, double now)
        {
            var keys = new HashSet<string>(playerKeys ?? Enumerable.Empty<string>(), StringComparer.Ordinal);
            var due = Bounties.Values.Where(b => b.State == "done" && keys.Contains(b.Player)).ToList();
            foreach (var b in due) { b.State = "paid"; b.Changed = now; b.Told = true; }
            Mint("bounties to players", due.Sum(b => b.Payout > 0 ? b.Payout : b.Reward));
            return due;
        }

        // ---- rumours (hail → Talk) ---------------------------------------------------------------------

        /// <summary>
        /// What a crew can tell a player (hail → Talk): pirate attacks reported nearby (stale — where, how long ago), wreck
        /// beacons, where a hub's crews hang out (each crew knows some hubs, not all — stable per crew), and grudges against
        /// this player. Newest first; "" items never. <paramref name="where"/> names a system for players (from, to).
        /// </summary>
        public List<string> Rumours(SimAgent a, string playerKey, double now, Func<string, string, string> where, double rangeLy = 80) =>
            Rumours(a, new List<string> { playerKey ?? "" }, now, where, rangeLy);

        /// <param name="playerKeys">Every key a memory may name this player by (pid:N, Steam id).</param>
        public List<string> Rumours(SimAgent a, List<string> playerKeys, double now, Func<string, string, string> where, double rangeLy = 80)
        {
            var list = new List<string>();
            if (a == null) return list;
            var here = a.System;
            bool Near(string sys) => sys == here || (Galaxy != null && Galaxy.Distance(here, sys) <= rangeLy);
            string W(string sys) => where != null ? where(here, sys) : sys;
            string Ago(double t) { var m = Math.Max(1, (now - t) / 60); return m < 90 ? $"{m:0} min ago" : $"{m / 60:0} h ago"; }
            // grudges first: that's about YOU
            foreach (var h in Hubs.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                var victim = playerKeys.Where(k => !string.IsNullOrEmpty(k)).Select(k => h.Memory.TryGetValue("memory.killed." + k, out var v) ? v : null).FirstOrDefault(v => v != null);
                if (victim != null) list.Add($"Watch yourself — {h.Name}'s crowd wants you dead. Something about {victim}.");
            }
            foreach (var e in Events.Where(e => e.Kind == "attack" && now - e.Time <= Config.ReportSeconds * 3 && Near(e.System) && e.Agent != a.Id)
                                    .OrderByDescending(e => e.Time).Take(2))
            {
                var pir = Fights.Values.FirstOrDefault(f => f.Members.Contains(e.Agent));
                var who = e.Name.Length > 0 ? e.Name : Get(e.Agent)?.Name ?? "a trader";   // older saves: events without names
                list.Add($"Pirates jumped {who} near {W(e.System)}, {Ago(e.Time)}." + (pir != null ? " Might still be at it." : ""));
            }
            // limpers on their way to a yard: fragile, slow, worth knowing about (for an escort — or for a pirate)
            foreach (var l in Agents.Values.Where(x => x != a && x.IsTrader && x.Alive && x.Extra.TryGetValue("detour", out var d) && d.Length > 0 && (Near(x.System) || Near(x.Dest)))
                                     .OrderBy(x => x.Hull).Take(2))
                list.Add($"{l.Name} is limping to {W(l.Dest)} for repairs — hull at {l.Hull * 100:0} %. Easy pickings, if you're that sort.");
            foreach (var e in Events.Where(e => e.Kind == "helped" && now - e.Time <= Config.ReportSeconds * 6 && Near(e.System) && e.Agent != a.Id).OrderByDescending(e => e.Time).Take(1))
                list.Add($"Heard {e.Text}, {Ago(e.Time)}. Decent sort.");
            foreach (var e in Events.Where(e => e.Kind == "hauled" && now - e.Time <= Config.ReportSeconds * 3 && Near(e.System)).OrderByDescending(e => e.Time).Take(1))
                list.Add($"Heard {e.Text}, {Ago(e.Time)}.");
            foreach (var w in Wrecks.Values.Where(x => !x.IsHub && Near(x.System)).OrderByDescending(x => x.Time).Take(2))
                list.Add($"There's a wreck out near {W(w.System)} — {w.Name}, went down {Ago(w.Time)}. Beacon's still up{(w.Amt > 0 ? ", and nobody's picked her clean yet" : "")}.");
            foreach (var kv in Bandits.Where(x => x.Value.Count > 0 && Near(x.Key)).OrderBy(x => x.Key, StringComparer.Ordinal))
            {
                if (Fnv("guns|" + a.Id + "|" + kv.Key) % 100 >= 35) continue;   // some crews have run past them, most haven't
                var g = kv.Value[(int)(Fnv("gunname|" + kv.Key) % (uint)kv.Value.Count)];
                list.Add($"Keep clear of {W(kv.Key)} — bandits hold it. {(kv.Value.Count > 1 ? $"{kv.Value.Count} guns, a {g.Name} among them" : $"There's a {g.Name} out there")}, and they shoot first.");
                break;   // one is plenty
            }
            foreach (var h in Hubs.Values.Where(x => Near(x.System)).OrderBy(x => x.Id, StringComparer.Ordinal))
            {
                if (Fnv("knows|" + a.Id + "|" + h.Id) % 100 >= 50) continue;   // not every crew knows every hideout
                var boss = Agents.Values.Where(x => !x.IsTrader && x.Alive && x.Home == h.Id).OrderBy(x => x.Id, StringComparer.Ordinal).FirstOrDefault();
                var pile = Wrecks.Values.Count(x => x.Hub == h.Id && !x.Centre && !x.Debris && !x.Gone);
                var junk = pile >= 4 ? $" They've a whole boneyard of dragged-off hulls there." : pile > 0 ? " They drag their kills back there, picked over." : "";
                list.Add(boss != null ? $"Word is {boss.Name}'s lot hang around {W(h.System)}. Folk call it {h.Name} — a pirate {HubRank(h)}. We steer well clear.{junk}"
                                      : $"There used to be a pirate den at {W(h.System)} — {h.Name}. Quiet now, they say.{(pile > 0 ? " Their haul might still be lying about." : "")}");
            }
            return list;
        }

        public string Serialize()
        {
            var inv = CultureInfo.InvariantCulture;
            var sb = new StringBuilder();
            sb.Append(Header).Append('\n');
            sb.Append("world last=").Append(LastTick.ToString("R", inv))
              .Append(" systems=").Append(ShipRecord.Esc(string.Join(";", Config.Systems))).Append(IdSeq > 0 ? " seq=" + IdSeq.ToString(inv) : "").Append('\n');
            foreach (var kv in Stations.OrderBy(x => x.Key, StringComparer.Ordinal))
                foreach (var st in kv.Value)
                    sb.Append("station sys=").Append(ShipRecord.Esc(kv.Key)).Append(" id=").Append(st.Id.ToString(inv)).Append(" name=").Append(ShipRecord.Esc(st.Name))
                      .Append(" x=").Append(st.Pos.X.ToString("R", inv)).Append(" y=").Append(st.Pos.Y.ToString("R", inv)).Append(" z=").Append(st.Pos.Z.ToString("R", inv))
                      .Append(st.HasBox ? " box=" + string.Join(",", new[] { st.Box.X, st.Box.Y, st.Box.Z, st.Half.X, st.Half.Y, st.Half.Z, st.Rot.X, st.Rot.Y, st.Rot.Z, st.Rot.W }.Select(v => v.ToString("R", inv))) : "").Append('\n');
            foreach (var a in Agents.Values.OrderBy(x => x.Id, StringComparer.Ordinal)) sb.Append(a.ToLine()).Append('\n');
            foreach (var w in Wrecks.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
                sb.Append("wreck id=").Append(ShipRecord.Esc(w.Id)).Append(" sys=").Append(ShipRecord.Esc(w.System)).Append(" name=").Append(ShipRecord.Esc(w.Name))
                  .Append(" kind=").Append(ShipRecord.Esc(w.Kind)).Append(" role=").Append(ShipRecord.Esc(w.Role)).Append(" cargo=").Append(ShipRecord.Esc(w.Cargo))
                  .Append(" amt=").Append(w.Amt.ToString(inv)).Append(" x=").Append(w.Pos.X.ToString("R", inv)).Append(" y=").Append(w.Pos.Y.ToString("R", inv)).Append(" z=").Append(w.Pos.Z.ToString("R", inv))
                  .Append(" t=").Append(w.Time.ToString("R", inv)).Append(" until=").Append(w.Until.ToString("R", inv)).Append(" entity=").Append(w.Entity.ToString(inv))
                  .Append(w.Ship.Length > 0 ? " ship=" + ShipRecord.Esc(w.Ship) : "").Append(w.Looted ? " looted=1" : "")
                  .Append(w.By.Length > 0 ? " by=" + ShipRecord.Esc(w.By) : "").Append(w.Hub.Length > 0 ? " hub=" + ShipRecord.Esc(w.Hub) : "")
                  .Append(w.Debris ? " debris=1" : "").Append(w.Gone ? " gone=1" : "").Append(w.Cash > 0 ? " cash=" + w.Cash.ToString(inv) : "").Append(w.Centre ? " centre=1" : "").Append(w.Decor ? " decor=1" : "").Append(w.Shown.Length > 0 ? " shown=" + ShipRecord.Esc(w.Shown) : "").Append('\n');
            foreach (var h in Hubs.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
                sb.Append("hub id=").Append(ShipRecord.Esc(h.Id)).Append(" sys=").Append(ShipRecord.Esc(h.System)).Append(" name=").Append(ShipRecord.Esc(h.Name))
                  .Append(" x=").Append(h.Pos.X.ToString("R", inv)).Append(" y=").Append(h.Pos.Y.ToString("R", inv)).Append(" z=").Append(h.Pos.Z.ToString("R", inv))
                  .Append(" since=").Append(h.Since.ToString("R", inv)).Append(" size=").Append(h.Size.ToString(inv))
                  .Append(h.Manned ? " manned=1 recruit=" + h.NextRecruit.ToString("R", inv) : "").Append(h.Den != 0 ? " den=" + h.Den.ToString(inv) : "").Append(h.Cash >= 1 ? " cash=" + Math.Round(h.Cash).ToString("R", inv) : "").Append(h.BrokenUntil > 0 ? " broken=" + h.BrokenUntil.ToString("R", inv) : "").Append(h.RichSince > 0 ? " rich=" + h.RichSince.ToString("R", inv) : "").Append(h.LastSplit > 0 ? " split=" + h.LastSplit.ToString("R", inv) : "")
                  .Append(h.Memory.Count > 0 ? " mem=" + ShipRecord.Esc(string.Join(";", h.Memory.Select(kv => kv.Key + "=" + kv.Value.Replace(";", ",").Replace("=", "-")))) : "")
                  .Append(h.Stash.Count > 0 ? " stash=" + ShipRecord.Esc(string.Join(";", h.Stash.Select(x => x.Item.Replace(";", ",").Replace(":", ",") + ":" + x.Amt.ToString(inv) + ":" + x.Time.ToString("R", inv)))) : "").Append('\n');
            foreach (var b in Bounties.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
                sb.Append("bounty id=").Append(ShipRecord.Esc(b.Id)).Append(" p=").Append(ShipRecord.Esc(b.Pirate)).Append(" pn=").Append(ShipRecord.Esc(b.PirateName))
                  .Append(" g=").Append(ShipRecord.Esc(b.GiverName)).Append(" pl=").Append(ShipRecord.Esc(b.Player)).Append(" w=").Append(ShipRecord.Esc(b.Where))
                  .Append(" st=").Append(ShipRecord.Esc(b.State)).Append(" r=").Append(b.Reward.ToString(inv)).Append(" since=").Append(b.Since.ToString("R", inv))
                  .Append(" until=").Append(b.Until.ToString("R", inv)).Append(" ch=").Append(b.Changed.ToString("R", inv)).Append(b.Told ? " told=1" : "")
                  .Append(b.Payout > 0 ? " pay=" + b.Payout.ToString(inv) : "").Append(b.Hunters > 1 ? " n=" + b.Hunters.ToString(inv) : "").Append(b.Note.Length > 0 ? " note=" + ShipRecord.Esc(b.Note) : "").Append('\n');
            if (Tipped.Count > 0) sb.Append("tipped players=").Append(ShipRecord.Esc(string.Join(";", Tipped))).Append('\n');
            foreach (var kv in Stats) sb.Append("stat k=").Append(ShipRecord.Esc(kv.Key)).Append(" v=").Append(Math.Round(kv.Value, 1).ToString("R", CultureInfo.InvariantCulture)).Append('\n');
            if (pirateDeaths.Count > 0) sb.Append("pkills list=").Append(ShipRecord.Esc(string.Join(";", pirateDeaths.Select(x => x.Key.ToString("R", CultureInfo.InvariantCulture) + "@" + x.Value)))).Append('\n');
            if (RolledStars.Count > 0) sb.Append("rolled stars=").Append(ShipRecord.Esc(string.Join(";", RolledStars))).Append('\n');
            foreach (var f in Fights.Values.OrderBy(x => x.Id, StringComparer.Ordinal))
                sb.Append("fight id=").Append(ShipRecord.Esc(f.Id)).Append(" sys=").Append(ShipRecord.Esc(f.System))
                  .Append(" pos=").Append(f.HasPos ? "1" : "0").Append(" x=").Append(f.Pos.X.ToString("R", inv)).Append(" y=").Append(f.Pos.Y.ToString("R", inv)).Append(" z=").Append(f.Pos.Z.ToString("R", inv))
                  .Append(" start=").Append(f.Start.ToString("R", inv)).Append(" next=").Append(f.Next.ToString("R", inv))
                  .Append(f.IsLive ? " live=" + ShipRecord.Esc(f.Live) : "").Append(" members=").Append(ShipRecord.Esc(string.Join(";", f.Members))).Append('\n');
            foreach (var e in Events.Skip(Math.Max(0, Events.Count - 50)))
                sb.Append("event t=").Append(e.Time.ToString("R", inv)).Append(" a=").Append(ShipRecord.Esc(e.Agent)).Append(" s=").Append(ShipRecord.Esc(e.System))
                  .Append(" k=").Append(ShipRecord.Esc(e.Kind)).Append(e.Lane.Length > 0 ? " l=" + ShipRecord.Esc(e.Lane) : "").Append(e.Name.Length > 0 ? " n=" + ShipRecord.Esc(e.Name) : "").Append(" text=").Append(ShipRecord.Esc(e.Text)).Append('\n');
            return sb.ToString();
        }

        /// <summary>Parse a saved world; unknown header → empty world (never guess at a format we didn't write). Bad lines skipped.</summary>
        /// <summary>A saved trader's speed back inside its role's range (ranges get retuned between versions).</summary>
        static void FitSpeed(SimAgent a)
        {
            if (!a.IsTrader || a.Role.Length == 0) return;
            var r = RoleOf(a);
            if (a.Speed < r.SpeedMin || a.Speed > r.SpeedMax) a.Speed = r.SpeedMin + (Fnv(a.Id + "|speed") % 1000) / 1000f * (r.SpeedMax - r.SpeedMin);
        }

        public static World Parse(string text, out int skipped)
        {
            skipped = 0;
            var w = new World();
            var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
            if (lines.Length == 0 || lines[0].Trim() != Header) { skipped = lines.Count(l => l.Trim().Length > 0); return w; }
            var inv = CultureInfo.InvariantCulture;
            foreach (var l in lines.Skip(1))
            {
                if (l.Trim().Length == 0) continue;
                if (l.StartsWith("agent ", StringComparison.Ordinal)) { var a = SimAgent.FromLine(l); if (a == null) skipped++; else { FitSpeed(a); w.Agents[a.Id] = a; } continue; }
                if (l.StartsWith("world ", StringComparison.Ordinal))
                {
                    var kv = Fields(l, "world ");
                    if (kv.TryGetValue("last", out var last) && double.TryParse(last, NumberStyles.Float, inv, out w.LastTick)) w.lastPopulate = w.LastTick;   // a loaded world trickles drifters in, no refill at once
                    if (kv.TryGetValue("systems", out var sys)) w.Config.Systems = sys.Split(';').Where(s => s.Length > 0).ToList();
                    if (kv.TryGetValue("seq", out var sq)) int.TryParse(sq, NumberStyles.Integer, inv, out w.IdSeq);
                    continue;
                }
                if (l.StartsWith("hub ", StringComparison.Ordinal))
                {
                    var kv = Fields(l, "hub ");
                    float Fh(string k) => float.TryParse(G(kv, k), NumberStyles.Float, inv, out var v) ? v : 0;
                    if (G(kv, "id").Length == 0) { skipped++; continue; }
                    int.TryParse(G(kv, "size"), NumberStyles.Integer, inv, out var size);
                    double.TryParse(G(kv, "since"), NumberStyles.Float, inv, out var hs);
                    var hub = new SimHub { Id = G(kv, "id"), System = G(kv, "sys"), Name = G(kv, "name"), Pos = new Vec3(Fh("x"), Fh("y"), Fh("z")), Since = hs, Size = Math.Max(1, size) };
                    hub.Manned = G(kv, "manned") == "1"; double.TryParse(G(kv, "recruit"), NumberStyles.Float, inv, out hub.NextRecruit);
                    int.TryParse(G(kv, "den"), NumberStyles.Integer, inv, out hub.Den);
                    double.TryParse(G(kv, "cash"), NumberStyles.Float, inv, out hub.Cash);
                    double.TryParse(G(kv, "broken"), NumberStyles.Float, inv, out hub.BrokenUntil);
                    double.TryParse(G(kv, "rich"), NumberStyles.Float, inv, out hub.RichSince);
                    double.TryParse(G(kv, "split"), NumberStyles.Float, inv, out hub.LastSplit);
                    foreach (var m in G(kv, "mem").Split(';').Select(x => x.Split(new[] { '=' }, 2)).Where(x => x.Length == 2)) hub.Memory[m[0]] = m[1];
                    foreach (var st in G(kv, "stash").Split(';').Select(x => x.Split(':')).Where(x => x.Length == 3 && x[0].Length > 0))
                        if (int.TryParse(st[1], NumberStyles.Integer, inv, out var sa) && double.TryParse(st[2], NumberStyles.Float, inv, out var stt) && sa > 0) hub.Stash.Add(new SimStash { Item = st[0], Amt = sa, Time = stt });
                    w.Hubs[hub.Id] = hub;
                    continue;
                }
                if (l.StartsWith("tipped ", StringComparison.Ordinal))
                {
                    var kv = Fields(l, "tipped ");
                    foreach (var p in G(kv, "players").Split(';').Where(x => x.Length > 0)) w.Tipped.Add(p);
                    continue;
                }
                if (l.StartsWith("bounty ", StringComparison.Ordinal))
                {
                    var kv = Fields(l, "bounty ");
                    double Fb(string k) => double.TryParse(G(kv, k), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
                    var b = new SimBounty { Id = G(kv, "id"), Pirate = G(kv, "p"), PirateName = G(kv, "pn"), GiverName = G(kv, "g"), Player = G(kv, "pl"), Where = G(kv, "w"),
                                            State = G(kv, "st"), Since = Fb("since"), Until = Fb("until"), Changed = Fb("ch"), Told = G(kv, "told") == "1" };
                    int.TryParse(G(kv, "r"), NumberStyles.Integer, CultureInfo.InvariantCulture, out b.Reward);
                    int.TryParse(G(kv, "pay"), NumberStyles.Integer, CultureInfo.InvariantCulture, out b.Payout);
                    b.Hunters = int.TryParse(G(kv, "n"), NumberStyles.Integer, CultureInfo.InvariantCulture, out var hn) && hn > 1 ? hn : 1;
                    b.Note = G(kv, "note");
                    if (b.Id.Length == 0 || b.Pirate.Length == 0) skipped++; else w.Bounties[b.Id] = b;
                    continue;
                }
                if (l.StartsWith("stat ", StringComparison.Ordinal))
                {
                    var sf = Fields(l, "stat ");
                    if (double.TryParse(G(sf, "v"), NumberStyles.Float, CultureInfo.InvariantCulture, out var sv) && G(sf, "k").Length > 0) w.Stats[G(sf, "k")] = sv;
                    continue;
                }
                if (l.StartsWith("pkills ", StringComparison.Ordinal))
                {   // pirate kills that still damp the raiders (a server restart doesn't undo a cleared hub)
                    foreach (var it in G(Fields(l, "pkills "), "list").Split(';'))
                    {
                        var at = it.IndexOf('@');
                        if (at > 0 && double.TryParse(it.Substring(0, at), NumberStyles.Float, CultureInfo.InvariantCulture, out var kt)) w.pirateDeaths.Add(new KeyValuePair<double, string>(kt, it.Substring(at + 1)));
                    }
                    continue;
                }
                if (l.StartsWith("rolled ", StringComparison.Ordinal))
                {
                    foreach (var st in G(Fields(l, "rolled "), "stars").Split(';')) if (st.Length > 0) w.RolledStars.Add(st);
                    continue;
                }
                if (l.StartsWith("wreck ", StringComparison.Ordinal))
                {
                    var kv = Fields(l, "wreck ");
                    double Dw(string k) => double.TryParse(G(kv, k), NumberStyles.Float, inv, out var v) ? v : 0;
                    if (G(kv, "id").Length == 0) { skipped++; continue; }
                    int.TryParse(G(kv, "amt"), NumberStyles.Integer, inv, out var amt); int.TryParse(G(kv, "entity"), NumberStyles.Integer, inv, out var ent);
                    w.Wrecks[G(kv, "id")] = new SimWreck
                    {
                        Id = G(kv, "id"), System = G(kv, "sys"), Name = G(kv, "name"), Kind = G(kv, "kind"), Role = G(kv, "role"), Cargo = G(kv, "cargo"), Amt = amt, Ship = G(kv, "ship"), Looted = G(kv, "looted") == "1", Cash = int.TryParse(G(kv, "cash"), NumberStyles.Integer, inv, out var wc) ? wc : 0, Centre = G(kv, "centre") == "1", Decor = G(kv, "decor") == "1",
                        Pos = new Vec3((float)Dw("x"), (float)Dw("y"), (float)Dw("z")), Time = Dw("t"), Until = Dw("until"), Entity = ent,
                        By = G(kv, "by"), Hub = G(kv, "hub"), Debris = G(kv, "debris") == "1", Gone = G(kv, "gone") == "1", Shown = G(kv, "shown"),
                    };
                    continue;
                }
                if (l.StartsWith("fight ", StringComparison.Ordinal))
                {
                    var kv = Fields(l, "fight ");
                    double Dn(string k) => double.TryParse(G(kv, k), NumberStyles.Float, inv, out var v) ? v : 0;
                    if (G(kv, "id").Length == 0) { skipped++; continue; }
                    var f = new SimFight
                    {
                        Id = G(kv, "id"), System = G(kv, "sys"), HasPos = G(kv, "pos") == "1", Pos = new Vec3((float)Dn("x"), (float)Dn("y"), (float)Dn("z")),
                        Start = Dn("start"), Next = Dn("next"), Live = G(kv, "live"),
                        Members = G(kv, "members").Split(';').Where(x => x.Length > 0).ToList(),
                    };
                    w.Fights[f.Id] = f;
                    continue;
                }
                if (l.StartsWith("station ", StringComparison.Ordinal))
                {
                    var kv = Fields(l, "station ");
                    float P(string k) => float.TryParse(G(kv, k), NumberStyles.Float, inv, out var v) ? v : 0f;
                    if (!int.TryParse(G(kv, "id"), NumberStyles.Integer, inv, out var sid) || G(kv, "sys").Length == 0) { skipped++; continue; }
                    if (!w.Stations.TryGetValue(G(kv, "sys"), out var sl)) w.Stations[G(kv, "sys")] = sl = new List<SimStation>();
                    var st = new SimStation { Id = sid, Name = G(kv, "name"), Pos = new Vec3(P("x"), P("y"), P("z")) };
                    var bx = G(kv, "box").Split(',').Select(v => float.TryParse(v, NumberStyles.Float, inv, out var f) ? f : float.NaN).ToArray();
                    if (bx.Length == 10 && bx.All(v => !float.IsNaN(v)))
                    { st.HasBox = true; st.Box = new Vec3(bx[0], bx[1], bx[2]); st.Half = new Vec3(bx[3], bx[4], bx[5]); st.Rot = new Quat(bx[6], bx[7], bx[8], bx[9]); }
                    sl.Add(st);
                    continue;
                }
                if (l.StartsWith("event ", StringComparison.Ordinal))
                {
                    var kv = Fields(l, "event ");
                    double.TryParse(kv.TryGetValue("t", out var ts) ? ts : "", NumberStyles.Float, inv, out var t);
                    w.Events.Add(new SimEvent { Time = t, Agent = G(kv, "a"), System = G(kv, "s"), Kind = G(kv, "k"), Lane = G(kv, "l"), Name = G(kv, "n"), Text = G(kv, "text") });
                    continue;
                }
                skipped++;
            }
            return w;
        }

        internal static Dictionary<string, string> Fields(string line, string prefix)
        {
            line = (line ?? "").Trim();
            if (!line.StartsWith(prefix, StringComparison.Ordinal)) return null;
            var kv = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var tok in line.Substring(prefix.Length).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var i = tok.IndexOf('=');
                if (i > 0) kv[tok.Substring(0, i)] = ShipRecord.Unesc(tok.Substring(i + 1));
            }
            return kv;
        }

        static string G(Dictionary<string, string> kv, string k) => kv.TryGetValue(k, out var v) ? v : "";

        // ---- helpers --------------------------------------------------------------------------

        void Emit(double t, SimAgent a, string kind, string text)
        {
            Events.Add(new SimEvent { Time = t, Agent = a.Id, Name = a.Name, System = a.System, Kind = kind, Text = text });
            if (kind == "destroyed" && !a.IsTrader)
            {
                pirateDeaths.Add(new KeyValuePair<double, string>(t, a.System));
                Count("sys|" + a.System + "|pkill");
                if (a.Home.Length > 0) Count("hub|" + a.Home + "|lost");
                if (Hubs.TryGetValue(a.Home, out var bh) && !IsBroken(bh) && (Overrun(bh, a) || !Agents.Values.Any(x => x != a && !x.IsTrader && x.Alive && x.Home == bh.Id)))
                    Break(bh, t);
            }
        }

        // ---- raider cap ---------------------------------------------------------------------------

        /// <summary>A pirate out raiding: not at home, not on its way home.</summary>
        bool Raiding(SimAgent a) => !a.IsTrader && a.Alive && a.State != "home" && !(a.State == "fight" && a.After == "home")
                                    && !(a.State == "warp" && HubOf(a) is SimHub h && h.System == a.Dest);

        /// <summary>Raiders in (or bound for) a system.</summary>
        int RaidersIn(string system) => Agents.Values.Count(a => Raiding(a) && (a.State == "warp" ? a.Dest == system : a.System == system));

        /// <summary>Station systems near players, and how many raiders they're meant to hold between them.</summary>
        List<string> FocusLanes() => Focus.Where(x => Config.Systems.Contains(x)).ToList();
        double RaiderTarget(int lanes) => Config.RaidersPerSystem * lanes * KillDampNow();

        /// <summary>1 − recent pirate kills near players (fading) ÷ <see cref="SimConfig.KillDamp"/>, floored.</summary>
        double KillDampNow()
        {
            var now = LastTick;
            pirateDeaths.RemoveAll(x => x.Key < now - 4 * Config.KillFadeSeconds);
            var focus = new HashSet<string>(Focus, StringComparer.Ordinal);
            var w = pirateDeaths.Where(x => focus.Contains(x.Value)).Sum(x => Math.Exp(-(now - x.Key) / Config.KillFadeSeconds));
            return Math.Max(Config.KillDampFloor, 1 - w / Config.KillDamp);
        }
        int RaidersNear(List<string> lanes) { var set = new HashSet<string>(lanes, StringComparer.Ordinal); return Agents.Values.Count(a => Raiding(a) && set.Contains(a.State == "warp" ? a.Dest : a.System)); }
        bool RaidersFull() { if (!Config.RaiderCap || Focus.Count == 0) return false; var l = FocusLanes(); return RaidersNear(l) >= Math.Ceiling(RaiderTarget(l.Count)); }

        string PickOther(Random rng, string current)
        {
            var others = Config.Systems.Where(s => s != current).ToList();
            return others.Count > 0 ? Pick(rng, others) : current;
        }

        static string Pick(Random rng, IList<string> list) => list[rng.Next(list.Count)];
        static double Between(Random rng, double lo, double hi) => lo + rng.NextDouble() * (hi - lo);
        static double Clamp01(double v) => v < 0 ? 0 : v > 1 ? 1 : v;
    }
}
