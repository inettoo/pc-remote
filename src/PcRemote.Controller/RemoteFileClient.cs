using System.Diagnostics;
using System.Security.Cryptography;
using System.Threading.Channels;
using PcRemote.Contracts;
using PcRemote.Protocol;

namespace PcRemote.Controller;

public sealed record TransferProgress(long Completed, long Total);

public sealed partial class RemoteDesktopClient
{
    private readonly SemaphoreSlim fileGate = new(1);
    private readonly object fileStateGate = new();
    private FileRequest? fileRequest;

    private sealed record FileRequest(Guid Id, Channel<Envelope> Messages, CancellationTokenSource Cancellation, CancellationToken Token);

    private async Task<FileRequest> BeginFileRequestAsync(CancellationToken cancellationToken)
    {
        if (!connected) throw new IOException("请先连接被控电脑。");
        if (!await fileGate.WaitAsync(0, cancellationToken)) throw new IOException("已有文件操作正在进行。");
        try
        {
            CancellationTokenSource linked;
            lock (stop)
            {
                ObjectDisposedException.ThrowIf(disposed, this);
                linked = CancellationTokenSource.CreateLinkedTokenSource(stop.Token, cancellationToken);
            }
            var request = new FileRequest(Guid.NewGuid(), Channel.CreateBounded<Envelope>(new BoundedChannelOptions(8) { SingleReader = true, SingleWriter = true }), linked, linked.Token);
            lock (fileStateGate) fileRequest = request;
            return request;
        }
        catch { fileGate.Release(); throw; }
    }

    private void EndFileRequest(FileRequest request, bool cancelRemote)
    {
        lock (fileStateGate) { if (fileRequest == request) fileRequest = null; }
        request.Messages.Writer.TryComplete();
        if (cancelRemote && !stop.IsCancellationRequested && !input.Writer.TryWrite(DesktopMessages.Message("FileCancel", new { }, request.Id))) Stop();
        request.Cancellation.Dispose();
        fileGate.Release();
    }

    private async Task RouteFileResponseAsync(Envelope message)
    {
        FileRequest? request;
        lock (fileStateGate) request = fileRequest;
        if (request is null || request.Id != message.RequestId || request.Token.IsCancellationRequested) return;
        try { await request.Messages.Writer.WriteAsync(message, request.Token); }
        catch (OperationCanceledException) when (request.Token.IsCancellationRequested) { }
        catch (ChannelClosedException) { /* Completed/cancelled operation: ignore its late response. */ }
    }

    private Task SendFileAsync<T>(FileRequest request, string type, T payload)
        => input.Writer.WriteAsync(DesktopMessages.Message(type, payload, request.Id), request.Token).AsTask();

    private static async Task<Envelope> ReadFileAsync(FileRequest request)
    {
        var message = await request.Messages.Reader.ReadAsync(request.Token).AsTask().WaitAsync(TimeSpan.FromSeconds(30), request.Token);
        if (message.Type == "FileError")
        {
            var problem = DesktopMessages.Read<FileProblem>(message);
            if (string.IsNullOrWhiteSpace(problem.Text) || problem.Text.Length > 256) throw new InvalidDataException("Invalid file error.");
            throw new IOException(problem.Text);
        }
        return message;
    }

    public Task<FileDirectory> ListFilesAsync(string relativePath, CancellationToken cancellationToken)
        => Task.Run(() => ListFilesCoreAsync(relativePath, cancellationToken), cancellationToken);

