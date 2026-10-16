using PcRemote.Contracts;

namespace PcRemote.Input;

public interface IInputSink
{
    Task ApplyAsync(InputEvent input, CancellationToken cancellationToken);
    Task ReleaseAllAsync(CancellationToken cancellationToken);
}
