using System.ComponentModel;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using Microsoft.Win32;

namespace Host2VMRelay;

/// <summary>Native menu interaction with a DPI-aware, Windows-theme palette.</summary>
internal sealed class TrayMenu : ContextMenuStrip
{
    private readonly bool? darkOverride;

    internal TrayMenu(bool? darkOverride = null)
    {
        this.darkOverride = darkOverride;
        ShowImageMargin = false;
        ShowCheckMargin = false;
        // The drop-down menu's native table layout reserves image/check columns
        // even for text-only entries. Stack rows to keep one shared content width.
        LayoutStyle = ToolStripLayoutStyle.VerticalStackWithOverflow;
        AutoSize = false;
    }

    protected override void OnOpening(CancelEventArgs e)
    {
        // Re-read on every opening so a theme change needs neither a restart nor
        // a global event subscription that can retain a disposed menu.
        bool dark = darkOverride ?? UsesDarkTheme();
        bool highContrast = SystemInformation.HighContrast;
        var palette = Palette.Create(dark);
        BackColor = highContrast ? SystemColors.Menu : palette.Background;
        ForeColor = highContrast ? SystemColors.MenuText : palette.Text;
        Renderer = highContrast ? new ToolStripSystemRenderer() : new MenuRenderer(palette);
        int Px(int value) => (int)Math.Round(value * DeviceDpi / 96d);
        Padding = new Padding(Px(4));
        int contentWidth = Items.OfType<ToolStripMenuItem>()
            .Select(item => TextRenderer.MeasureText(item.Text, item.Font).Width + Px(28)).DefaultIfEmpty(Px(28)).Max();
        foreach (ToolStripItem item in Items)
        {
            item.ForeColor = ForeColor;
            if (item is ToolStripSeparator)
            {
                item.Margin = new Padding(0, Px(3), 0, Px(3));
                item.AutoSize = false;
                item.Size = new Size(contentWidth, Px(1));
            }
            else
            {
                item.Padding = new Padding(Px(14), Px(5), Px(14), Px(5));
                item.Margin = Padding.Empty;
                item.AutoSize = false;
                item.Size = new Size(contentWidth, TextRenderer.MeasureText(item.Text, item.Font).Height + item.Padding.Vertical);
            }
        }
        Size = new Size(contentWidth + Padding.Horizontal,
            Items.Cast<ToolStripItem>().Sum(item => item.Height + item.Margin.Vertical) + Padding.Vertical);
        ApplyWindowTheme(dark && !highContrast);
        base.OnOpening(e);
    }

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);
        ApplyWindowTheme((darkOverride ?? UsesDarkTheme()) && !SystemInformation.HighContrast);
    }

    private void ApplyWindowTheme(bool dark)
    {
        if (!IsHandleCreated || !OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) return;
        // Windows 11 owns the outer corners and shadow. Do not clip the HWND
        // with a Region, which would interfere with the system shadow.
        int corners = 2, immersiveDark = dark ? 1 : 0;
        _ = DwmSetWindowAttribute(Handle, 33, ref corners, sizeof(int));
        _ = DwmSetWindowAttribute(Handle, 20, ref immersiveDark, sizeof(int));
    }

    private static bool UsesDarkTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return SystemColors.Menu.GetBrightness() < .5F;
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);

    internal bool ProcessKeyForTest(Keys key) => ProcessDialogKey(key);

    private readonly record struct Palette(Color Background, Color Text, Color Hover, Color Border, Color Disabled)
    {
        public static Palette Create(bool dark) => dark
            ? new(Color.FromArgb(41, 41, 41), Color.FromArgb(245, 245, 245), Color.FromArgb(62, 62, 62), Color.FromArgb(66, 66, 66), Color.FromArgb(145, 145, 145))
            : new(Color.FromArgb(250, 250, 250), Color.FromArgb(32, 35, 38), Color.FromArgb(232, 236, 234), Color.FromArgb(222, 225, 223), Color.FromArgb(135, 138, 140));
    }

    private sealed class MenuRenderer(Palette palette) : ToolStripProfessionalRenderer
    {
        protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
        {
            e.Graphics.Clear(palette.Background);
        }

        protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
        {
            if (!e.Item.Selected || !e.Item.Enabled) return;
            int dpi = e.ToolStrip?.DeviceDpi ?? 96;
            int inset = Math.Max(1, dpi / 96);
            var bounds = new Rectangle(inset, 0, e.Item.Width - 2 * inset, e.Item.Height - 1);
            using var path = Rounded(bounds, 5F * dpi / 96);
            using var brush = new SolidBrush(palette.Hover);
            var previous = e.Graphics.SmoothingMode;
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.FillPath(brush, path);
            e.Graphics.SmoothingMode = previous;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? palette.Text : palette.Disabled;
            e.TextRectangle = new Rectangle(e.Item.Padding.Left, 0,
                e.Item.Width - e.Item.Padding.Horizontal, e.Item.Height);
            e.TextFormat = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
        {
            int inset = 8 * (e.ToolStrip?.DeviceDpi ?? 96) / 96;
            using var pen = new Pen(palette.Border);
            e.Graphics.DrawLine(pen, inset, e.Item.Height / 2, e.Item.Width - inset, e.Item.Height / 2);
        }

        protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
        {
            using var pen = new Pen(palette.Border);
            e.Graphics.DrawRectangle(pen, 0, 0, e.ToolStrip.Width - 1, e.ToolStrip.Height - 1);
        }

        private static GraphicsPath Rounded(Rectangle bounds, float radius)
        {
            float diameter = Math.Min(radius * 2, Math.Min(bounds.Width, bounds.Height));
            var path = new GraphicsPath();
            path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
            path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
            path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
            path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
            path.CloseFigure();
            return path;
        }
    }
}
