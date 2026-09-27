using System.Text;

namespace Host2VMRelay;

public sealed partial class MainForm
{
    private void BuildLog()
    {
        var page = PageContent("运行日志");
        var copyLog = UiLayout.Button("复制日志", 120); var exportLog = UiLayout.Button("导出日志", 120); var clearLog = UiLayout.Button("清空", 90);
        clearLog.Name = "clearLog"; log.AccessibleName = "运行日志内容";
        var counter = UiLayout.Help("连接与操作发生后，记录将显示在这里。"); counter.Name = "logSummary";
        log.TextChanged += (_, _) =>
        {
            copyLog.Enabled = exportLog.Enabled = clearLog.Enabled = log.TextLength > 0;
            counter.Text = log.TextLength == 0 ? "暂无日志" : log.GetLineFromCharIndex(log.TextLength - 1) + 1 + " 行记录 · 仅保留最近日志";
        };
        var logFeedback = UiLayout.Help("");
        var pause = UiLayout.Button("暂停显示", 120);
        pause.Name = "pauseLog";
        pause.Click += (_, _) =>
        {
            pauseLogDisplay = !pauseLogDisplay;
            pause.Text = pauseLogDisplay ? "继续显示" : "暂停显示";
            logFeedback.Text = pauseLogDisplay
                ? "已暂停显示，转发继续运行。后台最多保留 1000 条待显示记录。"
                : "已恢复显示。选中文字时会暂停刷新，取消选择后继续。";
        };
        copyLog.Click += (_, _) =>
        {
            try
            {
                if (log.TextLength == 0) { logFeedback.Text = "暂无可复制的日志。"; return; }
                Clipboard.SetText(log.Text); copyLog.ShowFeedback("✓ 已复制"); logFeedback.Text = "复制成功，日志已复制到剪贴板。";
            }
            catch (Exception ex) { logFeedback.Text = "复制失败，请重试。"; Error(ex); }
        };
        exportLog.Click += (_, _) =>
        {
            using var dialog = new SaveFileDialog { Title = "导出运行日志", Filter = "文本文件|*.txt", FileName = "Host2VMRelay-" + DateTime.Now.ToString("yyyyMMdd-HHmmss") + ".txt", InitialDirectory = Settings.Folder };
            if (dialog.ShowDialog(this) != DialogResult.OK) { logFeedback.Text = "已取消导出。"; return; }
            try { File.WriteAllText(dialog.FileName, log.Text, new UTF8Encoding(false)); logFeedback.Text = "导出成功：" + dialog.FileName; } catch (Exception ex) { logFeedback.Text = "导出失败，请检查目标路径。"; Error(ex); }
        };
        clearLog.Click += (_, _) => { pendingLogs.Clear(); log.Clear(); logFeedback.Text = "日志已清空。"; };
        UiLayout.Add(page, UiLayout.Card("活动记录", "选中文字时暂停刷新，便于查看和复制。", UiLayout.Actions(pause, copyLog, exportLog, clearLog), UiLayout.Editor(log, 380, true), counter, logFeedback));
        UiLayout.Add(page, UiLayout.Help("分享日志前请检查其中的主机地址、用户名等信息。清空仅影响当前日志，不会清除连接配置。"));
    }
}
