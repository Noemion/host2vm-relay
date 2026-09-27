using Host2VMRelay;
using System.Text.Json;

if (args.Length == 2 && args[0] == "--local-check") { await LocalTransportChecks.RunAsync(args[1]); return; }
if (args.Length < 6) throw new ArgumentException("host sshPort user privateKey fingerprint scriptOutput");
var options = new RelayConnectionOptions(args[0], int.Parse(args[1]), args[2], 18090, true, args[3], "", true);
RelaySession? session = null;
File.WriteAllText(args[5], ClashScript.Generate(options.SocksPort, args[0]));
Console.WriteLine(JsonSerializer.Serialize(new { port = options.SocksPort, starting = true }));
try
{
    while (true)
    {
        try
        {
            if (session?.IsConnected != true)
            {
                session?.Dispose();
                session = await RelaySession.OpenAsync(options, fingerprint => fingerprint == args[4], Console.Error.WriteLine);
            }
            if (!await session.RefreshAsync(true)) throw new IOException("SSH probe failed");
            var health = session.Health;
            Console.WriteLine(JsonSerializer.Serialize(new { tcp = health.Tcp, udp = health.Udp }));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("RECONNECT: " + ex.Message);
            session?.Dispose();
            session = null;
        }
        await Task.Delay(1500);
    }
}
finally { session?.Dispose(); }
