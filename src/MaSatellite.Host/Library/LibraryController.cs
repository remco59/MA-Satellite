using MaSatellite.Host.Settings;
using MaSatellite.MaApi;
using MaSatellite.MaApi.Models;

namespace MaSatellite.Host.Library;

/// <summary>A player (queue) that media can be played on.</summary>
public sealed record PlayTarget(string QueueId, string Name, bool IsThisPc, bool Available, string State);

public sealed record QueueView(PlayTarget Target, MaQueueInfo? Queue, IReadOnlyList<MaQueueItem> Items);

/// <summary>What the library window does, without any UI: targets, playing items, queue view and transfer.</summary>
public sealed class LibraryController
{
    private readonly MaLibraryService _service;
    private readonly SettingsStore _settings;

    public LibraryController(MaLibraryService service, SettingsStore settings)
    {
        _service = service;
        _settings = settings;
    }

    public MaLibraryService Service => _service;

    /// <summary>All players, with this PC first. A player that is not (yet) known to MA still appears as "This PC".</summary>
    public async Task<IReadOnlyList<PlayTarget>> GetTargetsAsync(CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        var players = await client.GetPlayersAsync(ct).ConfigureAwait(false);
        var s = _settings.Current;
        var me = MaClient.FindThisPlayer(players, s.ClientId, s.PlayerName);

        var targets = new List<PlayTarget>();
        if (me is not null)
        {
            targets.Add(new PlayTarget(me.PlayerId, "This PC (" + me.Name + ")", true, me.Available, me.PlaybackState));
        }

        targets.AddRange(players
            .Where(p => p.PlayerId != me?.PlayerId && p.Available)
            .OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new PlayTarget(p.PlayerId, p.Name, false, p.Available, p.PlaybackState)));
        return targets;
    }

    public async Task PlayAsync(MaItem item, PlayTarget target, MaQueueOption option, CancellationToken ct = default)
    {
        if (!item.IsPlayable || string.IsNullOrEmpty(item.Uri))
        {
            throw new MaException($"'{item.Name}' cannot be played.");
        }

        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.PlayMediaAsync(target.QueueId, item.Uri, option, ct).ConfigureAwait(false);
    }

    public async Task<QueueView> GetQueueAsync(PlayTarget target, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        var queues = await client.GetQueuesAsync(ct).ConfigureAwait(false);
        var queue = queues.FirstOrDefault(q => q.QueueId == target.QueueId);
        var items = queue is { ItemCount: > 0 }
            ? await client.GetQueueItemsAsync(target.QueueId, 300, 0, ct).ConfigureAwait(false)
            : [];
        return new QueueView(target, queue, items);
    }

    /// <summary>Moves the queue of <paramref name="from"/> to <paramref name="to"/> and continues playing there.</summary>
    public async Task TransferAsync(PlayTarget from, PlayTarget to, CancellationToken ct = default)
    {
        if (from.QueueId == to.QueueId)
        {
            throw new MaException("Choose a different player to transfer the queue to.");
        }

        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.TransferQueueAsync(from.QueueId, to.QueueId, autoPlay: true, ct).ConfigureAwait(false);
    }

    public async Task QueueControlAsync(PlayTarget target, string command, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.QueueCommandAsync(target.QueueId, command, ct).ConfigureAwait(false);
    }

    public async Task PlayQueueIndexAsync(PlayTarget target, int index, CancellationToken ct = default)
    {
        var client = await _service.GetClientAsync(ct).ConfigureAwait(false);
        await client.PlayQueueIndexAsync(target.QueueId, index, ct).ConfigureAwait(false);
    }
}
