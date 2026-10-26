using MaSatellite.Core.Session;
using MaSatellite.Host.Infrastructure;

namespace MaSatellite.Host.Orchestration;

/// <summary>Persists the "last played server" id across restarts (needed for multi-server arbitration).</summary>
public sealed class FileLastServerStore : ILastServerStore
{
    private readonly string _path;
    private string? _value;

    public FileLastServerStore(AppPaths paths)
    {
        _path = Path.Combine(paths.Root, "last-server.txt");
        try
        {
            _value = File.Exists(_path) ? File.ReadAllText(_path).Trim() : null;
            if (string.IsNullOrEmpty(_value))
            {
                _value = null;
            }
        }
        catch (IOException)
        {
            _value = null;
        }
    }

    public string? LastPlayedServerId
    {
        get => _value;
        set
        {
            if (_value == value)
            {
                return;
            }

            _value = value;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
                File.WriteAllText(_path, value ?? string.Empty);
            }
            catch (IOException)
            {
                // best effort; arbitration still works for this run
            }
        }
    }
}
