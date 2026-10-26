using System.Buffers;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using System.Text;
using MaSatellite.Core.Time;

namespace MaSatellite.Core.Transport;

/// <summary>Plaintext Sendspin transport over a WebSocket (legacy, unencrypted protocol).</summary>
public sealed class WebSocketTransport : ISendspinTransport
{
    private const int MaxMessageBytes = 16 * 1024 * 1024;

    private readonly WebSocket _socket;
    private readonly ILocalClock _clock;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    public WebSocketTransport(WebSocket socket, ILocalClock clock, string remoteDescription)
    {
        _socket = socket;
        _clock = clock;
        RemoteDescription = remoteDescription;
    }

    public string RemoteDescription { get; }

    public async Task SendTextAsync(string text, CancellationToken cancellationToken)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_socket.State != WebSocketState.Open)
            {
                throw new InvalidOperationException("The connection is closed.");
            }

            await _socket.SendAsync(bytes, WebSocketMessageType.Text, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    /// <summary>Servers send binary audio/artwork frames; clients only need this in tests.</summary>
    public async Task SendBinaryAsync(ReadOnlyMemory<byte> data, CancellationToken cancellationToken)
    {
        await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(data, WebSocketMessageType.Binary, endOfMessage: true, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public async IAsyncEnumerable<TransportMessage> ReceiveAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var chunk = new byte[16 * 1024];
        var writer = new ArrayBufferWriter<byte>(64 * 1024);
        while (!cancellationToken.IsCancellationRequested && _socket.State is WebSocketState.Open or WebSocketState.CloseSent)
        {
            writer.Clear();
            ValueWebSocketReceiveResult result;
            do
            {
                try
                {
                    result = await _socket.ReceiveAsync(chunk.AsMemory(), cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    yield break;
                }

                if (result.MessageType == WebSocketMessageType.Close)
                {
                    if (_socket.State == WebSocketState.CloseReceived)
                    {
                        try
                        {
                            await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None).ConfigureAwait(false);
                        }
                        catch (WebSocketException)
                        {
                        }
                    }

                    yield break;
                }

                writer.Write(chunk.AsSpan(0, result.Count));
                if (writer.WrittenCount > MaxMessageBytes)
                {
                    yield break;
                }
            }
            while (!result.EndOfMessage);

            var receivedAt = _clock.NowMicros;
            if (result.MessageType == WebSocketMessageType.Text)
            {
                yield return TransportMessage.FromText(Encoding.UTF8.GetString(writer.WrittenSpan), receivedAt);
            }
            else
            {
                yield return TransportMessage.FromBinary(writer.WrittenSpan.ToArray(), receivedAt);
            }
        }
    }

    public async Task CloseAsync(CancellationToken cancellationToken)
    {
        if (_socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(2));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is WebSocketException or OperationCanceledException or ObjectDisposedException)
            {
            }
        }
    }

    public ValueTask DisposeAsync()
    {
        _socket.Abort();
        _socket.Dispose();
        _sendLock.Dispose();
        return ValueTask.CompletedTask;
    }
}
