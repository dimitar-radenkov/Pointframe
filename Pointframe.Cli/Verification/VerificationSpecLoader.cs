using System.Text.Json;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.Cli;

internal sealed class VerificationSpecException(string message) : Exception(message);

// Reads .pointframe/verify.json strictly: unknown properties, unknown step kinds, and scenarios that could
// pass without proving anything (a criterion no check covers, no negative control) are rejected before
// anything is launched, so a typo can never turn into a silently passing run.
internal static class VerificationSpecLoader
{
    internal const int SupportedSchemaVersion = 1;
    internal const string DefaultSpecRelativePath = ".pointframe/verify.json";
    private const string SpecDirectoryName = ".pointframe";

    private static readonly string[] SupportedCheckKinds =
        ["exists", "absent", "enabled", "toggleEquals", "selectionEquals", "textEquals"];

    internal static VerificationSpec Load(string specPath)
    {
        var fullPath = Path.GetFullPath(specPath);
        if (!File.Exists(fullPath))
        {
            throw new VerificationSpecException($"No verification spec at {fullPath}.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(File.ReadAllText(fullPath), new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip });
        }
        catch (JsonException exception)
        {
            throw new VerificationSpecException($"The verification spec is not valid JSON: {exception.Message}");
        }

        using (document)
        {
            return Parse(document.RootElement, fullPath, RootDirectoryFor(fullPath));
        }
    }

    // Paths in the spec are relative to the project root: the folder that holds .pointframe/, or the
    // spec's own folder when it lives elsewhere.
    internal static string RootDirectoryFor(string fullSpecPath)
    {
        var directory = Path.GetDirectoryName(fullSpecPath)!;
        return string.Equals(Path.GetFileName(directory), SpecDirectoryName, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(directory)!
            : directory;
    }

    internal static VerificationSpec Parse(JsonElement root, string specPath, string rootDirectory)
    {
        RequireObject(root, "spec");
        RequireOnly(root, "spec", "schemaVersion", "app", "gates", "scenarios");
        var schemaVersion = RequireInt(root, "schemaVersion", "spec");
        if (schemaVersion != SupportedSchemaVersion)
        {
            throw new VerificationSpecException($"spec.schemaVersion {schemaVersion} is not supported; expected {SupportedSchemaVersion}.");
        }

        var app = root.TryGetProperty("app", out var appElement) ? ParseApp(appElement, rootDirectory) : null;
        var gates = ParseGates(root, rootDirectory);
        var scenarios = new List<VerificationScenario>();
        if (root.TryGetProperty("scenarios", out var scenariosElement))
        {
            if (scenariosElement.ValueKind != JsonValueKind.Array)
            {
                throw new VerificationSpecException("spec.scenarios must be an array.");
            }

            var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var index = 0;
            foreach (var scenarioElement in scenariosElement.EnumerateArray())
            {
                var scenario = ParseScenario(scenarioElement, $"scenarios[{index}]");
                if (!ids.Add(scenario.Id))
                {
                    throw new VerificationSpecException($"scenarios[{index}].id '{scenario.Id}' is used twice.");
                }

                scenarios.Add(scenario);
                index++;
            }
        }

        if (gates.Count == 0 && scenarios.Count == 0)
        {
            throw new VerificationSpecException("spec needs at least one gate or one scenario.");
        }

        if (scenarios.Count > 0 && app is null)
        {
            throw new VerificationSpecException("spec.app is required when the spec has scenarios.");
        }

        return new VerificationSpec(schemaVersion, specPath, rootDirectory, app, gates, scenarios);
    }

    // One scenario on its own, as an examiner proposes it and a task snapshot stores it. It goes through
    // the same rules as a scenario in the spec, so a snapshot can never hold what the spec would reject.
    internal static VerificationScenario ParseScenario(string json, string path)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return ParseScenario(document.RootElement, path);
        }
        catch (JsonException exception)
        {
            throw new VerificationSpecException($"{path} is not valid JSON: {exception.Message}");
        }
    }

    private static VerificationApp ParseApp(JsonElement element, string rootDirectory)
    {
        RequireObject(element, "app");
        RequireOnly(element, "app", "id", "executable", "arguments", "workingDirectory", "isolation");
        var id = RequireIdentifier(element, "id", "app");
        var executable = ResolvePath(RequireString(element, "executable", "app"), rootDirectory);
        var arguments = OptionalStringArray(element, "arguments", "app");
        var workingDirectory = element.TryGetProperty("workingDirectory", out _)
            ? ResolvePath(RequireString(element, "workingDirectory", "app"), rootDirectory)
            : Path.GetDirectoryName(executable)!;
        var isolation = element.TryGetProperty("isolation", out var isolationElement) ? ParseIsolation(isolationElement) : null;
        return new VerificationApp(id, executable, arguments, workingDirectory, isolation);
    }

