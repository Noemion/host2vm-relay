namespace Host2VMRelay;

/// <summary>One status snapshot shared by the badge, tray and inline feedback.</summary>
internal sealed record ConnectionPresentation(string Key, string Caption, string TrayText, Color Color,
    ConnectionIconState Icon, string Details)
{
    public static ConnectionPresentation Create(bool connected, RelayHealth health, bool enableUdp, string? error)
    {
        // A cached forwarding lease cannot make a closed SSH session connected.
        if (!connected || !health.Tcp)
            return new("host", "● 连接失败", "虚拟机连接失败", System.Drawing.Color.FromArgb(173, 49, 43),
                ConnectionIconState.Failed, error ?? "虚拟机连接不可用，请检查地址、认证信息和运行日志。");
        if (enableUdp && !health.Udp)
            return new("tcp-only", "● UDP 连接失败 · TCP 可用", "UDP 连接失败 · TCP 可用", System.Drawing.Color.FromArgb(145, 83, 0),
                ConnectionIconState.Degraded, error ?? "UDP 未通过健康检查，请检查虚拟机的安全授权及辅助程序。");
        return new(enableUdp ? "both" : "tcp", enableUdp ? "● 已连接 · TCP / UDP" : "● 已连接 · TCP",
            enableUdp ? "已连接 · TCP / UDP" : "已连接 · TCP", System.Drawing.Color.FromArgb(20, 105, 70),
            ConnectionIconState.Connected, enableUdp ? "TCP / UDP 优先虚拟机 · 不可用时回退原有分流" : "TCP 经虚拟机 · UDP 使用宿主机原有分流");
    }
}
