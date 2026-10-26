using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using MaSatellite.MaApi.Models;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.MaApi;

public sealed record MaEvent(string Event, string? ObjectId, JsonElement Data);

/// <summary>
/// JSON command connection to Music Assistant's WebSocket API (<c>ws://host:8095/ws</c>): server info, token
/// authentication, request/response matching (including chunked results) and server events.
/// </summary>
public sealed class MaConnection : IAsyncDisposable
{
    /// <summary>From this schema version on the server requires an auth token.</summary>
    public const int AuthSchemaVersion = 28;

    private readonly Uri _uri;
    private readonly string? _token;
    private readonly ILogger _logger;
    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly ConcurrentDictionary<string, Pending> _pending = new();
    private readonly CancellationTokenSource _cts = new();
    private Task? _receiveLoop;
    private int _disposed;

    public MaConnection(Uri uri, string? token, ILogger? logger = null)
    {
        _uri = uri;
        _token = token;
        _logger = logger ?? NullLogger.Instance;
    }

    public MaServerInfo? ServerInfo { get; private set; }

    public bool IsConnected => _socket.State == WebSocketState.Open;

    public event Action<MaEvent>? EventReceived;

    /// <summary>Raised once when the connection ends (with the error, if any).</summary>
    public event Action<Exception?>? Disconnected;

    public static Uri BuildUri(string host, int port) => new UriBuilder("ws", host, port, "/ws").Uri;

