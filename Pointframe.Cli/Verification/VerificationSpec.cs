namespace Pointframe.Cli;

internal sealed record VerificationSpec(
    int SchemaVersion,
    string SpecPath,
    string RootDirectory,
    VerificationApp? App,
    IReadOnlyList<VerificationGate> Gates,
    IReadOnlyList<VerificationScenario> Scenarios);

internal sealed record VerificationApp(
    string Id,
    string ExecutablePath,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    VerificationIsolation? Isolation = null);

// The app is pointed at a fresh data folder per scenario, through one environment variable or one
// argument the project declares. The CLI creates and deletes the folder; the spec never names a path.
internal sealed record VerificationIsolation(string? EnvironmentVariable, string? Argument);

internal sealed record VerificationGate(
    string Id,
    string Run,
    string WorkingDirectory,
    int TimeoutMinutes);

internal sealed record VerificationScenario(
    string Id,
    IReadOnlyList<string> Criteria,
    IReadOnlyList<VerificationStep> Steps);

internal sealed record ElementLocator(string? AutomationId, string? Role, string? Name)
{
    public override string ToString() => AutomationId is not null
        ? $"automationId '{AutomationId}'"
        : $"{Role} '{Name}'";
}

internal abstract record VerificationStep
{
    public abstract string Kind { get; }

    internal sealed record EnterText(ElementLocator Locator, string Text) : VerificationStep
    {
        public override string Kind => "enterText";
    }

    internal sealed record Invoke(ElementLocator Locator) : VerificationStep
    {
        public override string Kind => "invoke";
    }

    internal sealed record PressKeys(IReadOnlyList<ushort> VirtualKeys) : VerificationStep
    {
        public override string Kind => "pressKeys";
    }

    internal sealed record Restart : VerificationStep
    {
        public override string Kind => "restart";
    }

    internal sealed record Check(
        string ConditionKind,
        ElementLocator Locator,
        string? Expected,
        string? Criterion,
        bool ExpectFailure,
        int TimeoutSeconds) : VerificationStep
    {
        public override string Kind => "check";
    }
}
