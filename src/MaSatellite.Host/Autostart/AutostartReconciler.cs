using Microsoft.Extensions.Logging;

namespace MaSatellite.Host.Autostart;

/// <summary>Brings the OS startup task in line with the user's autostart setting.</summary>
public sealed class AutostartReconciler
{
    private readonly IAutostartService _service;
    private readonly ILogger<AutostartReconciler> _logger;

    public AutostartReconciler(IAutostartService service, ILogger<AutostartReconciler> logger)
    {
        _service = service;
        _logger = logger;
    }

    public async Task<AutostartState> ReconcileAsync(bool desired, CancellationToken cancellationToken = default)
    {
        var state = await _service.GetStateAsync(cancellationToken).ConfigureAwait(false);
        if (!state.CanBeChangedByApp() || state.IsEnabled() == desired)
        {
            if (!state.CanBeChangedByApp())
            {
                _logger.LogInformation("Autostart is {State}; the app cannot change it.", state);
            }

            return state;
        }

        var result = await _service.SetEnabledAsync(desired, cancellationToken).ConfigureAwait(false);
        _logger.LogInformation("Autostart changed {From} -> {To} (wanted {Desired}).", state, result, desired);
        return result;
    }
}
