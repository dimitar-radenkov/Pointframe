using System.Reflection;
using System.Text.Json;
using Pointframe.Engine.Automation.Models;
using Pointframe.Mcp.Configuration;

namespace Pointframe.Mcp.Automation;

public sealed class WorkerDesktopAutomationService :
    IWindowsDesktopInputService,
    IWindowsUiAutomationActionProvider,
    IAsyncDisposable
{
    private readonly DesktopAutomationWorkerHost _host;
    private readonly DesktopControlGuard _controlGuard;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private bool _started;

    public WorkerDesktopAutomationService(
        DesktopTestingHostOptions options,
        DesktopControlGuard? controlGuard = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _controlGuard = controlGuard ?? new DesktopControlGuard();
        var executable = Environment.ProcessPath
            ?? throw new InvalidOperationException("The MCP worker executable path is unavailable.");
        _host = new DesktopAutomationWorkerHost(executable, Assembly.GetExecutingAssembly().Location);
    }

    public Task<DesktopInputPreflightResult> FocusAsync(DesktopInputTarget target, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
        DispatchInputAsync<DesktopInputTarget>(DesktopAutomationWorkerProtocol.Operations.Focus, target, expectedProcess, cancellationToken);

    public Task<DesktopInputPreflightResult> ClickAsync(DesktopClickRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
        DispatchInputAsync<DesktopClickRequest>(DesktopAutomationWorkerProtocol.Operations.Click, request, expectedProcess, cancellationToken);

    public Task<DesktopInputPreflightResult> PressKeysAsync(DesktopKeyPressRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
        DispatchInputAsync<DesktopKeyPressRequest>(DesktopAutomationWorkerProtocol.Operations.PressKeys, request, expectedProcess, cancellationToken);

    public Task<DesktopInputPreflightResult> DragAsync(DesktopDragRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
        DispatchInputAsync<DesktopDragRequest>(DesktopAutomationWorkerProtocol.Operations.Drag, request, expectedProcess, cancellationToken);

    public Task<DesktopInputPreflightResult> EnterTextAsync(DesktopTextRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
        DispatchInputAsync<DesktopTextRequest>(DesktopAutomationWorkerProtocol.Operations.EnterText, request, expectedProcess, cancellationToken);

    public Task<DesktopInputPreflightResult> ScrollAsync(DesktopScrollRequest request, DesktopProcessIdentity expectedProcess, CancellationToken cancellationToken = default) =>
        DispatchInputAsync<DesktopScrollRequest>(DesktopAutomationWorkerProtocol.Operations.Scroll, request, expectedProcess, cancellationToken);

    public void ReleaseOwnedInput()
    {
        _ = TryReleaseOwnedInput();
    }

    public bool TryReleaseOwnedInput()
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var response = DispatchAsync(DesktopAutomationWorkerProtocol.Operations.Release, new { }, timeout.Token)
                .GetAwaiter()
                .GetResult();
            if (response.Succeeded)
            {
                return true;
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
            or IOException
            or EndOfStreamException
            or TimeoutException
            or OperationCanceledException)
        {
        }

        return _controlGuard.TryReleaseOwnedInput();
    }

    public bool TryInvoke(string elementRef) =>
        DispatchUi(DesktopAutomationWorkerProtocol.Operations.Invoke, new { elementRef });

    public bool TrySetValue(string elementRef, string value) =>
        DispatchUi(DesktopAutomationWorkerProtocol.Operations.SetValue, new { elementRef, value });

    public async ValueTask DisposeAsync()
    {
        await _host.DisposeAsync().ConfigureAwait(false);
        _startGate.Dispose();
    }

    private async Task<DesktopInputPreflightResult> DispatchInputAsync<T>(
        string operation,
        T input,
        DesktopProcessIdentity expectedProcess,
        CancellationToken cancellationToken)
    {
        var response = await DispatchAsync(
            operation,
            new DesktopAutomationWorkerInputRequest(operation, input!, expectedProcess),
            cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(response.Payload))
        {
            return JsonSerializer.Deserialize<DesktopInputPreflightResult>(response.Payload)
                ?? DesktopInputPreflightResult.Invalid(response.Code, response.Code);
        }

        return response.Succeeded
            ? DesktopInputPreflightResult.Valid()
            : DesktopInputPreflightResult.Invalid(response.Code, response.Code);
    }

    private bool DispatchUi(string operation, object payload)
    {
        var response = DispatchAsync(operation, payload, CancellationToken.None).GetAwaiter().GetResult();
        return response.Succeeded;
    }

    private async Task<DesktopAutomationWorkerResponse> DispatchAsync(
        string operation,
        object payload,
        CancellationToken cancellationToken)
    {
        await EnsureStartedAsync(cancellationToken).ConfigureAwait(false);
        return await _host.DispatchAsync(
            new DesktopAutomationWorkerRequest(
                DesktopAutomationWorkerProtocol.Version,
                Guid.NewGuid().ToString("N"),
                operation,
                JsonSerializer.Serialize(payload)),
            cancellationToken).ConfigureAwait(false);
    }

    private async Task EnsureStartedAsync(CancellationToken cancellationToken)
    {
        if (_started)
        {
            return;
        }

        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_started)
            {
                await _host.StartAsync(cancellationToken).ConfigureAwait(false);
                _started = true;
            }
        }
        finally
        {
            _startGate.Release();
        }
    }
}
