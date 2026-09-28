using System.Net;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Host2VMRelay;

internal static class ReleaseUpdateChecks
{
    public static async Task RunAsync(string output, Action<bool, string> check)
    {
        byte[] payload = Encoding.UTF8.GetBytes("Update download integrity test; never executed.");
        string digest = Convert.ToHexString(SHA256.HashData(payload));
        string Metadata(string version = "0.10.0", string arch = "x64", string? hash = null, bool prerelease = false, string? url = null) => JsonSerializer.Serialize(new
        {
            tag_name = "v" + version, draft = false, prerelease, body = "Release notes",
            assets = new[] { new { name = $"Host2VMRelay-{version}-win-{arch}-Setup.exe", size = payload.Length,
                digest = "sha256:" + (hash ?? digest), browser_download_url = url ?? $"{ReleaseUpdater.RepositoryUrl}/releases/download/v{version}/Host2VMRelay-{version}-win-{arch}-Setup.exe" } }
        });
        foreach (var (arch, name) in new[] { (Architecture.X86, "x86"), (Architecture.X64, "x64"), (Architecture.Arm64, "arm64") })
        {
            var candidate = ReleaseUpdater.ParseRelease(Metadata(arch: name), arch);
            check(candidate.AssetName.EndsWith($"win-{name}-Setup.exe") && candidate.Version > new Version(0, 9, 9, 0), "numeric release comparison and installer selection: " + name);
        }
        check(ReleaseUpdater.ParseRelease(Metadata("0.6.7"), Architecture.X64).Version == new Version(0, 6, 7, 0), "release tag and assembly version compare equally");
        foreach (string invalid in new[] { Metadata(prerelease: true), Metadata("0.10.0-beta"), Metadata(hash: ""), Metadata(url: "https://example.com/file.exe"), Metadata(arch: "x86") })
        {
            bool rejected = false;
            try { ReleaseUpdater.ParseRelease(invalid, Architecture.X64); } catch (IOException) { rejected = true; }
            check(rejected, "reject incompatible or unverified release metadata");
        }
        string testRoot = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "release-update-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(testRoot);
        var cache = new UpdateCache(testRoot);
        var release = ReleaseUpdater.ParseRelease(Metadata(), Architecture.X64);
        using var handler = new FakeHandler();
        using var http = new HttpClient(handler);
        var updater = new ReleaseUpdater(http);
        try
        {
            handler.Reply = _ => new(HttpStatusCode.TooManyRequests);
            bool limited = false;
            try { await updater.CheckAsync(Architecture.X64, default); } catch (IOException ex) { limited = ex.Message.Contains("稍后重试"); }
            check(limited, "API rate limits produce retry guidance");
            handler.Reply = _ => new(HttpStatusCode.OK) { Content = new StringContent(Metadata()) };
            check((await updater.CheckAsync(Architecture.X64, default)).Version == release.Version, "release API response reaches the version parser");

            handler.Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            using (var installer = await updater.DownloadAsync(release, cache, null, default))
            {
                check(File.ReadAllBytes(installer.FilePath).SequenceEqual(payload), "download is verified before installation becomes available");
                bool locked = false;
                try { using var replacement = new FileStream(installer.FilePath, FileMode.Open, FileAccess.Write); } catch (IOException) { locked = true; }
                check(locked, "verified installer remains protected against replacement before launch");
                bool launchFailed = false;
                try { installer.Launch(_ => throw new IOException("simulated launch failure")); } catch (IOException) { launchFailed = true; }
                check(launchFailed && !File.Exists(Path.Combine(Path.GetDirectoryName(installer.FilePath)!, "launched")), "failed launch does not mark installation as started");
            }
            check(!Directory.EnumerateDirectories(testRoot).Any(), "abandoned verified download is removed");
            var progress = new List<int>();
            string launchedPath;
            using (var installer = await updater.DownloadAsync(release, cache, new InlineProgress(progress.Add), default))
            {
                launchedPath = installer.FilePath;
                installer.Launch(file => check(file == launchedPath && File.Exists(file), "handoff uses exactly the verified installer (launch mocked)"));
                cache.Cleanup();
                check(File.Exists(launchedPath) && File.Exists(Path.Combine(Path.GetDirectoryName(launchedPath)!, "launched")),
                    "locked installer retains its marker for cleanup on the next startup");
            }
            check(File.Exists(launchedPath) && progress.Count > 0 && progress[^1] == 100 && progress.SequenceEqual(progress.Order()), "successful handoff retains installer and download progress reaches 100");
            cache.Cleanup();
            check(!File.Exists(launchedPath), "next-startup cleanup removes the completed installer");

            foreach (byte[] invalid in new[] { Encoding.UTF8.GetBytes(new string('x', payload.Length)), payload[..^1] })
            {
                handler.Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(invalid) };
                bool rejected = false;
                try { using var installer = await updater.DownloadAsync(release, cache, null, default); } catch (IOException) { rejected = true; }
                check(rejected && !Directory.EnumerateDirectories(testRoot).Any(), "corrupt or incomplete download is rejected and removed");
            }
            using var cancellation = new CancellationTokenSource();
            handler.Reply = _ => new(HttpStatusCode.OK) { Content = new ByteArrayContent(payload) };
            bool canceled = false;
            try { using var installer = await updater.DownloadAsync(release, cache, new InlineProgress(_ => cancellation.Cancel()), cancellation.Token); } catch (OperationCanceledException) { canceled = true; }
            check(canceled && !Directory.EnumerateDirectories(testRoot).Any(), "canceling a download removes partial files");

            string old = cache.CreateDownload(), recent = cache.CreateDownload();
            File.WriteAllText(Path.Combine(old, "download.part"), "partial");
            File.WriteAllText(Path.Combine(recent, "download.part"), "partial");
            Directory.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-8));
            string unrelated = Path.Combine(testRoot, "keep"); Directory.CreateDirectory(unrelated);
            File.WriteAllText(Path.Combine(unrelated, "launched"), "sentinel");
            cache.Cleanup();
            check(!Directory.Exists(old) && Directory.Exists(recent) && File.Exists(Path.Combine(unrelated, "launched")), "cache cleanup expires stale downloads but preserves recent and unrelated folders");
        }
        finally
        {
            // This fresh GUID directory was created by this test beneath its output folder.
            Directory.Delete(testRoot, true);
        }
    }

    private sealed class InlineProgress(Action<int> report) : IProgress<int> { public void Report(int value) => report(value); }
    private sealed class FakeHandler : HttpMessageHandler
    {
        public Func<HttpRequestMessage, HttpResponseMessage> Reply { get; set; } = _ => throw new InvalidOperationException();
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => Task.FromResult(Reply(request));
    }
}