    public async Task ConnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await _socket.ConnectAsync(_uri, timeout.Token).ConfigureAwait(false);
            using var first = await ReceiveDocumentAsync(timeout.Token).ConfigureAwait(false)
                ?? throw new MaException("The server closed the connection immediately.");
            ServerInfo = ParseServerInfo(first.RootElement);
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException)
        {
            throw new MaException($"Cannot connect to Music Assistant at {_uri}: {ex.Message}", ex);
        }

        _receiveLoop = Task.Run(ReceiveLoopAsync, CancellationToken.None);

        if (ServerInfo.SchemaVersion >= AuthSchemaVersion)
        {
            if (string.IsNullOrWhiteSpace(_token))
            {
                await DisposeAsync().ConfigureAwait(false);
                throw new MaAuthException("This Music Assistant server requires an access token. Create a long-lived token in Music Assistant and enter it in the settings.");
            }

            try
            {
                var ok = await SendCommandAsync("auth", new Dictionary<string, object?> { ["token"] = _token }, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (ok.ValueKind is JsonValueKind.Null or JsonValueKind.False || ok.ValueKind == JsonValueKind.Undefined)
                {
                    throw new MaAuthException("Music Assistant rejected the access token (invalid or expired).");
                }
            }
            catch (MaCommandException ex)
            {
                await DisposeAsync().ConfigureAwait(false);
                throw new MaAuthException($"Music Assistant rejected the access token: {ex.Message}");
            }
            catch (MaAuthException)
            {
                await DisposeAsync().ConfigureAwait(false);
                throw;
            }
        }
    }

    /// <summary>Sends a command and returns its <c>result</c>. Chunked (partial) results are concatenated.</summary>
    public async Task<JsonElement> SendCommandAsync(string command, IReadOnlyDictionary<string, object?>? args = null, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        if (!IsConnected)
        {
            throw new MaException("Not connected to Music Assistant.");
        }

        var id = Guid.NewGuid().ToString("N");
        var pending = new Pending(command);
        _pending[id] = pending;
        try
        {
            var message = new Dictionary<string, object?>
            {
                ["message_id"] = id,
                ["command"] = command,
                ["args"] = args is null ? new Dictionary<string, object?>() : args.Where(kv => kv.Value is not null).ToDictionary(kv => kv.Key, kv => kv.Value),
            };
            var bytes = JsonSerializer.SerializeToUtf8Bytes(message);
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            return await pending.Completion.Task.WaitAsync(timeout ?? TimeSpan.FromSeconds(30), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new MaException($"Music Assistant did not answer '{command}' in time.");
        }
        catch (WebSocketException ex)
        {
            throw new MaException("The connection to Music Assistant was lost.", ex);
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 1)
        {
            return;
        }

        await _cts.CancelAsync().ConfigureAwait(false);
        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var t = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, t.Token).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException)
        {
        }

        _socket.Abort();
        if (_receiveLoop is not null)
        {
            try
            {
                await _receiveLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or WebSocketException)
            {
            }
        }

        FailAll(new MaException("The connection was closed."));
        _socket.Dispose();
        _sendLock.Dispose();
        _cts.Dispose();
    }

    internal static MaServerInfo ParseServerInfo(JsonElement e) => new(
        e.TryGetProperty("server_id", out var id) ? id.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("server_version", out var v) ? v.GetString() ?? string.Empty : string.Empty,
        e.TryGetProperty("schema_version", out var s) && s.TryGetInt32(out var sv) ? sv : 0,
        e.TryGetProperty("min_supported_schema_version", out var m) && m.TryGetInt32(out var mv) ? mv : 0,
        e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null);

    private async Task ReceiveLoopAsync()
    {
        Exception? error = null;
        try
        {
            while (!_cts.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                using var doc = await ReceiveDocumentAsync(_cts.Token).ConfigureAwait(false);
                if (doc is null)
                {
                    break;
                }

                Dispatch(doc.RootElement);
            }
        }
        catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or JsonException)
        {
            if (!_cts.IsCancellationRequested)
            {
                error = ex;
            }
        }

        FailAll(new MaException("The connection to Music Assistant was lost.", error ?? new IOException("closed")));
        if (!_cts.IsCancellationRequested)
        {
            Disconnected?.Invoke(error);
        }
    }

    private void Dispatch(JsonElement root)
    {
        if (root.TryGetProperty("message_id", out var idEl) && idEl.GetString() is { } id && _pending.TryGetValue(id, out var pending))
        {
            if (root.TryGetProperty("error_code", out var code))
            {
                var details = root.TryGetProperty("details", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
                pending.Completion.TrySetException(new MaCommandException(pending.Command, code.GetInt32(), details));
                return;
            }

            var result = root.TryGetProperty("result", out var r) ? r.Clone() : default;
            var partial = root.TryGetProperty("partial", out var p) && p.ValueKind == JsonValueKind.True;
            if (pending.Accumulate(result, partial) is { } final)
            {
                pending.Completion.TrySetResult(final);
            }

            return;
        }

        if (root.TryGetProperty("event", out var ev) && ev.GetString() is { } name)
        {
            var objectId = root.TryGetProperty("object_id", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() : null;
            var data = root.TryGetProperty("data", out var dd) ? dd.Clone() : default;
            try
            {
                EventReceived?.Invoke(new MaEvent(name, objectId, data));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Event handler for {Event} failed", name);
            }
        }
    }

    private void FailAll(Exception ex)
    {
        foreach (var p in _pending.Values)
        {
            p.Completion.TrySetException(ex);
        }
    }

    private async Task<JsonDocument?> ReceiveDocumentAsync(CancellationToken ct)
    {
        var buffer = new byte[16 * 1024];
        using var ms = new MemoryStream();
        ValueWebSocketReceiveResult result;
        do
        {
            result = await _socket.ReceiveAsync(buffer.AsMemory(), ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
            {
                return null;
            }

            ms.Write(buffer, 0, result.Count);
        }
        while (!result.EndOfMessage);

        ms.Position = 0;
        return await JsonDocument.ParseAsync(ms, cancellationToken: ct).ConfigureAwait(false);
    }

    private sealed class Pending
    {
        private List<JsonElement>? _chunks;

        public Pending(string command)
        {
            Command = command;
        }

        public string Command { get; }

        public TaskCompletionSource<JsonElement> Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        /// <summary>Returns the final result once complete, or null while more chunks are expected.</summary>
        public JsonElement? Accumulate(JsonElement result, bool partial)
        {
            if (!partial && _chunks is null)
            {
                return result;
            }

            _chunks ??= [];
            if (result.ValueKind == JsonValueKind.Array)
            {
                _chunks.AddRange(result.EnumerateArray().Select(x => x.Clone()));
            }

            if (partial)
            {
                return null;
            }

            using var ms = new MemoryStream();
            using (var writer = new Utf8JsonWriter(ms))
            {
                writer.WriteStartArray();
                foreach (var c in _chunks)
                {
                    c.WriteTo(writer);
                }

                writer.WriteEndArray();
            }

            return JsonDocument.Parse(Encoding.UTF8.GetString(ms.ToArray())).RootElement.Clone();
        }
    }
}
