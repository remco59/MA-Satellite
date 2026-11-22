using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Host.LocalApi;

/// <summary>
/// Small authenticated HTTP endpoint for Home Assistant announcements.
/// It binds to the LAN, but accepts only private-network clients and requires the Sendspin pairing token.
/// </summary>
public sealed class AnnouncementApiServer : IAsyncDisposable
{
    public const int DefaultPort = 8930;
    private const int MaxHeaderBytes = 16 * 1024;
    private const int MaxMediaBytes = 30 * 1024 * 1024;

    private readonly IAnnouncementApiSource _source;
    private readonly int _port;
    private readonly ILogger _logger;
    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();
    private Task? _acceptTask;

    public AnnouncementApiServer(IAnnouncementApiSource source, int port, ILogger? logger = null)
    {
        _source = source;
        _port = port;
        _logger = logger ?? NullLogger<AnnouncementApiServer>.Instance;
        _listener = new TcpListener(IPAddress.IPv6Any, port);
        _listener.Server.DualMode = true;
    }

    public void Start()
    {
        _listener.Start(8);
        _acceptTask = Task.Run(AcceptLoopAsync);
        _logger.LogInformation("Announcement API listening on port {Port} (private networks only)", _port);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync().ConfigureAwait(false);
        _listener.Stop();
        if (_acceptTask is not null)
        {
            try
            {
                await _acceptTask.ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
            }
        }

        _cts.Dispose();
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            TcpClient client;
            try
            {
                client = await _listener.AcceptTcpClientAsync(_cts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is OperationCanceledException or SocketException or ObjectDisposedException)
            {
                return;
            }

            _ = HandleClientAsync(client, _cts.Token);
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken cancellationToken)
    {
        using (client)
        {
            var remote = (client.Client.RemoteEndPoint as IPEndPoint)?.Address;
            if (remote is null || !IsPrivateAddress(remote))
            {
                await WriteResponseAsync(client.GetStream(), 403, "Forbidden", "Private network clients only.", cancellationToken).ConfigureAwait(false);
                return;
            }

            try
            {
                var stream = client.GetStream();
                var headerText = await ReadHeadersAsync(stream, cancellationToken).ConfigureAwait(false);
                var lines = headerText.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length == 0)
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", "Missing request line.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var requestLine = lines[0].Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (requestLine.Length != 3 || requestLine[2] != "HTTP/1.1")
                {
                    await WriteResponseAsync(stream, 400, "Bad Request", "Invalid HTTP request line.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var line in lines.Skip(1))
                {
                    var separator = line.IndexOf(':');
                    if (separator > 0)
                    {
                        headers[line[..separator].Trim()] = line[(separator + 1)..].Trim();
                    }
                }

                if (!IsAuthorized(headers.GetValueOrDefault("Authorization"), _source.ApiToken))
                {
                    await WriteResponseAsync(stream, 401, "Unauthorized", "A valid bearer token is required.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (requestLine[0] == "GET" && requestLine[1] == "/api/device-info")
                {
                    var info = new { name = _source.PlayerName, mac_address = _source.MacAddress };
                    await WriteJsonAsync(stream, 200, "OK", info, cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (requestLine[0] != "POST" || requestLine[1] != "/api/announcement")
                {
                    await WriteResponseAsync(stream, 404, "Not Found", "Unknown endpoint.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (headers.ContainsKey("Transfer-Encoding") || !int.TryParse(headers.GetValueOrDefault("Content-Length"), out var contentLength)
                    || contentLength <= 0 || contentLength > MaxMediaBytes)
                {
                    await WriteResponseAsync(stream, 413, "Payload Too Large", "A valid Content-Length up to 30 MB is required.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var contentType = headers.GetValueOrDefault("Content-Type") ?? "application/octet-stream";
                if (!contentType.StartsWith("audio/", StringComparison.OrdinalIgnoreCase)
                    && !contentType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase))
                {
                    await WriteResponseAsync(stream, 415, "Unsupported Media Type", "Send an encoded audio file.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                if (!_source.IsStreamActive)
                {
                    await WriteResponseAsync(stream, 409, "Conflict", "A live Sendspin stream must be active for announcement mixing.", cancellationToken).ConfigureAwait(false);
                    return;
                }

                var media = new byte[contentLength];
                await ReadExactlyAsync(stream, media, cancellationToken).ConfigureAwait(false);
                await _source.PlayAnnouncementAsync(media, contentType, 100, cancellationToken).ConfigureAwait(false);
                await WriteJsonAsync(stream, 202, "Accepted", new { status = "queued" }, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Announcement API request from {Remote} failed", remote);
                try
                {
                    await WriteResponseAsync(client.GetStream(), 500, "Internal Server Error", "The announcement could not be queued.", CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception writeError) when (writeError is IOException or SocketException or ObjectDisposedException)
                {
                }
            }
        }
    }

    private static async Task<string> ReadHeadersAsync(NetworkStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var one = new byte[1];
        while (buffer.Length < MaxHeaderBytes)
        {
            var count = await stream.ReadAsync(one.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0)
            {
                throw new IOException("Client disconnected before sending HTTP headers.");
            }

            buffer.WriteByte(one[0]);
            var length = buffer.Length;
            if (length >= 4)
            {
                var data = buffer.GetBuffer();
                if (data[length - 4] == 13 && data[length - 3] == 10 && data[length - 2] == 13 && data[length - 1] == 10)
                {
                    return Encoding.ASCII.GetString(data, 0, (int)length - 4);
                }
            }
        }

        throw new IOException("HTTP headers exceeded the size limit.");
    }

    private static async Task ReadExactlyAsync(NetworkStream stream, byte[] destination, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < destination.Length)
        {
            var read = await stream.ReadAsync(destination.AsMemory(offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                throw new IOException("Client disconnected before the announcement body was complete.");
            }

            offset += read;
        }
    }

    private static bool IsAuthorized(string? header, string expectedToken)
    {
        if (string.IsNullOrWhiteSpace(expectedToken) || header is null || !header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var supplied = Encoding.UTF8.GetBytes(header[7..].Trim());
        var expected = Encoding.UTF8.GetBytes(expectedToken);
        return supplied.Length == expected.Length && CryptographicOperations.FixedTimeEquals(supplied, expected);
    }

    private static bool IsPrivateAddress(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            return bytes[0] == 10
                || bytes[0] == 192 && bytes[1] == 168
                || bytes[0] == 172 && bytes[1] is >= 16 and <= 31
                || bytes[0] == 169 && bytes[1] == 254;
        }

        return address.AddressFamily == AddressFamily.InterNetworkV6
            && (bytes[0] & 0xFE) == 0xFC; // unique-local IPv6 (fc00::/7)
    }

    private static async Task WriteJsonAsync(NetworkStream stream, int status, string reason, object value, CancellationToken cancellationToken)
    {
        await WriteResponseAsync(
            stream,
            status,
            reason,
            JsonSerializer.Serialize(value),
            "application/json; charset=utf-8",
            cancellationToken).ConfigureAwait(false);
    }

    private static Task WriteResponseAsync(NetworkStream stream, int status, string reason, string body, CancellationToken cancellationToken) =>
        WriteResponseAsync(stream, status, reason, body, "text/plain; charset=utf-8", cancellationToken);

    private static async Task WriteResponseAsync(NetworkStream stream, int status, string reason, string body, string contentType, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(body);
        var header = Encoding.ASCII.GetBytes(
            $"HTTP/1.1 {status} {reason}\r\nContent-Type: {contentType}\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\nCache-Control: no-store\r\n\r\n");
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
    }
}
