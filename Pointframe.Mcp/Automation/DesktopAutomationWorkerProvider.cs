using System.Text.Json;
using Pointframe.Engine.Automation.Models;
using Pointframe.Engine.Automation.Services;

namespace Pointframe.Mcp.Automation;

public sealed class DesktopAutomationWorkerProvider : IDesktopAutomationWorkerProvider
{
    private readonly IWindowsDesktopInputService _input;
    private readonly IWindowsUiAutomationActionProvider _uiAutomation;

    // UI Automation lives here rather than in the parent: constructing FlaUI's UIA3Automation
    // initializes COM, and doing that inside the process serving the MCP protocol destabilizes it.
    // The worker already owned a backend for invoke/set_value; inspection now uses the same one.
    private readonly IDesktopUiObservationProvider? _uiObservation;

    public DesktopAutomationWorkerProvider(
        IWindowsDesktopInputService? input = null,
        IWindowsUiAutomationActionProvider? uiAutomation = null)
    {
        _input = input ?? new WindowsDesktopInputService();
        _uiAutomation = uiAutomation ?? new WindowsUiAutomationProvider(new FlaUiWindowsUiAutomationBackend());
        _uiObservation = _uiAutomation as IDesktopUiObservationProvider;
    }

    public async Task<DesktopAutomationWorkerResponse> HandleAsync(
        DesktopAutomationWorkerRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            return request.Operation switch
            {
                DesktopAutomationWorkerProtocol.Operations.Focus => await HandleInputAsync<DesktopInputTarget>(
                    request,
                    (target, process) => _input.FocusAsync(target, process, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DesktopAutomationWorkerProtocol.Operations.Click => await HandleInputAsync<DesktopClickRequest>(
                    request,
                    (input, process) => _input.ClickAsync(input, process, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DesktopAutomationWorkerProtocol.Operations.PressKeys => await HandleInputAsync<DesktopKeyPressRequest>(
                    request,
                    (input, process) => _input.PressKeysAsync(input, process, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DesktopAutomationWorkerProtocol.Operations.Drag => await HandleInputAsync<DesktopDragRequest>(
                    request,
                    (input, process) => _input.DragAsync(input, process, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DesktopAutomationWorkerProtocol.Operations.EnterText => await HandleInputAsync<DesktopTextRequest>(
                    request,
                    (input, process) => _input.EnterTextAsync(input, process, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DesktopAutomationWorkerProtocol.Operations.Scroll => await HandleInputAsync<DesktopScrollRequest>(
                    request,
                    (input, process) => _input.ScrollAsync(input, process, cancellationToken),
                    cancellationToken).ConfigureAwait(false),
                DesktopAutomationWorkerProtocol.Operations.Release => HandleRelease(request, cancellationToken),
                DesktopAutomationWorkerProtocol.Operations.Inspect => HandleInspect(request, cancellationToken),
                DesktopAutomationWorkerProtocol.Operations.Invoke => HandleUiAction(request, static (provider, elementRef, _) =>
                    provider.TryInvoke(elementRef), cancellationToken),
                DesktopAutomationWorkerProtocol.Operations.SetValue => HandleUiAction(request, static (provider, elementRef, payload) =>
                    provider.TrySetValue(elementRef, payload.GetProperty("value").GetString() ?? string.Empty), cancellationToken),
                _ => Failure(request, "UnsupportedOperation", "The worker operation is not supported."),
            };
        }

        catch (JsonException)
        {
            return Failure(request, "InvalidPayload", "The worker operation payload was invalid.");
        }
        catch (ArgumentException exception)
        {
            return Failure(request, "InvalidPayload", exception.Message);
        }
    }

    private DesktopAutomationWorkerResponse HandleRelease(
        DesktopAutomationWorkerRequest request,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var released = _input.TryReleaseOwnedInput();
        return new DesktopAutomationWorkerResponse(
            DesktopAutomationWorkerProtocol.Version,
            request.RequestId,
            released,
            released ? "Ok" : "InputReleaseFailed");
    }

    private static async Task<DesktopAutomationWorkerResponse> HandleInputAsync<TRequest>(
        DesktopAutomationWorkerRequest request,
        Func<TRequest, DesktopProcessIdentity, Task<DesktopInputPreflightResult>> dispatch,
        CancellationToken cancellationToken)
    {
        var input = Deserialize<DesktopAutomationWorkerInputRequest>(request.Payload);
        var typedRequest = Deserialize<TRequest>(JsonSerializer.Serialize(input.Request));
        var result = await dispatch(typedRequest, input.ExpectedProcess).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new DesktopAutomationWorkerResponse(
            DesktopAutomationWorkerProtocol.Version,
            request.RequestId,
            result.IsValid,
            result.Code,
            JsonSerializer.Serialize(result));
    }

    private DesktopAutomationWorkerResponse HandleInspect(
        DesktopAutomationWorkerRequest request,
        CancellationToken cancellationToken)
    {
        if (_uiObservation is null)
        {
            return Failure(request, "ProviderUnavailable", "The worker has no UI Automation provider.");
        }

        try
        {
            var observationRequest = Deserialize<DesktopObservationRequest>(request.Payload);
            Pointframe.Engine.Automation.DesktopTrace.Write(
                $"worker inspect pid={observationRequest.Process.ProcessId}");
            var snapshot = _uiObservation.Inspect(observationRequest);
            cancellationToken.ThrowIfCancellationRequested();
            Pointframe.Engine.Automation.DesktopTrace.Write(
                $"worker inspect ok status={snapshot.Status} elements={snapshot.Elements.Count}");
            return new DesktopAutomationWorkerResponse(
                DesktopAutomationWorkerProtocol.Version,
                request.RequestId,
                true,
                "Ok",
                JsonSerializer.Serialize(snapshot));
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // A UI Automation failure must degrade the observation, not kill the worker. An unhandled
            // exception here closes the pipe, and every later request fails with EndOfStream.
            Pointframe.Engine.Automation.DesktopTrace.Write(
                $"worker inspect threw {exception.GetType().Name}: {exception.Message}");
            return Failure(request, "ProviderUnavailable", exception.Message);
        }
    }

    private DesktopAutomationWorkerResponse HandleUiAction(
        DesktopAutomationWorkerRequest request,
        Func<IWindowsUiAutomationActionProvider, string, JsonElement, bool> action,
        CancellationToken cancellationToken)
    {
        var payload = Deserialize<JsonElement>(request.Payload);
        var elementRef = payload.GetProperty("elementRef").GetString();
        if (string.IsNullOrWhiteSpace(elementRef))
        {
            return Failure(request, "InvalidPayload", "An element reference is required.");
        }

        var succeeded = action(_uiAutomation, elementRef, payload);
        cancellationToken.ThrowIfCancellationRequested();
        return new DesktopAutomationWorkerResponse(
            DesktopAutomationWorkerProtocol.Version,
            request.RequestId,
            succeeded,
            succeeded ? "Ok" : "InputDispatchFailed");
    }

    private static T Deserialize<T>(string? payload)
    {
        return payload is null
            ? throw new JsonException("A payload is required.")
            : JsonSerializer.Deserialize<T>(payload)
                ?? throw new JsonException("The payload was empty.");
    }

    private static DesktopAutomationWorkerResponse Failure(
        DesktopAutomationWorkerRequest request,
        string code,
        string message) =>
        new(DesktopAutomationWorkerProtocol.Version, request.RequestId, false, code, message);
}
