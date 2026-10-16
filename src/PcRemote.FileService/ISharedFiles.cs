using PcRemote.Contracts;

namespace PcRemote.FileService;

// Implementations must confine paths to a user-selected root, including link resolution.
public interface ISharedFiles
{
    Task<IReadOnlyList<FileEntry>> ListAsync(string relativePath, CancellationToken cancellationToken);
    Task DownloadAsync(string relativePath, Stream destination, CancellationToken cancellationToken);
    Task UploadAsync(string relativePath, Stream source, CancellationToken cancellationToken);
}
