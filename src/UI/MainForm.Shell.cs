namespace Host2VMRelay;

public sealed partial class MainForm
{
    private readonly List<NavigationButton> navigationButtons = new();
    private TableLayoutPanel? shellBody, sidebar, contentArea;
    private FlowLayoutPanel? navigation;
    private NavigationButton? aboutButton;
    private Label? pageTitle, pageDescription;
    private PictureBox? brandImage;
    private bool changingShell;
    private bool shellLayoutReady;
    private static readonly string[] PageTitles = { "连接虚拟机", "转发规则", "Clash 接入", "运行日志", "设置", "关于" };
    private static readonly string[] PageDescriptions = { "通过 SSH，连接你信任的网络。", "只转发指定的域名、IP 与网段。", "一次接入，后续规则自动更新。", "连接、规则与诊断，一目了然。", "配置存储与显示偏好。", "项目信息与软件更新。" };

    private void BuildShell()
    {
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
        navigation = new FlowLayoutPanel { Dock = DockStyle.Fill, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.TopDown, WrapContents = false, Margin = Padding.Empty, BackColor = UiTheme.Sidebar };
        string[] labels = { "连接", "转发规则", "Clash 接入", "运行日志", "设置" };
        for (int i = 0; i < labels.Length; i++)
        {
            var button = new NavigationButton { PageIndex = i, Text = labels[i], AccessibleName = PageTitles[i], Name = "navigation-" + i, Margin = UiTheme.Spacing(0, 0, 0, 8) };
            button.Click += (_, _) => SelectPage(button.PageIndex); navigationButtons.Add(button); navigation.Controls.Add(button);
        }
        sidebar.Controls.Add(navigation, 0, 1);
        var sidebarFooter = UiLayout.Stack();
        aboutButton = new NavigationButton { PageIndex = AboutPageIndex, Text = "关于", AccessibleName = "关于", Name = "navigation-5" };
        aboutButton.Click += (_, _) => SelectPage(AboutPageIndex);
        navigationButtons.Add(aboutButton); UiLayout.Add(sidebarFooter, aboutButton); sidebar.Controls.Add(sidebarFooter, 0, 2);
        contentArea = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, Margin = Padding.Empty, Padding = UiTheme.Spacing(26, 22, 26, 12), BackColor = UiTheme.Canvas };
        contentArea.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        contentArea.RowStyles.Add(new RowStyle(SizeType.AutoSize)); contentArea.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); contentArea.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var header = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 2, RowCount = 2, Margin = UiTheme.Spacing(0, 0, 0, 16) };
        header.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); header.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        header.RowStyles.Add(new RowStyle(SizeType.AutoSize)); header.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        pageTitle = UiLayout.Heading(PageTitles[0], 18); pageTitle.Name = "pageTitle";
        pageDescription = UiLayout.Help(PageDescriptions[0]); pageDescription.Name = "pageDescription";
        state.Anchor = AnchorStyles.Top | AnchorStyles.Right; state.Margin = UiTheme.Spacing(12, 6, 0, 0);
        header.Controls.Add(pageTitle, 0, 0); header.Controls.Add(state, 1, 0);
        header.Controls.Add(pageDescription, 0, 1); header.SetColumnSpan(pageDescription, 2);
        contentArea.Controls.Add(header, 0, 0); contentArea.Controls.Add(pages, 0, 1);
        feed.Margin = UiTheme.Spacing(0, 10, 0, 0); contentArea.Controls.Add(feed, 0, 2);
        shellBody.Controls.Add(sidebar, 0, 0); shellBody.Controls.Add(contentArea, 1, 0);
        Controls.Add(shellBody);
        pages.SelectedIndexChanged += (_, _) => RefreshNavigation(); SizeChanged += (_, _) => UpdateShellLayout();
        FormClosed += (_, _) => { brandImage.Image?.Dispose(); brandImage.Image = null; }; UpdateShellLayout();
    }
    private void SelectPage(int index)
    {
        if (index == AboutPageIndex && pages.SelectedIndex != AboutPageIndex) aboutReturnPage = pages.SelectedIndex;
        if (index < 0 || index >= pages.PageCount) return; pages.SelectedIndex = index; RefreshNavigation();
    }
    private void RefreshNavigation()
    {
        int index = pages.SelectedIndex;
        if (index < 0 || pageTitle is null || pageDescription is null) return;
        pageTitle.Text = PageTitles[index]; pageDescription.Text = PageDescriptions[index];
        foreach (var button in navigationButtons) button.Selected = button.PageIndex == index;
    }
    private void UpdateShellLayout()
    {
        if (!shellLayoutReady || changingShell || shellBody is null || contentArea is null) return;
        changingShell = true;
        try
        {
            shellBody.ColumnStyles[0].Width = UiTheme.Px(this, 204);
            foreach (var button in navigationButtons)
            {
                button.MinimumSize = new Size(UiTheme.Px(this, 174), UiTheme.Px(this, 44));
                button.Margin = new Padding(0, 0, 0, UiTheme.Px(this, 6));
            }
            contentArea.Padding = new Padding(UiTheme.Px(this, 26), UiTheme.Px(this, 22), UiTheme.Px(this, 26), UiTheme.Px(this, 10));
        }
        finally { changingShell = false; }
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (pages.SelectedIndex == AboutPageIndex && keyData == (Keys.Alt | Keys.Left)) { SelectPage(aboutReturnPage); return true; }
        for (int i = 0; i < PageTitles.Length; i++) if (keyData == (Keys.Alt | (Keys)((int)Keys.D1 + i))) { SelectPage(i); return true; }
        if (keyData == (Keys.Control | Keys.Tab)) { SelectPage((pages.SelectedIndex + 1) % pages.PageCount); return true; }
        if (keyData == (Keys.Control | Keys.Shift | Keys.Tab)) { SelectPage((pages.SelectedIndex + pages.PageCount - 1) % pages.PageCount); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
}
