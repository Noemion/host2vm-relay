using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

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

public static class Rules
{
    public static string Compile(string input)
    {
        var result = new List<string>();
        foreach (var raw in input.Split('\n'))
        {
            var value = raw.Trim();
            if (value.Length == 0 || value.StartsWith('#')) continue;
            if (value.Contains(',')) throw new FormatException("请输入域名或 IP，不要输入 Clash 规则：" + value);
            if (value.Contains('/'))
            {
                var parts = value.Split('/');
                if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var address) ||
                    !int.TryParse(parts[1], out int bits) || bits < 0 || bits > (address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128))
                    throw new FormatException("无效网段（不要输入网址或路径）：" + value);
                result.Add((address.AddressFamily == AddressFamily.InterNetwork ? "IP-CIDR," : "IP-CIDR6,") + address + "/" + bits + ",no-resolve");
            }
            else if (IPAddress.TryParse(value, out var ip))
            {
                result.Add((ip.AddressFamily == AddressFamily.InterNetwork ? "IP-CIDR," + ip + "/32" : "IP-CIDR6," + ip + "/128") + ",no-resolve");
            }
            else
            {
                bool suffix = value.StartsWith("*.") || value.StartsWith("+.");
                string domain = suffix ? value[2..] : value;
                try { domain = new System.Globalization.IdnMapping().GetAscii(domain.TrimEnd('.')).ToLowerInvariant(); }
                catch { throw new FormatException("无效域名：" + value); }
                if (domain.Length > 253 || !domain.Contains('.') || domain.Split('.').Any(x => !Regex.IsMatch(x, "^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")) || Regex.IsMatch(domain, "^[0-9.]+$"))
                    throw new FormatException("无效域名或 IP：" + value);
                result.Add((suffix ? "DOMAIN-SUFFIX," : "DOMAIN,") + domain);
            }
        }
        return result.Count == 0 ? "# No company rules\n" : string.Join("\n", result.Distinct()) + "\n";
    }
}

// Loopback-only rule feed. No controller credentials or passwords are exposed.
public sealed class RuleServer : IDisposable
{
    private readonly TcpListener listener;
    public RuleServer(int port = 17861) { listener = new TcpListener(IPAddress.Loopback, port); }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    private readonly CancellationTokenSource stop = new();
    public string Payload = "# No company rules\n";
    public DateTime LastReadUtc;
    public void Start() { listener.Start(); _ = Run(); }
    private async Task Run()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Serve(client); } }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var stream = client.GetStream();
                var bytes = new byte[4096]; int count = 0;
                while (count < bytes.Length)
                {
                    int n = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                    if (n == 0) return;
                    count += n;
                    if (Encoding.ASCII.GetString(bytes, 0, count).Contains("\r\n\r\n")) break;
                }
                var line = Encoding.ASCII.GetString(bytes, 0, count).Split("\r\n")[0];
                bool ok = line == "GET /rules.txt HTTP/1.1" || line == "GET /rules.txt HTTP/1.0";
                var body = Encoding.UTF8.GetBytes(ok ? Volatile.Read(ref Payload) : "Not found\n");
                if (ok) LastReadUtc = DateTime.UtcNow;
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (ok ? "200 OK" : "404 Not Found") + "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}

public static class ClashScript
{
    public static string Generate(int port, string host = "192.168.229.10")
    {
        if (!IPAddress.TryParse(host, out var address)) throw new ArgumentException("虚拟机地址请填写 IPv4 或 IPv6，以便为 SSH 连接生成准确的 TUN 绕过规则。");
        string cidr = address + (address.AddressFamily == AddressFamily.InterNetwork ? "/32" : "/128");
        return Template.Replace("__SOCKS_PORT__", port.ToString()).Replace("__VM_CIDR__", cidr)
            .Replace("__VM_RULE_TYPE__", address.AddressFamily == AddressFamily.InterNetwork ? "IP-CIDR" : "IP-CIDR6");
    }
    private const string Template = """
// Host2VM Relay: paste once into the active subscription's extension script.
// Keep this app running. Rules update through a loopback HTTP rule provider.
function main(config, profileName) {
  for (const proxy of config.proxies ?? []) {
    if (proxy.type === "mieru") proxy.udp = true;
  }
  const node = "Host2VM Relay";
  const provider = "host2vm-relay-rules";
  config.proxies = [
    ...(config.proxies ?? []).filter(p => p.name !== node),
    { name: node, type: "socks5", server: "127.0.0.1", port: __SOCKS_PORT__, udp: false }
  ];
  config["rule-providers"] = config["rule-providers"] ?? {};
  config["rule-providers"][provider] = {
    type: "http", behavior: "classical", format: "text",
    url: "http://127.0.0.1:17861/rules.txt",
    path: "./rule-providers/host2vm-relay-rules.txt",
    interval: 15, proxy: "DIRECT"
  };
  const first = [
    "__VM_RULE_TYPE__,__VM_CIDR__,DIRECT,no-resolve",
    "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve",
    "RULE-SET," + provider + "," + node
  ];
  config.rules = [...first, ...(config.rules ?? []).filter(r =>
    !first.includes(r) && !r.endsWith("," + node) && !r.endsWith("," + node + ",no-resolve"))];

  // Preserve existing Fake-IP exceptions while putting company domains first.
  const dns = config.dns = config.dns ?? {};
  const oldMode = dns["fake-ip-filter-mode"] ?? "blacklist";
  const oldFilter = dns["fake-ip-filter"] ?? [];
  const priority = "RULE-SET," + provider + ",fake-ip";
  let filters;
  if (oldMode === "rule") {
    filters = oldFilter.filter(r => r !== priority);
  } else {
    const action = oldMode === "whitelist" ? "fake-ip" : "real-ip";
    filters = oldFilter.map(pattern => {
      if (pattern === "*") return "MATCH," + action;
      if (pattern.startsWith("geosite:")) return "GEOSITE," + pattern.slice(8) + "," + action;
      if (pattern.startsWith("rule-set:")) return "RULE-SET," + pattern.slice(9) + "," + action;
      if (pattern.startsWith("+.")) return "DOMAIN-SUFFIX," + pattern.slice(2) + "," + action;
      if (pattern.includes("*") || pattern.includes("?")) return "DOMAIN-WILDCARD," + pattern + "," + action;
      return "DOMAIN," + pattern + "," + action;
    });
    filters.push("MATCH," + (oldMode === "whitelist" ? "real-ip" : "fake-ip"));
  }
  dns.enable = true;
  dns["enhanced-mode"] = "fake-ip";
  dns["fake-ip-filter-mode"] = "rule";
  dns["fake-ip-filter"] = [priority, ...filters];
  // Some Verge versions override TUN fields: also set these in the TUN UI.
  const tun = config.tun = config.tun ?? {};
  tun["auto-route"] = true;
  tun["dns-hijack"] = [...new Set([...(tun["dns-hijack"] ?? []), "any:53", "tcp://any:53"])];
  tun["route-exclude-address"] = [...new Set([...(tun["route-exclude-address"] ?? []), "__VM_CIDR__"])];
  return config;
}
""";
}
