using System.Security.Cryptography;
using PcRemote.Contracts;

namespace PcRemote.FileService;

public sealed class RootedFiles : ISharedFiles
{
    private readonly string root;
    private readonly string prefix;

    public RootedFiles(string root)
    {
        this.root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        prefix = this.root + Path.DirectorySeparatorChar;
        if (!Directory.Exists(this.root) || (File.GetAttributes(this.root) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("用户文件根目录不可用或包含链接。");
    }

    public Task<DirectoryListing> ListAsync(string relativePath, CancellationToken cancellationToken)
        => Task.Run(() =>
        {
            string path = Resolve(relativePath);
            var entries = new List<FileEntry>();
            bool truncated = false;
            int examined = 0;
            foreach (string child in Directory.EnumerateFileSystemEntries(path))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (++examined > FileTransferLimits.DirectoryEntries * 2) { truncated = true; break; }
                if (Path.GetFileName(child).StartsWith(".pcremote-", StringComparison.Ordinal)) continue;
                FileAttributes attributes;
                try { attributes = File.GetAttributes(child); }
                catch (FileNotFoundException) { continue; } // A concurrently removed entry is not a persistent error.
                if ((attributes & FileAttributes.ReparsePoint) != 0) { truncated = true; continue; }
                if (entries.Count == FileTransferLimits.DirectoryEntries) { truncated = true; break; }
                string relative = Path.GetRelativePath(root, child);
                try { FilePathRules.ValidateRelative(relative); }
                catch (InvalidDataException) { truncated = true; continue; }
                bool directory = (attributes & FileAttributes.Directory) != 0;
                entries.Add(new FileEntry(relative, directory ? 0 : new FileInfo(child).Length, directory));
            }
            return new DirectoryListing(entries.OrderByDescending(value => value.IsDirectory).ThenBy(value => value.RelativePath, StringComparer.OrdinalIgnoreCase).ToArray(), truncated);
        }, cancellationToken);

    public Task<Stream> OpenReadAsync(string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Resolve(relativePath);
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileTransferLimits.ChunkBytes, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (stream.Length > FileTransferLimits.MaxFileBytes) { stream.Dispose(); throw new IOException("文件超过当前 1 GiB 上限。"); }
        return Task.FromResult<Stream>(stream);
    }

    public Task<IFileUpload> BeginUploadAsync(string relativePath, long length, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (length is < 0 or > FileTransferLimits.MaxFileBytes) throw new InvalidDataException("Invalid upload length.");
        string target = Resolve(relativePath, allowMissingLeaf: true);
        if (File.Exists(target) || Directory.Exists(target)) throw new IOException("同名文件已存在，请更换文件名。");
        string parent = Path.GetDirectoryName(target) ?? throw new IOException("Invalid upload directory.");
        if (!Directory.Exists(parent)) throw new DirectoryNotFoundException("上传目录不存在。");
        string temporary = Path.Combine(parent, ".pcremote-" + Guid.NewGuid().ToString("N") + ".partial");
        return Task.FromResult<IFileUpload>(new Upload(target, temporary, length));
    }

    private string Resolve(string relativePath, bool allowMissingLeaf = false)
    {
        FilePathRules.ValidateRelative(relativePath);
        string path = Path.GetFullPath(Path.Combine(root, relativePath));
        if (path != root && !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("路径不在用户文件范围内。");
        if ((File.GetAttributes(root) & FileAttributes.ReparsePoint) != 0) throw new IOException("用户文件目录已改变。");
        string current = root;
        string[] components = relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < components.Length; i++)
        {
            current = Path.Combine(current, components[i]);
            if (allowMissingLeaf && i == components.Length - 1 && !File.Exists(current) && !Directory.Exists(current)) break;
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("当前不支持链接、联接或云占位路径。");
        }
        return path;
    }

    private sealed class Upload : IFileUpload
    {
        private readonly string target, temporary;
        private readonly long length;
        private readonly FileStream stream;
        private readonly IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        private readonly SemaphoreSlim gate = new(1);
        private bool committed;
        private int disposed;
        public long Written { get; private set; }

        public Upload(string target, string temporary, long length)
        {
            this.target = target;
            this.temporary = temporary;
            this.length = length;
            stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, FileTransferLimits.ChunkBytes, FileOptions.Asynchronous);
        }

        public async Task WriteAsync(long offset, ReadOnlyMemory<byte> bytes, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(disposed != 0 || committed, this);
                if (offset != Written || bytes.Length is < 1 or > FileTransferLimits.ChunkBytes || bytes.Length > length - Written)
                    throw new InvalidDataException("文件块顺序或大小无效。");
                await stream.WriteAsync(bytes, cancellationToken);
                hash.AppendData(bytes.Span);
                Written += bytes.Length;
            }
            finally { gate.Release(); }
        }

        public async Task<string> CommitAsync(string expectedSha256, CancellationToken cancellationToken)
        {
            await gate.WaitAsync(cancellationToken);
            try
            {
                ObjectDisposedException.ThrowIf(disposed != 0 || committed, this);
                if (Written != length) throw new InvalidDataException("上传未完成。");
                byte[] actual = hash.GetCurrentHash();
                if (expectedSha256 is null || expectedSha256.Length != 64
                    || expectedSha256.Any(character => !char.IsAsciiHexDigit(character)))
                    throw new InvalidDataException("无效文件校验值。");
                if (!CryptographicOperations.FixedTimeEquals(actual, Convert.FromHexString(expectedSha256)))
                    throw new InvalidDataException("文件完整性校验失败。");
                await stream.FlushAsync(cancellationToken);
                await stream.DisposeAsync();
                cancellationToken.ThrowIfCancellationRequested();
                // Never overwrite an existing destination; commit is atomic on the same volume.
                File.Move(temporary, target, overwrite: false);
                committed = true;
                return Convert.ToHexString(actual);
            }
            finally { gate.Release(); }
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            await gate.WaitAsync();
            try
            {
                await stream.DisposeAsync();
                hash.Dispose();
                if (!committed) File.Delete(temporary);
            }
            finally { gate.Release(); gate.Dispose(); }
        }
    }
}
