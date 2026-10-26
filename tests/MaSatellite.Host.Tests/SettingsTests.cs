using MaSatellite.Host.Infrastructure;
using MaSatellite.Host.Settings;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Host.Tests;

public class SettingsTests
{
    private static SettingsStore NewStore(TempDir dir) =>
        new(new AppPaths(dir.Path), NullLogger<SettingsStore>.Instance);

    [Fact]
    public void Load_creates_defaults_when_missing()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);

        var settings = store.Load();

        Assert.True(settings.Autostart);
        Assert.Equal(ConnectionMode.Advertise, settings.ConnectionMode);
        Assert.True(File.Exists(Path.Combine(dir.Path, "settings.json")));
    }

    [Fact]
    public void Save_then_load_round_trips_and_keeps_client_id()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var initial = store.Load();

        var errors = store.Save(initial with { PlayerName = "Desk", MaHost = "192.168.0.98", Autostart = false, StaticDelayMs = 120 });
        Assert.Empty(errors);

        var reloaded = NewStore(dir).Load();
        Assert.Equal("Desk", reloaded.PlayerName);
        Assert.Equal("192.168.0.98", reloaded.MaHost);
        Assert.False(reloaded.Autostart);
        Assert.Equal(120, reloaded.StaticDelayMs);
        Assert.Equal(initial.ClientId, reloaded.ClientId);
    }

    [Fact]
    public void Protocol_settings_round_trip_as_readable_text()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var initial = store.Load();
        Assert.Equal(MaSatellite.Core.Session.SendspinProtocolMode.Auto, initial.ProtocolMode);
        Assert.True(initial.AllowUnpairedAccess);

        Assert.Empty(store.Save(initial with { ProtocolMode = MaSatellite.Core.Session.SendspinProtocolMode.Encrypted, AllowUnpairedAccess = false }));

        var reloaded = NewStore(dir).Load();
        Assert.Equal(MaSatellite.Core.Session.SendspinProtocolMode.Encrypted, reloaded.ProtocolMode);
        Assert.False(reloaded.AllowUnpairedAccess);
        Assert.Contains("\"Encrypted\"", File.ReadAllText(Path.Combine(dir.Path, "settings.json")));
    }

    [Fact]
    public void Save_rejects_invalid_and_does_not_write()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var initial = store.Load();

        var errors = store.Save(initial with { ListenPort = 70000, PlayerName = " " });

        Assert.Equal(2, errors.Count);
        Assert.Equal(initial, store.Current);
    }

    [Fact]
    public void Load_recovers_from_corrupt_file_with_backup()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), "{ not json");

        var settings = NewStore(dir).Load();

        Assert.Equal(ConnectionMode.Advertise, settings.ConnectionMode);
        Assert.True(File.Exists(Path.Combine(dir.Path, "settings.json.bad")));
    }

    [Fact]
    public void Load_preserves_client_id_when_other_values_are_invalid()
    {
        using var dir = new TempDir();
        var id = Guid.NewGuid().ToString("D");
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"), $"{{\"clientId\":\"{id}\",\"listenPort\":0}}");

        var settings = NewStore(dir).Load();

        Assert.Equal(id, settings.ClientId);
        Assert.Equal(AppSettings.DefaultListenPort, settings.ListenPort);
    }

    [Fact]
    public void Load_accepts_comments_and_string_enums()
    {
        using var dir = new TempDir();
        File.WriteAllText(Path.Combine(dir.Path, "settings.json"),
            "{ // comment\n \"connectionMode\": \"Connect\", \"maHost\": \"ma.local\" }");

        var settings = NewStore(dir).Load();

        Assert.Equal(ConnectionMode.Connect, settings.ConnectionMode);
        Assert.Equal("ma.local", settings.MaHost);
    }

    [Theory]
    [InlineData("http://192.168.0.98")]
    [InlineData("192.168.0.98/foo")]
    [InlineData("bad host name")]
    public void Validator_rejects_malformed_host(string host)
    {
        var errors = SettingsValidator.Validate(new AppSettings { MaHost = host });
        Assert.NotEmpty(errors);
    }

    [Fact]
    public void Validator_requires_host_in_connect_mode()
    {
        var errors = SettingsValidator.Validate(new AppSettings { ConnectionMode = ConnectionMode.Connect });
        Assert.Contains(errors, e => e.Contains("host is required", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Changed_event_fires_on_valid_save()
    {
        using var dir = new TempDir();
        var store = NewStore(dir);
        var initial = store.Load();
        AppSettings? seen = null;
        store.Changed += (_, s) => seen = s;

        store.Save(initial with { PlayerName = "Kitchen" });

        Assert.Equal("Kitchen", seen?.PlayerName);
    }
}
