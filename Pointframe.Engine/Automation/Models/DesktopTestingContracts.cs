namespace Pointframe.Engine.Automation.Models;

public enum DesktopSessionState
{
    Opening,
    Active,
    Paused,
    Closing,
    Closed,
}

public enum DesktopTargetState
{
    Running,
    Exited,
    Unavailable,
}

public enum DesktopSurfaceKind
{
    NotificationArea,
    NotificationOverflow,
}

public enum DesktopTestingAction
{
    ListApps,
    StartTestSession,
    RestartApp,
    ObserveApp,
    FocusWindow,
    Click,
    PressKeys,
    CheckUi,
    GetActionResult,
    GetTestReport,
    EndTestSession,
    Drag,
    EnterText,
    Invoke,
    Scroll,
}

public enum DesktopUiAutomationStatus
{
    Available,
    Unavailable,
    Partial,
    Failed,
}

public enum DesktopObservationStatus
{
    NotRequested,
    Available,
    Unavailable,
}

public enum DesktopOperationStatus
{
    Queued,
    Running,
    Completed,
}

public enum DesktopDispatchStatus
{
    NotStarted,
    Complete,
    Partial,
    Unknown,
}

public enum DesktopVerificationStatus
{
    NotRequested,
    Passed,
    Failed,
    Inconclusive,
}

public sealed record DesktopProcessIdentity
{
    public DesktopProcessIdentity(
        string processRef,
        int processId,
        DateTimeOffset startedUtc,
        string executablePath,
        string executableSha256)
    {
        if (string.IsNullOrWhiteSpace(processRef))
        {
            throw new ArgumentException("A process reference is required.", nameof(processRef));
        }

        if (processId <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(processId), processId, "The process ID must be positive.");
        }

        if (string.IsNullOrWhiteSpace(executablePath))
        {
            throw new ArgumentException("An executable path is required.", nameof(executablePath));
        }

        if (string.IsNullOrWhiteSpace(executableSha256))
        {
            throw new ArgumentException("An executable hash is required.", nameof(executableSha256));
        }

        ProcessRef = processRef;
        ProcessId = processId;
        StartedUtc = startedUtc;
        ExecutablePath = executablePath;
        ExecutableSha256 = executableSha256;
    }

    public string ProcessRef { get; }

    public int ProcessId { get; }

    public DateTimeOffset StartedUtc { get; }

    public string ExecutablePath { get; }

    public string ExecutableSha256 { get; }
}

public sealed record DesktopWindowIdentity
{
    public DesktopWindowIdentity(string windowRef, string processRef, nint nativeHandle, string? title = null)
    {
        if (string.IsNullOrWhiteSpace(windowRef))
        {
            throw new ArgumentException("A window reference is required.", nameof(windowRef));
        }

        if (string.IsNullOrWhiteSpace(processRef))
        {
            throw new ArgumentException("A process reference is required.", nameof(processRef));
        }

        WindowRef = windowRef;
        ProcessRef = processRef;
        NativeHandle = nativeHandle;
        Title = title;
    }

    public string WindowRef { get; }

    public string ProcessRef { get; }

    public nint NativeHandle { get; }

    public string? Title { get; }
}

public sealed record DesktopSurfaceIdentity
{
    public DesktopSurfaceIdentity(string surfaceRef, DesktopSurfaceKind kind, string ownerProcessRef)
    {
        if (string.IsNullOrWhiteSpace(surfaceRef))
        {
            throw new ArgumentException("A surface reference is required.", nameof(surfaceRef));
        }

        if (string.IsNullOrWhiteSpace(ownerProcessRef))
        {
            throw new ArgumentException("An owner process reference is required.", nameof(ownerProcessRef));
        }

        SurfaceRef = surfaceRef;
        Kind = kind;
        OwnerProcessRef = ownerProcessRef;
    }

    public string SurfaceRef { get; }

    public DesktopSurfaceKind Kind { get; }

    public string OwnerProcessRef { get; }
}

public sealed record DesktopImageReference
{
    public DesktopImageReference(
        string imageRef,
        int width,
        int height,
        PixelBounds desktopBoundsPixels,
        DateTimeOffset capturedUtc)
    {
        if (string.IsNullOrWhiteSpace(imageRef))
        {
            throw new ArgumentException("An image reference is required.", nameof(imageRef));
        }

        if (width <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(width), width, "Image width must be positive.");
        }

        if (height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(height), height, "Image height must be positive.");
        }

        if (desktopBoundsPixels.Width != width || desktopBoundsPixels.Height != height)
        {
            throw new ArgumentException("Image dimensions must match the desktop bounds.", nameof(desktopBoundsPixels));
        }

        ImageRef = imageRef;
        Width = width;
        Height = height;
        DesktopBoundsPixels = desktopBoundsPixels;
        CapturedUtc = capturedUtc;
    }

    public string ImageRef { get; }

    public int Width { get; }

    public int Height { get; }

    public PixelBounds DesktopBoundsPixels { get; }

    public DateTimeOffset CapturedUtc { get; }

    public DesktopTarget.ImagePoint CreatePoint(int x, int y)
    {
        return new DesktopTarget.ImagePoint(ImageRef, x, y, Width, Height);
    }
}

