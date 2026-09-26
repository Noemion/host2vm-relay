namespace Host2VMRelay;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) { SelfTest.Run(args.Last()); return; }
        using var mutex = new Mutex(true, "Local\\Host2VMRelay.Desktop", out bool first);
        if (!first) { MessageBox.Show("应用已在运行，请从系统托盘打开。", "Host2VMRelay"); return; }
        try
        {
            if (args.Contains("--smoke")) Settings.Folder = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(args.Last()))!, "smoke-settings");
            using var form = new MainForm();
            if (args.Contains("--smoke"))
            {
                form.Shown += async (_, _) => {
                    await Task.Delay(500);
                    form.CaptureTabs(args.Last());
                    form.ExitForTest();
                };
            }
            Application.Run(form);
        }
        catch (Exception ex) { MessageBox.Show(ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error); }
    }
}
