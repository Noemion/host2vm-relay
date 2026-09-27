using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Host2VMRelay;

/// <summary>Transfers real bytes through the production relay; no SSH credentials or external network.</summary>
internal static class LoadChecks
{
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start(256);
        using var relay = new RelaySocksServer(0);
        int upstreamPort = ((IPEndPoint)upstream.LocalEndpoint).Port;
        relay.SetUpstream(upstreamPort, null);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var send = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int connected = 0;
        const int count = RelaySocksServer.MaxTransfers, bytes = 256 * 1024;
        long baseline = Process.GetCurrentProcess().WorkingSet64;
        var clock = Stopwatch.StartNew();
        var times = new System.Collections.Concurrent.ConcurrentBag<double>();
        async Task Echo(TcpClient remote)
        {
            using (remote)
            {
                var stream = remote.GetStream();
                await stream.ReadExactlyAsync(new byte[3], token);
                await stream.WriteAsync(new byte[] { 5, 0 }, token);
                await stream.ReadExactlyAsync(new byte[10], token);
                await stream.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
                byte[] buffer = new byte[16384];
                int read;
                while ((read = await stream.ReadAsync(buffer, token)) != 0)
                    await stream.WriteAsync(buffer.AsMemory(0, read), token);
                remote.Client.Shutdown(SocketShutdown.Send);
            }
        }
        async Task Serve()
        {
            var tasks = new List<Task>();
            for (int i = 0; i < count; i++) tasks.Add(Echo(await upstream.AcceptTcpClientAsync(token)));
            await Task.WhenAll(tasks);
        }
        Task server = Serve();
        Task[] callers = Enumerable.Range(0, count).Select(async index =>
        {
            using var client = new TcpClient { NoDelay = true };
            await Greeting(client, relay.Port, token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
            byte[] reply = new byte[10]; await stream.ReadExactlyAsync(reply, token);
            if (reply[1] != 0) throw new IOException("Load connection refused");
            if (Interlocked.Increment(ref connected) == count) ready.TrySetResult();
            await send.Task.WaitAsync(token);
            var elapsed = Stopwatch.StartNew();
            byte[] payload = new byte[bytes]; new Random(index).NextBytes(payload);
            async Task Write()
            {
                await stream.WriteAsync(payload, token);
                client.Client.Shutdown(SocketShutdown.Send);
            }
            Task writer = Write();
            byte[] echoed = new byte[bytes]; await stream.ReadExactlyAsync(echoed, token);
            if (!payload.AsSpan().SequenceEqual(echoed)) throw new IOException("Concurrent transfer corrupted data");
            if (await stream.ReadAsync(new byte[1], token) != 0) throw new IOException("Missing EOF");
            await writer;
            times.Add(elapsed.Elapsed.TotalMilliseconds);
        }).ToArray();
        try
        {
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), token);
            check(relay.ActiveTransfers == count, "128 concurrent forwarding streams remain open together");
            relay.SetUpstream(upstreamPort, null);
            using (var health = new TcpClient())
            {
                await Greeting(health, relay.Port, token);
                var stream = health.GetStream();
                byte[] host = Encoding.ASCII.GetBytes(RelaySocksServer.HealthHost);
                await stream.WriteAsync(new byte[] { 5, 1, 0, 3, (byte)host.Length }.Concat(host).Concat(new byte[] { 0, 80 }).ToArray(), token);
                await stream.ReadExactlyAsync(new byte[10], token);
                await stream.WriteAsync("HEAD /tcp HTTP/1.1\r\nConnection: close\r\n\r\n"u8.ToArray(), token);
                using var reader = new StreamReader(stream, Encoding.ASCII);
                check(await reader.ReadLineAsync(token) == "HTTP/1.1 204 No Content", "health probe succeeds while all 128 forwarding slots are occupied");
            }
            using (var excess = new TcpClient())
            {
                await Greeting(excess, relay.Port, token);
                var stream = excess.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
                byte[] reply = new byte[10]; await stream.ReadExactlyAsync(reply, token);
                check(reply[1] == 2 && relay.RejectedConnections == 1, "overload returns a SOCKS failure and increments the rejection counter");
            }
        }
        finally { send.TrySetResult(); }
        await Task.WhenAll(callers.Append(server));
        for (int i = 0; i < 300 && relay.ActiveConnections != 0; i++) await Task.Delay(10, token);
        check(relay.ActiveConnections == 0 && relay.ActiveTransfers == 0, "load completion releases all sockets and transfer slots");
        double seconds = clock.Elapsed.TotalSeconds;
        double p95 = times.Order().ElementAt((int)Math.Ceiling(count * .95) - 1);
        check(true, $"loopback load: {count} streams, {count * bytes / 1048576} MiB each direction, {seconds:F2}s including setup, p95 transfer {p95:F0}ms, working-set delta {(Process.GetCurrentProcess().WorkingSet64 - baseline) / 1048576} MiB; not an SSH throughput guarantee");
    }
    private static async Task Greeting(TcpClient client, int port, CancellationToken token)
    {
        await client.ConnectAsync(IPAddress.Loopback, port, token);
        await client.GetStream().WriteAsync(new byte[] { 5, 1, 0 }, token);
        byte[] reply = new byte[2]; await client.GetStream().ReadExactlyAsync(reply, token);
        if (!reply.AsSpan().SequenceEqual(new byte[] { 5, 0 })) throw new IOException("SOCKS greeting failed");
    }
}
