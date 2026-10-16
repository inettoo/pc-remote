using PcRemote.Contracts;

namespace PcRemote.FileService;

public interface ISharedFiles
{
    Task<DirectoryListing> ListAsync(string relativePath, CancellationToken cancellationToken);
    Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken);
    Task<IFileUpload> BeginUploadAsync(string relativePath, long length, CancellationToken cancellationToken);
}

public interface IFileUpload : IAsyncDisposable
{
    long Written { get; }
    Task WriteAsync(long offset, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken);
    Task<string> CommitAsync(string expectedSha256, CancellationToken cancellationToken);
}
