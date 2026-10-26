using MaSatellite.Core.Security.Noise;

namespace MaSatellite.Core.Security;

public sealed record PairingRecord(string ServerId, byte[] LongTermPsk);

/// <summary>Persistent cryptographic identity of this player: static key pair, pairing PSK and the long-term PSKs of paired servers.</summary>
public interface ISendspinKeyStore
{
    /// <summary>Static Curve25519 key pair; its public key (base64url) is the <c>client_id</c>.</summary>
    KeyPair Identity { get; }

    /// <summary>This player's own pairing PSK, shown to the operator inside the pairing token.</summary>
    byte[] PairingPsk { get; }

    /// <summary>Replaces the pairing PSK (revokes the previously shared token).</summary>
    void RegeneratePairingPsk();

    PairingRecord? FindRecordByPskId(string pskId);

    IReadOnlyList<PairingRecord> Records { get; }

    /// <summary>Stores or replaces the record for a server, evicting the oldest when more than <see cref="MaxRecords"/> exist.</summary>
    void SaveRecord(PairingRecord record);

    void RemoveRecord(string serverId);
}

public static class KeyStoreExtensions
{
    public const int MaxRecords = 16;

    public static string ClientId(this ISendspinKeyStore store) => Base64Url.Encode(store.Identity.PublicKey);

    public static string PairingToken(this ISendspinKeyStore store) => Security.PairingToken.Create(store.Identity.PublicKey, store.PairingPsk);
}

/// <summary>Non-persistent key store (tests; also the fallback when no secret store is available).</summary>
public sealed class InMemoryKeyStore : ISendspinKeyStore
{
    private readonly object _gate = new();
    private readonly List<PairingRecord> _records = [];
    private byte[] _pairingPsk = SendspinPsk.NewRandom();

    public InMemoryKeyStore(KeyPair? identity = null)
    {
        Identity = identity ?? KeyPair.Generate();
    }

    public KeyPair Identity { get; }

    public byte[] PairingPsk
    {
        get
        {
            lock (_gate)
            {
                return _pairingPsk;
            }
        }
    }

    public IReadOnlyList<PairingRecord> Records
    {
        get
        {
            lock (_gate)
            {
                return [.. _records];
            }
        }
    }

    public void RegeneratePairingPsk()
    {
        lock (_gate)
        {
            _pairingPsk = SendspinPsk.NewRandom();
        }
    }

    public PairingRecord? FindRecordByPskId(string pskId)
    {
        lock (_gate)
        {
            return _records.FirstOrDefault(r => SendspinPsk.Id(r.LongTermPsk) == pskId);
        }
    }

    public void SaveRecord(PairingRecord record)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => r.ServerId == record.ServerId);
            _records.Add(record);
            while (_records.Count > KeyStoreExtensions.MaxRecords)
            {
                _records.RemoveAt(0);
            }
        }
    }

    public void RemoveRecord(string serverId)
    {
        lock (_gate)
        {
            _records.RemoveAll(r => r.ServerId == serverId);
        }
    }
}
