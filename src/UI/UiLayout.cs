namespace Host2VMRelay;

internal static class UiLayout
{
    public static Font BodyFont() => new("Microsoft YaHei UI", 12F, FontStyle.Regular, GraphicsUnit.Point);
    public static Font CodeFont() => new("Consolas", 12F, FontStyle.Regular, GraphicsUnit.Point);

    public static Button Button(string text, int minimumWidth = 110) => new()
    {
        Text = text, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
        MinimumSize = new Size(minimumWidth, 40), Padding = new Padding(10, 5, 10, 5),
        Margin = new Padding(0, 4, 10, 4)
    };

    public static Label Help(string text) => new()
    {
        Text = text, AutoSize = true, Dock = DockStyle.Top,
        MaximumSize = new Size(680, 0), Margin = new Padding(0, 4, 0, 8)
    };

    // Label widths follow their table columns, including after DPI and window-size changes.
    public static void WrapLabels(TableLayoutPanel table)
    {
        bool updating = false;
        table.Layout += (_, _) =>
        {
            if (updating) return;
            updating = true;
            try
            {
                int[] widths = table.GetColumnWidths();
                foreach (Control control in table.Controls)
                {
                    if (control is not Label label || !label.AutoSize || label.Dock != DockStyle.Top) continue;
                    int column = table.GetColumn(label);
                    if (column < 0 || column >= widths.Length) continue;
                    int width = widths.Skip(column).Take(table.GetColumnSpan(label)).Sum() - label.Margin.Horizontal;
                    if (width > 30 && label.MaximumSize.Width != width) label.MaximumSize = new Size(width, 0);
                }
            }
            finally { updating = false; }
        };
    }

    public static void FitToScreen(Form form, Size logicalMinimum)
    {
        if (form.IsDisposed || form.WindowState != FormWindowState.Normal) return;
        Rectangle area = Screen.FromHandle(form.Handle).WorkingArea;
        int dpi = form.DeviceDpi;
        form.MinimumSize = new Size(Math.Min(logicalMinimum.Width * dpi / 96, area.Width),
            Math.Min(logicalMinimum.Height * dpi / 96, area.Height));
        int width = Math.Min(form.Width, area.Width), height = Math.Min(form.Height, area.Height);
        form.Bounds = new Rectangle(Math.Clamp(form.Left, area.Left, area.Right - width),
            Math.Clamp(form.Top, area.Top, area.Bottom - height), width, height);
    }
}
