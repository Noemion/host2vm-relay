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
    public bool EnableUdp { get; set; } = true;
    public string Rules { get; set; } = "# 每行填写一个域名、IP 或网段";
    public string TestUrl { get; set; } = "https://example.com/";
    public Dictionary<string, string> HostKeys { get; set; } = new();

    public static readonly string DefaultFolder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Host2VMRelay");
    // Diagnostic processes override Folder without reading the real bootstrap file.
    public static string Folder = DefaultFolder;
    private static string LegacyFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Host2VMRelay");
    public static SettingsLocation Location => new(Path.Combine(LegacyFolder, "storage.json"), DefaultFolder);

    public static void InitializeLocation() => Folder = Location.Resolve();
    public static Settings Load()
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, "settings.json");
        if (!File.Exists(path) && SettingsLocation.SameFolder(Folder, DefaultFolder))
        {
            var legacy = Path.Combine(LegacyFolder, "settings.json");
            if (File.Exists(legacy)) File.Copy(legacy, path, false);
        }
        if (!File.Exists(path)) return new();
        return JsonSerializer.Deserialize<Settings>(File.ReadAllText(path))
            ?? throw new IOException("settings.json 内容为空，未自动重置配置：" + path);
    }

    internal string Serialize() => JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    public void Save() => SettingsLocation.AtomicWrite(Path.Combine(Folder, "settings.json"), Serialize(), true);
    public void ChangeFolder(string target, bool replaceExisting = false)
    {
        Folder = Location.Relocate(Folder, target, this, replaceExisting);
    }
}
