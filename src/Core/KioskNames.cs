namespace LivingSystems.Core
{
    /// <summary>
    /// In-game names of the trade kiosk and its trader (shown with the «LS» tag). The orphan sweep matches the
    /// kiosk name, so both sides must use these constants.
    /// </summary>
    public static class KioskNames
    {
        public const string Kiosk = "Holo-Trade Projector";
        public const string Npc = "Trader [HOLOGRAM]";

        /// <summary>Every kiosk name ever used (old saves/spawns keep the old one). The sweep matches all.</summary>
        public static readonly string[] AllKioskNames = { Kiosk, "Trade Kiosk" };

        public static bool IsKiosk(string entityName)
        {
            foreach (var n in AllKioskNames) if (entityName == StructureTag.Apply(n)) return true;
            return false;
        }
    }
}
