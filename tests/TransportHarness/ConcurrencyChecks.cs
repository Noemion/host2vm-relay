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
        for (int i = 0; i < 1000; i++) logs.Add(new string('x', 4096));
        string longBatch = logs.Drain();
        check(longBatch.Length <= PendingLogBuffer.MaxBatchCharacters && longBatch.Length > 0,
            "large messages cannot exceed the per-frame UI character budget");

        var budget = new ByteBudget(16 * 1024 * 1024);
        int reserved = 0;
        Parallel.For(0, 4096, _ => { if (budget.TryReserve(65536)) Interlocked.Increment(ref reserved); });
        check(reserved == 256 && budget.Used == 16 * 1024 * 1024, "concurrent UDP reply queues share a fixed 16 MiB budget");
        Parallel.For(0, reserved, _ => budget.Release(65536));
        check(budget.Used == 0 && budget.TryReserve(1024), "UDP byte budget is reusable after release");
        budget.Release(1024);
        using var configured = new RelaySocksServer(0, maxTransfers: 64);
        using var defaults = new RelaySocksServer(0);
        check(configured.TransferLimit == 64 && defaults.TransferLimit == 512, "custom and default connection limits reach transport admission");

        using var relay = new RelaySocksServer(0, maxTransfers: RelaySocksServer.MaxTransfers);
        int port = relay.Port;
        var clients = new System.Collections.Concurrent.ConcurrentBag<System.Net.Sockets.TcpClient>();
        try
        {
            await Task.WhenAll(Enumerable.Range(0, RelaySocksServer.MaxConnections).Select(async _ =>
            {
                var client = new System.Net.Sockets.TcpClient();
                clients.Add(client);
                await client.ConnectAsync(System.Net.IPAddress.Loopback, port, token);
                await client.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, token);
                var response = new byte[2];
                await client.GetStream().ReadExactlyAsync(response, token);
                if (!response.AsSpan().SequenceEqual(new byte[] { 5, 0 })) throw new Exception("Invalid greeting");
            }));
            check(relay.ActiveConnections == RelaySocksServer.MaxConnections, "bounded front-door capacity completes concurrent handshakes");
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
            check(relay.ActiveConnections == RelaySocksServer.MaxConnections && relay.RejectedConnections == 32, "32 excess clients are rejected without growing active connection count");
            relay.Dispose();
            for (int i = 0; i < 300 && relay.ActiveConnections != 0; i++) await Task.Delay(10, token);
            check(relay.ActiveConnections == 0, "disconnect drains all concurrent handshakes");
            using var replacement = new RelaySocksServer(port);
            check(replacement.Port == port, "listener port can be reused after concurrent disconnect");
        }
        finally { foreach (var client in clients) client.Dispose(); }

        for (int round = 0; round < 20; round++)
        {
            using var racing = new RelaySocksServer(0);
            int racingPort = racing.Port;
            Task[] attempts = Enumerable.Range(0, 32).Select(async _ =>
            {
                using var client = new System.Net.Sockets.TcpClient();
                try { await client.ConnectAsync(System.Net.IPAddress.Loopback, racingPort, token); }
                catch (System.Net.Sockets.SocketException) { }
            }).ToArray();
            await Task.Run(racing.Dispose, token);
            await Task.WhenAll(attempts);
            for (int i = 0; i < 100 && racing.ActiveConnections != 0; i++) await Task.Delay(10, token);
            check(racing.ActiveConnections == 0, "accept/dispose race leaves no registered sockets, round " + round);
        }
    }
}
