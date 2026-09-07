namespace Pointframe.Engine.Automation.Models;

public static class DesktopTestingLimits
{
    public const int SchemaVersion = 1;
    public const int MaxObservationCount = 16;
    public const int ObservationTtlSeconds = 30;
    public const int MaxImageLongestEdge = 1600;
    public const int MaxUiAutomationElements = 200;
    public const int MaxUiAutomationDepth = 8;
    public const int MaxQueueCapacity = 32;
    public const int MaxActionCapacity = 1024;
    public const int DefaultUiCheckTimeoutSeconds = 10;
    public const int MaxUiCheckTimeoutSeconds = 30;
    public const int UiCheckPollingMilliseconds = 100;
    public const int MaxClickCount = 2;
    public const int MinDragPoints = 2;
    public const int MaxDragPoints = 128;
    public const int MinDragDurationMilliseconds = 50;
    public const int MaxDragDurationMilliseconds = 5000;
    public const int MaxSimultaneousKeys = 4;
    public const int MinScrollDetents = -10;
    public const int MaxScrollDetents = 10;
    public const int MaxTextLength = 4096;

    public static void ValidateSchemaVersion(int schemaVersion)
    {
        if (schemaVersion != SchemaVersion)
        {
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), schemaVersion, $"Unsupported desktop testing schema version. Expected {SchemaVersion}.");
        }
    }
}
