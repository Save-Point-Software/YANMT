using System.Text.Json;
using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Status;

public class StatusColorsTests
{
    [Fact]
    public void Defaults_are_valid_and_distinct()
    {
        var c = StatusColors.Default;
        Assert.True(c.IsValid());
        Assert.Equal(5, new[] { c.Normal, c.Warning, c.Critical, c.Unknown, c.Orange }.Distinct().Count());
        Assert.Equal("#F28C28", c.Orange);
    }

    [Theory]
    [InlineData(NodeColor.Down, "#D92D20")]
    [InlineData(NodeColor.NoSnmp, "#F28C28")]
    [InlineData(NodeColor.Attention, "#F2C200")]
    [InlineData(NodeColor.Normal, "#2E9E44")]
    [InlineData(NodeColor.NoData, "#9AA0A6")]
    public void For_maps_each_node_color(NodeColor color, string expected) =>
        Assert.Equal(expected, StatusColors.Default.For(color));

    [Fact]
    public void Setting_saved_before_orange_existed_gets_the_default_orange()
    {
        // Same options as SettingsService.
        var json = """{"normal":"#00AA00","warning":"#FF9900","critical":"#AA0000","unknown":"#888888"}""";
        var c = JsonSerializer.Deserialize<StatusColors>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(new StatusColors("#00AA00", "#FF9900", "#AA0000", "#888888", "#F28C28"), c);
        Assert.True(c.IsValid());
    }

    [Fact]
    public void Orange_round_trips()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web);
        var custom = StatusColors.Default with { Orange = "#FF6600" };
        Assert.Equal(custom, JsonSerializer.Deserialize<StatusColors>(JsonSerializer.Serialize(custom, options), options));
    }

    [Theory]
    [InlineData(Severity.Normal, "#2E9E44")]
    [InlineData(Severity.Warning, "#F2C200")]
    [InlineData(Severity.Critical, "#D92D20")]
    [InlineData(Severity.Unknown, "#9AA0A6")]
    public void For_maps_each_severity(Severity severity, string expected)
    {
        Assert.Equal(expected, StatusColors.Default.For(severity));
    }

    [Theory]
    [InlineData("red")]
    [InlineData("#FFF")]
    [InlineData("#12345G")]
    [InlineData("url(javascript:alert(1))")]
    public void Rejects_anything_but_six_digit_hex(string bad)
    {
        Assert.False((StatusColors.Default with { Warning = bad }).IsValid());
        Assert.False((StatusColors.Default with { Orange = bad }).IsValid());
    }
}
