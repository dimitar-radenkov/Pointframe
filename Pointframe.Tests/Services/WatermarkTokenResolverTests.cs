using System.Globalization;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class WatermarkTokenResolverTests
{
    private static readonly DateTimeOffset Sample =
        new(2026, 3, 18, 14, 5, 9, TimeSpan.FromHours(2));

    [Fact]
    public void Resolve_EmptyTemplate_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, WatermarkTokenResolver.Resolve(string.Empty, Sample));
    }

    [Fact]
    public void Resolve_DateToken_UsesShortDate()
    {
        var expected = Sample.ToString("d", CultureInfo.CurrentCulture);
        Assert.Equal(expected, WatermarkTokenResolver.Resolve("{date}", Sample));
    }

    [Fact]
    public void Resolve_TimeToken_UsesShortTime()
    {
        var expected = Sample.ToString("t", CultureInfo.CurrentCulture);
        Assert.Equal(expected, WatermarkTokenResolver.Resolve("{time}", Sample));
    }

    [Fact]
    public void Resolve_DateTimeToken_UsesGeneralShort()
    {
        var expected = Sample.ToString("g", CultureInfo.CurrentCulture);
        Assert.Equal(expected, WatermarkTokenResolver.Resolve("{datetime}", Sample));
    }

    [Fact]
    public void Resolve_TimezoneToken_FormatsOffset()
    {
        Assert.Equal("UTC+02:00", WatermarkTokenResolver.Resolve("{timezone}", Sample));
    }

    [Fact]
    public void Resolve_NegativeOffset_FormatsWithMinusSign()
    {
        var negative = new DateTimeOffset(2026, 3, 18, 14, 5, 9, TimeSpan.FromHours(-5));
        Assert.Equal("UTC-05:00", WatermarkTokenResolver.Resolve("{timezone}", negative));
    }

    [Fact]
    public void Resolve_TokensAreCaseInsensitive()
    {
        Assert.Equal("UTC+02:00", WatermarkTokenResolver.Resolve("{TimeZone}", Sample));
    }

    [Fact]
    public void Resolve_LiteralTextWithToken_KeepsLiteral()
    {
        var result = WatermarkTokenResolver.Resolve("Captured {timezone}", Sample);
        Assert.Equal("Captured UTC+02:00", result);
    }
}
