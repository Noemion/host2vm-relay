using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Host2VMRelay;

/// <summary>Owns one Rust worker. Credentials and trust decisions travel only over inherited pipes.</summary>
internal sealed class RelayCoreProcess : IDisposable
{
    private readonly Process process;
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim writeGate = new(1);
    private readonly ConcurrentDictionary<long, TaskCompletionSource<JsonElement>> requests = new();
    private readonly TaskCompletionSource<int> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private long nextRequest;
    private int disposed;
    public int Port { get; private set; }
    public string Capability { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
    public Task DisposalCompleted => stopped.Task;
    public bool IsConnected
    {
        get
        {
            try { return Volatile.Read(ref disposed) == 0 && !process.HasExited && Port > 0; }
            catch (InvalidOperationException) { return false; }
        }
    }

    private RelayCoreProcess(Process process) => this.process = process;

    public static async Task<RelayCoreProcess> OpenAsync(RelayConnectionOptions options,
        Func<string, bool> trust, Action<string> log, CancellationToken token)
    {
        string directory = Path.Combine(AppContext.BaseDirectory, "native");
        string filename = OperatingSystem.IsWindows() ? "h2vm-core.exe" : "h2vm-core";
        await Task.Run(() => VerifyArtifacts(directory, filename), token).ConfigureAwait(false);
        token.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo(Path.Combine(directory, filename))
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8, WorkingDirectory = directory
        };
        var process = Process.Start(info) ?? throw new IOException("无法启动转发内核。");
        var core = new RelayCoreProcess(process);
        _ = Task.Run(() => core.ReadAsync(trust));
        _ = Task.Run(async () =>
        {
            try
            {
                while (await ReadLineAsync(process.StandardError, core.lifetime.Token).ConfigureAwait(false) is { } line)
                    log("转发内核：" + line);
            }
            catch (Exception) when (core.lifetime.IsCancellationRequested) { }
            catch { core.Dispose(); }
        });
        _ = Task.Run(async () =>
        {
            try { await process.WaitForExitAsync().ConfigureAwait(false); }
            finally { core.Dispose(); process.Dispose(); core.stopped.TrySetResult(); }
        });
        try
        {
            using var cancel = token.Register(core.Dispose);
            await core.SendAsync(new { version = 1, host = options.Host, port = options.Port, user = options.User,
                useKey = options.UseKey, keyPath = options.KeyPath, secret = options.Secret,
                token = core.Capability, maxConnections = options.MaxConnections, agentDirectory = directory }, token).ConfigureAwait(false);
            core.Port = await core.ready.Task.WaitAsync(TimeSpan.FromSeconds(155), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            return core;
        }
        catch { core.Dispose(); token.ThrowIfCancellationRequested(); throw; }
    }

    private static void VerifyArtifacts(string directory, string core)
    {
        string manifest = Path.Combine(directory, "SHA256SUMS");
        if (!File.Exists(manifest)) throw new IOException("转发内核缺失，请重新安装完整的软件包。");
        var hashes = File.ReadLines(manifest).Select(line => line.Split(' ', StringSplitOptions.RemoveEmptyEntries))
            .Where(parts => parts.Length == 2).ToDictionary(parts => parts[1], parts => parts[0], StringComparer.Ordinal);
        foreach (string name in new[] { core, "h2vm-agent-linux-x64", "h2vm-agent-linux-arm64" })
        {
            if (!hashes.TryGetValue(name, out string? expected)) throw new IOException("转发组件校验清单不完整。");
            using var stream = File.OpenRead(Path.Combine(directory, name));
            if (!Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase))
                throw new IOException("转发组件校验失败，请重新安装完整的软件包。");
        }
    }

