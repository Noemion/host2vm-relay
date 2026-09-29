namespace Host2VMRelay;

public sealed class CalendarUpdateSettings
{
    public bool Enabled { get; set; }
    public int Day { get; set; } = 1;
    public TimeOnly Start { get; set; } = new(9, 0);
    public DateTime? LastAutomaticSlot { get; set; }
    public int LastSuccessfulMonth { get; set; }
    public DateTimeOffset? LastAttempt { get; set; }
    public DateTimeOffset? LastSuccess { get; set; }
    public string Status { get; set; } = "尚未在线更新";

    internal CalendarUpdateSettings Copy() => (CalendarUpdateSettings)MemberwiseClone();
    internal void Validate()
    {
        if (Day is < 1 or > 31 || Start.Ticks % TimeSpan.TicksPerMinute != 0 || Status is null || Status.Length > 2000)
            throw new IOException("日历自动更新日期或时间无效。");
    }

    internal DateTime? DueSlot(DateTime now)
    {
        if (!Enabled || LastSuccessfulMonth == now.Year * 100 + now.Month ||
            now.Day != Math.Min(Day, DateTime.DaysInMonth(now.Year, now.Month))) return null;
        var start = now.Date.Add(Start.ToTimeSpan());
        if (now < start) return null;
        var slot = start.AddHours(Math.Floor((now - start).TotalHours));
        return LastAutomaticSlot >= slot ? null : slot;
    }
}
