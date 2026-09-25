using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Host2VMRelay;

// Loopback-only rule feed. No controller credentials or passwords are exposed.
public sealed class RuleServer : IDisposable
{
    private readonly TcpListener listener;
    public RuleServer(int port = 17861) { listener = new TcpListener(IPAddress.Loopback, port); }
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    private readonly CancellationTokenSource stop = new();
    public string Payload = "# No forwarding rules\n";
    public DateTime LastReadUtc;
    public void Start() { listener.Start(); _ = Run(); }
    private async Task Run()
    {
        try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); _ = Serve(client); } }
        catch (OperationCanceledException) { }
        catch (SocketException) when (stop.IsCancellationRequested) { }
    }
    private async Task Serve(TcpClient client)
    {
        using (client)
        using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token))
        {
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            try
            {
                var stream = client.GetStream();
                var bytes = new byte[4096]; int count = 0;
                while (count < bytes.Length)
                {
                    int n = await stream.ReadAsync(bytes.AsMemory(count), timeout.Token);
                    if (n == 0) return;
                    count += n;
                    if (Encoding.ASCII.GetString(bytes, 0, count).Contains("\r\n\r\n")) break;
                }
                var line = Encoding.ASCII.GetString(bytes, 0, count).Split("\r\n")[0];
                bool ok = line == "GET /rules.txt HTTP/1.1" || line == "GET /rules.txt HTTP/1.0";
                var body = Encoding.UTF8.GetBytes(ok ? Volatile.Read(ref Payload) : "Not found\n");
                if (ok) LastReadUtc = DateTime.UtcNow;
                var header = Encoding.ASCII.GetBytes("HTTP/1.1 " + (ok ? "200 OK" : "404 Not Found") + "\r\nContent-Type: text/plain; charset=utf-8\r\nCache-Control: no-store\r\nContent-Length: " + body.Length + "\r\nConnection: close\r\n\r\n");
                await stream.WriteAsync(header, timeout.Token);
                await stream.WriteAsync(body, timeout.Token);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException or SocketException) { }
        }
    }
    public void Dispose() { stop.Cancel(); listener.Stop(); }
}