    private static VerificationIsolation ParseIsolation(JsonElement element)
    {
        var path = "app.isolation";
        RequireObject(element, path);
        RequireOnly(element, path, "environmentVariable", "argument");
        var variable = element.TryGetProperty("environmentVariable", out _) ? RequireString(element, "environmentVariable", path) : null;
        var argument = element.TryGetProperty("argument", out _) ? RequireString(element, "argument", path) : null;
        if ((variable is null) == (argument is null))
        {
            throw new VerificationSpecException($"{path} needs exactly one of environmentVariable or argument.");
        }

        if (variable is not null
            && (!(char.IsAsciiLetter(variable[0]) || variable[0] == '_')
                || !variable.All(character => char.IsAsciiLetterOrDigit(character) || character == '_')
                || ProtectedVariables.Contains(variable)))
        {
            throw new VerificationSpecException(
                $"{path}.environmentVariable '{variable}' must be a plain name (letters, digits, '_') and not a system variable.");
        }

        if (argument is not null && (!argument.StartsWith('-') || argument.Any(char.IsWhiteSpace)))
        {
            throw new VerificationSpecException($"{path}.argument '{argument}' must be one option such as --data-dir; the CLI passes the folder after it.");
        }

        return new VerificationIsolation(variable, argument);
    }

