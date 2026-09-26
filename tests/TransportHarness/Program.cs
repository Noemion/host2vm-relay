using Host2VMRelay;
using Renci.SshNet;
using System.Security.Cryptography;
using System.Text.Json;

if (args.Length == 2 && args[0] == "--local-check") { await LocalTransportChecks.RunAsync(args[1]); return; }
if (args.Length < 6) throw new ArgumentException("host sshPort user privateKey fingerprint scriptOutput");
using var key = new PrivateKeyFile(args[3]);
using var client = new SshClient(new ConnectionInfo(args[0], int.Parse(args[1]), args[2], new PrivateKeyAuthenticationMethod(args[2], key)) { Timeout = TimeSpan.FromSeconds(3) });
client.HostKeyReceived += (_, e) => e.CanTrust = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=') == args[4];
client.KeepAliveInterval = TimeSpan.FromSeconds(2);
using var relay = new RelaySocksServer(18090);
using var stop = new CancellationTokenSource();
ForwardedPortDynamic? forward = null;
UdpTunnel? udp = null;
File.WriteAllText(args[5], ClashScript.Generate(relay.Port, args[0]));
Console.WriteLine(JsonSerializer.Serialize(new { port = relay.Port, starting = true }));
while (!stop.IsCancellationRequested)
{
    try
    {
        if (!client.IsConnected)
        {
            forward?.Dispose(); udp?.Dispose(); udp = null;
            client.Connect(); forward = new ForwardedPortDynamic("127.0.0.1", 0);
            client.AddForwardedPort(forward); forward.Start();
        }
        using (var probe = client.CreateCommand("printf h2vm-alive"))
        {
            probe.CommandTimeout = TimeSpan.FromSeconds(2);
            if (probe.Execute() != "h2vm-alive") throw new IOException("SSH probe failed");
        }
        if (udp is null || !await udp.ProbeAsync())
        {
            udp?.Dispose(); udp = null;
            try { udp = await UdpTunnel.StartAsync(client); } catch (Exception ex) { Console.Error.WriteLine("UDP: " + ex.Message); }
        }
        relay.SetUpstream((int)forward!.BoundPort, udp);
        Console.WriteLine(JsonSerializer.Serialize(new { tcp = relay.TcpHealthy, udp = relay.UdpHealthy }));
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("RECONNECT: " + ex.Message);
        relay.SetUpstream(0, null); udp?.Dispose(); udp = null;
        try { client.Disconnect(); } catch { }
    }
    await Task.Delay(1500);
}
