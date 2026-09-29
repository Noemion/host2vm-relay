namespace Host2VMRelay;

public sealed partial class MainForm
{
    internal static void RunScheduleSelfTest(Action<bool, string> check)
    {
        string oldSettings = Settings.Folder, oldRules = ClashRuleFile.Folder;
        string root = Path.Combine(Path.GetTempPath(), "h2vm-calendar-ui-" + Guid.NewGuid().ToString("N"));
        Exception? failure = null;
        try
        {
            Settings.Folder = Path.Combine(root, "settings");
            ClashRuleFile.Folder = Path.Combine(root, "rules");
            using var form = new MainForm(startHidden: true);
            _ = form.Handle;
            form.BeginInvoke(async () =>
            {
                form.timer.Stop();
                try
                {
                    form.VerifyScheduleForTest(check);
                    await form.VerifyCalendarForTestAsync(check);
                }
                catch (Exception ex) { failure = ex; }
                finally { form.ExitForTest(); }
            });
            Application.Run(form);
            if (failure is not null) throw new IOException("Schedule UI state checks failed", failure);
        }
        finally
        {
            Settings.Folder = oldSettings; ClashRuleFile.Folder = oldRules;
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    private async Task VerifyCalendarForTestAsync(Action<bool, string> check)
    {
        var now = new DateTime(2026, 9, 30, 9, 30, 0);
        scheduleClock = () => now;
        settings.CalendarUpdate = new() { Enabled = true, Day = 30, Start = new(9, 30) };
        string fixture = HolidayCalendarChecks.Fixture();
        int requests = 0;
        var completion = new TaskCompletionSource<HolidayCalendarDownload>(TaskCreationOptions.RunContinuationsAsynchronously);
        calendarDownloadOverride = (_, _) => { requests++; return completion.Task; };
        Task updating = UpdateCalendarAsync(settings.CalendarUpdate.DueSlot(now));
        check(calendarUpdating && !updateCalendar.Enabled && calendarStatus.Text.Contains("正在更新"), "calendar UI marks in-flight update and disables duplicate clicks");
        await UpdateCalendarAsync();
        CheckCalendarUpdate();
        check(requests == 1, "manual and automatic calendar updates never overlap");
        completion.SetResult(new(new() { [2026] = fixture }, "更新成功"));
        await updating;
        var persisted = Settings.Load();
        check(!calendarUpdating && updateCalendar.Enabled && persisted.CalendarUpdate.LastSuccessfulMonth == 202609 &&
            persisted.CalendarYears.ContainsKey(2026) && persisted.CalendarUpdate.LastSuccess.HasValue,
            "successful calendar update atomically persists the cache, timestamp and monthly completion");
        now = now.AddHours(1); CheckCalendarUpdate();
        check(requests == 1, "successful month does not trigger another automatic update");

        var successTime = settings.CalendarUpdate.LastSuccess;
        now = new(2026, 10, 30, 9, 30, 0);
        calendarDownloadOverride = (_, _) => Task.FromException<HolidayCalendarDownload>(new IOException("simulated offline"));
        await UpdateCalendarAsync(settings.CalendarUpdate.DueSlot(now));
        persisted = Settings.Load();
        check(persisted.CalendarYears[2026] == fixture && persisted.CalendarUpdate.LastSuccess == successTime &&
            persisted.CalendarUpdate.Status.Contains("simulated offline") && persisted.CalendarUpdate.LastSuccessfulMonth == 202609,
            "failed calendar update preserves usable data and last success while persisting failure status");
        check(persisted.CalendarUpdate.DueSlot(now.AddMinutes(59)) is null && persisted.CalendarUpdate.DueSlot(now.AddHours(1)).HasValue,
            "failed calendar update remains rate-limited after application restart");

        // Drive the actual manual update button; scheduling is disabled here.
        settings.CalendarUpdate.Enabled = false;
        calendarDownloadOverride = (_, _) => Task.FromResult(new HolidayCalendarDownload(new() { [2026] = fixture }, "手动更新成功"));
        SelectPage(4);
        // Invoke the click handler without requiring a visible/focusable desktop.
        typeof(Button).GetMethod("OnClick", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .Invoke(updateCalendar, new object[] { EventArgs.Empty });
        check(settings.CalendarUpdate.Status == "手动更新成功", "manual calendar click handler works with automatic updates disabled");

        string folder = Settings.Folder;
        string blockedPath = Path.Combine(folder, "not-a-directory");
        File.WriteAllText(blockedPath, "test");
        try
        {
            Settings.Folder = blockedPath;
            await UpdateCalendarAsync();
            check(calendarStatus.Text.Contains("状态保存失败") && settings.CalendarYears[2026] == fixture,
                "calendar write failure stays visible and does not replace the active cache");
        }
        finally { Settings.Folder = folder; }
    }
}
