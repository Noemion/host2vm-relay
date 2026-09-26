namespace Host2VMRelay;

internal static class SelfTest
{
    public static void Run(string output)
    {
        var lines = new List<string>();
        try {
            void Check(bool ok, string message) { if (!ok) throw new Exception(message); lines.Add("PASS " + message); }
            lines.Add("INFO Architecture: " + System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture);
            var runtimeModule = System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().FirstOrDefault(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
            lines.Add("INFO Runtime module: " + runtimeModule?.FileName);

            var compiled = Rules.Compile("code.example.com\n*.example.com\n10.20.30.40\n10.20.30.0/24\n2001:db8::1\n# comment\ncode.example.com");
            Check(compiled.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 5, "rule normalization and deduplication");
            Check(compiled.Contains("DOMAIN-SUFFIX,example.com") && compiled.Contains("IP-CIDR6,2001:db8::1/128,no-resolve"), "suffix and IPv6 rules");
            foreach (var invalid in new[] { "https://code.example.com/", "1.2.3.4/33", "a.com,DIRECT", "999.999.999.999", "a b.com" }) {
                bool rejected = false; try { Rules.Compile(invalid); } catch (FormatException) { rejected = true; } Check(rejected, "reject " + invalid);
            }

            var encrypted = SecretStore.Protect("test-password-中文");
            Check(!encrypted.Contains("test-password") && SecretStore.Unprotect(encrypted) == "test-password-中文", "DPAPI password roundtrip");

            var settingsPath = Path.Combine(Path.GetTempPath(), "Host2VMRelaySettingsTest-" + Guid.NewGuid());
            var originalSettings = Settings.Folder;
            try {
                Settings.Folder = settingsPath;
                new Settings { ProtectedSecret = encrypted }.Save();
                Check(Settings.Load().ProtectedSecret == encrypted && !File.ReadAllText(Path.Combine(settingsPath, "settings.json")).Contains("test-password"), "settings persist ciphertext only");
            } finally {
                Settings.Folder = originalSettings;
                Directory.Delete(settingsPath, true);
            }

            var rulePath = Path.Combine(Path.GetTempPath(), "Host2VMRelayRulesTest-" + Guid.NewGuid());
            var originalRuleFolder = ClashRuleFile.Folder;
            try {
                ClashRuleFile.Folder = rulePath;
                ClashRuleFile.Disable();
                Check(File.ReadAllText(ClashRuleFile.FilePath) == ClashRuleFile.DisabledPayload, "disabled Clash rule file");
                ClashRuleFile.Write(compiled);
                Check(File.ReadAllText(ClashRuleFile.FilePath) == compiled, "local Clash rule file update");
            } finally {
                ClashRuleFile.Folder = originalRuleFolder;
                Directory.Delete(rulePath, true);
            }

            var ipv4 = ClashScript.Generate(1080, "192.168.50.8");
            Check(ipv4.Contains("IP-CIDR,192.168.50.8/32,DIRECT"), "custom VM IPv4 bypass");
            Check(ipv4.Contains("type: \"file\"") && !ipv4.Contains("127.0.0.1:17861"), "local Clash rule provider");
            Check(ClashScript.Generate(1080, "fd00::8").Contains("IP-CIDR6,fd00::8/128,DIRECT"), "custom VM IPv6 bypass");
            Check(ClashScript.Generate(1081).Contains("port: 1081") && !ClashScript.Generate(1081).Contains("__SOCKS_PORT__"), "script port generation");

            File.WriteAllText(output, string.Join(Environment.NewLine, lines));
        } catch (Exception ex) {
            lines.Add("FAIL " + ex);
            File.WriteAllText(output, string.Join(Environment.NewLine, lines));
            Environment.ExitCode = 1;
        }
    }
}
