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
        settings.RememberSecret = remember.Checked; settings.ProtectedSecret = remember.Checked ? SecretStore.Protect(secret.Text) : ""; settings.Save();
    }
    private async Task Connect(bool automatic = false)
    {
        if (busy) return;
        try { SaveConnection(); } catch (Exception ex) { if (!automatic) Error(ex); wanted = false; return; }
        busy = true; connect.Enabled = false; disconnect.Enabled = false;
        SetConnectionControls(true);
        state.Text = "● 正在连接…"; state.ForeColor = Color.DarkOrange;
        string password = secret.Text;
        string endpoint = settings.Host + ":" + settings.Port;
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
                client.KeepAliveInterval = TimeSpan.FromSeconds(20);
                client.HostKeyReceived += (_, e) =>
                {
                    string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                    bool accepted = false;
                    Invoke(() =>
                    {
                        if (settings.HostKeys.TryGetValue(endpoint, out var known))
                        {
                            accepted = known == fingerprint;
                            if (!accepted) { wanted = false; Log("服务器指纹变化，已拒绝连接：" + endpoint); }
                        }
                        else if (!automatic && MessageBox.Show(this, "首次连接 " + endpoint + "\n服务器指纹：\n" + fingerprint + "\n\n请与虚拟机核对后选择是。是否信任并保存？", "确认 SSH 服务器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                        {
                            settings.HostKeys[endpoint] = fingerprint; settings.Save(); accepted = true;
                        }
                    });
                    e.CanTrust = accepted;
                };
                client.ErrorOccurred += (_, e) => Log("SSH：" + e.Exception.Message);
                client.Connect();
                forward = new ForwardedPortDynamic("127.0.0.1", (uint)settings.SocksPort);
                forward.Exception += (_, e) => Log("转发：" + e.Exception.Message);
                client.AddForwardedPort(forward); forward.Start();
            });
            ClashRuleFile.Write(Rules.Compile(settings.Rules));
            wanted = true; state.Text = "● 已连接"; state.ForeColor = Color.SeaGreen;
            feed.Text = "Clash 本地规则 · 已启用";
            Log("隧道已连接：127.0.0.1:" + settings.SocksPort + " → " + endpoint);
        }
        catch (Exception ex)
        {
            Cleanup(); TryDisableRules(); state.Text = "● 连接失败"; state.ForeColor = Color.Firebrick; Log("连接失败：" + ex.Message);
            if (!automatic) MessageBox.Show(this, ex.Message + "\n\n请检查 SSH 服务和本机 SOCKS5 端口是否被占用。", "连接失败");
        }
        finally { busy = false; nextRetry = DateTime.UtcNow.AddSeconds(15); SetConnectionControls(client?.IsConnected == true); }
    }
    private void Cleanup()
    {
        try { forward?.Dispose(); } catch { } forward = null;
        try { client?.Dispose(); } catch { } client = null;
        keyFile?.Dispose(); keyFile = null;
    }
    private void SetConnectionControls(bool connected)
    {
        connect.Enabled = !connected && !busy; disconnect.Enabled = !busy && (connected || wanted);
        foreach (var control in new Control[] { host, port, user, auth, keyPath, secret, socksPort, remember }) control.Enabled = !connected && !busy;
        if (keyControls != null) keyControls.Enabled = !connected && !busy && auth.SelectedIndex == 1;
    }
    private void Stop()
    {
        if (busy) return;
        wanted = false; Cleanup(); TryDisableRules(); SetConnectionControls(false);
        state.Text = "● 已断开"; state.ForeColor = Color.DimGray; feed.Text = "Clash 本地规则 · 未启用";
        Log("已断开隧道，Clash 本地规则已停用。");
    }
}
