using System.IO.Pipes;

namespace Host2VMRelay;

/// <summary>Requests provider refresh; service-managed copies may still need client configuration application.</summary>
internal static class ClashRuleRefresh
{
    private static readonly object gate = new();
    private static bool running, pending;
    public static void Request(Action<string> report)
    {
        // Isolated diagnostics write fixture rules and must never refresh the user's core.
        if (!SettingsLocation.SameFolder(ClashRuleFile.Folder, ClashRuleFile.DefaultFolder)) return;
        lock (gate)
        {
            pending = true;
            if (running) return;
            running = true;
        }
        _ = Task.Run(async () =>
        {
            while (true)
            {
                lock (gate)
                {
                    if (!pending) { running = false; return; }
                    pending = false;
                }
                try
                {
                    await RefreshAsync();
                    report(ClashActivationGuide.RulesRefreshed);
                }
                catch (Exception ex)
                {
                    report("规则文件已写入，但 Clash 重新加载失败：" + ex.Message + "。请在 Clash 中重新应用配置。");
                }
            }
        });
    }
    private static async Task RefreshAsync()
    {
        // Clash Verge uses a named-pipe API on Windows. Never open a new TCP
        // controller or modify the user's API authentication settings.
        var pipes = Directory.GetFiles(@"\\.\pipe\")
            .Where(p => Path.GetFileName(p).StartsWith("verge-mihomo-production-", StringComparison.Ordinal)).ToArray();
        if (pipes.Length != 1) throw new IOException("无法唯一确定运行中的 Clash Verge 控制接口");
        string pipeName = Path.GetFileName(pipes[0]);
        using var handler = new SocketsHttpHandler
        {
            ConnectCallback = async (_, token) =>
            {
                var pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
                try { await pipe.ConnectAsync(token); return pipe; }
                catch { pipe.Dispose(); throw; }
            }
        };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(3) };
        foreach (string name in new[] { "host2vm-relay-rules", "host2vm-relay-udp-rules" })
        {
            using var response = await client.PutAsync("http://localhost/providers/rules/" + name, null);
            response.EnsureSuccessStatusCode();
        }
    }
}
