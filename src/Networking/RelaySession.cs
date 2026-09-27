using System.Security.Cryptography;
using Renci.SshNet;

namespace Host2VMRelay;

internal sealed record RelayConnectionOptions(string Host, int Port, string User, int SocksPort,
    bool UseKey, string KeyPath, string Secret, bool EnableUdp, int MaxConnections = RelaySocksServer.DefaultTransfers)
{
    // Do not let a diagnostic interpolation expose the password or private-key passphrase.
    public override string ToString() => $"{User}@{Host}:{Port}, SOCKS {SocksPort}, UDP {EnableUdp}";
}

internal sealed record RelayHealth(bool Tcp, bool Udp);

/// <summary>Owns one authenticated connection, its forwarding resources and UDP recovery.</summary>
/// <remarks>The UI owns reconnect intent. This session never reconnects after disposal.</remarks>
internal sealed class RelaySession : IDisposable
{
    private readonly SshClient client;
    private readonly ForwardedPortDynamic forward;
    private readonly PrivateKeyFile? key;
    private readonly RelaySocksServer relay;
    private readonly bool enableUdp;
    private readonly Action<string> log;
    private readonly object gate = new();
    private readonly CancellationTokenSource lifetime = new();
    private readonly SemaphoreSlim refresh = new(1);
    private UdpTunnel? udp;
    private Task? udpStart;
    private long nextUdpRetry;
    private bool disposed;
    private readonly TaskCompletionSource disposalCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal Task DisposalCompleted => disposalCompleted.Task;
    private int monitoring;
    private volatile bool retryUdpAutomatically;
    public void StartMonitoring(bool retryUdp)
    {
        retryUdpAutomatically = retryUdp;
        if (Interlocked.Exchange(ref monitoring, 1) == 0) _ = Task.Run(MonitorAsync);
    }
    public void SetAutomaticRecovery(bool enabled) => retryUdpAutomatically = enabled;
    private async Task MonitorAsync()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(RelayHealthTiming.PollInterval, lifetime.Token).ConfigureAwait(false);
                if (!await RefreshAsync(retryUdpAutomatically).ConfigureAwait(false)) return;
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            log("WARNING 后台隧道检查失败：" + ex.Message);
            Dispose();
        }
    }

    public RelayHealth Health => new(relay.TcpHealthy, relay.UdpHealthy);
    public (int Active, int Limit, long Rejected) Load => (relay.ActiveTransfers, relay.TransferLimit, relay.RejectedConnections);
    public bool IsConnected { get { lock (gate) return !disposed && client.IsConnected && forward.IsStarted; } }

    private RelaySession(SshClient client, ForwardedPortDynamic forward, PrivateKeyFile? key,
        RelaySocksServer relay, bool enableUdp, Action<string> log)
    {
        this.client = client;
        this.forward = forward;
        this.key = key;
        this.relay = relay;
        this.enableUdp = enableUdp;
        this.log = log;
    }

    public static async Task<RelaySession> OpenAsync(RelayConnectionOptions options,
        Func<string, bool> trustHost, Action<string> log, CancellationToken token = default, Action<string>? trafficLog = null)
    {
        // Startup resources remain local until completely initialized; a cancelled open
        // cannot publish a half-created session into a newer UI connection attempt.
        var session = await Task.Run(async () =>
        {
            PrivateKeyFile? key = null;
            SshClient? client = null;
            ForwardedPortDynamic? forward = null;
            RelaySocksServer? relay = null;
            try
            {
                token.ThrowIfCancellationRequested();
                AuthenticationMethod method;
                if (options.UseKey)
                {
                    key = string.IsNullOrEmpty(options.Secret) ? new PrivateKeyFile(options.KeyPath)
                        : new PrivateKeyFile(options.KeyPath, options.Secret);
                    method = new PrivateKeyAuthenticationMethod(options.User, key);
                }
                else method = new PasswordAuthenticationMethod(options.User, options.Secret);
                client = new SshClient(new ConnectionInfo(options.Host, options.Port, options.User, method)
                    { Timeout = TimeSpan.FromSeconds(12) });
                client.KeepAliveInterval = TimeSpan.FromSeconds(5);
                client.HostKeyReceived += (_, e) => e.CanTrust = !token.IsCancellationRequested &&
                    trustHost("SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('='));
                client.ErrorOccurred += (_, e) => log("SSH：" + e.Exception.Message);
                await client.ConnectAsync(token).ConfigureAwait(false);
                token.ThrowIfCancellationRequested();
                forward = new ForwardedPortDynamic("127.0.0.1", 0);
                forward.Exception += (_, e) => log("TCP：" + e.Exception.Message);
                client.AddForwardedPort(forward);
                forward.Start();
                relay = new RelaySocksServer(options.SocksPort, log: trafficLog ?? log, maxTransfers: options.MaxConnections);
                relay.SetUpstream((int)forward.BoundPort, null);
                token.ThrowIfCancellationRequested();
                return new RelaySession(client, forward, key, relay, options.EnableUdp, log);
            }
            catch
            {
                Release(log, relay, forward, client, key);
                // SSH.NET may report a socket-disposal race instead of cancellation.
                // Expose one cancellation contract to callers after owned resources close.
                token.ThrowIfCancellationRequested();
                throw;
            }
        }, token).ConfigureAwait(false);
        try
        {
            using var cancellation = token.Register(session.Dispose);
            if (options.EnableUdp) await session.StartUdpAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            lock (session.gate) session.Publish();
            return session;
        }
        catch { session.Dispose(); throw; }
    }

    // Called under gate. The shared lease includes both probe deadlines and
    // scheduling headroom. UDP startup never blocks polling.
    private void Publish()
    {
        if (!disposed) relay.SetUpstream((int)forward.BoundPort, udp);
    }

    private async Task StartUdpAsync()
    {
        try
        {
            var candidate = await UdpTunnel.StartAsync(client, lifetime.Token).ConfigureAwait(false);
            UdpTunnel? previous;
            lock (gate)
            {
                previous = disposed ? candidate : udp;
                if (!disposed)
                {
                    udp = candidate;
                    Publish();
                }
            }
            previous?.Dispose();
        }
        catch (Exception ex)
        {
            if (!lifetime.IsCancellationRequested)
                log("WARNING UDP 组件不可用，UDP 将使用宿主机原有分流：" + ex.Message);
        }
        finally { lock (gate) nextUdpRetry = Environment.TickCount64 + 15000; }
    }

    public async Task<bool> RefreshAsync(bool retryUdp)
    {
        await refresh.WaitAsync().ConfigureAwait(false);
        try
        {
            if (!IsConnected) return false;
            bool alive = await Task.Run(() =>
            {
                try
                {
                    using var probe = client.CreateCommand("printf h2vm-alive");
                    probe.CommandTimeout = RelayHealthTiming.ProbeTimeout;
                    return probe.Execute() == "h2vm-alive";
                }
                catch { return false; }
            }).ConfigureAwait(false);
            if (!alive || lifetime.IsCancellationRequested) { Dispose(); return false; }
            UdpTunnel? tested;
            lock (gate) { Publish(); tested = udp; }
            if (tested is not null && !await tested.ProbeAsync(lifetime.Token).ConfigureAwait(false))
            {
                lock (gate)
                {
                    if (ReferenceEquals(udp, tested))
                    {
                        udp = null;
                        nextUdpRetry = Environment.TickCount64 + 15000;
                    }
                }
                if (!lifetime.IsCancellationRequested) log("WARNING UDP 健康检查失败：" + tested.LastError);
                tested.Dispose();
            }
            lock (gate)
            {
                if (disposed) return false;
                if (enableUdp && retryUdp && udp is null && (udpStart is null || udpStart.IsCompleted)
                    && Environment.TickCount64 >= nextUdpRetry)
                    udpStart = Task.Run(StartUdpAsync);
                Publish();
                return true;
            }
        }
        finally { refresh.Release(); }
    }

    public void Dispose()
    {
        UdpTunnel? previous;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            relay.SetUpstream(0, null);
            previous = udp;
            udp = null;
        }
        // Cancellation/SSH disposal may wait for callbacks. Never hold gate while
        // doing either: a completing start/refresh needs it to discard stale results.
        try { lifetime.Cancel(); }
        finally
        {
            try { Release(log, relay, previous, forward, client, key); }
            finally { disposalCompleted.TrySetResult(); }
        }
        // Outstanding refresh/start tasks still observe the synchronization objects.
    }

    private static void Release(Action<string> log, params IDisposable?[] resources)
    {
        foreach (var resource in resources)
        {
            try { resource?.Dispose(); }
            catch (Exception ex) { log("清理中继资源失败：" + ex.Message); }
        }
    }
}
