using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Host2VMRelay;

/// <summary>Uses the public latest-release redirect and checksum asset, without scraping HTML or using API credentials.</summary>
internal sealed class PublicReleaseSource(HttpClient http)
{
    public async Task<ReleaseUpdate> CheckAsync(Architecture architecture, CancellationToken token)
    {
        using var latest = ReleaseUpdater.Request(ReleaseUpdater.RepositoryUrl + "/releases/latest");
        using var page = await http.SendAsync(latest, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        page.EnsureSuccessStatusCode();
        string location = page.RequestMessage?.RequestUri?.AbsoluteUri ?? "";
        string prefix = ReleaseUpdater.RepositoryUrl + "/releases/tag/";
        if (!location.StartsWith(prefix, StringComparison.Ordinal))
            throw new IOException("公开发布页未返回本项目的正式版本地址，请稍后重试。");
        string tag = location[prefix.Length..];
        string name = ReleaseUpdater.AssetName(tag, architecture);
        string root = ReleaseUpdater.RepositoryUrl + "/releases/download/" + tag + "/";
        using var request = ReleaseUpdater.Request(root + "SHA256SUMS.txt");
        using var manifest = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        manifest.EnsureSuccessStatusCode();
        await manifest.Content.LoadIntoBufferAsync(128 * 1024).WaitAsync(token).ConfigureAwait(false);
        string digest = ReadDigest(await manifest.Content.ReadAsStringAsync(token).ConfigureAwait(false), name);
        using var head = ReleaseUpdater.Request(root + name);
        head.Method = HttpMethod.Head;
        using var asset = await http.SendAsync(head, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
        asset.EnsureSuccessStatusCode();
        return ReleaseUpdater.CreateRelease(tag, architecture, asset.Content.Headers.ContentLength ?? 0, "sha256:" + digest, root + name);
    }

    internal static string ReadDigest(string text, string name)
    {
        var matches = Regex.Matches(text, @"(?m)^([a-fA-F0-9]{64})[ \t]+\*?" + Regex.Escape(name) + @"\r?$");
        if (matches.Count != 1) throw new IOException("公开发布的 SHA256SUMS.txt 缺少唯一有效的安装包校验值，已拒绝升级。");
        return matches[0].Groups[1].Value;
    }
}
