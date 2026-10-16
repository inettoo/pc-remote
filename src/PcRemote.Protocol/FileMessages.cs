using PcRemote.Contracts;

namespace PcRemote.Protocol;

public sealed record FilePathRequest(string RelativePath);
public sealed record RemoteFileItem(string Name, string RelativePath, bool IsDirectory, long Length);
public sealed record FileDirectory(string RelativePath, RemoteFileItem[] Items, bool Truncated);
public sealed record FileLength(long Length);
public sealed record FileBlock(long Offset, byte[] Data);
public sealed record FileReceipt(long Length, string Sha256);
public sealed record FileUploadRequest(string RelativePath, long Length);
public sealed record FileOffset(long Offset);
public sealed record FileProblem(string Code, string Text);

public static class FileMessages
{
    public static bool IsRequest(string type) => type is "FileList" or "FileDownload" or "FileUpload" or "FileUploadChunk" or "FileUploadCommit" or "FileCancel";
    public static bool IsResponse(string type) => type is "FileDirectory" or "FileStarted" or "FileChunk" or "FileComplete" or "FileAck" or "FileError";
    public static void Validate(FileBlock block)
    {
        if (block.Offset is < 0 or > FileTransferLimits.MaxFileBytes || block.Data is null || block.Data.Length is < 1 or > FileTransferLimits.ChunkBytes)
            throw new InvalidDataException("Invalid file block.");
    }
    public static void Validate(FileReceipt receipt)
    {
        if (receipt.Length is < 0 or > FileTransferLimits.MaxFileBytes || receipt.Sha256 is null || receipt.Sha256.Length != 64
            || receipt.Sha256.Any(character => !char.IsAsciiHexDigit(character))) throw new InvalidDataException("Invalid file receipt.");
    }
    public static void Validate(FileDirectory directory)
    {
        if (directory.RelativePath is null || directory.RelativePath.Length > FileTransferLimits.RelativePathCharacters
            || directory.Items is null || directory.Items.Length > FileTransferLimits.DirectoryEntries)
            throw new InvalidDataException("Invalid directory response.");
        FilePathRules.ValidateRelative(directory.RelativePath);
        foreach (var item in directory.Items)
        {
            if (item is null || string.IsNullOrWhiteSpace(item.Name) || item.Name.Length > 255 || item.Name.Any(char.IsControl)
                || string.IsNullOrWhiteSpace(item.RelativePath) || item.RelativePath.Length > FileTransferLimits.RelativePathCharacters || item.Length < 0)
                throw new InvalidDataException("Invalid directory entry.");
            if (item.Name.Contains('/') || item.Name.Contains((char)92)) throw new InvalidDataException("Invalid file name.");
            FilePathRules.ValidateRelative(item.Name);
            FilePathRules.ValidateRelative(item.RelativePath);
            if (!Path.GetFileName(item.RelativePath).Equals(item.Name, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("File name/path mismatch.");
        }
    }
}
