using System.Net;
using System.Net.Sockets;

namespace Host2VMRelay;

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

  // Preserve existing Fake-IP exceptions while putting selected domains first.
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
