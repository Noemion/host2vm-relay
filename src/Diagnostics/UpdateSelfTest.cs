using System.Text.Json;

namespace Host2VMRelay;

internal static class UpdateSelfTest
{
    public static void Run(string output, Action<bool, string> check)
    {
        const string user = "// user-owned comment\nconst marker = '保留 😀';\nfunction main(c) { c.userValue = marker; c.rules.unshift('DOMAIN,custom.example,DIRECT'); return c; }";
        string first = ClashScript.Generate(1080, "192.168.229.10", user);
        var imported = ScriptComposer.Inspect(first);
        check(imported.Kind == "composed-v2" && imported.Original == user, "v2 extracts the full user section");
        string updated = ClashScript.Generate(1081, "fd00::8", first);
        check(updated == ClashScript.Generate(1081, "fd00::8", user), "incremental generation replaces only the managed wrapper");
        check(updated == ClashScript.Generate(1081, "fd00::8", updated), "repeated incremental generation is byte-stable");
        string edited = first.Replace("c.userValue = marker;", "c.edited = true; c.userValue = marker;", StringComparison.Ordinal);
        check(ScriptComposer.ExtractOriginal(edited).Contains("c.edited = true"), "v2 preserves direct user-region edits of a different length");
        check(ScriptComposer.ExtractOriginal(first.Replace("\n", "\r\n")) == user, "v2 accepts Windows line endings");
        using var templateResource = typeof(UpdateSelfTest).Assembly.GetManifestResourceStream("Host2VMRelay.Legacy050")!;
        using var templateReader = new StreamReader(templateResource);
        string managed = ScriptComposer.Normalize(templateReader.ReadToEnd()).TrimEnd('\n').Replace("__SOCKS_PORT__", "1080")
            .Replace("__VM_CIDR__", "192.168.229.10/32").Replace("__VM_RULE_TYPE__", "IP-CIDR");
        string legacy = ScriptComposer.LegacyHeader + user.Length + "\n" + ScriptComposer.Open + user + ScriptComposer.Close + managed + "\n";
        check(ScriptComposer.ExtractOriginal(legacy) == user, "published v1 template upgrades without nesting");
        string legacyEdited = legacy.Replace("c.userValue = marker;", "c.edited = true; c.userValue = marker;", StringComparison.Ordinal);
        check(ScriptComposer.ExtractOriginal(legacyEdited).Contains("c.edited = true"), "legacy user edits survive outdated length metadata");
        void Reject(string value, string label)
        {
            bool rejected = false;
            try { ScriptComposer.ExtractOriginal(value); } catch (FormatException) { rejected = true; }
            check(rejected, label);
        }
        Reject(first.Replace("interval: 3", "interval: 9", StringComparison.Ordinal), "managed-region edits require conflict resolution");
        Reject(legacy.Replace("interval: 3", "interval: 9", StringComparison.Ordinal), "legacy unknown managed edits are not discarded");
        Reject(first + "\nconfig.extra = true;", "trailing custom code is not silently discarded");
        Reject(first[..^20], "truncated generated scripts are rejected");
        Reject(first.Replace(ScriptComposer.UserEnd, "", StringComparison.Ordinal), "missing user boundary is rejected");
        Reject("```js\n" + first + "\n```", "fenced generated scripts are not nested as raw source");
        Reject("function main(c) { return c; }\n// <host2vm-relay:user>\n", "reserved markers cannot collide with user input");
        string managed040 = Legacy040.Replace("__SOCKS_PORT__", "1080")
            .Replace("__VM_CIDR__", "192.168.229.10/32").Replace("__VM_RULE_TYPE__", "IP-CIDR");
        string legacy040 = ScriptComposer.LegacyHeader + user.Length + "\n" + ScriptComposer.Open + user + ScriptComposer.Close + managed040 + "\n";
        check(ScriptComposer.ExtractOriginal(legacy040) == user, "published v0.4.0 managed template is recognized");
        var cases = new Dictionary<string, string>
        {
            ["fresh"] = first, ["updated"] = updated,
            ["v040Updated"] = ClashScript.Generate(1081, "fd00::8", legacy040),
            ["v1Updated"] = ClashScript.Generate(1081, "fd00::8", legacy),
            ["v1UserEdited"] = ClashScript.Generate(1081, "fd00::8", legacyEdited),
            ["v2UserEdited"] = ClashScript.Generate(1081, "fd00::8", edited)
        };
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(output)!, "incremental-cases.json"),
            JsonSerializer.Serialize(cases, new JsonSerializerOptions { WriteIndented = true }));
        TestStorage(output, check);
    }
    private static void TestStorage(string output, Action<bool, string> check)
    {
        string root = Path.Combine(Path.GetDirectoryName(output)!, "storage-tests-" + Guid.NewGuid().ToString("N"));
        string current = Path.Combine(root, "原目录"), target = Path.Combine(root, "New Folder 中文"), pointer = Path.Combine(root, "bootstrap", "storage.json");
        var location = new SettingsLocation(pointer, current);
        try
        {
            Directory.CreateDirectory(current);
            var settings = new Settings { User = "test", Rules = "test.example", ProtectedSecret = SecretStore.Protect("temporary-test-secret") };
            string saved = settings.Serialize();
            File.WriteAllText(Path.Combine(current, "settings.json"), saved);
            check(location.Resolve() == current, "missing location pointer selects Documents-compatible default");
            check(location.Relocate(current, target, settings) == target, "saved configuration migrates to a Unicode path");
            check(new SettingsLocation(pointer, current).Resolve() == target, "location survives a new resolver instance");
            string copied = File.ReadAllText(Path.Combine(target, "settings.json"));
            check(copied == saved && File.Exists(Path.Combine(current, "settings.json")), "migration preserves bytes and retains old configuration");
            check(!File.ReadAllText(pointer).Contains("ProtectedSecret"), "bootstrap contains no credentials");
            check(SecretStore.Unprotect(JsonSerializer.Deserialize<Settings>(copied)!.ProtectedSecret) == "temporary-test-secret", "moved DPAPI ciphertext decrypts for the same user");
            bool conflict = false;
            try { location.Relocate(target, current, settings); } catch (IOException) { conflict = true; }
            check(conflict && location.Resolve() == target, "existing target is protected without confirmation");
            location.Relocate(target, current, settings, true);
            check(location.Resolve() == current && Directory.GetFiles(current, "settings.json.backup-*").Length == 1, "confirmed replacement backs up the destination before restoring default");
            Directory.Move(current, current + "-unavailable");
            bool unavailable = false;
            try { location.Resolve(); } catch (IOException) { unavailable = true; }
            check(unavailable, "missing custom location does not silently create a blank profile");
            Directory.Move(current + "-unavailable", current);
            string barrier = Path.Combine(root, "not-a-directory"); File.WriteAllText(barrier, "blocked");
            var failing = new SettingsLocation(Path.Combine(barrier, "storage.json"), current);
            bool failed = false;
            try { failing.Relocate(current, Path.Combine(root, "staged-only"), settings); } catch (IOException) { failed = true; }
            check(failed && location.Resolve() == current && File.ReadAllText(Path.Combine(current, "settings.json")) == saved,
                "failed pointer write leaves active source intact");
            File.WriteAllText(pointer, "{invalid");
            bool damaged = false;
            try { location.Resolve(); } catch (IOException) { damaged = true; }
            check(damaged, "invalid pointer produces a recoverable error instead of silent fallback");
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    private const string Legacy040 = """
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
