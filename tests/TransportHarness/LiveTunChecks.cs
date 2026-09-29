using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using Host2VMRelay;

internal static class LiveTunChecks
{
    // Opt-in only: this changes the user's live TUN state. Never run in the
    // ordinary test suite or silently retry after a failed recovery.
    public static async Task RunAsync(string url, int rounds, string output)
    {
        var target = new Uri(url, UriKind.Absolute);
        if (target.Scheme != "https" || target.UserInfo.Length != 0 || target.Query.Length != 0 || target.Fragment.Length != 0)
            throw new ArgumentException("Use an HTTPS target without credentials, query or fragment.");
        if (rounds is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(rounds));
        using var api = ClashControlClient.Open();
        using var config = await ClashControlClient.ReadAsync(api, "configs", CancellationToken.None);
        if (!config.RootElement.GetProperty("tun").GetProperty("enable").GetBoolean())
            throw new IOException("Enable TUN and establish a working baseline first.");
        await ProbeAsync(target);
        var results = new List<object>();
        bool passed = false;
        try
        {
            for (int round = 1; round <= rounds; round++)
            {
                await SetTunAsync(api, false);
                await Task.Delay(300);
                var total = Stopwatch.StartNew();
                await SetTunAsync(api, true);
                double enableMs = total.Elapsed.TotalMilliseconds;
                var recovery = Stopwatch.StartNew();
                try
                {
                    int status = await ProbeAsync(target);
                    double recoveryMs = recovery.Elapsed.TotalMilliseconds;
                    results.Add(new { round, status, enableMs, recoveryMs, totalMs = total.Elapsed.TotalMilliseconds });
                    Console.WriteLine($"Round {round}: HTTP {status}; after enable {recoveryMs:F1} ms; including TUN creation {total.Elapsed.TotalMilliseconds:F1} ms");
                    if (recoveryMs >= 1000) throw new IOException("Recovery exceeded one second after TUN enable completed.");
                }
                catch (Exception ex)
                {
                    results.Add(new { round, error = ex.Message, enableMs, recoveryMs = recovery.Elapsed.TotalMilliseconds });
                    throw;
                }
                await Task.Delay(500);
            }
            passed = true;
        }
        finally
        {
            try { await SetTunAsync(api, true); }
            finally
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
                await File.WriteAllTextAsync(output, JsonSerializer.Serialize(new { passed, target = target.AbsoluteUri, rounds = results }, new JsonSerializerOptions { WriteIndented = true }));
            }
        }
    }

    private static async Task SetTunAsync(HttpClient api, bool enable)
    {
        using var response = await api.PatchAsJsonAsync("configs", new { tun = new { enable } });
        response.EnsureSuccessStatusCode();
    }

    private static async Task<int> ProbeAsync(Uri target)
    {
        using var handler = new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false };
        using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(1) };
        using var response = await http.GetAsync(target, HttpCompletionOption.ResponseHeadersRead);
        int status = (int)response.StatusCode;
        if (status is < 200 or >= 400) throw new IOException("Unexpected HTTP status " + status);
        return status;
    }
}
