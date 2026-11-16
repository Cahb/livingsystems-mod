using System;
using System.Linq;
using LivingSystems.Core;
using Xunit;

public class EscortTests
{
    [Fact]
    public void Fee_grows_with_distance_and_risk_and_rounds_to_50()
    {
        Assert.Equal(1800, Escort.Fee(Risk.Quiet, 2000));
        Assert.True(Escort.Fee(Risk.Moderate, 2000) > Escort.Fee(Risk.Quiet, 2000));
        Assert.True(Escort.Fee(Risk.Hot, 2000) > Escort.Fee(Risk.Moderate, 2000));
        Assert.True(Escort.Fee(Risk.Quiet, 8000) > Escort.Fee(Risk.Quiet, 2000));
        Assert.Equal(0, Escort.Fee(Risk.Hot, 3333) % 50);
    }

    [Theory]
    [InlineData(Risk.Quiet, 0.15)]
    [InlineData(Risk.Moderate, 0.5)]
    [InlineData(Risk.Hot, 0.85)]
    public void Attack_rate_matches_the_quoted_risk(Risk risk, double expected)
    {
        var rng = new Random(7);
        var n = 4000;
        var attacks = Enumerable.Range(0, n).Count(_ => Escort.Roll(rng, 3000, risk).Attack);
        Assert.InRange(attacks / (double)n, expected - 0.04, expected + 0.04);
    }

    [Fact]
    public void Attacks_come_mid_trip_with_pirates_and_quiet_trips_have_none()
    {
        var rng = new Random(1);
        for (var i = 0; i < 500; i++)
        {
            var t = Escort.Roll(rng, 3000);
            if (t.Attack) { Assert.InRange(t.AttackAt, 0.3, 0.7); Assert.NotEmpty(t.Pirates); }
            else Assert.Empty(t.Pirates);
        }
    }
}

public class EscortAskTests
{
    static double Share(Func<string, LivingSystems.Core.EscortAsk> ask) => Enumerable.Range(0, 2000).Count(i => ask("leg" + i).Wants) / 2000.0;

    [Fact]
    public void Escort_is_situational_not_always_yes()
    {
        Assert.False(LivingSystems.Core.Escort.Ask("x", false, LivingSystems.Core.Risk.Hot, 50000, 0, false).Wants);   // inbound: never
        Assert.False(LivingSystems.Core.Escort.Ask("x", true, LivingSystems.Core.Risk.Hot, 50000, 0, true).Wants);    // cagey: never
        var hotRich = Share(s => LivingSystems.Core.Escort.Ask(s, true, LivingSystems.Core.Risk.Hot, 50000, 0, false));
        var quietEmpty = Share(s => LivingSystems.Core.Escort.Ask(s, true, LivingSystems.Core.Risk.Quiet, 0, 0, false));
        var hotArmed = Share(s => LivingSystems.Core.Escort.Ask(s, true, LivingSystems.Core.Risk.Hot, 5000, 1, false));
        Assert.InRange(hotRich, 0.85, 1.0);
        Assert.InRange(quietEmpty, 0.0, 0.1);
        Assert.InRange(hotArmed, 0.1, 0.4);
    }

    [Fact]
    public void The_answer_is_fixed_per_leg()
    {
        for (var i = 0; i < 50; i++)
        {
            var a = LivingSystems.Core.Escort.Ask("t7|QAMT|Lambda|out" + i, true, LivingSystems.Core.Risk.Moderate, 3000, 0, false);
            var b = LivingSystems.Core.Escort.Ask("t7|QAMT|Lambda|out" + i, true, LivingSystems.Core.Risk.Moderate, 3000, 0, false);
            Assert.Equal(a.Wants, b.Wants); Assert.Equal(a.Line, b.Line);
        }
    }
}

public class HurtEscortTests
{
    [Fact]
    public void A_badly_hurt_ship_wants_an_escort_and_pays_more()
    {
        var yes = Enumerable.Range(0, 500).Select(i => LivingSystems.Core.Escort.Ask("leg" + i, true, LivingSystems.Core.Risk.Quiet, 0, 1, true, 0.3)).ToList();
        Assert.InRange(yes.Count(x => x.Wants) / 500.0, 0.8, 1.0);    // even empty, armed and cagey
        Assert.All(yes.Where(x => x.Wants), x => Assert.Equal(1.5, x.FeeFactor));
        Assert.False(LivingSystems.Core.Escort.Ask("x", false, LivingSystems.Core.Risk.Hot, 0, 0, false, 0.2).Wants);   // inbound: still no
    }
}
