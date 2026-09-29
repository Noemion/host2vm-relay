using System.Text.Json;

namespace Host2VMRelay;

internal static class ClashRuleStatus
{
    internal static string Normalize(string text) => string.Join("\n", text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        .Where(line => !line.StartsWith('#')));
    internal static int Count(string text) => Normalize(text).Split('\n', StringSplitOptions.RemoveEmptyEntries).Length;
    private static string Text(JsonElement item, string key) => item.TryGetProperty(key, out var value) ? value.ToString() : "";

    internal static string DescribeProvider(string label, string expected, string file, JsonElement provider)
    {
        int count = Count(file);
        string loaded = Text(provider, "ruleCount");
        string result = $"{label}：文件 {count} 条 / 内核 {loaded} 条。";
        if (Normalize(expected) != Normalize(file)) return result + "规则文件与当前应应用内容不一致，请检查连接状态并重新保存规则。";
        if (!int.TryParse(loaded, out int actual)) return result + "无法确认内核规则数量。";
        if (actual != count) return result + "规则未同步：请在 Clash 重新应用配置；仍无效时退出并重新打开 Clash。";
        return result + "数量一致，内容一致性仍未验证。";
    }

    public static async Task CheckAsync(string savedRules, bool tcp, bool udp, Action<string> report, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var token = timeout.Token;
        try
        {
            string compiled = Rules.Compile(savedRules);
            report($"检查时间：{DateTime.Now:HH:mm:ss} · 已保存 {Count(compiled)} 条规则（不含未保存编辑）。");
            using var api = ClashControlClient.Open();
            using var config = await ClashControlClient.ReadAsync(api, "configs", token);
            string mode = Text(config.RootElement, "mode");
            report("Clash 模式：" + mode + (mode == "rule" ? "。" : "；请切换到规则模式，否则分流规则不会按预期使用。"));
            using var rules = await ClashControlClient.ReadAsync(api, "rules", token);
            using var providers = await ClashControlClient.ReadAsync(api, "providers/rules", token);
            using var proxies = await ClashControlClient.ReadAsync(api, "proxies", token);
            foreach (var protocol in ClashRelayProtocol.All)
            {
                string label = protocol.Label, name = protocol.Provider, group = protocol.Group;
                string path = Path.Combine(ClashRuleFile.Folder, protocol.FileName);
                bool enabled = protocol == ClashRelayProtocol.Tcp ? tcp : udp;
                report(label + (enabled ? "：通道就绪，应选择中转。" : "：通道未就绪或未启用，应回退原有分流；目标规则保持不变。"));
                bool entry = rules.RootElement.TryGetProperty("rules", out var entries) && entries.EnumerateArray().Any(r =>
                    Text(r, "proxy") == group && Text(r, "payload").Contains(name, StringComparison.Ordinal) &&
                    (!r.TryGetProperty("extra", out var extra) || extra.ValueKind != JsonValueKind.Object ||
                        !extra.TryGetProperty("disabled", out var disabled) || disabled.ValueKind != JsonValueKind.True));
                report(label + (entry ? " 分流入口：已加载。" : " 分流入口：缺失或禁用，请重新生成并应用扩展脚本。"));
                if (providers.RootElement.TryGetProperty("providers", out var all) && all.TryGetProperty(name, out var provider))
                {
                    try
                    {
                        string file = await File.ReadAllTextAsync(path, token);
                        report(DescribeProvider(label, compiled, file, provider));
                    }
                    catch (IOException ex) { report(label + " 规则文件无法读取：" + ex.Message); }
                    catch (UnauthorizedAccessException ex) { report(label + " 规则文件无法读取：" + ex.Message); }
                }
                else report(label + " 规则集：内核未加载，请重新应用扩展脚本。");
                if (proxies.RootElement.TryGetProperty("proxies", out var groups) && groups.TryGetProperty(group, out var selected))
                {
                    string now = Text(selected, "now");
                    report(label + " 策略组：" + (now == "PASS" ? "PASS，当前回退原有分流。" : now == "Host2VMRelay" ? "已选择 Host2VMRelay。" : "当前选择 " + now + "，需检查策略组配置。"));
                    if (enabled && now == "PASS") report(label + "：通道就绪但尚未恢复中转，请检查动态健康同步日志。");
                    if (!enabled && now == "Host2VMRelay") report(label + "：通道未就绪但策略组仍选择中转，回退尚未确认。");
                }
                else report(label + " 策略组：未加载。");
            }
            report("此结果是检查时的快照。数量一致不代表内容相同；请在“Clash 接入”检测目标路由，确认实际 TCP 转发。");
        }
        catch (OperationCanceledException) { report(cancellationToken.IsCancellationRequested ? "检查已取消。" : "检查超时，未完成项目尚未确认。"); }
        catch (Exception ex) { report("检查未完成：" + ex.Message); }
    }
}
