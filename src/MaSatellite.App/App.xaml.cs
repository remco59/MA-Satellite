using System.Threading;
using MaSatellite.App.Shell;
using MaSatellite.Host.Infrastructure;
using Microsoft.UI.Xaml;

namespace MaSatellite.App;

public partial class App : Application
{
    private SingleInstanceGuard? _instance;
    private SatelliteShell? _shell;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _instance = SingleInstanceGuard.Acquire(@"Local\MaSatellite.SingleInstance");
        if (!_instance.IsPrimary)
        {
            // Ask the running process to reveal its full UI before this short-lived
            // process exits. The named event is shared across processes in this session.
            _instance.SignalActivation();
            _instance.Dispose();
            Exit();
            return;
        }

        _shell = new SatelliteShell(() => Exit());
        _shell.Start();
        StartActivationListener(_instance);
    }

    private void StartActivationListener(SingleInstanceGuard instance)
    {
        _ = Task.Run(() =>
        {
            while (instance.WaitForActivation(CancellationToken.None))
            {
                _shell?.ActivateMainWindow();
            }
        });
    }
}
