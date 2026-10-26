using MaSatellite.Core.Protocol;

namespace MaSatellite.Core.Session;

/// <summary>How strongly a connection claims the player. Higher ranks displace lower ones.</summary>
public enum AdmissionRank
{
    /// <summary>Connected for discovery only (legacy <c>connection_reason: discovery</c>, or empty <c>activities</c>).</summary>
    None = 0,

    /// <summary>A pairing attempt is running (<c>activities</c> contains <c>pairing</c>).</summary>
    Pairing = 1,

    /// <summary>The server needs the player for playback.</summary>
    Playback = 2,
}

/// <summary>What the service needs to decide whether this server may hold the (single) connection.</summary>
public sealed record ServerAdmission(string ServerId, string Name, AdmissionRank Rank);

/// <summary>Multi-server rules from the spec: one server at a time, highest-ranked activity wins.</summary>
public static class ArbitrationPolicy
{
    /// <summary>True when <paramref name="incoming"/> should replace <paramref name="existing"/>; false to keep the existing connection.</summary>
    public static bool ShouldReplace(ServerAdmission existing, ServerAdmission incoming, string? lastPlayedServerId)
    {
        if (existing.Rank == AdmissionRank.Pairing)
        {
            return false; // a pairing attempt is not displaced
        }

        if (incoming.Rank != existing.Rank)
        {
            return incoming.Rank > existing.Rank;
        }

        return incoming.Rank switch
        {
            AdmissionRank.Playback => true, // higher or equal priority is accepted
            AdmissionRank.Pairing => false,
            _ => incoming.ServerId == lastPlayedServerId && existing.ServerId != lastPlayedServerId,
        };
    }

    public static AdmissionRank FromLegacyReason(string? connectionReason) =>
        connectionReason == ConnectionReasons.Playback ? AdmissionRank.Playback : AdmissionRank.None;
}

/// <summary>One server connection, whichever protocol it speaks.</summary>
public interface ISendspinSession : IAsyncDisposable
{
    SessionState State { get; }

    string Remote { get; }

    /// <summary>Called once the server has identified itself; return false to decline (we then say goodbye).</summary>
    Func<ServerAdmission, Task<bool>>? AcceptServerAsync { get; set; }

    event Action<SessionState>? StateChanged;

    /// <summary>Raised when the server changed our delay setting; the host should persist it.</summary>
    event Action<int>? StaticDelayChanged;

    /// <summary>Raised when the server's answer shows it does not speak the protocol we used (the reason is for logs and display).</summary>
    event Action<string>? ProtocolMismatch;

    Task RunAsync(CancellationToken cancellationToken);

    Task SendCommandAsync(ControllerCommand command, CancellationToken cancellationToken = default);

    Task SetStaticDelayAsync(int delayMs, CancellationToken cancellationToken = default);

    Task DisconnectAsync(string reason);
}
