namespace Host2VMRelay;

public sealed partial class MainForm
{
    private TabPage Page(string title)
    {
        var page = new TabPage(title) { BackColor = Color.White, Padding = new Padding(16), AutoScroll = true };
        tabs.TabPages.Add(page); return page;
    }

    private TableLayoutPanel Grid(TabPage page)
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2 };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        UiLayout.WrapLabels(grid); page.Controls.Add(grid); return grid;
    }

    private static void Row(TableLayoutPanel grid, string title, Control control)
    {
        int row = grid.RowCount++; grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var label = new Label { Text = title, AutoSize = true, Margin = new Padding(0, 10, 12, 8) };
        control.Margin = new Padding(0, 5, 0, 9); control.Dock = DockStyle.Top;
        grid.Controls.Add(label, 0, row); grid.Controls.Add(control, 1, row);
    }

    private void BuildConnection()
    {
        var grid = Grid(Page("连接"));
        Row(grid, "虚拟机地址", host); Row(grid, "SSH 端口", port); Row(grid, "登录用户名", user);
        auth.Items.AddRange(new object[] { "密码登录", "私钥免密登录" }); Row(grid, "认证方式", auth);
        var keys = new TableLayoutPanel { AutoSize = true, ColumnCount = 2 };
        keys.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); keys.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        keyPath.Dock = DockStyle.Fill;
        var browse = UiLayout.Button("选择…", 80);
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Title = "选择私钥文件（不是 .pub 公钥）", InitialDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh") };
            if (dialog.ShowDialog(this) == DialogResult.OK) keyPath.Text = dialog.FileName;
        };
        keys.Controls.Add(keyPath, 0, 0); keys.Controls.Add(browse, 1, 0); Row(grid, "私钥文件", keys);
        keyControls = keys;
        auth.SelectedIndexChanged += (_, _) => keys.Enabled = auth.SelectedIndex == 1 && !busy && client?.IsConnected != true;
        secret.UseSystemPasswordChar = true; Row(grid, "密码 / 私钥口令", secret); Row(grid, "", remember);
        Row(grid, "本机 SOCKS5 端口", socksPort); Row(grid, "", retry);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        buttons.Controls.Add(connect); buttons.Controls.Add(disconnect); state.Margin = new Padding(10, 12, 0, 4); buttons.Controls.Add(state);
        Row(grid, "", buttons);
        Row(grid, "使用提示", UiLayout.Help("请先确认远端主机可以访问目标地址。首次 SSH 连接需要确认服务器指纹。\n关闭窗口会缩到托盘；退出应用会停止隧道。私钥登录直接读取所选文件，不复制私钥。"));
    }

    private void BuildRules()
    {
        var page = Page("域名 / IP");
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, MinimumSize = new Size(0, 360) };
        grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        grid.RowStyles.Add(new RowStyle(SizeType.AutoSize)); grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); grid.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        grid.Controls.Add(UiLayout.Help("每行一个地址，命中的 TCP 连接经远端主机转发。\n精确域名：code.example.com；域名及子域名：*.example.com\n单个 IP：10.20.30.40；网段：10.20.30.0/24；# 开头为注释"), 0, 0);
        ruleText.Multiline = true; ruleText.AcceptsReturn = true; ruleText.ScrollBars = ScrollBars.Both;
        ruleText.WordWrap = false; ruleText.Dock = DockStyle.Fill; ruleText.Font = UiLayout.CodeFont(); grid.Controls.Add(ruleText, 0, 1);
        var bar = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, WrapContents = true };
        var saveRules = UiLayout.Button("保存规则", 130);
        saveRules.Click += (_, _) =>
        {
            try
            {
                var payload = Rules.Compile(ruleText.Text);
                settings.Rules = ruleText.Text.Replace("\r", ""); settings.Save();
                if (client?.IsConnected == true) ClashRuleFile.Write(payload);
                Log("规则已保存；Clash 将重新读取本地规则文件。");
                MessageBox.Show(this, "已保存。隧道连接时规则生效；已有 DNS 缓存或连接可能需要刷新。", "规则已更新");
            }
            catch (Exception ex) { Error(ex); }
        };
        bar.Controls.Add(saveRules); bar.Controls.Add(new Label { Text = "规则变更无需重复粘贴脚本。", AutoSize = true, Margin = new Padding(6, 12, 0, 4) });
        grid.Controls.Add(bar, 0, 2); UiLayout.WrapLabels(grid); page.Controls.Add(grid);
    }

    private void BuildClash()
    {
        var grid = Grid(Page("接入 Clash"));
        Row(grid, "① 生成脚本", UiLayout.Help("没有自定义脚本时直接生成并复制；已有脚本时打开合并窗口，粘贴或导入 .js 文件，生成保留原逻辑的完整脚本。只需整体替换 Clash 当前订阅的扩展脚本，然后保存并重新应用。"));
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true };
        Button quickCopy = UiLayout.Button("直接生成并复制", 200), mergeScript = UiLayout.Button("导入 / 合并已有脚本", 220);
        quickCopy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(GenerateScript(null));
                MessageBox.Show(this, "已复制完整脚本。请到 Clash 当前订阅 → 编辑扩展脚本，整体替换、保存并重新应用。", "接入 Clash");
            }
            catch (Exception ex) { Error(ex); }
        };
        mergeScript.Click += (_, _) =>
        {
            using var dialog = new ScriptDialog(GenerateScript, existingClashScript);
            dialog.ShowDialog(this); existingClashScript = dialog.OriginalScript;
        };
        buttons.Controls.Add(quickCopy); buttons.Controls.Add(mergeScript); Row(grid, "", buttons);
        Row(grid, "② TUN 设置", UiLayout.Help("选择规则模式，开启 TUN 和自动路由。\n路由排除添加虚拟机 IP/32（IPv6 用 /128）。\nDNS 劫持包含 any:53 与 tcp://any:53。\n脚本启用 Fake-IP，为目标域名优先分配映射地址。部分版本的界面会覆盖 TUN 设置，请在界面确认。需要支持 fake-ip-filter-mode: rule 的 Mihomo 内核。"));
        Row(grid, "③ 验证", UiLayout.Help("连接隧道后打开目标网页，在 Clash 连接列表确认命中“Host2VMRelay”。底部状态仅表示本地规则是否启用。\nChrome 的自定义安全 DNS 可能绕过分流，请使用系统 DNS；规则也要包含登录跳转域名。"));
        var testUrl = new TextBox { Text = settings.TestUrl }; Row(grid, "测试网址", testUrl);
        var test = UiLayout.Button("通过 SOCKS5 测试网址", 240);
        test.Click += async (_, _) =>
        {
            test.Enabled = false;
            try
            {
                if (!Uri.TryCreate(testUrl.Text.Trim(), UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") || !string.IsNullOrEmpty(uri.UserInfo))
                    throw new ArgumentException("请填写 HTTP 或 HTTPS 网址，不要在网址中包含用户名和密码。");
                settings.TestUrl = uri.AbsoluteUri; settings.Save();
                using var handler = new SocketsHttpHandler { Proxy = new System.Net.WebProxy("socks5://127.0.0.1:" + socksPort.Value), UseProxy = true, AllowAutoRedirect = false };
                using var http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(20) };
                using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead);
                Log("SOCKS5 测试：HTTP " + (int)response.StatusCode);
                MessageBox.Show(this, "收到 HTTP " + (int)response.StatusCode + "。这验证 SOCKS5 隧道，不代表 TUN 已接管 DNS。", "测试结果");
            }
            catch (Exception ex) { Error(ex); }
            finally { if (!test.IsDisposed) test.Enabled = true; }
        };
        Row(grid, "", test);
        Row(grid, "边界", UiLayout.Help("仅支持 TCP（浏览器 HTTPS / SSH 等），不转发 UDP。请确保本机 SOCKS5 端口未被其他程序占用。程序不修改 Windows 路由或防火墙。"));
    }

    private string GenerateScript(string? existing)
    {
        string result = ClashScript.Generate((int)socksPort.Value, host.Text.Trim(), existing);
        // Preparing/copying a script must never deactivate an already connected tunnel.
        if (client?.IsConnected == true) ClashRuleFile.Write(Rules.Compile(settings.Rules));
        else ClashRuleFile.Disable();
        Log("完整 Clash 扩展脚本已生成。");
        return result;
    }

    private void BuildLog()
    {
        var page = Page("运行日志"); log.Multiline = true; log.ReadOnly = true; log.ScrollBars = ScrollBars.Vertical;
        log.Dock = DockStyle.Fill; log.Font = UiLayout.CodeFont(); page.Controls.Add(log);
    }

}
