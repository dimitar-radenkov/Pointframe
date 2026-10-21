using System.IO;
using System.Text.Json;
using Moq;
using Pointframe.Cli;
using Pointframe.Engine.Automation.Models;
using Xunit;

namespace Pointframe.Tests.Cli;

public sealed class VerificationSpecV1CompatibilityTests : IDisposable
{
    private readonly VerificationFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public void Load_GatesOnlyMinimalSpec_PreservesDefaultsAndProjectRoot()
    {
        var spec = Load("""
            {
              "schemaVersion": 1,
              "gates": [ { "id": "build", "run": "dotnet build" } ]
            }
            """);

        Assert.Equal(1, spec.SchemaVersion);
        Assert.Null(spec.App);
        var gate = Assert.Single(spec.Gates);
        Assert.Equal("build", gate.Id);
        Assert.Equal("dotnet build", gate.Run);
        Assert.Equal(_fixture.Root, gate.WorkingDirectory);
        Assert.Equal(30, gate.TimeoutMinutes);
        Assert.Empty(spec.Scenarios);
    }

    [Fact]
    public void Load_GateWithEveryOptionalField_PreservesValues()
    {
        var spec = Load("""
            {
              "schemaVersion": 1,
              "gates": [ {
                "id": "tests.v1",
                "run": "dotnet test Pointframe.Tests/Pointframe.Tests.csproj",
                "workingDirectory": "tools",
                "timeoutMinutes": 47
              } ]
            }
            """);

        var gate = Assert.Single(spec.Gates);
        Assert.Equal("tests.v1", gate.Id);
        Assert.Equal("dotnet test Pointframe.Tests/Pointframe.Tests.csproj", gate.Run);
        Assert.Equal(Path.Combine(_fixture.Root, "tools"), gate.WorkingDirectory);
        Assert.Equal(47, gate.TimeoutMinutes);
    }

