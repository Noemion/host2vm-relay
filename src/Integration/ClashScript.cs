using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace Host2VMRelay;

public static class ClashScript
{
    public static string Generate(int port, string host = "192.168.229.10", string? existingScript = null)
    {
        if (port is < 1 or > 65535) throw new ArgumentOutOfRangeException(nameof(port), "SOCKS5 端口必须为 1–65535。");
        if (!IPAddress.TryParse(host, out var address)) throw new ArgumentException("虚拟机地址请填写 IPv4 或 IPv6，以便生成准确的 TUN 绕过规则。");
        string cidr = address + (address.AddressFamily == AddressFamily.InterNetwork ? "/32" : "/128");
        // Substitute only our template, never the user's source text.
        string relay = Template.Replace("__SOCKS_PORT__", port.ToString(CultureInfo.InvariantCulture))
            .Replace("__VM_CIDR__", cidr)
            .Replace("__VM_RULE_TYPE__", address.AddressFamily == AddressFamily.InterNetwork ? "IP-CIDR" : "IP-CIDR6");
        return ScriptComposer.Compose(existingScript, relay);
    }

    private const string Template = """
function main(config, profileName) {
  // Run the user's original entry point first, in its own lexical scope.
  if (typeof __h2vmOriginalMain !== "function" || __h2vmOriginalMain === main) {
    throw new Error("原有扩展脚本必须提供 main(config, profileName) 函数。");
  }
  const result = __h2vmOriginalMain(config, profileName);
  if (result !== undefined) config = result;
  if (!config || typeof config !== "object" || Array.isArray(config) || typeof config.then === "function") {
    throw new Error("扩展脚本 main 必须同步返回配置对象，不能返回 Promise、数组或空值。");
  }
  for (const proxy of config.proxies ?? []) {
    if (proxy.type === "mieru") proxy.udp = true;
  }
  const node = "Host2VMRelay";
  const oldNode = "Host2VM Relay"; // Migrate configurations generated before 0.3.
  const provider = "host2vm-relay-rules";
  config.proxies = [
    ...(config.proxies ?? []).filter(p => p.name !== node && p.name !== oldNode),
    { name: node, type: "socks5", server: "127.0.0.1", port: __SOCKS_PORT__, udp: false }
  ];
  for (const group of config["proxy-groups"] ?? []) {
    if (Array.isArray(group.proxies)) {
      group.proxies = [...new Set(group.proxies.map(p => p === oldNode ? node : p))];
    }
  }
  config["rule-providers"] = config["rule-providers"] ?? {};
  config["rule-providers"][provider] = {
    type: "file", behavior: "classical", format: "text",
    path: "./rules/host2vm-relay-rules.txt", interval: 3
  };
  const first = [
    "__VM_RULE_TYPE__,__VM_CIDR__,DIRECT,no-resolve",
    "IP-CIDR,127.0.0.0/8,DIRECT,no-resolve",
    "RULE-SET," + provider + "," + node
  ];
  const ownedRule = r => [node, oldNode].some(n => r.endsWith("," + n) || r.endsWith("," + n + ",no-resolve"));
  config.rules = [...first, ...(config.rules ?? []).filter(r => !first.includes(r) && !ownedRule(r))];

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
  const tun = config.tun = config.tun ?? {};
  tun["auto-route"] = true;
  tun["dns-hijack"] = [...new Set([...(tun["dns-hijack"] ?? []), "any:53", "tcp://any:53"])];
  tun["route-exclude-address"] = [...new Set([...(tun["route-exclude-address"] ?? []), "__VM_CIDR__"])];
  return config;
}
""";
}
