using System.Net.WebSockets;
using MaSatellite.Core.Time;

namespace MaSatellite.Core.Transport;

/// <summary>Client-initiated connection: we open the WebSocket to the server's Sendspin endpoint.</summary>
public static class SendspinConnector
{
    public static async Task<ISendspinTransport> ConnectAsync(string host, int port, string path, ILocalClock clock, CancellationToken cancellationToken)
    {
        var uri = new UriBuilder("ws", host, port, path).Uri;
        var socket = new ClientWebSocket();
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            await socket.ConnectAsync(uri, timeout.Token).ConfigureAwait(false);
        }
        catch
        {
            socket.Dispose();
            throw;
        }

        return new WebSocketTransport(socket, clock, uri.ToString());
    }
}
