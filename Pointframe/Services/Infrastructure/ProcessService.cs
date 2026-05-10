using System.Diagnostics;

namespace Pointframe.Services;

public sealed class ProcessService : IProcessService
{
    public void Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}
