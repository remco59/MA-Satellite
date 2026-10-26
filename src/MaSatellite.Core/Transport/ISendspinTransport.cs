namespace MaSatellite.Core.Transport;

/// <summary>One complete message from the peer. Text frames carry JSON, binary frames carry audio/artwork.</summary>
public readonly record struct TransportMessage(bool IsText, string? Text, ReadOnlyMemory<byte> Binary, long ReceivedAtMicros)
{
    public static TransportMessage FromText(string text, long receivedAt) => new(true, text, default, receivedAt);

    public static TransportMessage FromBinary(ReadOnlyMemory<byte> data, long receivedAt) => new(false, null, data, receivedAt);
}

/// <summary>
/// The protocol layer talks to the network only through this interface, so the plaintext WebSocket transport can be
/// replaced by an encrypted (Noise) one without touching the session logic.
/// </summary>
public interface ISendspinTransport : IAsyncDisposable
{
    /// <summary>Human readable remote endpoint, for logs.</summary>
    string RemoteDescription { get; }

    Task SendTextAsync(string text, CancellationToken cancellationToken);

    /// <summary>Yields messages until the peer closes the connection or the token is cancelled.</summary>
    IAsyncEnumerable<TransportMessage> ReceiveAsync(CancellationToken cancellationToken);

    Task CloseAsync(CancellationToken cancellationToken);
}
