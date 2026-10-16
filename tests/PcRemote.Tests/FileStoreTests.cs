using System.Security.Cryptography;
using PcRemote.Contracts;
using PcRemote.FileService;

internal static class FileStoreTests
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        string testBase = Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, "work", "file-tests"));
        string workspace = Path.GetFullPath(Path.Combine(testBase, Guid.NewGuid().ToString("N")));
        if (!workspace.StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new Exception("Invalid test workspace.");
        Directory.CreateDirectory(workspace);
        string root = Path.Combine(workspace, "shared");
        Directory.CreateDirectory(root);
        try
        {
            var files = new RootedFiles(root);
            await check("file store rejects traversal, absolute paths, ADS and Windows device names", async () =>
            {
                foreach (string path in new[] { "../escape", "sub/../escape", Path.GetTempPath(), "file.txt:stream", "CON", "CON.foo.bar",
                    "NUL.txt", "LPT1", "COM¹.txt", "trailing.", "trailing ", "bad" + (char)0, new string('a', 513) })
                {
                    try { await files.BeginUploadAsync(path, 1, CancellationToken.None); }
                    catch (InvalidDataException) { continue; }
                    throw new Exception("Accepted unsafe file path.");
                }
            });
            await check("file upload becomes visible only after complete hash-verified commit", async () =>
            {
                byte[] bytes = RandomNumberGenerator.GetBytes(100_000);
                await using (var upload = await files.BeginUploadAsync("transfer.bin", bytes.Length, CancellationToken.None))
                {
                    for (int offset = 0; offset < bytes.Length; offset += FileTransferLimits.ChunkBytes)
                        await upload.WriteAsync(offset, bytes.AsMemory(offset, Math.Min(FileTransferLimits.ChunkBytes, bytes.Length - offset)), CancellationToken.None);
                    if (File.Exists(Path.Combine(root, "transfer.bin"))) throw new Exception("Partial destination exposed.");
                    await upload.CommitAsync(Convert.ToHexString(SHA256.HashData(bytes)), CancellationToken.None);
                }
                await using var source = await files.OpenReadAsync("transfer.bin", CancellationToken.None);
                using var output = new MemoryStream();
                await source.CopyToAsync(output);
                if (!bytes.SequenceEqual(output.ToArray())) throw new Exception("Transfer content mismatch.");
            });
            await check("file store never overwrites existing destination", async () =>
            {
                try { await files.BeginUploadAsync("transfer.bin", 1, CancellationToken.None); }
                catch (IOException) { return; }
                throw new Exception("Existing file accepted.");
            });
            await check("wrong hash never commits and disposal cleans temporary data", async () =>
            {
                await using (var upload = await files.BeginUploadAsync("bad.bin", 1, CancellationToken.None))
                {
                    await upload.WriteAsync(0, new byte[] { 1 }, CancellationToken.None);
                    bool rejected = false;
                    try { await upload.CommitAsync(new string('0', 64), CancellationToken.None); }
                    catch (InvalidDataException) { rejected = true; }
                    if (!rejected) throw new Exception("Wrong checksum was accepted.");
                    if (File.Exists(Path.Combine(root, "bad.bin"))) throw new Exception("Corrupt file committed.");
                }
                if (Directory.GetFiles(root, "*.partial").Length != 0) throw new Exception("Temporary file leaked.");
            });
            await check("out-of-order, oversized and incomplete chunks are rejected", async () =>
            {
                await using var upload = await files.BeginUploadAsync("ordering.bin", 2, CancellationToken.None);
                foreach (var chunk in new[] { (1L, new byte[] { 1 }), (0L, new byte[FileTransferLimits.ChunkBytes + 1]), (0L, new byte[] { 1, 2, 3 }) })
                {
                    try { await upload.WriteAsync(chunk.Item1, chunk.Item2, CancellationToken.None); }
                    catch (InvalidDataException) { continue; }
                    throw new Exception("Invalid chunk accepted.");
                }
                try { await upload.CommitAsync(new string('0', 64), CancellationToken.None); }
                catch (InvalidDataException) { return; }
                throw new Exception("Incomplete upload committed.");
            });
            await check("canceled upload cleans temporary data", async () =>
            {
                await using (var upload = await files.BeginUploadAsync("cancel.bin", 1, CancellationToken.None))
                {
                    using var cancellation = new CancellationTokenSource();
                    cancellation.Cancel();
                    bool cancelled = false;
                    try { await upload.WriteAsync(0, new byte[] { 1 }, cancellation.Token); }
                    catch (OperationCanceledException) { cancelled = true; }
                    if (!cancelled) throw new Exception("Canceled write was accepted.");
                }
                if (Directory.GetFiles(root, "*.partial").Length != 0 || File.Exists(Path.Combine(root, "cancel.bin")))
                    throw new Exception("Canceled data leaked.");
            });
            await check("file length cap and empty file commit", async () =>
            {
                bool rejected = false;
                try { await using var unexpected = await files.BeginUploadAsync("large.bin", FileTransferLimits.MaxFileBytes + 1, CancellationToken.None); }
                catch (InvalidDataException) { rejected = true; }
                if (!rejected) throw new Exception("Oversized upload was accepted.");
                if (File.Exists(Path.Combine(root, "large.bin"))) throw new Exception("Oversized file created.");
                await using var upload = await files.BeginUploadAsync("empty.bin", 0, CancellationToken.None);
                await upload.CommitAsync(Convert.ToHexString(SHA256.HashData(Array.Empty<byte>())), CancellationToken.None);
                if (new FileInfo(Path.Combine(root, "empty.bin")).Length != 0) throw new Exception("Empty file failed.");
            });
            await check("directory listing is bounded", async () =>
            {
                Directory.CreateDirectory(Path.Combine(root, "many"));
                for (int i = 0; i < 140; i++) await File.WriteAllTextAsync(Path.Combine(root, "many", $"file-{i}.txt"), "x");
                var listing = await files.ListAsync("many", CancellationToken.None);
                if (!listing.Truncated || listing.Entries.Count != FileTransferLimits.DirectoryEntries) throw new Exception("Listing is not bounded.");
            });
            await check("directory links cannot escape user root", async () =>
            {
                string outside = Path.Combine(workspace, "outside");
                Directory.CreateDirectory(outside);
                await File.WriteAllTextAsync(Path.Combine(outside, "private.txt"), "outside");
                try { Directory.CreateSymbolicLink(Path.Combine(root, "link"), outside); }
                catch (UnauthorizedAccessException) { throw new SkipException("OS link creation privilege unavailable."); }
                catch (IOException exception) when ((exception.HResult & 0xffff) == 1314) { throw new SkipException("OS link creation privilege unavailable."); }
                try { await using var unexpected = await files.OpenReadAsync("link/private.txt", CancellationToken.None); }
                catch (IOException) { return; }
                throw new Exception("Followed link outside user root.");
            });
        }
        finally
        {
            string resolved = Path.GetFullPath(workspace);
            if (!resolved.StartsWith(testBase + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || (File.GetAttributes(resolved) & FileAttributes.ReparsePoint) != 0) throw new Exception("Unsafe test cleanup.");
            Directory.Delete(resolved, recursive: true);
        }
    }
}
