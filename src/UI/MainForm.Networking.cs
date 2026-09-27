namespace Host2VMRelay;

public sealed partial class MainForm
{
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
        state.ForeColor = Color.DarkOrange;
        string endpoint = settings.Host + ":" + settings.Port;
        var options = new RelayConnectionOptions(settings.Host, settings.Port, settings.User,
            settings.SocksPort, settings.UseKey, settings.KeyPath, secret.Text, settings.EnableUdp);
        try
        {
            Cleanup();
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
            }, Log, formLifetime.Token);
            if (!wanted || IsDisposed) { connected.Dispose(); return; }
            session = connected;
            ApplyRouteFiles();
            PresentPath();
            Log("中继已连接：127.0.0.1:" + settings.SocksPort + " → " + endpoint);
        }
        catch (Exception ex)
        {
            Cleanup();
            if (IsDisposed || formLifetime.IsCancellationRequested) return;
            TryDisableRules();
            PresentPath(ex.Message);
            Log("连接失败：" + ex.Message);
        }
        finally
        {
            busy = false;
            nextRetry = DateTime.UtcNow.AddSeconds(15);
            if (!IsDisposed) SetConnectionControls(session?.IsConnected == true);
        }
    }

    private async Task PollNetworkAsync()
    {
        if (busy || polling || !wanted) return;
        if (session?.IsConnected != true)
        {
            Cleanup();
            TryDisableRules();
            PresentPath("虚拟机不可用");
            SetConnectionControls(false);
            if (retry.Checked && DateTime.UtcNow >= nextRetry) await Connect(true);
            return;
        }
        polling = true;
        var active = session;
        try
        {
            bool alive = await active.RefreshAsync(retry.Checked);
            // Disconnect or form closure invalidates all in-flight state publications.
            if (!ReferenceEquals(session, active) || !wanted || IsDisposed) return;
            if (!alive)
            {
                Cleanup();
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
        string payload = tcp || udp ? Rules.Compile(settings.Rules) : ClashRuleFile.DisabledPayload;
        ClashRuleFile.Write(tcp ? payload : ClashRuleFile.DisabledPayload);
        ClashRuleFile.WriteUdp(udp ? payload : ClashRuleFile.DisabledPayload);
    }
    private void PresentPath(string? reason = null)
    {
        bool tcp = session?.Health.Tcp == true, udp = session?.Health.Udp == true && settings.EnableUdp;
        string key = tcp ? (udp ? "both" : settings.EnableUdp ? "tcp-only" : "tcp") : "host";
        state.Text = tcp ? udp ? "● TCP + UDP" : "● TCP 已连接" : "● 宿主机路径";
        state.ForeColor = key is "host" or "tcp-only" ? Color.DarkOrange : Color.SeaGreen;
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
        if (tray.Visible) tray.ShowBalloonTip(4000, "Host2VMRelay", message, warning ? ToolTipIcon.Warning : ToolTipIcon.Info);
        lastPath = key;
    }
    private void Cleanup()
    {
        session?.Dispose();
        session = null;
    }
    private void SetConnectionControls(bool connected)
    {
        connect.Enabled = !connected && !busy; disconnect.Enabled = !busy && (connected || wanted);
        foreach (var control in new Control[] { host, port, user, auth, keyPath, secret, socksPort, remember, enableUdp }) control.Enabled = !connected && !busy;
        if (keyControls is not null) keyControls.Enabled = !connected && !busy && auth.SelectedIndex == 1;
    }
    private void Stop()
    {
        if (busy) return;
        wanted = false; Cleanup(); TryDisableRules(); SetConnectionControls(false); lastPath = "host";
        state.Text = "● 已断开"; state.ForeColor = Color.DimGray; feed.Text = "宿主机原有分流 · 虚拟机中继已停用";
        Log("已主动断开，恢复宿主机原有分流；不会自动重连。");
    }
}
