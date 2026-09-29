using System.Diagnostics;

namespace Host2VMRelay;

public sealed partial class MainForm
{
    private void BuildSettings()
    {
        var page = PageContent("设置");
        UiLayout.Add(page, BuildScheduleSettings());
        UiLayout.Add(page, BuildCalendarSettings());
        var current = new TextBox { Name = "settingsFolder", ReadOnly = true, Text = Settings.Folder };
        var notice = UiLayout.Help("迁移已保存的设置。未保存的规则请先保存；旧目录不会删除。");
        var open = UiLayout.Button("打开目录", 110);
        var choose = UiLayout.Primary("更改目录…", 125);
        var reset = UiLayout.Button("恢复默认", 110);
        open.Click += (_, _) =>
        {
            try
            {
                if (!Directory.Exists(Settings.Folder)) throw new DirectoryNotFoundException("配置目录不存在：" + Settings.Folder);
                Process.Start(new ProcessStartInfo { FileName = Settings.Folder, UseShellExecute = true, Verb = "open" });
                notice.Text = "已请求在文件资源管理器中打开配置目录。";
            }
            catch (Exception ex) { Error(ex); }
        };
        choose.Click += (_, _) =>
        {
            if (!CanMoveProfile()) return;
            using var picker = new FolderBrowserDialog { Description = "选择配置保存目录", UseDescriptionForTitle = true,
                SelectedPath = Settings.Folder, ShowNewFolderButton = true };
            if (picker.ShowDialog(this) == DialogResult.OK) MoveProfile(picker.SelectedPath);
            else notice.Text = "已取消更改目录。";
        };
        reset.Click += (_, _) => { if (CanMoveProfile()) MoveProfile(Settings.DefaultFolder); };
        UiLayout.Add(page, UiLayout.Card("配置存储", "连接信息、加密凭据、规则和主机指纹保存在 settings.json。",
            UiLayout.Field("当前配置目录", current), UiLayout.Actions(open, choose, reset), notice));
        var startup = new StartupRegistration();
        string executable = Environment.ProcessPath ?? throw new IOException("无法确定当前程序路径。");
        var autoStart = new CheckBox { Name = "startWithWindows", Text = "开机自启动（登录 Windows 后运行）", AutoSize = true };
        var silentStart = new CheckBox { Name = "silentStart", Text = "静默启动（仅显示托盘图标）", AutoSize = true, Checked = settings.SilentStart };
        var startupStatus = UiLayout.Help("两个选项独立生效，默认关闭。静默启动后可双击托盘图标打开窗口；启用定时连接时将按时段自动连接。");
        bool changingStartup = false;
        try { autoStart.Checked = startup.IsEnabled(executable); }
        catch (Exception ex) { autoStart.Enabled = false; startupStatus.Text = "无法读取启动项：" + ex.Message; }
        autoStart.CheckedChanged += (_, _) =>
        {
            if (changingStartup) return;
            try
            {
                startup.SetEnabled(autoStart.Checked, executable);
                startupStatus.Text = autoStart.Checked ? "已启用登录启动。Windows 的“启动应用”设置仍可禁用此启动项。" : "已关闭开机自启动。";
            }
            catch (Exception ex)
            {
                changingStartup = true; autoStart.Checked = !autoStart.Checked; changingStartup = false;
                startupStatus.Text = "启动项保存失败：" + ex.Message;
            }
        };
        silentStart.CheckedChanged += (_, _) =>
        {
            if (changingStartup) return;
            try
            {
                settings = settings.SaveUpdated(s => s.SilentStart = silentStart.Checked);
                startupStatus.Text = silentStart.Checked ? "已保存。下次启动仅显示托盘图标，当前窗口保持打开。" : "已保存。下次启动显示主窗口。";
            }
            catch (Exception ex)
            {
                changingStartup = true; silentStart.Checked = settings.SilentStart; changingStartup = false;
                startupStatus.Text = "静默启动设置保存失败：" + ex.Message;
            }
        };
        UiLayout.Add(page, UiLayout.Card("启动方式", "选项修改后立即保存。开机自启动只影响当前 Windows 用户，无需管理员权限。", autoStart, silentStart, startupStatus));
        var scale = new NumericUpDown { Name = "uiScalePercent", Minimum = Settings.MinUiScalePercent, Maximum = Settings.MaxUiScalePercent, Increment = 5, Value = settings.UiScalePercent };
        var font = new NumericUpDown { Name = "fontSizePoints", Minimum = Settings.MinFontSizePoints, Maximum = Settings.MaxFontSizePoints, Increment = .5m, DecimalPlaces = 1, Value = settings.FontSizePoints };
        var preview = UiLayout.Help("预览：连接已就绪 · Host2VMRelay 123456");
        Font? previewFont = null;
        void Preview()
        {
            var previous = previewFont;
            previewFont = new Font("Microsoft YaHei UI", (float)font.Value);
            preview.Font = previewFont; previous?.Dispose();
        }
        font.ValueChanged += (_, _) => Preview(); Preview();
        preview.Disposed += (_, _) => previewFont?.Dispose();
        var appearanceStatus = UiLayout.Help("缩放调整控件和间距；字体大小独立设置。保存后下次启动生效，当前连接不会中断。仍会跟随 Windows 显示器缩放。");
        var saveAppearance = UiLayout.Primary("保存显示设置", 160);
        var resetAppearance = UiLayout.Button("恢复默认显示", 160);
        void SaveAppearance()
        {
            try
            {
                settings = settings.SaveUpdated(s => { s.UiScalePercent = (int)scale.Value; s.FontSizePoints = font.Value; });
                saveAppearance.ShowFeedback("✓ 已保存");
                appearanceStatus.Text = "显示设置已保存，下次启动生效。当前连接保持运行；可在方便时从托盘退出并重新打开软件。";
            }
            catch (Exception ex) { appearanceStatus.Text = "保存失败：" + ex.Message; }
        }
        saveAppearance.Click += (_, _) => SaveAppearance();
        resetAppearance.Click += (_, _) => { scale.Value = 100; font.Value = 9m; SaveAppearance(); };
        UiLayout.Add(page, UiLayout.Card("界面与缩放", "按阅读习惯分别调整内容尺寸与字体。100% 对应当前默认界面。",
            UiLayout.Pair(UiLayout.Field("界面缩放（%）", scale), UiLayout.Field("字体大小（pt）", font)),
            preview, UiLayout.Actions(saveAppearance, resetAppearance), appearanceStatus));

        var notifications = new CheckBox { Text = "连接状态变化时显示托盘通知", AutoSize = true, Checked = settings.ShowConnectionNotifications };
        var requestLogs = new CheckBox { Text = "记录转发请求（包含目标地址）", AutoSize = true, Checked = settings.LogForwardingRequests };
        var reconnectDelay = new NumericUpDown { Minimum = 5, Maximum = 120, Value = settings.ReconnectDelaySeconds };
        var connectionLimit = new NumericUpDown { Name = "concurrentConnectionLimit", Minimum = Settings.MinConcurrentConnections,
            Maximum = Settings.MaxConcurrentConnections, Increment = 64, Value = settings.ConcurrentConnectionLimit };
        var behaviorStatus = UiLayout.Help("通知和请求日志保存后立即生效。关闭请求日志仍保留连接状态及 SSH 错误；重连间隔从下一次连接尝试结束后使用。");
        var saveBehavior = UiLayout.Primary("保存运行设置", 160);
        saveBehavior.Click += (_, _) =>
        {
            try
            {
                settings = settings.SaveUpdated(s => { s.ShowConnectionNotifications = notifications.Checked; s.LogForwardingRequests = requestLogs.Checked; s.ReconnectDelaySeconds = (int)reconnectDelay.Value; s.ConcurrentConnectionLimit = (int)connectionLimit.Value; });
                saveBehavior.ShowFeedback("✓ 已保存"); behaviorStatus.Text = "运行设置已保存。并发上限在下次连接时生效，当前连接保持运行。";
            }
            catch (Exception ex) { behaviorStatus.Text = "保存失败：" + ex.Message; }
        };
        UiLayout.Add(page, UiLayout.Card("运行与通知", "减少不必要的提示，并控制诊断记录的详细程度。",
            notifications, requestLogs,
            UiLayout.Pair(UiLayout.Field("自动重连间隔（秒）", reconnectDelay), UiLayout.Field("并发连接上限", connectionLimit)),
            UiLayout.Help("默认 512，范围 64～2048。TCP 连接与 UDP 关联共用名额；达到上限时拒绝新请求。内存或虚拟机资源有限时可调低，下次连接生效。"),
            UiLayout.Actions(saveBehavior), behaviorStatus));
        UiLayout.Add(page, UiLayout.Help("设置保存在上方显示的当前配置目录，可通过“更改目录”迁移。Clash 规则文件由 Clash 单独管理，不随本配置迁移。"));
        bool CanMoveProfile()
        {
            if (!busy && !wanted && session?.IsConnected != true) return true;
            notice.Text = "请先断开隧道，再更改配置目录。";
            return false;
        }
        void MoveProfile(string target)
        {
            try
            {
                if (SettingsLocation.SameFolder(Settings.Folder, target)) { notice.Text = "已经在使用此目录。"; return; }
                bool exists = File.Exists(Path.Combine(target, "settings.json"));
                string warning = exists ? "目标目录已有 settings.json，将先备份再用当前已保存的配置替换。" : "将把当前已保存的配置写入新目录。";
                if (MessageBox.Show(this, warning + "\n原目录保留。未保存的编辑不在迁移范围内。\n\n" + target,
                    "确认迁移配置", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2) != DialogResult.Yes) return;
                settings.ChangeFolder(target, exists); current.Text = Settings.Folder;
                notice.Text = "已切换目录，下次启动继续使用；原目录保留为备份。"; Log("配置保存目录已更新。");
            }
            catch (Exception ex) { notice.Text = "迁移未完成，活动配置目录未切换。请检查路径和写入权限。"; Error(ex); }
        }
    }
}
