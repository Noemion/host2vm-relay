namespace Host2VMRelay;

public sealed class ConnectionSchedule
{
    public bool Enabled { get; set; }
    public TimeOnly Start { get; set; } = new(9, 0);
    public TimeOnly End { get; set; } = new(18, 0);
    public bool Workdays { get; set; } = true;
    public bool RestDays { get; set; }

    internal void Validate()
    {
        if (Start == End || Start.Ticks % TimeSpan.TicksPerMinute != 0 || End.Ticks % TimeSpan.TicksPerMinute != 0)
            throw new IOException("定时连接的开始和结束时间必须精确到分钟，且不能相同。");
        if (Enabled && !Workdays && !RestDays)
            throw new IOException("定时连接至少需要选择工作日或休息日。");
    }

    // The day selection belongs to the start date, including after midnight.
    internal ScheduleWindow Evaluate(DateTime localNow, Func<DateOnly, bool?>? isWorkday = null)
    {
        if (!Enabled) return new(null, "定时连接未启用");
        var date = DateOnly.FromDateTime(localNow);
        var time = TimeOnly.FromDateTime(localNow);
        bool overnight = End < Start;
        if (overnight && time < End) date = date.AddDays(-1);
        bool? workday = (isWorkday ?? ChinaWorkCalendar.BuiltinIsWorkday)(date);
        if (!(Workdays && RestDays) && workday is null)
            return new(null, $"缺少 {date.Year} 年中国节假日安排，定时连接已暂停；请更新日历或选择每天。");
        bool selected = Workdays && RestDays || (workday == true ? Workdays : RestDays);
        bool inTime = overnight ? time >= Start || time < End : time >= Start && time < End;
        if (!selected || !inTime) return new(null, "等待下一个定时时段 · 当前使用宿主机原有分流");
        return new(date, "当前处于定时时段");
    }
}

internal readonly record struct ScheduleWindow(DateOnly? StartDate, string Description)
{
    public bool Active => StartDate.HasValue;
}

internal sealed class ScheduleActivation
{
    private DateOnly? attempted;
    internal void Reset() => attempted = null;
    internal void Suppress(ScheduleWindow window) { if (window.Active) attempted = window.StartDate; }
    internal bool ShouldStart(ScheduleWindow window)
    {
        if (!window.Active || attempted == window.StartDate) return false;
        attempted = window.StartDate;
        return true;
    }
}
