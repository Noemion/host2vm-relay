using System.Security.Cryptography;
using Renci.SshNet;

namespace Host2VMRelay;

public sealed class MainForm : Form
{
    private readonly Settings settings;
    private readonly RuleServer server = new();
    private SshClient? client;
    private ForwardedPortDynamic? forward;
    private PrivateKeyFile? keyFile;
    private readonly TextBox host = new(), user = new(), secret = new(), keyPath = new(), ruleText = new(), log = new();
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535 }, socksPort = new() { Minimum = 1024, Maximum = 65535 };
    private readonly ComboBox auth = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private Control? keyControls;
    private readonly CheckBox remember = new() { Text = "加密保存密码 / 私钥口令（仅当前 Windows 用户可解密）", AutoSize = true }, retry = new() { Text = "连接断开后自动重连", AutoSize = true };
    private readonly Button connect = new() { Text = "连接虚拟机", Width = 160, Height = 40 }, disconnect = new() { Text = "断开", Width = 90, Height = 40, Enabled = false };
    private readonly Label state = new() { AutoSize = true, Text = "● 未连接", ForeColor = Color.FromArgb(88, 104, 127) }, feed = new() { AutoSize = true, Text = "规则服务 127.0.0.1:17861 · 等待 Clash 读取" };
    private readonly NotifyIcon tray = new() { Icon = SystemIcons.Application, Text = "Host2VM Relay", Visible = true };
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };
    private bool busy, wanted, quitting;
    private DateTime nextRetry;
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };

    public MainForm()
    {
        settings = Settings.Load();
        Text = "Host2VM Relay";
        Font = new Font("Microsoft YaHei UI", 14, FontStyle.Regular, GraphicsUnit.Pixel);
        AutoScaleMode = AutoScaleMode.None;
        ClientSize = new Size(900, 730); MinimumSize = new Size(820, 700);
        BackColor = Color.FromArgb(245, 247, 251); StartPosition = FormStartPosition.CenterScreen;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(22), RowCount = 3, ColumnCount = 1 };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 82)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        var heading = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false };
        heading.Controls.Add(new Label { Text = "Host2VM Relay", Font = new Font(Font.FontFamily, 28, FontStyle.Bold, GraphicsUnit.Pixel), AutoSize = true });
        heading.Controls.Add(new Label { Text = "虚拟机 SSH 隧道  /  Clash 分流  /  托盘后台运行", AutoSize = true, ForeColor = Color.DimGray });
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(tabs, 0, 1); layout.Controls.Add(feed, 0, 2); Controls.Add(layout);
        BuildConnection(); BuildRules(); BuildClash(); BuildLog();
        host.Text = settings.Host; port.Value = settings.Port; user.Text = settings.User; socksPort.Value = settings.SocksPort;
        keyPath.Text = settings.KeyPath; auth.SelectedIndex = settings.UseKey ? 1 : 0;
        remember.Checked = settings.RememberSecret; retry.Checked = settings.Reconnect; ruleText.Text = settings.Rules.Replace("\n", Environment.NewLine);
        try { secret.Text = SecretStore.Unprotect(settings.ProtectedSecret); }
        catch { Log("保存的密码无法在当前 Windows 用户下解密，请重新输入。"); }
        server.Payload = Rules.Compile(settings.Rules);
        try { server.Start(); }
        catch { tray.Dispose(); throw new IOException("本机 17861 端口已被占用，规则服务无法启动。请关闭其他实例或占用该端口的程序。"); }
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => Restore());
        menu.Items.Add("连接", null, async (_, _) => { if (!busy && client?.IsConnected != true) await Connect(); });
        menu.Items.Add("断开", null, (_, _) => Stop());
        menu.Items.Add("退出", null, (_, _) => { if (busy) { Restore(); MessageBox.Show("正在连接，请等待本次连接完成后退出。"); return; } quitting = true; Close(); });
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Restore();
        connect.Click += async (_, _) => await Connect(); disconnect.Click += (_, _) => Stop();
        FormClosing += (_, e) => { if (!quitting) { e.Cancel = true; Hide(); tray.ShowBalloonTip(2000, "Host2VM Relay", "已缩到托盘，后台连接继续运行。右键托盘图标可退出。", ToolTipIcon.Info); } };
        FormClosed += (_, _) => { wanted = false; timer.Stop(); Cleanup(); server.Dispose(); tray.Dispose(); };
        timer.Tick += async (_, _) => {
            feed.Text = "规则服务 127.0.0.1:17861 · " + (server.LastReadUtc == default ? "等待 Clash 读取（首次需导入扩展脚本）" : "最近读取 " + server.LastReadUtc.ToLocalTime().ToString("HH:mm:ss"));
            if (wanted && !busy && client?.IsConnected != true) {
                SetConnectionControls(false);
                if (DateTime.UtcNow >= nextRetry && retry.Checked) await Connect(true);
                else { state.Text = retry.Checked ? "● 连接断开，等待重连" : "● 连接已断开"; state.ForeColor = Color.DarkOrange; }
            }
        };
        timer.Start(); Log("规则服务已启动。先连接虚拟机，再到「接入 Clash」完成一次性配置。");
    }
    private TabPage Page(string title) { var p = new TabPage(title) { BackColor = Color.White, Padding = new Padding(18), AutoScroll = true }; tabs.TabPages.Add(p); return p; }
    private TableLayoutPanel Grid(TabPage page) { var g = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 }; g.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); g.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); page.Controls.Add(g); return g; }
    private static void Row(TableLayoutPanel g, string title, Control control) { int i = g.RowCount++; g.RowStyles.Add(new RowStyle(SizeType.AutoSize)); var label = new Label { Text = title, AutoSize = true, Margin = new Padding(0, 10, 6, 8) }; control.Margin = new Padding(0, 5, 0, 9); control.Dock = DockStyle.Top; g.Controls.Add(label, 0, i); g.Controls.Add(control, 1, i); }
    private void BuildConnection()
    {
        var g = Grid(Page("连接"));
        Row(g, "虚拟机地址", host); Row(g, "SSH 端口", port); Row(g, "登录用户名", user);
        auth.Items.AddRange(new object[] { "密码登录", "私钥免密登录" }); Row(g, "认证方式", auth);
        var keys = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 }; keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); keys.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 82)); keyPath.Dock = DockStyle.Fill;
        var browse = new Button { Text = "选择…", Dock = DockStyle.Fill }; browse.Click += (_, _) => { using var d = new OpenFileDialog { Title = "选择私钥文件（不是 .pub 公钥）", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh") }; if (d.ShowDialog() == DialogResult.OK) keyPath.Text = d.FileName; };
        keys.Controls.Add(keyPath, 0, 0); keys.Controls.Add(browse, 1, 0); Row(g, "私钥文件", keys);
        keyControls = keys;
        auth.SelectedIndexChanged += (_, _) => keys.Enabled = auth.SelectedIndex == 1 && !busy && client?.IsConnected != true;
        secret.UseSystemPasswordChar = true; Row(g, "密码 / 私钥口令", secret); Row(g, "", remember);
        Row(g, "本机 SOCKS5 端口", socksPort); Row(g, "", retry);
        var buttons = new FlowLayoutPanel { AutoSize = true }; buttons.Controls.Add(connect); buttons.Controls.Add(disconnect); state.Margin = new Padding(15, 12, 0, 0); buttons.Controls.Add(state); Row(g, "", buttons);
        Row(g, "使用提示", new Label { AutoSize = true, MaximumSize = new Size(620, 0), Text = "先在远端主机连接 VPN（如果需要）。首次 SSH 连接需要确认服务器指纹。\n关闭窗口会缩到托盘；退出应用会停止隧道。私钥登录直接读取所选文件，不复制私钥。" });
    }
    private void BuildRules()
    {
        var p = Page("域名 / IP"); var g = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3 };
        g.RowStyles.Add(new RowStyle(SizeType.Absolute, 85)); g.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); g.RowStyles.Add(new RowStyle(SizeType.Absolute, 55));
        g.Controls.Add(new Label { Dock = DockStyle.Fill, Text = "每行一个地址，命中的 TCP 连接经远端主机转发。\n精确域名：code.example.com    域名及子域名：*.example.com\n单个 IP：10.20.30.40    网段：10.20.30.0/24    # 开头为注释" }, 0, 0);
        ruleText.Multiline = true; ruleText.AcceptsReturn = true; ruleText.ScrollBars = ScrollBars.Both; ruleText.WordWrap = false; ruleText.Dock = DockStyle.Fill; ruleText.Font = new Font("Consolas", 16, FontStyle.Regular, GraphicsUnit.Pixel); g.Controls.Add(ruleText, 0, 1);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Fill }; var save = new Button { Text = "保存规则", Width = 120, Height = 38 }; save.Click += (_, _) => { try { var payload = Rules.Compile(ruleText.Text); settings.Rules = ruleText.Text.Replace("\r", ""); settings.Save(); Volatile.Write(ref server.Payload, payload); Log("规则已保存；Clash 通常在下一个 15 秒更新周期读取。"); MessageBox.Show("已保存。Clash 通常在 15 秒内读取；DNS 旧缓存可能需刷新。", "规则已更新"); } catch (Exception ex) { Error(ex); } }; bar.Controls.Add(save); bar.Controls.Add(new Label { Text = "规则变更无需重复粘贴脚本；节点端口变更需要重新导入。", AutoSize = true, Margin = new Padding(12) }); g.Controls.Add(bar, 0, 2); p.Controls.Add(g);
    }
    private void BuildClash()
    {
        var p = Page("接入 Clash"); var g = Grid(p);
        Row(g, "① 复制脚本", new Label { AutoSize = true, MaximumSize = new Size(620, 0), Text = "在 Clash Verge Rev 当前订阅的「编辑扩展脚本」中替换为本应用生成的脚本，保存并应用订阅。脚本保留你已有的 mieru UDP 设置，并接入本地动态规则。其他自定义逻辑需手动合并。" });
        var copy = new Button { Text = "复制 Clash 扩展脚本", Height = 40 }; copy.Click += (_, _) => { try { Clipboard.SetText(ClashScript.Generate((int)socksPort.Value, host.Text.Trim())); } catch (Exception ex) { Error(ex); return; } Log("扩展脚本已复制到剪贴板。"); MessageBox.Show("已复制。请粘贴到当前订阅的扩展脚本并应用。", "接入 Clash"); }; Row(g, "", copy);
        Row(g, "② TUN 设置", new Label { AutoSize = true, MaximumSize = new Size(620, 0), Text = "选择规则模式，开启 TUN 和自动路由。\n路由排除添加虚拟机 IP/32（IPv6 用 /128）\nDNS 劫持包含：any:53 与 tcp://any:53\n脚本启用 Fake-IP，为公司域名优先分配映射地址。某些版本会由界面覆盖 TUN 设置，请在界面确认。需要支持 fake-ip-filter-mode: rule 的较新 Mihomo 内核。" });
        Row(g, "③ 验证", new Label { AutoSize = true, MaximumSize = new Size(620, 0), Text = "连接隧道后，使用 Chrome 打开公司网页。在 Clash 连接列表确认命中「Host2VM Relay」。底部显示规则读取时间只代表订阅下载，不代表网站已连通。\n若 Chrome 自定义了安全 DNS，请先改用系统 DNS。规则中也需包含登录跳转域名。" });
        var testUrl = new TextBox { Text = settings.TestUrl };
        Row(g, "测试网址", testUrl);
        var test = new Button { Text = "通过 SOCKS5 测试网址", Height = 40 };
        test.Click += async (_, _) => {
            test.Enabled = false;
            try {
                if (!Uri.TryCreate(testUrl.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new ArgumentException("请填写 HTTP 或 HTTPS 网址，不要在网址中包含用户名和密码。");
                settings.TestUrl = uri.AbsoluteUri; settings.Save();
                using var handler = new SocketsHttpHandler { Proxy = new System.Net.WebProxy("socks5://127.0.0.1:" + socksPort.Value), UseProxy = true, AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                Log("SOCKS5 测试：HTTP " + (int)response.StatusCode);
                MessageBox.Show("收到 HTTP " + (int)response.StatusCode + "。这验证 SOCKS5 隧道，不代表 TUN 已接管 DNS。", "测试结果");
            } catch (Exception ex) { Error(ex); } finally { if (!test.IsDisposed) test.Enabled = true; }
        };
        Row(g, "", test);
        Row(g, "边界", new Label { AutoSize = true, MaximumSize = new Size(620, 0), Text = "仅支持 TCP（浏览器 HTTPS / SSH 等），不转发 UDP。VPN 仍由你在远端主机连接。请先停止旧 PowerShell 隧道，释放本机 1080 端口。程序不修改 Windows 路由或防火墙。" });
    }
    private void BuildLog() { var p = Page("运行日志"); log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical; log.Dock = DockStyle.Fill; log.Font = new Font("Consolas", 14, FontStyle.Regular, GraphicsUnit.Pixel); p.Controls.Add(log); }
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    public void ExitForTest() { quitting = true; Close(); }
    public void CaptureTabs(string path) {
        // Regression: an offline session must allow manual reconnect even with retry disabled.
        wanted = true; SetConnectionControls(true); SetConnectionControls(false);
        if (!connect.Enabled || !host.Enabled) throw new InvalidOperationException("Disconnected controls did not recover.");
        if (auth.SelectedIndex == 0 && keyControls?.Enabled == true) throw new InvalidOperationException("Password mode enabled private-key browsing.");
        wanted = false; SetConnectionControls(false);
        for (int i = 0; i < tabs.TabCount; i++) {
            tabs.SelectedIndex = i; tabs.SelectedTab!.PerformLayout(); Refresh(); Application.DoEvents();
            using var bitmap = new Bitmap(Width, Height); DrawToBitmap(bitmap, new Rectangle(Point.Empty, Size));
            bitmap.Save(i == 0 ? path : Path.Combine(Path.GetDirectoryName(path)!, Path.GetFileNameWithoutExtension(path) + "-" + i + ".png"));
        }
        File.WriteAllText(Path.ChangeExtension(path, ".txt"), "Authentication selected index: " + auth.SelectedIndex + "; text: " + auth.Text);
    }
    private void Log(string text) { if (IsDisposed || Disposing || !IsHandleCreated) return; if (InvokeRequired) { try { BeginInvoke(() => Log(text)); } catch (InvalidOperationException) { } return; } log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine); if (log.TextLength > 60000) log.Text = log.Text[^40000..]; }
    private void Error(Exception ex) { Log(ex.Message); MessageBox.Show(ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    private void SaveConnection()
    {
        if (string.IsNullOrWhiteSpace(host.Text) || string.IsNullOrWhiteSpace(user.Text)) throw new ArgumentException("请填写虚拟机地址和用户名。");
        if (!System.Net.IPAddress.TryParse(host.Text.Trim(), out _)) throw new ArgumentException("虚拟机地址请填写 IPv4 或 IPv6 地址。");
        if ((int)socksPort.Value == 17861) throw new ArgumentException("17861 用于规则服务，请选择其他 SOCKS5 端口。");
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
            Cleanup();
            await Task.Run(() => {
                AuthenticationMethod method;
                if (settings.UseKey) { keyFile = string.IsNullOrEmpty(password) ? new PrivateKeyFile(settings.KeyPath) : new PrivateKeyFile(settings.KeyPath, password); method = new PrivateKeyAuthenticationMethod(settings.User, keyFile); }
                else method = new PasswordAuthenticationMethod(settings.User, password);
                client = new SshClient(new ConnectionInfo(settings.Host, settings.Port, settings.User, method) { Timeout = TimeSpan.FromSeconds(12) });
                client.KeepAliveInterval = TimeSpan.FromSeconds(20);
                client.HostKeyReceived += (_, e) => {
                    string fingerprint = "SHA256:" + Convert.ToBase64String(SHA256.HashData(e.HostKey)).TrimEnd('=');
                    bool accepted = false;
                    Invoke(() => {
                        if (settings.HostKeys.TryGetValue(endpoint, out var known)) {
                            accepted = known == fingerprint;
                            if (!accepted) { wanted = false; Log("服务器指纹变化，已拒绝连接：" + endpoint); }
                        } else if (!automatic && MessageBox.Show(this, "首次连接 " + endpoint + "\n服务器指纹：\n" + fingerprint + "\n\n请与虚拟机核对后选择是。是否信任并保存？", "确认 SSH 服务器", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) {
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
            wanted = true; state.Text = "● 已连接"; state.ForeColor = Color.SeaGreen;
            Log("隧道已连接：127.0.0.1:" + settings.SocksPort + " → " + endpoint);
        }
        catch (Exception ex) { Cleanup(); state.Text = "● 连接失败"; state.ForeColor = Color.Firebrick; Log("连接失败：" + ex.Message); if (!automatic) MessageBox.Show(ex.Message + "\n\n若 1080 端口被占用，请先关闭原来的 PowerShell 隧道。", "连接失败"); }
        finally {
            busy = false; nextRetry = DateTime.UtcNow.AddSeconds(15); SetConnectionControls(client?.IsConnected == true);
        }
    }
    private void Cleanup() { try { forward?.Dispose(); } catch { } forward = null; try { client?.Dispose(); } catch { } client = null; keyFile?.Dispose(); keyFile = null; }
    private void SetConnectionControls(bool connected) {
        connect.Enabled = !connected && !busy; disconnect.Enabled = !busy && (connected || wanted);
        foreach (var c in new Control[] { host, port, user, auth, keyPath, secret, socksPort, remember }) c.Enabled = !connected && !busy;
        if (keyControls != null) keyControls.Enabled = !connected && !busy && auth.SelectedIndex == 1;
    }
    private void Stop() { if (busy) return; wanted = false; Cleanup(); SetConnectionControls(false); state.Text = "● 已断开"; state.ForeColor = Color.DimGray; Log("已断开隧道，规则服务继续运行。"); }
}
