using System.Net;
using System.Net.Sockets;
using System.Text;
using Host2VMRelay;

internal static class HealthChecks
{
    public static async Task RunAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var token = timeout.Token;
        using var relay = new RelaySocksServer(0);
        relay.SetUpstream(12345, null);
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, relay.Port, token);
        var stream = client.GetStream();
        await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
        await stream.ReadExactlyAsync(new byte[2], token);
        var host = Encoding.ASCII.GetBytes(RelaySocksServer.HealthHost);
        await stream.WriteAsync(new byte[] { 5, 1, 0, 3, (byte)host.Length }.Concat(host).Concat(new byte[] { 0, 80 }).ToArray(), token);
        var reply = new byte[10]; await stream.ReadExactlyAsync(reply, token);
        if (reply[1] != 0) throw new IOException("Health SOCKS connect failed");
        using var reader = new StreamReader(stream, Encoding.ASCII, false, 1024, true);
        async Task Probe(string path, int status, bool close = false)
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes($"HEAD /{path} HTTP/1.1\r\nHost: {RelaySocksServer.HealthHost}\r\nConnection: {(close ? "close" : "keep-alive")}\r\n\r\n"), token);
            string? line = await reader.ReadLineAsync(token);
            if (line != $"HTTP/1.1 {status} {(status == 204 ? "No Content" : "Unavailable")}") throw new IOException("Unexpected probe response: " + line);
            while (!string.IsNullOrEmpty(await reader.ReadLineAsync(token))) { }
            Console.WriteLine($"PASS same connection /{path}: {status}");
        }
        await Probe("tcp", 204);
        await Probe("tcp", 204);
        await Probe("udp", 503);
        relay.SetUpstream(0, null);
        await Probe("tcp", 503, true);
        if (await reader.ReadLineAsync(token) is not null) throw new IOException("Close request was ignored");
        Console.WriteLine("PASS health changes are reevaluated and explicit close is honored");
    }
}
