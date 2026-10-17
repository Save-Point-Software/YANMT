using Yanmt.Core.Status;

namespace Yanmt.Core.Tests.Status;

public class StatusColorsTests
{
    [Fact]
    public void Defaults_are_valid_and_distinct()
    {
        var c = StatusColors.Default;
        Assert.True(c.IsValid());
        Assert.Equal(4, new[] { c.Normal, c.Warning, c.Critical, c.Unknown }.Distinct().Count());
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
    }
}
