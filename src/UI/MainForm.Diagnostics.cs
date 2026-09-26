namespace Host2VMRelay;

public sealed partial class MainForm
{
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    public void ExitForTest() { quitting = true; Close(); }

    public void CaptureTabs(string path, float layoutScale = 1F, bool requireNativeDpi = false)
    {
        var audit = new UiAcceptance(path, (int)Math.Round(layoutScale * 100), requireNativeDpi);
        try
        {
            if (!audit.CheckEnvironment(this)) return;
            audit.Check(AutoScaleMode == AutoScaleMode.Dpi && Font.Unit == GraphicsUnit.Point, "readable DPI-aware form");
            host.Name = "host"; user.Name = "user"; secret.Name = "secret"; keyPath.Name = "keyPath";
            ruleText.Name = "rules"; log.Name = "log"; port.Name = "sshPort"; socksPort.Name = "socksPort";
            wanted = true; SetConnectionControls(true); SetConnectionControls(false);
            audit.Check(connect.Enabled && host.Enabled, "manual reconnect controls recover after disconnection");
            audit.Check(auth.SelectedIndex != 0 || keyControls?.Enabled != true, "password mode disables private-key browse");
            wanted = false; SetConnectionControls(false);
            // Create each page at the actual startup DPI before testing notifications.
            for (int i = 0; i < tabs.TabCount; i++) { tabs.SelectedIndex = i; UiAcceptance.Settle(this); }
            tabs.SelectedIndex = 0;
            int startDpi = DeviceDpi;
            var fonts = UiAcceptance.FontBaseline(this);
            audit.ApplyDpi(this, audit.TargetDpi);
            audit.VerifyFontScaling(fonts, startDpi, audit.TargetDpi);
            audit.Check(windowIcon?.Width == 32 * audit.TargetDpi / 96, "window icon has the requested pixel size");
            audit.Check(trayIcon?.Width == 16 * audit.TargetDpi / 96, "tray icon has the requested pixel size");
            for (int i = 0; i < tabs.TabCount; i++)
            {
                tabs.SelectedIndex = i; UiAcceptance.Settle(this);
                audit.Inspect(this, "page-" + i);
            }
            using (var dialog = new ScriptDialog(GenerateScript, ""))
            {
                dialog.Show(this); dialog.VerifyForTest(); UiAcceptance.Settle(dialog);
                int dialogDpi = dialog.DeviceDpi;
                var dialogFonts = UiAcceptance.FontBaseline(dialog);
                audit.ApplyDpi(dialog, audit.TargetDpi);
                audit.VerifyFontScaling(dialogFonts, dialogDpi, audit.TargetDpi);
                audit.Inspect(dialog, "script-dialog");
                if (!requireNativeDpi)
                {
                    for (int round = 0; round < 3; round++)
                    {
                        audit.ApplyDpi(dialog, dialogDpi);
                        audit.VerifyFontScaling(dialogFonts, dialogDpi, dialogDpi);
                        audit.ApplyDpi(dialog, audit.TargetDpi);
                        audit.VerifyFontScaling(dialogFonts, dialogDpi, audit.TargetDpi);
                    }
                    audit.Inspect(dialog, "script-roundtrip");
                }
                dialog.Close();
            }
            if (!requireNativeDpi)
            {
                tabs.SelectedIndex = 0;
                for (int round = 0; round < 3; round++)
                {
                    audit.ApplyDpi(this, startDpi);
                    audit.VerifyFontScaling(fonts, startDpi, startDpi);
                    audit.ApplyDpi(this, audit.TargetDpi);
                    audit.VerifyFontScaling(fonts, startDpi, audit.TargetDpi);
                }
                audit.Inspect(this, "main-roundtrip");
            }
        }
        catch (Exception ex) { audit.Check(false, ex.ToString()); }
        finally { audit.Finish(path); }
    }
}
