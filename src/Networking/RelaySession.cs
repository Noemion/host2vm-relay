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
    private readonly RelayCoreProcess core;
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
    internal int SocksPort => relay.Port;
    public (int Active, int Limit, long Rejected) Load => (relay.ActiveTransfers, relay.TransferLimit, relay.RejectedConnections);
    public bool IsConnected { get { lock (gate) return !disposed && core.IsConnected; } }

    private RelaySession(RelayCoreProcess core, RelaySocksServer relay, bool enableUdp, Action<string> log)
    {
        this.core = core;
        this.relay = relay;
        this.enableUdp = enableUdp;
        this.log = log;
    }

    public static async Task<RelaySession> OpenAsync(RelayConnectionOptions options,
        Func<string, bool> trustHost, Action<string> log, CancellationToken token = default, Action<string>? trafficLog = null)
    {
        var core = await RelayCoreProcess.OpenAsync(options, trustHost, log, token).ConfigureAwait(false);
        RelaySession? session = null;
        try
        {
            var relay = new RelaySocksServer(options.SocksPort, log: trafficLog ?? log,
                maxTransfers: options.MaxConnections, upstreamCapability: core.Capability);
            session = new RelaySession(core, relay, options.EnableUdp, log);
            using var cancellation = token.Register(session.Dispose);
            lock (session.gate) session.Publish();
            if (options.EnableUdp) await session.StartUdpAsync().ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!core.IsConnected) throw new IOException("SSH 连接在初始化过程中中断。");
            lock (session.gate) session.Publish();
            return session;
        }
        catch { if (session is not null) session.Dispose(); else core.Dispose(); throw; }
    }

    // Called under gate. The shared lease includes both probe deadlines and
    // scheduling headroom. UDP startup never blocks polling.
    private void Publish()
    {
        if (!disposed) relay.SetUpstream(core.Port, udp);
    }

    private async Task StartUdpAsync()
    {
        try
        {
            var candidate = await core.OpenUdpAsync(lifetime.Token).ConfigureAwait(false);
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
            bool alive;
            try
            {
                await core.RequestAsync("probe", RelayHealthTiming.ProbeTimeout + TimeSpan.FromSeconds(1), lifetime.Token).ConfigureAwait(false);
                alive = true;
            }
            catch { alive = false; }
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
        // Cancellation/process disposal may wait for callbacks. Never hold gate while
        // doing either: a completing start/refresh needs it to discard stale results.
        try { lifetime.Cancel(); }
        finally
        {
            try { Release(log, relay, previous, core); }
            finally { _ = CompleteDisposalAsync(); }
        }
        // Outstanding refresh/start tasks still observe the synchronization objects.
    }

    private async Task CompleteDisposalAsync()
    {
        try { await core.DisposalCompleted.ConfigureAwait(false); }
        finally { disposalCompleted.TrySetResult(); }
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
