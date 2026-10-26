using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using MaSatellite.Core.Time;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Core.Transport;

/// <summary>
/// Accepts WebSocket connections on one TCP port (server-initiated Sendspin). A tiny HTTP upgrade handshake on top of
/// <see cref="TcpListener"/> keeps this free of http.sys URL ACLs (no admin rights) and of the ASP.NET Core runtime.
/// </summary>
public sealed class SendspinListener : IAsyncDisposable
{
    private const string WebSocketGuid = "258EAFA5-E914-47DA-95CA-C5AB0DC85B11";
    private const int MaxHeaderBytes = 16 * 1024;

    private readonly string _path;
    private readonly Func<ISendspinTransport, Task> _onConnection;
    private readonly ILocalClock _clock;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _cts = new();
    private readonly List<Task> _connections = [];
    private readonly HashSet<WebSocket> _sockets = [];
    private TcpListener? _listener;
    private Task? _acceptLoop;

    public SendspinListener(int port, string path, Func<ISendspinTransport, Task> onConnection, ILocalClock clock, ILogger? logger = null)
    {
        Port = port;
        _path = path;
        _onConnection = onConnection;
        _clock = clock;
        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>The bound port (useful when constructed with port 0).</summary>
    public int Port { get; private set; }

    public void Start()
    {
        var listener = CreateListener(Port);
        Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        _listener = listener;
        _acceptLoop = Task.Run(AcceptLoopAsync);
        _logger.LogInformation("Sendspin listener started on port {Port}, path {Path}", Port, _path);
    }

    /// <summary>Dual-stack when IPv6 is available, plain IPv4 otherwise (IPv6 can be disabled on a machine).</summary>
    private static TcpListener CreateListener(int port)
    {
        if (Socket.OSSupportsIPv6)
        {
            try
            {
                var dual = new TcpListener(IPAddress.IPv6Any, port);
                dual.Server.DualMode = true;
                dual.Start(16);
                return dual;
            }
            catch (SocketException ex) when (ex.SocketErrorCode is SocketError.AddressFamilyNotSupported or SocketError.ProtocolNotSupported)
            {
            }
        }

        var v4 = new TcpListener(IPAddress.Any, port);
        v4.Start(16);
        return v4;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener?.Stop();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
            }
        }

        // Dropping the listener drops its connections; otherwise they would wait for the peer forever.
        WebSocket[] open;
        Task[] pending;
        lock (_connections)
        {
            open = [.. _sockets];
            pending = [.. _connections];
        }

        foreach (var socket in open)
        {
            socket.Abort();
        }

        await Task.WhenAll(pending.Select(t => t.ContinueWith(_ => { }, TaskScheduler.Default))).ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or ObjectDisposedException or SocketException)
            {
                return;
            }

            var task = Task.Run(() => HandleClientAsync(client));
            lock (_connections)
            {
                _connections.RemoveAll(t => t.IsCompleted);
                _connections.Add(task);
            }
        }
    }

    private async Task HandleClientAsync(TcpClient client)
    {
        var remote = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        try
        {
            client.NoDelay = true;
            var stream = client.GetStream();
            var websocket = await UpgradeAsync(stream, client).ConfigureAwait(false);
            if (websocket is null)
            {
                return;
            }

            _logger.LogInformation("Server connected from {Remote}", remote);
            lock (_connections)
            {
                _sockets.Add(websocket);
            }

            try
            {
                await using var transport = new WebSocketTransport(websocket, _clock, remote);
                await _onConnection(transport).ConfigureAwait(false);
            }
            finally
            {
                lock (_connections)
                {
                    _sockets.Remove(websocket);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException or WebSocketException)
        {
            _logger.LogDebug(ex, "Connection from {Remote} ended", remote);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error on connection from {Remote}", remote);
        }
        finally
        {
            client.Dispose();
        }
    }

    private async Task<WebSocket?> UpgradeAsync(NetworkStream stream, TcpClient client)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(5));

        var head = await ReadHeadAsync(stream, timeout.Token).ConfigureAwait(false);
        if (head is null)
        {
            await WriteResponseAsync(stream, "400 Bad Request").ConfigureAwait(false);
            return null;
        }

        var lines = head.Split("\r\n", StringSplitOptions.None);
        var requestLine = lines[0].Split(' ');
        if (requestLine.Length < 3 || requestLine[0] != "GET")
        {
            await WriteResponseAsync(stream, "400 Bad Request").ConfigureAwait(false);
            return null;
        }

        var target = requestLine[1];
        var queryAt = target.IndexOf('?', StringComparison.Ordinal);
        if (queryAt >= 0)
        {
            target = target[..queryAt];
        }

        if (!string.Equals(target, _path, StringComparison.Ordinal))
        {
            await WriteResponseAsync(stream, "404 Not Found").ConfigureAwait(false);
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1))
        {
            var colon = line.IndexOf(':', StringComparison.Ordinal);
            if (colon > 0)
            {
                headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
            }
        }

        if (!headers.TryGetValue("Upgrade", out var upgrade) || !upgrade.Contains("websocket", StringComparison.OrdinalIgnoreCase)
            || !headers.TryGetValue("Sec-WebSocket-Key", out var key) || string.IsNullOrWhiteSpace(key)
            || !headers.TryGetValue("Sec-WebSocket-Version", out var version) || version != "13")
        {
            await WriteResponseAsync(stream, "426 Upgrade Required", "Sec-WebSocket-Version: 13\r\n").ConfigureAwait(false);
            return null;
        }

#pragma warning disable CA5350 // SHA-1 is mandated by RFC 6455 for the WebSocket accept key; it is not used for security here.
        var accept = Convert.ToBase64String(SHA1.HashData(Encoding.ASCII.GetBytes(key + WebSocketGuid)));
#pragma warning restore CA5350
        var response = $"HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Accept: {accept}\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(response), timeout.Token).ConfigureAwait(false);
        _ = client;

        return WebSocket.CreateFromStream(stream, new WebSocketCreationOptions
        {
            IsServer = true,
            KeepAliveInterval = TimeSpan.FromSeconds(15),
        });
    }

    private static async Task<string?> ReadHeadAsync(NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[MaxHeaderBytes];
        var length = 0;
        while (length < buffer.Length)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(length, 1), ct).ConfigureAwait(false);
            if (read == 0)
            {
                return null;
            }

            length++;
            if (length >= 4 && buffer[length - 4] == '\r' && buffer[length - 3] == '\n' && buffer[length - 2] == '\r' && buffer[length - 1] == '\n')
            {
                return Encoding.ASCII.GetString(buffer, 0, length - 4);
            }
        }

        return null;
    }

    private static async Task WriteResponseAsync(NetworkStream stream, string status, string extraHeaders = "")
    {
        try
        {
            var text = $"HTTP/1.1 {status}\r\n{extraHeaders}Content-Length: 0\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(text)).ConfigureAwait(false);
        }
        catch (IOException)
        {
        }
    }
}
