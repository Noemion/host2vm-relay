using System.Drawing.Drawing2D;

namespace Host2VMRelay;

internal static class UiTheme
{
    public const float ContentScale = 0.8F;
    public const float SpacingScale = 0.6F;
    public static int Units(int logical) => (int)Math.Round(logical * ContentScale, MidpointRounding.AwayFromZero);
    public static System.Drawing.Size Size(int width, int height) => new(Units(width), Units(height));
    public static Padding Spacing(params int[] values)
    {
        int S(int n) => (int)Math.Round(n * SpacingScale, MidpointRounding.AwayFromZero);
        return values.Length switch
        {
            1 => new Padding(S(values[0])),
            4 => new Padding(S(values[0]), S(values[1]), S(values[2]), S(values[3])),
            _ => throw new ArgumentException("Spacing needs one or four values.")
        };
    }
    public static Color Canvas => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(246, 247, 249);
    public static Color Sidebar => SystemInformation.HighContrast ? SystemColors.Control : Color.FromArgb(236, 239, 241);
    public static Color Surface => SystemInformation.HighContrast ? SystemColors.Window : Color.White;
    public static Color Field => SystemInformation.HighContrast ? SystemColors.Window : Color.FromArgb(247, 248, 250);
    public static Color Ink => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(31, 38, 43);
    public static Color Muted => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(91, 101, 110);
    public static Color Line => SystemInformation.HighContrast ? SystemColors.WindowText : Color.FromArgb(222, 227, 230);
    public static Color Accent => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(24, 105, 86);
    public static Color Selection => SystemInformation.HighContrast ? SystemColors.Highlight : Color.FromArgb(216, 231, 225);
    public static int Px(Control control, int logical) => (int)Math.Round(logical * ContentScale * (control.FindForm()?.DeviceDpi ?? control.DeviceDpi) / 96.0);
    public static GraphicsPath Round(RectangleF rectangle, float radius)
    {
        var path = new GraphicsPath();
        float diameter = Math.Min(radius * 2, Math.Min(rectangle.Width, rectangle.Height));
        if (diameter <= 0) { path.AddRectangle(rectangle); return path; }
        path.AddArc(rectangle.Left, rectangle.Top, diameter, diameter, 180, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Top, diameter, diameter, 270, 90);
        path.AddArc(rectangle.Right - diameter, rectangle.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(rectangle.Left, rectangle.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure(); return path;
    }
}

internal class CardPanel : TableLayoutPanel
{
    public CardPanel()
    {
        DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink; Dock = DockStyle.Top;
        ColumnCount = 1; ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        Padding = UiTheme.Spacing(22); Margin = UiTheme.Spacing(0, 0, 0, 18);
        BackColor = UiTheme.Surface; ForeColor = UiTheme.Ink;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Canvas); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.Round(new RectangleF(.5F, .5F, Math.Max(0, Width - 1F), Math.Max(0, Height - 1F)), UiTheme.Px(this, 14));
        using var fill = new SolidBrush(BackColor); using var border = new Pen(UiTheme.Line);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape);
    }
}

internal sealed class EntryFrame : TableLayoutPanel
{
    private readonly Control editor;
    public EntryFrame(Control editor, bool multiline = false, int editorHeight = 240)
    {
        this.editor = editor; DoubleBuffered = true; SetStyle(ControlStyles.ResizeRedraw, true);
        ColumnCount = 1; RowCount = 1; ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        RowStyles.Add(new RowStyle(multiline ? SizeType.Percent : SizeType.AutoSize, 100));
        Dock = DockStyle.Top; Margin = Padding.Empty; Padding = UiTheme.Spacing(12, 10, 12, 10);
        BackColor = UiTheme.Field; AutoSize = !multiline;
        if (multiline) { Height = UiTheme.Units(editorHeight); MinimumSize = UiTheme.Size(0, editorHeight); }
        else { AutoSizeMode = AutoSizeMode.GrowAndShrink; MinimumSize = UiTheme.Size(0, 44); }
        editor.Margin = Padding.Empty; editor.BackColor = UiTheme.Field; editor.ForeColor = UiTheme.Ink;
        if (editor is TextBox text) text.BorderStyle = BorderStyle.None;
        if (editor is NumericUpDown number) number.BorderStyle = BorderStyle.None;
        if (editor is ComboBox combo) combo.FlatStyle = FlatStyle.Flat;
        editor.Dock = multiline ? DockStyle.Fill : DockStyle.Top; Controls.Add(editor, 0, 0);
        editor.Enter += (_, _) => Invalidate(); editor.Leave += (_, _) => Invalidate(); editor.EnabledChanged += (_, _) => Invalidate();
        AccessibleRole = AccessibleRole.Grouping;
    }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Surface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        float inset = Math.Max(1, UiTheme.Px(this, 1));
        using var shape = UiTheme.Round(new RectangleF(inset, inset, Math.Max(0, Width - 2 * inset - 1), Math.Max(0, Height - 2 * inset - 1)), UiTheme.Px(this, 8));
        using var fill = new SolidBrush(BackColor);
        using var border = new Pen(editor.ContainsFocus ? UiTheme.Accent : UiTheme.Line, editor.ContainsFocus ? inset * 1.5F : 1);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape);
    }
}

