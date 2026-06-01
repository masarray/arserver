using System.IO;
using System.Text.Json;
using Ari61850Bridge.Models;

namespace Ari61850Bridge.Services;

public static class ProjectStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true
    };

    public static string DefaultFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "ArServer");
    public static string DefaultProjectPath => Path.Combine(DefaultFolder, "bridge-project.json");

    public static async Task SaveAsync(BridgeProject project, string? path = null)
    {
        Directory.CreateDirectory(DefaultFolder);
        var json = JsonSerializer.Serialize(project, Options);
        await File.WriteAllTextAsync(path ?? DefaultProjectPath, json);
    }

    public static async Task<BridgeProject?> LoadAsync(string? path = null)
    {
        var file = path ?? DefaultProjectPath;
        if (!File.Exists(file)) return null;
        var json = await File.ReadAllTextAsync(file);
        return JsonSerializer.Deserialize<BridgeProject>(json, Options);
    }
}
