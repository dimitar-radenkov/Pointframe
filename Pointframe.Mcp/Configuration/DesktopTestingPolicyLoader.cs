using System.Text.Json;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Configuration;

public sealed class DesktopTestingPolicyLoader
{
    private static readonly HashSet<string> RootProperties = new(StringComparer.Ordinal)
    {
        "schemaVersion",
        "artifactRoot",
        "evidencePolicy",
        "profiles",
    };

    private static readonly HashSet<string> ProfileProperties = new(StringComparer.Ordinal)
    {
        "id",
        "executablePath",
        "arguments",
        "workingDirectory",
        "allowAttach",
        "allowedActions",
        "allowedGlobalHotkeys",
        "allowedShellSurfaces",
        "allowMonitorObservation",
    };

    private static readonly HashSet<string> ForbiddenProperties = new(StringComparer.OrdinalIgnoreCase)
    {
        "environment",
        "environmentVariables",
        "dataRoot",
        "dataDirectory",
        "settingsPath",
        "databasePath",
        "settings",
        "seedSettings",
        "launchMode",
        "automationMode",
        "automationArguments",
        "workingDirectoryOverride",
    };

    public DesktopTestingPolicy LoadAndValidate(string policyPath)
    {
        var fullPolicyPath = RequireAbsoluteExistingFile(policyPath, nameof(policyPath));
        using var document = JsonDocument.Parse(File.ReadAllText(fullPolicyPath));
        var root = document.RootElement;
        RequireObject(root, "Policy root");
        ValidateProperties(root, RootProperties, "Policy");

        var schemaVersion = GetRequiredInt(root, "schemaVersion", "Policy");
        DesktopTestingLimits.ValidateSchemaVersion(schemaVersion);
        var artifactRoot = RequireAbsolutePath(GetRequiredString(root, "artifactRoot", "Policy"), "artifactRoot");
        RequireExistingDirectory(artifactRoot, "artifactRoot");
        var evidencePolicy = ParseEvidencePolicy(GetRequiredString(root, "evidencePolicy", "Policy"));
        var profilesElement = GetRequiredArray(root, "profiles", "Policy");

        var profiles = new List<DesktopTestingProfile>();
        var profileIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var profileElement in profilesElement.EnumerateArray())
        {
            RequireObject(profileElement, "Profile");
            ValidateProperties(profileElement, ProfileProperties, "Profile");
            var profile = ParseProfile(profileElement);
            if (!profileIds.Add(profile.Id))
            {
                throw new InvalidDataException($"Duplicate profile id '{profile.Id}'.");
            }

            profiles.Add(profile);
        }

        if (profiles.Count == 0)
        {
            throw new InvalidDataException("At least one desktop testing profile is required.");
        }

