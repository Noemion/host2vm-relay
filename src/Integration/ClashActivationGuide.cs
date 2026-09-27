namespace Host2VMRelay;

/// <summary>Saving or refreshing a provider does not prove that Clash applied the script.</summary>
internal static class ClashActivationGuide
{
    public const string Steps = "本软件暂不能自动应用 Clash 脚本或重启内核。请在 Clash 当前订阅的扩展脚本中粘贴完整结果，保存并重新应用配置；若仍未生效，请通过 Clash 界面重启内核。服务模式下若仍使用旧规则，请退出并重新打开 Clash，再应用配置。最后重新打开目标网页，在连接列表确认命中 Host2VMRelay。";
    public const string Copied = "已复制，尚未应用到 Clash。请按下方“使脚本生效”说明操作。";
    public const string RulesRefreshed = "Clash 已接受规则刷新请求，但尚未确认最新规则生效。服务模式可能仍读取旧副本；请在 Clash 中重新应用配置，必要时退出并重新打开 Clash。";
}
