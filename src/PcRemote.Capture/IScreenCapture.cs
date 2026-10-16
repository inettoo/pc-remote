using System.Threading;
using System.Threading.Tasks;
using PcRemote.Contracts;

namespace PcRemote.Capture;

public interface IScreenCapture
{
    Task<ScreenFrame> CaptureAsync(CancellationToken cancellationToken);
}
