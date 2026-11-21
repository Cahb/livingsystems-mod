using System;
using System.Collections.Generic;
using System.Linq;
using LivingSystems.Core.Flight;
using LivingSystems.Core.Sim;
using Xunit;

public class ShipyardTests
{
    /// <summary>A one-system world ("A") with a station — a shipyard or not — and one trader just lost there.</summary>
    static (World w, SimAgent lost) Lost(string station)
    {
        var w = new World(); w.Config.Systems = new List<string> { "A" };
        w.SetStations("A", new List<SimStation> { new SimStation { Id = 1, Name = station, Pos = new Vec3(0, 0, 0) } });
        w.Focus = new List<string> { "A" };
        var lost = w.Add(new SimAgent { Id = "t1", Name = "Kestrel", Kind = "trader", System = "A", State = "dead", Until = 100, Ship = "CV_Freighter_SparrowA" });
        w.LastTick = 0;
        return (w, lost);
    }

    [Fact]
    public void A_loss_is_rebuilt_at_the_nearest_yard_and_starts_there()
    {
        var (w, a) = Lost("Industrial Shipyard");
        var rng = new Random(1);
        w.Step(200, rng);
        Assert.Equal("build", a.State);
        Assert.Equal("A", a.System);
        Assert.Equal("Kestrel", a.Extra["replaces"]);
        Assert.Contains(w.Events, e => e.Kind == "laid" && e.Text.Contains("Industrial Shipyard"));
        var done = a.Until;
        Assert.InRange(done - 100, w.Config.BuildSeconds * 0.5, w.Config.BuildSeconds * 2);   // its hull's build time (a new hull: any size)
        w.Step(done + 1, rng);
        Assert.Equal("dock", a.State);
        Assert.Contains(w.Events, e => e.Kind == "new" && e.Text.Contains("fresh off the slip"));
    }

    [Fact]
    public void While_it_is_built_the_slot_is_held_and_word_gets_around()
    {
        var (w, a) = Lost("Industrial Shipyard");
        w.Config.TradersPerSystem = 1;   // exactly the one lost
        var rng = new Random(1);
        w.Step(200, rng);
        Assert.Equal("build", a.State);
        var added = w.Populate(rng, 200);
        Assert.DoesNotContain(added, x => x.IsTrader);   // no instant replacement on top of the one on the slip
        var teller = w.Add(new SimAgent { Id = "t2", Name = "Penny", Kind = "trader", System = "A", State = "dock", Until = 1e9 });
        Assert.Contains(w.Rumours(teller, "", 300, (x, y) => y), r => r.Contains("on the slip") && r.Contains("replacing Kestrel"));
    }

    [Fact]
    public void No_yard_in_range_means_as_before()
    {
        var (w, a) = Lost("Trading Station Lambda");
        w.Step(200, new Random(1));
        Assert.Equal("dock", a.State);
        Assert.Contains(w.Events, e => e.Kind == "new" && e.Text.Contains("enters service"));
        var (w2, b) = Lost("Industrial Shipyard");
        w2.Config.YardBuilds = false;
        w2.Step(200, new Random(1));
        Assert.Equal("dock", b.State);
    }
}
