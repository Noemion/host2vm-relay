using System.Net;

namespace Host2VMRelay;

internal static class ClashHealthChecks
{
    private sealed class Handler(HttpStatusCode status) : HttpMessageHandler
    {
        internal readonly List<string> Paths = new();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Paths.Add(request.RequestUri!.PathAndQuery);
            return Task.FromResult(new HttpResponseMessage(status));
        }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        foreach (var code in new[] { HttpStatusCode.OK, HttpStatusCode.ServiceUnavailable, HttpStatusCode.GatewayTimeout })
        {
            using var handler = new Handler(code);
            using var api = new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") };
            await ClashHealthSync.RefreshAsync(api, CancellationToken.None);
            check(handler.Paths.Count == 2 && handler.Paths.Any(p => p.Contains("%2Ftcp")) && handler.Paths.Any(p => p.Contains("%2Fudp")),
                "health synchronization updates both per-protocol URLs for " + code);
        }
        using var missing = new HttpClient(new Handler(HttpStatusCode.NotFound)) { BaseAddress = new Uri("http://localhost/") };
        bool rejected = false;
        try { await ClashHealthSync.RefreshAsync(missing, CancellationToken.None); } catch (HttpRequestException) { rejected = true; }
        check(rejected, "missing relay configuration is not reported as a healthy synchronization");
        var logs = new List<string>();
        using var cancel = new CancellationTokenSource();
        int attempts = 0;
        var sync = new ClashHealthSync(() => { attempts++; throw new IOException("controller unavailable"); }, logs.Add);
        Task running = sync.RunAsync(cancel.Token);
        await Task.Delay(ClashHealthSync.Interval * 2 + TimeSpan.FromMilliseconds(50));
        cancel.Cancel();
        await running.WaitAsync(TimeSpan.FromSeconds(1));
        check(attempts >= 2 && logs.Count == 1, "controller failures retry with deduplicated logs and cancel promptly");
    }
}
