using System.Buffers.Binary;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json;
using Host2VMRelay;

internal static class LocalTransportChecks
{
    public static async Task RunAsync(string resultPath)
    {
        resultPath = Path.GetFullPath(resultPath);
        Directory.CreateDirectory(Path.GetDirectoryName(resultPath)!);
        var checks = new List<string>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = timeout.Token;
        void Check(bool ok, string label)
        {
            if (!ok) throw new IOException(label);
            checks.Add(label); Console.WriteLine("PASS " + label);
        }
        try
        {
            await ConcurrencyChecks.RunAsync(Check, token);
            await LoadChecks.RunAsync(Check, token, RelaySocksServer.DefaultTransfers);
            await LoadChecks.RunAsync(Check, token);
            await HealthChecks.RunAsync();
            Check(true, "health endpoint supports repeated probes and state changes on one connection");
            await TcpRelayChecks.RunAsync(Check, token);
            await SessionChecks.RunAsync(Check, token);
            string agent = Path.Combine(AppContext.BaseDirectory, "native", OperatingSystem.IsWindows() ? "h2vm-agent-test.exe" : "h2vm-agent-linux-x64");
            var start = new ProcessStartInfo(agent)
            {
                UseShellExecute = false, RedirectStandardInput = true,
                RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true
            };
            using var process = Process.Start(start) ?? throw new IOException("Could not start isolated bridge test");
            Task<string> stderr = process.StandardError.ReadToEndAsync();
            try
            {
                using var tunnel = await UdpTunnel.OpenStreamsAsync(process.StandardInput.BaseStream, process.StandardOutput.BaseStream,
                    () => { if (!process.HasExited) process.Kill(); }, token);
                Check(tunnel.Healthy && await tunnel.ProbeAsync(token), "native host streams exchange a nonce checked by real UDP");
                var associations = new System.Collections.Concurrent.ConcurrentBag<uint>();
                Parallel.For(0, UdpTunnel.MaxAssociations * 2, _ =>
                {
                    try { associations.Add(tunnel.Register(_ => { })); }
                    catch (IOException) { }
                });
                Check(associations.Count == UdpTunnel.MaxAssociations, "4096 concurrent UDP registrations respect the atomic 2048-association limit");
                foreach (uint association in associations) tunnel.Unregister(association);
                uint reused = tunnel.Register(_ => { }); tunnel.Unregister(reused);
                Check(true, "UDP registration capacity is reusable after concurrent release");
                using var relay = new RelaySocksServer(0, TimeSpan.FromSeconds(1));
                relay.SetUpstream(0, tunnel);
                using var echo = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                using var packets = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                using var control = new TcpClient();
                await control.ConnectAsync(IPAddress.Loopback, relay.Port, token);
                var stream = control.GetStream();
                await stream.WriteAsync(new byte[] { 5, 1, 0 }, token);
                var greeting = new byte[2]; await stream.ReadExactlyAsync(greeting, token);
                Check(greeting.AsSpan().SequenceEqual(new byte[] { 5, 0 }), "native SOCKS handshake");
                int local = ((IPEndPoint)packets.Client.LocalEndPoint!).Port;
                await stream.WriteAsync(new byte[] { 5, 3, 0, 1, 127, 0, 0, 1, (byte)(local >> 8), (byte)local }, token);
                var reply = new byte[10]; await stream.ReadExactlyAsync(reply, token);
                Check(reply[1] == 0 && reply[3] == 1, "native UDP ASSOCIATE succeeds");
                var destination = new IPEndPoint(IPAddress.Loopback, BinaryPrimitives.ReadUInt16BigEndian(reply.AsSpan(8)));
                int echoPort = ((IPEndPoint)echo.Client.LocalEndPoint!).Port;
                foreach (var payload in new[] { Array.Empty<byte>(), RandomNumberGenerator.GetBytes(1200) })
                {
                    byte[] datagram = new byte[10 + payload.Length];
                    datagram[3] = 1; datagram[4] = 127; datagram[7] = 1;
                    BinaryPrimitives.WriteUInt16BigEndian(datagram.AsSpan(8), (ushort)echoPort); payload.CopyTo(datagram, 10);
                    await packets.SendAsync(datagram, destination, token);
                    var request = await echo.ReceiveAsync(token);
                    Check(request.Buffer.AsSpan().SequenceEqual(payload), "native UDP request preserves " + payload.Length + " bytes");
                    await echo.SendAsync(request.Buffer, request.RemoteEndPoint, token);
                    var response = await packets.ReceiveAsync(token);
                    Check(response.Buffer.AsSpan().SequenceEqual(datagram), "native UDP response preserves address and " + payload.Length + " bytes");
                }
                // Subscribe once, then receive only. Each reply must renew the idle timer.
                byte[] subscription = { 0, 0, 0, 1, 127, 0, 0, 1, (byte)(echoPort >> 8), (byte)echoPort, 42 };
                await packets.SendAsync(subscription, destination, token);
                var subscriber = await echo.ReceiveAsync(token);
                bool receivedAll = true;
                for (int i = 0; i < 8; i++)
                {
                    await Task.Delay(250, token);
                    await echo.SendAsync(new byte[] { (byte)i }, subscriber.RemoteEndPoint, token);
                    var pushed = await packets.ReceiveAsync(token);
                    receivedAll &= pushed.Buffer[^1] == i;
                }
                Check(receivedAll, "receive-only UDP remains active beyond its idle timeout");
                // No traffic now: genuine idle associations must still release their slot.
                int eof = await stream.ReadAsync(new byte[1], token);
                Check(eof == 0, "idle UDP association closes its control channel");
                for (int i = 0; i < 100 && relay.ActiveConnections != 0; i++) await Task.Delay(10, token);
                Check(relay.ActiveConnections == 0, "idle UDP association releases its admission slot");
                Check(await tunnel.ProbeAsync(token), "UDP tunnel remains healthy after association timeout");
                Check(tunnel.ReplyBudget.Used == 0, "UDP shutdown releases the shared reply-byte budget");
                Check(!relay.TcpHealthy && relay.UdpHealthy, "transport health is independent");
                process.Kill(); await process.WaitForExitAsync(token);
                for (int i = 0; i < 20 && tunnel.Healthy; i++) await Task.Delay(50, token);
                Check(!tunnel.Healthy && !relay.UdpHealthy, "helper failure invalidates native UDP health");
            }
            finally
            {
                if (!process.HasExited) process.Kill();
                await process.WaitForExitAsync(CancellationToken.None);
                File.WriteAllText(Path.ChangeExtension(resultPath, ".stderr.txt"), await stderr);
            }
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { Status = "PASS", OS = Environment.OSVersion.ToString(), Checks = checks,
                Scope = "Real native loopback TCP/UDP and production SOCKS/bridge code. Not a Windows TUN or SSH end-to-end test." }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(resultPath, JsonSerializer.Serialize(new { Status = "FAIL", Checks = checks, Error = ex.ToString() }));
            throw;
        }
    }
}