    // ReadLineAsync without a size bound would allow a faulty worker to allocate
    // unbounded UI-process memory. The control protocol never carries payloads.
    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder(); var character = new char[1];
        while (await reader.ReadAsync(character.AsMemory(), token).ConfigureAwait(false) != 0)
        {
            if (character[0] == '\n') return result.ToString();
            if (result.Length >= 65536) throw new IOException("转发内核控制消息过长。");
            result.Append(character[0]);
        }
        if (result.Length != 0) throw new IOException("转发内核控制消息不完整。");
        return null;
    }
    private async Task ReadAsync(Func<string, bool> trust)
    {
        try
        {
            while (await ReadLineAsync(process.StandardOutput, lifetime.Token).ConfigureAwait(false) is { } line)
            {
                using var document = JsonDocument.Parse(line);
                var value = document.RootElement;
                if (value.TryGetProperty("id", out var id) && requests.TryRemove(id.GetInt64(), out var pending))
                    pending.TrySetResult(value.Clone());
                else if (value.TryGetProperty("event", out var kind))
                {
                    if (kind.GetString() == "hostKey")
                    {
                        bool accepted = !lifetime.IsCancellationRequested && trust(value.GetProperty("fingerprint").GetString()!);
                        await SendAsync(new { op = "trust", accept = accepted }, lifetime.Token).ConfigureAwait(false);
                    }
                    else if (kind.GetString() == "ready")
                    {
                        int port = value.GetProperty("port").GetInt32();
                        if (value.GetProperty("version").GetInt32() != 1 || port is < 1 or > 65535)
                            throw new IOException("转发内核协议不兼容。");
                        ready.TrySetResult(port);
                    }
                }
            }
        }
        catch (Exception ex) { ready.TrySetException(ex); }
        finally { Dispose(); }
    }
    private async Task SendAsync<T>(T message, CancellationToken token)
    {
        await writeGate.WaitAsync(token).ConfigureAwait(false);
        try
        {
            string json = JsonSerializer.Serialize(message);
            if (Encoding.UTF8.GetByteCount(json) > 65535) throw new IOException("转发内核请求过长。");
            await process.StandardInput.WriteLineAsync(json.AsMemory(), token).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(token).ConfigureAwait(false);
        }
        finally { writeGate.Release(); }
    }
    public async Task RequestAsync(string operation, TimeSpan timeout, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
        deadline.CancelAfter(timeout);
        long id = Interlocked.Increment(ref nextRequest);
        var response = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        requests[id] = response;
        try
        {
            await SendAsync(new { id, op = operation }, deadline.Token).ConfigureAwait(false);
            var result = await response.Task.WaitAsync(deadline.Token).ConfigureAwait(false);
            if (!result.GetProperty("ok").GetBoolean()) throw new IOException(result.GetProperty("error").GetString());
        }
        finally { requests.TryRemove(id, out _); }
    }
    internal static async Task AuthenticateAsync(Stream stream, string? capability, CancellationToken token)
    {
        await stream.WriteAsync(new byte[] { 5, 1, capability is null ? (byte)0 : (byte)2 }, token).ConfigureAwait(false);
        byte[] response = new byte[2]; await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
        if (response[0] != 5 || response[1] != (capability is null ? 0 : 2)) throw new IOException("转发内核握手失败。");
        if (capability is null) return; // Mock upstream used only by transport tests.
        byte[] password = Encoding.ASCII.GetBytes(capability);
        await stream.WriteAsync(new byte[] { 1, 4, (byte)'h', (byte)'2', (byte)'v', (byte)'m', (byte)password.Length }.Concat(password).ToArray(), token).ConfigureAwait(false);
        await stream.ReadExactlyAsync(response, token).ConfigureAwait(false);
        if (response[0] != 1 || response[1] != 0) throw new IOException("转发内核认证失败。");
    }
    public async Task<UdpTunnel> OpenUdpAsync(CancellationToken token)
    {
        await RequestAsync("udp", TimeSpan.FromSeconds(35), token).ConfigureAwait(false);
        var socket = new TcpClient { NoDelay = true };
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token, lifetime.Token);
            deadline.CancelAfter(TimeSpan.FromSeconds(12));
            await socket.ConnectAsync(IPAddress.Loopback, Port, deadline.Token).ConfigureAwait(false);
            var stream = socket.GetStream();
            await AuthenticateAsync(stream, Capability, deadline.Token).ConfigureAwait(false);
            await stream.WriteAsync(new byte[] { 5, 0xf0, 0, 1, 0, 0, 0, 0, 0, 0 }, deadline.Token).ConfigureAwait(false);
            byte[] response = new byte[10]; await stream.ReadExactlyAsync(response, deadline.Token).ConfigureAwait(false);
            if (response[0] != 5 || response[1] != 0) throw new IOException("无法启动虚拟机 UDP 组件。");
            return await UdpTunnel.OpenStreamsAsync(stream, stream, socket.Dispose, deadline.Token).ConfigureAwait(false);
        }
        catch { socket.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        lifetime.Cancel();
        var error = new IOException("转发内核已退出或连接已取消。");
        ready.TrySetException(error);
        foreach (var request in requests.Values) request.TrySetException(error);
        // Kill only our owned child, including blocked startup. Never enumerate
        // processes by name, which could interrupt another running application.
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
    }
}
