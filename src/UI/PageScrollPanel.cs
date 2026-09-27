namespace Host2VMRelay;

/// <summary>
/// Uses native scrolling so managed and HWND-backed child controls share the same
/// clipping and repaint lifecycle. Do not manually offset the page content.
/// </summary>
internal sealed class PageScrollPanel : Panel
{
    private bool repaintPending;
    public PageScrollPanel(TableLayoutPanel content)
    {
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Canvas;
        AutoScroll = true;
        content.Dock = DockStyle.Top;
        Controls.Add(content);
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        QueueContentRepaint();
    }

    protected override void OnScroll(ScrollEventArgs se)
    {
        base.OnScroll(se);
        QueueContentRepaint();
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        QueueContentRepaint();
    }

    private void QueueContentRepaint()
    {
        if (repaintPending || !Visible || !IsHandleCreated || IsDisposed) return;
        repaintPending = true;
        // Native scrolling can reuse pixels while nested HWND controls and
        // owner-painted cards still have different invalid regions. Once the
        // visibility/scroll operation completes, invalidate the whole subtree,
        // including child windows. Coalesce rapid events and let WM_PAINT run
        // normally; synchronous Refresh/DoEvents here would re-enter layout.
        BeginInvoke(() =>
        {
            repaintPending = false;
            if (!IsDisposed && IsHandleCreated && Visible) Invalidate(true);
        });
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        repaintPending = false;
        base.OnHandleDestroyed(e);
    }

    internal void Reveal(Control control) { ScrollControlIntoView(control); QueueContentRepaint(); }
    internal void ResetScroll() { AutoScrollPosition = Point.Empty; QueueContentRepaint(); }
}
