using MaSatellite.Host.Infrastructure;
using MaSatellite.Host.Settings;
using MaSatellite.MaApi;
using Microsoft.Extensions.Logging;

namespace MaSatellite.Host.Library;

public enum MaConnectionState
{
    /// <summary>Not connected (yet); the next request connects.</summary>
    Idle,
    Connecting,
    Connected,

    /// <summary>No host or no token configured, or the token was rejected.</summary>
    NeedsSetup,
    Failed,
}

/// <summary>
/// Owns the connection to Music Assistant's own API (library browsing). It connects lazily when the UI needs it,
/// reconnects after a drop, and drops the connection when the host or token changes.
/// </summary>
public sealed class MaLibraryService : IAsyncDisposable
{
    private readonly SettingsStore _settings;
    private readonly ISecretStore _secrets;
    private readonly ILogger<MaLibraryService> _logger;
    private readonly SemaphoreSlim _connectLock = new(1, 1);
    private MaClient? _client;
    private string? _clientKey;
    private bool _disposed;

    public MaLibraryService(SettingsStore settings, ISecretStore secrets, ILogger<MaLibraryService> logger)
    {
        _settings = settings;
        _secrets = secrets;
        _logger = logger;
        _settings.Changed += OnSettingsChanged;
    }

    public MaConnectionState State { get; private set; } = MaConnectionState.Idle;

    /// <summary>Human-readable reason for <see cref="MaConnectionState.NeedsSetup"/> or <see cref="MaConnectionState.Failed"/>.</summary>
    public string? Problem { get; private set; }

    public string? ServerVersion { get; private set; }

    public event Action? StateChanged;

    /// <summary>Raised for every event the server pushes (queue updates etc.).</summary>
    public event Action<MaEvent>? ServerEvent;

    public bool HasToken => !string.IsNullOrWhiteSpace(_secrets.Get(SecretNames.MaToken));

    public void SaveToken(string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            _secrets.Delete(SecretNames.MaToken);
        }
        else
        {
            _secrets.Set(SecretNames.MaToken, token.Trim());
        }

        _ = DropClientAsync();
    }

    /// <summary>Connects (or returns the live connection). Throws <see cref="MaException"/> with a user-presentable message.</summary>
    public async Task<MaClient> GetClientAsync(CancellationToken ct = default)
    {
        await _connectLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var s = _settings.Current;
            if (string.IsNullOrWhiteSpace(s.MaHost))
            {
                Fail(MaConnectionState.NeedsSetup, "Enter the Music Assistant host in the settings.");
                throw new MaException(Problem!);
            }

            var token = _secrets.Get(SecretNames.MaToken);
            var key = $"{s.MaHost}:{s.MaWebPort}:{token?.GetHashCode()}";
            if (_client is { IsConnected: true } existing && key == _clientKey)
            {
                return existing;
            }

            await DropClientCoreAsync().ConfigureAwait(false);
            SetState(MaConnectionState.Connecting, null);
            var client = new MaClient(s.MaHost, s.MaWebPort, token, _logger);
            try
            {
                await client.ConnectAsync(ct).ConfigureAwait(false);
            }
            catch (MaAuthException ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                Fail(MaConnectionState.NeedsSetup, ex.Message);
                throw;
            }
            catch (MaException ex)
            {
                await client.DisposeAsync().ConfigureAwait(false);
                Fail(MaConnectionState.Failed, ex.Message);
                throw;
            }

            client.EventReceived += e => ServerEvent?.Invoke(e);
            client.Disconnected += _ =>
            {
                if (ReferenceEquals(_client, client))
                {
                    SetState(MaConnectionState.Idle, "Connection to Music Assistant lost.");
                }
            };
            _client = client;
            _clientKey = key;
            ServerVersion = client.ServerInfo?.ServerVersion;
            SetState(MaConnectionState.Connected, null);
            _logger.LogInformation("Connected to Music Assistant {Version}", ServerVersion);
            return client;
        }
        finally
        {
            _connectLock.Release();
        }
    }

    /// <summary>Tries a connection with the given values without touching saved settings; the saved token is used when none is given. Returns the server version.</summary>
    public async Task<string> TestAsync(string host, int port, string? tokenOverride, CancellationToken ct = default)
    {
        var token = string.IsNullOrWhiteSpace(tokenOverride) ? _secrets.Get(SecretNames.MaToken) : tokenOverride.Trim();
        return await TestConnectionAsync(host, port, token, ct).ConfigureAwait(false);
    }

    /// <summary>Tries a connection with explicit values. Returns the server version.</summary>
    public static async Task<string> TestConnectionAsync(string host, int port, string? token, CancellationToken ct = default)
    {
        await using var client = new MaClient(host, port, token);
        await client.ConnectAsync(ct).ConfigureAwait(false);
        return client.ServerInfo?.ServerVersion ?? "unknown";
    }

    public async ValueTask DisposeAsync()
    {
        _settings.Changed -= OnSettingsChanged;
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            _disposed = true;
            await DropClientCoreAsync().ConfigureAwait(false);
        }
        finally
        {
            _connectLock.Release();
        }

        _connectLock.Dispose();
    }

    private void OnSettingsChanged(object? sender, AppSettings s) => _ = DropClientAsync();

    private async Task DropClientAsync()
    {
        await _connectLock.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed)
            {
                return;
            }

            await DropClientCoreAsync().ConfigureAwait(false);
            SetState(MaConnectionState.Idle, null);
        }
        finally
        {
            _connectLock.Release();
        }
    }

    private async Task DropClientCoreAsync()
    {
        var client = _client;
        _client = null;
        _clientKey = null;
        if (client is not null)
        {
            await client.DisposeAsync().ConfigureAwait(false);
        }
    }

    private void Fail(MaConnectionState state, string problem) => SetState(state, problem);

    private void SetState(MaConnectionState state, string? problem)
    {
        State = state;
        Problem = problem;
        StateChanged?.Invoke();
    }
}
