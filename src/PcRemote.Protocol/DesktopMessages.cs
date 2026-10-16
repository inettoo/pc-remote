using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcRemote.Protocol;

public enum RemoteInputKind { Move = 0, LeftDown = 1, LeftUp = 2, RightDown = 3, RightUp = 4, Wheel = 5, KeyDown = 6, KeyUp = 7, ReleaseAll = 8 }
public sealed record RemoteInput(RemoteInputKind Kind, int X = 0, int Y = 0, int Key = 0, int Delta = 0);
public sealed record DesktopImage(int Width, int Height, byte[] Jpeg);
public sealed record SessionNotice(string Text);

public static class DesktopMessages
{
    public const int Port = 45832;
    public const int MaxJpegBytes = 600_000;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 8
    };

    public static Envelope Message<T>(string type, T payload) => new(FrameCodec.Major, FrameCodec.Minor, Guid.NewGuid(), type,
        JsonSerializer.SerializeToElement(payload, Options));

    public static T Read<T>(Envelope envelope) => envelope.Payload.Deserialize<T>(Options)
        ?? throw new InvalidDataException("Missing message payload.");

    public static void Validate(RemoteInput input)
    {
        if (!Enum.IsDefined(input.Kind) || input.X is < 0 or > 65535 || input.Y is < 0 or > 65535)
            throw new InvalidDataException("Invalid input coordinates.");
        if (input.Kind is RemoteInputKind.KeyDown or RemoteInputKind.KeyUp && input.Key is < 1 or > 254)
            throw new InvalidDataException("Invalid virtual key.");
        if (input.Kind == RemoteInputKind.Wheel && (input.Delta is < -1200 or > 1200 || input.Delta % 120 != 0))
            throw new InvalidDataException("Invalid wheel movement.");
    }

    public static void Validate(SessionNotice notice)
    {
        if (string.IsNullOrWhiteSpace(notice.Text) || notice.Text.Length > 256)
            throw new InvalidDataException("Invalid desktop notice.");
    }

    public static void Validate(DesktopImage image)
    {
        if (image.Width is < 1 or > 1920 || image.Height is < 1 or > 1920
            || (long)image.Width * image.Height > 3_686_400
            || image.Jpeg is null || image.Jpeg.Length is < 4 or > MaxJpegBytes)
            throw new InvalidDataException("Invalid screen image.");
    }
}
