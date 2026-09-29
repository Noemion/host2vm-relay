namespace Host2VMRelay;

public sealed partial class MainForm
{
    private Control BuildDnsExceptionHelp()
    {
        const string example = """
dns:
  fake-ip-filter-mode: rule
  fake-ip-filter:
    - DOMAIN,vpn.example.com,real-ip
    # 在这里保留已有的其他过滤项
    - MATCH,fake-ip
""";
        var details = UiLayout.Stack();
        details.Name = "dnsExceptionInstructions";
        var toggle = UiLayout.Button("查看配置方式", 170);
        toggle.Name = "toggleDnsExceptionGuide";
        var copy = UiLayout.Button("复制配置示例", 170);
        copy.Name = "copyDnsExceptionExample";
        var feedback = UiLayout.Help("");
        UiLayout.Add(details, UiLayout.Help("适用场景：VPN 登录网关与内网站点共用域名后缀，被宽泛的转发规则一起纳入 Fake-IP，导致 VPN 登录或恢复异常。普通内网站点不需要添加此例外。"));
        UiLayout.Add(details, UiLayout.Help("填写位置：Clash Verge → 订阅 → 全局扩展覆写配置。将下例合并到已有 dns 段，把 vpn.example.com 换成 VPN 登录网关的精确域名，不填协议、端口或路径。"));
        var code = UiLayout.Help(example);
        code.Font = UiLayout.CodeFont();
        UiLayout.Add(details, code);
        UiLayout.Add(details, UiLayout.Help("不要直接替换已有过滤列表；保留原有项，并将网关例外放在宽泛匹配和 MATCH 之前。若原配置是 blacklist 模式，只需在已有 fake-ip-filter 列表中添加网关域名，无需照抄 rule 示例。"));
        UiLayout.Add(details, UiLayout.Help("保存并重新应用 Clash 配置；旧脚本需更新为当前版本。该例外仅改变 DNS 返回真实 IP，不等于直连规则或 TUN 路由排除。结合规则应用情况、实际解析和 VPN 登录结果验证，不必盲目重启内核。"));
        UiLayout.Add(details, UiLayout.Actions(copy));
        UiLayout.Add(details, feedback);
        toggle.Click += (_, _) =>
        {
            details.Visible = !details.Visible;
            toggle.Text = details.Visible ? "收起配置说明" : "查看配置方式";
        };
        copy.Click += (_, _) =>
        {
            try
            {
                Clipboard.SetText(example);
                copy.ShowFeedback("✓ 已复制");
                feedback.Text = "示例已复制，尚未应用。请替换示例域名，并与原有 DNS 配置合并。";
            }
            catch (Exception ex) { feedback.Text = ex.Message; }
        };
        var card = UiLayout.Card("VPN 网关 DNS 例外（可选）", "只有 VPN 登录网关被内网 Fake-IP 规则包含时才需要。例外统一保存在 Clash 覆写配置中。",
            UiLayout.Actions(toggle), details);
        details.Visible = false;
        return card;
    }
}
