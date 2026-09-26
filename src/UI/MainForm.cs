using System.Security.Cryptography;
using Renci.SshNet;

namespace Host2VMRelay;

public sealed partial class MainForm : Form
{
    private readonly Settings settings;
    private SshClient? client;
    private ForwardedPortDynamic? forward;
    private PrivateKeyFile? keyFile;
    private readonly TextBox host = new(), user = new(), secret = new(), keyPath = new(), ruleText = new(), log = new();
    private readonly NumericUpDown port = new() { Minimum = 1, Maximum = 65535 }, socksPort = new() { Minimum = 1024, Maximum = 65535 };
    private readonly ComboBox auth = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    private Control? keyControls;
    private readonly CheckBox remember = new() { Text = "加密保存密码 / 私钥口令（仅当前 Windows 用户可解密）", AutoSize = true }, retry = new() { Text = "连接断开后自动重连", AutoSize = true };
    private readonly Button connect = UiLayout.Button("连接虚拟机", 160), disconnect = UiLayout.Button("断开", 90);
    private readonly Label state = new() { AutoSize = true, Text = "● 未连接", ForeColor = Color.DimGray }, feed = UiLayout.Help("Clash 本地规则 · 未启用");
    private readonly NotifyIcon tray = new() { Text = "Host2VMRelay" };
    private Icon? windowIcon, trayIcon;
    private readonly System.Windows.Forms.Timer timer = new() { Interval = 1500 };
    private bool busy, wanted, quitting;
    private DateTime nextRetry;
    private string existingClashScript = "";
    private readonly TabControl tabs = new() { Dock = DockStyle.Fill };

    public MainForm()
    {
        settings = Settings.Load();
        SuspendLayout();
        Text = "Host2VMRelay";
        Font = UiLayout.BodyFont();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        ClientSize = new Size(960, 740); MinimumSize = new Size(680, 520);
        BackColor = Color.FromArgb(245, 247, 251); StartPosition = FormStartPosition.CenterScreen;
        disconnect.Enabled = false;
        UpdateIcons(96);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(20), RowCount = 3, ColumnCount = 1 };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
        var heading = new TableLayoutPanel { Dock = DockStyle.Top, AutoSize = true, ColumnCount = 1, Margin = new Padding(0, 0, 0, 12) };
        heading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var title = UiLayout.Help("Host2VMRelay"); title.Font = new Font(Font.FontFamily, 22F, FontStyle.Bold, GraphicsUnit.Point);
        heading.Controls.Add(title);
        heading.Controls.Add(UiLayout.Help("虚拟机 SSH 隧道 · Clash 分流 · 托盘后台运行"));
        UiLayout.WrapLabels(heading);
        layout.Controls.Add(heading, 0, 0); layout.Controls.Add(tabs, 0, 1); layout.Controls.Add(feed, 0, 2);
        UiLayout.WrapLabels(layout); Controls.Add(layout);
        BuildConnection(); BuildRules(); BuildClash(); BuildLog();
        host.Text = settings.Host; port.Value = settings.Port; user.Text = settings.User; socksPort.Value = settings.SocksPort;
        keyPath.Text = settings.KeyPath; auth.SelectedIndex = settings.UseKey ? 1 : 0;
        remember.Checked = settings.RememberSecret; retry.Checked = settings.Reconnect; ruleText.Text = settings.Rules.Replace("\n", Environment.NewLine);
        try { secret.Text = SecretStore.Unprotect(settings.ProtectedSecret); }
        catch { Log("保存的密码无法在当前 Windows 用户下解密，请重新输入。"); }
        try { ClashRuleFile.Disable(); }
        catch (Exception ex) { Log("无法初始化 Clash 本地规则文件：" + ex.Message); }
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开主窗口", null, (_, _) => Restore());
        menu.Items.Add("连接", null, async (_, _) => { if (!busy && client?.IsConnected != true) await Connect(); });
        menu.Items.Add("断开", null, (_, _) => Stop());
        menu.Items.Add("退出", null, (_, _) =>
        {
            if (busy) { Restore(); MessageBox.Show(this, "正在连接，请等待本次连接完成后退出。"); return; }
            quitting = true; Close();
        });
        tray.ContextMenuStrip = menu; tray.DoubleClick += (_, _) => Restore(); tray.Visible = true;
        connect.Click += async (_, _) => await Connect(); disconnect.Click += (_, _) => Stop();
        FormClosing += (_, e) =>
        {
            // Do not cancel Windows shutdown; close-to-tray applies only to a user closing the window.
            if (!quitting && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true; Hide();
                tray.ShowBalloonTip(2000, "Host2VMRelay", "已缩到托盘，后台连接继续运行。右键托盘图标可退出。", ToolTipIcon.Info);
            }
        };
        FormClosed += (_, _) =>
        {
            wanted = false; timer.Stop(); Cleanup(); TryDisableRules();
            tray.Dispose(); menu.Dispose(); timer.Dispose(); windowIcon?.Dispose(); trayIcon?.Dispose();
        };
        timer.Tick += async (_, _) =>
        {
            bool connected = client?.IsConnected == true;
            feed.Text = connected ? "Clash 本地规则 · 已启用" : "Clash 本地规则 · 未启用";
            if (wanted && !busy && !connected)
            {
                TryDisableRules(); SetConnectionControls(false);
                if (DateTime.UtcNow >= nextRetry && retry.Checked) await Connect(true);
                else { state.Text = retry.Checked ? "● 连接断开，等待重连" : "● 连接已断开"; state.ForeColor = Color.DarkOrange; }
            }
        };
        Shown += (_, _) =>
        {
            UpdateIcons(DeviceDpi); UiLayout.FitToScreen(this, new Size(680, 520));
            Log("已初始化 Clash 本地规则。先连接虚拟机，再到“接入 Clash”生成完整扩展脚本。");
        };
        DpiChanged += (_, e) =>
        {
            UpdateIcons(e.DeviceDpiNew);
            BeginInvoke(() => UiLayout.FitToScreen(this, new Size(680, 520)));
        };
        ResumeLayout(true); timer.Start();
    }

    private void UpdateIcons(int dpi)
    {
        var large = AppIcon.Load(Math.Max(16, 32 * dpi / 96));
        var small = AppIcon.Load(Math.Max(16, 16 * dpi / 96));
        Icon = large; tray.Icon = small;
        windowIcon?.Dispose(); trayIcon?.Dispose(); windowIcon = large; trayIcon = small;
    }

    private void Log(string text)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        if (InvokeRequired) { try { BeginInvoke(() => Log(text)); } catch (InvalidOperationException) { } return; }
        log.AppendText(DateTime.Now.ToString("HH:mm:ss") + "  " + text + Environment.NewLine);
        if (log.TextLength > 60000) log.Text = log.Text[^40000..];
    }
    private void Error(Exception ex) { Log(ex.Message); MessageBox.Show(this, ex.Message, "操作失败", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
    private void TryDisableRules() { try { ClashRuleFile.Disable(); } catch (Exception ex) { Log("停用 Clash 本地规则失败：" + ex.Message); } }

}
