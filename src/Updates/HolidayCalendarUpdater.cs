using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Host2VMRelay;

internal sealed record HolidayYear(int Year, IReadOnlyDictionary<DateOnly, bool> Days)
{
    internal const int MaxBytes = 128 * 1024;
    internal static HolidayYear? Parse(string json, int expectedYear)
    {
        if (json.Length > MaxBytes) throw new IOException("节假日日历文件过大。");
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (expectedYear is < 2000 or > 9998 || root.GetProperty("year").GetInt32() != expectedYear)
            throw new IOException("节假日日历年份不匹配。");
        var papers = root.GetProperty("papers");
        var days = root.GetProperty("days");
        if (papers.GetArrayLength() == 0 && days.GetArrayLength() == 0) return null;
        if (papers.GetArrayLength() == 0 || days.GetArrayLength() is < 1 or > 100)
            throw new IOException("节假日日历缺少公告来源或有效日期。");
        foreach (var paper in papers.EnumerateArray())
            if (!Uri.TryCreate(paper.GetString(), UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                !(uri.Host == "gov.cn" || uri.Host.EndsWith(".gov.cn", StringComparison.OrdinalIgnoreCase)))
                throw new IOException("节假日日历公告来源无效。");
        var result = new Dictionary<DateOnly, bool>();
        var names = new HashSet<string>();
        foreach (var day in days.EnumerateArray())
        {
            string name = day.GetProperty("name").GetString() ?? "";
            var date = DateOnly.ParseExact(day.GetProperty("date").GetString()!, "yyyy-MM-dd", CultureInfo.InvariantCulture);
            if (string.IsNullOrWhiteSpace(name) || date < new DateOnly(expectedYear - 1, 12, 1) || date > new DateOnly(expectedYear + 1, 1, 31) ||
                !result.TryAdd(date, !day.GetProperty("isOffDay").GetBoolean()))
                throw new IOException("节假日日历包含重复或无效日期。");
            names.Add(name);
        }
        foreach (string holiday in new[] { "元旦", "春节", "清明", "劳动", "端午", "中秋", "国庆" })
            if (!names.Any(name => name.Contains(holiday, StringComparison.Ordinal)))
                throw new IOException("节假日日历尚不完整，缺少：" + holiday);
        return new(expectedYear, result);
    }
}

internal sealed record HolidayCalendarDownload(Dictionary<int, string> Years, string Status);

internal sealed class HolidayCalendarUpdater(HttpClient http)
{
    internal async Task<HolidayCalendarDownload> DownloadAsync(int year, CancellationToken token)
    {
        var downloaded = new Dictionary<int, string>();
        // Next year's notice can override dates in December of this year.
        foreach (int target in new[] { year - 1, year, year + 1 })
        {
            string? json = await FetchAsync(target, token).ConfigureAwait(false);
            if (json is not null) downloaded[target] = json;
            else if (target <= year) throw new IOException($"{target} 年节假日安排尚未完整发布，保留原日历。");
        }
        string status = "更新成功 · 已下载 " + string.Join("、", downloaded.Keys) + " 年";
        if (!downloaded.ContainsKey(year + 1)) status += $"；{year + 1} 年尚未公布，未作为有效日历保存";
        return new(downloaded, status);
    }

    private async Task<string?> FetchAsync(int year, CancellationToken token)
    {
        Exception? last = null;
        foreach (string root in new[] { "https://raw.githubusercontent.com/NateScarlet/holiday-cn/master/",
            "https://cdn.jsdelivr.net/gh/NateScarlet/holiday-cn@master/" })
        {
            token.ThrowIfCancellationRequested();
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, root + year + ".json");
                request.Headers.UserAgent.ParseAdd("Host2VMRelay/0.9.0");
                request.Headers.CacheControl = new() { NoCache = true };
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > HolidayYear.MaxBytes) throw new IOException("节假日日历文件过大。");
                using var stream = await response.Content.ReadAsStreamAsync(timeout.Token).ConfigureAwait(false);
                using var buffer = new MemoryStream();
                byte[] bytes = new byte[4096];
                int count;
                while ((count = await stream.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (buffer.Length + count > HolidayYear.MaxBytes) throw new IOException("节假日日历文件过大。");
                    buffer.Write(bytes, 0, count);
                }
                string json = System.Text.Encoding.UTF8.GetString(buffer.ToArray());
                return HolidayYear.Parse(json, year) is null ? null : json;
            }
            catch (Exception ex) when (!token.IsCancellationRequested && ex is HttpRequestException or IOException or JsonException or
                FormatException or InvalidOperationException or KeyNotFoundException or OperationCanceledException)
            { last = ex; }
        }
        throw new IOException($"无法更新 {year} 年日历：" + last?.Message, last);
    }
}