internal class ActionButton : Button
{
    protected bool Hot { get; private set; }
    private bool pressed, primary;
    public bool Primary { get => primary; set { primary = value; Invalidate(); } }
    public ActionButton()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
        FlatStyle = FlatStyle.Flat; FlatAppearance.BorderSize = 0;
        AutoSize = true; AutoSizeMode = AutoSizeMode.GrowAndShrink;
        MinimumSize = UiTheme.Size(100, 42); Padding = UiTheme.Spacing(16, 8, 16, 8); Margin = UiTheme.Spacing(0, 4, 10, 4); Cursor = Cursors.Hand;
        UseVisualStyleBackColor = false; BackColor = UiTheme.Surface; ForeColor = UiTheme.Ink;
    }
    public override Size GetPreferredSize(Size proposedSize)
    {
        Size text = TextRenderer.MeasureText(Text, Font, Size.Empty, TextFormatFlags.SingleLine);
        return new Size(Math.Max(MinimumSize.Width, text.Width + Padding.Horizontal + 2), Math.Max(MinimumSize.Height, text.Height + Padding.Vertical + 2));
    }
    protected override void OnMouseEnter(EventArgs e) { Hot = true; Invalidate(); base.OnMouseEnter(e); }
    protected override void OnMouseLeave(EventArgs e) { Hot = pressed = false; Invalidate(); base.OnMouseLeave(e); }
    protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) pressed = true; Invalidate(); base.OnMouseDown(e); }
    protected override void OnMouseUp(MouseEventArgs e) { pressed = false; Invalidate(); base.OnMouseUp(e); }
    protected override void OnKeyDown(KeyEventArgs e) { if (e.KeyCode == Keys.Space) pressed = true; Invalidate(); base.OnKeyDown(e); }
    protected override void OnKeyUp(KeyEventArgs e) { pressed = false; Invalidate(); base.OnKeyUp(e); }
    protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
    protected override void OnLostFocus(EventArgs e) { pressed = false; Invalidate(); base.OnLostFocus(e); }
    protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }
    protected virtual Color FillColor => !Enabled ? UiTheme.Field : Primary ? (pressed ? Color.FromArgb(15, 73, 60) : Hot ? Color.FromArgb(31, 119, 99) : UiTheme.Accent) : pressed ? UiTheme.Selection : Hot ? UiTheme.Field : UiTheme.Surface;
    protected virtual Color TextColor => !Enabled ? SystemColors.GrayText : Primary ? Color.White : UiTheme.Ink;
    protected virtual Color BorderColor => Primary && Enabled ? FillColor : UiTheme.Line;
    protected override void OnPaint(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaint(e); return; }
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Surface); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.Round(new RectangleF(1, 1, Math.Max(0, Width - 3), Math.Max(0, Height - 3)), UiTheme.Px(this, 9));
        using var fill = new SolidBrush(FillColor); using var border = new Pen(BorderColor);
        e.Graphics.FillPath(fill, shape); e.Graphics.DrawPath(border, shape); PaintCaption(e.Graphics);
        if (Focused && ShowFocusCues)
        {
            Rectangle focus = ClientRectangle; focus.Inflate(-UiTheme.Px(this, 5), -UiTheme.Px(this, 5));
            ControlPaint.DrawFocusRectangle(e.Graphics, focus, TextColor, FillColor);
        }
    }
    protected virtual void PaintCaption(Graphics graphics) => TextRenderer.DrawText(graphics, Text, Font, ClientRectangle, TextColor,
        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | (ShowKeyboardCues ? 0 : TextFormatFlags.HidePrefix));
}

