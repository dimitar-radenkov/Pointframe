using System.Reflection;
using Pointframe.Services;
using Xunit;

namespace Pointframe.Tests.Services;

public sealed class TelemetryEventCatalogTests
{
    private static string[] DeclaredEventNames()
    {
        return typeof(TelemetryEvents)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToArray();
    }

    [Fact]
    public void Catalog_RegistersEveryDeclaredEventConstant()
    {
        var unregistered = DeclaredEventNames()
            .Where(name => !TelemetryEventCatalog.TryGetDefinition(name, out _))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            unregistered.Length == 0,
            $"TelemetryEvents constants with no catalog definition: {string.Join(", ", unregistered)}");
    }

    [Fact]
    public void Catalog_ContainsNoDefinitionWithoutAnEventConstant()
    {
        var declared = DeclaredEventNames();
        var undeclared = TelemetryEventCatalog.All
            .Select(definition => definition.Name)
            .Where(name => !declared.Contains(name, StringComparer.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.True(
            undeclared.Length == 0,
            $"Catalog definitions with no TelemetryEvents constant: {string.Join(", ", undeclared)}");
    }

    [Fact]
    public void Catalog_EveryDefinitionIsReachableByItsOwnName()
    {
        // Guards the dictionary literal against [TelemetryEvents.A] = Product(TelemetryEvents.B).
        foreach (var definition in TelemetryEventCatalog.All)
        {
            Assert.True(
                TelemetryEventCatalog.TryGetDefinition(definition.Name, out var found),
                $"Definition '{definition.Name}' is registered under a different key.");
            Assert.Same(definition, found);
        }
    }

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
    public void Validate_WhenEventCarriesUndeclaredProperty_ReportsItAsUnknown()
    {
        var props = new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.Type] = "region",
            [TelemetryPropertyKeys.Source] = "tray",
            ["file_path"] = @"C:\captures\shot.png",
        };

        var result = TelemetryEventCatalog.Validate(TelemetryEvents.SnipStarted, props);

        Assert.False(result.IsValid);
        Assert.True(result.IsKnownEvent);
        Assert.Empty(result.MissingProperties);
        Assert.Equal(["file_path"], result.UnknownProperties);
    }

    [Fact]
    public void Validate_WhenEventCarriesDeclaredOptionalProperty_ReturnsValid()
    {
        var props = new Dictionary<string, string>
        {
            [TelemetryPropertyKeys.ExceptionType] = "InvalidOperationException",
            [TelemetryPropertyKeys.Context] = "dispatcher",
            [TelemetryPropertyKeys.LastAction] = "capture_pinned",
        };

        var result = TelemetryEventCatalog.Validate(TelemetryEvents.UnhandledException, props);

        Assert.True(result.IsValid);
        Assert.Empty(result.UnknownProperties);
    }

    [Fact]
    public void Catalog_OptionalPropertiesNeverRepeatRequiredOnes()
    {
        foreach (var definition in TelemetryEventCatalog.All)
        {
            var overlap = definition.OptionalProperties.Intersect(definition.RequiredProperties).ToArray();

            Assert.True(
                overlap.Length == 0,
                $"Event '{definition.Name}' declares {string.Join(", ", overlap)} as both required and optional.");
        }
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
