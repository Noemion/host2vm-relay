using System.Runtime.InteropServices;
using System.Text;

namespace Host2VMRelay;

internal sealed class ScriptDialog : Form
{
    private readonly Func<string?, string> generate;
    private readonly CheckBox merge = new() { Text = "合并已有脚本", AutoSize = true };
    private readonly TextBox source = new() { Name = "originalScript" }, output = new() { Name = "generatedScript" };
    private readonly Button import = UiLayout.Button("导入脚本…", 130), copy = UiLayout.Button("复制", 70), save = UiLayout.Button("另存为", 90);
    private readonly Label status = UiLayout.Help("先生成完整脚本，再复制或保存。");
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0, 12, 0, 12) };
    private readonly TableLayoutPanel editors = new() { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    private readonly CardPanel originalCard, generatedCard;
    private readonly EntryFrame originalFrame, generatedFrame;
    private Icon? appIcon;
    private bool layingOut;
    public string OriginalScript => merge.Checked ? source.Text : "";

    public ScriptDialog(Func<string?, string> generate, string existingScript)
    {
        this.generate = generate;
        SuspendLayout(); DoubleBuffered = true;
        Font = UiLayout.BodyFont(); ForeColor = UiTheme.Ink; BackColor = UiTheme.Canvas;
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Host2VMRelay — 脚本工作区";
        ClientSize = new Size(1080, 780); MinimumSize = new Size(680, 520);
        StartPosition = FormStartPosition.CenterParent; UpdateIcon(96);
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 5, Padding = new Padding(22), Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 5; i++) root.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));
        var header = UiLayout.Stack();
        UiLayout.Add(header, UiLayout.Heading("脚本工作区", 22));
        UiLayout.Add(header, UiLayout.Help("保留原有逻辑，生成可直接粘贴的完整配置。"));
        root.Controls.Add(header, 0, 0);
        merge.Margin = new Padding(0, 12, 22, 6);
        root.Controls.Add(UiLayout.Actions(merge, import), 0, 1);
        source.MaxLength = ScriptComposer.MaxSourceLength + 32768; source.AcceptsTab = true;
        originalFrame = UiLayout.Editor(source, 260);
        generatedFrame = UiLayout.Editor(output, 260, true);
        originalCard = UiLayout.Card("原始脚本", "JavaScript 扩展脚本，不是 YAML 订阅。", originalFrame);
        generatedCard = UiLayout.Card("完整预览", "整体替换当前订阅的扩展脚本。", generatedFrame);
        viewport.Controls.Add(editors); root.Controls.Add(viewport, 0, 2);
        var compose = UiLayout.Primary("生成并复制", 140); compose.Name = "composeScript";
        var close = UiLayout.Button("关闭", 70); close.DialogResult = DialogResult.Cancel; CancelButton = close;
        root.Controls.Add(UiLayout.Actions(compose, copy, save, close), 0, 3);
        status.Margin = new Padding(0, 8, 0, 0); root.Controls.Add(status, 0, 4);
        UiLayout.WrapLabels(root); Controls.Add(root);
        source.Text = WindowsLines(existingScript); merge.Checked = !string.IsNullOrWhiteSpace(existingScript);
        source.Enabled = merge.Checked; copy.Enabled = save.Enabled = false;
        merge.CheckedChanged += (_, _) => { source.Enabled = merge.Checked; InvalidateOutput(); UpdateWorkspaceLayout(); };
        source.TextChanged += (_, _) => InvalidateOutput();
        import.Click += (_, _) => Import(); compose.Click += (_, _) => GenerateAndCopy();
        copy.Click += (_, _) => Copy(); save.Click += (_, _) => SaveOutput();
        viewport.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        Shown += (_, _) => { UpdateIcon(DeviceDpi); UiLayout.FitToScreen(this, new Size(680, 520)); UpdateWorkspaceLayout(); };
        DpiChanged += (_, e) => { UpdateIcon(e.DeviceDpiNew); BeginInvoke(() => { UiLayout.FitToScreen(this, new Size(680, 520)); UpdateWorkspaceLayout(); }); };
        FormClosed += (_, _) => appIcon?.Dispose();
        UpdateWorkspaceLayout(); ResumeLayout(true);
    }

    private void UpdateWorkspaceLayout()
    {
        if (layingOut || originalCard is null || generatedCard is null) return;
        layingOut = true;
        try
        {
            bool dual = merge.Checked && viewport.ClientSize.Width * 96.0 / Math.Max(96, DeviceDpi) >= 840;
            editors.SuspendLayout();
            int columns = dual ? 2 : 1;
            if (editors.ColumnCount != columns || originalCard.Parent is null || generatedCard.Parent is null)
            {
                editors.Controls.Clear(); editors.ColumnStyles.Clear(); editors.RowStyles.Clear();
                editors.ColumnCount = columns; editors.RowCount = dual ? 1 : 2;
                for (int i = 0; i < columns; i++) editors.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F / columns));
                for (int i = 0; i < editors.RowCount; i++) editors.RowStyles.Add(new RowStyle(SizeType.AutoSize));
                editors.Controls.Add(originalCard, 0, 0); editors.Controls.Add(generatedCard, dual ? 1 : 0, dual ? 0 : 1);
            }
            originalCard.Visible = merge.Checked;
            originalCard.Margin = new Padding(0, 0, dual ? UiTheme.Px(this, 9) : 0, UiTheme.Px(this, 14));
            generatedCard.Margin = new Padding(dual ? UiTheme.Px(this, 9) : 0, 0, 0, UiTheme.Px(this, 14));
            int height = Math.Max(UiTheme.Px(this, 200), dual || !merge.Checked ? viewport.ClientSize.Height - UiTheme.Px(this, 124) : UiTheme.Px(this, 230));
            originalFrame.MinimumSize = generatedFrame.MinimumSize = new Size(0, UiTheme.Px(this, 180));
            originalFrame.Height = generatedFrame.Height = height;
            editors.ResumeLayout(true);
        }
        finally { layingOut = false; }
    }
    private void UpdateIcon(int dpi)
    {
        var next = AppIcon.Load(Math.Max(16, 32 * dpi / 96)); Icon = next; appIcon?.Dispose(); appIcon = next;
    }
    private static string WindowsLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    private void InvalidateOutput()
    {
        output.Clear(); copy.Enabled = save.Enabled = false; status.Text = "输入已变更，请重新生成。";
    }
    private void Import()
    {
        using var dialog = new OpenFileDialog { Title = "选择已有的 Clash 扩展脚本", Filter = "JavaScript / 文本脚本|*.js;*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > (ScriptComposer.MaxSourceLength + 32768L) * 4 + 4) throw new ArgumentException("文件过大，请选择原始扩展脚本。");
            using var reader = new StreamReader(dialog.FileName, new UTF8Encoding(false, true), true);
            source.Text = WindowsLines(ScriptComposer.ExtractOriginal(reader.ReadToEnd())); merge.Checked = true;
            status.Text = "已导入原脚本，可以生成并复制。";
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void PrepareOutput()
    {
        output.Text = WindowsLines(generate(merge.Checked ? source.Text : null));
        output.Select(0, 0); output.ScrollToCaret(); copy.Enabled = save.Enabled = true;
        status.Text = "完整脚本已生成，可复制或另存为。";
    }
    private void GenerateAndCopy()
    {
        try { PrepareOutput(); Copy(); } catch (Exception ex) { ShowError(ex); }
    }
    private void Copy()
    {
        if (output.TextLength == 0) return;
        try { Clipboard.SetText(output.Text); status.Text = "已复制 · 到 Clash 当前订阅替换并应用。"; }
        catch (Exception ex) { ShowError(new IOException("复制失败，预览仍保留；请重试“复制”。", ex)); }
    }
    private void SaveOutput()
    {
        if (output.TextLength == 0) return;
        using var dialog = new SaveFileDialog { Title = "保存完整扩展脚本", Filter = "JavaScript|*.js", FileName = "Host2VMRelay-clash.js", InitialDirectory = Settings.Folder };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, output.Text, new UTF8Encoding(false)); status.Text = "完整脚本已保存。"; } catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "脚本处理失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData == (Keys.Control | Keys.Enter)) { GenerateAndCopy(); return true; }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);

    internal void VerifyForTest()
    {
        merge.Checked = true;
        const string sample = "const label = '保留原脚本';\nfunction main(config, profileName) {\n  config.label = label;\n  return config;\n}";
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            source.Text = WindowsLines(sample.Replace("\n", newline)); PrepareOutput();
            if (ScriptComposer.ExtractOriginal(output.Text) != sample) throw new InvalidOperationException("Script preview changed the original source.");
            int nativeLines = SendMessageW(output.Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (nativeLines < output.Text.Count(c => c == '\n') || nativeLines < 20) throw new InvalidOperationException("Native preview collapsed hard line breaks.");
        }
        if (!output.Text.Contains("保留原脚本") || !output.Text.Contains("host2vm-relay-rules")) throw new InvalidOperationException("Script dialog lost user source.");
        source.AppendText("\r\n// updated");
        if (output.TextLength != 0 || copy.Enabled || save.Enabled) throw new InvalidOperationException("Stale script can be copied.");
        PrepareOutput(); source.Select(0, 0); source.ScrollToCaret(); UpdateWorkspaceLayout();
    }
}
