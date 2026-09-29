namespace Host2VMRelay;

public sealed partial class MainForm
{
    private Control BuildClashRuleStatus()
    {
        var check = UiLayout.Button("检查应用情况", 170);
        check.Name = "checkClashRuleStatus";
        var result = UiLayout.Help("尚未检查。保存规则或重新应用 Clash 配置后，点击检查。");
        result.Name = "clashRuleStatusResult";
        check.Click += async (_, _) =>
        {
            check.Enabled = false;
            result.Text = "正在读取 Clash 内核状态…";
            var lines = new List<string>();
            try
            {
                await ClashRuleStatus.CheckAsync(settings.Rules, session?.Health.Tcp == true,
                    session?.Health.Udp == true && settings.EnableUdp, line =>
                    {
                        if (result.IsDisposed) return;
                        lines.Add(line);
                        result.Text = string.Join(Environment.NewLine, lines);
                    }, formLifetime.Token);
            }
            finally { if (!check.IsDisposed) check.Enabled = true; }
        };
        return UiLayout.Card("Clash 规则应用情况", "核对规则文件、内核规则数量、分流入口和当前策略选择。",
            UiLayout.Actions(check), result);
    }
}
