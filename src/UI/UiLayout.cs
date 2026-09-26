using System.Runtime.CompilerServices;

namespace Host2VMRelay;

internal static class UiLayout
{
    public const float BaseFontPoints = 12F * UiTheme.ContentScale;
    public static Font BodyFont() => new("Microsoft YaHei UI", BaseFontPoints, FontStyle.Regular, GraphicsUnit.Point);
    public static Font CodeFont() => new("Consolas", BaseFontPoints, FontStyle.Regular, GraphicsUnit.Point);
    private static readonly ConditionalWeakTable<Form, ExplicitFonts> fontTrackers = new();
    private sealed class ExplicitFonts
    {
        private readonly Form form;
        private readonly List<(Control Control, string Family, float Ratio, FontStyle Style)> specs = new();
        private readonly List<TextBox> singleLineInputs = new();
        private readonly Dictionary<Control, Font> owned = new();
        public ExplicitFonts(Form form)
        {
            this.form = form; Capture(form);
            form.Disposed += (_, _) => { foreach (var font in owned.Values) font.Dispose(); owned.Clear(); };
        }
        private void Capture(Control parent)
        {
            foreach (Control child in parent.Controls)
            {
                if (!child.Font.Equals(parent.Font)) specs.Add((child, child.Font.FontFamily.Name, child.Font.SizeInPoints / form.Font.SizeInPoints, child.Font.Style));
                if (child is TextBox { Multiline: false } input) singleLineInputs.Add(input);
                Capture(child);
            }
        }
        public void Apply()
        {
            foreach (var spec in specs)
            {
                if (spec.Control.IsDisposed) continue;
                float points = form.Font.SizeInPoints * spec.Ratio;
                if (Math.Abs(spec.Control.Font.SizeInPoints - points) < .01F) continue;
                var next = new Font(spec.Family, points, spec.Style, GraphicsUnit.Point);
                spec.Control.Font = next;
                if (owned.Remove(spec.Control, out var previous)) previous.Dispose();
                owned[spec.Control] = next;
            }
            foreach (var input in singleLineInputs)
            {
                if (input.IsDisposed) continue;
                int height = TextRenderer.MeasureText("Ag国", input.Font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height + 4;
                input.AutoSize = false; input.MinimumSize = new Size(0, height); input.Height = height;
            }
        }
    }
    public static ActionButton Button(string text, int minimumWidth = 100) => new() { Text = text, AccessibleName = text, MinimumSize = UiTheme.Size(minimumWidth, 42) };
    public static ActionButton Primary(string text, int minimumWidth = 140) { var button = Button(text, minimumWidth); button.Primary = true; return button; }
    public static Label Help(string text) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Top, ForeColor = UiTheme.Muted,
        MaximumSize = UiTheme.Size(760, 0), Margin = UiTheme.Spacing(0, 3, 0, 10)
    };
    public static Label Heading(string text, float points = 14)
    {
        var label = Help(text); label.ForeColor = UiTheme.Ink;
        label.Font = new Font("Microsoft YaHei UI", points * UiTheme.ContentScale, FontStyle.Bold, GraphicsUnit.Point);
        label.Margin = UiTheme.Spacing(0, 0, 0, 10); return label;
    }
    public static TableLayoutPanel Stack()
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 1, Margin = Padding.Empty };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); WrapLabels(table); return table;
    }
    public static void Add(TableLayoutPanel table, Control control)
    {
        int row = table.RowCount++; table.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        control.Dock = DockStyle.Top; table.Controls.Add(control, 0, row);
    }
    public static CardPanel Card(string title, string description, params Control[] controls)
    {
        var card = new CardPanel { AccessibleName = title, AccessibleRole = AccessibleRole.Grouping };
        Add(card, Heading(title)); if (description.Length > 0) Add(card, Help(description));
        foreach (var control in controls) Add(card, control);
        WrapLabels(card); return card;
    }
    public static TableLayoutPanel Field(string title, Control control, string? hint = null, bool frame = true)
    {
        var field = Stack(); field.Margin = UiTheme.Spacing(0, 4, 0, 14);
        var caption = Help(title); caption.ForeColor = UiTheme.Ink; caption.Margin = UiTheme.Spacing(0, 0, 0, 7);
        control.AccessibleName = title; Add(field, caption); Add(field, frame ? new EntryFrame(control) : control);
        if (!string.IsNullOrEmpty(hint)) Add(field, Help(hint)); return field;
    }
    public static TableLayoutPanel Pair(Control left, Control right, float leftPercent = 50)
    {
        var table = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, ColumnCount = 2, RowCount = 1, Margin = UiTheme.Spacing(0, 0, 0, 12) };
        table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, leftPercent)); table.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100 - leftPercent));
        table.RowStyles.Add(new RowStyle(SizeType.AutoSize)); left.Dock = right.Dock = DockStyle.Top;
        left.Margin = UiTheme.Spacing(0, 0, 8, 0); right.Margin = UiTheme.Spacing(8, 0, 0, 0);
        table.Controls.Add(left, 0, 0); table.Controls.Add(right, 1, 0); return table;
    }
    public static FlowLayoutPanel Actions(params Control[] controls)
    {
        var flow = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = true, Margin = UiTheme.Spacing(0, 6, 0, 0) };
        foreach (var control in controls) flow.Controls.Add(control); return flow;
    }
    public static EntryFrame Editor(TextBox editor, int height = 240, bool readOnly = false)
    {
        editor.Multiline = true; editor.ReadOnly = readOnly; editor.AcceptsReturn = true;
        editor.WordWrap = false; editor.ScrollBars = ScrollBars.Both; editor.Font = CodeFont();
        editor.MinimumSize = UiTheme.Size(0, 96); editor.HideSelection = false;
        return new EntryFrame(editor, true, height);
    }
    public static void WrapLabels(TableLayoutPanel table)
    {
        bool updating = false;
        table.Layout += (_, _) =>
        {
            if (updating) return; updating = true;
            try
            {
                int[] widths = table.GetColumnWidths();
                foreach (Control control in table.Controls)
                {
                    if (control is not Label label || !label.AutoSize || label.Dock != DockStyle.Top) continue;
                    int column = table.GetColumn(label); if (column < 0 || column >= widths.Length) continue;
                    int width = widths.Skip(column).Take(table.GetColumnSpan(label)).Sum() - label.Margin.Horizontal;
                    if (width > 30 && label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
                }
            }
            finally { updating = false; }
        };
    }
    public static void FitToScreen(Form form, Size logicalMinimum)
    {
        if (form.IsDisposed) return;
        fontTrackers.GetValue(form, f => new ExplicitFonts(f)).Apply();
        if (form.WindowState != FormWindowState.Normal) return;
        Rectangle area = Screen.FromHandle(form.Handle).WorkingArea;
        form.MinimumSize = new Size(Math.Min(UiTheme.Px(form, logicalMinimum.Width), area.Width), Math.Min(UiTheme.Px(form, logicalMinimum.Height), area.Height));
        int width = Math.Min(form.Width, area.Width), height = Math.Min(form.Height, area.Height);
        form.Bounds = new Rectangle(Math.Clamp(form.Left, area.Left, area.Right - width), Math.Clamp(form.Top, area.Top, area.Bottom - height), width, height);
    }
}
