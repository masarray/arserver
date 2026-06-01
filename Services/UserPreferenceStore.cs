using System.IO;
using System.Text.Json;

namespace Ari61850Bridge.Services;

public static class UserPreferenceStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "ArServer");
    public static string PreferencesPath => Path.Combine(DefaultFolder, "user-preferences.json");

    // Despite the legacy method name, this now returns successful relay endpoints only.
    // Old v0.13-v0.16 files are read for compatibility, but new writes use SuccessfulRelays.
    public static IReadOnlyList<string> LoadRecentRelayIps()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return Array.Empty<string>();
            var json = File.ReadAllText(PreferencesPath);
            var prefs = JsonSerializer.Deserialize<UserPreferences>(json, Options) ?? new UserPreferences();

            var successful = prefs.SuccessfulRelays
                .Where(r => !string.IsNullOrWhiteSpace(r.IpAddress))
                .OrderByDescending(r => r.LastConnectedUtc)
                .Select(r => r.IpAddress.Trim());

            var legacy = prefs.RecentRelayIps
                .Where(ip => !string.IsNullOrWhiteSpace(ip))
                .Select(ip => ip.Trim());

            return successful
                .Concat(legacy)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(12)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    public static async Task SaveRecentRelayIpsAsync(IEnumerable<string> ips)
    {
        Directory.CreateDirectory(DefaultFolder);
        var now = DateTime.UtcNow;
        var distinctIps = ips
            .Where(ip => !string.IsNullOrWhiteSpace(ip))
            .Select(ip => ip.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToList();

        var prefs = LoadPreferences();
        foreach (var ip in distinctIps)
        {
            var item = prefs.SuccessfulRelays.FirstOrDefault(r => string.Equals(r.IpAddress, ip, StringComparison.OrdinalIgnoreCase));
            if (item == null)
            {
                prefs.SuccessfulRelays.Add(new SuccessfulRelayEndpoint
                {
                    IpAddress = ip,
                    MmsPort = 102,
                    LastConnectedUtc = now,
                    SuccessCount = 1
                });
            }
            else
            {
                item.LastConnectedUtc = now;
                item.SuccessCount = Math.Max(1, item.SuccessCount + 1);
            }
        }

        prefs.RecentRelayIps.Clear();
        prefs.SuccessfulRelays = prefs.SuccessfulRelays
            .Where(r => !string.IsNullOrWhiteSpace(r.IpAddress))
            .GroupBy(r => r.IpAddress.Trim(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.OrderByDescending(r => r.LastConnectedUtc).First())
            .OrderByDescending(r => r.LastConnectedUtc)
            .Take(12)
            .ToList();

        var json = JsonSerializer.Serialize(prefs, Options);
        await File.WriteAllTextAsync(PreferencesPath, json);
    }

    private static UserPreferences LoadPreferences()
    {
        try
        {
            if (!File.Exists(PreferencesPath)) return new UserPreferences();
            var json = File.ReadAllText(PreferencesPath);
            return JsonSerializer.Deserialize<UserPreferences>(json, Options) ?? new UserPreferences();
        }
        catch
        {
            return new UserPreferences();
        }
    }

    private sealed class UserPreferences
    {
        public List<string> RecentRelayIps { get; set; } = new(); // legacy compatibility only
        public List<SuccessfulRelayEndpoint> SuccessfulRelays { get; set; } = new();
    }

    private sealed class SuccessfulRelayEndpoint
    {
        public string IpAddress { get; set; } = "";
        public int MmsPort { get; set; } = 102;
        public string IedName { get; set; } = "";
        public DateTime LastConnectedUtc { get; set; } = DateTime.UtcNow;
        public int SuccessCount { get; set; } = 1;
    }
}
