using System.Text;

namespace Host2VMRelay;

public sealed partial class MainForm
{
    private TableLayoutPanel PageContent(string title)
    {
        var page = new TabPage(title) { BackColor = UiTheme.Canvas, Padding = new Padding(2, 2, 10, 2), AutoScroll = true };
        var stack = UiLayout.Stack(); stack.BackColor = UiTheme.Canvas;
        page.Controls.Add(stack); tabs.TabPages.Add(page); return stack;
    }

    private void BuildConnection()
    {
        var page = PageContent("连接");
        host.PlaceholderText = "例如 192.168.229.10"; user.PlaceholderText = "虚拟机登录用户名";
        auth.Items.AddRange(new object[] { "密码登录", "私钥登录" });
        secret.UseSystemPasswordChar = true;
        var passwordField = UiLayout.Field("密码", secret);
        var keys = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keys.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var keyFrame = new EntryFrame(keyPath); keyFrame.Margin = new Padding(0, 0, 10, 0);
        var browse = UiLayout.Button("浏览…", 86); browse.Margin = Padding.Empty;
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "选择私钥文件（不是 .pub 公钥）", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh") };
            if (dialog.ShowDialog(this) == DialogResult.OK) keyPath.Text = dialog.FileName;
        };
        keys.Controls.Add(keyFrame, 0, 0); keys.Controls.Add(browse, 1, 0); keyControls = keys;
        var keyField = UiLayout.Field("私钥文件", keys, frame: false);
        auth.SelectedIndexChanged += (_, _) =>
        {
            keyField.Visible = auth.SelectedIndex == 1;
            keys.Enabled = auth.SelectedIndex == 1 && !busy && client?.IsConnected != true;
            passwordField.Controls.OfType<Label>().First().Text = auth.SelectedIndex == 1 ? "私钥口令（没有可留空）" : "密码";
            secret.AccessibleName = auth.SelectedIndex == 1 ? "私钥口令" : "密码";
        };
        var identity = UiLayout.Card("SSH 连接", "填写虚拟机的地址与登录信息。",
            UiLayout.Pair(UiLayout.Field("虚拟机地址", host), UiLayout.Field("SSH 端口", port), 72),
            UiLayout.Pair(UiLayout.Field("用户名", user), UiLayout.Field("认证方式", auth)),
            keyField, passwordField, UiLayout.Actions(connect, disconnect));
        UiLayout.Add(page, identity);
        var preferences = UiLayout.Stack(); preferences.Margin = new Padding(0, 4, 0, 0);
        remember.Margin = new Padding(0, 4, 0, 10); retry.Margin = new Padding(0, 4, 0, 10);
        UiLayout.Add(preferences, remember); remember.Dock = DockStyle.None;
        UiLayout.Add(preferences, retry); retry.Dock = DockStyle.None;
        UiLayout.Add(page, UiLayout.Card("隧道偏好", "凭据加密保存在当前用户的文档目录，仅当前 Windows 用户可解密。",
            UiLayout.Pair(UiLayout.Field("本机 SOCKS5 端口", socksPort), preferences, 45)));
        UiLayout.Add(page, UiLayout.Help("首次连接请核对服务器指纹。关闭窗口后连接保留在托盘，退出应用才会停止隧道。"));
    }

    private void BuildRules()
    {
        var page = PageContent("转发规则");
        var summary = UiLayout.Help("尚未添加规则"); summary.Name = "ruleSummary";
        var saveRules = UiLayout.Primary("保存规则", 130);
        var copyRules = UiLayout.Button("复制规则", 120);
        void UpdateSummary()
        {
            bool dirty = ruleText.Text.Replace("\r", "") != settings.Rules.Replace("\r", "");
            try
            {
                int count = Rules.Compile(ruleText.Text).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
                summary.Text = $"{count} 条规则 · " + (dirty ? "有未保存的更改" : "已保存");
                summary.ForeColor = dirty ? UiTheme.Accent : UiTheme.Muted;
            }
            catch (FormatException) { summary.Text = "规则格式待修正 · 保存时将显示具体原因"; summary.ForeColor = Color.FromArgb(150, 90, 30); }
            copyRules.Enabled = ruleText.TextLength > 0;
        }
        ruleText.TextChanged += (_, _) => UpdateSummary();
        saveRules.Click += (_, _) =>
        {
            try
            {
                var payload = Rules.Compile(ruleText.Text);
                settings.Rules = ruleText.Text.Replace("\r", ""); settings.Save();
                if (client?.IsConnected == true) ClashRuleFile.Write(payload);
                UpdateSummary(); Log("规则已保存；Clash 将重新读取本地规则文件。");
            }
            catch (Exception ex) { Error(ex); }
        };
        copyRules.Click += (_, _) => { try { Clipboard.SetText(ruleText.Text); summary.Text = "规则已复制"; } catch (Exception ex) { Error(ex); } };
        UiLayout.Add(page, UiLayout.Card("规则列表", "每行一个目标。只有命中的 TCP 连接会通过虚拟机转发。",
            UiLayout.Actions(saveRules, copyRules), new EntryFrame(PrepareRulesEditor(), true, 260), summary));
        UiLayout.Add(page, UiLayout.Card("支持的格式", "",
            UiLayout.Help("精确域名  code.example.com\n域名及子域名  *.example.com\n单个 IP  10.20.30.40\n网段  10.20.30.0/24"),
            UiLayout.Help("# 开头为注释。不填写协议、端口或网页路径。规则变更无需重新粘贴脚本；已有连接和 DNS 缓存可能需要刷新。")));
        TextBox PrepareRulesEditor()
        {
            ruleText.Multiline = true; ruleText.AcceptsReturn = true; ruleText.ScrollBars = ScrollBars.Both;
            ruleText.WordWrap = false; ruleText.Font = UiLayout.CodeFont(); ruleText.MinimumSize = new Size(0, 100);
            return ruleText;
        }
    }

    private void BuildClash()
    {
        var page = PageContent("Clash 接入");
        var quickCopy = UiLayout.Primary("生成并复制", 150);
        var mergeScript = UiLayout.Button("合并已有脚本", 170);
        quickCopy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(GenerateScript(null));
                MessageBox.Show(this, "完整脚本已复制。请整体替换 Clash 当前订阅的扩展脚本，保存并重新应用。\nTUN 参数请在 Clash 设置界面配置。", "脚本已复制");
            }
            catch (Exception ex) { Error(ex); }
        };
        mergeScript.Click += (_, _) =>
        {
            using var dialog = new ScriptDialog(GenerateScript, existingClashScript);
            dialog.ShowDialog(this); existingClashScript = dialog.OriginalScript;
        };
        UiLayout.Add(page, UiLayout.Card("01  生成扩展脚本", "没有自定义脚本时直接生成；已有 JavaScript 时合并保留原逻辑。将完整结果粘贴到当前订阅的“编辑扩展脚本”，保存并应用。", UiLayout.Actions(quickCopy, mergeScript)));
        var tunDetails = UiLayout.Stack(); tunDetails.Name = "tunInstructions";
        UiLayout.Add(tunDetails, UiLayout.Help("模式：规则模式\n虚拟网卡：开启 TUN 与自动路由\nDNS 劫持：any:53、tcp://any:53\n路由排除：虚拟机 IPv4/32，或 IPv6/128"));
        UiLayout.Add(tunDetails, UiLayout.Help("不要排除需要转发的目标 IP。TUN 界面字段由 Clash 管理，扩展脚本不会覆盖这些字段。Fake-IP 规则需要支持 fake-ip-filter-mode: rule 的 Mihomo 内核。"));
        var toggle = UiLayout.Button("查看设置说明", 170); toggle.Name = "toggleTunGuide";
        toggle.Click += (_, _) => { tunDetails.Visible = !tunDetails.Visible; toggle.Text = tunDetails.Visible ? "收起设置说明" : "查看设置说明"; };
        var copyExclusion = UiLayout.Button("复制路由排除", 170);
        copyExclusion.Click += (_, _) =>
        {
            try
            {
                if (!System.Net.IPAddress.TryParse(host.Text.Trim(), out var address)) throw new ArgumentException("请先填写正确的虚拟机 IP。");
                Clipboard.SetText(address + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128"));
                Log("虚拟机路由排除地址已复制。");
            }
            catch (Exception ex) { Error(ex); }
        };
        UiLayout.Add(page, UiLayout.Card("02  配置虚拟网卡", "在 Clash 设置界面完成 TUN、DNS 劫持和虚拟机路由排除。", UiLayout.Actions(toggle, copyExclusion), tunDetails));
        tunDetails.Visible = false;
        var testUrl = new TextBox { Text = settings.TestUrl, Name = "testUrl" };
        var test = UiLayout.Button("测试 SOCKS5 连通性", 220);
        var testResult = UiLayout.Help("连接隧道后测试。此操作不验证 TUN 是否接管 DNS。"); testResult.Name = "testResult";
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            try
            {
                if (!Uri.TryCreate(testUrl.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new ArgumentException("请填写 HTTP 或 HTTPS 网址，不要在网址中包含用户名和密码。");
                settings.TestUrl = uri.AbsoluteUri; settings.Save(); testResult.Text = "正在通过隧道测试…";
                using var handler = new SocketsHttpHandler { Proxy = new System.Net.WebProxy("socks5://127.0.0.1:" + socksPort.Value), UseProxy = true, AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                testResult.Text = "收到 HTTP " + (int)response.StatusCode + " · SOCKS5 请求已完成";
                Log("SOCKS5 测试：HTTP " + (int)response.StatusCode);
            }
            catch (Exception ex) { testResult.Text = "测试未完成，请检查连接与运行日志。"; Error(ex); }
            finally { if (!test.IsDisposed) test.Enabled = true; }
        };
        UiLayout.Add(page, UiLayout.Card("03  验证连接", "浏览目标网页，并在 Clash 连接列表确认命中 Host2VMRelay。", UiLayout.Field("测试网址", testUrl), UiLayout.Actions(test), testResult));
        UiLayout.Add(page, UiLayout.Help("仅支持 TCP，不转发 UDP。Chrome 自定义安全 DNS 可能绕过分流；规则还需包含登录跳转域名。"));
    }

    private string GenerateScript(string? existing)
    {
        string result = ClashScript.Generate((int)socksPort.Value, host.Text.Trim(), existing);
        if (client?.IsConnected == true) ClashRuleFile.Write(Rules.Compile(settings.Rules)); else ClashRuleFile.Disable();
        Log("完整 Clash 扩展脚本已生成。"); return result;
    }

    private void BuildLog()
    {
        var page = PageContent("运行日志");
        var copyLog = UiLayout.Button("复制日志", 120); var exportLog = UiLayout.Button("导出日志", 120); var clearLog = UiLayout.Button("清空", 90);
        var counter = UiLayout.Help("连接与操作发生后，记录将显示在这里。"); counter.Name = "logSummary";
        log.TextChanged += (_, _) =>
        {
            copyLog.Enabled = exportLog.Enabled = clearLog.Enabled = log.TextLength > 0;
            counter.Text = log.TextLength == 0 ? "暂无日志" : log.Lines.Count(line => line.Length > 0) + " 条记录 · 仅保留本次运行日志";
        };
        copyLog.Click += (_, _) => { try { if (log.TextLength > 0) Clipboard.SetText(log.Text); } catch (Exception ex) { Error(ex); } };
        exportLog.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Title = "导出运行日志", Filter = "文本文件|*.txt", FileName = "Host2VMRelay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", InitialDirectory = Settings.Folder };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try { File.WriteAllText(dialog.FileName, log.Text, new UTF8Encoding(false)); } catch (Exception ex) { Error(ex); }
        };
        clearLog.Click += (_, _) => log.Clear();
        UiLayout.Add(page, UiLayout.Card("活动记录", "", UiLayout.Actions(copyLog, exportLog, clearLog), UiLayout.Editor(log, 380, true), counter));
        UiLayout.Add(page, UiLayout.Help("分享日志前请检查其中的主机地址、用户名等信息。清空仅影响当前日志，不会清除连接配置。"));
    }
}
