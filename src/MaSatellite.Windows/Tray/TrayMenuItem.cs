namespace MaSatellite.Windows.Tray;

/// <summary>One entry of the tray context menu. A separator has <see cref="Id"/> 0 and no text.</summary>
public sealed record TrayMenuItem(int Id, string Text, bool Checked = false, bool Enabled = true)
{
    public static TrayMenuItem Separator { get; } = new(0, string.Empty);

    public bool IsSeparator => Id == 0;
}
