using System.Text;

namespace Host2VMRelay;

internal sealed class ScriptDialog : Form
{
    private readonly Func<string?, string> generate;
    private readonly CheckBox merge = new() { Text = "合并现有 Clash 扩展脚本（可选）", AutoSize = true };
    private readonly TextBox source = new(), output = new();
    private readonly Button import = UiLayout.Button("导入 .js / .txt"), copy = UiLayout.Button("复制结果"), save = UiLayout.Button("另存为 .js");
    private readonly Label status = UiLayout.Help("不提供原脚本时直接生成；原脚本只在本次运行中保留，不会被本程序执行。");
    private readonly Icon appIcon;
    public string OriginalScript => merge.Checked ? source.Text : "";

    public ScriptDialog(Func<string?, string> generate, string existingScript)
    {
        this.generate = generate;
        SuspendLayout();
        Font = UiLayout.BodyFont();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Host2VMRelay — 生成 Clash 扩展脚本";
        ClientSize = new Size(940, 740); MinimumSize = new Size(680, 520);
        StartPosition = FormStartPosition.CenterParent; AutoScroll = true;
        appIcon = AppIcon.Load(32); Icon = appIcon;
        var layout = new TableLayoutPanel { Dock = DockStyle.Top, Height = 740, MinimumSize = new Size(0, 620), Padding = new Padding(18), ColumnCount = 1, RowCount = 8 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        for (int i = 0; i < 8; i++) layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles[3] = new RowStyle(SizeType.Percent, 42);
        layout.RowStyles[5] = new RowStyle(SizeType.Percent, 58);
        layout.Controls.Add(UiLayout.Help("先执行原脚本，再接入 Host2VMRelay。请提供 JavaScript 扩展脚本，不是 YAML 订阅配置；JavaScript 语法与运行结果仍由 Clash 校验。"), 0, 0);
        var tools = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
        merge.Margin = new Padding(0, 12, 15, 4); tools.Controls.Add(merge); tools.Controls.Add(import);
        layout.Controls.Add(tools, 0, 1);
        layout.Controls.Add(UiLayout.Help("原始扩展脚本：支持 function main(...)、const main = (...) => ... 及其辅助函数"), 0, 2);
        ConfigureEditor(source, false); source.MaxLength = ScriptComposer.MaxSourceLength + 32768;
        source.Text = existingScript; layout.Controls.Add(source, 0, 3);
        layout.Controls.Add(UiLayout.Help("完整脚本预览（整体替换 Clash 当前订阅的扩展脚本）"), 0, 4);
        ConfigureEditor(output, true); layout.Controls.Add(output, 0, 5);
        var buttons = new FlowLayoutPanel { AutoSize = true, Dock = DockStyle.Top, WrapContents = true, Margin = Padding.Empty };
        var compose = UiLayout.Button("生成完整脚本并复制", 220);
        var close = UiLayout.Button("关闭"); close.DialogResult = DialogResult.Cancel; CancelButton = close;
        foreach (var button in new[] { compose, copy, save, close }) buttons.Controls.Add(button);
        layout.Controls.Add(buttons, 0, 6); layout.Controls.Add(status, 0, 7);
        UiLayout.WrapLabels(layout); Controls.Add(layout);
        SizeChanged += (_, _) => layout.Height = Math.Max(ClientSize.Height, layout.MinimumSize.Height);
        merge.Checked = !string.IsNullOrWhiteSpace(existingScript); source.Enabled = merge.Checked;
        copy.Enabled = save.Enabled = false;
        merge.CheckedChanged += (_, _) => { source.Enabled = merge.Checked; InvalidateOutput(); };
        source.TextChanged += (_, _) => InvalidateOutput();
        import.Click += (_, _) => Import();
        compose.Click += (_, _) => GenerateAndCopy();
        copy.Click += (_, _) => Copy();
        save.Click += (_, _) => SaveOutput();
        Shown += (_, _) => UiLayout.FitToScreen(this, new Size(680, 520));
        DpiChanged += (_, _) => BeginInvoke(() => UiLayout.FitToScreen(this, new Size(680, 520)));
        FormClosed += (_, _) => appIcon.Dispose();
        ResumeLayout(true);
    }

    private static void ConfigureEditor(TextBox editor, bool readOnly)
    {
        editor.Multiline = true; editor.AcceptsReturn = true; editor.AcceptsTab = !readOnly;
        editor.ReadOnly = readOnly; editor.WordWrap = false; editor.ScrollBars = ScrollBars.Both;
        editor.Dock = DockStyle.Fill; editor.Font = UiLayout.CodeFont(); editor.HideSelection = false;
    }

    private void InvalidateOutput()
    {
        output.Clear(); copy.Enabled = save.Enabled = false;
        status.Text = "输入已变更，请重新生成完整脚本。";
    }

    private void Import()
    {
        using var dialog = new OpenFileDialog { Title = "选择已有的 Clash 扩展脚本", Filter = "JavaScript / 文本脚本|*.js;*.txt|所有文件|*.*" };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > (ScriptComposer.MaxSourceLength + 32768L) * 4 + 4)
                throw new ArgumentException("文件过大，请选择原始扩展脚本。");
            using var reader = new StreamReader(dialog.FileName, new UTF8Encoding(false, true), true);
            string text = reader.ReadToEnd();
            source.Text = ScriptComposer.ExtractOriginal(text); merge.Checked = true;
            status.Text = "已导入原脚本。点击“生成完整脚本并复制”完成合并。";
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void GenerateAndCopy()
    {
        try
        {
            output.Text = generate(merge.Checked ? source.Text : null);
            copy.Enabled = save.Enabled = true;
            Copy();
        }
        catch (Exception ex) { ShowError(ex); }
    }

    private void Copy()
    {
        if (output.TextLength == 0) return;
        try
        {
            Clipboard.SetText(output.Text);
            status.Text = "完整脚本已复制。请到 Clash 当前订阅 → 编辑扩展脚本，整体替换、保存并重新应用。";
        }
        catch (Exception ex) { ShowError(new IOException("复制失败，预览仍保留；请重试“复制结果”。", ex)); }
    }

    private void SaveOutput()
    {
        if (output.TextLength == 0) return;
        using var dialog = new SaveFileDialog { Title = "保存完整扩展脚本", Filter = "JavaScript|*.js", FileName = "Host2VMRelay-clash.js", InitialDirectory = Settings.Folder };
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        try { File.WriteAllText(dialog.FileName, output.Text, new UTF8Encoding(false)); status.Text = "完整脚本已保存。"; }
        catch (Exception ex) { ShowError(ex); }
    }

    private void ShowError(Exception ex) => MessageBox.Show(this, ex.Message, "脚本处理失败", MessageBoxButtons.OK, MessageBoxIcon.Warning);

    internal void VerifyForTest()
    {
        merge.Checked = true;
        source.Text = "const label = '保留原脚本'; function main(config, profileName) { config.label = label; return config; }";
        output.Text = generate(source.Text);
        if (!output.Text.Contains("保留原脚本") || !output.Text.Contains("host2vm-relay-rules")) throw new InvalidOperationException("Script dialog lost user source.");
        copy.Enabled = save.Enabled = true;
        source.AppendText("\n// updated");
        if (output.TextLength != 0 || copy.Enabled || save.Enabled) throw new InvalidOperationException("Stale script can be copied.");
        output.Text = generate(source.Text); copy.Enabled = save.Enabled = true;
    }
}
