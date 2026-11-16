using LivingSystems.Core;
using Xunit;

public class StructureTagTests
{
    [Fact]
    public void Apply_then_strip_round_trips() =>
        Assert.Equal("Kestrel", StructureTag.Strip(StructureTag.Apply("Kestrel")));

    [Fact]
    public void Apply_is_idempotent() =>
        Assert.Equal(StructureTag.Apply("Kestrel"), StructureTag.Apply(StructureTag.Apply("Kestrel")));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Kestrel")]
    [InlineData("My «LS» ship")]   // marker not at the start: a player's ship, never ours
    public void Untagged_names_are_never_ours(string name) =>
        Assert.False(StructureTag.IsTagged(name));
}
