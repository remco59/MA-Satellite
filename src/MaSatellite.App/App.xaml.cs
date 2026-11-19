using MaSatellite.App.Shell;
using MaSatellite.Host.Infrastructure;
using MaSatellite.Windows.Tray;
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
            // The primary instance may still be creating its hidden tray window if both launches race.
            // Retry briefly so a rapid second launch is not lost during startup.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                if (TrayIcon.RequestPrimaryActivation())
                {
                    break;
                }

                Thread.Sleep(50);
            }

            Exit();
            return;
        }

        _shell = new SatelliteShell(() => Exit());
        _shell.Start();
    }
}
