namespace Host2VMRelay;

/// <summary>
/// Uses native scrolling so managed and HWND-backed child controls share the same
/// clipping and repaint lifecycle. Do not manually offset the page content.
/// </summary>
internal sealed class PageScrollPanel : Panel
{
    public PageScrollPanel(TableLayoutPanel content)
    {
        Dock = DockStyle.Fill;
        BackColor = UiTheme.Canvas;
        AutoScroll = true;
        content.Dock = DockStyle.Top;
        Controls.Add(content);
    }

    internal void Reveal(Control control) => ScrollControlIntoView(control);
    internal void ResetScroll() => AutoScrollPosition = Point.Empty;
}
