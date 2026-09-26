namespace Host2VMRelay;

public sealed partial class MainForm
{
    private readonly List<NavigationButton> navigationButtons = new();
    private TableLayoutPanel? shellBody, sidebar, contentArea;
    private Panel? compactNavigation;
    private FlowLayoutPanel? navigation;
    private Label? pageTitle, pageDescription;
    private PictureBox? brandImage;
    private bool changingShell;
    private static readonly string[] PageTitles = { "连接虚拟机", "转发规则", "Clash 接入", "运行日志", "设置" };
    private static readonly string[] PageDescriptions = { "通过 SSH，连接你信任的网络。", "只转发指定的域名、IP 与网段。", "一次接入，后续规则自动更新。", "连接、规则与诊断，一目了然。", "配置存储与显示偏好。" };

    private void BuildShell()
    {
        var outer = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = Padding.Empty, BackColor = UiTheme.Canvas };
        outer.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); outer.RowStyles.Add(new RowStyle(SizeType.AutoSize)); outer.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        compactNavigation = new Panel { Dock = DockStyle.Top, AutoSize = true, Margin = Padding.Empty, Padding = UiTheme.Spacing(16, 10, 16, 0), BackColor = UiTheme.Sidebar, Visible = false };
        shellBody = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        shellBody.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiTheme.Units(204))); shellBody.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); shellBody.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = UiTheme.Spacing(14, 26, 14, 20), BackColor = UiTheme.Sidebar };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); sidebar.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var brand = UiLayout.Stack(); brand.Margin = UiTheme.Spacing(8, 0, 0, 26);
        brandImage = new PictureBox { Size = UiTheme.Size(42, 42), SizeMode = PictureBoxSizeMode.Zoom, Margin = UiTheme.Spacing(0, 0, 0, 12), TabStop = false };
        using (var icon = AppIcon.Load(128)) brandImage.Image = icon.ToBitmap();
        UiLayout.Add(brand, brandImage); brandImage.Dock = DockStyle.None;
        UiLayout.Add(brand, UiLayout.Heading("Host2VMRelay", 12)); UiLayout.Add(brand, UiLayout.Help("选择性网络中继")); sidebar.Controls.Add(brand, 0, 0);
        navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, BackColor = UiTheme.Sidebar };
        string[] labels = { "连接", "转发规则", "Clash 接入", "运行日志", "设置" };
        for (int i = 0; i < labels.Length; i++)
        {
            var button = new NavigationButton { PageIndex = i, Text = labels[i], AccessibleName = PageTitles[i], Name = "navigation-" + i, Margin = UiTheme.Spacing(0, 0, 0, 8) };
            button.Click += (_, _) => SelectPage(button.PageIndex); navigationButtons.Add(button); navigation.Controls.Add(button);
        }
        sidebar.Controls.Add(navigation, 0, 1);
        var footer = UiLayout.Stack(); UiLayout.Add(footer, UiLayout.Help("仅转发选定流量"));
        UiLayout.Add(footer, UiLayout.Help("v" + (typeof(MainForm).Assembly.GetName().Version?.ToString(3) ?? ""))); sidebar.Controls.Add(footer, 0, 2);
        contentArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = UiTheme.Spacing(26, 22, 26, 12), BackColor = UiTheme.Canvas };
        contentArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        contentArea.RowStyles.Add(new RowStyle(SizeType.AutoSize)); contentArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); contentArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = UiTheme.Spacing(0, 0, 0, 16) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize)); header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pageTitle = UiLayout.Heading(PageTitles[0], 22); pageTitle.Name = "pageTitle";
        pageDescription = UiLayout.Help(PageDescriptions[0]); pageDescription.Name = "pageDescription";
        state.Anchor = AnchorStyles.Top | AnchorStyles.Right; state.Margin = UiTheme.Spacing(12, 6, 0, 0);
        header.Controls.Add(pageTitle, 0, 0); header.Controls.Add(state, 1, 0);
        header.Controls.Add(pageDescription, 0, 1); header.SetColumnSpan(pageDescription, 2); UiLayout.WrapLabels(header);
        contentArea.Controls.Add(header, 0, 0); contentArea.Controls.Add(tabs, 0, 1);
        feed.Margin = UiTheme.Spacing(0, 10, 0, 0); contentArea.Controls.Add(feed, 0, 2); UiLayout.WrapLabels(contentArea);
        shellBody.Controls.Add(sidebar, 0, 0); shellBody.Controls.Add(contentArea, 1, 0);
        outer.Controls.Add(compactNavigation, 0, 0); outer.Controls.Add(shellBody, 0, 1); Controls.Add(outer);
        tabs.SelectedIndexChanged += (_, _) => RefreshNavigation(); SizeChanged += (_, _) => UpdateShellLayout();
        FormClosed += (_, _) => { brandImage.Image?.Dispose(); brandImage.Image = null; }; UpdateShellLayout();
    }
    private void SelectPage(int index)
    {
        if (index < 0 || index >= tabs.TabCount) return; tabs.SelectedIndex = index; RefreshNavigation();
    }
    private void RefreshNavigation()
    {
        int index = tabs.SelectedIndex;
        if (index < 0 || pageTitle is null || pageDescription is null) return;
        pageTitle.Text = PageTitles[index]; pageDescription.Text = PageDescriptions[index];
        foreach (var button in navigationButtons) button.Selected = button.PageIndex == index;
    }
    private void UpdateShellLayout()
    {
        if (changingShell || shellBody is null || sidebar is null || compactNavigation is null || navigation is null || contentArea is null) return;
        changingShell = true;
        try
        {
            bool compact = ClientSize.Width * 96.0 / Math.Max(96, DeviceDpi) < UiTheme.Units(860);
            var hostPanel = compact ? compactNavigation : (Control)sidebar;
            if (navigation.Parent != hostPanel)
            {
                navigation.Parent?.Controls.Remove(navigation);
                if (compact) compactNavigation.Controls.Add(navigation); else sidebar.Controls.Add(navigation, 0, 1);
            }
            compactNavigation.Visible = compact; sidebar.Visible = !compact;
            if (pageDescription is not null) pageDescription.Visible = !compact;
            shellBody.ColumnStyles[0].Width = compact ? 0 : UiTheme.Px(this, 204);
            navigation.FlowDirection = compact ? FlowDirection.LeftToRight : FlowDirection.TopDown;
            navigation.WrapContents = compact; navigation.AutoSize = compact; navigation.Dock = compact ? DockStyle.Top : DockStyle.Fill;
            string[] shortNames = { "连接", "规则", "Clash", "日志", "设置" }; string[] longNames = { "连接", "转发规则", "Clash 接入", "运行日志", "设置" };
            foreach (var button in navigationButtons)
            {
                button.Compact = compact; button.Text = compact ? shortNames[button.PageIndex] : longNames[button.PageIndex];
                button.MinimumSize = new Size(UiTheme.Px(this, compact ? 82 : 174), UiTheme.Px(this, compact ? 40 : 44));
                button.Margin = new Padding(0, 0, UiTheme.Px(this, compact ? 6 : 0), UiTheme.Px(this, 6));
            }
            contentArea.Padding = new Padding(UiTheme.Px(this, compact ? 16 : 26), UiTheme.Px(this, compact ? 10 : 22), UiTheme.Px(this, compact ? 16 : 26), UiTheme.Px(this, 10));
        }
        finally { changingShell = false; }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        for (int i = 0; i < PageTitles.Length; i++) if (keyData == (Keys.Alt | (Keys)((int)Keys.D1 + i))) { SelectPage(i); return true; }
        if (keyData == (Keys.Control | Keys.Tab)) { SelectPage((tabs.SelectedIndex + 1) % tabs.TabCount); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { SelectPage((tabs.SelectedIndex + tabs.TabCount - 1) % tabs.TabCount); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
