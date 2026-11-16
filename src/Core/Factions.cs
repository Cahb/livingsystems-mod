using System.Collections.Generic;

namespace LivingSystems.Core
{
    /// <summary>
    /// RE2 faction groups as the API reports them (named factions: group = the faction's Id in Factions.ecf,
    /// verified on real POIs: Pirates 16, Warlord 27, Polaris 7). Shared by the hail menu and displays.
    /// </summary>
    public static class Factions
    {
        /// <summary>RE2 factions that attack the player by default.</summary>
        public static readonly HashSet<int> Hostile = new HashSet<int> { 2, 8, 10, 16, 17, 27 };   // Zirax, Alien, Legacy, Pirates, Kriel, Warlord

        /// <summary>Group 0 = a player faction, 1 = a single player.</summary>
        public static bool IsPlayer(int group) => group == 0 || group == 1;

        public static string Name(int g)
        {
            switch (g)
            {
                case 0: return "player faction";
                case 1: return "player";
                case 2: return "Zirax";
                case 6: return "Talon";
                case 7: return "Polaris";
                case 8: return "Alien";
                case 10: return "Legacy";
                case 11: return "UCH";
                case 15: return "Civilian";
                case 16: return "Pirates";
                case 17: return "Kriel";
                case 18: return "Trader";
                case 19: return "Colonists";
                case 27: return "Warlord";
                default: return "faction " + g;
            }
        }
    }
}
