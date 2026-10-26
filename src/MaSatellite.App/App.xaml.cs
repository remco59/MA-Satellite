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
            Exit();
            return;
        }

        _shell = new SatelliteShell(() => Exit());
        _shell.Start();
    }
}
