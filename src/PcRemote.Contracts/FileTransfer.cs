namespace PcRemote.Contracts;

public static class FileTransferLimits
{
    public const int ChunkBytes = 32 * 1024;
    public const long MaxFileBytes = 1024L * 1024 * 1024;
    public const int DirectoryEntries = 128;
    public const int RelativePathCharacters = 512;
}
public sealed record DirectoryListing(IReadOnlyList<FileEntry> Entries, bool Truncated);
