namespace LivingSystems.Core
{
    /// <summary>
    /// Which saves the mod runs on: any save it's loaded with (installing the mod is the opt-in). A save path is still
    /// required — no path, no save to keep the sim in.
    /// </summary>
    public static class SaveGuard
    {
        /// <summary>Last folder name of a save path; handles / and \ and trailing separators.</summary>
        public static string SaveName(string savePath)
        {
            if (string.IsNullOrWhiteSpace(savePath)) return null;
            var parts = savePath.Split(new[] { '/', '\\' }, System.StringSplitOptions.RemoveEmptyEntries);
            return parts.Length == 0 ? null : parts[parts.Length - 1];
        }

        public static bool IsAllowed(string savePath) => SaveName(savePath) != null;
    }
}
