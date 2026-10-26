using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MaSatellite.Core.Security.Noise;

namespace MaSatellite.Core.Tests;

public class NoiseTests
{
    private static byte[] Hex(string s) => Convert.FromHexString(s);

    public static IEnumerable<object[]> VectorNames()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "noise-kkpsk2.json")));
        foreach (var v in doc.RootElement.GetProperty("vectors").EnumerateArray())
        {
            yield return [v.GetProperty("protocol_name").GetString()!];
        }
    }

    [Theory]
    [MemberData(nameof(VectorNames))]
    public void Matches_the_official_noise_test_vectors(string protocolName)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "noise-kkpsk2.json")));
        var v = doc.RootElement.GetProperty("vectors").EnumerateArray().Single(x => x.GetProperty("protocol_name").GetString() == protocolName);
        Assert.True(NoiseSuiteNames.TryParse(protocolName["Noise_KKpsk2_".Length..], out var suite));

        byte[] H(string name) => Hex(v.GetProperty(name).GetString()!);
        var psk = Hex(v.GetProperty("init_psks")[0].GetString()!);
        var initStatic = KeyPair.FromPrivate(H("init_static"));
        var respStatic = KeyPair.FromPrivate(H("resp_static"));
        Assert.Equal(H("init_remote_static"), respStatic.PublicKey);
        Assert.Equal(H("resp_remote_static"), initStatic.PublicKey);

        var init = new NoiseKkPsk2Handshake(suite, true, H("init_prologue"), initStatic, respStatic.PublicKey, H("init_ephemeral")) { Psk = psk };
        var resp = new NoiseKkPsk2Handshake(suite, false, H("resp_prologue"), respStatic, initStatic.PublicKey, H("resp_ephemeral")) { Psk = psk };

        var messages = v.GetProperty("messages").EnumerateArray().Select(m => (Payload: Hex(m.GetProperty("payload").GetString()!), Cipher: Hex(m.GetProperty("ciphertext").GetString()!))).ToList();

        var m1 = init.WriteMessage1(messages[0].Payload);
        Assert.Equal(messages[0].Cipher, m1);
        Assert.Equal(messages[0].Payload, resp.ReadMessage1(m1));

        var m2 = resp.WriteMessage2(messages[1].Payload);
        Assert.Equal(messages[1].Cipher, m2);
        Assert.Equal(messages[1].Payload, init.ReadMessage2(m2));

        var expectedHash = H("handshake_hash");
        var initCipher = init.Split();
        var respCipher = resp.Split();
        Assert.Equal(expectedHash, initCipher.HandshakeHash);
        Assert.Equal(expectedHash, respCipher.HandshakeHash);

        // transport messages alternate initiator -> responder, responder -> initiator
        for (var i = 2; i < messages.Count; i++)
        {
            var fromInitiator = (i - 2) % 2 == 0;
            var (sender, receiver) = fromInitiator ? (initCipher, respCipher) : (respCipher, initCipher);
            var ct = sender.Encrypt(messages[i].Payload);
            Assert.Equal(messages[i].Cipher, ct);
            Assert.Equal(messages[i].Payload, receiver.Decrypt(ct));
        }
    }

    [Fact]
    public void Sentinel_psk_and_id_match_the_published_constants()
    {
        var sentinel = SHA256.HashData(Encoding.UTF8.GetBytes("sendspin-sentinel-psk-v1"));
        Assert.Equal("1b5e24dbc1aed95fc2a5a338a90c05df44bd10f5ec1f4cd66cbf86272767b9d3", Convert.ToHexString(sentinel).ToLowerInvariant());
        var id = SHA256.HashData([.. Encoding.UTF8.GetBytes("sendspin-psk-id-v1"), .. sentinel]);
        Assert.Equal("185b15f6d2da4909bd1dc156a4ab206103abef0153bcd52d926170b95cf7ce8a", Convert.ToHexString(id).ToLowerInvariant());
    }

    [Theory]
    [InlineData(NoiseSuite.ChaChaPolySha256)]
    [InlineData(NoiseSuite.AesGcmSha256)]
    public void Random_keys_round_trip_and_a_wrong_psk_fails(NoiseSuite suite)
    {
        var a = KeyPair.Generate();
        var b = KeyPair.Generate();
        var psk = RandomNumberGenerator.GetBytes(32);
        var prologue = Encoding.UTF8.GetBytes("prologue");

        var server = new NoiseKkPsk2Handshake(suite, true, prologue, a, b.PublicKey);
        var client = new NoiseKkPsk2Handshake(suite, false, prologue, b, a.PublicKey);
        var m1 = server.WriteMessage1(Encoding.UTF8.GetBytes("{\"psk_id\":\"x\"}"));
        Assert.Equal("{\"psk_id\":\"x\"}", Encoding.UTF8.GetString(client.ReadMessage1(m1)));
        client.Psk = psk;
        server.Psk = psk;
        var m2 = client.WriteMessage2("{}"u8);
        Assert.Equal("{}", Encoding.UTF8.GetString(server.ReadMessage2(m2)));

        var s = server.Split();
        var c = client.Split();
        Assert.Equal(s.HandshakeHash, c.HandshakeHash);
        Assert.Equal("hello", Encoding.UTF8.GetString(c.Decrypt(s.Encrypt("hello"u8))));
        Assert.Equal("back", Encoding.UTF8.GetString(s.Decrypt(c.Encrypt("back"u8))));

        // a server holding a different PSK cannot read message 2
        var server2 = new NoiseKkPsk2Handshake(suite, true, prologue, a, b.PublicKey) { Psk = RandomNumberGenerator.GetBytes(32) };
        var client2 = new NoiseKkPsk2Handshake(suite, false, prologue, b, a.PublicKey);
        client2.ReadMessage1(server2.WriteMessage1("x"u8));
        client2.Psk = psk;
        Assert.ThrowsAny<CryptographicException>(() => server2.ReadMessage2(client2.WriteMessage2("{}"u8)));
    }

    [Fact]
    public void Different_prologues_fail_the_handshake()
    {
        var a = KeyPair.Generate();
        var b = KeyPair.Generate();
        var server = new NoiseKkPsk2Handshake(NoiseSuite.ChaChaPolySha256, true, "one"u8, a, b.PublicKey);
        var client = new NoiseKkPsk2Handshake(NoiseSuite.ChaChaPolySha256, false, "two"u8, b, a.PublicKey);
        Assert.ThrowsAny<CryptographicException>(() => client.ReadMessage1(server.WriteMessage1("x"u8)));
    }

    [Fact]
    public void Transport_rejects_replays_reordering_and_tampering()
    {
        var (server, client) = Established();
        var m1 = server.Encrypt("one"u8);
        var m2 = server.Encrypt("two"u8);

        Assert.ThrowsAny<CryptographicException>(() => client.Decrypt(m2)); // out of order
        Assert.Equal("one", Encoding.UTF8.GetString(client.Decrypt(m1)));
        Assert.ThrowsAny<CryptographicException>(() => client.Decrypt(m1)); // replay
        var tampered = (byte[])m2.Clone();
        tampered[0] ^= 1;
        Assert.ThrowsAny<CryptographicException>(() => client.Decrypt(tampered));
        Assert.Equal("two", Encoding.UTF8.GetString(client.Decrypt(m2))); // a failed attempt does not burn the nonce
    }

    [Fact]
    public void Low_order_public_keys_are_rejected()
    {
        var a = KeyPair.Generate();
        var zeroKey = new byte[32];
        var server = new NoiseKkPsk2Handshake(NoiseSuite.ChaChaPolySha256, true, default, a, zeroKey);
        Assert.ThrowsAny<CryptographicException>(() => server.WriteMessage1("x"u8));
    }

    private static (NoiseCipherPair Server, NoiseCipherPair Client) Established()
    {
        var a = KeyPair.Generate();
        var b = KeyPair.Generate();
        var psk = RandomNumberGenerator.GetBytes(32);
        var server = new NoiseKkPsk2Handshake(NoiseSuite.ChaChaPolySha256, true, default, a, b.PublicKey) { Psk = psk };
        var client = new NoiseKkPsk2Handshake(NoiseSuite.ChaChaPolySha256, false, default, b, a.PublicKey) { Psk = psk };
        client.ReadMessage1(server.WriteMessage1("x"u8));
        server.ReadMessage2(client.WriteMessage2("{}"u8));
        return (server.Split(), client.Split());
    }
}
