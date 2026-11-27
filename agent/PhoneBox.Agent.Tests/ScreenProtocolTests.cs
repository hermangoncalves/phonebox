using System.Text;
using System.Text.Json;
using PhoneBox.Agent;
using Xunit;

public sealed class ScreenProtocolTests
{
    private static MemoryStream Bytes(params byte[] b) => new(b);

    [Fact]
    public async Task TruncatedHeaderIsNotCleanEof()
    {
        using var input = new MemoryStream(new byte[] { 2, 0, 0 });
        await Assert.ThrowsAsync<InvalidDataException>(() => ScreenProtocol.ReadAsync(input, default));
    }

    [Fact]
    public async Task CleanEofReturnsNull()
    {
        using var input = new MemoryStream(Array.Empty<byte>());
        var frame = await ScreenProtocol.ReadAsync(input, default);
        Assert.Null(frame);
    }

    [Fact]
    public async Task UnknownKindThrows()
    {
        // kind 99, length 1, one payload byte
        using var input = new MemoryStream(new byte[] { 99, 0, 0, 0, 1, 0 });
        await Assert.ThrowsAsync<InvalidDataException>(() => ScreenProtocol.ReadAsync(input, default));
    }

    [Fact]
    public async Task OversizedLengthThrowsBeforeAllocation()
    {
        // kind 1 (JSON), length = MaxJson + 1
        var len = ScreenProtocol.MaxJson + 1;
        var header = new byte[] { 1, (byte)((len >> 24) & 0xFF), (byte)((len >> 16) & 0xFF), (byte)((len >> 8) & 0xFF), (byte)(len & 0xFF) };
        using var input = new MemoryStream(header);
        await Assert.ThrowsAsync<InvalidDataException>(() => ScreenProtocol.ReadAsync(input, default));
    }

    [Fact]
    public async Task ByteByByteReadsWork()
    {
        // Build a frame: kind=1, length=3, payload "abc", delivered one byte at a time via a slow stream.
        var raw = new byte[] { 1, 0, 0, 0, 3, (byte)'a', (byte)'b', (byte)'c' };
        using var input = new OneByteStream(raw);
        var frame = await ScreenProtocol.ReadAsync(input, default);
        Assert.NotNull(frame);
        Assert.Equal(1, frame!.Kind);
        Assert.Equal("abc", Encoding.UTF8.GetString(frame.Payload));
    }

    [Fact]
    public async Task TruncatedPayloadThrows()
    {
        // kind=2, length=10, but only 3 payload bytes
        var raw = new byte[] { 2, 0, 0, 0, 10, 1, 2, 3 };
        using var input = new MemoryStream(raw);
        await Assert.ThrowsAsync<InvalidDataException>(() => ScreenProtocol.ReadAsync(input, default));
    }

