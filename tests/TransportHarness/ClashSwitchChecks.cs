using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using System.Buffers.Binary;
using System.Text;
using Host2VMRelay;

internal static class ClashSwitchChecks
{
    public static async Task RunAsync(string executable)
    {
        string root = Path.Combine(Path.GetTempPath(), "h2vm-switch-" + Guid.NewGuid());
        Directory.CreateDirectory(root);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start(); int port = ((IPEndPoint)reservation.LocalEndpoint).Port; reservation.Stop();
        using var relay = new RelaySocksServer(0);
        using var pairListener = new TcpListener(IPAddress.Loopback, 0);
        pairListener.Start();
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, ((IPEndPoint)pairListener.LocalEndpoint).Port);
        using var peer = await pairListener.AcceptTcpClientAsync();
        pairListener.Stop();
        using var peerStop = new CancellationTokenSource();
        // Exercise UDP health publication and nonce validation without requiring
        // SSH credentials. This fixture does not certify UDP data delivery.
        var peerTask = Task.Run(async () =>
        {
            var stream = peer.GetStream();
            async Task Send(byte[] frame)
            {
                byte[] size = new byte[4]; BinaryPrimitives.WriteInt32BigEndian(size, frame.Length);
                await stream.WriteAsync(size, peerStop.Token); await stream.WriteAsync(frame, peerStop.Token);
            }
            await Send(new byte[] { (byte)'H', 0, 0, 0, 0 }.Concat(Encoding.ASCII.GetBytes("Host2VMRelay-UDP/1")).ToArray());
            try
            {
                while (!peerStop.IsCancellationRequested)
                {
                    byte[] size = new byte[4]; await stream.ReadExactlyAsync(size, peerStop.Token);
                    byte[] frame = new byte[BinaryPrimitives.ReadInt32BigEndian(size)];
                    await stream.ReadExactlyAsync(frame, peerStop.Token);
                    if (frame[0] == (byte)'P') { frame[0] = (byte)'R'; await Send(frame); }
                }
            }
            catch (OperationCanceledException) when (peerStop.IsCancellationRequested) { }
        });
        using var udp = await UdpTunnel.OpenStreamsAsync(client.GetStream(), client.GetStream(), () => { });
        string config = $$"""
            mixed-port: 0
            external-controller: 127.0.0.1:{{port}}
            mode: rule
            log-level: silent
            proxies:
              - {name: Host2VMRelay, type: socks5, server: 127.0.0.1, port: {{relay.Port}}, udp: true}
            proxy-groups:
              - name: Host2VMRelay-TCP
                type: fallback
                proxies: [PASS, Host2VMRelay]
                url: http://health.host2vm-relay.invalid/tcp
                interval: 3
                timeout: 2000
                expected-status: 204
                lazy: false
              - name: Host2VMRelay-UDP
                type: fallback
                proxies: [PASS, Host2VMRelay]
                url: http://health.host2vm-relay.invalid/udp
                interval: 3
                timeout: 2000
                expected-status: 204
                lazy: false
            rules:
              - MATCH,Host2VMRelay-TCP
            """;
        await File.WriteAllTextAsync(Path.Combine(root, "config.yaml"), config);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        start.ArgumentList.Add("-d"); start.ArgumentList.Add(root);
        using var core = Process.Start(start)!;
        using var api = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}/"), Timeout = TimeSpan.FromSeconds(1) };
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        Task? pump = null;
        try
        {
            for (int i = 0; ; i++)
            {
                try { using var response = await api.GetAsync("version", stop.Token); response.EnsureSuccessStatusCode(); break; }
                catch when (i < 40) { await Task.Delay(100, stop.Token); }
            }
            int active = 0;
            pump = Task.Run(async () =>
            {
                while (!stop.IsCancellationRequested)
                {
                    int current = Volatile.Read(ref active);
                    if (current >= 0)
                    {
                        await udp.ProbeAsync(stop.Token);
                        relay.SetUpstream(current == 1 ? 12345 : 0, current == 1 ? udp : null);
                    }
                    await ClashHealthSync.RefreshAsync(api, stop.Token);
                    await Task.Delay(ClashHealthSync.Interval, stop.Token);
                }
            });
            async Task WaitFor(string expected, Stopwatch watch)
            {
                while (watch.ElapsedMilliseconds < 1000)
                {
                    using var state = JsonDocument.Parse(await api.GetStringAsync("proxies/Host2VMRelay-TCP", stop.Token));
                    using var udpState = JsonDocument.Parse(await api.GetStringAsync("proxies/Host2VMRelay-UDP", stop.Token));
                    if (state.RootElement.GetProperty("now").GetString() == expected && udpState.RootElement.GetProperty("now").GetString() == expected)
                    {
                        Console.WriteLine($"PASS real Mihomo TCP/UDP select {expected}: {watch.ElapsedMilliseconds} ms"); return;
                    }
                    await Task.Delay(10, stop.Token);
                }
                throw new IOException("Switch exceeded 1 second: " + expected);
            }
            await WaitFor("PASS", Stopwatch.StartNew());
            for (int i = 0; i < 10; i++)
            {
                await Task.Delay(37 + i * 11, stop.Token);
                Volatile.Write(ref active, 1);
                await WaitFor("Host2VMRelay", Stopwatch.StartNew());
                Volatile.Write(ref active, 0);
                await WaitFor("PASS", Stopwatch.StartNew());
            }
            Volatile.Write(ref active, 1);
            await WaitFor("Host2VMRelay", Stopwatch.StartNew());
            Volatile.Write(ref active, -1);
            await WaitFor("PASS", Stopwatch.StartNew());
            Console.WriteLine("PASS loss of lease renewal falls back within one second");
        }
        finally
        {
            await stop.CancelAsync();
            if (pump is not null) { try { await pump; } catch (OperationCanceledException) { } }
            if (!core.HasExited) { core.Kill(); await core.WaitForExitAsync(); }
            await peerStop.CancelAsync(); await peerTask;
            Directory.Delete(root, true);
        }
    }
}
