using System.Text.Json;

namespace TaskbarResourceMonitor;

internal sealed class SettingsStore
{
    private const string AppFolderName = "TaskbarResourceMonitor";
    private const string FileName = "settings.json";

    private sealed record Settings(bool AlwaysOnTop, string[] Drives);

    public bool AlwaysOnTop { get; set; } = true;
    public string[] Drives { get; set; } = ["C:\\"];

    public void Load()
    {
        var path = GetPath();
        if (!File.Exists(path)) return;

        try
        {
            var json = File.ReadAllText(path);
            var s = JsonSerializer.Deserialize<Settings>(json);
            if (s is null) return;
            AlwaysOnTop = s.AlwaysOnTop;
            Drives = (s.Drives is { Length: > 0 }) ? s.Drives : ["C:\\"];
        }
        catch
        {
            // ignore corrupt settings
        }
    }

    public void Save()
    {
        var path = GetPath();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var json = JsonSerializer.Serialize(new Settings(AlwaysOnTop, Drives), new JsonSerializerOptions
            {
                WriteIndented = true
            });
            File.WriteAllText(path, json);
        }
        catch
        {
            // best effort
        }
    }

    private static string GetPath()
    {
        var baseDir = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(baseDir, AppFolderName, FileName);
    }
}

