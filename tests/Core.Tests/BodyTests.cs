using LivingSystems.Core.Sim;
using Xunit;

public class BodyTests
{
    [Fact]
    public void Range_has_hysteresis_out_past_2500_in_within_2000()
    {
        Assert.False(BodyRules.Away(true, 2400f));
        Assert.True(BodyRules.Away(true, 2600f));
        Assert.True(BodyRules.Away(false, 0f));          // no watcher in the playfield = out
        Assert.False(BodyRules.Near(true, 2200f));       // between the lines: stays where it is
        Assert.True(BodyRules.Near(true, 1900f));
        Assert.False(BodyRules.Near(false, 0f));
    }

    [Fact]
    public void Away_clock_needs_the_grace_straight()
    {
        var c = new AwayClock();
        Assert.False(c.Tick(true, 0));
        Assert.False(c.Tick(true, 4));
        Assert.False(c.Tick(false, 4.5));   // came back in range: starts over
        Assert.False(c.Tick(true, 5));
        Assert.False(c.Tick(true, 9.9));
        Assert.True(c.Tick(true, 10));
        Assert.False(c.Tick(true, 10.5));   // reset after firing
    }

    [Fact]
    public void Gone_is_not_dead_only_evidence_kills()
    {
        Assert.Equal(BodyFate.Gone, BodyRules.Judge(false, false, false));
        Assert.Equal(BodyFate.Here, BodyRules.Judge(true, false, false));
        Assert.Equal(BodyFate.Dead, BodyRules.Judge(false, true, false));
        Assert.Equal(BodyFate.Claimed, BodyRules.Judge(true, true, true));
    }

    [Fact]
    public void Messages_round_trip_through_the_wire_split()
    {
        string[] Wire(BodyMsg m) => string.Join("|", m.Fields()).Split('|');
        var back = BodyMsg.Back("hire", "h12", "range", true, 1.5f, -2f, 3000f, "SteelPlate:40,MoneyCard:5", BodyProgress.Write(new[] {
            new System.Collections.Generic.KeyValuePair<string, string>("tname", "Wreck of A;B=C|D"),
            new System.Collections.Generic.KeyValuePair<string, string>("share", "0.25") }), 777);
        var r = BodyMsg.Parse(Wire(back));
        Assert.Equal("bodyback", r.Verb); Assert.Equal("hire", r.Kind); Assert.Equal("h12", r.Id); Assert.Equal("range", r.Why);
        Assert.True(r.HasPos); Assert.Equal(3000f, r.Z); Assert.Equal(777, r.Ghost);
        Assert.Equal("SteelPlate:40,MoneyCard:5", r.Hold);
        var p = BodyProgress.Read(r.Progress);
        Assert.Equal("Wreck of A;B=C|D", BodyProgress.Get(p, "tname"));
        Assert.Equal(0.25, BodyProgress.Num(p, "share"), 6);

        var ad = BodyMsg.Parse(Wire(BodyMsg.Adopt("hire", "h12", 0, 55, false, 0, 0, 0, "", "pid=12")));
        Assert.Equal("adopt", ad.Verb); Assert.Equal(0, ad.Entity); Assert.Equal(55, ad.Ghost); Assert.False(ad.HasPos);

        var sp = BodyMsg.Parse(Wire(BodyMsg.Spawn("hire", "h12", "LS_Tug", 1, 2, 3, 90, "Iron Mule", 7)));
        Assert.Equal("LS_Tug", sp.Prefab); Assert.Equal("Iron Mule", sp.Name); Assert.Equal(90f, sp.Yaw); Assert.Equal(7, sp.Faction);

        Assert.Equal(4242, BodyMsg.Parse(Wire(BodyMsg.Spawned("hire", "h12", 4242))).Entity);
        Assert.Equal("nospawn", BodyMsg.Parse(Wire(BodyMsg.Dead("hire", "h12", "nospawn"))).Why);
        Assert.Null(BodyMsg.Parse(new[] { "simend", "a1", "left" }));
    }
}
