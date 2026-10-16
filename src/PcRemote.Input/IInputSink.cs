using System.Threading;
using System.Threading.Tasks;
using PcRemote.Protocol;

namespace PcRemote.Input;

public interface IInputSink
{
    Task ApplyAsync(RemoteInput input, CancellationToken cancellationToken);
    Task ReleaseAllAsync(CancellationToken cancellationToken);
}
