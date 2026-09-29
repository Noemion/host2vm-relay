namespace Host2VMRelay;

public sealed partial class MainForm
{
    private ChinaWorkCalendar calendar = new();
    private readonly Label calendarStatus = UiLayout.Help("");
    private readonly Button updateCalendar = UiLayout.Primary("立即更新日历", 160);
    private bool calendarUpdating;
    private DateTime? attemptedCalendarSlot;
    private string? calendarTransientError;
    private Func<int, CancellationToken, Task<HolidayCalendarDownload>>? calendarDownloadOverride;

    private ScheduleWindow EvaluateSchedule() => settings.Schedule.Evaluate(scheduleClock(), calendar.IsWorkday);

    private Control BuildCalendarSettings()
    {
        var enabled = new CheckBox { Name = "calendarAutoUpdate", Text = "每月自动更新日历", AutoSize = true, Checked = settings.CalendarUpdate.Enabled };
        var day = new NumericUpDown { Name = "calendarUpdateDay", Minimum = 1, Maximum = 31, Value = settings.CalendarUpdate.Day };
        var start = new DateTimePicker { Name = "calendarUpdateStart", Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm", ShowUpDown = true, Value = DateTime.Today.Add(settings.CalendarUpdate.Start.ToTimeSpan()) };
        var save = UiLayout.Button("保存更新计划", 160); save.Name = "saveCalendarUpdate";
        updateCalendar.Name = "updateCalendarNow";
        var feedback = UiLayout.Help("自动更新默认关闭；手动更新不受日期限制。");
        save.Click += (_, _) =>
        {
            try
            {
                settings = settings.SaveUpdated(s =>
                {
                    s.CalendarUpdate.Enabled = enabled.Checked;
                    s.CalendarUpdate.Day = (int)day.Value;
                    s.CalendarUpdate.Start = TimeOnly.FromDateTime(start.Value);
                });
                feedback.Text = "更新计划已保存，按本机时间执行。";
                RefreshCalendarStatus();
                CheckCalendarUpdate();
            }
            catch (Exception ex) { feedback.Text = "保存失败：" + ex.Message; }
        };
        updateCalendar.Click += async (_, _) => await UpdateCalendarAsync();
        RefreshCalendarStatus();
        return UiLayout.Card("节假日日历更新", "在线来源：holiday-cn（根据国务院公告整理的公开数据），支持 GitHub 与 CDN 备用源。",
            enabled, UiLayout.Pair(UiLayout.Field("每月几号", day), UiLayout.Field("当天开始时间", start)),
            UiLayout.Help("从设定时间起每小时尝试一次，成功后当月停止；失败则仅在当天继续重试。没有对应日期的月份按月末执行。"),
            UiLayout.Help("程序需保持运行；当天稍晚启动会补一次当前时段，错过整天则等下个月。更新前一年、当年和已公布的次年日历；未公布的次年不影响本次成功。"),
            UiLayout.Actions(save, updateCalendar), feedback, calendarStatus);
    }

    private void RefreshCalendarStatus()
    {
        var value = settings.CalendarUpdate;
        string attempt = value.LastAttempt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "无";
        string success = value.LastSuccess?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "无";
        string result = calendarUpdating ? "正在更新…" : calendarTransientError ?? (value.Status == "正在更新…" ? "上次更新未完成，保留原日历" : value.Status);
        var now = scheduleClock();
        string plan = !value.Enabled ? "自动更新未启用" : value.LastSuccessfulMonth == now.Year * 100 + now.Month
            ? "本月自动更新已完成" : $"计划：每月 {value.Day} 日 {value.Start:HH:mm} 起，每小时重试";
        calendarStatus.Text = $"{result}\n可用日历：{calendar.Coverage}\n最近尝试：{attempt}\n最近成功：{success}\n{plan}";
    }

    private void CheckCalendarUpdate()
    {
        if (calendarUpdating || formLifetime.IsCancellationRequested) return;
        var slot = settings.CalendarUpdate.DueSlot(scheduleClock());
        if (slot is null || attemptedCalendarSlot >= slot) return;
        attemptedCalendarSlot = slot; // Also prevents a write failure from retrying every UI tick.
        _ = UpdateCalendarAsync(slot);
    }

    private async Task UpdateCalendarAsync(DateTime? automaticSlot = null)
    {
        if (calendarUpdating || formLifetime.IsCancellationRequested) return;
        calendarUpdating = true; updateCalendar.Enabled = false;
        calendarTransientError = null;
        try
        {
            settings = settings.SaveUpdated(s =>
            {
                s.CalendarUpdate.LastAttempt = DateTimeOffset.Now;
                s.CalendarUpdate.Status = "正在更新…";
                if (automaticSlot.HasValue) s.CalendarUpdate.LastAutomaticSlot = automaticSlot;
            });
            RefreshCalendarStatus();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(formLifetime.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(65));
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            int year = scheduleClock().Year;
            var result = await (calendarDownloadOverride?.Invoke(year, timeout.Token)
                ?? new HolidayCalendarUpdater(http).DownloadAsync(year, timeout.Token));
            if (formLifetime.IsCancellationRequested || IsDisposed) return;
            settings = settings.SaveUpdated(s =>
            {
                foreach (int old in s.CalendarYears.Keys.Where(key => key < year - 2).ToArray()) s.CalendarYears.Remove(old);
                foreach (var entry in result.Years) s.CalendarYears[entry.Key] = entry.Value;
                s.CalendarUpdate.LastSuccess = DateTimeOffset.Now;
                s.CalendarUpdate.Status = result.Status;
                if (automaticSlot is { } slot) s.CalendarUpdate.LastSuccessfulMonth = slot.Year * 100 + slot.Month;
            });
            calendar = new(settings.CalendarYears);
            Log(result.Status);
            // Apply new day classifications on the next normal timer tick, so
            // opening SSH cannot keep the calendar update button busy.
            UpdateScheduleStatus(EvaluateSchedule());
        }
        catch (Exception ex)
        {
            if (formLifetime.IsCancellationRequested || IsDisposed) return;
            string reason = ex is OperationCanceledException ? "请求超时" : ex.Message;
            string status = "更新失败，保留原日历：" + reason[..Math.Min(reason.Length, 500)];
            try { settings = settings.SaveUpdated(s => s.CalendarUpdate.Status = status); }
            catch (Exception saveError) { calendarTransientError = status + "；状态保存失败：" + saveError.Message; }
            Log(status);
            calendarStatus.Text = status;
        }
        finally
        {
            calendarUpdating = false;
            if (!IsDisposed) { updateCalendar.Enabled = true; RefreshCalendarStatus(); }
        }
    }
}
