using MaSatellite.Host.Settings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Graphics;

namespace MaSatellite.App.Views;

public sealed partial class SettingsWindow : Window
{
    private readonly SettingsStore _store;
    private readonly Func<bool, Task> _applyAutostart;

    public SettingsWindow(SettingsStore store, Func<bool, Task> applyAutostart)
    {
        _store = store;
        _applyAutostart = applyAutostart;
        InitializeComponent();
        Title = "ma-satellite settings";
        AppWindow.Resize(new SizeInt32(520, 640));
        Populate(store.Current);
    }

    private void Populate(AppSettings s)
    {
        PlayerNameBox.Text = s.PlayerName;
        HostBox.Text = s.MaHost;
        ModeBox.SelectedIndex = s.ConnectionMode == ConnectionMode.Advertise ? 0 : 1;
        ListenPortBox.Value = s.ListenPort;
        DelayBox.Value = s.StaticDelayMs;
        AutostartSwitch.IsOn = s.Autostart;
    }

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        var updated = _store.Current with
        {
            PlayerName = PlayerNameBox.Text.Trim(),
            MaHost = HostBox.Text.Trim(),
            ConnectionMode = ModeBox.SelectedIndex == 1 ? ConnectionMode.Connect : ConnectionMode.Advertise,
            ListenPort = ToInt(ListenPortBox.Value, AppSettings.DefaultListenPort),
            StaticDelayMs = ToInt(DelayBox.Value, 0),
            Autostart = AutostartSwitch.IsOn,
        };

        var errors = _store.Save(updated);
        if (errors.Count > 0)
        {
            MessageBar.Severity = InfoBarSeverity.Error;
            MessageBar.Title = "Not saved";
            MessageBar.Message = string.Join(Environment.NewLine, errors);
            MessageBar.IsOpen = true;
            return;
        }

        await _applyAutostart(updated.Autostart);
        MessageBar.Severity = InfoBarSeverity.Success;
        MessageBar.Title = "Saved";
        MessageBar.Message = "Settings saved.";
        MessageBar.IsOpen = true;
    }

    private static int ToInt(double value, int fallback) => double.IsNaN(value) ? fallback : (int)Math.Round(value);
}
