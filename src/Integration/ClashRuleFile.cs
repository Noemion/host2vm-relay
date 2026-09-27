namespace Host2VMRelay;

public static class ClashRuleFile
{
    public const string DisabledPayload = "DOMAIN,disabled.host2vm-relay.invalid\n";
    public static readonly string DefaultFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "io.github.clash-verge-rev.clash-verge-rev", "rules");
    public static string Folder = DefaultFolder;
    public static string FilePath => Path.Combine(Folder, "host2vm-relay-rules.txt");
    public static string UdpFilePath => Path.Combine(Folder, "host2vm-relay-udp-rules.txt");
    public static bool Write(string payload) => WritePath(FilePath, payload);
    public static bool WriteUdp(string payload) => WritePath(UdpFilePath, payload);
    private static bool WritePath(string path, string payload)
    {
        Directory.CreateDirectory(Folder);
        string content = string.IsNullOrWhiteSpace(payload) ? DisabledPayload : payload.Replace("\r\n", "\n");
        if (!content.EndsWith('\n')) content += "\n";
        if (File.Exists(path) && File.ReadAllText(path) == content) return false;
        SettingsLocation.AtomicWrite(path, content, true);
        return true;
    }
    public static void Disable() { Write(DisabledPayload); WriteUdp(DisabledPayload); }
}
