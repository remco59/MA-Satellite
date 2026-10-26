using System.Security.Cryptography;
using System.Text;

namespace MaSatellite.Core.Security.Noise;

internal sealed class CipherState
{
    private readonly NoiseSuite _suite;
    private byte[]? _key;
    private ulong _nonce;

    public CipherState(NoiseSuite suite)
    {
        _suite = suite;
    }

    public bool HasKey => _key is not null;

    public void Initialize(byte[] key)
    {
        _key = key;
        _nonce = 0;
    }

    public byte[] EncryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> plaintext)
    {
        if (_key is null)
        {
            return plaintext.ToArray();
        }

        if (_nonce == ulong.MaxValue)
        {
            throw new CryptographicException("Noise nonce space exhausted.");
        }

        var ct = NoiseCrypto.Encrypt(_suite, _key, _nonce, ad, plaintext);
        _nonce++;
        return ct;
    }

    public byte[] DecryptWithAd(ReadOnlySpan<byte> ad, ReadOnlySpan<byte> ciphertext)
    {
        if (_key is null)
        {
            return ciphertext.ToArray();
        }

        if (_nonce == ulong.MaxValue)
        {
            throw new CryptographicException("Noise nonce space exhausted.");
        }

        var pt = NoiseCrypto.Decrypt(_suite, _key, _nonce, ad, ciphertext);
        _nonce++; // only advances on success, so a forged message cannot desynchronise us
        return pt;
    }
}

/// <summary>The two directions of an established Noise session. Transport messages carry no associated data.</summary>
public sealed class NoiseCipherPair
{
    private readonly CipherState _send;
    private readonly CipherState _receive;

    internal NoiseCipherPair(CipherState send, CipherState receive, byte[] handshakeHash)
    {
        _send = send;
        _receive = receive;
        HandshakeHash = handshakeHash;
    }

    /// <summary>The handshake hash <c>h</c>, which a re-handshake uses as its prologue.</summary>
    public byte[] HandshakeHash { get; }

    public byte[] Encrypt(ReadOnlySpan<byte> plaintext) => _send.EncryptWithAd(ReadOnlySpan<byte>.Empty, plaintext);

    /// <summary>Throws <see cref="CryptographicException"/> for forged, replayed or reordered messages.</summary>
    public byte[] Decrypt(ReadOnlySpan<byte> ciphertext) => _receive.DecryptWithAd(ReadOnlySpan<byte>.Empty, ciphertext);
}

/// <summary>
/// The <c>Noise_KKpsk2_25519_*_SHA256</c> handshake Sendspin uses. The <b>server is the initiator</b> and the <b>client the responder</b>.
/// The PSK is only needed for message 2, so it is supplied between the two messages (<see cref="Psk"/>), after the responder has
/// read message 1 and chosen the PSK its payload names.
/// <code>
/// -&gt; s            (pre-message, both sides know it)
/// &lt;- s
/// -&gt; e, es, ss
/// &lt;- e, ee, se, psk
/// </code>
/// </summary>
public sealed class NoiseKkPsk2Handshake
{
    private readonly NoiseSuite _suite;
    private readonly bool _initiator;
    private readonly KeyPair _s;
    private readonly byte[] _rs;
    private readonly byte[]? _fixedEphemeral;
    private readonly CipherState _cipher;
    private byte[] _ck;
    private byte[] _h;
    private KeyPair _e;
    private byte[]? _re;
    private int _step;

    /// <param name="suite">Cipher suite.</param>
    /// <param name="initiator">True for the server side.</param>
    /// <param name="prologue">Bytes both sides bind into the handshake.</param>
    /// <param name="localStatic">Our static key pair.</param>
    /// <param name="remoteStatic">The peer's static public key.</param>
    /// <param name="fixedEphemeral">Test vectors only: the ephemeral private key to use instead of a random one.</param>
    public NoiseKkPsk2Handshake(NoiseSuite suite, bool initiator, ReadOnlySpan<byte> prologue, KeyPair localStatic, byte[] remoteStatic, byte[]? fixedEphemeral = null)
    {
        _suite = suite;
        _initiator = initiator;
        _s = localStatic;
        _rs = remoteStatic;
        _fixedEphemeral = fixedEphemeral;
        _cipher = new CipherState(suite);

        var name = Encoding.ASCII.GetBytes(suite.ProtocolName());
        if (name.Length <= NoiseCrypto.HashLength)
        {
            _h = new byte[NoiseCrypto.HashLength];
            name.CopyTo(_h, 0);
        }
        else
        {
            _h = NoiseCrypto.Hash(name);
        }

        _ck = _h;
        MixHash(prologue);
        // Pre-messages: the initiator's static key first, then the responder's.
        MixHash(initiator ? localStatic.PublicKey : remoteStatic);
        MixHash(initiator ? remoteStatic : localStatic.PublicKey);
    }

    /// <summary>The 32-byte PSK mixed into message 2. Must be set before message 2 is written or read.</summary>
    public byte[]? Psk { get; set; }

