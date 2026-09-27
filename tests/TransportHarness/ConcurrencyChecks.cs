using Host2VMRelay;

internal static class ConcurrencyChecks
{
    private sealed class Resource(Action release) : IDisposable
    {
        public void Dispose() => release();
    }
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        var queue = new BackgroundCleanup();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int order = 0;
        Task first = queue.Enqueue(new Resource(() =>
        {
            entered.SetResult();
            release.Wait(token);
            Interlocked.Increment(ref order);
        }));
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3), token);
            Task second = queue.Enqueue(new Resource(() =>
            {
                if (Interlocked.Increment(ref order) != 2) throw new Exception("Cleanup order changed");
            }));
            check(!first.IsCompleted && !second.IsCompleted, "slow disposal does not block caller or allow overlapping cleanup");
            check(ReferenceEquals(second, queue.Enqueue(null)), "reconnect waits for pending cleanup even after session detachment");
            release.Set();
            await second.WaitAsync(TimeSpan.FromSeconds(3), token);
            check(order == 2, "queued resources are released exactly once in order");
        }
        finally { release.Set(); await first.WaitAsync(TimeSpan.FromSeconds(3), token); }

        var ownerFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task joined = queue.Enqueue(new Resource(() => { }), ownerFinished.Task);
        check(!joined.IsCompleted, "cleanup joins a concurrent disposal owner before allowing reconnect");
        ownerFinished.SetResult();
        await joined.WaitAsync(TimeSpan.FromSeconds(3), token);

        var logs = new PendingLogBuffer();
        Parallel.For(0, 20000, i => logs.Add("concurrent request " + i));
        string batch = logs.Drain();
        check(batch.Contains("19000") && batch.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length == 101,
            "concurrent log flood is bounded with explicit drop notice and 100-record UI batches");
        for (int i = 0; i < 9; i++) logs.Drain();
        check(logs.Drain() == "", "log flood retains at most 1000 pending records");

        using var relay = new RelaySocksServer(0);
        int port = relay.Port;
        var clients = new System.Collections.Concurrent.ConcurrentBag<System.Net.Sockets.TcpClient>();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, 128).Select(async _ =>
            {
                var client = new System.Net.Sockets.TcpClient();
                clients.Add(client);
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, token);
                await client.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, token);
                var response = new byte[2];
                await client.GetStream().ReadExactlyAsync(response, token);
                if (!response.AsSpan().SequenceEqual(new byte[] { 5, 0 })) throw new Exception("Invalid greeting");
            }));
            check(relay.ActiveConnections == 128, "128 concurrent SOCKS clients complete handshake");
            await Task.WhenAll(Enumerable.Range(0, 32).Select(async _ =>
            {
                using var client = new System.Net.Sockets.TcpClient();
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, token);
                try
                {
                    if (await client.GetStream().ReadAsync(new byte[1], token) != 0)
                        throw new Exception("Unexpected overload response");
                }
                catch (System.IO.IOException) { /* Reset is also a valid overload rejection. */ }
            }));
            check(relay.ActiveConnections == 128, "32 excess clients are rejected without growing active connection count");
            relay.Dispose();
            for (int i = 0; i < 300 && relay.ActiveConnections != 0; i++) await Task.Delay(10, token);
            check(relay.ActiveConnections == 0, "disconnect drains all 128 concurrent sessions");
            using var replacement = new RelaySocksServer(port);
            check(replacement.Port == port, "listener port can be reused after concurrent disconnect");
        }
        finally { foreach (var client in clients) client.Dispose(); }
    }
}
