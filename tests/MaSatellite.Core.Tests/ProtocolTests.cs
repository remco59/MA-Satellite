using System.Text.Json;
using MaSatellite.Core.Protocol;

namespace MaSatellite.Core.Tests;

public class ProtocolTests
{
    [Fact]
    public void Client_hello_serializes_with_wire_names()
    {
        var hello = new ClientHello(
            "id-1", "Desk", new DeviceInfo("ma-satellite", null, "0.1.0"), 1,
            [Roles.Player, Roles.Controller],
            new PlayerSupport([new AudioFormatSpec("flac", 2, 48000, 16)], 4_000_000, ["volume", "mute"]),
            null);

        using var doc = JsonDocument.Parse(MessageCodec.ClientHello(hello));
        var root = doc.RootElement;
        Assert.Equal("client/hello", root.GetProperty("type").GetString());
        var p = root.GetProperty("payload");
        Assert.Equal("id-1", p.GetProperty("client_id").GetString());
        Assert.Equal(1, p.GetProperty("version").GetInt32());
        Assert.Equal("player@v1", p.GetProperty("supported_roles")[0].GetString());
        var support = p.GetProperty("player@v1_support");
        Assert.Equal(4_000_000, support.GetProperty("buffer_capacity").GetInt32());
        var fmt = support.GetProperty("supported_formats")[0];
        Assert.Equal("flac", fmt.GetProperty("codec").GetString());
        Assert.Equal(48000, fmt.GetProperty("sample_rate").GetInt32());
        Assert.Equal(16, fmt.GetProperty("bit_depth").GetInt32());
        Assert.False(p.TryGetProperty("artwork@v1_support", out _)); // null omitted
        Assert.False(p.GetProperty("device_info").TryGetProperty("manufacturer", out _));
    }

    [Fact]
    public void Client_state_and_command_shapes()
    {
        using var s = JsonDocument.Parse(MessageCodec.ClientState(new ClientState(ClientStates.Synchronized,
            new PlayerStatePayload(40, false, 0, 250, 1000, null))));
        var player = s.RootElement.GetProperty("payload").GetProperty("player");
        Assert.Equal(40, player.GetProperty("volume").GetInt32());
        Assert.False(player.GetProperty("muted").GetBoolean());
        Assert.Equal(250, player.GetProperty("required_lead_time_ms").GetInt32());

        using var c = JsonDocument.Parse(MessageCodec.ClientCommand(new ControllerCommand("seek", PositionMs: 5000)));
        var cmd = c.RootElement.GetProperty("payload").GetProperty("controller");
        Assert.Equal("seek", cmd.GetProperty("command").GetString());
        Assert.Equal(5000, cmd.GetProperty("position_ms").GetInt64());
        Assert.False(cmd.TryGetProperty("volume", out _));
    }

    [Fact]
    public void Parses_server_hello()
    {
        var m = MessageCodec.ParseServerMessage("""
            {"type":"server/hello","payload":{"server_id":"ma","name":"Music Assistant","version":1,"active_roles":["player@v1"],"connection_reason":"playback"}}
            """);
        var hello = Assert.IsType<ServerHelloMessage>(m).Hello;
        Assert.Equal("ma", hello.ServerId);
        Assert.Equal(ConnectionReasons.Playback, hello.ConnectionReason);
        Assert.Equal(["player@v1"], hello.ActiveRoles);
    }

    [Fact]
    public void Parses_server_time_with_large_values()
    {
        var m = MessageCodec.ParseServerMessage("""
            {"type":"server/time","payload":{"client_transmitted":9007199254740993,"server_received":1700000000123456,"server_transmitted":1700000000123999}}
            """);
        var t = Assert.IsType<ServerTimeMessage>(m).Time;
        Assert.Equal(9007199254740993, t.ClientTransmitted);
        Assert.Equal(1700000000123999, t.ServerTransmitted);
    }

    [Fact]
    public void Parses_stream_start_with_flac_header_and_artwork()
    {
        var m = MessageCodec.ParseServerMessage("""
            {"type":"stream/start","payload":{"player":{"codec":"flac","sample_rate":44100,"channels":2,"bit_depth":16,"codec_header":"ZkxhQw=="},
             "artwork":{"channels":[{"source":"album","format":"jpeg","width":512,"height":512}]}}}
            """);
        var start = Assert.IsType<StreamStartMessage>(m).Start;
        Assert.Equal("flac", start.Player!.Codec);
        Assert.Equal("ZkxhQw==", start.Player.CodecHeader);
        Assert.Single(start.ArtworkChannels!);
        Assert.Equal(512, start.ArtworkChannels![0].Width);
    }

