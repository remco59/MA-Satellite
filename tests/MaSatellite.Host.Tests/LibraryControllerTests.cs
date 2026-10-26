using MaSatellite.Host.Infrastructure;
using MaSatellite.Host.Library;
using MaSatellite.Host.Settings;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;
using MaSatellite.MaApi.Tests;
using Microsoft.Extensions.Logging.Abstractions;

namespace MaSatellite.Host.Tests;

public class LibraryControllerTests
{
    private const string ClientId = "11111111-1111-1111-1111-111111111111";

    private sealed class Rig : IAsyncDisposable
    {
        private readonly TempDir _dir = new();

        public Rig(FakeMaServer server, string? token = "secret")
        {
            Server = server;
            Secrets = new InMemorySecretStore();
            if (token is not null)
            {
                Secrets.Set(SecretNames.MaToken, token);
            }

            Settings = new SettingsStore(new AppPaths(_dir.Path), NullLogger<SettingsStore>.Instance);
            Settings.Load();
            Settings.Save(Settings.Current with { ClientId = ClientId, PlayerName = "Desk PC", MaHost = "127.0.0.1", MaWebPort = server.Port });
            Service = new MaLibraryService(Settings, Secrets, NullLogger<MaLibraryService>.Instance);
            Controller = new LibraryController(Service, Settings);
        }

        public FakeMaServer Server { get; }

        public InMemorySecretStore Secrets { get; }

        public SettingsStore Settings { get; }

        public MaLibraryService Service { get; }

        public LibraryController Controller { get; }

        public async ValueTask DisposeAsync()
        {
            await Service.DisposeAsync();
            await Server.DisposeAsync();
            _dir.Dispose();
        }
    }

    private static FakeMaServer ServerWithPlayers()
    {
        var server = new FakeMaServer();
        server.Handlers["players/all"] = _ => new object[]
        {
            new { player_id = "zeta", name = "Zeta speaker", available = true, playback_state = "idle", type = "player" },
            new { player_id = "sendspin-" + ClientId, name = "Desk PC", available = true, playback_state = "playing", type = "player" },
            new { player_id = "alpha", name = "Alpha speaker", available = true, playback_state = "idle", type = "player" },
            new { player_id = "off", name = "Unplugged", available = false, playback_state = "idle", type = "player" },
        };
        server.Handlers["player_queues/play_media"] = _ => null;
        server.Handlers["player_queues/transfer"] = _ => null;
        return server;
    }

    [Fact]
    public async Task Targets_list_this_pc_first_then_available_players_alphabetically()
    {
        await using var rig = new Rig(ServerWithPlayers());

        var targets = await rig.Controller.GetTargetsAsync();

        Assert.Equal(["This PC (Desk PC)", "Alpha speaker", "Zeta speaker"], targets.Select(t => t.Name));
        Assert.True(targets[0].IsThisPc);
        Assert.Equal("sendspin-" + ClientId, targets[0].QueueId);
        Assert.Equal(MaConnectionState.Connected, rig.Service.State);
        Assert.Equal("2.10.8", rig.Service.ServerVersion);
    }

    [Fact]
    public async Task Playing_an_album_on_another_player_targets_its_queue()
    {
        var server = ServerWithPlayers();
        await using var rig = new Rig(server);
        var targets = await rig.Controller.GetTargetsAsync();
        var item = new MaItem(MaMediaType.Album, "12", "library", "Kid A", "library://album/12", "Radiohead", null, 2000, null, null);

        await rig.Controller.PlayAsync(item, targets[1], MaQueueOption.Replace);

        var args = server.Received.Last(r => r.Command == "player_queues/play_media").Args;
        Assert.Equal("alpha", args.GetProperty("queue_id").GetString());
        Assert.Equal("library://album/12", args.GetProperty("media").GetString());
        Assert.Equal("replace", args.GetProperty("option").GetString());
    }

    [Fact]
    public async Task Unplayable_items_are_rejected_locally()
    {
        await using var rig = new Rig(ServerWithPlayers());
        var targets = await rig.Controller.GetTargetsAsync();
        var item = new MaItem(MaMediaType.Track, "1", "x", "Gone", "", null, null, null, null, null, IsPlayable: false);
        await Assert.ThrowsAsync<MaException>(() => rig.Controller.PlayAsync(item, targets[0], MaQueueOption.Play));
    }

