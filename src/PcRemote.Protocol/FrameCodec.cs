using System.Buffers.Binary;
using System.Text.Json;

namespace PcRemote.Protocol;

public sealed record Envelope(int Major, int Minor, Guid RequestId, string Type, JsonElement Payload);

public static class FrameCodec
{
    public const int Major = 2;
    public const int Minor = 0;
    public const int MaxPayloadBytes = 1024 * 1024;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true
    };

    public static async Task WriteAsync(Stream stream, Envelope envelope, CancellationToken cancellationToken = default)
    {
        Validate(envelope);
        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(envelope, Options);
        ValidateLength(payload.Length);
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, payload.Length);
        await stream.WriteAsync(header, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
    }

    public static async Task<Envelope> ReadAsync(Stream stream, CancellationToken cancellationToken = default)
    {
        byte[] header = new byte[4];
        await stream.ReadExactlyAsync(header, cancellationToken);
        int length = BinaryPrimitives.ReadInt32BigEndian(header);
        ValidateLength(length);
        byte[] payload = new byte[length];
        await stream.ReadExactlyAsync(payload, cancellationToken);
        Envelope envelope = JsonSerializer.Deserialize<Envelope>(payload, Options)
            ?? throw new InvalidDataException("Missing envelope.");
        Validate(envelope);
        return envelope;
    }

    private static void ValidateLength(int length)
    {
        if (length <= 0 || length > MaxPayloadBytes)
            throw new InvalidDataException("Frame length is outside the allowed range.");
    }

    private static void Validate(Envelope envelope)
    {
        if (envelope.Major != Major || envelope.Minor != Minor)
            throw new InvalidDataException("Unsupported protocol version.");
        if (envelope.RequestId == Guid.Empty || string.IsNullOrWhiteSpace(envelope.Type)
            || envelope.Type.Length > 64
            || envelope.Payload.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new InvalidDataException("Invalid envelope.");
    }
}
