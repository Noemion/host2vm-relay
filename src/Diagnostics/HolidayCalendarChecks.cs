using System.Net;
using System.Text.Json.Nodes;

namespace Host2VMRelay;

internal static class HolidayCalendarChecks
{
    internal static string Fixture()
    {
        using var stream = typeof(HolidayCalendarChecks).Assembly.GetManifestResourceStream("Host2VMRelay.Calendar2026Test")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    internal static async Task RunLiveAsync(string output)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(output))!);
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = Timeout.InfiniteTimeSpan };
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(65));
            var result = await new HolidayCalendarUpdater(http).DownloadAsync(DateTime.Now.Year, timeout.Token);
            var calendar = new ChinaWorkCalendar(result.Years);
            File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { Status = "PASS", Result = result.Status, Coverage = calendar.Coverage }));
        }
        catch (Exception ex)
        {
            File.WriteAllText(output, System.Text.Json.JsonSerializer.Serialize(new { Status = "FAIL", Error = ex.Message }));
            Environment.ExitCode = 1;
        }
    }

    internal static async Task RunAsync(Action<bool, string> check)
    {
        string fixture = Fixture();
        var parsed = HolidayYear.Parse(fixture, 2026)!;
        var downloaded = new ChinaWorkCalendar(new Dictionary<int, string> { [2026] = fixture });
        for (var date = new DateOnly(2026, 1, 1); date.Year == 2026; date = date.AddDays(1))
            if (downloaded.IsWorkday(date) != ChinaWorkCalendar.BuiltinIsWorkday(date))
                throw new Exception("Official 2026 calendar disagrees with downloaded fixture on " + date);
        check(parsed.Days.Count == 39, "downloaded 2026 calendar agrees with official built-in calendar for all 365 days");
        const string pending = "{\"year\":2027,\"papers\":[],\"days\":[]}";
        check(HolidayYear.Parse(pending, 2027) is null, "unpublished calendar placeholder never becomes a weekday-only calendar");
        var invalid = new List<string> { fixture.Replace("\"year\": 2026", "\"year\": 2027"), "{}", fixture.Replace("https://www.gov.cn", "https://example.com") };
        var root = JsonNode.Parse(fixture)!;
        var days = root["days"]!.AsArray();
        days.Add(days[0]!.DeepClone()); invalid.Add(root.ToJsonString()); days.RemoveAt(days.Count - 1);
        days[0]!.AsObject().Remove("isOffDay"); invalid.Add(root.ToJsonString());
        root = JsonNode.Parse(fixture)!; root["days"] = new JsonArray(root["days"]![0]!.DeepClone()); invalid.Add(root.ToJsonString());
        foreach (var json in invalid)
        {
            bool rejected = false;
            try { HolidayYear.Parse(json, 2026); } catch (Exception ex) when (ex is IOException or KeyNotFoundException or InvalidOperationException) { rejected = true; }
            check(rejected, "invalid, duplicate, incomplete or unsourced holiday data rejected");
        }
        string previous = fixture.Replace("2026", "2025"); // Transport fixture only, not a 2025 calendar assertion.
        int fallbackRequests = 0;
        using var handler = new FakeHandler(request =>
        {
            check(request.Headers.Authorization is null, "public calendar requests carry no credentials");
            if (request.RequestUri!.Host == "raw.githubusercontent.com") return new(HttpStatusCode.ServiceUnavailable);
            fallbackRequests++;
            string data = request.RequestUri.AbsolutePath.EndsWith("2025.json") ? previous :
                request.RequestUri.AbsolutePath.EndsWith("2026.json") ? fixture : pending;
            return new(HttpStatusCode.OK) { Content = new StringContent(data) };
        });
        using var http = new HttpClient(handler);
        var result = await new HolidayCalendarUpdater(http).DownloadAsync(2026, default);
        check(fallbackRequests == 3 && result.Years.Count == 2 && result.Status.Contains("2027 年尚未公布"),
            "calendar source failure falls back to CDN and an unpublished next year still permits success");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        bool cancellationObserved = false;
        try { await new HolidayCalendarUpdater(http).DownloadAsync(2026, canceled.Token); } catch (OperationCanceledException) { cancellationObserved = true; }
        check(cancellationObserved && fallbackRequests == 3, "calendar cancellation stops before more network requests");
        using var brokenHttp = new HttpClient(new FakeHandler(_ => new(HttpStatusCode.OK) { Content = new StringContent("{}") }));
        bool failed = false;
        try { await new HolidayCalendarUpdater(brokenHttp).DownloadAsync(2026, default); } catch (IOException) { failed = true; }
        check(failed, "invalid calendar sources never publish a partial download");

        var plan = new CalendarUpdateSettings { Enabled = true, Day = 31, Start = new(9, 30) };
        check(plan.DueSlot(new(2026, 2, 28, 9, 29, 59)) is null && plan.DueSlot(new(2026, 2, 28, 9, 30, 0)) == new DateTime(2026, 2, 28, 9, 30, 0),
            "monthly update starts on exact minute and clamps day 31 to month end");
        plan.LastAutomaticSlot = new(2026, 2, 28, 9, 30, 0);
        check(plan.DueSlot(new(2026, 2, 28, 10, 29, 59)) is null && plan.DueSlot(new(2026, 2, 28, 10, 30, 0)) == new DateTime(2026, 2, 28, 10, 30, 0),
            "failed monthly update retries hourly without retrying the same slot");
        check(plan.DueSlot(new(2026, 3, 1, 9, 30, 0)) is null, "monthly retry does not spill into the following day");
        plan.LastSuccessfulMonth = 202602;
        check(plan.DueSlot(new(2026, 2, 28, 23, 45, 0)) is null && plan.DueSlot(new(2026, 3, 31, 12, 45, 0)) == new DateTime(2026, 3, 31, 12, 30, 0),
            "monthly success stops retries until next month and late startup catches the current slot");
        var restored = Settings.Deserialize(new Settings { CalendarUpdate = plan, CalendarYears = result.Years }.Serialize());
        check(restored.CalendarUpdate.LastSuccessfulMonth == 202602 && restored.CalendarYears.Count == 2,
            "calendar data and monthly success state persist together across restarts and profile migration");
        plan.LastSuccessfulMonth = 0; plan.LastAutomaticSlot = null;
        check(plan.DueSlot(new(2028, 2, 29, 10, 0, 0)).HasValue && plan.DueSlot(new(2028, 2, 28, 10, 0, 0)) is null,
            "monthly day 31 honors leap-year February");
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => Task.FromResult(reply(request));
    }
}
