using System.Runtime.InteropServices;
using System.Text;

namespace Host2VMRelay;

internal sealed class ScriptDialog : Form
{
    private readonly Func<string?, string> generate;
    private readonly TextBox source = new() { Name = "originalScript", AccessibleName = "原始脚本" }, output = new() { Name = "generatedScript", AccessibleName = "完整脚本预览" };
    private readonly ActionButton paste = UiLayout.Button("粘贴当前脚本", 150), import = UiLayout.Button("导入脚本…", 130), copy = UiLayout.Button("复制", 70), save = UiLayout.Button("另存为", 90);
    private readonly ActionButton compose = UiLayout.Primary("生成并复制", 160);
    private readonly Label status = UiLayout.Help("可直接生成，也可粘贴或导入已有脚本后生成。");
    private readonly TableLayoutPanel viewport = new() { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, Margin = UiTheme.Spacing(0, 8, 0, 8) };
    private readonly Panel editorHost = new() { Name = "scriptEditor", Dock = DockStyle.Fill, Margin = Padding.Empty };
    private readonly ActionButton originalTab = UiLayout.Button("原始脚本", 110), generatedTab = UiLayout.Button("生成结果（只读）", 170);
    private readonly EntryFrame originalFrame, generatedFrame;
    private readonly Panel workspaceScroll = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = Padding.Empty };
    private TableLayoutPanel? chrome;
    private Icon? appIcon;
    private bool layingOut;
    private bool workspaceLayoutReady;
    private bool showingGenerated;
    private readonly bool embedded;
    public string OriginalScript => source.Text;
    internal int EditorViewportHeight => editorHost.ClientSize.Height;

    public event EventHandler? BackRequested;
    public ScriptDialog(Func<string?, string> generate, string existingScript, bool embedded = false)
    {
        this.generate = generate; this.embedded = embedded; SuspendLayout(); DoubleBuffered = true;
        Font = UiLayout.BodyFont(); ForeColor = UiTheme.Ink; BackColor = UiTheme.Canvas;
        AutoScaleDimensions = new SizeF(96F, 96F); AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Host2VMRelay — 脚本工作区"; ClientSize = UiTheme.Size(1080, 780); MinimumSize = UiTheme.Size(680, 520);
        StartPosition = FormStartPosition.CenterParent; UpdateIcon(96);
        if (embedded) { TopLevel = false; FormBorderStyle = FormBorderStyle.None; MinimumSize = Size.Empty; Dock = DockStyle.Fill; }
        var root = new TableLayoutPanel { Dock = DockStyle.Top, ColumnCount = 1, RowCount = 6, Padding = UiTheme.Spacing(22), Margin = Padding.Empty };
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
        root.Controls.Add(header, 0, 0);
        root.Controls.Add(UiLayout.Actions(paste, import), 0, 1);
        source.MaxLength = ScriptComposer.MaxGeneratedLength; source.AcceptsTab = true;
        originalFrame = UiLayout.Editor(source, 260); generatedFrame = UiLayout.Editor(output, 260, true);
        // A borderless host lets EntryFrame draw all four rounded corners. Do
        // not keep the standalone editor's minimum height: it clips in short
        // embedded workspaces and at high DPI. Each document keeps its undo,
        // selection and scroll state when the other one is displayed.
        originalFrame.Dock = generatedFrame.Dock = DockStyle.Fill;
        originalFrame.MinimumSize = generatedFrame.MinimumSize = source.MinimumSize = output.MinimumSize = Size.Empty;
        editorHost.Controls.Add(originalFrame); editorHost.Controls.Add(generatedFrame);
        originalTab.Name = "originalScriptTab"; generatedTab.Name = "generatedScriptTab";
        originalTab.MinimumSize = UiTheme.Size(110, 34); generatedTab.MinimumSize = UiTheme.Size(170, 34);
        originalTab.Click += (_, _) => SelectEditor(false);
        generatedTab.Click += (_, _) => SelectEditor(true);
        var tabRow = UiLayout.Actions(originalTab, generatedTab); tabRow.Margin = Padding.Empty;
        viewport.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        viewport.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        viewport.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        viewport.Controls.Add(tabRow, 0, 0); viewport.Controls.Add(editorHost, 0, 1);
        root.Controls.Add(viewport, 0, 2); SelectEditor(false);
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
        workspaceScroll.Controls.Add(root); Controls.Add(workspaceScroll);
        // Empty input generates a fresh script; existing input is inspected and
        // merged automatically, without a separate mode that can discard it.
        source.Text = WindowsLines(existingScript);
        copy.Enabled = save.Enabled = false;
        source.TextChanged += (_, _) => InvalidateOutput(); paste.Click += (_, _) => PasteScript(); import.Click += (_, _) => Import(); compose.Click += (_, _) => GenerateAndCopy();
        copy.Click += (_, _) => Copy(); save.Click += (_, _) => SaveOutput();
        workspaceScroll.ClientSizeChanged += (_, _) => UpdateWorkspaceLayout();
        chrome.Layout += (_, _) => UpdateWorkspaceLayout();
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
            chrome.Padding = new Padding(UiTheme.Px(this, compact ? 12 : 22));
            viewport.Margin = new Padding(0, UiTheme.Px(this, compact ? 4 : 8), 0, UiTheme.Px(this, compact ? 4 : 8));
            // Ask the parent to remeasure wrapped text after compact padding or
            // DPI changes; otherwise the previous row height can be cached.
            chrome.PerformLayout(status, nameof(status.Font));
            // Keep an editable viewport even when text and actions consume most
            // of a small/high-DPI window. Overflow scrolls the workspace instead
            // of squeezing the editor to zero or overlapping the action rows.
            int fixedRows = chrome.GetRowHeights().Where((_, row) => row != 2).Sum();
            int tabsHeight = viewport.GetRowHeights()[0];
            int minimumHeight = chrome.Padding.Vertical + fixedRows + tabsHeight +
                viewport.Margin.Vertical + UiTheme.Px(this, 90);
            chrome.Height = Math.Max(workspaceScroll.ClientSize.Height, minimumHeight);
        }
        finally { layingOut = false; }
    }
    private void SelectEditor(bool generated)
    {
        showingGenerated = generated;
        originalFrame.Visible = !generated; generatedFrame.Visible = generated;
        originalTab.Emphasized = !generated; generatedTab.Emphasized = generated;
        originalTab.AccessibleDescription = generated ? "切换到可编辑的原始脚本" : "当前显示原始脚本，可编辑";
        generatedTab.AccessibleDescription = generated ? "当前显示生成结果，只读" : "切换到只读的生成结果";
        originalTab.Invalidate(); generatedTab.Invalidate();
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
        source.Text = WindowsLines(imported.Original);
        SelectEditor(false);
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
        string input = source.Text;
        var imported = ScriptComposer.Inspect(input);
        output.Text = WindowsLines(generate(input));
        SelectEditor(true);
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
        if (keyData == (Keys.Control | Keys.Tab) || keyData == (Keys.Control | Keys.Shift | Keys.Tab))
        {
            SelectEditor(!showingGenerated); (showingGenerated ? output : source).Focus(); return true;
        }
        if (keyData == (Keys.Control | Keys.Enter)) { GenerateAndCopy(); return true; } return base.ProcessCmdKey(ref msg, keyData);
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, IntPtr lParam);
    internal void VerifyForTest()
    {
        if (!source.Enabled || source.ReadOnly || !source.ShortcutsEnabled)
            throw new InvalidOperationException("Script workspace must accept pasted input on first open.");
        source.Clear(); PrepareOutput();
        if (output.TextLength == 0) throw new InvalidOperationException("Empty input must generate a fresh script.");
        const string fragment = "function enable(proxy) { proxy.udp = true; }\nfor (const proxy of config.proxies ?? []) { enable(proxy); }";
        LoadOriginalScript(fragment); PrepareOutput();
        if (ScriptComposer.ExtractOriginal(output.Text) != fragment)
            throw new InvalidOperationException("Automatic composition lost a fragment or its helper function.");
        LoadOriginalScript(output.Text);
        if (source.Text != WindowsLines(fragment) || showingGenerated)
            throw new InvalidOperationException("Importing a generated script must restore its editable user source.");
        const string sample = "const label = '保留原脚本';\nfunction main(config, profileName) {\n  config.label = label;\n  return config;\n}";
        foreach (string newline in new[] { "\n", "\r\n", "\r" })
        {
            source.Text = WindowsLines(sample.Replace("\n", newline)); PrepareOutput();
            if (!showingGenerated) throw new InvalidOperationException("Generated script tab was not selected.");
            if (ScriptComposer.ExtractOriginal(output.Text) != sample) throw new InvalidOperationException("Script preview changed the original source.");
            int nativeLines = SendMessageW(output.Handle, 0x00BA, IntPtr.Zero, IntPtr.Zero).ToInt32();
            if (nativeLines < output.Text.Count(c => c == '\n') || nativeLines < 20) throw new InvalidOperationException("Native preview collapsed hard line breaks.");
        }
        if (!output.Text.Contains("保留原脚本") || !output.Text.Contains("host2vm-relay-rules")) throw new InvalidOperationException("Script dialog lost user source.");
        originalTab.PerformClick();
        if (source.Text != WindowsLines(sample) || source.ReadOnly || !output.ReadOnly)
            throw new InvalidOperationException("Switching script tabs changed source or edit permissions.");
        source.AppendText("\r\n// updated");
        if (output.TextLength != 0 || copy.Enabled || save.Enabled) throw new InvalidOperationException("Stale script can be copied.");
        PrepareOutput(); source.Select(0, 0); source.ScrollToCaret(); UpdateWorkspaceLayout();
    }
    internal void VerifyLayoutForTest(UiAcceptance audit)
    {
        foreach (bool generated in new[] { false, true })
        {
            SelectEditor(generated); UiAcceptance.Settle(this);
            var frame = generated ? generatedFrame : originalFrame;
            audit.Check(frame.Bounds == editorHost.ClientRectangle, "script editor fits its host without clipping the rounded bottom border");
            audit.Check(viewport.ClientRectangle.Contains(editorHost.Bounds), "script editor stays inside the flexible workspace row");
            audit.Check(editorHost.Height >= UiTheme.Px(this, 90), "both script documents retain usable editor height");
        }
        originalTab.PerformClick(); UiAcceptance.Settle(this);
        audit.Check(originalFrame.Visible && !generatedFrame.Visible && source.Enabled && !source.ReadOnly,
            "original script selector restores editable input");
    }
}
