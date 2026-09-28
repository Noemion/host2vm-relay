using System.Text.Json;

namespace Host2VMRelay;

internal static class SelfTest
{
    public static void Run(string output)
    {
        output = Path.GetFullPath(output);
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        var lines = new List<string>();
        try
        {
            void Check(bool ok, string message) { if (!ok) throw new Exception(message); lines.Add("PASS " + message); }
            lines.Add("INFO Architecture: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            var compiled = Rules.Compile("code.example.com\n*.example.com\n10.20.30.40\n10.20.30.0/24\n2001:db8::1\n# comment\ncode.example.com");
            Check(compiled.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 5, "rule normalization and deduplication");
            Check(compiled.Contains("DOMAIN-SUFFIX,example.com") && compiled.Contains("IP-CIDR6,2001:db8::1/128,no-resolve"), "suffix and IPv6 rules");
            foreach (var invalid in new[] { "https://code.example.com/", "1.2.3.4/33", "a.com,DIRECT", "999.999.999.999", "a b.com" })
            {
                bool rejected = false; try { Rules.Compile(invalid); } catch (FormatException) { rejected = true; }
                Check(rejected, "reject " + invalid);
            }
            var encrypted = SecretStore.Protect("test-password-中文");
            Check(!encrypted.Contains("test-password") && SecretStore.Unprotect(encrypted) == "test-password-中文", "DPAPI password roundtrip");
            var root = Path.Combine(Path.GetDirectoryName(output)!, "self-test-data-" + Guid.NewGuid());
            string originalSettings = Settings.Folder, originalRules = ClashRuleFile.Folder;
            try
            {
                Settings.Folder = Path.Combine(root, "settings");
                ClashRuleFile.Folder = Path.Combine(root, "rules");
                Check(Settings.Load().User == "", "diagnostic profile does not migrate personal settings");
                new Settings { ProtectedSecret = encrypted }.Save();
                Check(Settings.Load().ProtectedSecret == encrypted && !File.ReadAllText(Path.Combine(Settings.Folder, "settings.json")).Contains("test-password"), "settings persist ciphertext only");
                ClashRuleFile.Disable();
                Check(File.ReadAllText(ClashRuleFile.FilePath) == ClashRuleFile.DisabledPayload && File.ReadAllText(ClashRuleFile.UdpFilePath) == ClashRuleFile.DisabledPayload, "both local rule files start disabled");
                ClashRuleFile.Write(compiled);
                Check(File.ReadAllText(ClashRuleFile.FilePath) == compiled && File.ReadAllText(ClashRuleFile.UdpFilePath) == ClashRuleFile.DisabledPayload, "TCP rule updates do not enable UDP before its channel is healthy");
                ClashRuleFile.WriteUdp(compiled);
                Check(File.ReadAllText(ClashRuleFile.UdpFilePath) == compiled, "independent UDP rule update");
                var timestamp = File.GetLastWriteTimeUtc(ClashRuleFile.FilePath);
                ClashRuleFile.Write(compiled);
                Check(File.GetLastWriteTimeUtc(ClashRuleFile.FilePath) == timestamp, "unchanged rules are not rewritten");
            }
            finally
            {
                Settings.Folder = originalSettings; ClashRuleFile.Folder = originalRules;
                if (Directory.Exists(root)) Directory.Delete(root, true);
            }
            // Asset tests validate every encoded frame. Exercise runtime status
            // rendering only at the small tray and high-DPI window sizes.
            foreach (int size in new[] { 16, 64 })
            {
                using var icon = AppIcon.Load(size);
                Check(icon.Width == size && icon.Height == size, "embedded application icon " + size);
                var states = new HashSet<string>();
                bool dimensionsMatch = true;
                foreach (var state in Enum.GetValues<ConnectionIconState>())
                {
                    using var statusIcon = AppIcon.Load(size, state);
                    using var bitmap = statusIcon.ToBitmap();
                    using var encoded = new MemoryStream();
                    bitmap.Save(encoded, System.Drawing.Imaging.ImageFormat.Png);
                    states.Add(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(encoded.ToArray())));
                    dimensionsMatch &= statusIcon.Width == size && statusIcon.Height == size;
                }
                Check(dimensionsMatch && states.Count == Enum.GetValues<ConnectionIconState>().Length,
                    "connection status icons retain dimensions and remain visually distinct at " + size);
            }
            var failed = ConnectionPresentation.Create(false, new RelayHealth(true, true), true, "认证失败");
            Check(failed.Icon == ConnectionIconState.Failed && failed.Caption.Contains("连接失败") && failed.Details == "认证失败", "closed SSH overrides stale healthy leases and preserves the failure reason");
            var partial = ConnectionPresentation.Create(true, new RelayHealth(true, false), true, "虚拟机拒绝执行");
            Check(partial.Icon == ConnectionIconState.Degraded && partial.Caption.Contains("UDP 连接失败") && partial.Details.Contains("拒绝执行"), "UDP failure is explicit while a working TCP path remains available");
            Check(ConnectionPresentation.Create(true, new RelayHealth(true, false), false, null).Icon == ConnectionIconState.Connected, "disabled UDP is not reported as a failure");
            Check(ConnectionPresentation.Create(true, new RelayHealth(true, true), true, null).Key == "both", "recovered UDP returns the status to connected");
            foreach (int invalid in new[] { 0, 65536 })
            {
                bool rejected = false; try { ClashScript.Generate(invalid); } catch (ArgumentOutOfRangeException) { rejected = true; }
                Check(rejected, "reject invalid SOCKS port " + invalid);
            }
            string first = ClashScript.Generate(1080);
            bool badMarker = false;
            try { ScriptComposer.ExtractOriginal(first.Replace("managed-sha256: ", "managed-sha256: x")); } catch (FormatException) { badMarker = true; }
            Check(badMarker, "reject damaged generated markers");
            bool tooLarge = false;
            try { ClashScript.Generate(1080, existingScript: new string('x', ScriptComposer.MaxSourceLength + 1)); } catch (ArgumentException) { tooLarge = true; }
            Check(tooLarge, "reject oversized source");
            UpdateSelfTest.Run(output, Check);
            ReleaseUpdateChecks.RunAsync(output, Check).GetAwaiter().GetResult();
            ExportScriptCases(Path.Combine(Path.GetDirectoryName(output)!, "script-cases.json"));
            lines.Add("PASS actual C# generated scripts exported for JavaScript execution checks");
            File.WriteAllText(output, string.Join(Environment.NewLine, lines));
        }
        catch (Exception ex)
        {
            lines.Add("FAIL " + ex); File.WriteAllText(output, string.Join(Environment.NewLine, lines)); Environment.ExitCode = 1;
        }
    }
    private static void ExportScriptCases(string path)
    {
        var cases = new Dictionary<string, string?>
        {
            ["empty"] = null,
            ["merge"] = "const label = '中文 😀 __SOCKS_PORT__'; function helper(x) { return x + ':kept'; } function main(config, profileName) { config.label = helper(label); config.profile = profileName; config.rules.unshift('DOMAIN,user.example,DIRECT'); return config; }",
            ["arrow"] = "const main = (config, profileName) => ({ ...config, profile: profileName, arrow: true });",
            ["mutating"] = "function main(config) { config.mutated = true; }",
            ["early"] = "function main(config, profileName) { if (profileName === 'test') return { ...config, early: true }; return config; }",
            ["throws"] = "function main(config) { throw new Error('user failure'); }",
            ["missing"] = "const example = 'function main(config) { return config; }';",
            ["async"] = "async function main(config) { return config; }",
            ["null"] = "function main(config) { return null; }",
            ["array"] = "function main(config) { return []; }",
            ["comment"] = "function main(config) { config.comment = true; return config; } // trailing comment",
            ["tun-legacy writes"] = "function main(c) { c.tun ??= {}; c.tun['auto-route'] = true; c.tun['dns-hijack'] = [...new Set([...(c.tun['dns-hijack'] ?? []),'any:53','tcp://any:53'])]; c.tun['route-exclude-address'] = [...(c.tun['route-exclude-address'] ?? []),'192.168.99.10/32']; c.custom = 'preserved'; return c; }",
            ["tun-in-place array changes"] = "function main(c) { c.tun['dns-hijack'].push('tcp://any:53'); c.tun['route-exclude-address'].push('10.0.0.0/8'); c.custom = 'preserved'; return c; }",
            ["tun-replacement tun"] = "function main(c) { c.tun = {enable:false,stack:'system',mtu:9000,'udp-timeout':300}; c.custom = 'preserved'; return c; }",
            ["tun-deleted tun"] = "function main(c) { delete c.tun; c.custom = 'preserved'; return c; }",
            ["tun-replacement config"] = "function main(c) { return {rules:c.rules,custom:'preserved'}; }",
            ["tun-custom"] = "function main(c) { c.tun = {'udp-timeout':300}; return c; }"
        };
        var generated = cases.ToDictionary(x => x.Key, x => ClashScript.Generate(1080, "192.168.229.10", x.Value));
        generated["regenerated"] = ClashScript.Generate(1081, "fd00::8", generated["merge"]);
        File.WriteAllText(path, JsonSerializer.Serialize(generated, new JsonSerializerOptions { WriteIndented = true }));
    }
}
