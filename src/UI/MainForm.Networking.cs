using System.Security.Cryptography;
using Renci.SshNet;

namespace Host2VMRelay;

public sealed partial class MainForm
{
    private void SaveConnection()
    {
        if (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(user.Text)) throw new ArgumentException("请填写虚拟机地址和用户名。");
        if (!System.Net.IPAddress.TryParse(host.Text.Trim(), out _)) throw new ArgumentException("虚拟机地址请填写 IPv4 或 IPv6 地址。");
        if (auth.SelectedIndex == 1 && !File.Exists(keyPath.Text)) throw new ArgumentException("请选择存在的私钥文件。");
        settings.Host = host.Text.Trim(); settings.Port = (int)port.Value; settings.User = user.Text.Trim(); settings.SocksPort = (int)socksPort.Value;
        settings.UseKey = auth.SelectedIndex == 1; settings.KeyPath = keyPath.Text; settings.Reconnect = retry.Checked;
        settings.EnableUdp = enableUdp.Checked; settings.RememberSecret = remember.Checked;
        settings.ProtectedSecret = remember.Checked ? SecretStore.Protect(secret.Text) : ""; settings.Save();
    }
    private async Task Connect(bool automatic = false)
    {
        if (busy || polling) return;
        try { SaveConnection(); } catch (Exception ex) { if (!automatic) Error(ex); wanted = false; return; }
        busy = true; wanted = true; SetConnectionControls(true);
        state.Text = "● 正在连接…"; state.ForeColor = Color.DarkOrange;
        string password = secret.Text, endpoint = settings.Host + ":" + settings.Port;
        try
        {
            Cleanup(); TryDisableRules();
            await Task.Run(() =>
            {
                AuthenticationMethod method;
                if (settings.UseKey)
                {
                    keyFile = string.IsNullOrEmpty(password) ? new PrivateKeyFile(settings.KeyPath) : new PrivateKeyFile(settings.KeyPath, password);
                    method = new PrivateKeyAuthenticationMethod(settings.User, keyFile);
                }
                else method = new PasswordAuthenticationMethod(settings.User, password);
                client = new SshClient(new ConnectionInfo(settings.Host, settings.Port, settings.User, method) { Timeout = TimeSpan.FromSeconds(12) });
                client.KeepAliveInterval = TimeSpan.FromSeconds(5);
                client.HostKeyReceived += (_, e) =>
                {
                    string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('='); bool accepted = false;
                    Invoke(() =>
                    {
                        if (settings.HostKeys.TryGetValue(endpoint, out var known))
                        {
                            accepted = known == fingerprint;
                            if (!accepted) { wanted = false; Log("服务器指纹变化，已拒绝连接：" + endpoint); }
                        }
                        else if (!automatic && MessageBox.Show(this, "首次连接 " + endpoint + "\n服务器指纹：\n" + fingerprint + "\n\n请与虚拟机核对。是否信任并保存？", "确认 SSH 服务器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        { settings.HostKeys[endpoint] = fingerprint; settings.Save(); accepted = true; }
                        else wanted = false;
                    });
                    e.CanTrust = accepted;
                };
                client.ErrorOccurred += (_, e) => Log("SSH：" + e.Exception.Message);
                client.Connect();
                forward = new ForwardedPortDynamic("127.0.0.1", 0);
                forward.Exception += (_, e) => Log("TCP：" + e.Exception.Message);
                client.AddForwardedPort(forward); forward.Start();
                relay = new RelaySocksServer(settings.SocksPort);
                relay.SetUpstream((int)forward.BoundPort, null);
            });
            await StartUdpAsync();
            relay!.SetUpstream((int)forward!.BoundPort, udpTunnel);
            ApplyRouteFiles(); PresentPath();
            Log("中继已连接：127.0.0.1:" + settings.SocksPort + " → " + endpoint);
        }
        catch (Exception ex)
        {
            Cleanup(); TryDisableRules(); PresentPath(ex.Message); Log("连接失败：" + ex.Message);
        }
        finally { busy = false; nextRetry = DateTime.UtcNow.AddSeconds(15); SetConnectionControls(client?.IsConnected == true); }
    }
    private bool udpStarting;
    private async Task StartUdpAsync()
    {
        var active = client;
        if (udpStarting || !settings.EnableUdp || active?.IsConnected != true) return;
        udpStarting = true;
        try
        {
            var tunnel = await UdpTunnel.StartAsync(active);
            if (!ReferenceEquals(client, active) || !wanted) { tunnel.Dispose(); return; }
            udpTunnel?.Dispose(); udpTunnel = tunnel;
            if (relay is not null && forward?.IsStarted == true)
                relay.SetUpstream((int)forward.BoundPort, tunnel);
        }
        catch (Exception ex)
        {
            if (ReferenceEquals(client, active)) Log("WARNING UDP 组件不可用，UDP 将使用宿主机原有分流：" + ex.Message);
        }
        finally { udpStarting = false; nextUdpRetry = DateTime.UtcNow.AddSeconds(15); }
    }
    private async Task PollNetworkAsync()
    {
        if (busy || polling || !wanted) return;
        if (client?.IsConnected != true || forward?.IsStarted != true || relay is null)
        {
            Cleanup(); TryDisableRules(); PresentPath("虚拟机不可用"); SetConnectionControls(false);
            if (retry.Checked && DateTime.UtcNow >= nextRetry) await Connect(true);
            return;
        }
        polling = true; var active = client;
        try
        {
            bool alive = await Task.Run(() =>
            {
                try { using var probe = active.CreateCommand("printf h2vm-alive"); probe.CommandTimeout = TimeSpan.FromSeconds(3); return probe.Execute() == "h2vm-alive"; }
                catch { return false; }
            });
            if (!ReferenceEquals(client, active) || !wanted) return;
            if (!alive)
            {
                Cleanup(); TryDisableRules(); PresentPath("SSH 健康检查失败"); SetConnectionControls(false); return;
            }
            relay?.SetUpstream((int)forward!.BoundPort, udpTunnel);
            var testedUdp = udpTunnel;
            if (testedUdp is not null && !await testedUdp.ProbeAsync() && ReferenceEquals(udpTunnel, testedUdp))
            { testedUdp.Dispose(); udpTunnel = null; nextUdpRetry = DateTime.UtcNow.AddSeconds(15); }
            if (!ReferenceEquals(client, active) || !wanted) return;
            if (settings.EnableUdp && udpTunnel is null && retry.Checked && DateTime.UtcNow >= nextUdpRetry) _ = StartUdpAsync();
            if (!ReferenceEquals(client, active) || relay is null || forward is null) return;
            relay.SetUpstream((int)forward.BoundPort, udpTunnel);
            ApplyRouteFiles(); PresentPath();
        }
        catch (Exception ex)
        {
            TryDisableRules();
            feed.Text = "规则同步失败，未确认切换；请查看运行日志。";
            Log("WARNING 状态同步失败：" + ex.Message);
        }
        finally { polling = false; }
    }
    private void ApplyRouteFiles()
    {
        bool tcp = relay?.TcpHealthy == true, udp = relay?.UdpHealthy == true && settings.EnableUdp;
        string payload = tcp || udp ? Rules.Compile(settings.Rules) : ClashRuleFile.DisabledPayload;
        ClashRuleFile.Write(tcp ? payload : ClashRuleFile.DisabledPayload);
        ClashRuleFile.WriteUdp(udp ? payload : ClashRuleFile.DisabledPayload);
    }
    private void PresentPath(string? reason = null)
    {
        bool tcp = relay?.TcpHealthy == true, udp = relay?.UdpHealthy == true && settings.EnableUdp;
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
        try { relay?.Dispose(); } catch { } relay = null;
        try { udpTunnel?.Dispose(); } catch { } udpTunnel = null;
        try { forward?.Dispose(); } catch { } forward = null;
        try { client?.Dispose(); } catch { } client = null;
        keyFile?.Dispose(); keyFile = null;
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
