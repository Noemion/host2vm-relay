namespace Host2VMRelay;

public sealed partial class MainForm
{
    private void VerifyScheduleForTest(Action<bool, string> check)
    {
        var saved = settings;
        var savedClock = scheduleClock;
        var originalCaption = state.Text;
        var originalFeed = feed.Text;
        var now = new DateTime(2026, 9, 30, 18, 0, 0);
        try
        {
            scheduleClock = () => now;
            settings = Settings.Deserialize(settings.Serialize());
            settings.Schedule = new ConnectionSchedule { Enabled = true };
            // These paths intentionally have no session, credentials or network I/O.
            Connect().GetAwaiter().GetResult();
            check(!wanted && !busy && session is null && feed.Text.Contains("关闭定时"), "manual connect is blocked outside the schedule");
            wanted = true; busy = true;
            using (var pending = new CancellationTokenSource())
            {
                connectionAttempt = pending;
                PollNetworkAsync().GetAwaiter().GetResult();
                check(pending.IsCancellationRequested && !wanted, "schedule end cancels a busy connection attempt and clears reconnect intent");
                connectionAttempt = null;
            }
            busy = false;
            PollNetworkAsync().GetAwaiter().GetResult();
            check(!wanted && state.Text.Contains("定时等待"), "schedule end presents an intentional waiting state");
            now = new(2026, 9, 30, 12, 0, 0);
            wanted = true;
            Stop().GetAwaiter().GetResult();
            PollNetworkAsync().GetAwaiter().GetResult();
            check(!wanted && !busy && session is null, "manual stop inside an active window is not undone by the next timer tick");
            settings.Schedule.Enabled = false;
            wanted = true;
            check(!ApplyScheduleAsync().GetAwaiter().GetResult() && wanted, "disabling schedule preserves the current connection intent");
            settings.Schedule.Enabled = true;
            now = new(2027, 1, 4, 12, 0, 0);
            PollNetworkAsync().GetAwaiter().GetResult();
            check(!wanted && scheduleStatus.Text.Contains("2027"), "missing calendar pauses automatic reconnect and exposes the year in the UI");
            var start = pages.Pages[4].Controls.Find("scheduleStart", true).OfType<DateTimePicker>().Single();
            var end = pages.Pages[4].Controls.Find("scheduleEnd", true).OfType<DateTimePicker>().Single();
            check(start.CustomFormat == "HH:mm" && end.CustomFormat == "HH:mm" && start.ShowUpDown && end.ShowUpDown,
                "schedule exposes keyboard-editable 24-hour start and end pickers");
        }
        finally
        {
            settings = saved; scheduleClock = savedClock;
            busy = wanted = false; connectionAttempt = null;
            scheduleActivation.Reset(); lastScheduleNotice = null;
            state.Text = originalCaption; feed.Text = originalFeed;
            UpdateScheduleStatus(settings.Schedule.Evaluate(scheduleClock()));
            SetConnectionControls(false);
        }
    }
}
