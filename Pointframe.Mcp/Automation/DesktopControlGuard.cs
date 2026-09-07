using System.Security.Cryptography;
using System.Text;

namespace Pointframe.Mcp.Automation;

public sealed record DesktopControlGuardOptions(
    string? UserName = null,
    int? SessionId = null,
    string? DesktopName = null);

public sealed record DesktopControlAcquireResult(bool Acquired, string Code, string Message)
{
    public static DesktopControlAcquireResult Success() => new(true, "Ok", "The desktop lease was acquired.");

    public static DesktopControlAcquireResult Failure(string code, string message) => new(false, code, message);
}

public sealed record DesktopControlValidationResult(bool IsValid, string Code, string Message)
{
    public static DesktopControlValidationResult Valid() => new(true, "Ok", "The desktop lease is valid.");

    public static DesktopControlValidationResult Invalid(string code, string message) => new(false, code, message);
}

public interface IDesktopControlNativeAdapter : IDisposable
{
    bool TryAcquireMutex(string mutexName);

    bool TryRegisterPauseHotKey();

    bool IsCompetingInputDetected { get; }

    void MarkInjectedInput(TimeSpan duration);

    void RunMessageLoop(CancellationToken cancellationToken);
}

public interface IDesktopControlNativeAdapterFactory
{
    IDesktopControlNativeAdapter Create();
}

public sealed class DesktopControlGuard : IDisposable
{
    private readonly DesktopControlGuardOptions _options;
    private readonly IDesktopControlNativeAdapterFactory _adapterFactory;
    private readonly Func<bool>? _parentReleaseFallback;
    private readonly object _sync = new();
    private IDesktopControlNativeAdapter? _adapter;
    private Thread? _thread;
    private CancellationTokenSource? _stop;
    private string? _failureCode;
    private bool _acquired;
    private bool _paused;
    private bool _disposed;

    public DesktopControlGuard(
        DesktopControlGuardOptions? options = null,
        IDesktopControlNativeAdapterFactory? adapterFactory = null,
        Func<bool>? parentReleaseFallback = null)
    {
        _options = options ?? new DesktopControlGuardOptions();
        _adapterFactory = adapterFactory ?? new WindowsDesktopControlNativeAdapterFactory();
        _parentReleaseFallback = parentReleaseFallback;
    }

    public DesktopControlAcquireResult Acquire(CancellationToken cancellationToken = default)
    {
        ManualResetEventSlim ready;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acquired)
            {
                return DesktopControlAcquireResult.Success();
            }

            _stop = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            ready = new ManualResetEventSlim();
            _thread = new Thread(() =>
            {
                IDesktopControlNativeAdapter? adapter = null;
                try
                {
                    adapter = _adapterFactory.Create();
                    var mutexName = BuildMutexName(_options);
                    if (!adapter.TryAcquireMutex(mutexName))
                    {
                        _failureCode = "DesktopBusy";
                        return;
                    }

                    if (!adapter.TryRegisterPauseHotKey())
                    {
                        _failureCode = "HotKeyRegistrationFailed";
                        return;
                    }

                    lock (_sync)
                    {
                        _adapter = adapter;
                        _acquired = true;
                    }

                    ready.Set();
                    adapter.RunMessageLoop(_stop.Token);
                }
                catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                    _failureCode = "NativeInitializationFailed";
                }
                finally
                {
                    ready.Set();
                    lock (_sync)
                    {
                        _acquired = false;
                        if (!ReferenceEquals(_adapter, adapter))
                        {
                            adapter?.Dispose();
                        }
                    }
                }
            })
            {
                IsBackground = true,
                Name = "Pointframe desktop control guard",
            };
            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();
        }

        ready.Wait(cancellationToken);
        lock (_sync)
        {
            if (_acquired)
            {
                return DesktopControlAcquireResult.Success();
            }

            var result = DesktopControlAcquireResult.Failure(
                _failureCode ?? "DesktopBusy",
                "The desktop lease could not be acquired.");
            DisposeNativeResources();
            return result;
        }
    }

    public DesktopControlValidationResult Validate()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_acquired || _adapter is null)
            {
                return DesktopControlValidationResult.Invalid("DesktopUnavailable", "The desktop lease is not active.");
            }

            if (_paused)
            {
                return DesktopControlValidationResult.Invalid("SessionPaused", "The desktop lease is paused.");
            }

            if (_adapter.IsCompetingInputDetected)
            {
                return DesktopControlValidationResult.Invalid("DesktopBusy", "Competing desktop input was detected.");
            }

            return DesktopControlValidationResult.Valid();
        }
    }

    public void Pause()
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_acquired)
            {
                _paused = true;
            }
        }
    }

    public void MarkInjectedInput(TimeSpan duration)
    {
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _adapter?.MarkInjectedInput(duration);
        }
    }

    public bool TryReleaseOwnedInput()
    {
        Func<bool>? fallback;
        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            fallback = _parentReleaseFallback;
        }

        if (fallback is null)
        {
            return false;
        }

        try
        {
            return fallback();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        TryReleaseOwnedInputIfPossible();
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _stop?.Cancel();
        }

        if (_thread is { IsAlive: true } && !ReferenceEquals(Thread.CurrentThread, _thread))
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        DisposeNativeResources();
    }

    private void TryReleaseOwnedInputIfPossible()
    {
        lock (_sync)
        {
            if (_disposed || _parentReleaseFallback is null)
            {
                return;
            }
        }

        try
        {
            _ = _parentReleaseFallback();
        }
        catch (Exception exception) when (exception is InvalidOperationException or System.ComponentModel.Win32Exception)
        {
        }
    }

    private void DisposeNativeResources()
    {
        lock (_sync)
        {
            _adapter?.Dispose();
            _adapter = null;
            _acquired = false;
            _stop?.Dispose();
            _stop = null;
        }
    }

    private static string BuildMutexName(DesktopControlGuardOptions options)
    {
        var identity = string.Join(
            "|",
            options.UserName ?? Environment.UserName,
            options.SessionId?.ToString() ?? Environment.ProcessId.ToString(),
            options.DesktopName ?? "Default");
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(identity)));
        return $"Local\\Pointframe.DesktopTesting.{hash}";
    }
}

