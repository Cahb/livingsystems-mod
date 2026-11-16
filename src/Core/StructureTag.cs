namespace LivingSystems.Core
{
    /// <summary>
    /// The in-world name marker on every structure we spawn (PLAN.md §3).
    /// The orphan sweep only ever touches structures carrying it.
    /// </summary>
    public static class StructureTag
    {
        public const string Prefix = "«LS» ";

        public static string Apply(string name) => IsTagged(name) ? name : Prefix + name;

        public static bool IsTagged(string name) =>
            name != null && name.StartsWith(Prefix, System.StringComparison.Ordinal);

        public static string Strip(string name) => IsTagged(name) ? name.Substring(Prefix.Length) : name;
    }
}