    [Fact]
    public void Parses_server_command_player()
    {
        var m = MessageCodec.ParseServerMessage("""{"type":"server/command","payload":{"player":{"command":"volume","volume":33}}}""");
        var cmd = Assert.IsType<ServerCommandMessage>(m).Command.Player!;
        Assert.Equal("volume", cmd.Command);
        Assert.Equal(33, cmd.Volume);
    }

    [Fact]
    public void Unknown_types_are_surfaced_not_thrown()
    {
        var m = MessageCodec.ParseServerMessage("""{"type":"future/thing","payload":{"x":1}}""");
        Assert.Equal("future/thing", Assert.IsType<UnknownServerMessage>(m).Type);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("""{"payload":{}}""")]
    [InlineData("""{"type":5}""")]
    public void Malformed_frames_throw_format_exception(string json)
    {
        Assert.Throws<FormatException>(() => MessageCodec.ParseServerMessage(json));
    }

    [Fact]
    public void Metadata_merge_handles_null_clear_and_progress()
    {
        var state = new MetadataState();
        using var d1 = JsonDocument.Parse("""{"timestamp":1000000,"title":"A","artist":"B","album":"C","year":1999,"progress":{"track_progress":1000,"track_duration":200000,"playback_speed":1000}}""");
        state = MetadataState.Merge(state, d1.RootElement);
        Assert.Equal("A", state.Title);
        Assert.Equal(1999, state.Year);
        Assert.Equal(200000, state.Progress!.TrackDurationMs);

        using var d2 = JsonDocument.Parse("""{"title":"New","album":null,"year":null}""");
        state = MetadataState.Merge(state, d2.RootElement);
        Assert.Equal("New", state.Title);
        Assert.Equal("B", state.Artist);
        Assert.Null(state.Album);
        Assert.Null(state.Year);
        Assert.NotNull(state.Progress);

        using var d3 = JsonDocument.Parse("""{"progress":null}""");
        Assert.Null(MetadataState.Merge(state, d3.RootElement).Progress);
    }

    [Fact]
    public void Position_follows_spec_formula_and_clamps()
    {
        var state = new MetadataState
        {
            Timestamp = 10_000_000,
            Progress = new TrackProgress(1000, 5000, 1000),
        };

        Assert.Equal(1000, state.PositionMs(10_000_000));
        Assert.Equal(3000, state.PositionMs(12_000_000));
        Assert.Equal(5000, state.PositionMs(60_000_000)); // clamped at duration
        Assert.Equal(1000, (state with { Progress = state.Progress with { PlaybackSpeed = 0 } }).PositionMs(99_000_000)); // paused
        Assert.Equal(0, state.PositionMs(0)); // before timestamp clamps at 0
        Assert.Null(new MetadataState().PositionMs(1));
    }

    [Fact]
    public void Controller_and_group_merge()
    {
        using var c = JsonDocument.Parse("""{"supported_commands":["play","pause","seek"],"volume":42,"muted":true,"repeat":"all","shuffle":true,"seek_max_ms":180000}""");
        var cs = ControllerState.Merge(new ControllerState(), c.RootElement);
        Assert.True(cs.Supports("seek"));
        Assert.Equal(42, cs.Volume);
        Assert.Equal(RepeatMode.All, cs.Repeat);
        Assert.Equal(180000, cs.SeekMaxMs);

        using var g = JsonDocument.Parse("""{"playback_state":"playing","group_id":"g1","group_name":"Living"}""");
        var gs = GroupState.Merge(new GroupState(), g.RootElement);
        Assert.Equal(GroupPlaybackState.Playing, gs.PlaybackState);
        using var g2 = JsonDocument.Parse("""{"group_name":null}""");
        Assert.Null(GroupState.Merge(gs, g2.RootElement).GroupName);
    }

    [Fact]
    public void Binary_frame_parsing()
    {
        var data = new byte[] { 4, 0, 0, 0, 0, 0, 0, 1, 0, 0xAA, 0xBB };
        Assert.True(BinaryFrame.TryParse(data, out var f));
        Assert.Equal(BinaryMessageTypes.Audio, f.Type);
        Assert.Equal(256, f.TimestampMicros);
        Assert.Equal([0xAA, 0xBB], f.Payload.ToArray());
        Assert.False(BinaryFrame.TryParse(new byte[4], out _));

        Assert.True(BinaryFrame.TryParse(new byte[] { 10, 0, 0, 0, 0, 0, 0, 0, 1 }, out var art));
        Assert.Equal(2, art.ArtworkChannel);
        Assert.Empty(art.Payload.ToArray());
    }
}
