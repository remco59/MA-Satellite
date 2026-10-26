using System.Buffers.Binary;

namespace MaSatellite.Core.Session;

public enum ArtworkResult
{
    /// <summary>Message accepted; nothing to report yet.</summary>
    Ok,

    /// <summary>A transfer finished (<c>completed</c> is set; empty data means "clear the artwork").</summary>
    Completed,

    /// <summary>The pending image was cancelled.</summary>
    Cancelled,

    /// <summary>A malformed message or sequence: the connection must be closed.</summary>
    ProtocolError,
}

public sealed record ArtworkImageComplete(int Channel, long TimestampMicros, byte[] Data);

/// <summary>
/// Reassembles artwork transfers: an announce <c>[type][flags=2][timestamp:8][total_size:4]</c>, parts <c>[type][flags=0][data]</c>
/// and cancels <c>[type][flags=1]</c>. At most one transfer is in flight across all channels.
/// </summary>
public sealed class ArtworkAssembler
{
    private const byte FlagCancel = 1;
    private const byte FlagAnnounce = 2;
    private const int MaxImageBytes = 16 * 1024 * 1024;

    private int _channel = -1;
    private long _timestamp;
    private byte[]? _buffer;
    private int _received;

    /// <summary>Discards any partial transfer (stream end, role removed).</summary>
    public void Reset()
    {
        _channel = -1;
        _buffer = null;
        _received = 0;
    }

    public ArtworkResult Process(int channel, ReadOnlySpan<byte> message, out ArtworkImageComplete? completed)
    {
        completed = null;
        if (message.Length < 2)
        {
            return ArtworkResult.ProtocolError;
        }

        var flags = message[1];
        if ((flags & 0xFC) != 0 || (flags & FlagCancel) != 0 && (flags & FlagAnnounce) != 0)
        {
            return ArtworkResult.ProtocolError;
        }

        if ((flags & FlagCancel) != 0)
        {
            if (message.Length != 2)
            {
                return ArtworkResult.ProtocolError;
            }

            if (_channel == channel)
            {
                Reset();
            }

            return ArtworkResult.Cancelled;
        }

        if ((flags & FlagAnnounce) != 0)
        {
            if (message.Length != 14 || _channel >= 0)
            {
                return ArtworkResult.ProtocolError; // wrong size, or an announce while a transfer is in flight
            }

            var timestamp = BinaryPrimitives.ReadInt64BigEndian(message.Slice(2, 8));
            var total = BinaryPrimitives.ReadUInt32BigEndian(message.Slice(10, 4));
            if (total == 0)
            {
                completed = new ArtworkImageComplete(channel, timestamp, []);
                return ArtworkResult.Completed;
            }

            if (total > MaxImageBytes)
            {
                // Too large for us: count the parts but keep none of the data.
                _channel = channel;
                _timestamp = timestamp;
                _buffer = null;
                _received = 0;
                _discardTotal = (int)Math.Min(total, int.MaxValue);
                return ArtworkResult.Ok;
            }

            _channel = channel;
            _timestamp = timestamp;
            _buffer = new byte[total];
            _received = 0;
            _discardTotal = 0;
            return ArtworkResult.Ok;
        }

        // part
        if (_channel != channel)
        {
            return ArtworkResult.ProtocolError;
        }

        var data = message[2..];
        if (_buffer is null)
        {
            _received += data.Length;
            if (_received > _discardTotal)
            {
                return ArtworkResult.ProtocolError;
            }

            if (_received == _discardTotal)
            {
                Reset();
            }

            return ArtworkResult.Ok;
        }

        if (_received + data.Length > _buffer.Length)
        {
            return ArtworkResult.ProtocolError;
        }

        data.CopyTo(_buffer.AsSpan(_received));
        _received += data.Length;
        if (_received == _buffer.Length)
        {
            completed = new ArtworkImageComplete(channel, _timestamp, _buffer);
            Reset();
            return ArtworkResult.Completed;
        }

        return ArtworkResult.Ok;
    }

    private int _discardTotal;
}
