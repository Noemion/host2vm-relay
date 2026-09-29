namespace Host2VMRelay;

internal sealed class ChinaWorkCalendar
{
    private readonly Dictionary<DateOnly, bool> overrides = new();
    private readonly HashSet<int> downloadedYears = new();
    internal string Coverage => string.Join("、", downloadedYears.Append(2026).Distinct().Order()) + " 年";
    internal ChinaWorkCalendar(IReadOnlyDictionary<int, string>? cached = null)
    {
        if (cached is null) return;
        foreach (var pair in cached.OrderBy(pair => pair.Key))
        {
            var year = HolidayYear.Parse(pair.Value, pair.Key) ?? throw new IOException("缓存日历尚未公布。");
            downloadedYears.Add(year.Year);
            foreach (var day in year.Days) overrides[day.Key] = day.Value;
        }
    }

    internal bool? IsWorkday(DateOnly date)
    {
        if (overrides.TryGetValue(date, out bool workday)) return workday;
        if (downloadedYears.Contains(date.Year)) return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
        return BuiltinIsWorkday(date);
    }
    // State Council notice 国办发明电〔2025〕7号, verified 2026-09-30.
    // https://www.beijing.gov.cn/zhengce/zhengcefagui/202511/t20251104_4258873.html
    private static readonly HashSet<DateOnly> ExtraWorkdays = new()
    {
        new(2026, 1, 4), new(2026, 2, 14), new(2026, 2, 28),
        new(2026, 5, 9), new(2026, 9, 20), new(2026, 10, 10)
    };
    private static readonly (DateOnly Start, DateOnly End)[] Holidays =
    {
        (new(2026, 1, 1), new(2026, 1, 3)),
        (new(2026, 2, 15), new(2026, 2, 23)),
        (new(2026, 4, 4), new(2026, 4, 6)),
        (new(2026, 5, 1), new(2026, 5, 5)),
        (new(2026, 6, 19), new(2026, 6, 21)),
        (new(2026, 9, 25), new(2026, 9, 27)),
        (new(2026, 10, 1), new(2026, 10, 7))
    };

    internal static bool? BuiltinIsWorkday(DateOnly date)
    {
        // Never guess future statutory adjustments from a weekday alone.
        if (date.Year != 2026) return null;
        if (ExtraWorkdays.Contains(date)) return true;
        if (Holidays.Any(range => date >= range.Start && date <= range.End)) return false;
        return date.DayOfWeek is not (DayOfWeek.Saturday or DayOfWeek.Sunday);
    }
}