    [Fact]
    public async Task Transfer_moves_the_queue_and_refuses_the_same_player()
    {
        var server = ServerWithPlayers();
        await using var rig = new Rig(server);
        var targets = await rig.Controller.GetTargetsAsync();

        await rig.Controller.TransferAsync(targets[1], targets[0]);

        var args = server.Received.Last(r => r.Command == "player_queues/transfer").Args;
        Assert.Equal("alpha", args.GetProperty("source_queue_id").GetString());
        Assert.Equal("sendspin-" + ClientId, args.GetProperty("target_queue_id").GetString());
        Assert.True(args.GetProperty("auto_play").GetBoolean());
        await Assert.ThrowsAsync<MaException>(() => rig.Controller.TransferAsync(targets[0], targets[0]));
    }

    [Fact]
    public async Task Queue_view_loads_items_only_when_the_queue_has_some()
    {
        var server = ServerWithPlayers();
        server.Handlers["player_queues/all"] = _ => new object[]
        {
            new { queue_id = "alpha", display_name = "Alpha speaker", active = true, available = true, items = 2, state = "playing" },
            new { queue_id = "zeta", display_name = "Zeta speaker", active = false, available = true, items = 0, state = "idle" },
        };
        server.Handlers["player_queues/items"] = _ => new object[]
        {
            new { queue_id = "alpha", queue_item_id = "q1", name = "One", duration = 100, index = 0 },
            new { queue_id = "alpha", queue_item_id = "q2", name = "Two", duration = 200, index = 1 },
        };
        await using var rig = new Rig(server);
        var targets = await rig.Controller.GetTargetsAsync();

        var alpha = await rig.Controller.GetQueueAsync(targets.Single(t => t.QueueId == "alpha"));
        Assert.Equal(["One", "Two"], alpha.Items.Select(i => i.Name));
        Assert.Equal("playing", alpha.Queue!.State);

        var zeta = await rig.Controller.GetQueueAsync(targets.Single(t => t.QueueId == "zeta"));
        Assert.Empty(zeta.Items);
        Assert.DoesNotContain(server.Received, r => r.Command == "player_queues/items" && r.Args.GetProperty("queue_id").GetString() == "zeta");
    }

    [Fact]
    public async Task Missing_token_or_host_needs_setup_with_a_helpful_message()
    {
        await using var noToken = new Rig(ServerWithPlayers(), token: null);
        var ex = await Assert.ThrowsAsync<MaAuthException>(() => noToken.Controller.GetTargetsAsync());
        Assert.Contains("token", ex.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(MaConnectionState.NeedsSetup, noToken.Service.State);

        await using var noHost = new Rig(ServerWithPlayers());
        noHost.Settings.Save(noHost.Settings.Current with { MaHost = "" });
        await Assert.ThrowsAsync<MaException>(() => noHost.Controller.GetTargetsAsync());
        Assert.Equal(MaConnectionState.NeedsSetup, noHost.Service.State);
        Assert.Contains("host", noHost.Service.Problem!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Saving_a_new_token_reconnects_with_it()
    {
        var server = ServerWithPlayers();
        await using var rig = new Rig(server, token: "wrong");
        await Assert.ThrowsAsync<MaAuthException>(() => rig.Controller.GetTargetsAsync());

        rig.Service.SaveToken("  secret  ");
        var targets = await rig.Controller.GetTargetsAsync();

        Assert.NotEmpty(targets);
        Assert.Equal("secret", rig.Secrets.Get(SecretNames.MaToken));
        Assert.Equal(MaConnectionState.Connected, rig.Service.State);
    }

    [Fact]
    public async Task Changing_the_host_setting_drops_the_old_connection()
    {
        var server = ServerWithPlayers();
        await using var rig = new Rig(server);
        await rig.Controller.GetTargetsAsync();
        Assert.Equal(MaConnectionState.Connected, rig.Service.State);

        rig.Settings.Save(rig.Settings.Current with { MaHost = "localhost" });

        var end = Environment.TickCount64 + 3000;
        while (rig.Service.State != MaConnectionState.Idle && Environment.TickCount64 < end)
        {
            await Task.Delay(20);
        }

        Assert.Equal(MaConnectionState.Idle, rig.Service.State);
    }

    [Fact]
    public async Task Test_connection_reports_the_server_version_without_saving_anything()
    {
        await using var server = ServerWithPlayers();
        var version = await MaLibraryService.TestConnectionAsync("127.0.0.1", server.Port, "secret");
        Assert.Equal("2.10.8", version);
        await Assert.ThrowsAsync<MaAuthException>(() => MaLibraryService.TestConnectionAsync("127.0.0.1", server.Port, "bad"));
    }
}
