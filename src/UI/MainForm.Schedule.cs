namespace Host2VMRelay;

public sealed partial class MainForm
{
    private readonly ScheduleActivation scheduleActivation = new();
    private readonly Label scheduleStatus = UiLayout.Help("");
    private readonly Label scheduleOverview = UiLayout.Help("");
    private CancellationTokenSource? connectionAttempt;
    private string? lastScheduleNotice;
    private Func<DateTime> scheduleClock = () => DateTime.Now;

    private Control BuildScheduleSettings()
    {
        var enabled = new CheckBox { Name = "scheduleEnabled", Text = "启用定时连接", AutoSize = true, Checked = settings.Schedule.Enabled };
        var workdays = new CheckBox { Name = "scheduleWorkdays", Text = "工作日（含法定调休上班日）", AutoSize = true, Checked = settings.Schedule.Workdays };
        var restDays = new CheckBox { Name = "scheduleRestDays", Text = "休息日（排除调休上班日）", AutoSize = true, Checked = settings.Schedule.RestDays };
        DateTimePicker Picker(string name, TimeOnly time) => new() { Name = name, Format = DateTimePickerFormat.Custom,
            CustomFormat = "HH:mm", ShowUpDown = true, Value = DateTime.Today.Add(time.ToTimeSpan()) };
        var start = Picker("scheduleStart", settings.Schedule.Start);
        var end = Picker("scheduleEnd", settings.Schedule.End);
        var save = UiLayout.Primary("保存定时设置", 160); save.Name = "saveSchedule";
        var feedback = UiLayout.Help("保存后立即生效。开始时间自动连接，结束时间断开并恢复宿主机原有分流。");
        save.Click += async (_, _) =>
        {
            try
            {
                var candidate = new ConnectionSchedule { Enabled = enabled.Checked, Start = TimeOnly.FromDateTime(start.Value),
                    End = TimeOnly.FromDateTime(end.Value), Workdays = workdays.Checked, RestDays = restDays.Checked };
                candidate.Validate();
                // Save connection fields as well so an unattended start uses the reviewed profile.
                if (candidate.Enabled) SaveConnection();
                settings = settings.SaveUpdated(s => s.Schedule = candidate);
                scheduleActivation.Reset(); lastScheduleNotice = null;
                save.ShowFeedback("✓ 已保存");
                feedback.Text = candidate.Enabled ? "已保存。定时连接已生效；首次使用请先手动连接并核对服务器指纹。"
                    : "已关闭定时连接，当前连接保持原状，可手动断开。";
                await PollNetworkAsync();
            }
            catch (Exception ex) { feedback.Text = "保存失败：" + ex.Message; }
        };
        UpdateScheduleStatus(EvaluateSchedule());
        return UiLayout.Card("定时连接", "按本机时间运行，程序需保持运行（可缩到托盘）；不会唤醒休眠的电脑。",
            enabled, UiLayout.Pair(UiLayout.Field("开始时间", start), UiLayout.Field("结束时间", end)), workdays, restDays,
            UiLayout.Help("两项都选表示每天。休息日包括周末和法定假日，排除调休上班日。结束时间早于开始时间表示跨午夜，日期类型按开始当天判断。"),
            UiLayout.Help("内置 2026 年中国节假日安排，可在下方更新日历。缺少对应年份时暂停并提示更新；“每天”不受影响。"),
            UiLayout.Help("请先完成一次手动连接以保存服务器指纹；重启后自动连接需要已保存的凭据或可用私钥。手动断开后，本次时段不再自动连接，下个时段恢复。关闭定时后当前连接保持原状。"),
            UiLayout.Actions(save), feedback, scheduleStatus);
    }

    private void UpdateScheduleStatus(ScheduleWindow window)
    {
        string text = settings.Schedule.Enabled ? $"定时 {settings.Schedule.Start:HH:mm}–{settings.Schedule.End:HH:mm} · {window.Description}" : window.Description;
        if (scheduleStatus.Text != text) scheduleStatus.Text = text;
        if (scheduleOverview.Text != text) scheduleOverview.Text = text;
    }

    // Runs before the networking busy guard, so the end time can cancel an SSH attempt.
    private async Task<bool> ApplyScheduleAsync()
    {
        var window = EvaluateSchedule();
        UpdateScheduleStatus(window);
        if (!settings.Schedule.Enabled) return false;
        if (!window.Active)
        {
            if (lastScheduleNotice != window.Description) { Log(window.Description); lastScheduleNotice = window.Description; }
            wanted = false;
            connectionAttempt?.Cancel();
            if (!busy && session is not null) await Stop(manual: false);
            if (!busy && session is null) PresentScheduledStop();
            return true;
        }
        lastScheduleNotice = null;
        if (busy || polling) return true;
        if (scheduleActivation.ShouldStart(window) && !wanted && session?.IsConnected != true)
        {
            Log("已进入定时时段，自动连接虚拟机。");
            await Connect(automatic: true);
            return true;
        }
        return false;
    }

    private void PresentScheduledStop()
    {
        lastPath = "host"; connectionFailureReason = null;
        state.Text = "● 定时等待"; state.ForeColor = Color.DimGray;
        feed.Text = EvaluateSchedule().Description;
        connectionTip.SetToolTip(state, feed.Text);
        SetConnectionControls(false);
        SetConnectionIcon(ConnectionIconState.Disconnected, "定时等待 · 虚拟机未连接");
    }
}
