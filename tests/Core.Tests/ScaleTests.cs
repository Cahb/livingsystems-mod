using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using LivingSystems.Core.Sim;
using Xunit;

/// <summary>
/// Scaling guards: the sim's cost per agent must stay roughly flat. Compared as a ratio between N and 4N agents (machine
/// speed cancels out): linear work is ~4×, a scan-per-agent-per-event loop is ~16×. Seeding is linear; stepping is still
/// ~quadratic (every event scans all agents — an indexed event queue would fix it; needed for 10k), so its guard catches
/// anything worse than that plus the per-tick budget. Offline numbers at real scale: `.cache/simrun` with TR/PR/FOCUSLY.
/// </summary>
public class ScaleTests
{
    static World Make(int systems)
    {
        var w = new World();
        for (var i = 0; i < systems; i++)
        {
            var s = "S" + i;
            w.Config.Systems.Add(s);
            w.SetStations(s, new List<SimStation> { new SimStation { Id = i + 1, Name = "Trading " + s, Pos = new LivingSystems.Core.Flight.Vec3(0, 0, 0) } });
        }
        return w;
    }

    /// <summary>Seed <paramref name="traders"/>/<paramref name="pirates"/> spread over 20 systems, then half a sim hour.</summary>
    static (double seed, double step) Time(int traders, int pirates)
    {
        var w = Make(20); var rng = new Random(7);
        w.Focus = w.Config.Systems.ToList();
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < 20; i++) w.SeedAt("S" + i, traders / 20, rng, 1000, pirates / 20, warm: true);
        var seed = sw.Elapsed.TotalMilliseconds;
        sw.Restart();
        for (var t = 1015.0; t <= 1000 + 1800; t += 15) w.Step(t, rng);
        return (seed, sw.Elapsed.TotalMilliseconds);
    }

    [Fact]
    public void Seeding_is_linear_and_stepping_stays_in_budget()
    {
        Time(100, 20);   // warm-up (JIT)
        var small = Time(250, 50);
        var big = Time(1000, 200);
        // budget per tick at 1,200 agents: the Dedi steps every 15 s; seconds here would mean a stalled server
        Assert.True(big.step / 120 < 250, $"step {big.step / 120:0.0} ms at 1000/200");
        Assert.True(big.seed < 8000,   // Debug build; the ratio below is the real guard
                    $"seeding 1200 agents took {big.seed:0} ms");
        Assert.True(big.seed / Math.Max(1, small.seed) < 10, $"seed {small.seed:0} → {big.seed:0} ms for 4× the agents");
        Assert.True(big.step / Math.Max(1, small.step) < 32, $"step {small.step:0} → {big.step:0} ms for 4× the agents");
    }
}
