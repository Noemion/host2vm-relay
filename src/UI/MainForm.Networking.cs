namespace Host2VMRelay;

public sealed partial class MainForm
{
    private string? compiledRulesSource;
    private string compiledRulesPayload = ClashRuleFile.DisabledPayload;
    private void SaveConnection()
    {
        if (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(user.Text))
            throw new ArgumentException("请填写虚拟机地址和用户名。");
        if (!System.Net.IPAddress.TryParse(host.Text.Trim(), out _))
            throw new ArgumentException("虚拟机地址请填写 IPv4 或 IPv6 地址。");
        if (auth.SelectedIndex == 1 && !File.Exists(keyPath.Text))
            throw new ArgumentException("请选择存在的私钥文件。");
        settings = settings.SaveUpdated(candidate =>
        {
            candidate.Host = host.Text.Trim();
            candidate.Port = (int)port.Value;
            candidate.User = user.Text.Trim();
            candidate.SocksPort = (int)socksPort.Value;
            candidate.UseKey = auth.SelectedIndex == 1;
            candidate.KeyPath = keyPath.Text;
            candidate.Reconnect = retry.Checked;
            candidate.EnableUdp = enableUdp.Checked;
            candidate.RememberSecret = remember.Checked;
            candidate.ProtectedSecret = remember.Checked ? SecretStore.Protect(secret.Text) : "";
        });
    }

