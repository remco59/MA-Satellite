using MaSatellite.Core.Security;

namespace MaSatellite.Core.Tests;

public class PairingTokenTests
{
    private const string ReferenceToken = "SP:0AAAQEAYEAUDAOCAJBIFQYDIOB4IBCEQTCQKRMFYYDENBWHA5DYP6BYPC4PSOLZXH5DU6V97M5XXO74HR6LZ7J5PW674PT6X37T6757Y";

    private static byte[] Range(int from, int count) => Enumerable.Range(from, count).Select(i => (byte)i).ToArray();

    [Fact]
    public void Matches_the_spec_reference_vector()
    {
        Assert.Equal(ReferenceToken, PairingToken.Create(Range(0, 32), Range(0xe0, 32)));
    }

    [Fact]
    public void Parses_leniently_and_round_trips()
    {
        foreach (var variant in new[] { ReferenceToken, ReferenceToken.ToLowerInvariant(), "  " + ReferenceToken + "\n", ReferenceToken[3..] })
        {
            Assert.True(PairingToken.TryParse(variant, out var key, out var psk), variant);
            Assert.Equal(Range(0, 32), key);
            Assert.Equal(Range(0xe0, 32), psk);
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("SP:1AAAA")]
    [InlineData("SP:0")]
    [InlineData("SP:0AAAA")] // payload too short
    [InlineData("SP:0!!!!!!")]
    public void Rejects_malformed_tokens(string token)
    {
        Assert.False(PairingToken.TryParse(token, out _, out _));
    }

    [Fact]
    public void Tokens_only_use_the_qr_alphanumeric_alphabet()
    {
        var t = PairingToken.Create(SendspinPsk.NewRandom(), SendspinPsk.NewRandom());
        Assert.Matches("^[0-9A-Z:]+$", t);
        Assert.DoesNotContain('2', t[4..]);
    }

    [Fact]
    public void Base64url_round_trips_without_padding()
    {
        var data = Range(0, 33);
        var text = Base64Url.Encode(data);
        Assert.DoesNotContain('=', text);
        Assert.DoesNotContain('+', text);
        Assert.Equal(data, Base64Url.Decode(text));
        Assert.True(Base64Url.TryDecode(Base64Url.Encode(Range(0, 32)), 32, out _));
        Assert.False(Base64Url.TryDecode("short", 32, out _));
        Assert.False(Base64Url.TryDecode("a", 32, out _));
    }

    [Fact]
    public void Key_store_evicts_oldest_and_replaces_per_server()
    {
        var store = new InMemoryKeyStore();
        for (var i = 0; i < KeyStoreExtensions.MaxRecords + 3; i++)
        {
            store.SaveRecord(new PairingRecord("srv" + i, SendspinPsk.NewRandom()));
        }

        Assert.Equal(KeyStoreExtensions.MaxRecords, store.Records.Count);
        Assert.DoesNotContain(store.Records, r => r.ServerId == "srv0");

        var psk = SendspinPsk.NewRandom();
        store.SaveRecord(new PairingRecord("srv10", psk));
        Assert.Single(store.Records, r => r.ServerId == "srv10");
        Assert.Equal("srv10", store.FindRecordByPskId(SendspinPsk.Id(psk))!.ServerId);
        Assert.Null(store.FindRecordByPskId("nope"));
    }
}
