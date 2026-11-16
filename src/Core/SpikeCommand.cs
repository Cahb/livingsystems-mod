using System;
using System.Collections.Generic;
using System.Globalization;

namespace LivingSystems.Core
{
    /// <summary>
    /// Chat commands for the F00 spike. "/ls spawn CV_Freighter_SparrowA 2 polaris" etc.
    /// Parsing lives here so it is unit-tested; execution lives in the Mod.
    /// </summary>
    public sealed class SpikeCommand
    {
        public string Verb { get; private set; }
        public IReadOnlyList<string> Args { get; private set; }

        public const string DefaultPrefab = "CV_Freighter_SparrowA";

        public static readonly string[] Verbs =
            { "help", "spawn", "move", "stop", "clear", "list", "dialog", "info", "factions", "faction", "goto", "follow",
              "trade", "scan", "cargo", "items", "perf", "npc", "traderblock", "heal", "holo", "tp", "find", "spawn2", "kit", "record", "makekiosk", "kiosk", "pad", "purge", "padinfo", "gsinfo", "hail", "comms", "fine", "fly", "movepf", "sim" };

        /// <summary>Movement modes the spike compares (PLAN.md §4a Q1).</summary>
        public static readonly string[] MoveModes = { "fwd", "fwdtick", "move", "pos", "lfwd", "lup" };

        public static bool TryParse(string text, out SpikeCommand cmd)
        {
            cmd = null;
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Trim().Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length > 0 && string.Equals(parts[0], "/comms", StringComparison.OrdinalIgnoreCase))
            { cmd = new SpikeCommand { Verb = "comms", Args = new List<string>() }; return true; }
            if (parts.Length == 0 || !string.Equals(parts[0], "/ls", StringComparison.OrdinalIgnoreCase)) return false;
            var verb = parts.Length > 1 ? parts[1].ToLowerInvariant() : "help";
            if (Array.IndexOf(Verbs, verb) < 0) verb = "help";
            var args = new List<string>();
            for (int i = 2; i < parts.Length; i++) args.Add(parts[i]);
            cmd = new SpikeCommand { Verb = verb, Args = args };
            return true;
        }

        public string Arg(int i, string fallback) => i < Args.Count ? Args[i] : fallback;

