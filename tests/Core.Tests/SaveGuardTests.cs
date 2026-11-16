using LivingSystems.Core;
using Xunit;

public class SaveGuardTests
{
    [Theory]
    [InlineData(@"S:\steamapps\common\Empyrion - Dedicated Server\Saves\Games\LSTest")]
    [InlineData(@"S:\steamapps\common\Empyrion - Dedicated Server\Saves\Games\DediGame")]
    [InlineData("/x/Saves/Games/fresh one/")]
    public void Any_save_runs_the_mod(string path) => Assert.True(SaveGuard.IsAllowed(path));

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void No_save_path_no_mod(string path) => Assert.False(SaveGuard.IsAllowed(path));

    [Fact]
    public void Save_name_is_the_last_folder() => Assert.Equal("LSTest", SaveGuard.SaveName(@"S:\x\Saves\Games\LSTest\"));
}
