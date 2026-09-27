using System.Net;
using System.Net.Sockets;
using System.Text;
using Host2VMRelay;

internal static class RustSessionChecks
{
    // Called by the isolated Linux SSH fixture; all destination services belong
    // to that fixture. This exercises the actual Windows UI-facing session API.
    public static async Task RunAsync(string[] args)
    {
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(70));
        var token = deadline.Token;
        var options = new RelayConnectionOptions(args[0], int.Parse(args[1]), args[2], 0, true, args[3], "", true);
        using var session = await RelaySession.OpenAsync(options, fingerprint => fingerprint == args[4], Console.Error.WriteLine, token);
        session.StartMonitoring(true);
        if (!session.IsConnected || !session.Health.Tcp || !session.Health.Udp) throw new IOException("Session not healthy");
        await Task.WhenAll(Enumerable.Range(0, 32).Select(async i =>
        {
            using var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, session.SocksPort, token);
            var stream = client.GetStream();
            await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
            byte[] response = new byte[2]; await stream.ReadExactlyAsync(response, token);
            if (!response.AsSpan().SequenceEqual(new byte[] { 5, 0 })) throw new IOException("SOCKS greeting failed");
            int port = int.Parse(args[5]);
            await stream.WriteAsync(new byte[] { 5, 1, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port }, token);
            response = new byte[10]; await stream.ReadExactlyAsync(response, token);
            if (response[1] != 0) throw new IOException("SSH forwarding failed");
            byte[] body = Encoding.UTF8.GetBytes("windows-session-" + i);
            await stream.WriteAsync(body, token); client.Client.Shutdown(SocketShutdown.Send);
            using var memory = new MemoryStream(); await stream.CopyToAsync(memory, token);
            if (!memory.ToArray().AsSpan().SequenceEqual(body.Concat(Encoding.UTF8.GetBytes("after-eof")).ToArray()))
                throw new IOException("Half-close lost response");
        }));
        if (!await session.RefreshAsync(true)) throw new IOException("Session health refresh failed");
        session.Dispose();
        await session.DisposalCompleted.WaitAsync(TimeSpan.FromSeconds(5), token);
        if (session.IsConnected || session.Health.Tcp || session.Health.Udp) throw new IOException("Disposed session remained healthy");
        Console.WriteLine("PASS Windows RelaySession: strict host key, helper deployment, TCP/UDP health, 32 SSH transfers, half-close, disposal");
    }
}