    private static readonly HashSet<string> ProtectedVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "PATH", "PATHEXT", "COMSPEC", "SYSTEMROOT", "WINDIR", "TEMP", "TMP", "USERPROFILE", "APPDATA",
        "LOCALAPPDATA", "PROGRAMDATA", "HOMEDRIVE", "HOMEPATH", "USERNAME",
    };

    private static IReadOnlyList<VerificationGate> ParseGates(JsonElement root, string rootDirectory)
    {
        if (!root.TryGetProperty("gates", out var gatesElement))
        {
            return [];
        }

        if (gatesElement.ValueKind != JsonValueKind.Array)
        {
            throw new VerificationSpecException("spec.gates must be an array.");
        }

        var gates = new List<VerificationGate>();
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var index = 0;
        foreach (var element in gatesElement.EnumerateArray())
        {
            var path = $"gates[{index}]";
            RequireObject(element, path);
            RequireOnly(element, path, "id", "run", "workingDirectory", "timeoutMinutes");
            var id = RequireIdentifier(element, "id", path);
            if (!ids.Add(id))
            {
                throw new VerificationSpecException($"{path}.id '{id}' is used twice.");
            }

            var workingDirectory = element.TryGetProperty("workingDirectory", out _)
                ? ResolvePath(RequireString(element, "workingDirectory", path), rootDirectory)
                : rootDirectory;
            var timeoutMinutes = element.TryGetProperty("timeoutMinutes", out _) ? RequireInt(element, "timeoutMinutes", path) : 30;
            if (timeoutMinutes < 1 || timeoutMinutes > 120)
            {
                throw new VerificationSpecException($"{path}.timeoutMinutes must be from 1 through 120.");
            }

            gates.Add(new VerificationGate(id, RequireString(element, "run", path), workingDirectory, timeoutMinutes));
            index++;
        }

        return gates;
    }

    private static VerificationScenario ParseScenario(JsonElement element, string path)
    {
        RequireObject(element, path);
        RequireOnly(element, path, "id", "criteria", "steps");
        var id = RequireIdentifier(element, "id", path);
        var criteria = OptionalStringArray(element, "criteria", path);
        try
        {
            _ = DesktopTestReportService.NormalizeCriteria(criteria);
        }
        catch (ArgumentException exception)
        {
            throw new VerificationSpecException($"{path}.criteria: {exception.Message}");
        }

        var stepsElement = RequireProperty(element, "steps", path);
        if (stepsElement.ValueKind != JsonValueKind.Array || stepsElement.GetArrayLength() == 0)
        {
            throw new VerificationSpecException($"{path}.steps must be a non-empty array.");
        }

        var steps = new List<VerificationStep>();
        var index = 0;
        foreach (var stepElement in stepsElement.EnumerateArray())
        {
            steps.Add(ParseStep(stepElement, $"{path}.steps[{index}]"));
            index++;
        }

        ValidateCoverage(path, criteria, steps);
        return new VerificationScenario(id, criteria, steps);
    }

    // The same rules a session's report applies at the end, checked up front: every criterion needs a
    // check naming it, and a scenario with criteria needs a negative control.
    private static void ValidateCoverage(string path, IReadOnlyList<string> criteria, IReadOnlyList<VerificationStep> steps)
    {
        var criterionIds = Enumerable.Range(1, criteria.Count).Select(number => $"C{number}").ToHashSet(StringComparer.Ordinal);
        var checks = steps.OfType<VerificationStep.Check>().ToArray();
        foreach (var check in checks.Where(check => check.Criterion is not null))
        {
            if (!criterionIds.Contains(check.Criterion!))
            {
                throw new VerificationSpecException(
                    $"{path}: a check names criterion '{check.Criterion}', but the scenario declares {DescribeIds(criterionIds)}.");
            }
        }

        if (criteria.Count == 0)
        {
            return;
        }

        var uncovered = criterionIds.Where(criterionId => !checks.Any(check => check.Criterion == criterionId)).ToArray();
        if (uncovered.Length > 0)
        {
            throw new VerificationSpecException($"{path}: no check covers {string.Join(", ", uncovered)}.");
        }

        if (!checks.Any(check => check.ExpectFailure))
        {
            throw new VerificationSpecException(
                $"{path}: a scenario with criteria needs at least one negative control (a check with \"expectFailure\": true).");
        }
    }

    private static string DescribeIds(IReadOnlyCollection<string> ids) =>
        ids.Count == 0 ? "no criteria" : string.Join(", ", ids.Order(StringComparer.Ordinal));

    private static VerificationStep ParseStep(JsonElement element, string path)
    {
        RequireObject(element, path);
        var properties = element.EnumerateObject().ToArray();
        if (properties.Length != 1)
        {
            throw new VerificationSpecException(
                $"{path} must have exactly one property naming the step: enterText, invoke, pressKeys, restart, or check.");
        }

        var kind = properties[0].Name;
        var body = properties[0].Value;
        var bodyPath = $"{path}.{kind}";
        RequireObject(body, bodyPath);
        switch (kind)
        {
            case "enterText":
                RequireOnly(body, bodyPath, "automationId", "role", "name", "text");
                return new VerificationStep.EnterText(ParseLocator(body, bodyPath), RequireString(body, "text", bodyPath, allowEmpty: true));
            case "invoke":
                RequireOnly(body, bodyPath, "automationId", "role", "name");
                return new VerificationStep.Invoke(ParseLocator(body, bodyPath));
            case "pressKeys":
                RequireOnly(body, bodyPath, "keys");
                return new VerificationStep.PressKeys(ParseKeys(body, bodyPath));
            case "restart":
                RequireOnly(body, bodyPath);
                return new VerificationStep.Restart();
            case "check":
                return ParseCheck(body, bodyPath);
            default:
                throw new VerificationSpecException(
                    $"{path}: unknown step '{kind}'. Expected enterText, invoke, pressKeys, restart, or check.");
        }
    }

    private static VerificationStep.Check ParseCheck(JsonElement body, string path)
    {
        RequireOnly(body, path, "kind", "automationId", "role", "name", "expected", "criterion", "expectFailure", "timeoutSeconds");
        var kind = RequireString(body, "kind", path);
        var normalizedKind = SupportedCheckKinds.FirstOrDefault(item => string.Equals(item, kind, StringComparison.OrdinalIgnoreCase))
            ?? throw new VerificationSpecException($"{path}.kind '{kind}' is not supported. Expected {string.Join(", ", SupportedCheckKinds)}.");
        var expected = body.TryGetProperty("expected", out _) ? RequireString(body, "expected", path, allowEmpty: true) : null;
        if (expected is null && normalizedKind is "toggleEquals" or "selectionEquals" or "textEquals")
        {
            throw new VerificationSpecException($"{path}: a {normalizedKind} check needs \"expected\".");
        }

        var criterion = body.TryGetProperty("criterion", out _) ? RequireString(body, "criterion", path) : null;
        var expectFailure = body.TryGetProperty("expectFailure", out var expectFailureElement)
            && expectFailureElement.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                _ => throw new VerificationSpecException($"{path}.expectFailure must be true or false."),
            };
        if (expectFailure && criterion is not null)
        {
            throw new VerificationSpecException($"{path}: a negative control (expectFailure) cannot also cover a criterion.");
        }

        var timeoutSeconds = body.TryGetProperty("timeoutSeconds", out _)
            ? RequireInt(body, "timeoutSeconds", path)
            : DesktopTestingLimits.DefaultUiCheckTimeoutSeconds;
        if (timeoutSeconds < 1 || timeoutSeconds > DesktopTestingLimits.MaxUiCheckTimeoutSeconds)
        {
            throw new VerificationSpecException(
                $"{path}.timeoutSeconds must be from 1 through {DesktopTestingLimits.MaxUiCheckTimeoutSeconds}.");
        }

        return new VerificationStep.Check(normalizedKind, ParseLocator(body, path), expected, criterion, expectFailure, timeoutSeconds);
    }

    private static ElementLocator ParseLocator(JsonElement body, string path)
    {
        var automationId = body.TryGetProperty("automationId", out _) ? RequireString(body, "automationId", path) : null;
        var role = body.TryGetProperty("role", out _) ? RequireString(body, "role", path) : null;
        var name = body.TryGetProperty("name", out _) ? RequireString(body, "name", path) : null;
        if (automationId is not null && (role is not null || name is not null))
        {
            throw new VerificationSpecException($"{path}: use either automationId, or role and name, not both.");
        }

        if (automationId is null && (role is null || name is null))
        {
            throw new VerificationSpecException($"{path}: name the element with automationId, or with both role and name.");
        }

        return new ElementLocator(automationId, role, name);
    }

    private static IReadOnlyList<ushort> ParseKeys(JsonElement body, string path)
    {
        var keysElement = RequireProperty(body, "keys", path);
        if (keysElement.ValueKind != JsonValueKind.Array
            || keysElement.GetArrayLength() == 0
            || keysElement.GetArrayLength() > DesktopTestingLimits.MaxSimultaneousKeys)
        {
            throw new VerificationSpecException(
                $"{path}.keys must hold 1 to {DesktopTestingLimits.MaxSimultaneousKeys} virtual-key codes, modifiers first.");
        }

        return keysElement.EnumerateArray()
            .Select(key => key.ValueKind == JsonValueKind.Number && key.TryGetUInt16(out var code) && code > 0
                ? code
                : throw new VerificationSpecException($"{path}.keys must be Windows virtual-key codes (1-65535)."))
            .ToArray();
    }

    private static string ResolvePath(string value, string rootDirectory) =>
        Path.GetFullPath(Path.IsPathFullyQualified(value) ? value : Path.Combine(rootDirectory, value));

    private static void RequireObject(JsonElement element, string path)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new VerificationSpecException($"{path} must be a JSON object.");
        }
    }

    private static void RequireOnly(JsonElement element, string path, params string[] allowed)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!allowed.Contains(property.Name, StringComparer.Ordinal))
            {
                throw new VerificationSpecException(allowed.Length == 0
                    ? $"{path} takes no properties; found '{property.Name}'."
                    : $"{path} has an unknown property '{property.Name}'. Allowed: {string.Join(", ", allowed)}.");
            }
        }
    }

    private static JsonElement RequireProperty(JsonElement element, string name, string path) =>
        element.TryGetProperty(name, out var value)
            ? value
            : throw new VerificationSpecException($"{path}.{name} is required.");

    private static string RequireString(JsonElement element, string name, string path, bool allowEmpty = false)
    {
        var value = RequireProperty(element, name, path);
        if (value.ValueKind != JsonValueKind.String || (!allowEmpty && string.IsNullOrWhiteSpace(value.GetString())))
        {
            throw new VerificationSpecException($"{path}.{name} must be a {(allowEmpty ? string.Empty : "non-empty ")}string.");
        }

        return value.GetString()!;
    }

    private static string RequireIdentifier(JsonElement element, string name, string path)
    {
        var value = RequireString(element, name, path);
        if (!value.All(character => char.IsAsciiLetterOrDigit(character) || character is '-' or '_' or '.'))
        {
            throw new VerificationSpecException($"{path}.{name} '{value}' may hold only letters, digits, '-', '_', and '.'.");
        }

        return value;
    }

    private static int RequireInt(JsonElement element, string name, string path)
    {
        var value = RequireProperty(element, name, path);
        return value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : throw new VerificationSpecException($"{path}.{name} must be a whole number.");
    }

    private static IReadOnlyList<string> OptionalStringArray(JsonElement element, string name, string path)
    {
        if (!element.TryGetProperty(name, out var value))
        {
            return [];
        }

        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new VerificationSpecException($"{path}.{name} must be an array of strings.");
        }

        return value.EnumerateArray()
            .Select(item => item.ValueKind == JsonValueKind.String
                ? item.GetString()!
                : throw new VerificationSpecException($"{path}.{name} must be an array of strings."))
            .ToArray();
    }
}
