using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class ScreenOptionsTests
{
    [Fact]
    public void MissingOptionsDoNotGrantInput()
    {
        var o = ScreenOptions.From(new WidgetConfig { Type = "screen" });
        Assert.Equal("extend", o.Mode);
        Assert.Equal(30, o.Fps);
        Assert.Equal(8_000_000, o.Bitrate);
        Assert.False(o.Control);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(15, 15)]
    [InlineData(30, 30)]
    [InlineData(60, 30)]
    [InlineData(0, 30)]
    [InlineData(5, 30)]
    public void FpsNormalizes(int input, int expected)
    {
        var w = new WidgetConfig { Type = "screen" };
        w.Set("fps", input);
        Assert.Equal(expected, ScreenOptions.From(w).Fps);
    }

    [Theory]
    [InlineData("low", 4_000_000)]
    [InlineData("medium", 8_000_000)]
    [InlineData("high", 16_000_000)]
    [InlineData("nope", 8_000_000)]
    public void QualityMapsToBitrate(string quality, int expected)
    {
        var w = new WidgetConfig { Type = "screen" };
        w.Set("quality", quality);
        Assert.Equal(expected, ScreenOptions.From(w).Bitrate);
    }

    [Theory]
    [InlineData("mirror", "mirror")]
    [InlineData("extend", "extend")]
    [InlineData("garbage", "extend")]
    [InlineData(null, "extend")]
    public void ModeNormalizes(string? mode, string expected)
    {
        var w = new WidgetConfig { Type = "screen" };
        if (mode is not null) w.Set("mode", mode);
        Assert.Equal(expected, ScreenOptions.From(w).Mode);
    }

    [Fact]
    public void MalformedOptionTypesDoNotThrow()
    {
        var w = new WidgetConfig { Type = "screen", Options = new Dictionary<string, JsonElement> {
            ["fps"] = JsonSerializer.SerializeToElement("30"),
            ["quality"] = JsonSerializer.SerializeToElement(true),
            ["mode"] = JsonSerializer.SerializeToElement(42),
            ["control"] = JsonSerializer.SerializeToElement("yes"),
            ["cursor"] = JsonSerializer.SerializeToElement(7),
        } };
        var o = ScreenOptions.From(w);
        Assert.Equal("extend", o.Mode);
        Assert.Equal(30, o.Fps);
        Assert.Equal(8_000_000, o.Bitrate);
        Assert.False(o.Control);
        Assert.Equal("embedded", o.Cursor);
    }

    [Fact]
    public void ControlDefaultsFalseAndCanBeEnabled()
    {
        var w = new WidgetConfig { Type = "screen" };
        Assert.False(ScreenOptions.From(w).Control);
        w.Set("control", true);
        Assert.True(ScreenOptions.From(w).Control);
    }

    [Fact]
    public void CursorNormalizes()
    {
        var w = new WidgetConfig { Type = "screen" };
        w.Set("cursor", "hidden");
        Assert.Equal("hidden", ScreenOptions.From(w).Cursor);
        w.Set("cursor", "bogus");
        Assert.Equal("embedded", ScreenOptions.From(w).Cursor);
    }
}