internal sealed class NavigationButton : ActionButton
{
    private bool selected;
    public int PageIndex { get; init; }
    public bool Compact { get; set; }
    public bool Selected
    {
        get => selected;
        set
        {
            selected = value; AccessibleDescription = value ? "当前页面" : "切换页面";
            if (SystemInformation.HighContrast) { BackColor = value ? SystemColors.Highlight : SystemColors.Control; ForeColor = value ? SystemColors.HighlightText : SystemColors.ControlText; }
            Invalidate();
        }
    }
    public NavigationButton() { MinimumSize = UiTheme.Size(150, 46); Padding = UiTheme.Spacing(38, 10, 14, 10); }
    protected override Color FillColor => Selected ? UiTheme.Selection : Hot ? UiTheme.Field : UiTheme.Sidebar;
    protected override Color BorderColor => FillColor;
    protected override Color TextColor => Selected ? UiTheme.Accent : UiTheme.Muted;
    protected override void PaintCaption(Graphics graphics)
    {
        int icon = UiTheme.Px(this, 17), left = UiTheme.Px(this, 14), gap = UiTheme.Px(this, 10);
        var area = new Rectangle(left + icon + gap, 0, Width - left - icon - gap - UiTheme.Px(this, 8), Height);
        TextRenderer.DrawText(graphics, Text, Font, area, TextColor, TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine);
        using var pen = new Pen(TextColor, Math.Max(1.3F, UiTheme.Px(this, 1))) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round };
        float x = left, y = (Height - icon) / 2F, s = icon;
        if (PageIndex == 0)
        {
            graphics.DrawRectangle(pen, x, y, s, s * .66F); graphics.DrawLine(pen, x + s * .5F, y + s * .66F, x + s * .5F, y + s);
            graphics.DrawLine(pen, x + s * .2F, y + s, x + s * .8F, y + s);
        }
        else if (PageIndex == 1)
        {
            for (int i = 0; i < 3; i++) { float row = y + (i + .3F) * s / 3; graphics.DrawEllipse(pen, x, row, s * .08F, s * .08F); graphics.DrawLine(pen, x + s * .3F, row + s * .04F, x + s, row + s * .04F); }
        }
        else if (PageIndex == 2)
        {
            graphics.DrawLine(pen, x, y + s * .3F, x + s, y + s * .3F); graphics.DrawLine(pen, x + s * .7F, y, x + s, y + s * .3F);
            graphics.DrawLine(pen, x, y + s * .7F, x + s, y + s * .7F); graphics.DrawLine(pen, x, y + s * .7F, x + s * .3F, y + s);
        }
        else if (PageIndex == 3)
        {
            graphics.DrawRectangle(pen, x, y, s, s); graphics.DrawLine(pen, x + s * .2F, y + s * .3F, x + s * .4F, y + s * .5F);
            graphics.DrawLine(pen, x + s * .4F, y + s * .5F, x + s * .2F, y + s * .7F); graphics.DrawLine(pen, x + s * .55F, y + s * .7F, x + s * .8F, y + s * .7F);
        }
        else
        {
            graphics.DrawEllipse(pen, x + s * .2F, y + s * .2F, s * .6F, s * .6F);
            graphics.DrawEllipse(pen, x + s * .4F, y + s * .4F, s * .2F, s * .2F);
            for (int i = 0; i < 8; i++)
            {
                double a = i * Math.PI / 4;
                graphics.DrawLine(pen, x + s * (.5F + .3F * (float)Math.Cos(a)), y + s * (.5F + .3F * (float)Math.Sin(a)),
                    x + s * (.5F + .48F * (float)Math.Cos(a)), y + s * (.5F + .48F * (float)Math.Sin(a)));
            }
        }
    }
}

internal sealed class StatusBadge : Label
{
    public StatusBadge() { AutoSize = true; Padding = UiTheme.Spacing(12, 6, 12, 6); Margin = Padding.Empty; }
    protected override void OnPaintBackground(PaintEventArgs e)
    {
        if (SystemInformation.HighContrast) { base.OnPaintBackground(e); return; }
        e.Graphics.Clear(Parent?.BackColor ?? UiTheme.Canvas); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
        using var shape = UiTheme.Round(new RectangleF(0, 0, Math.Max(0, Width - 1), Math.Max(0, Height - 1)), UiTheme.Px(this, 16));
        using var fill = new SolidBrush(Color.FromArgb(235, 240, 237)); e.Graphics.FillPath(fill, shape);
    }
}

internal sealed class WorkspaceTabs : TabControl
{
    public WorkspaceTabs() { Dock = DockStyle.Fill; Margin = System.Windows.Forms.Padding.Empty; Padding = Point.Empty; TabStop = false; }
    protected override void WndProc(ref Message m)
    {
        if (m.Msg == 0x1328 && !DesignMode) { m.Result = IntPtr.Zero; return; }
        base.WndProc(ref m);
    }
}
