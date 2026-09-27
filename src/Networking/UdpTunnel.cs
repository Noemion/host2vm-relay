using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;

namespace Host2VMRelay;

/// <summary>Bounded datagram multiplexing over one authenticated SSH exec channel.</summary>
internal sealed class UdpTunnel : IDisposable
{
    internal const int MaxAssociations = 2048;
    // Shared across all associations; per-association packet limits alone could
    // otherwise retain gigabytes when thousands of local consumers stop reading.
    internal ByteBudget ReplyBudget { get; } = new(16 * 1024 * 1024);
    private const int MaxFrame = 66048;
    private readonly Stream input, output;
    private readonly Action release;
    private readonly CancellationTokenSource stopped = new();
    private readonly Channel<byte[]> pending = Channel.CreateBounded<byte[]>(new BoundedChannelOptions(128) { FullMode = BoundedChannelFullMode.Wait, SingleReader = true });
    private readonly ConcurrentDictionary<uint, Action<byte[]>> receivers = new();
    private readonly object receiverGate = new();
    private readonly ConcurrentDictionary<uint, (byte[] Nonce, TaskCompletionSource<bool> Result)> probes = new();
    private readonly TaskCompletionSource<bool> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long lastProbe;
    private int disposed, nextAssociation;
    public CancellationToken Stopped => stopped.Token;
    public bool Healthy => Volatile.Read(ref disposed) == 0 && Environment.TickCount64 - Interlocked.Read(ref lastProbe) < RelayHealthTiming.Lease.TotalMilliseconds;
    public string LastError { get; private set; } = "";

    private UdpTunnel(Stream input, Stream output, Action release)
    {
        this.input = input; this.output = output; this.release = release;
        lastProbe = Environment.TickCount64 - 60000;
        _ = Task.Run(ReadLoop);
        _ = Task.Run(WriteLoop);
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
        if (!await ProbeAsync(token).ConfigureAwait(false)) throw new IOException("虚拟机 UDP 自检失败；请检查辅助程序执行权限和 UDP 套接字。");
    }
    public uint Register(Action<byte[]> receive)
    {
        ArgumentNullException.ThrowIfNull(receive);
        lock (receiverGate)
        {
            if (!Healthy || receivers.Count >= MaxAssociations) throw new IOException("UDP 中继不可用或会话数达到上限。");
            uint id;
            do { id = unchecked((uint)++nextAssociation); } while (id == 0 || !receivers.TryAdd(id, receive));
            return id;
        }
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
            if (!Queue((byte)'P', id, nonce))
            {
                LastError = "UDP 探测无法入队：通道已关闭或队列已满。";
                return false;
            }
            bool ok = await promise.Task.WaitAsync(RelayHealthTiming.ProbeTimeout, token).ConfigureAwait(false);
            if (ok) Interlocked.Exchange(ref lastProbe, Environment.TickCount64);
            return ok;
        }
        catch (TimeoutException) { LastError = "UDP 健康探测超过三秒未收到响应。"; return false; }
        catch (OperationCanceledException) { return false; }
        catch (IOException ex) { LastError = ex.Message; return false; }
        finally { probes.TryRemove(id, out _); }
    }
    private async Task ReadLoop()
    {
        try
        {
            byte[] size = new byte[4];
            while (!stopped.IsCancellationRequested)
            {
                await output.ReadExactlyAsync(size, stopped.Token).ConfigureAwait(false); int count = BinaryPrimitives.ReadInt32BigEndian(size);
                if (count < 5 || count > MaxFrame) throw new IOException("无效 UDP 中继帧。");
                byte[] frame = new byte[count]; await output.ReadExactlyAsync(frame, stopped.Token).ConfigureAwait(false);
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
                // The worker pipe is asynchronous; remote flow control never parks a UI thread.
                await input.WriteAsync(frame, stopped.Token).ConfigureAwait(false);
                await input.FlushAsync(stopped.Token).ConfigureAwait(false);
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
        lock (receiverGate) receivers.Clear();
        try { input.Dispose(); } catch { }
        try { release(); } catch { }
        try { output.Dispose(); } catch { }
        // Stopped remains readable by outstanding UDP associations until their tasks unwind.
    }
}
