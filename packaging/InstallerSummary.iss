[Messages]
zhcn.WelcomeLabel1=欢迎安装 Host2VMRelay
en.WelcomeLabel1=Welcome to Host2VMRelay
; Keep the introduction short enough for the stock welcome label. This is
; static text, not a memo or a scrollable custom page.
zhcn.WelcomeLabel2=通过 SSH，将 Windows 上选定的网络流量转发到 Linux 虚拟机。%n%n支持 TCP、UDP，以及 Clash 规则分流。%n%n连接信息和规则保存在本机，凭据由当前 Windows 用户加密保护。%n%n接下来选择安装路径、开始菜单名称和桌面快捷方式，再确认安装。
en.WelcomeLabel2=Forward selected Windows traffic through your Linux virtual machine over SSH.%n%nSupports TCP, UDP and Clash routing rules.%n%nConnection settings stay on this PC. Saved credentials are protected for your Windows account.%n%nChoose an install folder, Start Menu folder and desktop shortcut, then review the installation.

[Code]
var
  PreviousUninstaller, InstalledVersion, InstallationOperation: String;

procedure DetectInstallation;
var
  UninstallString: String;
  InstalledNumber, TargetNumber: Int64;
  Comparison: Integer;
begin
  PreviousUninstaller := '';
  InstalledVersion := '';
  InstallationOperation := 'OperationNew';
  if not RegKeyExists(HKCU, '{#UninstallRegistryKey}') then Exit;
  if RegQueryStringValue(HKCU, '{#UninstallRegistryKey}', 'UninstallString', UninstallString) then
    PreviousUninstaller := RemoveQuotes(UninstallString);
  // An incomplete or unrecognizable registration is not a fresh installation.
  InstallationOperation := 'OperationUnknown';
  if not RegQueryStringValue(HKCU, '{#UninstallRegistryKey}', 'DisplayVersion', InstalledVersion) then Exit;
  if not StrToVersion(InstalledVersion, InstalledNumber) then Exit;
  if not StrToVersion('{#AppVersion}', TargetNumber) then Exit;
  // Compare numeric components: 0.6.10 is newer than 0.6.9; trailing zeros
  // do not turn a reinstall into an upgrade or downgrade.
  Comparison := ComparePackedVersion(InstalledNumber, TargetNumber);
  if Comparison < 0 then InstallationOperation := 'OperationUpgrade'
  else if Comparison = 0 then InstallationOperation := 'OperationReinstall'
  else InstallationOperation := 'OperationDowngrade';
end;

function InstallationSummary: String;
begin
  Result := CustomMessage(InstallationOperation) + ': ';
  if (InstallationOperation <> 'OperationNew') and (InstalledVersion <> '') then
    Result := Result + InstalledVersion + '  →  ';
  Result := Result + '{#AppVersion}';
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo,
  MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
var
  CurrentVersion, RuntimeStatus, ApplicationStatus, DesktopStatus: String;
begin
  // Refresh on each visit, including after Back or an external runtime install.
  DetectInstallation;
  CurrentVersion := InstalledVersion;
  if InstallationOperation = 'OperationNew' then CurrentVersion := CustomMessage('NotInstalled')
  else if CurrentVersion = '' then CurrentVersion := CustomMessage('UnknownVersion');
  if HasDesktopRuntime then RuntimeStatus := CustomMessage('RuntimeReady')
  else RuntimeStatus := CustomMessage('RuntimeNotReady');
  if CheckForMutexes('Local\Host2VMRelay.Desktop') then ApplicationStatus := CustomMessage('ApplicationRunning')
  else ApplicationStatus := CustomMessage('ApplicationStopped');
  if WizardIsTaskSelected('desktopicon') then DesktopStatus := CustomMessage('DesktopEnabled')
  else DesktopStatus := CustomMessage('DesktopDisabled');
  Result := FmtMessage(CustomMessage('DetectedVersion'), [CurrentVersion]) + NewLine +
    FmtMessage(CustomMessage('TargetVersion'), ['{#AppVersion}', '{#AppArch}']) + NewLine +
    FmtMessage(CustomMessage('DetectedOperation'), [CustomMessage(InstallationOperation)]) + NewLine +
    RuntimeStatus + NewLine + ApplicationStatus + NewLine + NewLine +
    MemoDirInfo + NewLine + NewLine + MemoGroupInfo + NewLine + NewLine + DesktopStatus;
  if InstallationOperation = 'OperationDowngrade' then
    Result := Result + NewLine + NewLine + CustomMessage('DowngradeNotice');
  if InstallationOperation = 'OperationUnknown' then
    Result := Result + NewLine + NewLine + CustomMessage('UnknownVersionNotice');
end;

procedure ShowInstallationConfirmation;
begin
  WizardForm.PageNameLabel.Caption := FmtMessage(CustomMessage('ConfirmOperation'), [CustomMessage(InstallationOperation)]);
  WizardForm.PageDescriptionLabel.Caption := CustomMessage('ConfirmDescription');
  WizardForm.NextButton.Caption := CustomMessage(InstallationOperation);
end;

[CustomMessages]
zhcn.OperationNew=首次安装
en.OperationNew=Install
zhcn.OperationUpgrade=升级
en.OperationUpgrade=Upgrade
zhcn.OperationReinstall=重新安装
en.OperationReinstall=Reinstall
zhcn.OperationDowngrade=降级
en.OperationDowngrade=Downgrade
zhcn.OperationUnknown=覆盖安装
en.OperationUnknown=Replace
zhcn.ConfirmOperation=确认%1
en.ConfirmOperation=Confirm: %1
zhcn.ConfirmDescription=请核对检测结果和安装选项。
en.ConfirmDescription=Review the detected state and installation options.
zhcn.DetectedVersion=已安装版本：%1
en.DetectedVersion=Installed version: %1
zhcn.TargetVersion=本次版本：%1（%2）
en.TargetVersion=Target version: %1 (%2)
zhcn.DetectedOperation=本次操作：%1
en.DetectedOperation=Operation: %1
zhcn.NotInstalled=未安装
en.NotInstalled=Not installed
zhcn.UnknownVersion=无法识别
en.UnknownVersion=Unknown
zhcn.RuntimeReady=.NET 8 桌面运行时（{#AppArch}）：已就绪
en.RuntimeReady=.NET 8 Desktop Runtime ({#AppArch}): ready
zhcn.RuntimeNotReady=.NET 8 桌面运行时（{#AppArch}）：缺失，需先安装
en.RuntimeNotReady=.NET 8 Desktop Runtime ({#AppArch}): missing; install it first
zhcn.ApplicationRunning=应用正在运行；下一步可选择自动关闭，转发连接将断开。
en.ApplicationRunning=App running; the next step offers to close it and disconnect the relay.
zhcn.ApplicationStopped=应用未运行。
en.ApplicationStopped=App not running.
zhcn.DesktopEnabled=桌面快捷方式：创建
en.DesktopEnabled=Desktop shortcut: create
zhcn.DesktopDisabled=桌面快捷方式：不创建
en.DesktopDisabled=Desktop shortcut: do not create
zhcn.DowngradeNotice=将安装较早版本。配置会保留，但旧版可能不支持新增设置。
en.DowngradeNotice=Installing an older version. Settings are retained but newer options may not be supported.
zhcn.UnknownVersionNotice=已有安装记录，但版本无法识别。请确认是否覆盖安装。
en.UnknownVersionNotice=An installation record exists, but its version is unknown. Confirm before replacing it.
