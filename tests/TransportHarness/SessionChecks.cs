using System.Net;
using System.Net.Sockets;
using Host2VMRelay;

internal static class SessionChecks
{
    public static async Task RunAsync(Action<bool, string> check, CancellationToken token)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var options = new RelayConnectionOptions("127.0.0.1", port, "test", 0, false, "", "", true);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
        Task<RelaySession> opening = RelaySession.OpenAsync(options, _ => throw new IOException("Unexpected host key"), _ => { }, cancel.Token);
        using var peer = await listener.AcceptTcpClientAsync(token);
        // A peer that never sends an SSH banner must not hold shutdown hostage.
        cancel.Cancel();
        bool failed = false;
        try
        {
            using var unexpected = await opening.WaitAsync(TimeSpan.FromSeconds(3), token);
        }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested && !token.IsCancellationRequested) { failed = true; }
        check(failed, "cancelled SSH startup completes without publishing a session");
        var buffer = new byte[256];
        bool closed = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(TimeSpan.FromSeconds(3));
            while (await peer.GetStream().ReadAsync(buffer, deadline.Token) != 0) { }
            closed = true;
        }
        catch (IOException) { closed = true; }
        check(closed, "cancelled SSH startup closes its partially initialized socket");
    }
}
