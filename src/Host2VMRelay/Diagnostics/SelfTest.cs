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
            var encrypted = SecretStore.Protect("test-password-中文"); Check(!encrypted.Contains("test-password") && SecretStore.Unprotect(encrypted) == "test-password-中文", "DPAPI password roundtrip");
            var path = Path.Combine(Path.GetTempPath(), "KylinTunnelTest-" + Guid.NewGuid()); var original = Settings.Folder;
            try { Settings.Folder = path; new Settings { ProtectedSecret = encrypted }.Save(); Check(Settings.Load().ProtectedSecret == encrypted && !File.ReadAllText(Path.Combine(path, "settings.json")).Contains("test-password"), "settings persist ciphertext only"); } finally { Settings.Folder = original; Directory.Delete(path, true); }
            using var server = new RuleServer(0) { Payload = compiled }; server.Start();
            var baseUrl = "http://127.0.0.1:" + server.Port;
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            Check(http.GetStringAsync(baseUrl + "/rules.txt").GetAwaiter().GetResult() == compiled, "HTTP rule provider payload");
            server.Payload = Rules.Compile("new.example.com"); Check(http.GetStringAsync(baseUrl + "/rules.txt").GetAwaiter().GetResult().Contains("new.example.com"), "rule updates served live");
            Check(http.GetAsync(baseUrl + "/other").GetAwaiter().GetResult().StatusCode == System.Net.HttpStatusCode.NotFound, "HTTP unknown path rejected");
            Check(ClashScript.Generate(1080, "192.168.50.8").Contains("IP-CIDR,192.168.50.8/32,DIRECT"), "custom VM IPv4 bypass");
            Check(ClashScript.Generate(1080, "fd00::8").Contains("IP-CIDR6,fd00::8/128,DIRECT"), "custom VM IPv6 bypass");
            Check(ClashScript.Generate(1081).Contains("port: 1081") && !ClashScript.Generate(1081).Contains("__SOCKS_PORT__"), "script port generation");
            File.WriteAllText(output, string.Join(Environment.NewLine, lines));
        } catch (Exception ex) { lines.Add("FAIL " + ex); File.WriteAllText(output, string.Join(Environment.NewLine, lines)); Environment.ExitCode = 1; }
    }
}
