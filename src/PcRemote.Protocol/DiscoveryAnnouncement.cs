using System.Text.Json;
using System.Text.Json.Serialization;

namespace PcRemote.Protocol;

public sealed record DiscoveryAnnouncement(string Magic, int Major, int Minor, Guid InstanceId, string Name, Guid ScanId, string Kind);

public static class DiscoveryCodec
{
    public const int Port = 45831;
    public const int MaxDatagramBytes = 512;
    public const string Magic = "pc-remote-lan";
    public const int Major = 2;
    public const int Minor = 0;
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 4
    };

    public static byte[] Encode(Guid instanceId, string name, Guid? scanId = null)
    {
        var announcement = new DiscoveryAnnouncement(Magic, Major, Minor, instanceId, name, scanId ?? instanceId, "host");
        Validate(announcement);
        byte[] data = JsonSerializer.SerializeToUtf8Bytes(announcement, Options);
        if (data.Length > MaxDatagramBytes) throw new InvalidDataException("Discovery packet is too large.");
        return data;
    }

    public static byte[] EncodeProbe(Guid scanId)
    {
        if (scanId == Guid.Empty) throw new ArgumentException("Missing scan identity.", nameof(scanId));
        return JsonSerializer.SerializeToUtf8Bytes(new DiscoveryAnnouncement(Magic, Major, Minor, Guid.Empty, "", scanId, "probe"), Options);
    }

    public static DiscoveryAnnouncement Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length is 0 or > MaxDatagramBytes) throw new InvalidDataException("Invalid discovery packet length.");
        // Reject duplicate keys rather than accepting an ambiguous last value.
        using var document = JsonDocument.Parse(data.ToArray(), new JsonDocumentOptions { MaxDepth = 4 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Expected discovery object.");
        var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!keys.Add(property.Name)) throw new InvalidDataException("Duplicate discovery field.");
        var announcement = JsonSerializer.Deserialize<DiscoveryAnnouncement>(data, Options)
            ?? throw new InvalidDataException("Missing discovery announcement.");
        Validate(announcement);
        return announcement;
    }

    private static void Validate(DiscoveryAnnouncement announcement)
    {
        if (announcement.Magic != Magic || announcement.Major != Major || announcement.Minor != Minor
            || announcement.ScanId == Guid.Empty)
            throw new InvalidDataException("Invalid or unsupported discovery announcement.");
        bool validHost = announcement.Kind == "host" && announcement.InstanceId != Guid.Empty
            && !string.IsNullOrWhiteSpace(announcement.Name) && announcement.Name.Length <= 64
            && !announcement.Name.Any(char.IsControl);
        bool validProbe = announcement.Kind == "probe" && announcement.InstanceId == Guid.Empty && announcement.Name == "";
        if (!validHost && !validProbe) throw new InvalidDataException("Invalid discovery message.");
    }
}