    private async Task<FileDirectory> ListFilesCoreAsync(string relativePath, CancellationToken cancellationToken)
    {
        var request = await BeginFileRequestAsync(cancellationToken);
        bool success = false;
        try
        {
            await SendFileAsync(request, "FileList", new FilePathRequest(relativePath));
            var message = await ReadFileAsync(request);
            if (message.Type != "FileDirectory") throw new InvalidDataException("Unexpected directory response.");
            var directory = DesktopMessages.Read<FileDirectory>(message);
            FileMessages.Validate(directory);
            if (!directory.RelativePath.Equals(relativePath, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Directory response path mismatch.");
            success = true;
            return directory;
        }
        finally { EndFileRequest(request, !success); }
    }

    public Task DownloadFileAsync(string relativePath, string destination, bool overwrite, IProgress<TransferProgress> progress, CancellationToken cancellationToken)
        => Task.Run(() => DownloadFileCoreAsync(relativePath, destination, overwrite, progress, cancellationToken), cancellationToken);

    private async Task DownloadFileCoreAsync(string relativePath, string destination, bool overwrite, IProgress<TransferProgress> progress, CancellationToken cancellationToken)
    {
        var request = await BeginFileRequestAsync(cancellationToken);
        bool success = false;
        string? temporary = null;
        try
        {
            await SendFileAsync(request, "FileDownload", new FilePathRequest(relativePath));
            var first = await ReadFileAsync(request);
            if (first.Type != "FileStarted") throw new InvalidDataException("Missing file length.");
            long length = DesktopMessages.Read<FileLength>(first).Length;
            if (length is < 0 or > FileTransferLimits.MaxFileBytes) throw new InvalidDataException("Invalid file length.");
            string target = Path.GetFullPath(destination);
            string parent = Path.GetDirectoryName(target) ?? throw new IOException("保存目录不可用。");
            temporary = Path.Combine(parent, ".pcremote-" + Guid.NewGuid().ToString("N") + ".partial");
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileTransferLimits.ChunkBytes, FileOptions.Asynchronous))
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                long written = 0, reported = 0;
                while (true)
                {
                    var message = await ReadFileAsync(request);
                    if (message.Type == "FileComplete")
                    {
                        var receipt = DesktopMessages.Read<FileReceipt>(message);
                        FileMessages.Validate(receipt);
                        if (written != length || receipt.Length != written
                            || !CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), Convert.FromHexString(receipt.Sha256)))
                            throw new InvalidDataException("下载完整性校验失败。");
                        break;
                    }
                    if (message.Type != "FileChunk") throw new InvalidDataException("Unexpected file block.");
                    var block = DesktopMessages.Read<FileBlock>(message);
                    FileMessages.Validate(block);
                    if (block.Offset != written || block.Data.Length > length - written) throw new InvalidDataException("File block order/length mismatch.");
                    await output.WriteAsync(block.Data, request.Token);
                    hash.AppendData(block.Data);
                    written += block.Data.Length;
                    ReportProgress(progress, written, length, ref reported);
                }
                await output.FlushAsync(request.Token);
            }
            request.Token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite);
            temporary = null;
            progress.Report(new(length, length));
            success = true;
        }
        finally
        {
            EndFileRequest(request, !success);
            if (temporary is not null) File.Delete(temporary);
        }
    }

    public Task UploadFileAsync(string sourcePath, string remotePath, IProgress<TransferProgress> progress, CancellationToken cancellationToken)
        => Task.Run(() => UploadFileCoreAsync(sourcePath, remotePath, progress, cancellationToken), cancellationToken);

    private async Task UploadFileCoreAsync(string sourcePath, string remotePath, IProgress<TransferProgress> progress, CancellationToken cancellationToken)
    {
        var request = await BeginFileRequestAsync(cancellationToken);
        bool success = false;
        try
        {
            await using var source = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferLimits.ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
            long length = source.Length;
            if (length > FileTransferLimits.MaxFileBytes) throw new IOException("当前单文件上限为 1 GiB。");
            await SendFileAsync(request, "FileUpload", new FileUploadRequest(remotePath, length));
            await ReadAckAsync(request, 0);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            byte[] buffer = new byte[FileTransferLimits.ChunkBytes];
            long offset = 0, reported = 0;
            while (true)
            {
                int count = await source.ReadAsync(buffer, request.Token);
                if (count == 0) break;
                hash.AppendData(buffer.AsSpan(0, count));
                await SendFileAsync(request, "FileUploadChunk", new FileBlock(offset, buffer[..count]));
                offset += count;
                await ReadAckAsync(request, offset);
                ReportProgress(progress, offset, length, ref reported);
            }
            string sha256 = Convert.ToHexString(hash.GetHashAndReset());
            await SendFileAsync(request, "FileUploadCommit", new FileReceipt(offset, sha256));
            var response = await ReadFileAsync(request);
            if (response.Type != "FileComplete") throw new InvalidDataException("Missing upload receipt.");
            var receipt = DesktopMessages.Read<FileReceipt>(response);
            FileMessages.Validate(receipt);
            if (receipt.Length != offset || !sha256.Equals(receipt.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("上传完整性校验失败。");
            progress.Report(new(offset, length));
            success = true;
        }
        finally { EndFileRequest(request, !success); }
    }

    private static async Task ReadAckAsync(FileRequest request, long offset)
    {
        var response = await ReadFileAsync(request);
        if (response.Type != "FileAck" || DesktopMessages.Read<FileOffset>(response).Offset != offset)
            throw new InvalidDataException("Invalid upload acknowledgement.");
    }

    private static void ReportProgress(IProgress<TransferProgress> progress, long done, long total, ref long reported)
    {
        if (reported == 0 || Stopwatch.GetElapsedTime(reported) >= TimeSpan.FromMilliseconds(100) || done == total)
        {
            progress.Report(new(done, total));
            reported = Stopwatch.GetTimestamp();
        }
    }
}
