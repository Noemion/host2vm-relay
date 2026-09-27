using System.Net;
using System.Net.Sockets;
using Host2VMRelay;

internal static class TcpRelayChecks
{
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        using var upstream = new TcpListener(IPAddress.Loopback, 0);
        upstream.Start();
        var events = new System.Collections.Concurrent.ConcurrentQueue<string>();
        using var relay = new RelaySocksServer(0, log: events.Enqueue);
        int port = ((IPEndPoint)upstream.LocalEndpoint).Port;

        async Task<TcpClient> Connect(TcpClient caller)
        {
            relay.SetUpstream(port, null);
            await caller.ConnectAsync(IPAddress.Loopback, relay.Port, token);
            var front = caller.GetStream();
            await front.WriteAsync(new byte[] { 5, 1, 0 }, token);
            await front.ReadExactlyAsync(new byte[2], token);
            await front.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
            var remote = await upstream.AcceptTcpClientAsync(token);
            var back = remote.GetStream();
            await back.ReadExactlyAsync(new byte[3], token);
            await back.WriteAsync(new byte[] { 5, 0 }, token);
            await back.ReadExactlyAsync(new byte[10], token);
            await back.WriteAsync(new byte[] { 5, 0, 0, 1, 127, 0, 0, 1, 0, 80 }, token);
            await front.ReadExactlyAsync(new byte[10], token);
            return remote;
        }

        async Task Drained(string scenario)
        {
            for (int i = 0; i < 100 && relay.ActiveConnections != 0; i++)
                await Task.Delay(10, token);
            check(relay.ActiveConnections == 0, scenario);
            check(events.Any(line => line.Contains("127.0.0.1:80")), "TCP forwarding log identifies destination");
        }

        // The response is sent only after EOF, so closing both sides on normal
        // completion would truncate this legitimate request/response exchange.
        using (var caller = new TcpClient())
        using (var remote = await Connect(caller))
        {
            var callerStream = caller.GetStream();
            await callerStream.WriteAsync("request"u8.ToArray(), token);
            caller.Client.Shutdown(SocketShutdown.Send);
            using var request = new MemoryStream();
            await remote.GetStream().CopyToAsync(request, token);
            check(request.ToArray().AsSpan().SequenceEqual("request"u8), "TCP half-close delivers the complete request");
            await Task.Delay(50, token);
            await remote.GetStream().WriteAsync("late response"u8.ToArray(), token);
            remote.Client.Shutdown(SocketShutdown.Send);
            using var response = new MemoryStream();
            await callerStream.CopyToAsync(response, token);
            check(response.ToArray().AsSpan().SequenceEqual("late response"u8), "TCP half-close preserves the delayed reverse response");
        }
        await Drained("TCP half-close completion releases its admission slot");
        check(events.Any(line => line.Contains("客户端→虚拟机") && line.Contains("已转发 7 字节") && line.Contains("EOF")),
            "connection diagnostics identify client EOF and exact forwarded byte count");
        check(events.Any(line => line.Contains("虚拟机→客户端") && line.Contains("已转发 13 字节") && line.Contains("EOF")),
            "connection diagnostics identify upstream EOF without logging payloads");

        foreach (bool resetUpstream in new[] { true, false })
        {
            using var caller = new TcpClient();
            using var remote = await Connect(caller);
            var broken = resetUpstream ? remote : caller;
            broken.Client.LingerState = new LingerOption(true, 0);
            // TcpClient.Dispose first calls Shutdown(Both), which can turn this into
            // a graceful FIN. Close the socket directly to inject an actual reset.
            broken.Client.Dispose();
            // The other endpoint deliberately remains open and silent.
            await Drained(resetUpstream ? "upstream TCP reset cancels a silent client read and releases its slot"
                : "client TCP reset cancels a silent upstream read and releases its slot");
        }

        using (var caller = new TcpClient())
        using (var remote = await Connect(caller))
        {
            relay.Dispose();
            await Drained("server disposal drains both TCP copy tasks and releases their slot");
        }
    }
}
