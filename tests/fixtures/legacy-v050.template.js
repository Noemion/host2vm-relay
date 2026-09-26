function main(config, profileName) {
  // These TUN fields belong to Clash Verge Settings, not extension scripts.
  // Capture deep copies before the imported script can mutate arrays in place.
  const guiTunKeys = ["enable", "stack", "device", "auto-route", "route-exclude-address",
    "auto-redirect", "auto-detect-interface", "dns-hijack", "strict-route", "mtu"];
  const inputTun = config.tun ?? {};
  const savedTun = {};
  for (const key of guiTunKeys) {
    if (Object.prototype.hasOwnProperty.call(inputTun, key)) {
      const value = inputTun[key];
      savedTun[key] = value === undefined ? undefined : JSON.parse(JSON.stringify(value));
    }
  }
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
  // Restore the incoming GUI values, including absence. This also neutralizes
  // TUN writes from imported older scripts without rewriting their source.
  // Keep non-GUI TUN options and all unrelated user configuration intact.
  if (config.tun != null || Object.keys(savedTun).length > 0) {
    const tun = config.tun = config.tun ?? {};
    if (typeof tun !== "object" || Array.isArray(tun)) {
      throw new Error("TUN 配置必须为对象，请检查原有扩展脚本。");
    }
    for (const key of guiTunKeys) {
      if (Object.prototype.hasOwnProperty.call(savedTun, key)) tun[key] = savedTun[key];
      else delete tun[key];
    }
  }
  // 在 Clash 的 TUN 设置中开启自动路由，DNS 劫持添加 any:53、tcp://any:53，
  // 路由排除添加 __VM_CIDR__；不要在扩展脚本里写入这些界面字段。
  return config;
}
