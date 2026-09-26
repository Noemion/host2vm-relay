using System.Globalization;

namespace Host2VMRelay;

internal static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();
        if (args.Contains("--self-test")) { SelfTest.Run(args.Last()); return; }
        bool smoke = args.Contains("--smoke");
        using var mutex = new Mutex(true, smoke ? "Local\\Host2VMRelay.Smoke" : "Local\\Host2VMRelay.Desktop", out bool first);
        if (!first) { if (smoke) Environment.ExitCode = 1; else MessageBox.Show("应用已在运行，请从系统托盘打开。", "Host2VMRelay"); return; }
        string? smokeOutput = smoke ? Path.GetFullPath(args.Last()) : null;
        try
        {
            if (smoke)
            {
                string folder = Path.GetDirectoryName(smokeOutput!)!;
                Directory.CreateDirectory(folder);
                Settings.Folder = Path.Combine(folder, "smoke-settings");
                ClashRuleFile.Folder = Path.Combine(folder, "smoke-rules");
            }
            if (!smoke) Settings.InitializeLocation();
            using var form = new MainForm();
            if (smoke)
            {
                string? scaleArgument = args.FirstOrDefault(a => a.StartsWith("--ui-scale=", StringComparison.Ordinal));
                int percent = scaleArgument is null ? 100 : int.Parse(scaleArgument.Split('=')[1], CultureInfo.InvariantCulture);
                if (percent is not (100 or 125 or 150 or 175 or 200)) throw new ArgumentException("Acceptance scale must be 100, 125, 150, 175 or 200.");
                bool native = args.Contains("--native-dpi");
                string? monitorArgument = args.FirstOrDefault(a => a.StartsWith("--monitor=", StringComparison.Ordinal));
                if (monitorArgument is not null)
                {
                    int index = int.Parse(monitorArgument.Split('=')[1], CultureInfo.InvariantCulture);
                    var monitors = Screen.AllScreens;
                    if (index < 0 || index >= monitors.Length) throw new ArgumentException("Monitor index is out of range.");
                    Rectangle area = monitors[index].WorkingArea;
                    form.StartPosition = FormStartPosition.Manual;
                    form.Location = new Point(area.Left + 16, area.Top + 16);
                }
                form.Shown += async (_, _) =>
                {
                    try { await Task.Delay(300); form.CaptureTabs(smokeOutput!, percent / 100F, native); }
                    catch (Exception ex) { File.WriteAllText(Path.ChangeExtension(smokeOutput!, ".txt"), "FAIL " + ex); Environment.ExitCode = 1; }
                    finally { form.ExitForTest(); }
                };
            }
            Application.Run(form);
        }
        catch (Exception ex)
        {
            if (smoke) { File.WriteAllText(Path.ChangeExtension(smokeOutput!, ".txt"), "FAIL " + ex); Environment.ExitCode = 1; }
            else MessageBox.Show(ex.Message, "启动失败", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
    }
}
