using System.Diagnostics;

namespace SnippingTool.Services;

public interface IProcessService
{
    void Start(ProcessStartInfo startInfo);
}
