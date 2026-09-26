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
        if (!first) { MessageBox.Show("应用已在运行，请从系统托盘打开。", "Host2VMRelay"); return; }
        string? smokeOutput = smoke ? Path.GetFullPath(args.Last()) : null;
        try
        {
            if (smoke)
            {
                string folder = Path.GetDirectoryName(smokeOutput!)!;
                // UI checks never read/migrate personal credentials or change the user's Clash rule file.
                Settings.Folder = Path.Combine(folder, "smoke-settings");
                ClashRuleFile.Folder = Path.Combine(folder, "smoke-rules");
            }
            using var form = new MainForm();
            if (smoke)
            {
                string? scaleArgument = args.FirstOrDefault(a => a.StartsWith("--ui-scale=", StringComparison.Ordinal));
                float scale = scaleArgument is null ? 1F : float.Parse(scaleArgument.Split('=')[1], CultureInfo.InvariantCulture) / 100F;
                if (scale is < 1F or > 3F) throw new ArgumentException("UI scale must be between 100 and 300.");
                form.Shown += async (_, _) =>
                {
                    try { await Task.Delay(300); form.CaptureTabs(smokeOutput!, scale); }
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
