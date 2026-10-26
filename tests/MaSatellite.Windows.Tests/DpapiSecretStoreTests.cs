using System.Text;
using MaSatellite.Host.Infrastructure;
using MaSatellite.Windows.Security;

namespace MaSatellite.Windows.Tests;

public class DpapiSecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "masat-dpapi-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        GC.SuppressFinalize(this);
        try
        {
            Directory.Delete(_root, true);
        }
        catch (IOException)
        {
        }
    }

    private DpapiSecretStore NewStore() => new(new AppPaths(_root));

    [Fact]
    public void Round_trips_and_is_not_stored_in_plain_text()
    {
        var store = NewStore();
        store.Set(SecretNames.MaToken, "eyJhbGciOi-super-secret-token");

        Assert.Equal("eyJhbGciOi-super-secret-token", NewStore().Get(SecretNames.MaToken));

        var raw = File.ReadAllBytes(Path.Combine(_root, "secrets", "ma-token.bin"));
        Assert.DoesNotContain("super-secret", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("super-secret", Encoding.Unicode.GetString(raw));
    }

    [Fact]
    public void Missing_secret_is_null_and_delete_is_idempotent()
    {
        var store = NewStore();
        Assert.Null(store.Get("nope"));
        store.Set("a", "1");
        store.Delete("a");
        store.Delete("a");
        Assert.Null(store.Get("a"));
    }

    [Fact]
    public void Corrupted_file_yields_null_instead_of_throwing()
    {
        var store = NewStore();
        store.Set("a", "1");
        File.WriteAllBytes(Path.Combine(_root, "secrets", "a.bin"), [1, 2, 3, 4]);
        Assert.Null(store.Get("a"));
    }

    [Fact]
    public void Overwrite_replaces_the_value()
    {
        var store = NewStore();
        store.Set("a", "one");
        store.Set("a", "two");
        Assert.Equal("two", store.Get("a"));
    }

    [Theory]
    [InlineData("../evil")]
    [InlineData("a/b")]
    [InlineData("a.b")]
    public void Names_cannot_escape_the_secrets_folder(string name)
    {
        Assert.Throws<ArgumentException>(() => NewStore().Set(name, "x"));
    }
}