public abstract record DesktopTarget
{
    private DesktopTarget()
    {
    }

    public sealed record Element : DesktopTarget
    {
        public Element(string elementRef)
        {
            if (string.IsNullOrWhiteSpace(elementRef))
            {
                throw new ArgumentException("An element reference is required.", nameof(elementRef));
            }

            ElementRef = elementRef;
        }

        public string ElementRef { get; }
    }

    public sealed record ImagePoint : DesktopTarget
    {
        public ImagePoint(string imageRef, int x, int y, int imageWidth, int imageHeight)
        {
            if (string.IsNullOrWhiteSpace(imageRef))
            {
                throw new ArgumentException("An image reference is required.", nameof(imageRef));
            }

            if (imageWidth <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(imageWidth), imageWidth, "Image width must be positive.");
            }

            if (imageHeight <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(imageHeight), imageHeight, "Image height must be positive.");
            }

            if (x < 0 || x >= imageWidth)
            {
                throw new ArgumentOutOfRangeException(nameof(x), x, "The image point must be within the image width.");
            }

            if (y < 0 || y >= imageHeight)
            {
                throw new ArgumentOutOfRangeException(nameof(y), y, "The image point must be within the image height.");
            }

            ImageRef = imageRef;
            X = x;
            Y = y;
            ImageWidth = imageWidth;
            ImageHeight = imageHeight;
        }

        public string ImageRef { get; }

        public int X { get; }

        public int Y { get; }

        public int ImageWidth { get; }

        public int ImageHeight { get; }
    }
}

public sealed record DesktopObservation
{
    public DesktopObservation(
        int schemaVersion,
        string observationRef,
        DesktopProcessIdentity process,
        DesktopTargetState targetState,
        DesktopObservationStatus observationStatus,
        IReadOnlyList<DesktopImageReference> images,
        DesktopUiAutomationStatus uiaStatus,
        DateTimeOffset pixelCapturedUtc,
        DateTimeOffset? uiaCapturedUtc = null,
        bool isTruncated = false)
    {
        DesktopTestingLimits.ValidateSchemaVersion(schemaVersion);

        if (string.IsNullOrWhiteSpace(observationRef))
        {
            throw new ArgumentException("An observation reference is required.", nameof(observationRef));
        }

        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(images);

        SchemaVersion = schemaVersion;
        ObservationRef = observationRef;
        Process = process;
        TargetState = targetState;
        ObservationStatus = observationStatus;
        Images = images;
        UiaStatus = uiaStatus;
        PixelCapturedUtc = pixelCapturedUtc;
        UiaCapturedUtc = uiaCapturedUtc;
        IsTruncated = isTruncated;
    }

    public int SchemaVersion { get; }

    public string ObservationRef { get; }

    public DesktopProcessIdentity Process { get; }

    public DesktopTargetState TargetState { get; }

    public DesktopObservationStatus ObservationStatus { get; }

    public IReadOnlyList<DesktopImageReference> Images { get; }

    public DesktopUiAutomationStatus UiaStatus { get; }

    public DateTimeOffset PixelCapturedUtc { get; }

    public DateTimeOffset? UiaCapturedUtc { get; }

    public bool IsTruncated { get; }
}

public sealed record DesktopOperationError
{
    public DesktopOperationError(string code, string message)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("An error code is required.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("An error message is required.", nameof(message));
        }

        Code = code;
        Message = message;
    }

    public string Code { get; }

    public string Message { get; }
}

public sealed record DesktopActionResult
{
    public DesktopActionResult(
        int schemaVersion,
        string actionId,
        DesktopOperationStatus operationStatus,
        DesktopDispatchStatus dispatch,
        DesktopVerificationStatus verification,
        DesktopObservationStatus observationStatus,
        DesktopOperationError? error = null)
    {
        DesktopTestingLimits.ValidateSchemaVersion(schemaVersion);

        if (!Guid.TryParse(actionId, out _))
        {
            throw new ArgumentException("ActionId must be a UUID.", nameof(actionId));
        }

        if (verification == DesktopVerificationStatus.Failed && error is null)
        {
            throw new ArgumentException("A failed verification must include an error.", nameof(verification));
        }

        SchemaVersion = schemaVersion;
        ActionId = actionId;
        OperationStatus = operationStatus;
        Dispatch = dispatch;
        Verification = verification;
        ObservationStatus = observationStatus;
        Error = error;
    }

    public int SchemaVersion { get; }

    public string ActionId { get; }

    public DesktopOperationStatus OperationStatus { get; }

    public DesktopDispatchStatus Dispatch { get; }

    public DesktopVerificationStatus Verification { get; }

    public DesktopObservationStatus ObservationStatus { get; }

    public DesktopOperationError? Error { get; }
}
