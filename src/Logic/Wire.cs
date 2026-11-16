using System.Text;

namespace LivingSystems
{
    /// <summary>Tiny text protocol between Dedi and PfServer: fields joined by '|'.</summary>
    static class Wire
    {
        public static byte[] Pack(params object[] fields) => Encoding.UTF8.GetBytes(string.Join("|", fields));
        /// <summary>
        /// The game sets "sender" to the sending ASSEMBLY's name (verified: "LivingSystems.Logic"),
        /// not the receiver string we pass. Accept our loader and logic assemblies.
        /// </summary>
        public static bool IsOurs(string sender) =>
            sender == "LivingSystems" || (sender != null && sender.StartsWith("LivingSystems.", System.StringComparison.Ordinal));

        public static string[] Unpack(byte[] data) => Encoding.UTF8.GetString(data ?? new byte[0]).Split('|');
    }
}
