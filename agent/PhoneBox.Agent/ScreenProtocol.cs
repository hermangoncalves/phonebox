using System.Buffers.Binary;
using System.Text;
using System.Text.Json;

namespace PhoneBox.Agent;

internal sealed record ScreenFrame(byte Kind, byte[] Payload);

internal static class ScreenProtocol
{
    public const byte KindJson = 1;
    public const byte KindVideo = 2;
    public const int MaxJson = 64 * 1024;
    public const int MaxVideo = 256 * 1024;

    private static readonly Dictionary<string, string[]> EventFields = new(StringComparer.Ordinal)
    {
        { "ready", new[] { "width", "height", "codec", "inputAvailable" } },
        { "media", new[] { "epoch" } },
        { "error", new[] { "code" } },
        { "stats", new[] { "fps", "bitrate" } },
    };

    private static readonly HashSet<string> KnownEvents = new(EventFields.Keys, StringComparer.Ordinal);

    private static readonly Dictionary<string, string[]> OperationFields = new(StringComparer.Ordinal)
    {
        { "heartbeat", Array.Empty<string>() },
        { "pointer", new[] { "x", "y" } },
        { "button", new[] { "n", "down" } },
        { "scroll", new[] { "dx", "dy" } },
        { "releaseAll", Array.Empty<string>() },
        { "chooseMonitor", Array.Empty<string>() },
        { "recover", new[] { "viewer" } },
    };

    private static readonly HashSet<string> KnownOperations = new(OperationFields.Keys, StringComparer.Ordinal);

    public static IReadOnlySet<string> Events => KnownEvents;
    public static IReadOnlySet<string> Operations => KnownOperations;

    public static async Task<ScreenFrame?> ReadAsync(Stream input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        try
        {
            var kindBuf = new byte[1];
            var first = await input.ReadAsync(kindBuf.AsMemory(), ct);
            if (first == 0) return null;
            if (first != 1) throw new InvalidDataException("truncated frame header");

            var kind = kindBuf[0];
            var max = kind switch
            {
                KindJson => MaxJson,
                KindVideo => MaxVideo,
                _ => 0,
            };
            if (max == 0) throw new InvalidDataException($"unknown frame kind {kind}");

            var lenBuf = new byte[4];
            await ReadExactAsync(input, lenBuf, ct);
            var length = BinaryPrimitives.ReadUInt32BigEndian(lenBuf);
            if (length > (uint)max) throw new InvalidDataException($"frame length {length} exceeds {max}");

            var payload = length == 0 ? Array.Empty<byte>() : new byte[length];
            if (length > 0) await ReadExactAsync(input, payload, ct);
            return new ScreenFrame(kind, payload);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw new OperationCanceledException(ct);
        }
    }

    public static async Task WriteAsync(Stream output, ScreenFrame frame, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.Payload is null) throw new ArgumentNullException(nameof(frame));

        var max = frame.Kind switch
        {
            KindJson => MaxJson,
            KindVideo => MaxVideo,
            _ => 0,
        };
        if (max == 0) throw new InvalidDataException($"unknown frame kind {frame.Kind}");
        if (frame.Payload.Length > max) throw new InvalidDataException($"frame payload {frame.Payload.Length} exceeds {max}");

        var header = new byte[5];
        header[0] = frame.Kind;
        BinaryPrimitives.WriteUInt32BigEndian(header.AsSpan(1), (uint)frame.Payload.Length);
        await output.WriteAsync(header.AsMemory(), ct);
        if (frame.Payload.Length > 0) await output.WriteAsync(frame.Payload.AsMemory(), ct);
        await output.FlushAsync(ct);
    }

    public static byte[] Video(uint epoch, uint sequence, ReadOnlySpan<byte> mp4)
    {
        if (mp4.Length > MaxVideo - 8)
            throw new ArgumentOutOfRangeException(nameof(mp4),
                $"mp4 slice of {mp4.Length} bytes leaves no room for the 8-byte header (max {MaxVideo - 8})");

        var buf = new byte[8 + mp4.Length];
        BinaryPrimitives.WriteUInt32BigEndian(buf, epoch);
        BinaryPrimitives.WriteUInt32BigEndian(buf.AsSpan(4), sequence);
        mp4.CopyTo(buf.AsSpan(8));
        return buf;
    }

    public static byte[] SerializeEvent(string type, IReadOnlyDictionary<string, object?> fields)
    {
        if (string.IsNullOrEmpty(type)) throw new ArgumentException("event type is required", nameof(type));
        if (!EventFields.TryGetValue(type, out var allowed))
            throw new ArgumentException($"unknown event type '{type}'", nameof(type));
        ArgumentNullException.ThrowIfNull(fields);

        var allowedSet = new HashSet<string>(allowed, StringComparer.Ordinal);
        foreach (var key in fields.Keys)
        {
            if (!allowedSet.Contains(key))
                throw new ArgumentException($"field '{key}' is not allowed for event '{type}'");
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            writer.WriteString("type", type);
            foreach (var key in allowed)
            {
                if (!fields.TryGetValue(key, out var value) || value is null) continue;
                writer.WritePropertyName(key);
                JsonSerializer.Serialize(writer, value, value.GetType());
            }
            writer.WriteEndObject();
        }
        return stream.ToArray();
    }

    public static string SerializeEventJson(string type, IReadOnlyDictionary<string, object?> fields)
        => Encoding.UTF8.GetString(SerializeEvent(type, fields));

    public static bool TrySerializeEvent(string type, IReadOnlyDictionary<string, object?> fields, out byte[]? json)
    {
        try { json = SerializeEvent(type, fields); return true; }
        catch { json = null; return false; }
    }

    public static bool TryParseOperation(ReadOnlySpan<byte> json, out string operation, out Dictionary<string, JsonElement> fields)
    {
        operation = "";
        fields = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        try
        {
            using var doc = JsonDocument.Parse(json.ToArray());
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            if (!doc.RootElement.TryGetProperty("op", out var opEl) || opEl.ValueKind != JsonValueKind.String) return false;
            operation = opEl.GetString() ?? "";
            if (!KnownOperations.Contains(operation)) return false;

            var allowed = new HashSet<string>(OperationFields[operation], StringComparer.Ordinal);
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Name == "op") continue;
                if (!allowed.Contains(prop.Name)) return false;
                fields[prop.Name] = prop.Value.Clone();
            }
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static (string Operation, Dictionary<string, JsonElement> Fields) ParseOperation(ReadOnlySpan<byte> json)
    {
        if (!TryParseOperation(json, out var op, out var fields))
            throw new InvalidDataException("invalid or unknown operation");
        return (op, fields);
    }

    public static byte[] Serialize(string type, IReadOnlyDictionary<string, object?> fields) => SerializeEvent(type, fields);
    public static byte[] ToJson(string type, IReadOnlyDictionary<string, object?> fields) => SerializeEvent(type, fields);

    private static async Task ReadExactAsync(Stream input, byte[] buffer, CancellationToken ct)
    {
        int offset = 0;
        while (offset < buffer.Length)
        {
            var read = await input.ReadAsync(buffer.AsMemory(offset), ct);
            if (read == 0) throw new InvalidDataException("truncated frame");
            offset += read;
        }
    }
}
