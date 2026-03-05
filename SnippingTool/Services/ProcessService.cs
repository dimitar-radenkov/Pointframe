using System.Diagnostics;

namespace SnippingTool.Services;

public sealed class ProcessService : IProcessService
{
    public void Start(ProcessStartInfo startInfo) => Process.Start(startInfo);
}
