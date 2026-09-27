namespace Host2VMRelay;

/// <summary>
/// Owns the persistent application pages. Navigation lives in the shell, so a
/// native tab window (and its hidden header/display-rectangle bookkeeping) is
/// unnecessary. Switching visibility preserves each page's input and scroll.
/// </summary>
internal sealed class PageHost : Panel
{
    private readonly List<Panel> pages = new();
    private int selectedIndex = -1;
    public IReadOnlyList<Panel> Pages => pages;
    public int PageCount => pages.Count;
    public event EventHandler? SelectedIndexChanged;

    public PageHost()
    {
        Dock = DockStyle.Fill; Margin = Padding.Empty;
        BackColor = UiTheme.Canvas; TabStop = false;
    }

    public void AddPage(Panel page)
    {
        page.Dock = DockStyle.Fill; page.Visible = false;
        pages.Add(page); Controls.Add(page);
        if (selectedIndex < 0) SelectedIndex = 0;
    }

    public int SelectedIndex
    {
        get => selectedIndex;
        set
        {
            if (value < 0 || value >= pages.Count) throw new ArgumentOutOfRangeException(nameof(value));
            if (value == selectedIndex) return;
            SuspendLayout();
            try
            {
                if (selectedIndex >= 0) pages[selectedIndex].Hide();
                selectedIndex = value;
                pages[value].Show(); pages[value].BringToFront();
            }
            finally { ResumeLayout(true); }
            SelectedIndexChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
