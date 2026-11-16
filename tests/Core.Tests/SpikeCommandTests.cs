using LivingSystems.Core;
using Xunit;

public class SpikeCommandTests
{
    [Fact]
    public void Non_commands_are_ignored()
    {
        Assert.False(SpikeCommand.TryParse("hello there", out _));
        Assert.False(SpikeCommand.TryParse("/lsx spawn", out _));
        Assert.False(SpikeCommand.TryParse(null, out _));
    }

    [Fact]
    public void Spawn_with_args()
    {
        Assert.True(SpikeCommand.TryParse("  /LS Spawn CV_Freighter_HarkonA 3 zirax ", out var c));
        Assert.Equal("spawn", c.Verb);
        Assert.Equal("CV_Freighter_HarkonA", c.Arg(0, SpikeCommand.DefaultPrefab));
        Assert.Equal(3, c.IntArg(1, 1, 1, 10));
        Assert.Equal(2, SpikeCommand.FactionGroup(c.Arg(2, "polaris")));
    }

    [Fact]
    public void Bad_numbers_fall_back_and_clamp()
    {
        SpikeCommand.TryParse("/ls spawn x 999", out var c);
        Assert.Equal(10, c.IntArg(1, 1, 1, 10));
        SpikeCommand.TryParse("/ls move pos abc", out c);
        Assert.Equal(20f, c.FloatArg(1, 20f, 0f, 300f));
    }

    [Fact]
    public void Unknown_verb_and_bare_command_mean_help()
    {
        SpikeCommand.TryParse("/ls", out var a);
        SpikeCommand.TryParse("/ls frobnicate", out var b);
        Assert.Equal("help", a.Verb);
        Assert.Equal("help", b.Verb);
    }

    [Fact]
    public void Unknown_faction_defaults_to_polaris() => Assert.Equal(7, SpikeCommand.FactionGroup("whatever"));

    [Theory]
    [InlineData("polaris", 7, 0)]
    [InlineData("0:12", 0, 12)]
    [InlineData("7:3", 7, 3)]
    [InlineData("x:3", 7, 0)]
    [InlineData("pirates", 16, 0)]
    [InlineData("16:0", 16, 0)]
    [InlineData("warlord", 27, 0)]
    public void Faction_spec(string spec, int group, int id)
    {
        SpikeCommand.ParseFaction(spec, out var g, out var i);
        Assert.Equal(group, g); Assert.Equal(id, i);
    }

    [Fact]
    public void Comms_is_its_own_command()
    {
        Assert.True(SpikeCommand.TryParse("/comms", out var c));
        Assert.Equal("comms", c.Verb);
        Assert.False(SpikeCommand.TryParse("/commsx", out _));
    }
}
