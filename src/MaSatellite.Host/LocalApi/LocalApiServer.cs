using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using MaSatellite.Core.Protocol;
using MaSatellite.Core.Session;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Host.LocalApi;

/// <summary>
/// Loopback-only HTTP/WebSocket API for desktop widgets (Lively, Rainmeter-style tools, browsers).
/// Requests from web pages on other origins and requests with a foreign Host header (DNS rebinding) are refused.
/// </summary>
public sealed class LocalApiServer : IAsyncDisposable
{
    internal static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower) },
    };

    private readonly ILocalApiSource _source;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly object _clientsGate = new();
    private readonly List<EventClient> _clients = [];
    private Task? _acceptLoop;
    private Task? _ticker;

    public LocalApiServer(ILocalApiSource source, int port, ILogger? logger = null)
    {
        _source = source;
        _port = port;
        _logger = logger ?? NullLogger.Instance;
    }

    public int Port => _port;

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{_port}/");
        _listener.Start();
        _source.StatusChanged += OnStatusChanged;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _ticker = Task.Run(TickLoopAsync);
        _logger.LogInformation("Local API listening on http://127.0.0.1:{Port}/", _port);
    }

    public async ValueTask DisposeAsync()
    {
        _source.StatusChanged -= OnStatusChanged;
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Close();
        EventClient[] clients;
        lock (_clientsGate)
        {
            clients = [.. _clients];
            _clients.Clear();
        }

        foreach (var c in clients)
        {
            c.Socket.Abort();
        }

        foreach (var task in new[] { _acceptLoop, _ticker })
        {
            if (task is null)
            {
                continue;
            }

            try
            {
                await task.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or HttpListenerException or ObjectDisposedException)
            {
            }
        }

        _cts.Dispose();
    }

    internal static bool IsAllowedOrigin(string? origin)
    {
        if (string.IsNullOrEmpty(origin))
        {
            return true; // not a browser cross-origin request (curl, native widget hosts)
        }

        if (origin is "null" || origin.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
        {
            return true; // local HTML files, as used by Lively wallpapers
        }

        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            return false;
        }

        return uri.Host is "localhost" or "127.0.0.1" or "[::1]" or "::1";
    }

    internal static bool IsAllowedHost(string? host, int port)
    {
        if (string.IsNullOrEmpty(host))
        {
            return false;
        }

        return host.Equals($"127.0.0.1:{port}", StringComparison.OrdinalIgnoreCase)
            || host.Equals($"localhost:{port}", StringComparison.OrdinalIgnoreCase);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is HttpListenerException or ObjectDisposedException or InvalidOperationException)
            {
                return;
            }

            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        try
        {
            if (!IsAllowedHost(request.Headers["Host"], _port))
            {
                await WriteTextAsync(response, 403, "Forbidden host").ConfigureAwait(false);
                return;
            }

            var origin = request.Headers["Origin"];
            if (!IsAllowedOrigin(origin))
            {
                await WriteTextAsync(response, 403, "Forbidden origin").ConfigureAwait(false);
                return;
            }

            if (!string.IsNullOrEmpty(origin))
            {
                response.Headers["Access-Control-Allow-Origin"] = origin;
                response.Headers["Vary"] = "Origin";
            }

            var path = request.Url?.AbsolutePath ?? "/";
            if (request.HttpMethod == "OPTIONS")
            {
                response.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                response.Headers["Access-Control-Allow-Headers"] = "Content-Type";
                response.Headers["Access-Control-Max-Age"] = "600";
                response.StatusCode = 204;
                response.Close();
                return;
            }

            switch (path)
            {
                case "/api/health" when request.HttpMethod == "GET":
                    await WriteJsonAsync(response, new { status = "ok" }).ConfigureAwait(false);
                    break;
                case "/api/now-playing" when request.HttpMethod == "GET":
                    await WriteJsonAsync(response, SnapshotDto()).ConfigureAwait(false);
                    break;
                case "/api/artwork" when request.HttpMethod == "GET":
                    await WriteArtworkAsync(response).ConfigureAwait(false);
                    break;
                case "/api/command" when request.HttpMethod == "POST":
                    await HandleCommandAsync(request, response).ConfigureAwait(false);
                    break;
                case "/api/events" when request.IsWebSocketRequest:
                    await HandleEventsAsync(context).ConfigureAwait(false);
                    break;
                default:
                    await WriteTextAsync(response, 404, "Not found").ConfigureAwait(false);
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpListenerException or IOException or ObjectDisposedException or OperationCanceledException)
        {
            // client went away
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Local API request failed");
            try
            {
                response.Abort();
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private NowPlayingDto SnapshotDto() =>
        NowPlayingMapper.Map(_source.Status, _source.ServerNowMicros, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());

    private async Task WriteArtworkAsync(HttpListenerResponse response)
    {
        var art = _source.Status.Session.Artwork;
        if (art is null)
        {
            await WriteTextAsync(response, 404, "No artwork").ConfigureAwait(false);
            return;
        }

        response.StatusCode = 200;
        response.ContentType = art.Format switch { "png" => "image/png", "bmp" => "image/bmp", _ => "image/jpeg" };
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = art.Data.Length;
        await response.OutputStream.WriteAsync(art.Data).ConfigureAwait(false);
        response.Close();
    }

    private async Task HandleCommandAsync(HttpListenerRequest request, HttpListenerResponse response)
    {
        // Requiring JSON forces browsers to preflight cross-origin calls, which our Origin check then refuses.
        if (request.ContentType?.StartsWith("application/json", StringComparison.OrdinalIgnoreCase) != true)
        {
            await WriteTextAsync(response, 415, "Content-Type must be application/json").ConfigureAwait(false);
            return;
        }

        ApiCommand? command;
        try
        {
            command = await JsonSerializer.DeserializeAsync<ApiCommand>(request.InputStream, Json, _cts.Token).ConfigureAwait(false);
        }
        catch (JsonException)
        {
            command = null;
        }

        if (command is null || string.IsNullOrWhiteSpace(command.Command))
        {
            await WriteTextAsync(response, 400, "Invalid command").ConfigureAwait(false);
            return;
        }

        switch (command.Command)
        {
            case "volume" when command.Volume is { } v:
                _source.Volume.SetVolume(v);
                break;
            case "mute" when command.Mute is { } m:
                _source.Volume.SetMuted(m);
                break;
            case "play" or "pause" or "stop" or "next" or "previous" or "seek" or "seek_relative" or "shuffle" or "unshuffle" or "repeat_off" or "repeat_one" or "repeat_all":
                await _source.SendCommandAsync(new ControllerCommand(command.Command, null, null, command.PositionMs, command.OffsetMs)).ConfigureAwait(false);
                break;
            default:
                await WriteTextAsync(response, 400, "Unknown command").ConfigureAwait(false);
                return;
        }

        await WriteJsonAsync(response, new { status = "ok" }).ConfigureAwait(false);
    }

    private async Task HandleEventsAsync(HttpListenerContext context)
    {
        WebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(null, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            return;
        }

        var client = new EventClient(wsContext.WebSocket);
        lock (_clientsGate)
        {
            _clients.Add(client);
        }

        try
        {
            await client.SendAsync(Serialize("state"), _cts.Token).ConfigureAwait(false);

            // We only push; read until the client closes so we notice disconnects.
            var buffer = new byte[256];
            while (client.Socket.State == WebSocketState.Open && !_cts.IsCancellationRequested)
            {
                var result = await client.Socket.ReceiveAsync(buffer, _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    break;
                }
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            lock (_clientsGate)
            {
                _clients.Remove(client);
            }

            client.Socket.Dispose();
            client.Dispose();
        }
    }

    private string Serialize(string type)
    {
        var dto = SnapshotDto();
        object data = type == "progress"
            ? new { dto.PositionMs, dto.PositionUpdatedAt, dto.DurationMs, dto.Playback, dto.PlaybackSpeed }
            : dto;
        return JsonSerializer.Serialize(new ApiEvent(type, data), Json);
    }

    private void OnStatusChanged(SatelliteStatus status) => Broadcast("state");

    private async Task TickLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        try
        {
            while (await timer.WaitForNextTickAsync(_cts.Token).ConfigureAwait(false))
            {
                if (_source.Status.Session.Group.PlaybackState == GroupPlaybackState.Playing)
                {
                    Broadcast("progress");
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void Broadcast(string type)
    {
        EventClient[] clients;
        lock (_clientsGate)
        {
            if (_clients.Count == 0)
            {
                return;
            }

            clients = [.. _clients];
        }

        var message = Serialize(type);
        foreach (var client in clients)
        {
            _ = client.SendAsync(message, _cts.Token);
        }
    }

    private static async Task WriteJsonAsync(HttpListenerResponse response, object value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        response.StatusCode = 200;
        response.ContentType = "application/json; charset=utf-8";
        response.Headers["Cache-Control"] = "no-store";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private static async Task WriteTextAsync(HttpListenerResponse response, int status, string text)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        response.StatusCode = status;
        response.ContentType = "text/plain; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    private sealed class EventClient : IDisposable
    {
        private readonly SemaphoreSlim _sendLock = new(1, 1);

        public EventClient(WebSocket socket)
        {
            Socket = socket;
        }

        public WebSocket Socket { get; }

        public void Dispose() => _sendLock.Dispose();

        public async Task SendAsync(string message, CancellationToken ct)
        {
            if (Socket.State != WebSocketState.Open)
            {
                return;
            }

            try
            {
                await _sendLock.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    await Socket.SendAsync(Encoding.UTF8.GetBytes(message), WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
                }
                finally
                {
                    _sendLock.Release();
                }
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException or InvalidOperationException)
            {
                // the receive loop notices and removes the client
            }
        }
    }
}
