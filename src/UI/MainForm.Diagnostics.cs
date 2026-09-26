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
            audit.Check(navigationButtons.Count == 4 && tabs.TabCount == 4, "four accessible navigation destinations");
            int originalAuth = auth.SelectedIndex;
            auth.SelectedIndex = 1; UiAcceptance.Settle(this);
            audit.Check(keyControls?.Enabled == true && keyPath.Visible, "private-key mode exposes the file picker");
            auth.SelectedIndex = 0; UiAcceptance.Settle(this);
            audit.Check(keyControls?.Enabled == false && !keyPath.Visible, "password mode hides the unused key field");
            auth.SelectedIndex = originalAuth;
            for (int i = 0; i < tabs.TabCount; i++)
            {
                navigationButtons[i].PerformClick(); UiAcceptance.Settle(this);
                audit.Check(tabs.SelectedIndex == i && navigationButtons.Count(b => b.Selected) == 1, "navigation selects exactly one page: " + i);
                audit.Check(pageTitle?.Text == PageTitles[i], "page heading follows navigation: " + i);
            }
            SelectPage(1);
            string originalRules = ruleText.Text, savedRules = settings.Rules;
            var ruleSummary = tabs.TabPages[1].Controls.Find("ruleSummary", true).OfType<Label>().Single();
            ruleText.Text = "# comment only\r\n";
            audit.Check(ruleSummary.Text.StartsWith("0 条规则"), "comment-only rules count as zero");
            ruleText.Text = "test.example\r\ntest.example\r\n# comment";
            audit.Check(ruleSummary.Text.StartsWith("1 条规则") && ruleSummary.Text.Contains("未保存"), "rule count uses normalized unique entries and marks unsaved changes");
            ruleText.Text = "https://invalid.example/path";
            audit.Check(ruleSummary.Text.Contains("格式待修正"), "invalid rule input gives inline feedback");
            audit.Check(settings.Rules == savedRules, "editing alone does not overwrite saved rules");
            ruleText.Text = originalRules;
            SelectPage(3);
            string previousLog = log.Text, previousHost = settings.Host;
            log.Text = "00:00:00  diagnostic entry\r\n";
            tabs.TabPages[3].Controls.Find("clearLog", true).OfType<Button>().Single().PerformClick();
            audit.Check(log.TextLength == 0 && settings.Host == previousHost && settings.Rules == savedRules, "clear log preserves connection and rule settings");
            log.Text = previousLog;
            SelectPage(0);
            int startDpi = DeviceDpi; var fonts = UiAcceptance.FontBaseline(this);
            audit.ApplyDpi(this, audit.TargetDpi); audit.VerifyFontScaling(fonts, startDpi, audit.TargetDpi);
            audit.Check(windowIcon?.Width == 32 * audit.TargetDpi / 96, "window icon has requested pixel size");
            audit.Check(trayIcon?.Width == 16 * audit.TargetDpi / 96, "tray icon has requested pixel size");
            bool compact = ClientSize.Width * 96.0 / DeviceDpi < 860;
            audit.Check(compactNavigation?.Visible == compact && sidebar?.Visible != compact, "navigation adapts without hiding destinations");
            for (int i = 0; i < tabs.TabCount; i++)
            {
                navigationButtons[i].PerformClick(); UiAcceptance.Settle(this); audit.Inspect(this, "page-" + i);
                if (i == 0)
                {
                    auth.SelectedIndex = 1; UiAcceptance.Settle(this); audit.Inspect(this, "page-0-private-key"); auth.SelectedIndex = originalAuth;
                }
                if (i == 2)
                {
                    var toggle = tabs.TabPages[i].Controls.Find("toggleTunGuide", true).OfType<Button>().Single();
                    var guide = tabs.TabPages[i].Controls.Find("tunInstructions", true).Single();
                    toggle.PerformClick(); UiAcceptance.Settle(this); audit.Check(guide.Visible, "TUN disclosure opens"); audit.Inspect(this, "page-2-expanded");
                    toggle.PerformClick(); audit.Check(!guide.Visible, "TUN disclosure closes");
                }
            }
            using (var dialog = new ScriptDialog(GenerateScript, ""))
            {
                dialog.Show(this); dialog.VerifyForTest(); UiAcceptance.Settle(dialog);
                int dialogDpi = dialog.DeviceDpi; var dialogFonts = UiAcceptance.FontBaseline(dialog);
                audit.ApplyDpi(dialog, audit.TargetDpi); audit.VerifyFontScaling(dialogFonts, dialogDpi, audit.TargetDpi);
                audit.Check(dialog.EditorViewportHeight >= UiTheme.Px(dialog, 90), "script viewport retains usable height in a compact window");
                audit.Inspect(dialog, "script-dialog");
                if (!requireNativeDpi)
                {
                    for (int round = 0; round < 3; round++)
                    {
                        audit.ApplyDpi(dialog, dialogDpi); audit.VerifyFontScaling(dialogFonts, dialogDpi, dialogDpi);
                        audit.ApplyDpi(dialog, audit.TargetDpi); audit.VerifyFontScaling(dialogFonts, dialogDpi, audit.TargetDpi);
                    }
                    audit.Check(dialog.EditorViewportHeight >= UiTheme.Px(dialog, 90), "script viewport survives DPI round trips");
                    audit.Inspect(dialog, "script-roundtrip");
                }
                dialog.Close();
            }
            if (!requireNativeDpi)
            {
                SelectPage(0);
                for (int round = 0; round < 3; round++)
                {
                    audit.ApplyDpi(this, startDpi); audit.VerifyFontScaling(fonts, startDpi, startDpi);
                    audit.ApplyDpi(this, audit.TargetDpi); audit.VerifyFontScaling(fonts, startDpi, audit.TargetDpi);
                }
                audit.Inspect(this, "main-roundtrip");
            }
        }
        catch (Exception ex) { audit.Check(false, ex.ToString()); }
        finally { audit.Finish(path); }
    }
}
