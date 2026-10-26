using MaSatellite.Core.Security;
using MaSatellite.Host.Infrastructure;
using MaSatellite.Host.Security;

namespace MaSatellite.Host.Tests;

public class SecretKeyStoreTests
{
    [Fact]
    public void A_fresh_store_creates_and_persists_an_identity_and_pairing_psk()
    {
        var secrets = new InMemorySecretStore();
        var first = new SecretKeyStore(secrets);

        Assert.Equal(32, first.Identity.PublicKey.Length);
        Assert.Equal(32, first.PairingPsk.Length);
        Assert.NotNull(secrets.Get(SecretNames.SendspinKeys));

        var second = new SecretKeyStore(secrets);
        Assert.Equal(first.Identity.PublicKey, second.Identity.PublicKey);
        Assert.Equal(first.Identity.PrivateKey, second.Identity.PrivateKey);
        Assert.Equal(first.PairingPsk, second.PairingPsk);
        Assert.Equal(first.ClientId(), second.ClientId());
    }

    [Fact]
    public void Records_survive_a_restart_and_can_be_found_by_psk_id()
    {
        var secrets = new InMemorySecretStore();
        var store = new SecretKeyStore(secrets);
        var psk = SendspinPsk.NewRandom();
        store.SaveRecord(new PairingRecord("server-a", psk));

        var reloaded = new SecretKeyStore(secrets);
        var record = Assert.Single(reloaded.Records);
        Assert.Equal("server-a", record.ServerId);
        Assert.Equal(psk, record.LongTermPsk);
        Assert.Equal("server-a", reloaded.FindRecordByPskId(SendspinPsk.Id(psk))!.ServerId);
        Assert.Null(reloaded.FindRecordByPskId(SendspinPsk.Id(SendspinPsk.NewRandom())));
    }

    [Fact]
    public void Saving_again_replaces_the_record_and_removal_is_persisted()
    {
        var secrets = new InMemorySecretStore();
        var store = new SecretKeyStore(secrets);
        store.SaveRecord(new PairingRecord("s", SendspinPsk.NewRandom()));
        var newer = SendspinPsk.NewRandom();
        store.SaveRecord(new PairingRecord("s", newer));
        Assert.Equal(newer, Assert.Single(store.Records).LongTermPsk);

        store.RemoveRecord("s");
        Assert.Empty(new SecretKeyStore(secrets).Records);
        store.RemoveRecord("unknown"); // harmless
    }

    [Fact]
    public void The_oldest_record_is_evicted_beyond_the_limit()
    {
        var store = new SecretKeyStore(new InMemorySecretStore());
        for (var i = 0; i < KeyStoreExtensions.MaxRecords + 3; i++)
        {
            store.SaveRecord(new PairingRecord($"s{i}", SendspinPsk.NewRandom()));
        }

        Assert.Equal(KeyStoreExtensions.MaxRecords, store.Records.Count);
        Assert.DoesNotContain(store.Records, r => r.ServerId == "s0");
        Assert.Contains(store.Records, r => r.ServerId == $"s{KeyStoreExtensions.MaxRecords + 2}");
    }

    [Fact]
    public void Regenerating_the_pairing_psk_changes_the_token_and_is_persisted()
    {
        var secrets = new InMemorySecretStore();
        var store = new SecretKeyStore(secrets);
        var oldToken = store.PairingToken();
        store.RegeneratePairingPsk();

        Assert.NotEqual(oldToken, store.PairingToken());
        Assert.Equal(store.PairingToken(), new SecretKeyStore(secrets).PairingToken());
        Assert.True(PairingToken.TryParse(store.PairingToken(), out var clientKey, out var psk));
        Assert.Equal(store.Identity.PublicKey, clientKey);
        Assert.Equal(store.PairingPsk, psk);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("{}")]
    [InlineData("{\"privateKey\":\"AAAA\",\"pairingPsk\":\"AAAA\"}")]
    public void Unreadable_secrets_yield_a_fresh_identity_instead_of_failing(string stored)
    {
        var secrets = new InMemorySecretStore();
        secrets.Set(SecretNames.SendspinKeys, stored);

        var store = new SecretKeyStore(secrets);

        Assert.Equal(32, store.Identity.PublicKey.Length);
        Assert.NotEqual(stored, secrets.Get(SecretNames.SendspinKeys));
        Assert.Equal(store.Identity.PublicKey, new SecretKeyStore(secrets).Identity.PublicKey);
    }
}
