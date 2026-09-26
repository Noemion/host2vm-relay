using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using Renci.SshNet;

namespace Host2VMRelay;

/// <summary>Bounded datagram multiplexing over one authenticated SSH exec channel.</summary>
internal sealed class UdpTunnel : IDisposable
{
    private const int MaxFrame = 66048;
    private readonly Stream input, output;
    private readonly Action release;
    private readonly CancellationTokenSource stopped = new();
    private readonly Channel<byte[]> pending = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentDictionary<uint, Action<byte[]>> receivers = new();
    private readonly ConcurrentDictionary<uint, (byte[] Nonce, TaskCompletionSource<bool> Result)> probes = new();
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long lastProbe;
    private int disposed, nextAssociation;
    public CancellationToken Stopped => stopped.Token;
    public bool Healthy => Volatile.Read(ref disposed) == 0 && Environment.TickCount64 - Interlocked.Read(ref lastProbe) < 7000;
    public string LastError { get; private set; } = "";

    private UdpTunnel(Stream input, Stream output, Action release)
    {
        this.input = input; this.output = output; this.release = release;
        lastProbe = Environment.TickCount64 - 60000;
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
    }

    public static async Task<UdpTunnel> StartAsync(SshClient client, CancellationToken cancellationToken = default)
    {
        using var resource = typeof(UdpTunnel).Assembly.GetManifestResourceStream("Host2VMRelay.UdpBridge")
            ?? throw new IOException("UDP 中继资源缺失。");
        using var memory = new MemoryStream(); resource.CopyTo(memory);
        string encoded = Convert.ToBase64String(memory.ToArray());
        // Only fixed code and Base64 enter the command. No user path or credential is interpolated.
        var command = client.CreateCommand("python3 -I -u -c 'import base64;exec(base64.b64decode(\"" + encoded + "\"))'");
        command.CommandTimeout = Timeout.InfiniteTimeSpan;
        UdpTunnel? tunnel = null;
        try
        {
            Task execution = command.ExecuteAsync(cancellationToken);
            Stream stdin = command.CreateInputStream();
            tunnel = new UdpTunnel(stdin, command.OutputStream, command.Dispose);
            _ = ObserveCommand(execution, tunnel);
            await tunnel.InitializeAsync(cancellationToken).ConfigureAwait(false);
            return tunnel;
        }
        catch { if (tunnel is not null) tunnel.Dispose(); else command.Dispose(); throw; }
    }

    // The stream transport is also used by the cross-platform integration harness.
    internal static async Task<UdpTunnel> OpenStreamsAsync(Stream input, Stream output, Action release, CancellationToken token = default)
    {
        var tunnel = new UdpTunnel(input, output, release);
        try { await tunnel.InitializeAsync(token).ConfigureAwait(false); return tunnel; }
        catch { tunnel.Dispose(); throw; }
    }
    private async Task InitializeAsync(CancellationToken token)
    {
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(8), token).ConfigureAwait(false);
        if (!await ProbeAsync(token).ConfigureAwait(false)) throw new IOException("虚拟机 UDP 自检失败；需要 Python 3 和可用的 UDP 套接字。");
    }
    private static async Task ObserveCommand(Task execution, UdpTunnel tunnel)
    {
        try { await execution.ConfigureAwait(false); tunnel.Fail("虚拟机 UDP 组件已退出。"); }
        catch (Exception ex) { tunnel.Fail(ex.Message); }
    }
    public uint Register(Action<byte[]> receive)
    {
        if (!Healthy || receivers.Count >= 128) throw new IOException("UDP 中继不可用或会话数达到上限。");
        uint id;
        do { id = unchecked((uint)Interlocked.Increment(ref nextAssociation)); } while (id == 0 || !receivers.TryAdd(id, receive));
        return id;
    }
    public void Unregister(uint id)
    {
        receivers.TryRemove(id, out _); Queue((byte)'C', id, Array.Empty<byte>());
    }
    public bool Send(uint id, byte[] datagram) => Healthy && receivers.ContainsKey(id) && Queue((byte)'D', id, datagram);
    private bool Queue(byte opcode, uint id, byte[] body)
    {
        if (Volatile.Read(ref disposed) != 0 || body.Length > MaxFrame - 5) return false;
        byte[] frame = new byte[9 + body.Length];
        BinaryPrimitives.WriteInt32BigEndian(frame, body.Length + 5); frame[4] = opcode;
        BinaryPrimitives.WriteUInt32BigEndian(frame.AsSpan(5), id); body.CopyTo(frame, 9);
        return pending.Writer.TryWrite(frame);
    }
    public async Task<bool> ProbeAsync(CancellationToken token = default)
    {
        if (Volatile.Read(ref disposed) != 0) return false;
        uint id; var nonce = RandomNumberGenerator.GetBytes(16);
        var promise = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        do { id = BinaryPrimitives.ReadUInt32BigEndian(RandomNumberGenerator.GetBytes(4)); } while (!probes.TryAdd(id, (nonce, promise)));
        try
        {
            if (!Queue((byte)'P', id, nonce)) return false;
            bool ok = await promise.Task.WaitAsync(TimeSpan.FromSeconds(3), token).ConfigureAwait(false);
            if (ok) Interlocked.Exchange(ref lastProbe, Environment.TickCount64);
            return ok;
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException or IOException) { return false; }
        finally { probes.TryRemove(id, out _); }
    }
    private void ReadLoop()
    {
        try
        {
            byte[] size = new byte[4];
            while (!stopped.IsCancellationRequested)
            {
                output.ReadExactly(size); int count = BinaryPrimitives.ReadInt32BigEndian(size);
                if (count < 5 || count > MaxFrame) throw new IOException("无效 UDP 中继帧。");
                byte[] frame = new byte[count]; output.ReadExactly(frame);
                uint id = BinaryPrimitives.ReadUInt32BigEndian(frame.AsSpan(1)); byte[] body = frame[5..];
                switch ((char)frame[0])
                {
                    case 'H':
                        if (Encoding.ASCII.GetString(body) != "Host2VMRelay-UDP/1") throw new IOException("不支持的 UDP 组件版本。");
                        ready.TrySetResult(true); break;
                    case 'R':
                        if (probes.TryGetValue(id, out var probe) && body.AsSpan().SequenceEqual(probe.Nonce)) probe.Result.TrySetResult(true);
                        break;
                    case 'D':
                        if (receivers.TryGetValue(id, out var receive)) receive(body);
                        break;
                    case 'E': LastError = Encoding.UTF8.GetString(body.AsSpan(0, Math.Min(body.Length, 256))); break;
                    default: throw new IOException("未知 UDP 中继消息。");
                }
            }
        }
        catch (Exception ex) { Fail(ex.Message); }
    }
    private async Task WriteLoop()
    {
        try
        {
            await foreach (byte[] frame in pending.Reader.ReadAllAsync(stopped.Token).ConfigureAwait(false))
            {
                // SSH.NET's channel stream may block on remote flow control; never block the UI.
                input.Write(frame); input.Flush();
            }
        }
        catch (Exception ex) { Fail(ex.Message); }
    }
    private void Fail(string message)
    {
        LastError = message; ready.TrySetException(new IOException(message)); Dispose();
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        stopped.Cancel(); pending.Writer.TryComplete();
        foreach (var probe in probes.Values) probe.Result.TrySetResult(false);
        receivers.Clear();
        try { input.Dispose(); } catch { }
        try { release(); } catch { }
        try { output.Dispose(); } catch { }
        // Stopped remains readable by outstanding UDP associations until their tasks unwind.
    }
}
