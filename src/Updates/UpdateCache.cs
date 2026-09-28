using System.Diagnostics;

namespace Host2VMRelay;

/// <summary>Owns only GUID-named download folders under the application's temporary cache.</summary>
internal sealed class UpdateCache(string root)
{
    public static string DefaultRoot => Path.Combine(Path.GetTempPath(), "Host2VMRelay", "Updates");
    public string Root { get; } = Path.GetFullPath(root);

    public string CreateDownload()
    {
        Directory.CreateDirectory(Root);
        if (IsReparsePoint(Root)) throw new IOException("更新缓存目录不能是符号链接。");
        string folder = Path.Combine(Root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        return folder;
    }

    public void Cleanup()
    {
        try
        {
            if (!Directory.Exists(Root) || IsReparsePoint(Root)) return;
            foreach (string folder in Directory.EnumerateDirectories(Root))
            {
                if (!Owns(folder) || IsReparsePoint(folder)) continue;
                if (File.Exists(Path.Combine(folder, "launched")) || Directory.GetLastWriteTimeUtc(folder) < DateTime.UtcNow.AddDays(-7))
                    DeleteDownload(folder);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Locked installers are retried on the next startup. */ }
    }

    public void DeleteDownload(string folder)
    {
        try
        {
            if (!Owns(folder) || !Directory.Exists(folder) || IsReparsePoint(Root) || IsReparsePoint(folder)) return;
            // Never recurse or follow links in a shared temporary directory.
            foreach (string file in Directory.EnumerateFiles(folder))
            {
                string name = Path.GetFileName(file);
                if (!IsReparsePoint(file) && (name == "download.part" ||
                    System.Text.RegularExpressions.Regex.IsMatch(name, @"^Host2VMRelay-[0-9]+\.[0-9]+\.[0-9]+-win-(x64|x86|arm64)-Setup\.exe$")))
                    File.Delete(file);
            }
            // Keep the marker if a locked installer prevented cleanup, so the
            // next launch retries immediately instead of waiting for expiry.
            string marker = Path.Combine(folder, "launched");
            if (File.Exists(marker) && !IsReparsePoint(marker)) File.Delete(marker);
            Directory.Delete(folder, false);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }

    private bool Owns(string folder) => Path.GetDirectoryName(Path.GetFullPath(folder))?.Equals(Root, StringComparison.OrdinalIgnoreCase) == true &&
        Guid.TryParseExact(Path.GetFileName(folder), "N", out _);
    private static bool IsReparsePoint(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
}

internal sealed class PreparedInstaller(string path, FileStream guard, UpdateCache cache) : IDisposable
{
    private bool launched;
    private bool disposed;
    public string FilePath { get; } = path;

    public void Launch() => Launch(file =>
    {
        using var process = Process.Start(new ProcessStartInfo(file) { UseShellExecute = true });
        if (process is null) throw new IOException("安装程序未能启动。");
    });

    internal void Launch(Action<string> start)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (launched) throw new InvalidOperationException("安装程序已启动。");
        start(FilePath);
        launched = true;
        try { File.WriteAllText(Path.Combine(Path.GetDirectoryName(FilePath)!, "launched"), ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* Age-based cleanup remains available. */ }
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        guard.Dispose();
        if (!launched) cache.DeleteDownload(Path.GetDirectoryName(FilePath)!);
    }
}
