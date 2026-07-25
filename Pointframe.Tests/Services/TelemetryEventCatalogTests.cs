using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class TelemetryEventCatalogTests
{
    [Fact]
    public void Validate_WhenKnownEventIncludesRequiredProperties_ReturnsValid()
    {
        foreach (var definition in TelemetryEventCatalog.All)
        {
            var properties = definition.RequiredProperties.ToDictionary(
                key => key,
                _ => "value",
                StringComparer.Ordinal);

            var result = TelemetryEventCatalog.Validate(definition.Name, properties);

            Assert.True(result.IsValid, $"Expected event '{definition.Name}' to validate.");
            Assert.True(result.IsKnownEvent);
            Assert.NotNull(result.Definition);
            Assert.Empty(result.MissingProperties);
        }
    }

    [Fact]
    public void Validate_WhenKnownEventMissesRequiredProperty_ReturnsMissingPropertyResult()
    {
        var props = new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "region",
        };

        var result = TelemetryEventCatalog.Validate(TelemetryEvents.SnipStarted, props);

        Assert.False(result.IsValid);
        Assert.True(result.IsKnownEvent);
        Assert.Contains(TelemetryPropertyKeys.Source, result.MissingProperties);
    }

    [Fact]
    public void Validate_WhenEventIsUnknown_ReturnsUnknownEventResult()
    {
        var result = TelemetryEventCatalog.Validate("not_registered", null);

        Assert.False(result.IsValid);
        Assert.False(result.IsKnownEvent);
        Assert.Null(result.Definition);
        Assert.Empty(result.MissingProperties);
    }

    [Fact]
    public void Catalog_ContainsExpectedDiagnosticEvents()
    {
        Assert.True(TelemetryEventCatalog.TryGetDefinition(TelemetryEvents.AppHeartbeat, out var heartbeat));
        Assert.True(TelemetryEventCatalog.TryGetDefinition(TelemetryEvents.StartupCompleted, out var startup));
        Assert.True(TelemetryEventCatalog.TryGetDefinition(TelemetryEvents.UnhandledException, out var exception));

        Assert.Equal(TelemetryChannel.Diagnostic, heartbeat.Channel);
        Assert.Equal(TelemetryChannel.Diagnostic, startup.Channel);
        Assert.Equal(TelemetryChannel.Diagnostic, exception.Channel);
    }
}
