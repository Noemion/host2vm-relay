using System.Diagnostics;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Host2VMRelay;

/// <summary>
/// A short-lived viewport snapshot fades over the canvas. Native editors stay
/// intact underneath: no per-control opacity, relocation or layered HWND styles.
/// </summary>
internal sealed class PageFade : Control, IMessageFilter
{
    internal const int DurationMilliseconds = 120;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 15 };
    private readonly Stopwatch elapsed = new();
    private Bitmap? frame;
    private bool filtering;
    internal bool IsRunning => frame is not null;
    internal int PaintCount { get; private set; }

    public PageFade()
    {
        SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
            ControlStyles.OptimizedDoubleBuffer | ControlStyles.Opaque, true);
        SetStyle(ControlStyles.Selectable, false);
        Dock = DockStyle.Fill; TabStop = false; Visible = false;
        AccessibleRole = AccessibleRole.None;
        timer.Tick += (_, _) =>
        {
            if (elapsed.ElapsedMilliseconds >= DurationMilliseconds) Stop();
            else Invalidate();
        };
    }

    internal static bool AnimationAllowed => !SystemInformation.HighContrast &&
        SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out bool enabled, 0) && enabled;

    internal void Start(Control page)
    {
        Stop();
        if (!page.Visible || !page.IsHandleCreated || page.Width <= 0 || page.Height <= 0) return;
        // Hidden docked controls may not have been arranged yet. Size the cover
        // before assigning its frame so the first Show cannot cancel the fade.
        Bounds = page.Bounds;
        // Bound transient memory (32 MiB). Large viewports use normal painting.
        if ((long)page.Width * page.Height > 8 * 1024 * 1024) return;
        Bitmap? captured = null;
        try
        {
            var captureTime = Stopwatch.StartNew();
            captured = new Bitmap(page.Width, page.Height, PixelFormat.Format32bppRgb);
            using (var graphics = Graphics.FromImage(captured))
            {
                graphics.Clear(page.BackColor);
                var dc = graphics.GetHdc();
                try
                {
                    // Only our own UI-thread page is printed, never a foreign
                    // process. WM_PRINT includes native input child windows.
                    if (!PrintWindow(page.Handle, dc, 1 /* PW_CLIENTONLY */)) return;
                }
                finally { graphics.ReleaseHdc(dc); }
            }
            // Do not add an animation delay after an unusually expensive paint.
            if (captureTime.ElapsedMilliseconds > 50) return;
            frame = captured; captured = null;
            PaintCount = 0;
            BackColor = page.BackColor;
            elapsed.Restart();
            BringToFront(); Show();
            Application.AddMessageFilter(this); filtering = true;
            timer.Start(); Invalidate();
        }
        catch (Exception ex) when (ex is OutOfMemoryException or ExternalException or ArgumentException)
        {
            // Cosmetic failure must not prevent access to the selected page.
            Stop();
        }
        finally { captured?.Dispose(); }
    }

    internal void Stop()
    {
        timer.Stop(); elapsed.Reset();
        if (filtering) { Application.RemoveMessageFilter(this); filtering = false; }
        var previous = frame; frame = null;
        Hide(); previous?.Dispose();
        if (previous is not null && Parent is { IsDisposed: false } parent) parent.Invalidate(true);
    }

    public bool PreFilterMessage(ref Message message)
    {
        // Remove the cover before dispatching input; never swallow the user's
        // first click, scroll or keystroke while the fade is in progress.
        bool input = message.Msg is >= 0x100 and <= 0x109 or >= 0x201 and <= 0x20E;
        if (input && Control.FromChildHandle(message.HWnd) is { } target &&
            Parent is { } parent && (target == parent || parent.Contains(target))) Stop();
        return false;
    }

    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x84 /* WM_NCHITTEST */)
        {
            message.Result = new IntPtr(-1); // HTTRANSPARENT: hit-test the live page.
            return;
        }
        base.WndProc(ref message);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        if (frame is null) return;
        PaintCount++;
        float progress = Math.Clamp((float)elapsed.Elapsed.TotalMilliseconds / DurationMilliseconds, 0, 1);
        float opacity = 1 - MathF.Pow(1 - progress, 3); // Fast start, gentle finish.
        using var attributes = new ImageAttributes();
        attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
        e.Graphics.DrawImage(frame, ClientRectangle, 0, 0, frame.Width, frame.Height, GraphicsUnit.Pixel, attributes);
    }

    protected override void OnSizeChanged(EventArgs e) { Stop(); base.OnSizeChanged(e); }
    protected override void OnHandleDestroyed(EventArgs e) { Stop(); base.OnHandleDestroyed(e); }
    protected override void Dispose(bool disposing)
    {
        if (disposing) { Stop(); timer.Dispose(); }
        base.Dispose(disposing);
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PrintWindow(IntPtr window, IntPtr dc, uint flags);
    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint action, uint parameter,
        [MarshalAs(UnmanagedType.Bool)] out bool value, uint flags);
}
