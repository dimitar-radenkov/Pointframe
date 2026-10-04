using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pointframe.Cli;

internal sealed record SpecTrust(
    int SchemaVersion,
    string ProjectRoot,
    string CommandsSha256,
    IReadOnlyList<string> Commands,
    DateTimeOffset ApprovedUtc,
    string ApprovedBy = "person",
    string? Reason = null);

internal sealed record TaskCriterionBefore(string Id, string Text, string Before);

internal sealed record TaskFailBefore(string Status, IReadOnlyList<TaskCriterionBefore> Criteria, IReadOnlyList<string> Problems, string? BundleDirectory);

internal sealed record SpecDigest(
    string Sha256,
    IReadOnlyDictionary<string, string> Gates,
    IReadOnlyDictionary<string, string> Scenarios);

internal sealed record TaskSnapshot(
    int SchemaVersion,
    string TaskId,
    string ProjectRoot,
    string TaskText,
    string TaskSha256,
    DateTimeOffset CreatedUtc,
    string? Head,
    string? TreeHashAtStart,
    SpecDigest SpecAtStart,
    JsonElement Scenario,
    IReadOnlyList<string> RequiredAutomationIds,
    string? ExaminerNotes,
    string Examiner,
    TaskFailBefore FailBefore);

// Gate trust and task snapshots live under %LOCALAPPDATA%\Pointframe\verify, outside the repository, keyed
// by the project folder: a worker editing the repository cannot change what it is graded against, and
// approving either one needs an interactive terminal (see IConfirmation).
internal sealed class VerificationStore(string baseDirectory)
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    internal static VerificationStore Default { get; } = new(Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Pointframe", "verify"));

    internal string BaseDirectory => baseDirectory;

    internal static string ProjectKey(string projectRoot) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(projectRoot).TrimEnd('\\', '/').ToUpperInvariant())))[..16];

    internal string TaskPath(string projectRoot, string taskId) =>
        Path.Combine(baseDirectory, "tasks", ProjectKey(projectRoot), $"{taskId}.json");

    internal SpecTrust? ReadTrust(string projectRoot) => Read<SpecTrust>(TrustPath(projectRoot));

    internal void WriteTrust(SpecTrust trust) => Write(TrustPath(trust.ProjectRoot), trust);

    internal bool RevokeTrust(string projectRoot)
    {
        var path = TrustPath(projectRoot);
        if (!File.Exists(path))
        {
            return false;
        }

        File.Delete(path);
        return true;
    }

    internal (TaskSnapshot Snapshot, string Sha256)? ReadTask(string projectRoot, string taskId)
    {
        var path = TaskPath(projectRoot, taskId);
        if (!File.Exists(path))
        {
            return null;
        }

        var bytes = File.ReadAllBytes(path);
        var snapshot = JsonSerializer.Deserialize<TaskSnapshot>(bytes, Json)
            ?? throw new InvalidDataException($"The task snapshot {path} is empty.");
        return (snapshot, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    internal string WriteTask(TaskSnapshot snapshot)
    {
        var path = TaskPath(snapshot.ProjectRoot, snapshot.TaskId);
        Write(path, snapshot);
        return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    }

    // The task a hook verifies: set by `verify task start`, so the Stop hook needs no task id from the agent.
    internal string? ReadActiveTask(string projectRoot)
    {
        var path = ActivePath(projectRoot);
        return File.Exists(path) ? File.ReadAllText(path).Trim() : null;
    }

    internal void WriteActiveTask(string projectRoot, string taskId)
    {
        var path = ActivePath(projectRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, taskId);
    }

    private string ActivePath(string projectRoot) => Path.Combine(baseDirectory, "active", $"{ProjectKey(projectRoot)}.txt");

    private string TrustPath(string projectRoot) => Path.Combine(baseDirectory, "trust", $"{ProjectKey(projectRoot)}.json");

    private static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllBytes(path), Json) : null;

    private static void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(value, Json));
    }
}

internal static class SpecDigests
{
    // One hash per gate and per scenario, over their parsed content, so the verdict can say which gate or
    // scenario changed since a task started instead of only "the spec changed".
    internal static SpecDigest Of(VerificationSpec spec, string specSha256) => new(
        specSha256,
        spec.Gates.ToDictionary(gate => gate.Id, gate => Hash(JsonSerializer.Serialize(gate)), StringComparer.Ordinal),
        spec.Scenarios.ToDictionary(scenario => scenario.Id, scenario => Hash(Serialize(scenario)), StringComparer.Ordinal));

    // What `verify trust` approves: everything the spec makes the CLI start with the user's permissions,
    // the gate commands and the app (executable, arguments, working folder, isolation).
    internal static string CommandsSha256(VerificationSpec spec) => Hash(JsonSerializer.Serialize(new { spec.Gates, spec.App }));

    internal static IReadOnlyList<string> Commands(VerificationSpec spec)
    {
        var commands = spec.Gates.Select(gate => $"gate {gate.Id}: {gate.Run}   (in {gate.WorkingDirectory})").ToList();
        if (spec.App is { } app)
        {
            var arguments = app.Arguments.Count == 0 ? string.Empty : $" {string.Join(" ", app.Arguments)}";
            commands.Add($"app {app.Id}: {app.ExecutablePath}{arguments}   (in {app.WorkingDirectory})");
        }

        return commands;
    }

    internal static IReadOnlyList<string> Changes(SpecDigest before, SpecDigest now)
    {
        var changes = new List<string>();
        Compare("gate", before.Gates, now.Gates, changes);
        Compare("scenario", before.Scenarios, now.Scenarios, changes);
        if (changes.Count == 0 && before.Sha256 != now.Sha256)
        {
            changes.Add("the spec file changed outside its gates and scenarios (app or formatting)");
        }

        return changes;
    }

    internal static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));

    private static string Serialize(VerificationScenario scenario) => JsonSerializer.Serialize(new
    {
        scenario.Id,
        scenario.Criteria,
        Steps = scenario.Steps.Select(step => JsonSerializer.Serialize(step, step.GetType())).ToArray(),
    });

    private static void Compare(string kind, IReadOnlyDictionary<string, string> before, IReadOnlyDictionary<string, string> now, List<string> changes)
    {
        foreach (var (id, hash) in now)
        {
            if (!before.TryGetValue(id, out var old))
            {
                changes.Add($"{kind} '{id}' added");
            }
            else if (old != hash)
            {
                changes.Add($"{kind} '{id}' changed");
            }
        }

        changes.AddRange(before.Keys.Where(id => !now.ContainsKey(id)).Select(id => $"{kind} '{id}' removed"));
    }
}

internal interface IConfirmation
{
    bool CanAsk { get; }

    bool Confirm(string prompt);
}

// Approval needs a person at a terminal. An agent's shell tool runs with redirected input, so it cannot
// answer, and there is deliberately no --yes flag it could pass instead.
internal sealed class ConsoleConfirmation(TextWriter prompts) : IConfirmation
{
    public bool CanAsk => !Console.IsInputRedirected;

    public bool Confirm(string prompt)
    {
        prompts.Write($"{prompt} Type 'yes' to approve: ");
        return string.Equals(Console.ReadLine()?.Trim(), "yes", StringComparison.Ordinal);
    }
}
