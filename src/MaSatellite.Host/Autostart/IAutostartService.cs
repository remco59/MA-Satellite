namespace MaSatellite.Host.Autostart;

public enum AutostartState
{
    /// <summary>Not running as a packaged app, so no startup task exists.</summary>
    Unavailable,
    Enabled,
    Disabled,

    /// <summary>The user turned it off in Task Manager / Settings; only the user can turn it back on.</summary>
    DisabledByUser,
    DisabledByPolicy,
}

public interface IAutostartService
{
    Task<AutostartState> GetStateAsync(CancellationToken cancellationToken = default);

    /// <summary>Requests the desired state and returns the resulting one.</summary>
    Task<AutostartState> SetEnabledAsync(bool enabled, CancellationToken cancellationToken = default);
}

public static class AutostartStateExtensions
{
    public static bool IsEnabled(this AutostartState state) => state == AutostartState.Enabled;

    public static bool CanBeChangedByApp(this AutostartState state) =>
        state is AutostartState.Enabled or AutostartState.Disabled;
}
