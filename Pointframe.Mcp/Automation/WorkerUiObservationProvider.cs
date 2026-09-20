using System.Text.Json;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.Mcp.Automation;

/// <summary>
/// Supplies the UI Automation element tree for an observation by asking the supervised worker, which
/// is the only process that owns a UI Automation backend.
/// </summary>
/// <remarks>
/// The parent deliberately does not inspect the desktop itself. Constructing FlaUI's UIA3Automation
/// initializes COM inside whichever process creates it, and doing that in the process that serves the
/// MCP protocol made unrelated tools hang intermittently. Before this existed, the parent registered a
/// provider with no backend at all, so every observation came back ProviderUnavailable with zero
/// elements - which meant no element_ref or window_ref was ever minted, and desktop_invoke,
/// desktop_focus_window and desktop_check_ui had nothing to address.
/// </remarks>
public sealed class WorkerUiObservationProvider(WorkerDesktopAutomationService worker) : IDesktopUiObservationProvider
{
    private static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    public DesktopUiSnapshot Inspect(DesktopObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        try
        {
            var payload = worker.InspectUi(request);
            Pointframe.Engine.Automation.DesktopTrace.Write($"inspect payload={(payload is null ? "<null>" : payload.Length + " chars")}");
            if (payload is null)
            {
                return Unavailable("ProviderUnavailable");
            }

            var snapshot = JsonSerializer.Deserialize<DesktopUiSnapshot>(payload, SerializerOptions);
            return snapshot ?? Unavailable("InvalidPayload");
        }
        catch (Exception exception) when (exception is JsonException
            or IOException
            or EndOfStreamException
            or InvalidOperationException
            or OperationCanceledException)
        {
            Pointframe.Engine.Automation.DesktopTrace.Write($"inspect failed: {exception.GetType().Name}: {exception.Message}");
            // An observation that loses its element tree is still worth returning for its pixels, so
            // report the degradation rather than failing the whole call.
            return Unavailable("ProviderUnavailable");
        }
    }

    private static DesktopUiSnapshot Unavailable(string code) =>
        new(DesktopUiAutomationStatus.Unavailable, [], DateTimeOffset.UtcNow, ErrorCode: code);
}
