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
    public static string Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Host2VMRelay");
    public static Settings Load()
    {
        var p = Path.Combine(Folder, "settings.json");
        // Migrate the preview's settings without changing the DPAPI entropy.
        var defaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Host2VMRelay");
        var legacy = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "KylinTunnel", "settings.json");
        if (Folder == defaultFolder && !File.Exists(p) && File.Exists(legacy)) {
            Directory.CreateDirectory(Folder); File.Copy(legacy, p, false);
        }
        return File.Exists(p) ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(p)) ?? new() : new();
    }
    public void Save()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "settings.json");
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
}
