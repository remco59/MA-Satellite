using System.Text.Json;

namespace MaSatellite.Core.Protocol;

/// <summary>A parsed server → client JSON message. Unknown types are surfaced so callers can ignore them.</summary>
public abstract record ServerMessage;

public sealed record ServerHelloMessage(ServerHello Hello) : ServerMessage;

public sealed record ServerTimeMessage(ServerTime Time) : ServerMessage;

public sealed record ServerStateMessage(JsonElement? Metadata, JsonElement? Controller) : ServerMessage;

public sealed record ServerCommandMessage(ServerCommand Command) : ServerMessage;

public sealed record StreamStartMessage(StreamStart Start) : ServerMessage;

public sealed record StreamClearMessage(StreamClear Clear) : ServerMessage;

public sealed record StreamEndMessage(StreamEnd End) : ServerMessage;

public sealed record GroupUpdateMessage(JsonElement Delta) : ServerMessage;

public sealed record UnknownServerMessage(string Type) : ServerMessage;

public static class MessageCodec
{
    public static string Serialize<T>(string type, T payload) =>
        JsonSerializer.Serialize(new Envelope<T>(type, payload), WireJson.Options);

    public static string ClientHello(ClientHello hello) => Serialize("client/hello", hello);

    public static string ClientTime(long clientTransmitted) => Serialize("client/time", new ClientTime(clientTransmitted));

    public static string ClientState(ClientState state) => Serialize("client/state", state);

    public static string ClientCommand(ControllerCommand command) => Serialize("client/command", new ClientCommand(command));

    public static string ClientGoodbye(string reason) => Serialize("client/goodbye", new ClientGoodbye(reason));

    public static string StreamRequestFormat(StreamRequestFormat request) => Serialize("stream/request-format", request);

    /// <summary>Parses one text frame. Throws <see cref="FormatException"/> for frames that are not valid Sendspin envelopes.</summary>
    public static ServerMessage ParseServerMessage(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json);
        }
        catch (JsonException ex)
        {
            throw new FormatException("Invalid JSON in Sendspin message.", ex);
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out var typeEl)
                || typeEl.ValueKind != JsonValueKind.String)
            {
                throw new FormatException("Sendspin message has no type.");
            }

            var type = typeEl.GetString()!;
            var payload = root.TryGetProperty("payload", out var p) && p.ValueKind == JsonValueKind.Object ? p.Clone() : default;
            if (payload.ValueKind != JsonValueKind.Object)
            {
                payload = JsonDocument.Parse("{}").RootElement.Clone();
            }

            try
            {
                return type switch
                {
                    "server/hello" => new ServerHelloMessage(new ServerHello(
                        Str(payload, "server_id") ?? string.Empty,
                        Str(payload, "name") ?? string.Empty,
                        Int(payload, "version") ?? 1,
                        StrArray(payload, "active_roles") ?? Array.Empty<string>(),
                        Str(payload, "connection_reason"))),
                    "server/time" => new ServerTimeMessage(new ServerTime(
                        Long(payload, "client_transmitted") ?? 0,
                        Long(payload, "server_received") ?? 0,
                        Long(payload, "server_transmitted") ?? 0)),
                    "server/state" => new ServerStateMessage(Obj(payload, "metadata"), Obj(payload, "controller")),
                    "server/command" => new ServerCommandMessage(ParseServerCommand(payload)),
                    "stream/start" => new StreamStartMessage(ParseStreamStart(payload)),
                    "stream/clear" => new StreamClearMessage(new StreamClear(StrArray(payload, "roles"))),
                    "stream/end" => new StreamEndMessage(new StreamEnd(StrArray(payload, "roles"))),
                    "group/update" => new GroupUpdateMessage(payload),
                    _ => new UnknownServerMessage(type),
                };
            }
            catch (Exception ex) when (ex is InvalidOperationException or KeyNotFoundException or FormatException)
            {
                throw new FormatException($"Malformed '{type}' message.", ex);
            }
        }
    }

    private static ServerCommand ParseServerCommand(JsonElement payload)
    {
        var player = Obj(payload, "player");
        if (player is not { } p)
        {
            return new ServerCommand(null);
        }

        return new ServerCommand(new PlayerCommand(
            Str(p, "command") ?? string.Empty,
            Int(p, "volume"),
            Bool(p, "mute"),
            Int(p, "static_delay_ms")));
    }

    private static StreamStart ParseStreamStart(JsonElement payload)
    {
        StreamPlayerFormat? player = null;
        if (Obj(payload, "player") is { } p)
        {
            player = new StreamPlayerFormat(
                Str(p, "codec") ?? string.Empty,
                Int(p, "sample_rate") ?? 0,
                Int(p, "channels") ?? 0,
                Int(p, "bit_depth") ?? 0,
                Str(p, "codec_header"));
        }

        List<StreamArtworkChannel>? channels = null;
        if (Obj(payload, "artwork") is { } a && a.TryGetProperty("channels", out var arr) && arr.ValueKind == JsonValueKind.Array)
        {
            channels = arr.EnumerateArray()
                .Select(c => new StreamArtworkChannel(Str(c, "source") ?? "none", Str(c, "format") ?? string.Empty, Int(c, "width") ?? 0, Int(c, "height") ?? 0))
                .ToList();
        }

        return new StreamStart(player, channels);
    }

    private static JsonElement? Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static long? Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var i) ? i : null;

    private static bool? Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static string[]? StrArray(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()!).ToArray()
            : null;

    private sealed record Envelope<T>(string Type, T Payload);
}
