using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using MaSatellite.Core.Time;
using MaSatellite.Core.Transport;

namespace MaSatellite.Core.Tests.Support;

/// <summary>A minimal in-process Sendspin server (legacy plaintext protocol) for end-to-end tests.</summary>
internal sealed class FakeServer
{
    public const long Offset = 5_000_000_000; // server clock = local clock + 5000 s

    private readonly ILocalClock _clock;
    private readonly List<(string Type, JsonElement Payload)> _received = [];
    private WebSocketTransport? _transport;

    /// <summary>When set, the server answers the client's hello with this message type instead of server/hello.</summary>
    public string? RejectWith { get; init; }

    public FakeServer(ILocalClock clock, string serverId = "fake-server", string name = "Fake Music Assistant", string reason = "playback")
    {
        _clock = clock;
        ServerId = serverId;
        Name = name;
        Reason = reason;
    }

    public string ServerId { get; }

    public string Name { get; }

    public string Reason { get; }

    public long ServerNow => _clock.NowMicros + Offset;

    public bool Closed { get; private set; }

    public IReadOnlyList<(string Type, JsonElement Payload)> Received
    {
        get
        {
            lock (_received)
            {
                return [.. _received];
            }
        }
    }

    public async Task RunAsync(ISendspinTransport transport)
    {
        var ws = (WebSocketTransport)transport;
        _transport = ws;
        try
        {
            await foreach (var msg in ws.ReceiveAsync(CancellationToken.None))
            {
                if (!msg.IsText)
                {
                    continue;
                }

                using var doc = JsonDocument.Parse(msg.Text!);
                var type = doc.RootElement.GetProperty("type").GetString()!;
                var payload = doc.RootElement.GetProperty("payload").Clone();
                lock (_received)
                {
                    _received.Add((type, payload));
                }

                switch (type)
                {
                    case "client/hello" when RejectWith is not null:
                        await SendAsync(RejectWith, new { reason = "malformed" });
                        break;
                    case "client/hello":
                        await SendAsync("server/hello", new
                        {
                            server_id = ServerId,
                            name = Name,
                            version = 1,
                            active_roles = new[] { "player@v1", "controller@v1", "metadata@v1", "artwork@v1" },
                            connection_reason = Reason,
                        });
                        break;
                    case "client/time":
                        var received = ServerNow;
                        await SendAsync("server/time", new
                        {
                            client_transmitted = payload.GetProperty("client_transmitted").GetInt64(),
                            server_received = received,
                            server_transmitted = received + 20,
                        });
                        break;
                    case "client/goodbye":
                        Closed = true;
                        return;
                }
            }
        }
        finally
        {
            Closed = true;
        }
    }

    public Task SendAsync(string type, object payload) =>
        _transport!.SendTextAsync(JsonSerializer.Serialize(new { type, payload }), CancellationToken.None);

    public Task SendBinaryAsync(byte type, long timestamp, ReadOnlySpan<byte> data)
    {
        var buf = new byte[9 + data.Length];
        buf[0] = type;
        BinaryPrimitives.WriteInt64BigEndian(buf.AsSpan(1, 8), timestamp);
        data.CopyTo(buf.AsSpan(9));
        return _transport!.SendBinaryAsync(buf, CancellationToken.None);
    }

    public Task CloseAsync() => _transport!.CloseAsync(CancellationToken.None);

    public IEnumerable<JsonElement> Payloads(string type) => Received.Where(r => r.Type == type).Select(r => r.Payload);

    public async Task<JsonElement> WaitForAsync(string type, Func<JsonElement, bool>? predicate = null, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            foreach (var p in Payloads(type))
            {
                if (predicate is null || predicate(p))
                {
                    return p;
                }
            }

            await Task.Delay(10);
        }

        throw new TimeoutException($"Server never received {type}");
    }

    /// <summary>Streams a sine as 16-bit PCM chunks of 20 ms, starting at <paramref name="startTs"/> (server µs), paced to stay ~<paramref name="aheadMs"/> ms ahead.</summary>
    public async Task StreamSineAsync(long startTs, double seconds, double freq = 440, int aheadMs = 600, int rate = 48000)
    {
        const int chunkFrames = 960;
        var total = (int)(seconds * rate);
        for (var f = 0; f < total; f += chunkFrames)
        {
            var bytes = new byte[chunkFrames * 4];
            for (var i = 0; i < chunkFrames; i++)
            {
                var v = (short)(Math.Sin(2 * Math.PI * freq * (f + i) / rate) * 12000);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4, 2), v);
                BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan((i * 4) + 2, 2), v);
            }

            var ts = startTs + (f * 1_000_000L / rate);
            while (ts - ServerNow > aheadMs * 1000L)
            {
                await Task.Delay(5);
            }

            await SendBinaryAsync(4, ts, bytes);
        }
    }

    public static string Utf8(string s) => Encoding.UTF8.GetString(Encoding.UTF8.GetBytes(s));
}
