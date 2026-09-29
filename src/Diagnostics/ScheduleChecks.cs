namespace Host2VMRelay;

internal static class ScheduleChecks
{
    internal static void Run(Action<bool, string> check)
    {
        var schedule = new ConnectionSchedule { Enabled = true };
        bool Active(string date) => schedule.Evaluate(DateTime.Parse(date, System.Globalization.CultureInfo.InvariantCulture)).Active;
        check(!Active("2026-09-30 08:59:59") && Active("2026-09-30 09:00:00") &&
            Active("2026-09-30 17:59:59") && !Active("2026-09-30 18:00:00"), "schedule includes start and excludes end");
        foreach (var date in new[] { "2026-01-04", "2026-02-14", "2026-02-28", "2026-05-09", "2026-09-20", "2026-10-10" })
            check(Active(date + " 12:00"), "statutory makeup workday " + date);
        foreach (var date in new[] { "2026-01-01", "2026-01-03", "2026-02-15", "2026-02-23", "2026-04-06",
            "2026-05-05", "2026-06-19", "2026-09-25", "2026-10-01", "2026-10-07", "2026-09-19" })
            check(!Active(date + " 12:00"), "holiday or ordinary weekend excluded " + date);
        schedule.Workdays = false; schedule.RestDays = true;
        check(Active("2026-10-01 12:00") && Active("2026-09-19 12:00") && !Active("2026-09-20 12:00") && !Active("2026-09-30 12:00"),
            "rest-day schedule includes holidays and weekends but excludes makeup workdays");
        schedule.Workdays = true;
        check(Active("2027-01-01 12:00"), "every-day schedule does not require a statutory calendar");
        schedule.RestDays = false;
        check(!Active("2027-01-01 12:00") && schedule.Evaluate(new(2027, 1, 1, 12, 0, 0)).Description.Contains("2027"),
            "unknown calendar year pauses automation with an explicit warning");
        schedule.Start = new(22, 0); schedule.End = new(2, 0);
        check(Active("2026-09-30 22:00") && Active("2026-10-01 01:59:59") && !Active("2026-10-01 02:00") && !Active("2026-10-01 22:00"),
            "overnight window retains its workday start date across a holiday boundary");
        check(Active("2027-01-01 01:00") && !Active("2027-01-01 22:00"), "overnight schedule retains supported start year across New Year");
        var activation = new ScheduleActivation();
        var evening = schedule.Evaluate(new(2026, 9, 30, 22, 0, 0));
        var morning = schedule.Evaluate(new(2026, 10, 1, 1, 0, 0));
        check(activation.ShouldStart(evening) && !activation.ShouldStart(morning), "startup or wake inside a window starts once across midnight");
        activation.Reset(); activation.Suppress(evening);
        check(!activation.ShouldStart(evening) && !activation.ShouldStart(morning) &&
            activation.ShouldStart(schedule.Evaluate(new(2026, 10, 8, 22, 0, 0))), "manual stop suppresses current window but allows the next workday");
        activation.Reset();
        check(activation.ShouldStart(evening), "saving schedule re-evaluates the current window");
        check(!Settings.Deserialize("{}").Schedule.Enabled, "legacy settings do not enable scheduled connections");
        var settings = new Settings { Schedule = schedule };
        var restored = Settings.Deserialize(settings.Serialize());
        check(restored.Schedule.Enabled && restored.Schedule.Start == new TimeOnly(22, 0) && restored.Schedule.End == new TimeOnly(2, 0)
            && restored.Schedule.Workdays && !restored.Schedule.RestDays, "schedule settings persist across restart");
        foreach (string json in new[] { "{\"Schedule\":null}", "{\"Schedule\":{\"Start\":\"09:00:00\",\"End\":\"09:00:00\"}}",
            "{\"Schedule\":{\"Enabled\":true,\"Workdays\":false,\"RestDays\":false}}", "{\"Schedule\":{\"Start\":\"09:00:01\"}}" })
        {
            bool rejected = false;
            try { Settings.Deserialize(json); } catch (IOException) { rejected = true; }
            check(rejected, "reject invalid schedule " + json);
        }
        string previous = Settings.Folder;
        string root = Path.Combine(Path.GetTempPath(), "h2vm-schedule-" + Guid.NewGuid().ToString("N"));
        try
        {
            Settings.Folder = root;
            settings.Save();
            try { settings.SaveUpdated(s => { s.Schedule.Start = s.Schedule.End; }); } catch (IOException) { }
            check(settings.Schedule.Start == new TimeOnly(22, 0) && Settings.Load().Schedule.Start == new TimeOnly(22, 0),
                "failed schedule save preserves live and persisted settings");
        }
        finally { Settings.Folder = previous; if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
