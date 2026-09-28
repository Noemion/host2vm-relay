using System.Text;

namespace Host2VMRelay;

public sealed partial class MainForm
{
    private TableLayoutPanel PageContent(string title)
    {
        var page = new Panel { Text = title, AccessibleName = title, AccessibleRole = AccessibleRole.Pane,
            BackColor = UiTheme.Canvas, Padding = UiTheme.Spacing(2, 2, 10, 2) };
        var stack = UiLayout.Stack(); stack.BackColor = UiTheme.Canvas;
        page.Controls.Add(new PageScrollPanel(stack)); pages.AddPage(page); return stack;
    }
    private void BuildConnection()
    {
        var page = PageContent("连接");
        host.PlaceholderText = "例如 192.168.229.10"; user.PlaceholderText = "虚拟机登录用户名";
        auth.Items.AddRange(new object[] { "密码登录", "私钥登录" }); secret.UseSystemPasswordChar = true;
        var passwordField = UiLayout.Field("密码", secret);
        var keys = new TableLayoutPanel { AutoSize = true, Dock = DockStyle.Top, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keys.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var keyFrame = new EntryFrame(keyPath); keyFrame.Margin = UiTheme.Spacing(0, 0, 10, 0);
        keyPath.AccessibleName = "私钥文件路径";
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
            keyField.Visible = auth.SelectedIndex == 1; keys.Enabled = auth.SelectedIndex == 1 && !busy && session?.IsConnected != true;
            passwordField.Controls.OfType<Label>().First().Text = auth.SelectedIndex == 1 ? "私钥口令（没有可留空）" : "密码";
            secret.AccessibleName = auth.SelectedIndex == 1 ? "私钥口令" : "密码";
        };
        var identity = UiLayout.Card("SSH 连接", "填写虚拟机的地址与登录信息。",
            UiLayout.Pair(UiLayout.Field("虚拟机地址", host), UiLayout.Field("SSH 端口", port), 72),
            UiLayout.Pair(UiLayout.Field("用户名", user), UiLayout.Field("认证方式", auth)),
            keyField, passwordField, UiLayout.Actions(connect, disconnect), connectionLoad);
        UiLayout.Add(page, identity);
        var preferences = UiLayout.Stack(); preferences.Margin = UiTheme.Spacing(0, 4, 0, 0);
        remember.Margin = UiTheme.Spacing(0, 4, 0, 10); retry.Margin = UiTheme.Spacing(0, 4, 0, 10);
        UiLayout.Add(preferences, remember); remember.Dock = DockStyle.None;
        UiLayout.Add(preferences, retry); retry.Dock = DockStyle.None;
        UiLayout.Add(preferences, enableUdp); enableUdp.Dock = DockStyle.None;
        UiLayout.Add(page, UiLayout.Card("隧道偏好", "凭据仅当前 Windows 用户可解密。保存目录可在“设置”中更改。",
            UiLayout.Pair(UiLayout.Field("本机 SOCKS5 端口", socksPort), preferences, 45)));
        UiLayout.Add(page, UiLayout.Help("首次连接请核对服务器指纹。关闭窗口后连接保留在托盘，退出应用才会停止隧道。"));
        var copyExecutionCommand = UiLayout.Button("复制关闭命令", 170);
        var executionFeedback = UiLayout.Help(VmExecutionGuide.Recovery);
        copyExecutionCommand.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(VmExecutionGuide.DisableCommand);
                copyExecutionCommand.ShowFeedback("✓ 已复制");
                executionFeedback.Text = "命令已复制，请在麒麟虚拟机终端粘贴并执行。";
            }
            catch (Exception ex) { Error(ex); }
        };
        UiLayout.Add(page, UiLayout.Card("麒麟执行授权", VmExecutionGuide.Allow,
            UiLayout.Help(VmExecutionGuide.Disable), UiLayout.Help(VmExecutionGuide.DisableCommand),
            UiLayout.Actions(copyExecutionCommand), executionFeedback));
    }
    private void BuildRules()
    {
        var page = PageContent("转发规则");
        var summary = UiLayout.Help("尚未添加规则"); summary.Name = "ruleSummary";
        var saveRules = UiLayout.Primary("保存规则", 130); var copyRules = UiLayout.Button("复制规则", 120);
        void UpdateSummary()
        {
            bool dirty = ruleText.Text.Replace("\r", "") != settings.Rules.Replace("\r", "");
            try
            {
                int count = Rules.Compile(ruleText.Text).Split('\n', StringSplitOptions.RemoveEmptyEntries).Count(line => !line.TrimStart().StartsWith('#'));
                summary.Text = $"{count} 条规则 · " + (dirty ? "有未保存的更改" : "已保存"); summary.ForeColor = dirty ? UiTheme.Accent : UiTheme.Muted;
            }
            catch (FormatException) { summary.Text = "规则格式待修正 · 保存时将显示具体原因"; summary.ForeColor = Color.FromArgb(150, 90, 30); }
            copyRules.Enabled = ruleText.TextLength > 0;
        }
        ruleText.TextChanged += (_, _) => UpdateSummary();
        saveRules.Click += (_, _) =>
        {
            try
            {
                _ = Rules.Compile(ruleText.Text);
                settings = settings.SaveUpdated(candidate => candidate.Rules = ruleText.Text.Replace("\r", ""));
                UpdateSummary();
                try
                {
                    ApplyRouteFiles();
                    summary.Text = "保存成功，已提交 Clash 规则同步；结果见运行日志。";
                    Log("规则已保存；规则刷新结果将在日志中显示。");
                }
                catch (Exception ex)
                {
                    Error(new IOException("规则已保存，但同步到 Clash 失败；连接期间将自动重试。", ex));
                }
            }
            catch (Exception ex) { Error(ex); }
        };
        copyRules.Click += (_, _) => { try { Clipboard.SetText(ruleText.Text); summary.Text = "规则已复制"; copyRules.ShowFeedback("✓ 已复制"); } catch (Exception ex) { Error(ex); } };
        UiLayout.Add(page, UiLayout.Card("规则列表", "每行一个目标。命中的 TCP / UDP 优先经过虚拟机，不可用时回退原有分流。",
            UiLayout.Actions(saveRules, copyRules), new EntryFrame(PrepareRulesEditor(), true, 260), summary));
        UiLayout.Add(page, UiLayout.Card("支持的格式", "",
            UiLayout.Help("精确域名  code.example.com\n域名及子域名  *.example.com\n单个 IP  10.20.30.40\n网段  10.20.30.0/24"),
            UiLayout.Help("# 开头为注释。不填写协议、端口或网页路径。规则变更无需重新粘贴脚本，但服务模式可能需要在 Clash 中重新应用配置；已有连接和 DNS 缓存可能需要刷新。")));
        TextBox PrepareRulesEditor()
        {
            ruleText.Multiline = true; ruleText.AcceptsReturn = true; ruleText.ScrollBars = ScrollBars.Both;
            ruleText.WordWrap = false; ruleText.Font = UiLayout.CodeFont(); ruleText.MinimumSize = UiTheme.Size(0, 100);
            ruleText.AccessibleName = "转发规则编辑器"; return ruleText;
        }
    }
    private void BuildClash()
    {
        var page = PageContent("Clash 接入");
        var quickCopy = UiLayout.Primary("生成并复制", 150); var mergeScript = UiLayout.Button("合并已有脚本", 170);
        mergeScript.Name = "openScriptWorkspace";
        var scriptFeedback = UiLayout.Help("");
        quickCopy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(GenerateScript(null));
                quickCopy.ShowFeedback("✓ 已生成并复制");
                scriptFeedback.Text = ClashActivationGuide.Copied;
            }
            catch (Exception ex) { Error(ex); }
        };
        ScriptDialog? workspace = null;
        mergeScript.Click += (_, _) =>
        {
            if (workspace is null)
            {
                workspace = new ScriptDialog(GenerateScript, existingClashScript, embedded: true);
                page.Parent!.Controls.Add(workspace);
                workspace.BackRequested += (_, _) =>
                {
                    existingClashScript = workspace.OriginalScript;
                    workspace.Hide(); page.Show();
                };
            }
            page.Hide(); workspace.Show(); workspace.BringToFront();
        };
        UiLayout.Add(page, UiLayout.Card("01  生成扩展脚本", "没有自定义脚本时直接生成；也可粘贴旧版完整脚本，只更新托管区并保留自定义逻辑。将完整结果粘贴到当前订阅的“编辑扩展脚本”，保存并应用。", UiLayout.Actions(quickCopy, mergeScript), scriptFeedback));
        UiLayout.Add(page, UiLayout.Card("使脚本生效", ClashActivationGuide.Steps));
        var tunDetails = UiLayout.Stack(); tunDetails.Name = "tunInstructions";
        UiLayout.Add(tunDetails, UiLayout.Help("模式：规则模式\n虚拟网卡：开启 TUN 与自动路由\nDNS 劫持：any:53、tcp://any:53\n路由排除：虚拟机 IPv4/32，或 IPv6/128"));
        UiLayout.Add(tunDetails, UiLayout.Help("不要排除需要转发的目标 IP。TUN 界面字段由 Clash 管理，扩展脚本不会覆盖这些字段。Fake-IP 规则需要支持 fake-ip-filter-mode: rule 的 Mihomo 内核。"));
        var toggle = UiLayout.Button("查看设置说明", 170); toggle.Name = "toggleTunGuide";
        toggle.Click += (_, _) => { tunDetails.Visible = !tunDetails.Visible; toggle.Text = tunDetails.Visible ? "收起设置说明" : "查看设置说明"; };
        var copyExclusion = UiLayout.Button("复制路由排除", 170);
        var routeFeedback = UiLayout.Help("复制虚拟机地址后，粘贴到 Clash 的路由排除设置。");
        copyExclusion.Click += (_, _) =>
        {
            try
            {
                if (!System.Net.IPAddress.TryParse(host.Text.Trim(), out var address)) throw new ArgumentException("请先填写正确的虚拟机 IP。");
                Clipboard.SetText(address + (address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? "/32" : "/128")); Log("虚拟机路由排除地址已复制。");
                copyExclusion.ShowFeedback("✓ 已复制");
                routeFeedback.Text = "复制成功：" + address + "，请粘贴到 Clash 的路由排除设置。";
            }
            catch (Exception ex) { Error(ex); }
        };
        UiLayout.Add(page, UiLayout.Card("02  配置虚拟网卡", "在 Clash 设置界面完成 TUN、DNS 劫持和虚拟机路由排除。", UiLayout.Actions(toggle, copyExclusion), routeFeedback, tunDetails));
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
                settings = settings.SaveUpdated(candidate => candidate.TestUrl = uri.AbsoluteUri);
                testResult.Text = "正在通过隧道测试…";
                using var handler = new SocketsHttpHandler { Proxy = new System.Net.WebProxy("socks5://127.0.0.1:" + socksPort.Value), UseProxy = true, AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                testResult.Text = "收到 HTTP " + (int)response.StatusCode + " · SOCKS5 请求已完成"; Log("SOCKS5 测试：HTTP " + (int)response.StatusCode);
            }
            catch (Exception ex) { testResult.Text = "测试未完成，请检查连接与运行日志。"; Error(ex); }
            finally { if (!test.IsDisposed) test.Enabled = true; }
        };
        var diagnose = UiLayout.Primary("检测路由是否生效", 210);
        diagnose.Name = "diagnoseRoute";
        var cancelDiagnostic = UiLayout.Button("取消检测", 110); cancelDiagnostic.Enabled = false;
        var diagnosticResult = UiLayout.Help("尚未检测。输入目标网址后，可核对规则、实际 TCP 链路和 TLS 证书。");
        diagnosticResult.Name = "routeDiagnosticResult";
        CancellationTokenSource? diagnosticCancellation = null;
        cancelDiagnostic.Click += (_, _) => diagnosticCancellation?.Cancel();
        diagnose.Click += async (_, _) =>
        {
            diagnose.Enabled = false; cancelDiagnostic.Enabled = true;
            var evidence = new List<string>();
            using var operation = CancellationTokenSource.CreateLinkedTokenSource(formLifetime.Token);
            diagnosticCancellation = operation;
            try
            {
                var target = RouteDiagnostics.ParseTarget(testUrl.Text);
                diagnosticResult.Text = "正在检测…";
                await RouteDiagnostics.RunAsync(target, settings.Rules, session?.Health.Tcp == true, line =>
                {
                    if (IsDisposed || diagnosticResult.IsDisposed) return;
                    evidence.Add(line); diagnosticResult.Text = string.Join(Environment.NewLine, evidence);
                    Log("路由诊断：" + line);
                }, operation.Token);
            }
            catch (Exception ex) { if (!diagnosticResult.IsDisposed) diagnosticResult.Text = "检测未完成：" + ex.Message; }
            finally
            {
                diagnosticCancellation = null;
                if (!diagnose.IsDisposed) { diagnose.Enabled = true; cancelDiagnostic.Enabled = false; }
            }
        };
        UiLayout.Add(page, UiLayout.Card("03  验证连接", "路由检测通过 Clash 发起一次无登录信息的 TCP 请求，并读取对应连接记录。SOCKS5 测试仅验证虚拟机通道。",
            UiLayout.Field("测试网址", testUrl), UiLayout.Actions(diagnose, cancelDiagnostic), diagnosticResult, UiLayout.Actions(test), testResult));
        UiLayout.Add(page, UiLayout.Help("UDP 保持原域名、IP 和端口；支持 Linux x64 / ARM64，连接时自动准备转发组件。单播数据报经 SSH 封装，不支持广播/组播；Chrome 自定义安全 DNS 可能绕过分流。"));
    }
    private string GenerateScript(string? existing)
    {
        string result = ClashScript.Generate((int)socksPort.Value, host.Text.Trim(), existing);
        ApplyRouteFiles(); Log("完整 Clash 扩展脚本已生成。"); return result;
    }
}
