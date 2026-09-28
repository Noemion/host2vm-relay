namespace Host2VMRelay;

internal static class VmExecutionGuide
{
    public const string DisableCommand = "sudo /usr/sbin/setstatus -f exectl off -p";
    public const string Allow = "麒麟虚拟机若弹出执行授权窗口，请核对程序后点击“允许”。辅助程序更新后可能再次请求授权。";
    public const string Disable = "若希望关闭后续执行授权提示，可在虚拟机终端执行下方命令。此操作永久关闭系统级执行控制，影响所有程序，重启后仍生效；请确认符合你的设备管理要求。";
    public const string Recovery = "完成授权或关闭执行控制后，自动重连会尝试恢复 UDP；也可手动断开后重新连接。";
    public const string FailureHint = "若麒麟虚拟机正在请求执行授权，请在虚拟机弹窗中允许；也可在虚拟机终端执行 " + DisableCommand + "，永久关闭所有程序的执行控制。详见连接页“麒麟执行授权”。";
}