        public int IntArg(int i, int fallback, int min, int max)
        {
            if (i >= Args.Count || !int.TryParse(Args[i], NumberStyles.Integer, CultureInfo.InvariantCulture, out var v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        public float FloatArg(int i, float fallback, float min, float max)
        {
            if (i >= Args.Count || !float.TryParse(Args[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v)) return fallback;
            return Math.Max(min, Math.Min(max, v));
        }

        /// <summary>
        /// "polaris" etc., or explicit "group:id" (e.g. "0:12") once /ls factions has told us RE2's ids.
        /// </summary>
        public static void ParseFaction(string spec, out byte group, out int id)
        {
            id = 0;
            var parts = (spec ?? "").Split(':');
            if (parts.Length == 2 && byte.TryParse(parts[0], out group) && int.TryParse(parts[1], out id)) return;
            group = FactionGroup(spec);
        }

        /// <summary>Faction group byte as the legacy API wants it (Eleon.Modding.FactionGroup).</summary>
        public static byte FactionGroup(string name)
        {
            // RE2 named factions are encoded as GROUP = the faction's Id in Factions.ecf, id 0
            // (verified on real POIs: Pirates 16:0, Warlord 27:0, Polaris 7:0).
            switch ((name ?? "").ToLowerInvariant())
            {
                case "zirax": return 2;
                case "talon": return 6;
                case "alien": return 8;
                case "player": return 1;
                case "legacy": return 10;
                case "uch": return 11;
                case "civilian": return 15;
                case "pirates": case "pirate": return 16;
                case "kriel": return 17;
                case "trader": return 18;
                case "colonists": return 19;
                case "warlord": return 27;
                default: return 7;   // polaris: friendly to most players
            }
        }

        /// <summary>Short overview; details per topic via <see cref="HelpFor"/>.</summary>
        public const string Help =
            "Living Systems — traders, pirates and wrecks live around you. Find them by text, talk to them, work for them. " +
            "SCAN: in your ship's control panel (P), name a signal LS_TRANSPONDER and switch it on — ships on the band: distance, direction, what they carry (or /ls fly where). " +
            "HAIL: name another signal LS_HAIL; point your nose at a ship and flip it (or /ls hail) — Talk (rumours: attacks, wrecks, pirate hubs), Trade (its hold), Jobs (escort, bounties). " +
            "Pirates answer a hail with a price to leave you be. LCDs: LS_RADAR_LCD (scanner), LS_TRAFFIC_LCD, LS_CONTACTS_LCD, LS_FEED_LCD, or LS_HAIL_LCD (all in one). " +
            "More: /ls help names | /ls help admin (test and debug commands).";

        /// <summary>Help text for one topic; unknown topic → the overview.</summary>
        public static string HelpFor(string topic)
        {
            switch ((topic ?? "").ToLowerInvariant())
            {
                case "names":
                case "blocks":
                    return "Name these blocks on your ship (Control Panel → Devices → rename, exact spelling): " +
                           "LS_TRANSPONDER = a control-panel signal (any slot; a switch, lever or light of that name also works): ON = transponder scan, OFF = stop | " +
                           "LS_HAIL = a control-panel signal (or a block of that name): each OFF→ON flip hails the ship in your nose (else the contacts list) | " +
                           "LS_RADAR_LCD / LS_TRAFFIC_LCD / LS_CONTACTS_LCD / LS_FEED_LCD = LCD pages; LS_HAIL_LCD = all in one.";
                case "fly":
                    return "/ls fly follow [distance=150] [cruise=40] — every «LS» ship follows you | /ls fly hold — brake and stay | " +
                           "/ls fly off — release (brakes) | /ls fly cal <fwd|back|up|down|right|left> <push> <seconds> [brake|coast] — measure a push | " +
                           "/ls fly probe — log the next spawned ship's state until it obeys | /ls fly idle auto|brake|coast. " +
                           "Fresh ships ignore steering for ~16 s after spawning; push gains are learned per ship.";
                case "trade":
                    return "/ls hail or /comms or the COMMS button — contacts in range, hail one | lever TRADE_PAD_ACTIVATE ON — kiosk docks on TRADE_PAD | " +
                           "/ls kiosk [off] | /ls pad | /ls padinfo | /ls trade sell|buy [item count]... | /ls items <filter>. See /ls help names.";
                case "spawn":
                    return "/ls spawn [prefab] [count 1-10] [polaris|zirax|talon] [BA|CV|SV|HV|auto] [distance=80] | /ls spawn2 <prefab> [distance] | " +
                           "/ls list | /ls info | /ls clear (removes «LS» ships) | /ls faction <name|group:id> | /ls factions";
                case "admin":
                    return "Test: /ls sim (who is where) | /ls sim log [n] | /ls sim here (pirate + trader in front of you) | /ls sim wreck [clear] | /ls fly patrol | " +
                           "/ls sim reset | /ls reload | /ls spawn … | /ls clear. Probes and dumps: /ls help debug.";
                case "debug":
                    return "/ls movepf <id> [x y z] <playfield> (legacy ChangePlayfield probe) | /ls gsinfo <id>... (global list: playfield, pos, faction) | " +
                           "/ls scan [radius] | /ls cargo <id> | /ls perf | /ls find [blockType=1314] [entityId|all] | /ls record [id] | " +
                           "/ls makekiosk [id] | /ls purge | /ls kit | /ls npc | /ls traderblock | /ls holo [off] | /ls dialog | " +
                           "old spike steering: /ls move …, /ls goto|follow …, /ls stop [a|b|c|d]";
                default:
                    return Help;
            }
        }
    }
}
