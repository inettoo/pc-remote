namespace PcRemote.Contracts;

public static class FilePathRules
{
    public static void ValidateRelative(string relativePath)
    {
        if (relativePath is null || relativePath.Length > FileTransferLimits.RelativePathCharacters
            || Path.IsPathRooted(relativePath) || relativePath.Contains(':') || relativePath.Any(char.IsControl))
            throw new InvalidDataException("请输入用户文件范围内的相对路径。");
        foreach (string component in relativePath.Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries))
        {
            if (component is "." or ".." || component.Length > 255 || component.EndsWith(' ') || component.EndsWith('.')
                || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) throw new InvalidDataException("无效文件路径。");
            string stem = component.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal))
                    && "123456789¹²³".Contains(stem[3]))) throw new InvalidDataException("不支持设备名称。");
        }
    }

}
