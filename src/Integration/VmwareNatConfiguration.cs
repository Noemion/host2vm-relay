using System.Text;
using System.Text.RegularExpressions;
using System.Diagnostics;

namespace Host2VMRelay;

/// <summary>Repairs NAT link propagation only in a powered-off VMware configuration.</summary>
internal sealed class VmwareNatConfiguration
{
    private const string Key = "vmnat.linkStatePropagation.disable";
    private static readonly Regex Setting = new(@"^\s*vmnat\.linkStatePropagation\.disable\s*=\s*""(TRUE|FALSE)""\s*(?:#.*)?$", RegexOptions.IgnoreCase);
    private static readonly Regex Nat = new(@"^\s*ethernet\d+\.connectionType\s*=\s*""nat""\s*(?:#.*)?$", RegexOptions.IgnoreCase);
    public string Path { get; }
    private readonly Func<bool> virtualMachineRunning;

    public VmwareNatConfiguration(string path, Func<bool>? virtualMachineRunning = null)
    {
        this.virtualMachineRunning = virtualMachineRunning ?? AnyVirtualMachineRunning;
        Path = System.IO.Path.GetFullPath(path);
        if (!System.IO.Path.GetExtension(Path).Equals(".vmx", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("请选择 VMware 虚拟机的 .vmx 配置文件。");
    }

    private string Read()
    {
        if (new FileInfo(Path).Length > 2 * 1024 * 1024) throw new IOException("虚拟机配置文件过大，未修改。");
        return File.ReadAllText(Path, new UTF8Encoding(false, true));
    }

    public string Inspect()
    {
        string text = Read();
        _ = Prepare(text);
        bool disabled = Lines(text).Any(line => Setting.Match(line) is { Success: true } match &&
            match.Groups[1].Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase));
        return disabled ? "配置已关闭 NAT 网络状态传播；修改后需完全关机再启动才生效。" :
            "尚未关闭 NAT 网络状态传播。宿主机切换 TUN 时，虚拟机 NAT 网卡可能暂时断链；SSH 已连接不代表内网可达。";
    }

    public string Repair()
    {
        EnsureOffline();
        string before = Read(), after = Prepare(before);
        if (before == after) return "";
        string backup = Path + ".h2vm-" + Guid.NewGuid().ToString("N") + ".bak";
        File.Copy(Path, backup, false);
        EnsureOffline();
        if (Read() != before) throw new IOException("虚拟机配置在检查期间发生变化，未修改；请重新检测。");
        SettingsLocation.AtomicWrite(Path, after, true);
        return backup;
    }

    private void EnsureOffline()
    {
        // Stale lock directories survive clean shutdowns. Leave them alone.
        // Elevated VM process paths may be inaccessible: refuse conservatively
        // while any VMware VM runs rather than risk modifying a live VM.
        if (virtualMachineRunning())
            throw new IOException("仍有 VMware 虚拟机进程运行。请保存工作并完全关闭所有 VMware 虚拟机（不能挂起），再应用修复；不会强制关闭虚拟机。");
        if (Regex.IsMatch(Read(), @"(?im)^\s*checkpoint\.vmState\s*=\s*""[^""]+"""))
            throw new IOException("虚拟机保存了挂起状态。请先恢复并正常关机，再应用修复。");
    }

    private static bool AnyVirtualMachineRunning()
    {
        var processes = Process.GetProcessesByName("vmware-vmx");
        try { return processes.Length != 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    private static string[] Lines(string text) => text.Replace("\r\n", "\n").Split('\n');

    internal static string Prepare(string text)
    {
        string[] lines = Lines(text);
        if (!lines.Any(line => Nat.IsMatch(line))) throw new IOException("未发现 NAT 网卡，此修复不适用于该虚拟机。");
        var settings = lines.Where(line => Regex.IsMatch(line, @"^\s*vmnat\.linkStatePropagation\.disable\s*=", RegexOptions.IgnoreCase)).ToArray();
        if (settings.Length > 1 || settings.Any(line => !Setting.IsMatch(line)))
            throw new IOException("NAT 网络状态设置重复或格式异常，请检查原配置；未自动覆盖。");
        if (settings.Length == 1)
        {
            string line = settings[0];
            var match = Setting.Match(line).Groups[1];
            if (match.Value.Equals("TRUE", StringComparison.OrdinalIgnoreCase)) return text;
            string changed = line.Remove(match.Index, match.Length).Insert(match.Index, "TRUE");
            return new Regex("^" + Regex.Escape(line) + "(?=\\r?$)", RegexOptions.Multiline).Replace(text, _ => changed, 1);
        }
        string newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        return text + (text.EndsWith('\n') ? "" : newline) + Key + " = \"TRUE\"" + newline;
    }
}
