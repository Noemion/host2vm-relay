using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading.Channels;

namespace Host2VMRelay;

/// <summary>Loopback SOCKS5 endpoint. TCP uses SSH.NET; UDP uses the session-scoped bridge.</summary>
internal sealed class RelaySocksServer : IDisposable
{
    public const string HealthHost = "health.host2vm-relay.invalid";
    private sealed record Upstream(int Port, UdpTunnel? Udp, long Expires);
    private Upstream upstream = new(0, null, 0);
    private readonly TcpListener listener;
    private readonly CancellationTokenSource lifetime = new();
    private readonly ConcurrentDictionary<TcpClient, byte> clients = new();
    internal const int MaxTransfers = 2048;
    internal const int DefaultTransfers = 512;
    internal const int MaxConnections = MaxTransfers + 64;
    // Extra front-door capacity lets probes reach HealthAsync when all forwarding
    // slots are occupied. Handshakes remain bounded and have an eight-second deadline.
    private readonly SemaphoreSlim slots;
    private readonly SemaphoreSlim transfers;
    internal int TransferLimit { get; }
    private readonly object admissionGate = new();
    private int disposed;
    private long rejected;
    private readonly TimeSpan udpIdleTimeout;
    private readonly Action<string>? log;
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    internal int ActiveConnections => clients.Count;
    internal int ActiveTransfers => TransferLimit - transfers.CurrentCount;
    internal long RejectedConnections => Interlocked.Read(ref rejected);
    public bool TcpHealthy { get { var s = Volatile.Read(ref upstream); return Volatile.Read(ref disposed) == 0 && s.Port > 0 && Environment.TickCount64 < s.Expires; } }
    public bool UdpHealthy => Volatile.Read(ref disposed) == 0 && Volatile.Read(ref upstream).Udp?.Healthy == true;