public sealed class WindowsDesktopControlNativeAdapterFactory : IDesktopControlNativeAdapterFactory
{
    public IDesktopControlNativeAdapter Create() => new WindowsDesktopControlNativeAdapter();
}

internal sealed class WindowsDesktopControlNativeAdapter : IDesktopControlNativeAdapter
{
    private Mutex? _mutex;
    private DateTimeOffset _injectedUntil;
    private uint _threadId;

    public bool IsCompetingInputDetected =>
        DateTimeOffset.UtcNow >= _injectedUntil && GetLastInputInfoValue() > 0;

    public bool TryAcquireMutex(string mutexName)
    {
        _mutex = new Mutex(false, mutexName);
        try
        {
            return _mutex.WaitOne(TimeSpan.Zero);
        }
        catch (AbandonedMutexException)
        {
            return true;
        }
    }

    public bool TryRegisterPauseHotKey()
    {
        _threadId = NativeMethods.GetCurrentThreadId();
        return NativeMethods.RegisterHotKey(nint.Zero, 1, NativeMethods.ModControl | NativeMethods.ModAlt, 0x13);
    }

    public void MarkInjectedInput(TimeSpan duration)
    {
        _injectedUntil = DateTimeOffset.UtcNow.Add(duration);
    }

    public void RunMessageLoop(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            while (NativeMethods.PeekMessage(out var message, nint.Zero, 0, 0, 1))
            {
                if (message.MessageId == NativeMethods.WmHotKey)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                }

                NativeMethods.TranslateMessage(ref message);
                NativeMethods.DispatchMessage(ref message);
            }

            cancellationToken.WaitHandle.WaitOne(25);
        }

        if (_threadId != 0)
        {
            NativeMethods.UnregisterHotKey(nint.Zero, 1);
        }

        _mutex?.ReleaseMutex();
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        _mutex = null;
    }

    private static uint GetLastInputInfoValue()
    {
        var info = new NativeMethods.LastInputInfo { CbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.LastInputInfo>() };
        return NativeMethods.GetLastInputInfo(ref info) ? info.DwTime : 0;
    }

    private static class NativeMethods
    {
        internal const uint ModAlt = 0x0001;
        internal const uint ModControl = 0x0002;
        internal const uint WmHotKey = 0x0312;

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(nint hWnd, int id, uint fsModifiers, uint vk);

        [System.Runtime.InteropServices.DllImport("user32.dll", SetLastError = true)]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(nint hWnd, int id);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool PeekMessage(out NativeMessage message, nint hWnd, uint min, uint max, uint remove);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref NativeMessage message);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        internal static extern nint DispatchMessage(ref NativeMessage message);

        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
        internal static extern bool GetLastInputInfo(ref LastInputInfo info);

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct NativeMessage
        {
            internal nint HWnd;
            internal uint MessageId;
            internal nuint WParam;
            internal nint LParam;
            internal uint Time;
            internal int PtX;
            internal int PtY;
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        internal struct LastInputInfo
        {
            internal uint CbSize;
            internal uint DwTime;
        }
    }
}
