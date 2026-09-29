namespace Host2VMRelay;

// Update Mihomo's per-URL health directly. Provider files contain stable targets;
// a reconnect must never depend on a service-managed file copy being refreshed.
internal sealed class ClashHealthSync(Func<HttpClient> openClient, Action<string> log)
{
    internal static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    internal static Task RefreshAsync(HttpClient api, CancellationToken token) =>
        Task.WhenAll(ClashRelayProtocol.All.Select(protocol => ProbeAsync(api, protocol, token)));

    private static async Task ProbeAsync(HttpClient api, ClashRelayProtocol protocol, CancellationToken token)
    {
        string url = Uri.EscapeDataString(protocol.HealthUrl);
        using var response = await api.GetAsync("proxies/" + ClashRelayProtocol.Node + "/delay?url=" + url + "&timeout=150&expected=204", token).ConfigureAwait(false);
        // A failed URL test still updates core health. Missing nodes/controllers
        // are configuration failures, not evidence of a successful fallback.
        if (response.StatusCode != System.Net.HttpStatusCode.ServiceUnavailable &&
            response.StatusCode != System.Net.HttpStatusCode.GatewayTimeout)
            response.EnsureSuccessStatusCode();
    }

    public async Task RunAsync(CancellationToken token)
    {
        bool failed = false;
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var api = openClient();
                api.Timeout = TimeSpan.FromMilliseconds(500);
                while (!token.IsCancellationRequested)
                {
                    await RefreshAsync(api, token).ConfigureAwait(false);
                    if (failed) { log("Clash 动态健康同步已恢复；规则加载内容请在应用情况中检查。"); failed = false; }
                    await Task.Delay(Interval, token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
            catch (Exception ex)
            {
                if (!failed) log("Clash 动态健康同步不可用：" + ex.Message + "。请检查 Clash 是否运行并加载扩展脚本。");
                failed = true;
            }
            try { await Task.Delay(Interval, token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { return; }
        }
    }
}