    public RelaySocksServer(int port, TimeSpan? udpIdleTimeout = null, Action<string>? log = null, int maxTransfers = DefaultTransfers)
    {
        if (maxTransfers is < 1 or > MaxTransfers) throw new ArgumentOutOfRangeException(nameof(maxTransfers));
        TransferLimit = maxTransfers;
        slots = new(maxTransfers + 64);
        transfers = new(maxTransfers);
        this.log = log;
        this.udpIdleTimeout = udpIdleTimeout ?? TimeSpan.FromMinutes(2);
        if (this.udpIdleTimeout <= TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(udpIdleTimeout));
        listener = new TcpListener(IPAddress.Loopback, port); listener.Start(maxTransfers + 64); _ = AcceptAsync();
    }
    public void SetUpstream(int tcpPort, UdpTunnel? udp)
    {
        Volatile.Write(ref upstream, new Upstream(tcpPort, udp, Environment.TickCount64 + (long)RelayHealthTiming.Lease.TotalMilliseconds));
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                // Admission is nonblocking. A cancelled token here would otherwise
                // throw after accept and leak the newly accepted socket.
                lock (admissionGate)
                {
                    // Dispose and admission are one transaction: a socket accepted
                    // during shutdown must not escape the disposal snapshot.
                    if (disposed != 0 || !slots.Wait(0))
                    { Interlocked.Increment(ref rejected); client.Dispose(); continue; }
                    clients.TryAdd(client, 0);
                }
                _ = ServeAsync(client);
            }
        }
        catch (Exception ex) when (ex is SocketException or ObjectDisposedException or OperationCanceledException) { }
    }
    private static async Task<byte[]> ReadAsync(Stream stream, int size, CancellationToken token)
    {
        byte[] bytes = new byte[size]; await stream.ReadExactlyAsync(bytes, token).ConfigureAwait(false); return bytes;
    }
    private static async Task<byte[]> AddressAsync(Stream stream, byte kind, CancellationToken token)
    {
        int size = kind switch { 1 => 4, 4 => 16, 3 => (await ReadAsync(stream, 1, token).ConfigureAwait(false))[0], _ => throw new IOException("Unsupported SOCKS address.") };
        if (size == 0) throw new IOException("Empty SOCKS address.");
        byte[] rest = await ReadAsync(stream, size + 2, token).ConfigureAwait(false);
        return kind == 3 ? new[] { kind, (byte)size }.Concat(rest).ToArray() : new[] { kind }.Concat(rest).ToArray();
    }
    private static string Host(byte[] address) => address[0] switch
    {
        1 => new IPAddress(address.AsSpan(1, 4)).ToString(),
        4 => new IPAddress(address.AsSpan(1, 16)).ToString(),
        3 => Encoding.ASCII.GetString(address, 2, address[1]),
        _ => throw new IOException("Invalid address.")
    };
    private static byte[] Reply(byte code, int port = 0) => new byte[] { 5, code, 0, 1, 127, 0, 0, 1, (byte)(port >> 8), (byte)port };
    private void Trace(string message)
    {
        // Diagnostic callbacks must never interrupt transport or expose payloads.
        try { log?.Invoke(message.Replace('\r', ' ').Replace('\n', ' ')); } catch { }
    }
    private static string Destination(byte[] address) => Host(address) + ":" + BinaryPrimitives.ReadUInt16BigEndian(address.AsSpan(address.Length - 2));
    private async Task ServeAsync(TcpClient client)
    {
        string? target = null;
        bool transferSlot = false;
        using (client)
        using (var tokenSource = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token))
        {
            try
            {
                client.NoDelay = true; tokenSource.CancelAfter(TimeSpan.FromSeconds(8)); var token = tokenSource.Token;
                Stream stream = client.GetStream(); byte[] hello = await ReadAsync(stream, 2, token).ConfigureAwait(false);
                if (hello[0] != 5 || hello[1] == 0) return;
                byte[] methods = await ReadAsync(stream, hello[1], token).ConfigureAwait(false);
                if (!methods.Contains((byte)0)) { await stream.WriteAsync(new byte[] { 5, 255 }, token).ConfigureAwait(false); return; }
                await stream.WriteAsync(new byte[] { 5, 0 }, token).ConfigureAwait(false);
                byte[] request = await ReadAsync(stream, 4, token).ConfigureAwait(false);
                if (request[0] != 5 || request[2] != 0) return;
                byte[] address = await AddressAsync(stream, request[3], token).ConfigureAwait(false);
                if (request[1] == 1 && Host(address).Equals(HealthHost, StringComparison.OrdinalIgnoreCase) && BinaryPrimitives.ReadUInt16BigEndian(address.AsSpan(address.Length - 2)) == 80)
                {
                    await stream.WriteAsync(Reply(0), token).ConfigureAwait(false); await HealthAsync(stream, token).ConfigureAwait(false); return;
                }
                if (request[1] == 1) { target = Destination(address); Trace("TCP 请求：" + target); }
                if (request[1] is 1 or 3)
                {
                    transferSlot = transfers.Wait(0);
                    if (!transferSlot)
                    {
                        Interlocked.Increment(ref rejected);
                        Trace("转发连接数已达到上限，拒绝本次请求。");
                        await stream.WriteAsync(Reply(2), token).ConfigureAwait(false);
                        return;
                    }
                }
                Upstream state = Volatile.Read(ref upstream);
                if (request[1] == 3)
                {
                    if (state.Udp?.Healthy != true) { await stream.WriteAsync(Reply(1), token).ConfigureAwait(false); return; }
                    string host = Host(address);
                    if (!IPAddress.TryParse(host, out var ip) || !(IPAddress.IsLoopback(ip) || ip.Equals(IPAddress.Any) || ip.Equals(IPAddress.IPv6Any)))
                    { await stream.WriteAsync(Reply(2), token).ConfigureAwait(false); return; }
                    tokenSource.CancelAfter(Timeout.InfiniteTimeSpan);
                    await UdpAsync(client, state.Udp, BinaryPrimitives.ReadUInt16BigEndian(address.AsSpan(address.Length - 2)), token).ConfigureAwait(false);
                }
                else if (request[1] == 1)
                {
                    if (state.Port <= 0 || Environment.TickCount64 >= state.Expires) { Trace("TCP 拒绝：" + target + "，隧道尚未就绪。"); await stream.WriteAsync(Reply(1), token).ConfigureAwait(false); return; }
                    using var remote = new TcpClient { NoDelay = true };
                    await remote.ConnectAsync(IPAddress.Loopback, state.Port, token).ConfigureAwait(false);
                    var tunnel = remote.GetStream();
                    await tunnel.WriteAsync(new byte[] { 5, 1, 0 }, token).ConfigureAwait(false);
                    if (!(await ReadAsync(tunnel, 2, token).ConfigureAwait(false)).AsSpan().SequenceEqual(new byte[] { 5, 0 })) throw new IOException("SSH SOCKS handshake failed.");
                    await tunnel.WriteAsync(new byte[] { 5, 1, 0 }.Concat(address).ToArray(), token).ConfigureAwait(false);
                    byte[] header = await ReadAsync(tunnel, 4, token).ConfigureAwait(false);
                    if (header[0] != 5 || header[2] != 0) throw new IOException("Invalid upstream SOCKS response.");
                    byte[] bound = await AddressAsync(tunnel, header[3], token).ConfigureAwait(false);
                    await stream.WriteAsync(header[..3].Concat(bound).ToArray(), token).ConfigureAwait(false);
                    if (header[1] != 0) { Trace("TCP 转发失败：" + target + "，上游 SOCKS 状态 " + header[1]); return; }
                    Trace("TCP 已建立虚拟机转发：" + target);
                    tokenSource.CancelAfter(Timeout.InfiniteTimeSpan);
                    await DuplexRelay.RunAsync(stream, tunnel,
                        () => client.Client.Shutdown(SocketShutdown.Send),
                        () => remote.Client.Shutdown(SocketShutdown.Send),
                        () => { client.Dispose(); remote.Dispose(); }, token,
                        (fromClient, bytes, reason) => Trace("TCP " + (fromClient ? "客户端→虚拟机" : "虚拟机→客户端") +
                            "：" + target + "，已转发 " + bytes + " 字节，" + reason)).ConfigureAwait(false);
                }
                else await stream.WriteAsync(Reply(7), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException)
            { if (target is not null && !lifetime.IsCancellationRequested) Trace("TCP 连接结束：" + target + "，" + ex.Message); }
            finally
            {
                if (target is not null) Trace("TCP 已关闭：" + target);
                if (transferSlot) transfers.Release();
                clients.TryRemove(client, out _); slots.Release();
            }
        }
    }
    private async Task HealthAsync(Stream stream, CancellationToken token)
    {
        // Mihomo unified-delay sends a second HEAD over the same SOCKS stream.
        // Bound both header size and request count; the caller's deadline also
        // limits idle connections. Reevaluate health for every request.
        byte[] one = new byte[1];
        for (int count = 0; count < 16; count++)
        {
            using var request = new MemoryStream();
            bool complete = false;
            while (request.Length < 8192)
            {
                if (await stream.ReadAsync(one, token).ConfigureAwait(false) == 0) return;
                request.WriteByte(one[0]);
                if (request.Length >= 4 && request.GetBuffer().AsSpan((int)request.Length - 4, 4).SequenceEqual("\r\n\r\n"u8))
                { complete = true; break; }
            }
            if (!complete) return;
            string[] lines = Encoding.ASCII.GetString(request.ToArray()).Split("\r\n", StringSplitOptions.None);
            string[] first = lines[0].Split(' ');
            if (first.Length != 3 || (first[0] != "HEAD" && first[0] != "GET")) return;
            bool close = first[2] != "HTTP/1.1" || count == 15;
            foreach (string header in lines.Skip(1))
            {
                int colon = header.IndexOf(':');
                if (colon < 0) continue;
                string name = header[..colon].Trim(), value = header[(colon + 1)..].Trim();
                // Only bodyless probes are supported. Never parse a body as the
                // next request or leave bytes that corrupt connection reuse.
                if (name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase) ||
                    (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) && value != "0")) return;
                if (name.Equals("Connection", StringComparison.OrdinalIgnoreCase) &&
                    value.Split(',').Any(v => v.Trim().Equals("close", StringComparison.OrdinalIgnoreCase))) close = true;
            }
            bool healthy = first[1] == "/tcp" ? TcpHealthy : first[1] == "/udp" && UdpHealthy;
            byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + (healthy ? "204 No Content" : "503 Unavailable") +
                "\r\nContent-Length: 0\r\nConnection: " + (close ? "close" : "keep-alive") + "\r\n\r\n");
            await stream.WriteAsync(response, token).ConfigureAwait(false);
            if (close) return;
        }
    }

    private static bool ValidDatagram(byte[] packet)
    {
        if (packet.Length < 7 || packet.Length > 65769 || packet[0] != 0 || packet[1] != 0 || packet[2] != 0) return false;
        int addressEnd = packet[3] switch { 1 => 8, 4 => 20, 3 => packet[4] == 0 ? int.MaxValue : 5 + packet[4], _ => int.MaxValue };
        return addressEnd <= packet.Length - 2 && packet.Length - addressEnd - 2 <= 65507 && BinaryPrimitives.ReadUInt16BigEndian(packet.AsSpan(addressEnd, 2)) != 0;
    }
    private async Task UdpAsync(TcpClient control, UdpTunnel tunnel, int expectedPort, CancellationToken token)
    {
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(token, tunnel.Stopped);
        using var socket = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var replies = Channel.CreateBounded<byte[]>(64); IPEndPoint? peer = expectedPort == 0 ? null : new(IPAddress.Loopback, expectedPort);
        var reportedTargets = new HashSet<string>(StringComparer.Ordinal);
        uint id = tunnel.Register(packet =>
        {
            if (!tunnel.ReplyBudget.TryReserve(packet.Length)) return;
            if (!replies.Writer.TryWrite(packet)) tunnel.ReplyBudget.Release(packet.Length);
        });
        try
        {
            await control.GetStream().WriteAsync(Reply(0, ((IPEndPoint)socket.Client.LocalEndPoint!).Port), stop.Token).ConfigureAwait(false);
            async Task Receive()
            {
                while (!stop.IsCancellationRequested)
                {
                    UdpReceiveResult incoming = await socket.ReceiveAsync(stop.Token).ConfigureAwait(false);
                    if (!incoming.RemoteEndPoint.Address.Equals(IPAddress.Loopback) || !ValidDatagram(incoming.Buffer)) continue;
                    var expected = Volatile.Read(ref peer);
                    if (expected is not null && !expected.Equals(incoming.RemoteEndPoint)) continue;
                    Interlocked.CompareExchange(ref peer, incoming.RemoteEndPoint, null);
                    // Activity means successfully queued/sent traffic in either direction.
                    if (tunnel.Send(id, incoming.Buffer))
                    {
                        stop.CancelAfter(udpIdleTimeout);
                        // Log the first datagram per destination, not every packet.
                        string destination = Destination(incoming.Buffer[3..(incoming.Buffer[3] == 1 ? 10 : incoming.Buffer[3] == 4 ? 22 : 7 + incoming.Buffer[4])]);
                        if (reportedTargets.Count < 128 && reportedTargets.Add(destination))
                            Trace("UDP 已提交虚拟机转发：" + destination);
                    }
                }
            }
            async Task Send()
            {
                await foreach (byte[] packet in replies.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    try
                    {
                        var destination = Volatile.Read(ref peer);
                        if (destination is null || !ValidDatagram(packet)) continue;
                        await socket.SendAsync(packet, destination, stop.Token).ConfigureAwait(false);
                        stop.CancelAfter(udpIdleTimeout);
                    }
                    finally { tunnel.ReplyBudget.Release(packet.Length); }
                }
            }
            stop.CancelAfter(udpIdleTimeout);
            Task receive = Receive(), send = Send();
            Task closed = control.GetStream().ReadAsync(new byte[1], stop.Token).AsTask();
            await Task.WhenAny(receive, send, closed).ConfigureAwait(false);
            stop.Cancel(); socket.Dispose(); control.Dispose();
            try { await Task.WhenAll(receive, send, closed).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        finally
        {
            stop.Cancel(); tunnel.Unregister(id); replies.Writer.TryComplete();
            while (replies.Reader.TryRead(out var queued)) tunnel.ReplyBudget.Release(queued.Length);
        }
    }
    public void Dispose()
    {
        lock (admissionGate)
        {
            if (disposed != 0) return;
            disposed = 1;
        }
        lifetime.Cancel(); listener.Stop(); foreach (var client in clients.Keys) client.Dispose();
    }
}
