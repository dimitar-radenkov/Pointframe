using System.Diagnostics;
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
    private readonly IDesktopAutomationWorkerHost _host;
    private readonly DesktopControlGuard _controlGuard;
    private readonly int _warmupMilliseconds;
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
        _warmupMilliseconds = DesktopTestingLimits.WorkerWarmupMilliseconds;
    }

    internal WorkerDesktopAutomationService(
        IDesktopAutomationWorkerHost host,
        int warmupMilliseconds = DesktopTestingLimits.WorkerWarmupMilliseconds,
        DesktopControlGuard? controlGuard = null)
    {
        ArgumentNullException.ThrowIfNull(host);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(warmupMilliseconds);
        _host = host;
        _warmupMilliseconds = warmupMilliseconds;
        _controlGuard = controlGuard ?? new DesktopControlGuard();
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

    public UiInvokeOutcome TryInvoke(string elementRef)
    {
        var response = DispatchAsync(
                DesktopAutomationWorkerProtocol.Operations.Invoke,
                new { elementRef },
                CancellationToken.None)
            .GetAwaiter()
            .GetResult();
        if (!response.Succeeded)
        {
            return UiInvokeOutcome.Failed;
        }

        return response.Code switch
        {
            "InvokePending" => UiInvokeOutcome.Pending,
            "Ok" => UiInvokeOutcome.Completed,
            _ => UiInvokeOutcome.Failed,
        };
    }

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

    public string? InspectUi(Pointframe.Engine.Automation.Models.DesktopObservationRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Inspection walks a live UI tree, so it needs a longer budget than a single input event; the
        // worker itself caps the walk, and this only stops a wedged worker from blocking the caller.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var response = DispatchAsync(
                DesktopAutomationWorkerProtocol.Operations.Inspect,
                request,
                timeout.Token)
            .GetAwaiter()
            .GetResult();
        Pointframe.Engine.Automation.DesktopTrace.Write($"InspectUi succeeded={response.Succeeded} code={response.Code}");
        return response.Succeeded ? response.Payload : null;
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
        try
        {
            return await _host.DispatchAsync(
                new DesktopAutomationWorkerRequest(
                    DesktopAutomationWorkerProtocol.Version,
                    Guid.NewGuid().ToString("N"),
                    operation,
                    DesktopAutomationWorkerProtocol.SerializePayload(payload)),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidDataException or IOException)
        {
            // The request may already have reached the worker, which keeps processing it after this
            // side gives up waiting (a timeout only cancels the parent-side pipe read, it does not
            // reach the worker), or the pipe just returned something other than this request's own
            // response. Either way the connection can no longer be trusted to stay aligned with future
            // request/response pairs, so abandon it outright rather than reuse a pipe that may still be
            // desynchronized; the next call starts a fresh worker and pipe.
            await AbandonWorkerAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task AbandonWorkerAsync()
    {
        await _startGate.WaitAsync().ConfigureAwait(false);
        try
        {
            await _host.AbandonAsync().ConfigureAwait(false);
            _started = false;
        }
        finally
        {
            _startGate.Release();
        }
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
                for (var attempt = 1; attempt <= 3; attempt++)
                {
                    var stopwatch = Stopwatch.StartNew();
                    try
                    {
                        await _host.StartAsync(cancellationToken).ConfigureAwait(false);
                        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                        timeout.CancelAfter(_warmupMilliseconds);
                        var response = await _host.DispatchAsync(
                            new DesktopAutomationWorkerRequest(
                                DesktopAutomationWorkerProtocol.Version,
                                Guid.NewGuid().ToString("N"),
                                DesktopAutomationWorkerProtocol.Operations.Ping,
                                "{}"),
                            timeout.Token).ConfigureAwait(false);
                        if (response.Succeeded)
                        {
                            _started = true;
                            stopwatch.Stop();
                            Pointframe.Engine.Automation.DesktopTrace.Write($"worker warmup ok attempt={attempt} ms={stopwatch.ElapsedMilliseconds}");
                            return;
                        }
                    }
                    catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception exception) when (exception is OperationCanceledException
                        or IOException
                        or InvalidOperationException
                        or TimeoutException)
                    {
                    }

                    stopwatch.Stop();
                    Pointframe.Engine.Automation.DesktopTrace.Write($"worker warmup failed attempt={attempt}");
                    await _host.AbandonAsync().ConfigureAwait(false);
                }

                _started = true;
                throw new IOException("The desktop automation worker did not pass its UI Automation warmup.");
            }
        }
        finally
        {
            _startGate.Release();
        }
    }
}
