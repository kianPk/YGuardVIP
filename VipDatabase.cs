using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace YGuardVIP;

public class VipGrant
{
    [JsonPropertyName("steamId")]
    public ulong SteamId { get; set; }

    /// <summary>null = permanent</summary>
    [JsonPropertyName("expiresAtUtc")]
    public DateTime? ExpiresAtUtc { get; set; }

    [JsonPropertyName("grantedAtUtc")]
    public DateTime GrantedAtUtc { get; set; } = DateTime.UtcNow;

    [JsonPropertyName("grantedBy")]
    public string GrantedBy { get; set; } = "";
}

/// <summary>
/// Persistent VIP grants (JSON "database") — survives map/server restarts.
/// Stored under configs/plugins/YGuardVIP so plugin DLL updates don't wipe it.
/// </summary>
public class VipDatabase
{
    private readonly string _path;
    private readonly ConcurrentDictionary<ulong, VipGrant> _grants = new();
    private readonly object _ioLock = new();

    public VipDatabase(string path)
    {
        _path = path;
        Load();
    }

    public bool IsActive(ulong steamId)
    {
        if (!_grants.TryGetValue(steamId, out var g))
            return false;

        if (g.ExpiresAtUtc == null)
            return true;

        return g.ExpiresAtUtc > DateTime.UtcNow;
    }

    public VipGrant? Get(ulong steamId)
        => _grants.TryGetValue(steamId, out var g) && IsActive(steamId) ? g : null;

    public IReadOnlyCollection<VipGrant> ListActive()
    {
        PurgeExpired();
        return _grants.Values.Where(g => g.ExpiresAtUtc == null || g.ExpiresAtUtc > DateTime.UtcNow).ToList();
    }

    public IReadOnlyList<VipGrant> PurgeExpired()
    {
        var removed = new List<VipGrant>();
        foreach (var (id, g) in _grants)
        {
            if (g.ExpiresAtUtc != null && g.ExpiresAtUtc <= DateTime.UtcNow)
            {
                if (_grants.TryRemove(id, out var dead))
                    removed.Add(dead);
            }
        }

        if (removed.Count > 0)
            Save();

        return removed;
    }

    public VipGrant Grant(ulong steamId, TimeSpan? duration, string grantedBy)
    {
        DateTime? expires;
        if (!duration.HasValue)
        {
            expires = null;
        }
        else if (_grants.TryGetValue(steamId, out var existing)
                 && existing.ExpiresAtUtc is DateTime prev
                 && prev > DateTime.UtcNow)
        {
            // Stack remaining time when the player already has active VIP.
            expires = prev.Add(duration.Value);
        }
        else
        {
            expires = DateTime.UtcNow.Add(duration.Value);
        }

        var grant = new VipGrant
        {
            SteamId = steamId,
            GrantedAtUtc = DateTime.UtcNow,
            GrantedBy = grantedBy,
            ExpiresAtUtc = expires
        };
        _grants[steamId] = grant;
        Save();
        return grant;
    }

    public bool Revoke(ulong steamId)
    {
        var ok = _grants.TryRemove(steamId, out _);
        if (ok) Save();
        return ok;
    }

    public string FormatRemaining(ulong steamId)
    {
        if (!_grants.TryGetValue(steamId, out var g))
            return "none";
        if (g.ExpiresAtUtc == null)
            return "permanent";
        var left = g.ExpiresAtUtc.Value - DateTime.UtcNow;
        if (left <= TimeSpan.Zero)
            return "expired";
        if (left.TotalDays >= 1)
            return $"{(int)left.TotalDays}d {left.Hours}h";
        if (left.TotalHours >= 1)
            return $"{(int)left.TotalHours}h {left.Minutes}m";
        return $"{(int)left.TotalMinutes}m";
    }

    /// <summary>
    /// Parse duration: 30m, 12h, 7d, 2w, 1mo, perm / permanent / 0
    /// </summary>
    public static bool TryParseDuration(string input, out TimeSpan? duration, out string error)
    {
        duration = null;
        error = "";
        if (string.IsNullOrWhiteSpace(input))
        {
            error = "missing duration (e.g. 7d, 12h, 30d, perm)";
            return false;
        }

        var s = input.Trim().ToLowerInvariant();
        if (s is "perm" or "permanent" or "0" or "lifetime" or "forever")
        {
            duration = null;
            return true;
        }

        var m = Regex.Match(s, @"^(\d+)\s*(m|min|mins|h|hr|hrs|d|day|days|w|week|weeks|mo|month|months)$");
        if (!m.Success)
        {
            error = "invalid duration — use 30m, 12h, 7d, 2w, 1mo, or perm";
            return false;
        }

        var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        if (n <= 0)
        {
            error = "duration must be > 0 (or use perm)";
            return false;
        }

        var unit = m.Groups[2].Value;
        duration = unit switch
        {
            "m" or "min" or "mins" => TimeSpan.FromMinutes(n),
            "h" or "hr" or "hrs" => TimeSpan.FromHours(n),
            "d" or "day" or "days" => TimeSpan.FromDays(n),
            "w" or "week" or "weeks" => TimeSpan.FromDays(7 * n),
            "mo" or "month" or "months" => TimeSpan.FromDays(30 * n),
            _ => null
        };

        if (duration == null)
        {
            error = "invalid unit";
            return false;
        }

        return true;
    }

    public void Save()
    {
        lock (_ioLock)
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            var list = _grants.Values.OrderBy(g => g.SteamId).ToList();
            var json = JsonSerializer.Serialize(list, new JsonSerializerOptions { WriteIndented = true });
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
            var loaded = JsonSerializer.Deserialize<List<VipGrant>>(json);
            if (loaded == null) return;

            foreach (var g in loaded)
            {
                if (g.SteamId == 0) continue;
                _grants[g.SteamId] = g;
            }
        }
        catch
        {
            // corrupt file → empty
        }
    }
}
