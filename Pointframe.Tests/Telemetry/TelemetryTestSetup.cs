using System.Runtime.CompilerServices;
using Pointframe.Telemetry;

namespace Pointframe.Tests.Telemetry;

internal static class TelemetryTestSetup
{
    // Production flushes give up after 2 s; the first emit in a test process builds the OpenTelemetry
    // pipeline and can take longer than that on a loaded CI runner, which made export assertions flaky.
    [ModuleInitializer]
    internal static void RaiseFlushTimeout()
    {
        OperationTelemetry.FlushTimeoutMilliseconds = 30000;
    }
}
