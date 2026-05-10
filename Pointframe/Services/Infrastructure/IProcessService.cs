using System.Diagnostics;

namespace Pointframe.Services;

public interface IProcessService
{
    void Start(ProcessStartInfo startInfo);
}
