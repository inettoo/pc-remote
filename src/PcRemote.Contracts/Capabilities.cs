namespace PcRemote.Contracts;

[Flags]
public enum SessionCapability { None = 0, View = 1, Control = 2, Files = 4 }
public sealed record Device(Guid Id, string Name, string Address);
public sealed record ScreenFrame(int Width, int Height, ReadOnlyMemory<byte> EncodedBytes);
public sealed record FileEntry(string RelativePath, long Length, bool IsDirectory);
