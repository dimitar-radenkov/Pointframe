using System.Globalization;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class WatermarkTokenResolverTests
{
    private static readonly DateTimeOffset Sample =
        new(2026, 3, 18, 14, 5, 9, TimeSpan.FromHours(2));

    [Fact]
    public void Resolve_DateOnly_UsesShortDate()
    {
        var expected = Sample.ToString("d", CultureInfo.CurrentCulture);
        Assert.Equal(expected, WatermarkTokenResolver.Resolve(WatermarkTextTemplate.DateOnly, Sample));
    }

    [Fact]
    public void Resolve_TimeOnly_UsesShortTime()
    {
        var expected = Sample.ToString("t", CultureInfo.CurrentCulture);
        Assert.Equal(expected, WatermarkTokenResolver.Resolve(WatermarkTextTemplate.TimeOnly, Sample));
    }

    [Fact]
    public void Resolve_DateTime_UsesGeneralShort()
    {
        var expected = Sample.ToString("g", CultureInfo.CurrentCulture);
        Assert.Equal(expected, WatermarkTokenResolver.Resolve(WatermarkTextTemplate.DateTime, Sample));
    }

    [Fact]
    public void Resolve_TimezoneOnly_FormatsOffset()
    {
        Assert.Equal("UTC+02:00", WatermarkTokenResolver.Resolve(WatermarkTextTemplate.TimezoneOnly, Sample));
    }

    [Fact]
    public void Resolve_NegativeOffset_FormatsWithMinusSign()
    {
        var negative = new DateTimeOffset(2026, 3, 18, 14, 5, 9, TimeSpan.FromHours(-5));
        Assert.Equal("UTC-05:00", WatermarkTokenResolver.Resolve(WatermarkTextTemplate.TimezoneOnly, negative));
    }

    [Fact]
    public void Resolve_DateTimeWithTimezone_IncludesBothParts()
    {
        var expectedDateTime = Sample.ToString("g", CultureInfo.CurrentCulture);
        var result = WatermarkTokenResolver.Resolve(WatermarkTextTemplate.DateTimeWithTimezone, Sample);

        Assert.Equal($"{expectedDateTime} UTC+02:00", result);
    }

    [Fact]
    public void Resolve_UnknownEnumValue_FallsBackToDateTime()
    {
        var expected = Sample.ToString("g", CultureInfo.CurrentCulture);
        var result = WatermarkTokenResolver.Resolve((WatermarkTextTemplate)999, Sample);

        Assert.Equal(expected, result);
    }
}