    [Fact]
    public async Task CancellationThrows()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        using var input = new SlowStream();
        await Assert.ThrowsAsync<OperationCanceledException>(() => ScreenProtocol.ReadAsync(input, cts.Token));
    }

    [Fact]
    public void VideoRoundTripsEpochAndSequenceInNetworkByteOrder()
    {
        var mp4 = new byte[] { 0x10, 0x20, 0x30 };
        var buf = ScreenProtocol.Video(0x01020304, 0x05060708, mp4);
        Assert.Equal(11, buf.Length);
        Assert.Equal(0x01, buf[0]); Assert.Equal(0x02, buf[1]); Assert.Equal(0x03, buf[2]); Assert.Equal(0x04, buf[3]);
        Assert.Equal(0x05, buf[4]); Assert.Equal(0x06, buf[5]); Assert.Equal(0x07, buf[6]); Assert.Equal(0x08, buf[7]);
        Assert.Equal(mp4, buf[8..]);
    }

    [Fact]
    public void VideoRejectsOversizedSlice()
    {
        var big = new byte[ScreenProtocol.MaxVideo - 7];
        Assert.Throws<ArgumentOutOfRangeException>(() => ScreenProtocol.Video(1, 1, big));
    }

    [Fact]
    public async Task WriteThenReadRoundTrips()
    {
        var original = new ScreenFrame(1, Encoding.UTF8.GetBytes("hello"));
        using var ms = new MemoryStream();
        await ScreenProtocol.WriteAsync(ms, original, default);
        ms.Position = 0;
        var read = await ScreenProtocol.ReadAsync(ms, default);
        Assert.NotNull(read);
        Assert.Equal(original.Kind, read!.Kind);
        Assert.Equal(original.Payload, read.Payload);
    }

    [Fact]
    public async Task WriteRejectsOversizedPayload()
    {
        var frame = new ScreenFrame(1, new byte[ScreenProtocol.MaxJson + 1]);
        using var ms = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => ScreenProtocol.WriteAsync(ms, frame, default));
    }

    [Fact]
    public void SerializeEventRejectsUnknownFieldWithSecret()
    {
        var fields = new Dictionary<string, object?>
        {
            ["width"] = 1920,
            ["height"] = 1080,
            ["codec"] = "avc1.42C028",
            ["inputAvailable"] = true,
            ["token"] = "s3cret-token-do-not-leak",
        };
        var ex = Assert.Throws<ArgumentException>(() => ScreenProtocol.SerializeEvent("ready", fields));
        Assert.Contains("token", ex.Message);
    }

    [Fact]
    public void SerializeEventOmitsNullsAndKeepsWhitelist()
    {
        var fields = new Dictionary<string, object?>
        {
            ["width"] = 1920,
            ["height"] = 1080,
            ["codec"] = "avc1.42C028",
            ["inputAvailable"] = null,
        };
        var json = ScreenProtocol.SerializeEventJson("ready", fields);
        Assert.Contains("\"type\":\"ready\"", json);
        Assert.Contains("\"width\":1920", json);
        Assert.DoesNotContain("inputAvailable", json);
        Assert.DoesNotContain("token", json);
    }

    [Fact]
    public void TryParseOperationRejectsUnknownField()
    {
        var json = Encoding.UTF8.GetBytes("{\"op\":\"pointer\",\"x\":0.5,\"token\":\"leak\"}");
        Assert.False(ScreenProtocol.TryParseOperation(json, out _, out _));
    }

    [Fact]
    public void TryParseOperationAcceptsKnownOperation()
    {
        var json = Encoding.UTF8.GetBytes("{\"op\":\"pointer\",\"x\":0.5,\"y\":0.25}");
        Assert.True(ScreenProtocol.TryParseOperation(json, out var op, out var fields));
        Assert.Equal("pointer", op);
        Assert.Equal(2, fields.Count);
    }

    [Fact]
    public void FixtureHasValidBoxesAndCodec()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "Fixtures");
        var init = File.ReadAllBytes(Path.Combine(dir, "screen-init.mp4"));
        var media = File.ReadAllBytes(Path.Combine(dir, "screen-media.m4s"));
        Assert.True(init.Length > 0);
        Assert.True(media.Length > 0);
        // init starts with ftyp
        Assert.Equal((byte)'f', init[4]); Assert.Equal((byte)'t', init[5]);
        Assert.Equal((byte)'y', init[6]); Assert.Equal((byte)'p', init[7]);
        // media starts with moof
        Assert.Equal((byte)'m', media[4]); Assert.Equal((byte)'o', media[5]);
        Assert.Equal((byte)'o', media[6]); Assert.Equal((byte)'f', media[7]);
        var json = File.ReadAllText(Path.Combine(dir, "screen-fixture.json"));
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("avc1.42C028", doc.RootElement.GetProperty("codec").GetString());
    }

    // helpers
    private sealed class OneByteStream : Stream
    {
        private readonly byte[] _data;
        private int _pos;
        public OneByteStream(byte[] data) { _data = data; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _pos; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_pos >= _data.Length) return 0;
            buffer[offset] = _data[_pos++];
            return 1;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
            => Task.FromResult(Read(buffer, offset, count));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            if (_pos >= _data.Length) return ValueTask.FromResult(0);
            buffer.Span[0] = _data[_pos++];
            return ValueTask.FromResult(1);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class SlowStream : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => 0;
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) { Thread.Sleep(Timeout.Infinite); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct)
        {
            var tcs = new TaskCompletionSource<int>();
            ct.Register(() => tcs.TrySetCanceled(ct));
            return tcs.Task;
        }
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var tcs = new TaskCompletionSource<int>();
            ct.Register(() => tcs.TrySetCanceled(ct));
            return new ValueTask<int>(tcs.Task);
        }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
