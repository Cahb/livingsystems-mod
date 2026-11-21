using System.IO;

namespace LivingSystems
{
    /// <summary>
    /// Which hull salvage tugs fly: the server owner's own blueprint <c>Content/Mods/LivingSystems/prefabs/LS_Tug.epb</c> if
    /// it's there (made in game — the mod ships no game assets and never edits one), else the vanilla cargo tug by name.
    /// Dedi and PfServers read the same folder, so they agree.
    /// </summary>
    static class TugHull
    {
        static string name;
        public static string Name
        {
            get
            {
                if (name != null) return name;
                var dir = Path.Combine(Path.GetDirectoryName(typeof(LivingSystemsMod).Assembly.Location) ?? ".", "prefabs");
                name = File.Exists(Path.Combine(dir, Core.Sim.World.TugOverride + ".epb")) ? Core.Sim.World.TugOverride : Core.Sim.World.DefaultTugShip;
                return name;
            }
        }

        static bool thrustRead; static float[] thrust;
        /// <summary>The hull's full thrust, m/s², from <c>prefabs/&lt;hull&gt;.thrust</c> — six numbers read off the ship's
        /// Statistics panel: forward back up down right left. None → the flight driver learns it in flight.</summary>
        public static float[] Thrust
        {
            get
            {
                if (thrustRead) return thrust;
                thrustRead = true;
                try
                {
                    var f = Path.Combine(Path.GetDirectoryName(typeof(LivingSystemsMod).Assembly.Location) ?? ".", "prefabs", Name + ".thrust");
                    if (!File.Exists(f)) return null;
                    var nums = new System.Collections.Generic.List<float>();
                    foreach (var line in File.ReadAllLines(f))
                    {
                        var l = line.Split('#')[0];
                        foreach (var tok in l.Split(new[] { ' ', '\t', ',', ';' }, System.StringSplitOptions.RemoveEmptyEntries))
                            if (float.TryParse(tok, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v)) nums.Add(v);
                    }
                    if (nums.Count == 6 && nums.TrueForAll(v => v > 0)) thrust = nums.ToArray();
                }
                catch { thrust = null; }
                return thrust;
            }
        }
    }
}
