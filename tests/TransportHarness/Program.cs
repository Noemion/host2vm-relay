using Host2VMRelay;
using System.Text.Json;

if (args.Length == 3 && args[0] == "--load-relay")
{ await LoadRelayProcess.RunChildAsync(int.Parse(args[1]), int.Parse(args[2])); return; }

if (args.Length == 4 && args[0] == "--ssh-handshake")
{
    int proxyPort = int.Parse(args[3]);
    var connection = new Renci.SshNet.ConnectionInfo(args[1], int.Parse(args[2]), "diagnostic-no-auth",
        Renci.SshNet.ProxyTypes.Socks5, "127.0.0.1", proxyPort, "", "", new Renci.SshNet.NoneAuthenticationMethod("diagnostic-no-auth"))
        { Timeout = TimeSpan.FromSeconds(8) };
    using var ssh = new Renci.SshNet.SshClient(connection);
    bool reachedHostKey = false;
    ssh.HostKeyReceived += (_, e) =>
    {
        reachedHostKey = true;
        Console.WriteLine("SSH key exchange reached: " + e.HostKeyName + "; host key deliberately rejected before authentication.");
        e.CanTrust = false;
    };
    try { await ssh.ConnectAsync(CancellationToken.None); }
    catch (Exception ex) { Console.WriteLine(ex.GetType().Name + ": " + ex.Message); }
    if (!reachedHostKey) Environment.ExitCode = 1;
    return;
}

if (args.Length == 1 && args[0] == "--health-check") { await HealthChecks.RunAsync(); return; }
if (args.Length == 2 && args[0] == "--tcp-check")
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
    for (int i = 0; i < int.Parse(args[1]); i++)
    {
        await TcpRelayChecks.RunAsync((ok, label) => { if (!ok) throw new IOException(label); }, timeout.Token);
        Console.WriteLine($"PASS TCP lifecycle iteration {i + 1}");
    }
    return;
}
if (args.Length == 7 && args[0] == "--session-check") { await RustSessionChecks.RunAsync(args[1..]); return; }
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
