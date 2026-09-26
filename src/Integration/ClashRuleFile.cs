namespace Host2VMRelay;

public static class ClashRuleFile
{
    public const string DisabledPayload = "DOMAIN,disabled.host2vm-relay.invalid\n";

    public static string Folder = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "io.github.clash-verge-rev.clash-verge-rev",
        "rules");

    public static string FilePath => Path.Combine(Folder, "host2vm-relay-rules.txt");

    public static void Write(string payload)
    {
        Directory.CreateDirectory(Folder);
        var content = string.IsNullOrWhiteSpace(payload) ? DisabledPayload : payload.Replace("\r\n", "\n");
        if (!content.EndsWith('\n')) content += "\n";
        if (File.Exists(FilePath) && File.ReadAllText(FilePath) == content) return;

        var temp = FilePath + ".tmp";
        File.WriteAllText(temp, content, new System.Text.UTF8Encoding(false));
        File.Move(temp, FilePath, true);
    }

    public static void Disable() => Write(DisabledPayload);
}
