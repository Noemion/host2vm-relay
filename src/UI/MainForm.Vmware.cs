namespace Host2VMRelay;

public sealed partial class MainForm
{
    private Control BuildVmwareNetworkHelp()
    {
        var path = new TextBox { ReadOnly = true, Name = "vmwareConfigurationPath" };
        var browse = UiLayout.Button("选择虚拟机配置", 180);
        var repair = UiLayout.Button("关机后应用修复", 180);
        repair.Enabled = false;
        var result = UiLayout.Help("若切换 TUN 后 SSH 仍连接、内网却等待十几秒才恢复，请检查 VMware NAT 网卡是否同时断链。");
        VmwareNatConfiguration? configuration = null;
        browse.Click += (_, _) =>
        {
            using var dialog = new OpenFileDialog { Filter = "VMware 配置 (*.vmx)|*.vmx", CheckFileExists = true };
            if (dialog.ShowDialog(this) != DialogResult.OK) return;
            try
            {
                configuration = new VmwareNatConfiguration(dialog.FileName);
                path.Text = dialog.FileName;
                result.Text = configuration.Inspect();
                repair.Enabled = true;
            }
            catch (Exception ex) { configuration = null; repair.Enabled = false; result.Text = ex.Message; }
        };
        repair.Click += (_, _) =>
        {
            if (configuration is null) return;
            try
            {
                string backup = configuration.Repair();
                result.Text = backup.Length == 0 ? configuration.Inspect() :
                    "已关闭 NAT 网络状态传播。现在可启动虚拟机，再连接 VPN 和中继并复测 TUN。备份：" + backup;
                Log(result.Text);
            }
            catch (Exception ex) { result.Text = ex.Message; }
        };
        return UiLayout.Card("VMware NAT 稳定性", "修复仅调整所选虚拟机的 NAT 网络状态传播，自动备份配置。需要完全关机后应用，不会关闭或重启虚拟机。",
            path, UiLayout.Actions(browse, repair), result);
    }
}
