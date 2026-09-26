using System.Runtime.InteropServices;
using System.Text.Json;

namespace Host2VMRelay;

internal sealed class UiAcceptance
{
    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindowDpiAwarenessContext(IntPtr window);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AreDpiAwarenessContextsEqual(IntPtr first, IntPtr second);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessageW(IntPtr window, uint message, IntPtr wParam, ref NativeRect rect);
    private readonly string folder;
    private readonly List<object> stages = new();
    private readonly List<string> errors = new();
    private readonly List<string> checks = new();
    private readonly int percent;
    private readonly bool native;
    private string stage = "initialization";
    public int TargetDpi => percent * 96 / 100;
    public int AssertionCount { get; private set; }
    public bool Blocked { get; private set; }

    public UiAcceptance(string output, int percent, bool native)
    {
        folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        Directory.CreateDirectory(folder);
        this.percent = percent; this.native = native;
    }
    public void Check(bool condition, string message)
    {
        AssertionCount++;
        string item = stage + ": " + message;
        if (!condition) errors.Add(item); else checks.Add(item);
    }
    public bool CheckEnvironment(Form form)
    {
        int actual = checked((int)GetDpiForWindow(form.Handle));
        Check(AreDpiAwarenessContextsEqual(GetWindowDpiAwarenessContext(form.Handle), new IntPtr(-4)), "window is PerMonitorV2 aware");
        Check(actual > 0 && form.DeviceDpi == actual, "startup managed DPI matches the native window DPI");
        Check(form.Font.SizeInPoints >= UiLayout.BaseFontPoints - .1F, "readable base font at startup");
        stages.Add(new { Stage = stage, Method = "native-startup", NativeDpi = actual, ManagedDpi = form.DeviceDpi,
            Monitor = Screen.FromHandle(form.Handle).DeviceName, WorkArea = Screen.FromHandle(form.Handle).WorkingArea.ToString(),
            Windows = Environment.OSVersion.ToString(), Runtime = Environment.Version.ToString() });
        if (native && actual != TargetDpi)
        {
            Blocked = true;
            errors.Add($"Native acceptance requires {TargetDpi} DPI ({percent}%), but GetDpiForWindow returned {actual}. No system setting was changed and no synthetic message was sent.");
            return false;
        }
        return true;
    }
    // Parent-window message injection exercises WinForms scaling and our callbacks.
    // It does not change the OS DPI or reproduce the entire child-window notification sequence.
    public void ApplyDpi(Form form, int dpi)
    {
        if (native) { Check(GetDpiForWindow(form.Handle) == dpi, "native DPI matches requested DPI without injection"); return; }
        int previous = form.DeviceDpi;
        if (previous == dpi) return;
        int events = 0;
        DpiChangedEventHandler handler = (_, e) => { if (e.DeviceDpiNew == dpi) events++; };
        form.DpiChanged += handler;
        try
        {
            var rect = new NativeRect { Left = form.Left, Top = form.Top,
                Right = form.Left + (int)Math.Round(form.Width * (double)dpi / previous),
                Bottom = form.Top + (int)Math.Round(form.Height * (double)dpi / previous) };
            SendMessageW(form.Handle, 0x02E0, new IntPtr(dpi | (dpi << 16)), ref rect);
            Settle(form);
            Check(events == 1, $"DpiChanged delivered once: {previous} -> {dpi}");
            Check(form.DeviceDpi == dpi, $"WinForms DeviceDpi is {dpi} after notification");
        }
        finally { form.DpiChanged -= handler; }
    }
    public static Dictionary<Control, int> FontBaseline(Control root) => All(root).ToDictionary(c => c, GlyphHeight);
    private static int GlyphHeight(Control control) => TextRenderer.MeasureText("Ag国", control.Font, Size.Empty,
        TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
    public void VerifyFontScaling(Dictionary<Control, int> baseline, int startDpi, int targetDpi)
    {
        foreach (var pair in baseline)
        {
            Control control = pair.Key;
            if (control.IsDisposed || !(control is Form or Label or ButtonBase or TextBoxBase or ComboBox or NumericUpDown)) continue;
            double expected = pair.Value * (double)targetDpi / startDpi;
            int actual = GlyphHeight(control);
            Check(Math.Abs(actual - expected) <= Math.Max(3, expected * 0.12),
                $"font scales for {Identity(control)}: measured={actual}px, expected~{expected:F1}px");
        }
    }
    public void Inspect(Form form, string name)
    {
        stage = name;
        form.Activate(); form.BringToFront(); Settle(form);
        Rectangle area = Screen.FromHandle(form.Handle).WorkingArea;
        Check(area.Contains(form.Bounds), "window remains inside the monitor work area");
        Check(form.AutoScaleMode == AutoScaleMode.Dpi, "DPI automatic scaling remains enabled");
        Check(form.DeviceDpi == TargetDpi, "requested managed DPI is active");
        var visible = All(form).Where(c => c.Visible).ToArray();
        var measurements = new List<object>();
        foreach (var c in visible)
        {
            if (!IsLeaf(c)) continue;
            int glyph = GlyphHeight(c);
            measurements.Add(new { Control = Identity(c), Bounds = c.Bounds.ToString(), Client = c.ClientSize.ToString(),
                FontPoints = c.Font.SizeInPoints, GlyphHeight = glyph, Dpi = c.DeviceDpi, Enabled = c.Enabled });
            Check(c.Width > 0 && c.Height > 0, "nonempty bounds: " + Identity(c));
            if (native) Check(c.DeviceDpi == TargetDpi, "native child DPI is correct: " + Identity(c));
            if (c is Label || c is ButtonBase)
            {
                Size preferred = c.GetPreferredSize(c is Label ? new Size(Math.Max(1, c.Width), 0) : Size.Empty);
                Check(c.Height + 2 >= preferred.Height, $"text height fits {Identity(c)}: {c.Height} >= {preferred.Height}");
                Check(c.Width + 2 >= preferred.Width, $"text width fits {Identity(c)}: {c.Width} >= {preferred.Width}");
            }
            if (c is TextBoxBase editor)
            {
                int minimum = glyph + 2 + (editor is TextBox text && text.Multiline && text.ScrollBars is ScrollBars.Both or ScrollBars.Horizontal
                    ? SystemInformation.HorizontalScrollBarHeight : 0);
                Check(c.ClientSize.Height + 2 >= minimum, $"editor has a readable line: {Identity(c)} ({c.ClientSize.Height} >= {minimum})");
            }
        }
        foreach (var parent in visible.Where(c => !IsLeaf(c)))
        {
            var children = parent.Controls.Cast<Control>().Where(c => c.Visible).ToArray();
            for (int i = 0; i < children.Length; i++)
                for (int j = i + 1; j < children.Length; j++)
                {
                    var overlap = Rectangle.Intersect(children[i].Bounds, children[j].Bounds);
                    Check(overlap.Width <= 1 || overlap.Height <= 1,
                        "siblings do not overlap: " + Identity(children[i]) + " / " + Identity(children[j]));
                }
        }
        ResetScroll(form); Settle(form); Screenshot(form, name + "-top.png");
        foreach (var c in visible.Where(c => IsLeaf(c) && c is not Label))
        {
            Reveal(c);
            if (c.Enabled && c.CanSelect)
            {
                Check(c.Focus() || c.ContainsFocus, "keyboard focus reaches " + Identity(c));
                Settle(form);
            }
            Rectangle rect = c.RectangleToScreen(c.ClientRectangle);
            Rectangle clip = rect;
            for (Control? p = c.Parent; p is not null; p = p.Parent)
                clip = Rectangle.Intersect(clip, p.RectangleToScreen(p.ClientRectangle));
            if (c is ButtonBase)
                Check(clip.Width + 2 >= rect.Width && clip.Height + 2 >= rect.Height,
                    "whole action is reachable by scrolling: " + Identity(c));
            else
                Check(clip.Width >= Math.Min(80, rect.Width) && clip.Height >= Math.Min(GlyphHeight(c), rect.Height),
                    "input is reachable by scrolling: " + Identity(c));
        }
        Screenshot(form, name + "-scrolled.png");
        stages.Add(new { Stage = name, Method = native ? "native-monitor" : "injected-WM_DPICHANGED",
            ScreenshotMethod = "desktop-CopyFromScreen", NativeDpi = GetDpiForWindow(form.Handle), ManagedDpi = form.DeviceDpi,
            Window = form.Bounds.ToString(), WorkArea = area.ToString(), Controls = measurements });
    }
    private static void Reveal(Control control)
    {
        var parents = new List<ScrollableControl>();
        for (Control? p = control.Parent; p is not null; p = p.Parent)
            if (p is ScrollableControl { AutoScroll: true } scroll) parents.Add(scroll);
        for (int pass = 0; pass < 2; pass++)
            foreach (var scroll in parents) scroll.ScrollControlIntoView(control);
        Application.DoEvents();
    }
    private static bool IsLeaf(Control c) => c is Label or ButtonBase or TextBoxBase or ComboBox or NumericUpDown;
    private static IEnumerable<Control> All(Control root)
    {
        yield return root;
        if (IsLeaf(root)) yield break;
        foreach (Control child in root.Controls)
            foreach (Control descendant in All(child)) yield return descendant;
    }
    private static string Identity(Control c)
    {
        string text = c is TextBoxBase ? c.Name : c.Text.Replace("\r", "").Replace("\n", " ");
        return c.GetType().Name + ":" + (text.Length > 50 ? text[..50] : text);
    }
    private static void ResetScroll(Control root)
    {
        foreach (var c in All(root).OfType<ScrollableControl>())
            if (c.AutoScroll) c.AutoScrollPosition = Point.Empty;
    }
    public static void Settle(Control form)
    {
        for (int i = 0; i < 3; i++) { form.PerformLayout(); Application.DoEvents(); }
        form.Refresh();
    }
    private void Screenshot(Form form, string name)
    {
        form.Refresh(); Application.DoEvents(); Thread.Sleep(80);
        using var bitmap = new Bitmap(form.Width, form.Height);
        using (var graphics = Graphics.FromImage(bitmap))
            graphics.CopyFromScreen(form.Location, Point.Empty, form.Size, CopyPixelOperation.SourceCopy);
        var colors = new HashSet<int>();
        for (int y = 0; y < bitmap.Height; y += 13)
            for (int x = 0; x < bitmap.Width; x += 13) colors.Add(bitmap.GetPixel(x, y).ToArgb());
        Check(colors.Count > 8, "desktop screenshot contains a rendered window, not a blank capture");
        bitmap.Save(Path.Combine(folder, name));
    }
    public void Finish(string output)
    {
        string status = Blocked ? "BLOCKED" : errors.Count == 0 ? "PASS" : "FAIL";
        File.WriteAllText(Path.ChangeExtension(output, ".json"), JsonSerializer.Serialize(new {
            Status = status, Mode = native ? "native-monitor" : "message-injection", TargetPercent = percent,
            TargetDpi, Assertions = AssertionCount, Errors = errors, Checks = checks, Stages = stages,
            Limitations = native ? "Single-monitor automated geometry/focus checks; physical multi-monitor movement and visual sharpness need operator review."
                : "No Windows scale setting was changed. Parent DPI-message injection is not native monitor or child-window DPI acceptance."
        }, new JsonSerializerOptions { WriteIndented = true }));
        File.WriteAllText(Path.ChangeExtension(output, ".txt"), $"{status} DPI {percent}% ({TargetDpi}); mode={(native ? "native-monitor" : "message-injection")}; assertions={AssertionCount}; errors={errors.Count}\n" + string.Join("\n", errors));
        if (Blocked) Environment.ExitCode = 2; else if (errors.Count != 0) Environment.ExitCode = 1;
    }
}
