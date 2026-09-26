using System.Diagnostics;

namespace Host2VMRelay;

public sealed partial class MainForm
{
    private void BuildSettings()
    {
        var page = PageContent("设置");
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
            }
            catch (Exception ex) { Error(ex); }
        };
        choose.Click += (_, _) =>
        {
            if (!CanMoveProfile()) return;
            using var picker = new FolderBrowserDialog { Description = "选择配置保存目录", UseDescriptionForTitle = true,
                SelectedPath = Settings.Folder, ShowNewFolderButton = true };
            if (picker.ShowDialog(this) == DialogResult.OK) MoveProfile(picker.SelectedPath);
        };
        reset.Click += (_, _) => { if (CanMoveProfile()) MoveProfile(Settings.DefaultFolder); };
        UiLayout.Add(page, UiLayout.Card("配置存储", "连接信息、加密凭据、规则和主机指纹保存在 settings.json。",
            UiLayout.Field("当前配置目录", current), UiLayout.Actions(open, choose, reset), notice));
        UiLayout.Add(page, UiLayout.Card("界面与缩放", "更紧凑的字号、输入框、按钮和卡片间距。",
            UiLayout.Help("应用内容尺寸为旧版的约 80%，继续跟随 Windows 当前显示器的 DPI。系统缩放、标题栏和系统文件对话框不变。")));
        UiLayout.Add(page, UiLayout.Help("配置目录定位文件仅保存路径，位于 LocalAppData/Host2VMRelay/storage.json。Clash 的规则文件仍位于 Clash 数据目录，不随本配置迁移。"));
        bool CanMoveProfile()
        {
            if (!busy && !wanted && client?.IsConnected != true) return true;
            MessageBox.Show(this, "请先断开隧道，再更改配置目录。", "配置存储", MessageBoxButtons.OK, MessageBoxIcon.Information);
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
