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
        using var client = ClashControlClient.Open();
        foreach (string name in new[] { "host2vm-relay-rules", "host2vm-relay-udp-rules" })
        {
            using var response = await client.PutAsync("http://localhost/providers/rules/" + name, null);
            response.EnsureSuccessStatusCode();
        }
    }
}
