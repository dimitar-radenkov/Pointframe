using System.Text.Json;
using Pointframe.Engine.Automation.Models;

namespace Pointframe.Mcp.Automation;

public sealed class DesktopAutomationWorkerProvider : IDesktopAutomationWorkerProvider
{
    private readonly IWindowsDesktopInputService _input;
    private readonly IWindowsUiAutomationActionProvider _uiAutomation;

    public DesktopAutomationWorkerProvider(
        IWindowsDesktopInputService? input = null,
        IWindowsUiAutomationActionProvider? uiAutomation = null)
    {
        _input = input ?? new WindowsDesktopInputService();
        _uiAutomation = uiAutomation ?? new WindowsUiAutomationProvider(new FlaUiWindowsUiAutomationBackend());
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
