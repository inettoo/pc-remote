using System.Security.Cryptography;
using PcRemote.Contracts;
using PcRemote.FileService;
using PcRemote.Protocol;

namespace PcRemote.Host;

internal sealed class AgentFileTransfers(
    Func<ISharedFiles> createFiles,
    Func<Envelope, CancellationToken, Task> send,
    CancellationToken lifetime) : IAsyncDisposable
{
    private readonly Lazy<ISharedFiles> files = new(createFiles);
    private CancellationTokenSource? downloadCancellation;
    private Task? download;
    private Guid downloadId, uploadId;
    private IFileUpload? upload;
    private bool Busy => upload is not null || download is { IsCompleted: false };

    public async Task HandleAsync(Envelope message)
    {
        try
        {
            if (message.Type == "FileCancel")
            {
                if (message.RequestId == downloadId) downloadCancellation?.Cancel();
                if (message.RequestId == uploadId && upload is not null) { await upload.DisposeAsync(); upload = null; }
                return;
            }
            if (message.Type == "FileList")
            {
                var path = DesktopMessages.Read<FilePathRequest>(message);
                var listing = await files.Value.ListAsync(path.RelativePath, lifetime);
                var directory = new FileDirectory(path.RelativePath, listing.Entries.Select(value =>
                    new RemoteFileItem(Path.GetFileName(value.RelativePath), value.RelativePath, value.IsDirectory, value.Length)).ToArray(), listing.Truncated);
                await send(DesktopMessages.Message("FileDirectory", directory, message.RequestId), lifetime);
                return;
            }
            if (message.Type == "FileDownload")
            {
                if (Busy) throw new IOException("File operation already active.");
                if (download is not null) await download;
                downloadCancellation?.Dispose();
                downloadCancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
                downloadId = message.RequestId;
                download = DownloadAsync(message, downloadCancellation.Token);
                return;
            }
            if (message.Type == "FileUpload")
            {
                if (Busy) throw new IOException("File operation already active.");
                var request = DesktopMessages.Read<FileUploadRequest>(message);
                upload = await files.Value.BeginUploadAsync(request.RelativePath, request.Length, lifetime);
                uploadId = message.RequestId;
                await send(DesktopMessages.Message("FileAck", new FileOffset(0), uploadId), lifetime);
                return;
            }
            if (upload is null || message.RequestId != uploadId) throw new InvalidDataException("No matching upload.");
            if (message.Type == "FileUploadChunk")
            {
                var block = DesktopMessages.Read<FileBlock>(message);
                FileMessages.Validate(block);
                await upload.WriteAsync(block.Offset, block.Data, lifetime);
                await send(DesktopMessages.Message("FileAck", new FileOffset(upload.Written), uploadId), lifetime);
                return;
            }
            if (message.Type == "FileUploadCommit")
            {
                var receipt = DesktopMessages.Read<FileReceipt>(message);
                FileMessages.Validate(receipt);
                if (receipt.Length != upload.Written) throw new InvalidDataException("File length mismatch.");
                string hash = await upload.CommitAsync(receipt.Sha256, lifetime);
                long written = upload.Written;
                await upload.DisposeAsync();
                upload = null;
                await send(DesktopMessages.Message("FileComplete", new FileReceipt(written, hash), uploadId), lifetime);
                return;
            }
            throw new InvalidDataException("Unexpected file command.");
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            if (message.RequestId == uploadId && upload is not null) { await upload.DisposeAsync(); upload = null; }
            // Do not send OS exceptions containing private absolute paths to the network.
            await send(DesktopMessages.Message("FileError", new FileProblem("failed", "文件操作失败：请检查路径、权限、同名文件或大小限制。"), message.RequestId), lifetime);
        }
    }

    private async Task DownloadAsync(Envelope request, CancellationToken cancellationToken)
    {
        try
        {
            string path = DesktopMessages.Read<FilePathRequest>(request).RelativePath;
            await using var source = await files.Value.OpenReadAsync(path, cancellationToken);
            long length = source.Length;
            if (length is < 0 or > FileTransferLimits.MaxFileBytes) throw new InvalidDataException("Unsupported file length.");
            await send(DesktopMessages.Message("FileStarted", new FileLength(length), request.RequestId), cancellationToken);
            byte[] buffer = new byte[FileTransferLimits.ChunkBytes];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long offset = 0;
            while (true)
            {
                int count = await source.ReadAsync(buffer, cancellationToken);
                if (count == 0) break;
                if (count > length - offset) throw new InvalidDataException("File length changed.");
                hash.AppendData(buffer.AsSpan(0, count));
                await send(DesktopMessages.Message("FileChunk", new FileBlock(offset, buffer[..count]), request.RequestId), cancellationToken);
                offset += count;
            }
            if (offset != length) throw new InvalidDataException("Incomplete source file.");
            await send(DesktopMessages.Message("FileComplete", new FileReceipt(offset, Convert.ToHexString(hash.GetHashAndReset())), request.RequestId), cancellationToken);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            await send(DesktopMessages.Message("FileError", new FileProblem("cancelled", "文件传输已取消。"), request.RequestId), lifetime);
        }
        catch (Exception)
        {
            await send(DesktopMessages.Message("FileError", new FileProblem("failed", "下载失败：文件不可访问、超过 1 GiB 或发生变化。"), request.RequestId), lifetime);
        }
    }

    public async ValueTask DisposeAsync()
    {
        downloadCancellation?.Cancel();
        if (download is not null)
        {
            try { await download; }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (IOException) when (lifetime.IsCancellationRequested) { }
        }
        downloadCancellation?.Dispose();
        if (upload is not null) await upload.DisposeAsync();
    }
}
