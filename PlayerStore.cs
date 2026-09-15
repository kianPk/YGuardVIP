using System.Collections.Concurrent;
using System.Text.Json;

namespace YGuardVIP;

public class PlayerVipSettings
{
    public bool TagEnabled { get; set; } = true;
    public string SmokeColor { get; set; } = "red";
}

public class PlayerStore
{
    private readonly string _path;
    private readonly ConcurrentDictionary<ulong, PlayerVipSettings> _data = new();
    private readonly object _ioLock = new();

    public PlayerStore(string path)
    {
        _path = path;
        Load();
    }

    public PlayerVipSettings Get(ulong steamId, YGuardVipConfig config)
    {
        return _data.GetOrAdd(steamId, _ => new PlayerVipSettings
        {
            TagEnabled = config.DefaultTagEnabled,
            SmokeColor = config.DefaultSmokeColor
        });
    }

    public void Save()
    {
        lock (_ioLock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var json = JsonSerializer.Serialize(_data, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json);
        }
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
                return;

            var json = File.ReadAllText(_path);
            var loaded = JsonSerializer.Deserialize<Dictionary<ulong, PlayerVipSettings>>(json);
            if (loaded == null)
                return;

            foreach (var (id, settings) in loaded)
                _data[id] = settings;
        }
        catch
        {
            // ignore corrupt store; starts empty
        }
    }
}