    private async Task Connect(bool automatic = false)
    {
        if (busy || polling) return;
        try { SaveConnection(); }
        catch (Exception ex) { if (!automatic) Error(ex); wanted = false; return; }
        busy = true;
        wanted = true;
        SetConnectionControls(true);
        state.Text = "● 正在连接…";
        SetConnectionIcon(ConnectionIconState.Connecting, "正在连接虚拟机");
        state.ForeColor = Color.FromArgb(145, 83, 0);
        string endpoint = settings.Host + ":" + settings.Port;
        var options = new RelayConnectionOptions(settings.Host, settings.Port, settings.User,
            settings.SocksPort, settings.UseKey, settings.KeyPath, secret.Text, settings.EnableUdp);
        try
        {
            await Cleanup();
            if (IsDisposed || formLifetime.IsCancellationRequested) return;
            TryDisableRules();
            var connected = await RelaySession.OpenAsync(options, fingerprint =>
            {
                var decision = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                BeginInvoke(() =>
                {
                    try
                    {
                        if (formLifetime.IsCancellationRequested) { decision.TrySetCanceled(); return; }
                        bool accepted = false;
                        if (settings.HostKeys.TryGetValue(endpoint, out var known))
                        {
                            accepted = known == fingerprint;
                            if (!accepted) { wanted = false; Log("服务器指纹变化，已拒绝连接：" + endpoint); }
                        }
                        else if (!automatic && MessageBox.Show(this, "首次连接 " + endpoint + "\n服务器指纹：\n" + fingerprint +
                            "\n\n请与虚拟机核对。是否信任并保存？", "确认 SSH 服务器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            settings = settings.SaveUpdated(candidate => candidate.HostKeys[endpoint] = fingerprint);
                            accepted = true;
                        }
                        else wanted = false;
                        decision.TrySetResult(accepted);
                    }
                    catch (Exception ex) { decision.TrySetException(ex); }
                });
                // Cancellation must release the SSH callback even when the UI is closing.
                return decision.Task.WaitAsync(formLifetime.Token).GetAwaiter().GetResult();
            }, Log, formLifetime.Token, message => { if (Volatile.Read(ref settings).LogForwardingRequests) Log(message); });
            if (!wanted || IsDisposed) { await connectionCleanup.Enqueue(connected, connected.DisposalCompleted); return; }
            session = connected;
            connected.StartMonitoring(retry.Checked);
            ApplyRouteFiles();
            PresentPath();
            Log("中继已连接：127.0.0.1:" + settings.SocksPort + " → " + endpoint);
        }
        catch (Exception ex)
        {
            await Cleanup();
            if (IsDisposed || formLifetime.IsCancellationRequested) return;
            TryDisableRules();
            PresentPath(ex.Message);
            Log("连接失败：" + ex.Message);
        }
        finally
        {
            busy = false;
            nextRetry = DateTime.UtcNow.AddSeconds(settings.ReconnectDelaySeconds);
            if (!IsDisposed) SetConnectionControls(session?.IsConnected == true);
        }
    }

    private async Task PollNetworkAsync()
    {
        if (busy || polling || !wanted) return;
        if (session?.IsConnected != true)
        {
            polling = true;
            try
            {
                TryDisableRules();
                PresentPath("虚拟机不可用");
                await Cleanup();
                if (IsDisposed || !wanted) return;
                SetConnectionControls(false);
            }
            finally { polling = false; }
            if (wanted && retry.Checked && DateTime.UtcNow >= nextRetry) await Connect(true);
            return;
        }
        polling = true;
        var active = session;
        try
        {
            // Transport health is maintained by the session's background loop.
            // UI paint/log delays must never expire a live forwarding lease.
            active.SetAutomaticRecovery(retry.Checked);
            bool alive = active.IsConnected;
            // Disconnect or form closure invalidates all in-flight state publications.
            if (!ReferenceEquals(session, active) || !wanted || IsDisposed) return;
            if (!alive)
            {
                await Cleanup();
                if (IsDisposed || !wanted) return;
                TryDisableRules();
                PresentPath("SSH 健康检查失败");
                SetConnectionControls(false);
                return;
            }
            ApplyRouteFiles();
            PresentPath();
        }
        catch (Exception ex)
        {
            if (!ReferenceEquals(session, active) || IsDisposed) return;
            TryDisableRules();
            feed.Text = "规则同步失败，未确认切换；请查看运行日志。";
            Log("WARNING 状态同步失败：" + ex.Message);
        }
        finally { polling = false; }
    }
    private void ApplyRouteFiles()
    {
        bool tcp = session?.Health.Tcp == true, udp = session?.Health.Udp == true && settings.EnableUdp;
        // Profile objects also change for unrelated preferences. Cache by source
        // text so routine health ticks do not parse every domain again on the UI.
        if ((tcp || udp) && compiledRulesSource != settings.Rules)
        {
            compiledRulesPayload = Rules.Compile(settings.Rules);
            compiledRulesSource = settings.Rules;
        }
        string payload = tcp || udp ? compiledRulesPayload : ClashRuleFile.DisabledPayload;
        bool tcpChanged = ClashRuleFile.Write(tcp ? payload : ClashRuleFile.DisabledPayload);
        bool udpChanged = ClashRuleFile.WriteUdp(udp ? payload : ClashRuleFile.DisabledPayload);
        if (tcpChanged || udpChanged) ClashRuleRefresh.Request(Log);
    }
    private void PresentPath(string? reason = null)
    {
        var load = session?.Load;
        connectionLoad.Text = load is { } usage
            ? $"当前转发 {usage.Active} / {usage.Limit} · 本次连接累计过载拒绝 {usage.Rejected} 次"
            : "当前无转发连接。";
        bool tcp = session?.Health.Tcp == true, udp = session?.Health.Udp == true && settings.EnableUdp;
        string key = tcp ? (udp ? "both" : settings.EnableUdp ? "tcp-only" : "tcp") : "host";
        SetConnectionIcon(!tcp ? ConnectionIconState.Disconnected : settings.EnableUdp && !udp ? ConnectionIconState.Degraded : ConnectionIconState.Connected,
            !tcp ? "虚拟机未连接" : udp ? "虚拟机已连接 · TCP / UDP" : settings.EnableUdp ? "虚拟机已连接 · UDP 未就绪" : "虚拟机已连接 · TCP");
        state.Text = tcp ? udp ? "● 已连接 · TCP / UDP" : settings.EnableUdp ? "● 仅 TCP · UDP 未就绪" : "● 已连接 · TCP" : "● 已回退 · 宿主机";
        state.ForeColor = key is "host" or "tcp-only" ? Color.FromArgb(145, 83, 0) : Color.FromArgb(20, 105, 70);
        feed.Text = tcp ? udp ? "TCP / UDP 优先虚拟机 · 不可用时回退原有分流" : "TCP 经虚拟机 · UDP 使用宿主机原有分流" : "已请求回退宿主机原有分流 · 新连接自动生效";
        if (lastPath == key) return;
        string message = key switch
        {
            "host" => "虚拟机中继不可用，正在回退宿主机原有分流。" + (retry.Checked && wanted ? " 将自动重试。" : ""),
            "tcp-only" => "UDP 中继不可用，UDP 回退宿主机原有分流；TCP 继续经过虚拟机。",
            "both" => "TCP / UDP 中继已恢复，匹配流量优先经过虚拟机。",
            _ => "TCP 中继已连接，UDP 继续使用宿主机原有分流。"
        };
        bool warning = key is "host" or "tcp-only";
        Log((warning ? "WARNING " : "INFO ") + message + (reason is null ? "" : " 原因：" + reason));
        if (tray.Visible && settings.ShowConnectionNotifications) tray.ShowBalloonTip(4000, "Host2VMRelay", message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        lastPath = key;
    }
    private Task Cleanup()
    {
        // Detach before yielding: in-flight probes can no longer update this session's UI.
        var previous = session;
        session = null;
        return connectionCleanup.Enqueue(previous, previous?.DisposalCompleted);
    }
    private void SetConnectionControls(bool connected)
    {
        connect.Enabled = !connected && !busy; disconnect.Enabled = !busy && (connected || wanted);
        foreach (var control in new Control[] { host, port, user, auth, keyPath, secret, socksPort, remember, enableUdp }) control.Enabled = !connected && !busy;
        if (keyControls is not null) keyControls.Enabled = !connected && !busy && auth.SelectedIndex == 1;
    }
    private async Task Stop()
    {
        if (busy) return;
        wanted = false; busy = true; SetConnectionControls(false);
        state.Text = "● 正在断开…"; feed.Text = "正在后台释放连接，请稍候。";
        SetConnectionIcon(ConnectionIconState.Connecting, "正在断开虚拟机");
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        Log("正在断开中继，后台清理连接资源。");
        try
        {
            Task cleanup = Cleanup();
            TryDisableRules();
            await cleanup;
            if (IsDisposed) return;
            lastPath = "host";
            state.Text = "● 已断开"; state.ForeColor = Color.DimGray; feed.Text = "宿主机原有分流 · 虚拟机中继已停用";
            connectionLoad.Text = "当前无转发连接。";
            SetConnectionIcon(ConnectionIconState.Disconnected, "虚拟机已断开");
            Log($"已主动断开，清理耗时 {elapsed.Elapsed.TotalSeconds:F1} 秒；不会自动重连。");
        }
        catch (Exception ex) { if (!IsDisposed) Error(ex); }
        finally
        {
            busy = false;
            if (!IsDisposed)
            {
                SetConnectionControls(false);
                SetConnectionIcon(ConnectionIconState.Disconnected, "虚拟机已断开");
            }
        }
    }
}
