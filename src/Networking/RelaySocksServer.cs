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
    private readonly SemaphoreSlim slots = new(128);
    public int Port => ((IPEndPoint)listener.LocalEndpoint).Port;
    public bool TcpHealthy { get { var s = Volatile.Read(ref upstream); return s.Port > 0 && Environment.TickCount64 < s.Expires; } }
    public bool UdpHealthy => Volatile.Read(ref upstream).Udp?.Healthy == true;

    public RelaySocksServer(int port)
    {
        listener = new TcpListener(IPAddress.Loopback, port); listener.Start(128); _ = AcceptAsync();
    }
    public void SetUpstream(int tcpPort, UdpTunnel? udp)
    {
        Volatile.Write(ref upstream, new Upstream(tcpPort, udp, Environment.TickCount64 + 9000));
    }
    private async Task AcceptAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                var client = await listener.AcceptTcpClientAsync(lifetime.Token).ConfigureAwait(false);
                if (!await slots.WaitAsync(0, lifetime.Token).ConfigureAwait(false)) { client.Dispose(); continue; }
                clients.TryAdd(client, 0); _ = ServeAsync(client);
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
    private async Task ServeAsync(TcpClient client)
    {
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
                    if (!TcpHealthy) { await stream.WriteAsync(Reply(1), token).ConfigureAwait(false); return; }
                    using var remote = new TcpClient { NoDelay = true };
                    await remote.ConnectAsync(IPAddress.Loopback, state.Port, token).ConfigureAwait(false);
                    var tunnel = remote.GetStream();
                    await tunnel.WriteAsync(new byte[] { 5, 1, 0 }, token).ConfigureAwait(false);
                    if (!(await ReadAsync(tunnel, 2, token).ConfigureAwait(false)).AsSpan().SequenceEqual(new byte[] { 5, 0 })) throw new IOException("SSH SOCKS handshake failed.");
                    await tunnel.WriteAsync(new byte[] { 5, 1, 0 }.Concat(address).ToArray(), token).ConfigureAwait(false);
                    byte[] header = await ReadAsync(tunnel, 4, token).ConfigureAwait(false);
                    byte[] bound = await AddressAsync(tunnel, header[3], token).ConfigureAwait(false);
                    await stream.WriteAsync(header[..3].Concat(bound).ToArray(), token).ConfigureAwait(false);
                    if (header[1] != 0) return;
                    tokenSource.CancelAfter(Timeout.InfiniteTimeSpan);
                    Task up = CopyAsync(stream, tunnel, remote, token), down = CopyAsync(tunnel, stream, client, token);
                    await Task.WhenAll(up, down).ConfigureAwait(false);
                }
                else await stream.WriteAsync(Reply(7), token).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException) { }
            finally { clients.TryRemove(client, out _); slots.Release(); }
        }
    }
    private static async Task CopyAsync(Stream source, Stream destination, TcpClient target, CancellationToken token)
    {
        await source.CopyToAsync(destination, token).ConfigureAwait(false);
        try { target.Client.Shutdown(SocketShutdown.Send); } catch (SocketException) { }
    }
    private async Task HealthAsync(Stream stream, CancellationToken token)
    {
        using var request = new MemoryStream(); byte[] one = new byte[1];
        while (request.Length < 8192)
        {
            await stream.ReadExactlyAsync(one, token).ConfigureAwait(false); request.WriteByte(one[0]);
            if (request.Length >= 4 && request.GetBuffer().AsSpan((int)request.Length - 4, 4).SequenceEqual("\r\n\r\n"u8)) break;
        }
        string line = Encoding.ASCII.GetString(request.ToArray()).Split('\n')[0];
        bool healthy = line.Contains(" /tcp ", StringComparison.Ordinal) ? TcpHealthy : line.Contains(" /udp ", StringComparison.Ordinal) && UdpHealthy;
        byte[] response = Encoding.ASCII.GetBytes("HTTP/1.1 " + (healthy ? "204 No Content" : "503 Unavailable") + "\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        await stream.WriteAsync(response, token).ConfigureAwait(false);
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
        uint id = tunnel.Register(packet => replies.Writer.TryWrite(packet));
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
                    stop.CancelAfter(TimeSpan.FromMinutes(2)); tunnel.Send(id, incoming.Buffer);
                }
            }
            async Task Send()
            {
                await foreach (byte[] packet in replies.Reader.ReadAllAsync(stop.Token).ConfigureAwait(false))
                {
                    var destination = Volatile.Read(ref peer);
                    if (destination is null || !ValidDatagram(packet)) continue;
                    await socket.SendAsync(packet, destination, stop.Token).ConfigureAwait(false);
                }
            }
            stop.CancelAfter(TimeSpan.FromMinutes(2));
            Task receive = Receive(), send = Send();
            Task closed = control.GetStream().ReadAsync(new byte[1], stop.Token).AsTask();
            await Task.WhenAny(receive, send, closed).ConfigureAwait(false);
            stop.Cancel(); socket.Dispose(); control.Dispose();
            try { await Task.WhenAll(receive, send, closed).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        }
        finally { stop.Cancel(); tunnel.Unregister(id); replies.Writer.TryComplete(); }
    }
    public void Dispose()
    {
        if (lifetime.IsCancellationRequested) return;
        lifetime.Cancel(); listener.Stop(); foreach (var client in clients.Keys) client.Dispose();
    }
}
