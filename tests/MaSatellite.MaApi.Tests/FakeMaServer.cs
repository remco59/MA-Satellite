using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace MaSatellite.MaApi.Tests;

/// <summary>A tiny stand-in for Music Assistant's <c>/ws</c> endpoint.</summary>
internal sealed class FakeMaServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private WebSocket? _socket;

    public FakeMaServer(int schemaVersion = 30, string? requiredToken = "secret")
    {
        SchemaVersion = schemaVersion;
        RequiredToken = requiredToken;
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        Port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _loop = Task.Run(AcceptAsync);
    }

    public int Port { get; }

    public int SchemaVersion { get; }

    public string? RequiredToken { get; }

    /// <summary>Command name → handler returning the result JSON (or throwing <see cref="FakeError"/>).</summary>
    public Dictionary<string, Func<JsonElement, object?>> Handlers { get; } = [];

    public List<(string Command, JsonElement Args)> Received { get; } = [];

    /// <summary>When set for a command, the result array is split into chunks of this size sent as partial messages.</summary>
    public Dictionary<string, int> ChunkSizes { get; } = [];

    public sealed class FakeError(int code, string details) : Exception(details)
    {
        public int Code { get; } = code;
    }

    public async Task SendEventAsync(string name, string? objectId, object data) =>
        await SendAsync(new { @event = name, object_id = objectId, data });

    public async Task CloseSocketAsync()
    {
        if (_socket is { State: WebSocketState.Open })
        {
            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Close();
        _socket?.Abort();
        try
        {
            await _loop;
        }
        catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or OperationCanceledException)
        {
        }
    }

    private async Task AcceptAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync();
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException)
            {
                return;
            }

            if (!ctx.Request.IsWebSocketRequest || ctx.Request.Url!.AbsolutePath != "/ws")
            {
                ctx.Response.StatusCode = 404;
                ctx.Response.Close();
                continue;
            }

            var ws = (await ctx.AcceptWebSocketAsync(null)).WebSocket;
            _socket = ws;
            _ = Task.Run(() => ServeAsync(ws));
        }
    }

    private async Task ServeAsync(WebSocket ws)
    {
        await SendAsync(new { server_id = "fake", server_version = "2.10.8", schema_version = SchemaVersion, min_supported_schema_version = 24, name = "Fake MA" });
        var authed = SchemaVersion < 28;
        var buffer = new byte[64 * 1024];
        try
        {
            while (ws.State == WebSocketState.Open)
            {
                var result = await ws.ReceiveAsync(buffer, _cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    return;
                }

                using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(buffer, 0, result.Count));
                var root = doc.RootElement;
                var id = root.GetProperty("message_id").GetString()!;
                var command = root.GetProperty("command").GetString()!;
                var args = root.TryGetProperty("args", out var a) ? a.Clone() : default;
                lock (Received)
                {
                    Received.Add((command, args));
                }

                if (command == "auth")
                {
                    var ok = args.GetProperty("token").GetString() == RequiredToken;
                    authed = ok;
                    await (ok ? SendAsync(new { message_id = id, result = new { user_id = "u1" } }) : SendAsync(new { message_id = id, error_code = 20, details = "Invalid token" }));
                    continue;
                }

                if (!authed)
                {
                    await SendAsync(new { message_id = id, error_code = 20, details = "Authentication required" });
                    continue;
                }

                if (!Handlers.TryGetValue(command, out var handler))
                {
                    await SendAsync(new { message_id = id, error_code = 2, details = "Command not found" });
                    continue;
                }

                object? value;
                try
                {
                    value = handler(args);
                }
                catch (FakeError e)
                {
                    await SendAsync(new { message_id = id, error_code = e.Code, details = e.Message });
                    continue;
                }

                if (ChunkSizes.TryGetValue(command, out var size) && value is System.Collections.IEnumerable list)
                {
                    var items = list.Cast<object>().ToList();
                    for (var i = 0; i < items.Count; i += size)
                    {
                        var last = i + size >= items.Count;
                        await SendAsync(new { message_id = id, result = items.Skip(i).Take(size).ToList(), partial = !last });
                    }
                }
                else
                {
                    await SendAsync(new { message_id = id, result = value });
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
    }

    private async Task SendAsync(object message)
    {
        var ws = _socket;
        if (ws is null || ws.State != WebSocketState.Open)
        {
            return;
        }

        await ws.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(message)), WebSocketMessageType.Text, true, CancellationToken.None);
    }
}
