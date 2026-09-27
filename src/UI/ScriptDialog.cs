using System.Runtime.InteropServices;
using System.Text;

namespace Host2VMRelay;

internal sealed class ScriptDialog : Form
{
    private readonly Func<string?, string> generate;
    private readonly CheckBox merge = new() { Text = "合并 / 更新旧脚本", AutoSize = true };
    private readonly TextBox source = new() { Name = "originalScript", AccessibleName = "原始脚本" }, output = new() { Name = "generatedScript", AccessibleName = "完整脚本预览" };
    private readonly ActionButton paste = UiLayout.Button("粘贴当前脚本", 150), import = UiLayout.Button("导入脚本…", 130), copy = UiLayout.Button("复制", 70), save = UiLayout.Button("另存为", 90);
    private readonly ActionButton compose = UiLayout.Primary("生成并复制", 160);
    private readonly Label status = UiLayout.Help("先生成完整脚本，再复制或保存。"), introduction = UiLayout.Help("支持完整 main 函数或直接操作 config 的代码片段；旧版生成脚本可增量更新。");
    private readonly Panel viewport = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = UiTheme.Spacing(0, 12, 0, 12) };
    private readonly TabControl scriptTabs = new() { Name = "scriptTabs", Dock = DockStyle.Fill };
    private readonly TabPage originalTab = new("原始脚本"), generatedTab = new("生成结果（只读）");
    private readonly EntryFrame originalFrame, generatedFrame;
    private TableLayoutPanel? chrome;
    private Icon? appIcon;
    private bool layingOut;
    private bool workspaceLayoutReady;
    private readonly bool embedded;
    public string OriginalScript => merge.Checked ? source.Text : "";
    internal int EditorViewportHeight => viewport.ClientSize.Height;

    public event EventHandler? BackRequested;
    public ScriptDialog(Func<string?, string> generate, string existingScript, bool embedded = false)
    {
        this.generate = generate; this.embedded = embedded; SuspendLayout(); DoubleBuffered = true;
        Font = UiLayout.BodyFont(); ForeColor = UiTheme.Ink; BackColor = UiTheme.Canvas;
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Host2VMRelay — 脚本工作区"; ClientSize = UiTheme.Size(1080, 780); MinimumSize = UiTheme.Size(680, 520);
        StartPosition = FormStartPosition.CenterParent; UpdateIcon(96);
        if (embedded) { TopLevel = false; FormBorderStyle = FormBorderStyle.None; MinimumSize = Size.Empty; Dock = DockStyle.Fill; }
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 6, Padding = UiTheme.Spacing(22), Margin = Padding.Empty };
        chrome = root; root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 6; i++) root.RowStyles.Add(new RowStyle(i == 2 ? SizeType.Percent : SizeType.AutoSize, i == 2 ? 100 : 0));
        var header = UiLayout.Stack();
        if (embedded)
        {
            var back = UiLayout.Button("← 返回 Clash 接入", 220);
            back.MinimumSize = UiTheme.Size(220, 52); back.Emphasized = true;
            back.Font = new Font(Font, FontStyle.Bold);
            back.Name = "backToClash"; back.AccessibleDescription = "返回上一级，保留当前脚本。快捷键 Alt+左方向键。";
            back.Click += (_, _) => BackRequested?.Invoke(this, EventArgs.Empty);
            var navigationRow = UiLayout.Actions(back);
            navigationRow.Margin = UiTheme.Spacing(0, 0, 0, 22);
            UiLayout.Add(header, navigationRow);
        }
        UiLayout.Add(header, UiLayout.Heading("脚本工作区", embedded ? 16 : 18));
        UiLayout.Add(header, introduction); root.Controls.Add(header, 0, 0);
        merge.Margin = UiTheme.Spacing(0, 12, 22, 6); root.Controls.Add(UiLayout.Actions(merge, paste, import), 0, 1);
        source.MaxLength = ScriptComposer.MaxGeneratedLength; source.AcceptsTab = true;
        originalFrame = UiLayout.Editor(source, 260); generatedFrame = UiLayout.Editor(output, 260, true);
        // Both tabs occupy one editor area. Separate text controls preserve each
        // document's selection, scroll position and undo history while switching.
        originalFrame.Dock = generatedFrame.Dock = DockStyle.Fill;
        originalTab.Controls.Add(originalFrame); generatedTab.Controls.Add(generatedFrame);
        scriptTabs.TabPages.AddRange([originalTab, generatedTab]);
        viewport.AutoScroll = false;
        viewport.Controls.Add(scriptTabs); root.Controls.Add(viewport, 0, 2);
        compose.Name = "composeScript";
        var actions = UiLayout.Actions(compose, copy, save);
        if (!embedded)
        {
            var close = UiLayout.Button("关闭", 70); close.DialogResult = DialogResult.Cancel; CancelButton = close;
            actions.Controls.Add(close);
        }
        root.Controls.Add(actions, 0, 3);
        status.Margin = UiTheme.Spacing(0, 8, 0, 0); root.Controls.Add(status, 0, 4);
        root.Controls.Add(UiLayout.Help("使脚本生效：" + ClashActivationGuide.Steps), 0, 5);
        UiLayout.WrapLabels(root); Controls.Add(root);
        // This workspace is opened by “合并已有脚本”; keep its input discoverable
        // even on the first visit, before any script has been entered.
        source.Text = WindowsLines(existingScript); merge.Checked = true;
        source.Enabled = merge.Checked; copy.Enabled = save.Enabled = false;
        merge.CheckedChanged += (_, _) => { source.Enabled = merge.Checked; InvalidateOutput(); UpdateWorkspaceLayout(); };
        source.TextChanged += (_, _) => InvalidateOutput(); paste.Click += (_, _) => PasteScript(); import.Click += (_, _) => Import(); compose.Click += (_, _) => GenerateAndCopy();
        copy.Click += (_, _) => Copy(); save.Click += (_, _) => SaveOutput(); viewport.SizeChanged += (_, _) => UpdateWorkspaceLayout();
        Load += (_, _) => { workspaceLayoutReady = true; UpdateIcon(DeviceDpi); if (!embedded) UiLayout.FitToScreen(this, new Size(680, 520)); UpdateWorkspaceLayout(); };
        DpiChanged += (_, e) =>
        {
            workspaceLayoutReady = false; UpdateIcon(e.DeviceDpiNew);
            BeginInvoke(() =>
            {
                if (IsDisposed) return;
                workspaceLayoutReady = true;
                if (!embedded) UiLayout.FitToScreen(this, new Size(680, 520));
                UpdateWorkspaceLayout();
            });
        };
        FormClosed += (_, _) => appIcon?.Dispose(); UpdateWorkspaceLayout(); ResumeLayout(true);
    }
    private void UpdateWorkspaceLayout()
    {
        if (!workspaceLayoutReady || layingOut || chrome is null) return;
        layingOut = true;
        try
        {
            bool compact = ClientSize.Width * 96.0 / Math.Max(96, DeviceDpi) < UiTheme.Units(860) || ClientSize.Height * 96.0 / Math.Max(96, DeviceDpi) < UiTheme.Units(560);
            introduction.Visible = !compact; chrome.Padding = new Padding(UiTheme.Px(this, compact ? 12 : 22));
            viewport.Margin = new Padding(0, UiTheme.Px(this, compact ? 6 : 12), 0, UiTheme.Px(this, compact ? 6 : 12));
            status.Margin = new Padding(0, UiTheme.Px(this, compact ? 4 : 8), 0, 0);

        }
        finally { layingOut = false; }
    }
    private void UpdateIcon(int dpi) { var next = AppIcon.Load(Math.Max(16, 32 * dpi / 96)); Icon = next; appIcon?.Dispose(); appIcon = next; }
    private static string WindowsLines(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n').Replace("\n", "\r\n");
    private void InvalidateOutput() { copy.ClearFeedback(); compose.ClearFeedback(); output.Clear(); copy.Enabled = save.Enabled = false; status.Text = "输入已变更，请重新生成。"; }
    private void PasteScript()
    {
        try
        {
            if (!Clipboard.ContainsText()) { status.Text = "剪贴板中没有文本，请先复制当前扩展脚本。"; return; }
            LoadOriginalScript(Clipboard.GetText());
            source.Focus();
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void LoadOriginalScript(string text)
    {
        // Validate before replacing the editor, preserving existing work if the
        // clipboard contains a damaged managed script or an oversized payload.
        var imported = ScriptComposer.Inspect(text);
        source.Text = WindowsLines(imported.Original); merge.Checked = true;
        scriptTabs.SelectedTab = originalTab;
        status.Text = imported.Kind == "original" ? "原脚本已载入，可编辑后生成。" : "已识别旧版完整脚本，保留用户区并准备更新托管区。";
    }
    private void Import()
    {
        using var dialog = new OpenFileDialog { Title = "选择已有的 Clash 扩展脚本", Filter = "JavaScript / 文本脚本|*.js;*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) { status.Text = "已取消操作，脚本内容保持不变。"; return; }
        try
        {
            if (new FileInfo(dialog.FileName).Length > (long)ScriptComposer.MaxGeneratedLength * 4 + 4) throw new ArgumentException("文件过大，请选择原始扩展脚本。");
            using var reader = new StreamReader(dialog.FileName, new UTF8Encoding(false, true), true);
            LoadOriginalScript(reader.ReadToEnd());
        }
        catch (Exception ex) { ShowError(ex); }
    }
    private void PrepareOutput()
    {
        string? input = merge.Checked ? source.Text : null;
        var imported = ScriptComposer.Inspect(input);
        output.Text = WindowsLines(generate(input));
        scriptTabs.SelectedTab = generatedTab;
        output.Select(0, 0); output.ScrollToCaret();
        copy.Enabled = save.Enabled = true;
        status.Text = imported.Kind == "original" ? "完整脚本已生成，可复制或另存为。" : "增量更新完成：用户区已保留，托管区已替换。";
    }
    private void GenerateAndCopy()
    {
        try
        {
            UseWaitCursor = true; status.Text = "正在生成完整脚本…"; status.Refresh();
            PrepareOutput(); if (Copy()) compose.ShowFeedback("✓ 已生成并复制");
        }
        catch (Exception ex) { ShowError(ex); }
        finally { UseWaitCursor = false; }
    }
    private bool Copy()
    {
        if (output.TextLength == 0) return false;
        try
        {
            Clipboard.SetText(output.Text);
            status.Text = ClashActivationGuide.Copied;
            copy.ShowFeedback("✓ 已复制");
            return true;
        }
        catch (Exception ex) { ShowError(new IOException("复制失败，预览仍保留；请重试“复制”。", ex)); return false; }
    }
    private void SaveOutput()
    {
        if (output.TextLength == 0) return;
        using var dialog = new SaveFileDialog { Title = "保存完整扩展脚本", Filter = "JavaScript|*.js", FileName = "Host2VMRelay-clash.js", InitialDirectory = Settings.Folder };
        if (dialog.ShowDialog(this) != DialogResult.OK) { status.Text = "已取消操作，脚本内容保持不变。"; return; }
        try { File.WriteAllText(dialog.FileName, output.Text, new UTF8Encoding(false)); status.Text = "已保存文件，尚未应用到 Clash：" + dialog.FileName; save.ShowFeedback("✓ 已保存"); } catch (Exception ex) { ShowError(ex); }
    }
    private void ShowError(Exception ex)
    {
        status.Text = "操作失败：" + ex.Message;
    }
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (embedded && keyData == (Keys.Alt | Keys.Left)) { BackRequested?.Invoke(this, EventArgs.Empty); return true; }
        if (keyData == (Keys.Control | Keys.Enter)) { GenerateAndCopy(); return true; } return base.ProcessCmdKey(ref msg, keyData);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal void VerifyForTest()
    {
        if (!merge.Checked || !source.Enabled || source.ReadOnly || !source.ShortcutsEnabled)
            throw new InvalidOperationException("Script workspace must accept pasted input on first open.");
        const string sample = "const label = '保留原脚本';\nfunction main(config, profileName) {\n  config.label = label;\n  return config;\n}";
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            source.Text = WindowsLines(sample.Replace("\n", newline)); PrepareOutput();
            if (scriptTabs.SelectedTab != generatedTab) throw new InvalidOperationException("Generated script tab was not selected.");
            if (ScriptComposer.ExtractOriginal(output.Text) != sample) throw new InvalidOperationException("Script preview changed the original source.");
            int nativeLines = SendMessageW(output.Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (nativeLines < output.Text.Count(c => c == '\n') || nativeLines < 20) throw new InvalidOperationException("Native preview collapsed hard line breaks.");
        }
        if (!output.Text.Contains("保留原脚本") || !output.Text.Contains("host2vm-relay-rules")) throw new InvalidOperationException("Script dialog lost user source.");
        scriptTabs.SelectedTab = originalTab;
        if (source.Text != WindowsLines(sample) || source.ReadOnly || !output.ReadOnly)
            throw new InvalidOperationException("Switching script tabs changed source or edit permissions.");
        source.AppendText("\r\n// updated");
        if (output.TextLength != 0 || copy.Enabled || save.Enabled) throw new InvalidOperationException("Stale script can be copied.");
        PrepareOutput(); source.Select(0, 0); source.ScrollToCaret(); UpdateWorkspaceLayout();
    }
}