    public bool IsComplete => _step == 2;

    public byte[] WriteMessage1(ReadOnlySpan<byte> payload)
    {
        Expect(_initiator, 0);
        _e = NewEphemeral();
        var message = new List<byte>(96);
        message.AddRange(_e.PublicKey);
        MixHash(_e.PublicKey);
        MixKey(_e.PublicKey);
        MixKey(NoiseCrypto.Dh(_e.PrivateKey, _rs)); // es
        MixKey(NoiseCrypto.Dh(_s.PrivateKey, _rs)); // ss
        message.AddRange(EncryptAndHash(payload));
        _step = 1;
        return [.. message];
    }

    public byte[] ReadMessage1(ReadOnlySpan<byte> message)
    {
        Expect(!_initiator, 0);
        if (message.Length < 32 + NoiseCrypto.TagLength)
        {
            throw new CryptographicException("Noise message 1 is too short.");
        }

        _re = message[..32].ToArray();
        MixHash(_re);
        MixKey(_re);
        MixKey(NoiseCrypto.Dh(_s.PrivateKey, _re)); // es
        MixKey(NoiseCrypto.Dh(_s.PrivateKey, _rs)); // ss
        var payload = DecryptAndHash(message[32..]);
        _step = 1;
        return payload;
    }

    public byte[] WriteMessage2(ReadOnlySpan<byte> payload)
    {
        Expect(!_initiator, 1);
        var psk = RequirePsk();
        _e = NewEphemeral();
        var message = new List<byte>(96);
        message.AddRange(_e.PublicKey);
        MixHash(_e.PublicKey);
        MixKey(_e.PublicKey);
        MixKey(NoiseCrypto.Dh(_e.PrivateKey, _re!)); // ee
        MixKey(NoiseCrypto.Dh(_e.PrivateKey, _rs)); // se (responder: DH(e, rs))
        MixKeyAndHash(psk);
        message.AddRange(EncryptAndHash(payload));
        _step = 2;
        return [.. message];
    }

    public byte[] ReadMessage2(ReadOnlySpan<byte> message)
    {
        Expect(_initiator, 1);
        var psk = RequirePsk();
        if (message.Length < 32 + NoiseCrypto.TagLength)
        {
            throw new CryptographicException("Noise message 2 is too short.");
        }

        _re = message[..32].ToArray();
        MixHash(_re);
        MixKey(_re);
        MixKey(NoiseCrypto.Dh(_e.PrivateKey, _re)); // ee
        MixKey(NoiseCrypto.Dh(_s.PrivateKey, _re)); // se (initiator: DH(s, re))
        MixKeyAndHash(psk);
        var payload = DecryptAndHash(message[32..]);
        _step = 2;
        return payload;
    }

    /// <summary>Splits into transport ciphers once message 2 has been processed.</summary>
    public NoiseCipherPair Split()
    {
        if (!IsComplete)
        {
            throw new InvalidOperationException("The handshake is not complete.");
        }

        var (k1, k2) = NoiseCrypto.Hkdf2(_ck, ReadOnlySpan<byte>.Empty);
        var c1 = new CipherState(_suite);
        var c2 = new CipherState(_suite);
        c1.Initialize(k1);
        c2.Initialize(k2);
        return _initiator ? new NoiseCipherPair(c1, c2, _h) : new NoiseCipherPair(c2, c1, _h);
    }

    private byte[] RequirePsk() =>
        Psk is { Length: 32 } psk ? psk : throw new InvalidOperationException("Set a 32-byte PSK before message 2.");

    private void Expect(bool roleOk, int step)
    {
        if (!roleOk)
        {
            throw new InvalidOperationException("This message belongs to the other role.");
        }

        if (_step != step)
        {
            throw new InvalidOperationException($"Handshake message out of order (at step {_step}).");
        }
    }

    private KeyPair NewEphemeral() => _fixedEphemeral is { } fixedKey ? KeyPair.FromPrivate(fixedKey) : KeyPair.Generate();

    private void MixHash(ReadOnlySpan<byte> data) => _h = NoiseCrypto.Hash(_h, data.ToArray());

    private void MixKey(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var (ck, k) = NoiseCrypto.Hkdf2(_ck, inputKeyMaterial);
        _ck = ck;
        _cipher.Initialize(k);
    }

    private void MixKeyAndHash(ReadOnlySpan<byte> inputKeyMaterial)
    {
        var (ck, tempH, k) = NoiseCrypto.Hkdf3(_ck, inputKeyMaterial);
        _ck = ck;
        MixHash(tempH);
        _cipher.Initialize(k);
    }

    private byte[] EncryptAndHash(ReadOnlySpan<byte> plaintext)
    {
        var ct = _cipher.EncryptWithAd(_h, plaintext);
        MixHash(ct);
        return ct;
    }

    private byte[] DecryptAndHash(ReadOnlySpan<byte> ciphertext)
    {
        var pt = _cipher.DecryptWithAd(_h, ciphertext);
        MixHash(ciphertext);
        return pt;
    }
}
