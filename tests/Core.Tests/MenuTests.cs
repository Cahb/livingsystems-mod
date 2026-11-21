using LivingSystems.Core;
using Xunit;

public class MenuTests
{
    [Fact]
    public void Links_carry_the_version_and_plain_lines_stay_plain()
    {
        var p = new MenuPage { Body = "Channel open." }.Text("Range 300 m").Link("Talk", () => { });
        var body = MenuRender.Body(p, 0, 7);
        Assert.Contains("Channel open.", body);
        Assert.Contains("Range 300 m", body);
        Assert.Contains("<link=\"7:l1\">", body);
        Assert.DoesNotContain("<link=\"7:l0\">", body);   // the plain line
    }

    [Fact]
    public void Parse_round_trips_and_rejects_foreign_links()
    {
        Assert.True(MenuRender.Parse("12:l3", out var v, out var id));
        Assert.Equal(12, v); Assert.Equal("l3", id);
        Assert.True(MenuRender.Parse("3:pg:next", out _, out var pg));
        Assert.Equal(MenuRender.Next, pg);
        Assert.False(MenuRender.Parse("", out _, out _));
        Assert.False(MenuRender.Parse("q1", out _, out _));
    }

    [Fact]
    public void Long_pages_split_with_a_footer_and_clamp()
    {
        var p = new MenuPage { PerPage = 3 };
        for (var i = 0; i < 7; i++) p.Link("ship " + i, () => { });
        Assert.Equal(3, p.Pages);
        var first = MenuRender.Body(p, 0, 1);
        Assert.Contains("ship 0", first); Assert.DoesNotContain("ship 3", first);
        Assert.Contains("1 / 3", first);
        Assert.Contains(MenuRender.Next, first); Assert.DoesNotContain(MenuRender.Prev, first);   // no prev on page 1
        var last = MenuRender.Body(p, 99, 1);   // clamped to the last page
        Assert.Contains("ship 6", last); Assert.Contains("3 / 3", last);
        Assert.DoesNotContain(MenuRender.Next, last);
        Assert.DoesNotContain(" / ", MenuRender.Body(new MenuPage().Link("one", () => { }), 0, 1));   // one page: no footer
    }

    [Fact]
    public void Find_gets_the_line_a_link_points_at()
    {
        var hit = 0;
        var p = new MenuPage().Text("x").Link("a", () => hit = 1).Link("b", () => hit = 2);
        p.Find("l2").Do();
        Assert.Equal(2, hit);
        Assert.Null(p.Find("l9"));
    }
}
