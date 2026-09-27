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
        return Deserialize(File.ReadAllText(path));
    }

    internal static Settings Deserialize(string content)
    {
        var settings = JsonSerializer.Deserialize<Settings>(content)
            ?? throw new IOException("settings.json 内容为空，未自动重置配置。");
        settings.Validate();
        return settings;
    }
    private void Validate()
    {
        // Fail before binding controls or publishing a profile. Never silently reset
        // an invalid port or trust store, which could select a different endpoint.
        if (Port is < 1 or > 65535 || SocksPort is < 1024 or > 65535)
            throw new IOException("settings.json 的 SSH/SOCKS 端口超出有效范围，请修正配置后重试。");
        if (Host is null || User is null || KeyPath is null || ProtectedSecret is null || Rules is null || TestUrl is null ||
            HostKeys is null || HostKeys.Any(pair => string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)))
            throw new IOException("settings.json 包含无效的空字段或主机指纹，请修正配置后重试；原文件未修改。");
    }
    internal string Serialize()
    {
        Validate();
        return JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
    }
    public void Save() => SettingsLocation.AtomicWrite(Path.Combine(Folder, "settings.json"), Serialize(), true);
    /// <summary>Persist a detached candidate before publishing it to readers of the active profile.</summary>
    public Settings SaveUpdated(Action<Settings> update)
    {
        var candidate = (Settings)MemberwiseClone();
        candidate.HostKeys = new Dictionary<string, string>(HostKeys);
        update(candidate);
        candidate.Save();
        return candidate;
    }
    public void ChangeFolder(string target, bool replaceExisting = false)
    {
        Folder = Location.Relocate(Folder, target, this, replaceExisting);
    }
}
