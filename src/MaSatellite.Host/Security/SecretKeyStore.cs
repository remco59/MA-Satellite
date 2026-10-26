using System.Text.Json;
using MaSatellite.Core.Security;
using MaSatellite.Core.Security.Noise;
using MaSatellite.Host.Infrastructure;

namespace MaSatellite.Host.Security;

/// <summary>
/// <see cref="ISendspinKeyStore"/> that keeps the player's static key, pairing PSK and the long-term PSKs of paired
/// servers in the <see cref="ISecretStore"/> (DPAPI on Windows), as one JSON secret. A missing or unreadable secret
/// yields a fresh identity, which makes the player show up as a new player in the server.
/// </summary>
public sealed class SecretKeyStore : ISendspinKeyStore
{
    private readonly ISecretStore _secrets;
    private readonly object _gate = new();
    private readonly List<PairingRecord> _records = [];
    private KeyPair _identity;
    private byte[] _pairingPsk = [];

    public SecretKeyStore(ISecretStore secrets)
    {
        _secrets = secrets;
        if (!TryLoad())
        {
            _identity = KeyPair.Generate();
            _pairingPsk = SendspinPsk.NewRandom();
            _records.Clear();
            Persist();
        }
    }

    public KeyPair Identity
    {
        get
        {
            lock (_gate)
            {
                return _identity;
            }
        }
    }

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
            Persist();
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

            Persist();
        }
    }

    public void RemoveRecord(string serverId)
    {
        lock (_gate)
        {
            if (_records.RemoveAll(r => r.ServerId == serverId) > 0)
            {
                Persist();
            }
        }
    }

    private bool TryLoad()
    {
        try
        {
            if (_secrets.Get(SecretNames.SendspinKeys) is not { Length: > 0 } json)
            {
                return false;
            }

            var dto = JsonSerializer.Deserialize<StoredKeys>(json);
            if (dto is null
                || !Base64Url.TryDecode(dto.PrivateKey, 32, out var priv)
                || !Base64Url.TryDecode(dto.PairingPsk, 32, out var psk))
            {
                return false;
            }

            _identity = KeyPair.FromPrivate(priv);
            _pairingPsk = psk;
            foreach (var r in dto.Records ?? [])
            {
                if (r.ServerId is { Length: > 0 } id && Base64Url.TryDecode(r.LongTermPsk, 32, out var lt))
                {
                    _records.Add(new PairingRecord(id, lt));
                }
            }

            return true;
        }
        catch (Exception ex) when (ex is JsonException or FormatException or ArgumentException)
        {
            return false;
        }
    }

    private void Persist()
    {
        var dto = new StoredKeys
        {
            PrivateKey = Base64Url.Encode(_identity.PrivateKey),
            PairingPsk = Base64Url.Encode(_pairingPsk),
            Records = [.. _records.Select(r => new StoredRecord { ServerId = r.ServerId, LongTermPsk = Base64Url.Encode(r.LongTermPsk) })],
        };
        _secrets.Set(SecretNames.SendspinKeys, JsonSerializer.Serialize(dto));
    }

    private sealed class StoredKeys
    {
        public string? PrivateKey { get; set; }

        public string? PairingPsk { get; set; }

        public List<StoredRecord>? Records { get; set; }
    }

    private sealed class StoredRecord
    {
        public string? ServerId { get; set; }

        public string? LongTermPsk { get; set; }
    }
}