    [Theory]
    [InlineData("{ \"environmentVariable\": \"POINTFRAME_TEST_DATA\" }", "POINTFRAME_TEST_DATA", null)]
    [InlineData("{ \"argument\": \"--data-dir\" }", null, "--data-dir")]
    public void Load_AppWithEveryOptionalFieldAndIsolationForm_PreservesValues(string isolation, string? variable, string? argument)
    {
        var spec = Load($$"""
            {
              "schemaVersion": 1,
              "app": {
                "id": "desktop_app-1",
                "executable": "bin/App.exe",
                "arguments": [ "--profile", "test" ],
                "workingDirectory": "runtime",
                "isolation": {{isolation}}
              },
              "gates": [ { "id": "build", "run": "dotnet build" } ]
            }
            """);

        var app = Assert.IsType<VerificationApp>(spec.App);
        Assert.Equal("desktop_app-1", app.Id);
        Assert.Equal(Path.Combine(_fixture.Root, "bin", "App.exe"), app.ExecutablePath);
        Assert.Equal(["--profile", "test"], app.Arguments);
        Assert.Equal(Path.Combine(_fixture.Root, "runtime"), app.WorkingDirectory);
        Assert.NotNull(app.Isolation);
        Assert.Equal(variable, app.Isolation.EnvironmentVariable);
        Assert.Equal(argument, app.Isolation.Argument);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Load_EmptyIsolationEnvironmentVariable_IsSpecError(string variable)
    {
        var exception = Assert.Throws<VerificationSpecException>(() => Load($$"""
            { "schemaVersion": 1, "app": { "id": "a", "executable": "app.exe", "isolation": { "environmentVariable": "{{variable}}" } }, "gates": [ { "id": "g", "run": "echo x" } ] }
            """));

        Assert.Contains("app.isolation.environmentVariable", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("exists")]
    [InlineData("absent")]
    [InlineData("enabled")]
    public void Load_NonComparingCheckWithExpected_IsRejected(string kind)
    {
        var exception = Assert.Throws<VerificationSpecException>(() => Load($$"""
            { "schemaVersion": 1, "app": { "id": "a", "executable": "app.exe" }, "scenarios": [ { "id": "s", "steps": [ { "check": { "kind": "{{kind}}", "automationId": "x", "expected": "ignored" } } ] } ] }
            """));

        Assert.Contains("expected is not allowed", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_DuplicatePropertiesAnywhereInSpec_AreRejected()
    {
        var exception = Assert.Throws<VerificationSpecException>(() => Load("""
            { "schemaVersion": 1, "gates": [ { "id": "g", "run": "echo x", "timeoutMinutes": 5, "timeoutMinutes": 6 } ] }
            """));

        Assert.Contains("duplicate property 'timeoutMinutes'", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Load_ScenarioWithEveryStepAndCheckKind_PreservesV1Meanings()
    {
        var spec = Load("""
            {
              "schemaVersion": 1,
              "app": { "id": "fixture", "executable": "bin/App.exe" },
              "scenarios": [ {
                "id": "all-steps",
                "criteria": [ "Text is saved." ],
                "steps": [
                  { "enterText": { "role": "Edit", "name": "Input", "text": "hello" } },
                  { "invoke": { "automationId": "save" } },
                  { "pressKeys": { "keys": [ 17, 83 ] } },
                  { "restart": {} },
                  { "check": { "kind": "exists", "automationId": "exists" } },
                  { "check": { "kind": "absent", "role": "Text", "name": "Gone" } },
                  { "check": { "kind": "enabled", "automationId": "enabled", "timeoutSeconds": 7 } },
                  { "check": { "kind": "toggleEquals", "automationId": "toggle", "expected": true } },
                  { "check": { "kind": "selectionEquals", "automationId": "selection", "expected": 3 } },
                  { "check": { "kind": "textEquals", "automationId": "text", "expected": "hello", "criterion": "C1", "timeoutSeconds": 11 } },
                  { "check": { "kind": "textEquals", "automationId": "negative", "expected": "wrong", "expectFailure": true } }
                ]
              } ]
            }
            """);

        var scenario = Assert.Single(spec.Scenarios);
        Assert.Equal("all-steps", scenario.Id);
        Assert.Equal(["Text is saved."], scenario.Criteria);
        Assert.Equal(["enterText", "invoke", "pressKeys", "restart", "check", "check", "check", "check", "check", "check", "check"], scenario.Steps.Select(step => step.Kind));
        var enterText = Assert.IsType<VerificationStep.EnterText>(scenario.Steps[0]);
        Assert.Equal(new ElementLocator(null, "Edit", "Input"), enterText.Locator);
        Assert.Equal("hello", enterText.Text);
        Assert.Equal(new ElementLocator("save", null, null), Assert.IsType<VerificationStep.Invoke>(scenario.Steps[1]).Locator);
        Assert.Equal([17, 83], Assert.IsType<VerificationStep.PressKeys>(scenario.Steps[2]).VirtualKeys);
        Assert.IsType<VerificationStep.Restart>(scenario.Steps[3]);

        var checks = scenario.Steps.OfType<VerificationStep.Check>().ToArray();
        Assert.Equal(["exists", "absent", "enabled", "toggleEquals", "selectionEquals", "textEquals", "textEquals"], checks.Select(check => check.ConditionKind));
        Assert.Equal(DesktopTestingLimits.DefaultUiCheckTimeoutSeconds, checks[0].TimeoutSeconds);
        Assert.Equal("Gone", checks[1].Locator.Name);
        Assert.Equal(7, checks[2].TimeoutSeconds);
        Assert.Equal("true", checks[3].Expected);
        Assert.Equal("3", checks[4].Expected);
        Assert.Equal("hello", checks[5].Expected);
        Assert.Equal("C1", checks[5].Criterion);
        Assert.Equal(11, checks[5].TimeoutSeconds);
        Assert.True(checks[6].ExpectFailure);
        Assert.Null(checks[6].Criterion);
    }

    [Theory]
    [InlineData("unknown top-level property", """{ "schemaVersion": 1, "mystery": true, "gates": [ { "id": "g", "run": "echo x" } ] }""")]
    [InlineData("unknown app property", """{ "schemaVersion": 1, "app": { "id": "a", "executable": "app.exe", "mystery": true }, "gates": [ { "id": "g", "run": "echo x" } ] }""")]
    [InlineData("unknown gate property", """{ "schemaVersion": 1, "gates": [ { "id": "g", "run": "echo x", "mystery": true } ] }""")]
    [InlineData("unknown scenario property", """{ "schemaVersion": 1, "app": { "id": "a", "executable": "app.exe" }, "scenarios": [ { "id": "s", "steps": [ { "restart": {} } ], "mystery": true } ] }""")]
    [InlineData("unknown step property", """{ "schemaVersion": 1, "app": { "id": "a", "executable": "app.exe" }, "scenarios": [ { "id": "s", "steps": [ { "restart": { "mystery": true } } ] } ] }""")]
    [InlineData("unknown check property", """{ "schemaVersion": 1, "app": { "id": "a", "executable": "app.exe" }, "scenarios": [ { "id": "s", "steps": [ { "check": { "kind": "exists", "automationId": "x", "mystery": true } } ] } ] }""")]
    public void Load_UnknownPropertyAtEachObjectLevel_IsRejected(string _, string json)
    {
        var exception = Assert.Throws<VerificationSpecException>(() => Load(json));

        Assert.Contains("mystery", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{ "schemaVersion": 0, "gates": [ { "id": "build", "run": "observable command" } ] }""")]
    [InlineData("""{ "schemaVersion": 2, "gates": [ { "id": "build", "run": "observable command" } ] }""")]
    [InlineData("""{ "gates": [ { "id": "build", "run": "observable command" } ] }""")]
    [InlineData("""{ "schemaVersion": "1", "gates": [ { "id": "build", "run": "observable command" } ] }""")]
    public async Task Run_UnsupportedOrMissingSchemaVersion_ExitsTwoBeforeRunningGate(string json)
    {
        var specPath = WriteSpec(json);
        var services = new VerificationFixture.Services();
        var output = new StringWriter();

        var exitCode = await services.Application(_fixture.Store, output)
            .RunAsync(new CliCommand("verify", SpecPath: specPath, VerifyAction: "run"), CancellationToken.None);

        Assert.Equal(2, exitCode);
        using var verdict = JsonDocument.Parse(output.ToString());
        Assert.Equal("spec_invalid", verdict.RootElement.GetProperty("errorCode").GetString());
        services.Commands.Verify(
            item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public async Task Run_UnknownTopLevelField_ExitsTwoBeforeRunningGate()
    {
        var specPath = WriteSpec("""
            {
              "schemaVersion": 1,
              "unknown": true,
              "gates": [ { "id": "build", "run": "observable command" } ]
            }
            """);
        var services = new VerificationFixture.Services();
        var output = new StringWriter();

        var exitCode = await services.Application(_fixture.Store, output)
            .RunAsync(new CliCommand("verify", SpecPath: specPath, VerifyAction: "run"), CancellationToken.None);

        Assert.Equal(2, exitCode);
        using var verdict = JsonDocument.Parse(output.ToString());
        Assert.Equal("spec_invalid", verdict.RootElement.GetProperty("errorCode").GetString());
        services.Commands.Verify(
            item => item.RunAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TimeSpan>(), It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    [Fact]
    public void Load_DuplicateIdsAreCaseInsensitiveWithinTheirCollections()
    {
        var json = """
            {
              "schemaVersion": 1,
              "gates": [ { "id": "Build", "run": "dotnet build" }, { "id": "build", "run": "dotnet test" } ]
            }
            """;

        var exception = Assert.Throws<VerificationSpecException>(() => Load(json));

        Assert.Contains("used twice", exception.Message, StringComparison.Ordinal);
    }

    private VerificationSpec Load(string json) => VerificationSpecLoader.Load(WriteSpec(json));

    private string WriteSpec(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_fixture.SpecPath)!);
        File.WriteAllText(_fixture.SpecPath, json);
        return _fixture.SpecPath;
    }
}
