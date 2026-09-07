using System.Drawing;

namespace Pointframe.Engine;

public interface IOcrEngineService
{
    Task<string?> RecognizeAsync(Bitmap bitmap, CancellationToken cancellationToken = default);
}
