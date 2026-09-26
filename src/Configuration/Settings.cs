using System.Text.Json;

namespace Host2VMRelay;

public sealed class Settings
{
    public string Host { get; set; } = "192.168.229.10";
    public int Port { get; set; } = 22;
    public string User { get; set; } = "";
    public int SocksPort { get; set; } = 1080;
    public string KeyPath { get; set; } = "";
    public bool UseKey { get; set; }
    public bool RememberSecret { get; set; } = true;
    public string ProtectedSecret { get; set; } = "";
    public bool Reconnect { get; set; } = true;
    public string Rules { get; set; } = "# 每行填写一个域名、IP 或网段";
    public string TestUrl { get; set; } = "https://example.com/";
    public Dictionary<string, string> HostKeys { get; set; } = new();

    private static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Host2VMRelay");
    public static string Folder = DefaultFolder;
    private static string LegacyFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Host2VMRelay");

    public static Settings Load()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "settings.json");
        // Only the real default profile is eligible for migration, never diagnostic overrides.
        if (!File.Exists(path) && string.Equals(Path.GetFullPath(Folder), Path.GetFullPath(DefaultFolder), StringComparison.OrdinalIgnoreCase))
        {
            var legacy = Path.Combine(LegacyFolder, "settings.json");
            if (File.Exists(legacy)) File.Copy(legacy, path, false);
        }
        return File.Exists(path) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new() : new();
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
