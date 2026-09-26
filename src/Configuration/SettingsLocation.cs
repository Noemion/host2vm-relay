using System.Text;
using System.Text.Json;

namespace Host2VMRelay;

/// <summary>A fixed bootstrap file locates the movable profile; it contains no credentials.</summary>
public sealed class SettingsLocation
{
    private sealed class Pointer
    {
        public Pointer() { }
        public int Schema { get; set; } = 1;
        public string Folder { get; set; } = "";
    }
    public string PointerFile { get; }
    public string DefaultFolder { get; }
    public SettingsLocation(string pointerFile, string defaultFolder)
    {
        PointerFile = Path.GetFullPath(pointerFile);
        DefaultFolder = NormalizeFolder(defaultFolder);
    }
    public static string NormalizeFolder(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Path.IsPathFullyQualified(folder))
            throw new ArgumentException("配置目录必须是完整的绝对路径。");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(folder));
    }
    public static bool SameFolder(string first, string second) => string.Equals(NormalizeFolder(first), NormalizeFolder(second),
        OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    public string Resolve()
    {
        if (!File.Exists(PointerFile)) return DefaultFolder;
        if (new FileInfo(PointerFile).Length > 16384) throw new IOException("配置目录定位文件过大，请检查：" + PointerFile);
        Pointer pointer;
        try { pointer = JsonSerializer.Deserialize<Pointer>(File.ReadAllText(PointerFile)) ?? throw new JsonException("Empty pointer"); }
        catch (JsonException ex) { throw new IOException("配置目录定位文件损坏，未自动切换到空白配置：" + PointerFile, ex); }
        if (pointer.Schema != 1) throw new IOException("不支持的配置目录定位文件版本。");
        string folder = NormalizeFolder(pointer.Folder);
        if (!Directory.Exists(folder) || !File.Exists(Path.Combine(folder, "settings.json")))
            throw new IOException("所选配置目录不可用或 settings.json 缺失，请恢复目录后重试：" + folder);
        return folder;
    }

    public string Relocate(string currentFolder, string targetFolder, Settings savedSettings, bool replaceExisting = false)
    {
        ArgumentNullException.ThrowIfNull(savedSettings);
        string target = NormalizeFolder(targetFolder);
        if (SameFolder(currentFolder, target)) return target;
        string path = Path.Combine(target, "settings.json");
        Directory.CreateDirectory(target);
        if (File.Exists(path) && !replaceExisting)
            throw new IOException("目标目录已有 settings.json，未覆盖。请选择其他目录，或明确确认先备份再替换。");
        string content = savedSettings.Serialize();
        // Never delete the original profile. A replaced destination receives a backup.
        if (File.Exists(path))
        {
            string backup = path + ".backup-" + DateTime.UtcNow.ToString("yyyyMMddHHmmss") + "-" + Guid.NewGuid().ToString("N");
            File.Copy(path, backup, false);
        }
        AtomicWrite(path, content, replaceExisting);
        _ = JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? throw new IOException("目标配置校验失败。");
        var pointer = new Pointer { Folder = target };
        AtomicWrite(PointerFile, JsonSerializer.Serialize(pointer, new JsonSerializerOptions { WriteIndented = true }), true);
        return target;
    }

    internal static void AtomicWrite(string path, string content, bool overwrite)
    {
        path = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                byte[] bytes = new UTF8Encoding(false).GetBytes(content);
                file.Write(bytes); file.Flush(true);
            }
            File.Move(temporary, path, overwrite);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