        return new DesktopTestingPolicy(schemaVersion, artifactRoot, evidencePolicy, profiles);
    }

    private static DesktopTestingProfile ParseProfile(JsonElement element)
    {
        var id = GetRequiredString(element, "id", "Profile");
        var executablePath = RequireAbsoluteExistingFile(GetRequiredString(element, "executablePath", $"Profile '{id}'"), "executablePath");
        if (!string.Equals(Path.GetExtension(executablePath), ".exe", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException($"Profile '{id}' executablePath must name an .exe file.");
        }

        var workingDirectory = RequireAbsolutePath(GetRequiredString(element, "workingDirectory", $"Profile '{id}'"), "workingDirectory");
        RequireExistingDirectory(workingDirectory, "workingDirectory");
        var arguments = ParseArguments(GetRequiredArray(element, "arguments", $"Profile '{id}'"), id);
        var allowAttach = GetRequiredBoolean(element, "allowAttach", $"Profile '{id}'");
        var allowedActions = ParseActions(GetRequiredArray(element, "allowedActions", $"Profile '{id}'"), id);
        var allowedHotkeys = ParseHotkeys(GetRequiredObject(element, "allowedGlobalHotkeys", $"Profile '{id}'"), id);
        var allowedShellSurfaces = ParseShellSurfaces(GetRequiredArray(element, "allowedShellSurfaces", $"Profile '{id}'"), id);
        var allowMonitorObservation = GetRequiredBoolean(element, "allowMonitorObservation", $"Profile '{id}'");

        return new DesktopTestingProfile(
            id,
            executablePath,
            arguments,
            workingDirectory,
            allowAttach,
            allowedActions,
            allowedHotkeys,
            allowedShellSurfaces,
            allowMonitorObservation);
    }

    private static IReadOnlyList<string> ParseArguments(JsonElement element, string profileId)
    {
        var arguments = new List<string>();
        foreach (var argument in element.EnumerateArray())
        {
            if (argument.ValueKind != JsonValueKind.String)
            {
                throw new InvalidDataException($"Profile '{profileId}' arguments must contain only strings.");
            }

            arguments.Add(argument.GetString()!);
        }

        return arguments;
    }

    private static IReadOnlySet<DesktopTestingAction> ParseActions(JsonElement element, string profileId)
    {
        var actions = new HashSet<DesktopTestingAction>();
        foreach (var action in element.EnumerateArray())
        {
            var actionName = RequireString(action, $"Profile '{profileId}' allowedActions");
            if (!Enum.TryParse<DesktopTestingAction>(actionName, ignoreCase: false, out var parsedAction))
            {
                throw new InvalidDataException($"Profile '{profileId}' contains unsupported action '{actionName}'.");
            }

            if (!actions.Add(parsedAction))
            {
                throw new InvalidDataException($"Profile '{profileId}' contains duplicate action '{actionName}'.");
            }
        }

        return actions;
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> ParseHotkeys(JsonElement element, string profileId)
    {
        var hotkeys = new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in element.EnumerateObject())
        {
            if (string.IsNullOrWhiteSpace(property.Name))
            {
                throw new InvalidDataException($"Profile '{profileId}' contains an empty global hotkey id.");
            }

            if (!hotkeys.TryAdd(property.Name, ParseHotkey(property.Value, profileId, property.Name)))
            {
                throw new InvalidDataException($"Profile '{profileId}' contains duplicate global hotkey id '{property.Name}'.");
            }
        }

        return hotkeys;
    }

    private static IReadOnlyList<string> ParseHotkey(JsonElement element, string profileId, string hotkeyId)
    {
        if (element.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"Profile '{profileId}' hotkey '{hotkeyId}' must be an array.");
        }

        var keys = element.EnumerateArray().Select(key => RequireString(key, $"Profile '{profileId}' hotkey '{hotkeyId}'")).ToArray();
        if (keys.Length == 0 || keys.Length > DesktopTestingLimits.MaxSimultaneousKeys)
        {
            throw new InvalidDataException($"Profile '{profileId}' hotkey '{hotkeyId}' must contain 1 to {DesktopTestingLimits.MaxSimultaneousKeys} keys.");
        }

        return keys;
    }

    private static IReadOnlySet<DesktopSurfaceKind> ParseShellSurfaces(JsonElement element, string profileId)
    {
        var surfaces = new HashSet<DesktopSurfaceKind>();
        foreach (var surface in element.EnumerateArray())
        {
            var surfaceName = RequireString(surface, $"Profile '{profileId}' allowedShellSurfaces");
            if (!Enum.TryParse<DesktopSurfaceKind>(surfaceName, ignoreCase: false, out var parsedSurface))
            {
                throw new InvalidDataException($"Profile '{profileId}' contains unsupported shell surface '{surfaceName}'.");
            }

            if (!surfaces.Add(parsedSurface))
            {
                throw new InvalidDataException($"Profile '{profileId}' contains duplicate shell surface '{surfaceName}'.");
            }
        }

        return surfaces;
    }

    private static DesktopEvidencePolicy ParseEvidencePolicy(string value)
    {
        if (!Enum.TryParse<DesktopEvidencePolicy>(value, ignoreCase: false, out var policy))
        {
            throw new InvalidDataException($"Unsupported evidencePolicy '{value}'.");
        }

        return policy;
    }

    private static string RequireAbsoluteExistingFile(string path, string fieldName)
    {
        var fullPath = RequireAbsolutePath(path, fieldName);
        if (!File.Exists(fullPath))
        {
            throw new InvalidDataException($"{fieldName} must point to an existing file.");
        }

        RejectReparsePoint(fullPath, fieldName);
        return fullPath;
    }

    private static string RequireAbsolutePath(string path, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
        {
            throw new InvalidDataException($"{fieldName} must be an absolute path.");
        }

        return Path.GetFullPath(path);
    }

    private static void RequireExistingDirectory(string path, string fieldName)
    {
        if (!Directory.Exists(path))
        {
            throw new InvalidDataException($"{fieldName} must point to an existing directory.");
        }

        RejectReparsePoint(path, fieldName);
    }

    private static void RejectReparsePoint(string path, string fieldName)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException($"{fieldName} must not point to a reparse point.");
        }
    }

    private static void ValidateProperties(JsonElement element, IReadOnlySet<string> allowedProperties, string scope)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in element.EnumerateObject())
        {
            if (!seen.Add(property.Name))
            {
                throw new InvalidDataException($"{scope} contains duplicate property '{property.Name}'.");
            }

            if (ForbiddenProperties.Contains(property.Name))
            {
                throw new InvalidDataException($"{scope} contains forbidden target-environment property '{property.Name}'.");
            }

            if (!allowedProperties.Contains(property.Name))
            {
                throw new InvalidDataException($"{scope} contains unknown property '{property.Name}'.");
            }
        }
    }

    private static void RequireObject(JsonElement element, string scope)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidDataException($"{scope} must be a JSON object.");
        }
    }

    private static string GetRequiredString(JsonElement element, string propertyName, string scope)
    {
        return RequireString(GetRequiredProperty(element, propertyName, scope), $"{scope} property '{propertyName}'");
    }

    private static string RequireString(JsonElement element, string scope)
    {
        if (element.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(element.GetString()))
        {
            throw new InvalidDataException($"{scope} must be a non-empty string.");
        }

        return element.GetString()!;
    }

    private static int GetRequiredInt(JsonElement element, string propertyName, string scope)
    {
        var value = GetRequiredProperty(element, propertyName, scope);
        if (!value.TryGetInt32(out var result))
        {
            throw new InvalidDataException($"{scope} property '{propertyName}' must be an integer.");
        }

        return result;
    }

    private static bool GetRequiredBoolean(JsonElement element, string propertyName, string scope)
    {
        var value = GetRequiredProperty(element, propertyName, scope);
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            throw new InvalidDataException($"{scope} property '{propertyName}' must be a boolean.");
        }

        return value.GetBoolean();
    }

    private static JsonElement GetRequiredObject(JsonElement element, string propertyName, string scope)
    {
        var value = GetRequiredProperty(element, propertyName, scope);
        RequireObject(value, $"{scope} property '{propertyName}'");
        return value;
    }

    private static JsonElement GetRequiredArray(JsonElement element, string propertyName, string scope)
    {
        var value = GetRequiredProperty(element, propertyName, scope);
        if (value.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException($"{scope} property '{propertyName}' must be an array.");
        }

        return value;
    }

    private static JsonElement GetRequiredProperty(JsonElement element, string propertyName, string scope)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            throw new InvalidDataException($"{scope} is missing required property '{propertyName}'.");
        }

        return value;
    }
}
