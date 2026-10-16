using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using PcRemote.Protocol;

int passed = 0;
async Task Check(string name, Func<Task> test)
{
    await test();
    Console.WriteLine($"PASS {name}");
    passed++;
}
async Task Reject<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
MemoryStream Raw(string json)
{
    byte[] body = Encoding.UTF8.GetBytes(json);
    byte[] data = new byte[body.Length + 4];
    BinaryPrimitives.WriteInt32BigEndian(data, body.Length);
    body.CopyTo(data, 4);
    return new MemoryStream(data);
}
await Check("round trip and big endian", async () =>
{
    var message = new Envelope(1, 0, Guid.NewGuid(), "Hello", JsonSerializer.SerializeToElement(new { name = "电脑" }));
    using var stream = new MemoryStream();
    await FrameCodec.WriteAsync(stream, message);
    if (BinaryPrimitives.ReadInt32BigEndian(stream.ToArray()) != stream.Length - 4) throw new Exception("Bad header");
    stream.Position = 0;
    var decoded = await FrameCodec.ReadAsync(stream);
    if (decoded.RequestId != message.RequestId || decoded.Payload.GetProperty("name").GetString() != "电脑") throw new Exception("Bad payload");
});
foreach (int size in new[] { 0, -1, FrameCodec.MaxPayloadBytes + 1 })
    await Check($"reject length {size}", async () =>
    {
        byte[] header = new byte[4];
        BinaryPrimitives.WriteInt32BigEndian(header, size);
        await Reject<InvalidDataException>(async () => await FrameCodec.ReadAsync(new MemoryStream(header)));
    });
await Check("truncated header", () => Reject<EndOfStreamException>(async () => await FrameCodec.ReadAsync(new MemoryStream(new byte[3]))));
await Check("truncated body", () => Reject<EndOfStreamException>(async () => await FrameCodec.ReadAsync(new MemoryStream(new byte[] { 0, 0, 0, 2, 123 }))));
await Check("invalid JSON", () => Reject<JsonException>(async () => await FrameCodec.ReadAsync(Raw("{"))));
await Check("missing fields", () => Reject<JsonException>(async () => await FrameCodec.ReadAsync(Raw("{}"))));
await Check("null envelope", () => Reject<InvalidDataException>(async () => await FrameCodec.ReadAsync(Raw("null"))));
foreach (var version in new[] { (2, 0), (1, 1) })
    await Check($"reject version {version}", () => Reject<InvalidDataException>(() => FrameCodec.WriteAsync(new MemoryStream(),
        new Envelope(version.Item1, version.Item2, Guid.NewGuid(), "Hello", JsonSerializer.SerializeToElement(new { })))));
await Check("invalid identity", () => Reject<InvalidDataException>(() => FrameCodec.WriteAsync(new MemoryStream(),
    new Envelope(1, 0, Guid.Empty, "Hello", JsonSerializer.SerializeToElement(new { })))));
await Check("cancel read", async () =>
{
    using var cts = new CancellationTokenSource();
    cts.Cancel();
    await Reject<OperationCanceledException>(async () => await FrameCodec.ReadAsync(new MemoryStream(new byte[4]), cts.Token));
});
await Check("oversized write", () => Reject<InvalidDataException>(() => FrameCodec.WriteAsync(new MemoryStream(),
    new Envelope(1, 0, Guid.NewGuid(), "Hello", JsonSerializer.SerializeToElement(new string('x', FrameCodec.MaxPayloadBytes))))));
await DiscoveryTests.RunAsync(Check);
Console.WriteLine($"{passed} checks passed.");
