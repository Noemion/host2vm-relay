namespace Host2VMRelay;

public sealed partial class MainForm
{
    private void Restore()
    {
        Show(); WindowState = FormWindowState.Normal; Activate();
        UiLayout.FitToScreen(this, new Size(680, 520)); UpdateShellLayout();
        Invalidate(true);
    }
    public void ExitForTest() { quitting = true; Close(); }

    internal void CheckStartup(string output)
    {
        int painted = 0, sample = 0;
        var samples = new List<object>();
        var failures = new List<string>();
        PageScrollPanel? scrollPage = null;
        Point scrollOrigin = Point.Empty;
        var observer = new System.Windows.Forms.Timer { Interval = 150 };
        Load += (_, _) => Observe("load-before-show");
        Shown += (_, _) => observer.Start();
        observer.Tick += (_, _) =>
        {
            Observe("idle-" + (++sample));
            if (sample < 4) return;
            if (sample == 4)
            {
                SelectPage(4);
                scrollPage = tabs.TabPages[4].Controls.OfType<PageScrollPanel>().Single();
                scrollPage.ResetScroll();
                scrollOrigin = scrollPage.Controls[0].Location;
                if (!scrollPage.AutoScroll || !scrollPage.VerticalScroll.Visible)
                    failures.Add("Settings page must expose native vertical scrolling");
                scrollPage.AutoScrollPosition = new Point(0, 300);
                return;
            }
            if (sample == 5)
            {
                if (scrollPage!.AutoScrollPosition.Y >= 0)
                    failures.Add("Settings page did not scroll");
                if (scrollPage.Controls[0].Top != scrollOrigin.Y + scrollPage.AutoScrollPosition.Y)
                    failures.Add("Page content does not follow native scroll position");
                scrollPage.ResetScroll();
                return;
            }
            if (scrollPage!.AutoScrollPosition != Point.Empty || scrollPage.Controls[0].Location != scrollOrigin)
                failures.Add("Page geometry changed after scrolling back to the top");
            observer.Stop(); observer.Dispose();
            if (painted == 0) failures.Add("No natural navigation paint observed");
            File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { Status = failures.Count == 0 ? "PASS" : "FAIL", Dpi = DeviceDpi, NaturalNavigationPaints = painted, Samples = samples, Errors = failures }, new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (failures.Count > 0) Environment.ExitCode = 1;
            ExitForTest();
        };
        void Observe(string stage)
        {
            painted = navigationButtons.Sum(b => b.PaintCount);
            bool compact = navigation?.Parent == compactNavigation;
            int expected = compact ? 0 : UiTheme.Px(this, 204);
            float actual = shellBody!.ColumnStyles[0].Width;
            if (Math.Abs(actual - expected) > 1) failures.Add(stage + ": sidebar was scaled twice");
            if (navigationButtons.Any(b => b.Height < TextRenderer.MeasureText(b.Text, b.Font).Height)) failures.Add(stage + ": navigation text is clipped");
            samples.Add(new { Stage = stage, SidebarWidth = actual, ExpectedSidebarWidth = expected, Client = ClientSize.ToString(), NavigationPaints = painted });
        }
    }

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
            audit.Check(navigationButtons.Count == 5 && tabs.TabCount == 5, "five accessible navigation destinations");
            audit.Check(Math.Abs(UiLayout.BaseFontPoints - (float)settings.FontSizePoints) < .01F && Math.Abs(UiTheme.ContentScale - .8F * settings.UiScalePercent / 100F) < .01F, "saved content scale and independent font size are applied");
            SelectPage(4); UiAcceptance.Settle(this);
            var folderField = tabs.TabPages[4].Controls.Find("settingsFolder", true).OfType<TextBox>().Single();
            audit.Check(folderField.ReadOnly && folderField.Text == Settings.Folder, "settings page displays the active configuration path");
            SelectPage(0);
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
            bool compact = ClientSize.Width * 96.0 / DeviceDpi < Math.Max(UiTheme.Units(860), 688 * UiTheme.FontPoints / 9.6F);
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
