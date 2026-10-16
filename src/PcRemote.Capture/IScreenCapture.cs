using PcRemote.Contracts;

namespace PcRemote.Capture;

public interface IScreenCapture
{
    IAsyncEnumerable<ScreenFrame> CaptureAsync(CancellationToken cancellationToken);
}
