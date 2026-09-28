using System.Net;
using System.Net.Http.Headers;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Host2VMRelay;

internal sealed record ReleaseUpdate(Version Version, string Tag, string AssetName, Uri DownloadUrl, long Size, string Sha256)
{
    public string PageUrl => ReleaseUpdater.RepositoryUrl + "/releases/tag/" + Tag;
}

/// <summary>Reads only this repository's stable releases; downloads are bounded and verified before handoff.</summary>
internal sealed class ReleaseUpdater(HttpClient http)
{
    public const string RepositoryUrl = "https://github.com/Noemion/host2vm-relay";
    private const string LatestUrl = "https://api.github.com/repos/Noemion/host2vm-relay/releases/latest";
    private const long MaximumInstallerSize = 256L * 1024 * 1024;
    public static Version CurrentVersion => typeof(ReleaseUpdater).Assembly.GetName().Version!;

    public static HttpClient CreateClient() => new(new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All })
    { Timeout = Timeout.InfiniteTimeSpan };

    public async Task<ReleaseUpdate> CheckAsync(Architecture architecture, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        using var request = Request(LatestUrl);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.TooManyRequests)
            throw new IOException("GitHub 暂时限制了更新请求，请稍后重试，或打开发布页面查看。");
        response.EnsureSuccessStatusCode();
        await response.Content.LoadIntoBufferAsync(1024 * 1024).WaitAsync(deadline.Token).ConfigureAwait(false);
        return ParseRelease(await response.Content.ReadAsStringAsync(deadline.Token).ConfigureAwait(false), architecture);
    }

    internal static ReleaseUpdate ParseRelease(string json, Architecture architecture)
    {
        string rid = architecture switch { Architecture.X64 => "x64", Architecture.X86 => "x86", Architecture.Arm64 => "arm64", _ => throw new NotSupportedException("当前系统架构暂不支持自动升级。") };
        using var document = JsonDocument.Parse(json);
        var release = document.RootElement;
        string tag = release.GetProperty("tag_name").GetString() ?? "";
        if (release.GetProperty("draft").GetBoolean() || release.GetProperty("prerelease").GetBoolean() ||
            !Regex.IsMatch(tag, @"^v[0-9]+\.[0-9]+\.[0-9]+$", RegexOptions.CultureInvariant) || !Version.TryParse(tag[1..], out var version))
            throw new IOException("发布信息不是受支持的正式版本，请打开 GitHub 查看。");
        // Assembly versions have four components; normalize tags to the same shape.
        version = new Version(version.Major, version.Minor, version.Build, 0);
        string name = $"Host2VMRelay-{version.ToString(3)}-win-{rid}-Setup.exe";
        var assets = release.GetProperty("assets").EnumerateArray().Where(a => a.GetProperty("name").GetString() == name).ToArray();
        if (assets.Length != 1) throw new IOException("该版本尚未提供当前系统架构的安装包，请稍后重试。");
        var asset = assets[0];
        string expectedUrl = RepositoryUrl + "/releases/download/" + tag + "/" + name;
        if (asset.GetProperty("browser_download_url").GetString() != expectedUrl)
            throw new IOException("安装包下载地址与项目仓库不符，已拒绝下载。");
        string digest = asset.TryGetProperty("digest", out var value) ? value.GetString() ?? "" : "";
        if (!Regex.IsMatch(digest, "^sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant))
            throw new IOException("安装包缺少有效的 SHA-256 校验信息，请在 GitHub 查看发布状态。");
        long size = asset.GetProperty("size").GetInt64();
        if (size <= 0 || size > MaximumInstallerSize) throw new IOException("安装包大小异常，已拒绝下载。");
        return new(version, tag, name, new Uri(expectedUrl), size, digest[7..]);
    }

    public async Task<PreparedInstaller> DownloadAsync(ReleaseUpdate release, UpdateCache cache, IProgress<int>? progress, CancellationToken token)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(10));
        string folder = cache.CreateDownload();
        string partial = Path.Combine(folder, "download.part");
        string destination = Path.Combine(folder, release.AssetName);
        try
        {
            using var request = Request(release.DownloadUrl.AbsoluteUri);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength is long length && length != release.Size)
                throw new IOException("下载文件大小与发布信息不一致。");
            await using (var input = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false))
            await using (var output = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 65536, true))
            {
                byte[] buffer = new byte[65536];
                long received = 0;
                int previousPercent = -1;
                while (true)
                {
                    using var stalled = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token);
                    stalled.CancelAfter(TimeSpan.FromSeconds(30));
                    int count = await input.ReadAsync(buffer, stalled.Token).ConfigureAwait(false);
                    if (count == 0) break;
                    received += count;
                    if (received > release.Size) throw new IOException("下载文件超过预期大小。");
                    await output.WriteAsync(buffer.AsMemory(0, count), deadline.Token).ConfigureAwait(false);
                    int percent = (int)(received * 100 / release.Size);
                    if (percent != previousPercent) { progress?.Report(percent); previousPercent = percent; }
                }
                if (received != release.Size) throw new IOException("安装包下载不完整，请重试。");
            }
            File.Move(partial, destination);
            // Keep a read handle open through launch so the checked file cannot be
            // replaced or modified between verification and Process.Start.
            var guard = new FileStream(destination, FileMode.Open, FileAccess.Read, FileShare.Read, 65536, true);
            try
            {
                string actual = Convert.ToHexString(await SHA256.HashDataAsync(guard, deadline.Token).ConfigureAwait(false));
                if (!actual.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase)) throw new IOException("安装包 SHA-256 校验失败，已拒绝使用。请重试。");
                deadline.Token.ThrowIfCancellationRequested();
                return new PreparedInstaller(destination, guard, cache);
            }
            catch { guard.Dispose(); throw; }
        }
        catch { cache.DeleteDownload(folder); throw; }
    }

    private static HttpRequestMessage Request(string url)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.UserAgent.ParseAdd("Host2VMRelay/" + CurrentVersion.ToString(3));
        return request;
    }
}
