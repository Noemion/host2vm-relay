using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Host2VMRelay;

public sealed partial class MainForm
{
    private const int AboutPageIndex = 5;
    private int aboutReturnPage;
    private readonly HttpClient updateHttp = ReleaseUpdater.CreateClient();
    private readonly UpdateCache updateCache = new(UpdateCache.DefaultRoot);
    private CancellationTokenSource? updateOperation;
    private ReleaseUpdate? availableUpdate;
    private Label updateStatus = null!;
    private ActionButton checkUpdate = null!, installUpdate = null!, cancelUpdate = null!;
    private ProgressBar downloadProgress = null!;
    internal PreparedInstaller? PendingInstaller { get; private set; }

    private void BuildAbout()
    {
        var page = PageContent("关于");
        var back = UiLayout.Button("← 返回", 110); back.Emphasized = true; back.Name = "backFromAbout";
        back.Click += (_, _) => SelectPage(aboutReturnPage);
        UiLayout.Add(page, UiLayout.Actions(back));
        var github = UiLayout.Button("打开 GitHub", 150); github.Name = "openRepository";
        var releasePage = UiLayout.Button("查看发布说明", 170);
        github.Click += (_, _) => OpenProjectLink(ReleaseUpdater.RepositoryUrl, github);
        releasePage.Click += (_, _) => OpenProjectLink(availableUpdate?.PageUrl ?? ReleaseUpdater.RepositoryUrl + "/releases/latest", releasePage);
        UiLayout.Add(page, UiLayout.Card("Host2VMRelay", "通过 SSH 将选定的网络请求转发到虚拟机。",
            UiLayout.Help("当前版本：v" + ReleaseUpdater.CurrentVersion.ToString(3)),
            UiLayout.Actions(github, releasePage)));
        checkUpdate = UiLayout.Button("检查更新", 130); checkUpdate.Name = "checkUpdate";
        installUpdate = UiLayout.Primary("下载并升级", 160); installUpdate.Enabled = false; installUpdate.Name = "installUpdate";
        cancelUpdate = UiLayout.Button("取消下载", 130); cancelUpdate.Visible = false;
        updateStatus = UiLayout.Help("点击“检查更新”，查看 GitHub 上的最新正式版本。"); updateStatus.Name = "updateStatus";
        downloadProgress = new ProgressBar { Minimum = 0, Maximum = 100, Height = UiTheme.Units(14), Visible = false,
            AccessibleName = "安装包下载进度", Margin = UiTheme.Spacing(0, 4, 0, 12) };
        checkUpdate.Click += async (_, _) => await CheckForUpdateAsync();
        installUpdate.Click += async (_, _) => await InstallUpdateAsync();
        cancelUpdate.Click += (_, _) => { updateOperation?.Cancel(); updateStatus.Text = "正在取消下载…"; };
        UiLayout.Add(page, UiLayout.Card("软件更新", "下载与当前 Windows 系统架构匹配的安装包。升级时会退出本软件，中断当前转发连接，并打开安装界面。",
            UiLayout.Actions(checkUpdate, installUpdate, cancelUpdate), updateStatus, downloadProgress,
            UiLayout.Help("安装包暂存于系统临时目录。下载失败或取消时清理；下次启动时清理已使用的安装包及超过 7 天的旧下载。"),
            UiLayout.Help("安装版需要对应架构的 .NET 8 桌面运行时。便携版也通过安装程序升级，原便携目录保留。")));
        FormClosed += (_, _) => { updateOperation?.Cancel(); updateHttp.Dispose(); };
    }

    internal void ShowUpdateError(string message)
    {
        SelectPage(AboutPageIndex);
        updateStatus.Text = message;
    }

    private void OpenProjectLink(string url, ActionButton button)
    {
        try { using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); button.ShowFeedback("✓ 已打开"); }
        catch (Exception ex) { updateStatus.Text = "无法打开浏览器：" + ex.Message; }
    }

    private async Task CheckForUpdateAsync()
    {
        if (updateOperation is not null) return;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(formLifetime.Token);
        updateOperation = operation;
        checkUpdate.Enabled = installUpdate.Enabled = false;
        availableUpdate = null;
        updateStatus.Text = "正在检查最新正式版本…";
        try
        {
            var release = await new ReleaseUpdater(updateHttp).CheckAsync(RuntimeInformation.OSArchitecture, operation.Token);
            if (IsDisposed) return;
            if (release.Version > ReleaseUpdater.CurrentVersion)
            {
                availableUpdate = release;
                updateStatus.Text = $"发现新版本 {release.Tag} · 安装包 {release.Size / 1048576.0:F1} MB。点击“下载并升级”开始。";
            }
            else updateStatus.Text = "当前已是最新正式版本。";
        }
        catch (Exception ex) { if (!IsDisposed) updateStatus.Text = ex is OperationCanceledException ? "检查更新超时或已取消，请重试。" : "检查更新失败：" + ex.Message; }
        finally
        {
            updateOperation = null;
            if (!IsDisposed) { checkUpdate.Enabled = true; installUpdate.Enabled = availableUpdate is not null; }
        }
    }

    private async Task InstallUpdateAsync()
    {
        if (updateOperation is not null || availableUpdate is not { } release) return;
        if (busy || polling) { updateStatus.Text = "连接操作正在进行，请完成后再点击升级。"; return; }
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(formLifetime.Token);
        updateOperation = operation;
        checkUpdate.Enabled = installUpdate.Enabled = false; cancelUpdate.Visible = downloadProgress.Visible = true;
        downloadProgress.Value = 0;
        updateStatus.Text = "正在下载安装包…";
        PreparedInstaller? installer = null;
        bool stopping = false;
        try
        {
            var progress = new Progress<int>(percent =>
            {
                if (IsDisposed || updateOperation != operation || operation.IsCancellationRequested || stopping) return;
                downloadProgress.Value = percent;
                updateStatus.Text = percent == 100 ? "下载完成，正在校验安装包…" : $"正在下载 {release.Tag} · {percent}%";
            });
            installer = await new ReleaseUpdater(updateHttp).DownloadAsync(release, updateCache, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (busy || polling) throw new InvalidOperationException("连接操作正在进行，请完成后再点击升级。");
            stopping = true;
            cancelUpdate.Visible = false;
            updateStatus.Text = "校验通过，正在关闭连接并准备安装…";
            busy = true; wanted = false; timer.Stop(); SetConnectionControls(false);
            TryDisableRules();
            await Cleanup().WaitAsync(TimeSpan.FromSeconds(15), operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            PendingInstaller = installer; installer = null;
            quitting = true; Close();
        }
        catch (Exception ex)
        {
            if (!IsDisposed) updateStatus.Text = ex is OperationCanceledException
                ? (operation.IsCancellationRequested ? "已取消升级。" : "下载超时，请检查网络后重试。")
                : "升级未完成：" + ex.Message;
        }
        finally
        {
            installer?.Dispose(); updateOperation = null;
            if (!IsDisposed)
            {
                if (stopping) { busy = false; timer.Start(); SetConnectionControls(false); }
                checkUpdate.Enabled = installUpdate.Enabled = true; cancelUpdate.Visible = downloadProgress.Visible = false;
            }
        }
    }
}
