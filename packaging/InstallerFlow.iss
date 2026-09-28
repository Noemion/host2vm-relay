[Code]
var
  PreparationPage: TOutputProgressWizardPage;
  PhaseText, VersionText: String;
  ProgressActive: Boolean;
  Preparing: Boolean;
  PreviousFiles: TStringList;
  PreviousVersionRemoved: Boolean;
  InstallActions: TNewMemo;
  UpgradeLogPath: String;
  UpgradeLogLines: Integer;
  UpgradeTimer: UINT_PTR;
  ReadingUpgradeLog: Boolean;
  InstallationProgressBase: Integer;
  InstallProgress: TNewProgressBar;
  ReportedProgress: Integer;

procedure RefreshProgressHeader;
begin
  if not ProgressActive then Exit;
  if Preparing then
  begin
    PreparationPage.Description := VersionText;
    PreparationPage.SetText(PhaseText,
      CustomMessage('TotalProgress') + IntToStr(ReportedProgress div 10) + '%');
  end
  else if WizardForm.CurPageID = wpInstalling then
    WizardForm.PageDescriptionLabel.Caption := VersionText + ' · ' +
      CustomMessage('TotalProgress') + IntToStr(ReportedProgress div 10) + '%';
end;

procedure SetPhase(const Text: String);
begin
  PhaseText := Text;
  RefreshProgressHeader;
end;

procedure SetVersion(const Text: String);
begin
  VersionText := Text;
  RefreshProgressHeader;
end;

procedure ReportProgress(Value: Integer);
begin
  // Both standard pages share one monotonic total. The engine updates and
  // paints its byte counter before calling CurInstallProgressChanged, so keep
  // that hidden counter separate from the total shown at its stock bounds.
  if Value <= ReportedProgress then Exit;
  if Value > 1000 then Value := 1000;
  ReportedProgress := Value;
  if Preparing then PreparationPage.SetProgress(Value, 1000)
  else InstallProgress.Position := Value;
  RefreshProgressHeader;
end;

function SetTimer(hWnd: HWND; nIDEvent: UINT_PTR; uElapse: UINT; lpTimerFunc: NativeInt): UINT_PTR;
  external 'SetTimer@user32.dll stdcall';
function KillTimer(hWnd: HWND; nIDEvent: UINT_PTR): Boolean;
  external 'KillTimer@user32.dll stdcall';

function CreateActionLog(Parent: TWinControl; Top, Width, Height: Integer): TNewMemo;
begin
  Result := TNewMemo.Create(WizardForm);
  Result.Parent := Parent;
  Result.SetBounds(0, Top, Width, Height);
  Result.ReadOnly := True;
  Result.ScrollBars := ssBoth;
  Result.WordWrap := False;
  Result.Anchors := [akLeft, akTop, akRight, akBottom];
end;

procedure RecordAction(const Text: String);
begin
  InstallActions.Lines.Add(Text);
  // Keep the on-screen history bounded; the setup log retains all operations.
  if InstallActions.Lines.Count > 500 then InstallActions.Lines.Delete(0);
  InstallActions.SelStart := Length(InstallActions.Text);
  SendMessage(InstallActions.Handle, $00B7, 0, 0);
  Log(Text);
end;

procedure ReadUpgradeLog;
var
  Lines: TStringList;
  Data: AnsiString;
  CompleteLength: Integer;
  Line: String;
begin
  if (UpgradeLogPath = '') or not LoadStringFromLockedFile(UpgradeLogPath, Data) then Exit;
  // A concurrent writer may have emitted only part of its last UTF-8 line.
  CompleteLength := Length(Data);
  while (CompleteLength > 0) and (Data[CompleteLength] <> #10) do Dec(CompleteLength);
  Lines := TStringList.Create;
  try
    Lines.Text := UTF8Decode(Copy(Data, 1, CompleteLength));
    while UpgradeLogLines < Lines.Count do
    begin
      Line := Lines[UpgradeLogLines];
      Inc(UpgradeLogLines);
      if Trim(Line) <> '' then RecordAction(Line);
    end;
  finally
    Lines.Free;
  end;
end;

// Count existing files before launching the old uninstaller. Never follow
// junctions: the inventory must stay inside the old installation directory.
procedure InventoryPreviousFiles(const Directory: String);
var
  Entry: TFindRec;
  Path: String;
begin
  if FindFirst(AddBackslash(Directory) + '*', Entry) then
  try
    repeat
      if (Entry.Name <> '.') and (Entry.Name <> '..') and
         ((Entry.Attributes and $400) = 0) then
      begin
        Path := AddBackslash(Directory) + Entry.Name;
        if (Entry.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then
          InventoryPreviousFiles(Path)
        else
          PreviousFiles.Add(Path);
      end;
    until not FindNext(Entry);
  finally
    FindClose(Entry);
  end;
end;

procedure UpgradeLogTimer(hWnd: HWND; Msg: UINT; TimerID: UINT_PTR; Time: DWORD);
var
  I, Removed: Integer;
begin
  // Standard progress pages pump messages. Do not reenter a slow inventory
  // scan when SetProgress processes another timer message.
  if ReadingUpgradeLog then Exit;
  ReadingUpgradeLog := True;
  try
    ReadUpgradeLog;
    Removed := 0;
    for I := 0 to PreviousFiles.Count - 1 do
      if not FileExists(PreviousFiles[I]) then Inc(Removed);
    // Reserve the end of this phase until the uninstaller reports success.
    if PreviousFiles.Count > 0 then
      ReportProgress(50 + MulDiv(Removed, 190, PreviousFiles.Count));
  finally
    ReadingUpgradeLog := False;
  end;
end;

procedure BeforeFileInstall;
begin
  RecordAction(CustomMessage('ActionWrite') + ' ' + ExpandConstant(CurrentFileName));
end;

procedure AfterFileInstall;
begin
  RecordAction(CustomMessage('ActionWritten') + ' ' + ExpandConstant(CurrentFileName));
end;

procedure RecordShortcut(const Path: String);
begin
  RecordAction(CustomMessage('ActionShortcut') + ' ' + ExpandConstant(Path) + '.lnk');
end;

// Add only a log below the framework's progress controls. The wizard keeps
// ownership of page margins, headers, fonts, images and navigation buttons.
procedure AttachActionLog(Parent: TWinControl; Gauge: TNewProgressBar);
var
  DetailsTop: Integer;
begin
  DetailsTop := Gauge.Top + Gauge.Height + ScaleY(12);
  InstallActions.Parent := Parent;
  InstallActions.SetBounds(Gauge.Left, DetailsTop, Gauge.Width,
    Parent.ClientHeight - DetailsTop);
end;

procedure InitializeWizard;
begin
  PreviousFiles := TStringList.Create;
  VersionText := 'Host2VMRelay {#AppVersion}';
  PreparationPage := CreateOutputProgressPage(SetupMessage(msgWizardPreparing), VersionText);
  InstallProgress := TNewProgressBar.Create(WizardForm);
  InstallProgress.Parent := WizardForm.InstallingPage;
  InstallProgress.SetBounds(WizardForm.ProgressGauge.Left, WizardForm.ProgressGauge.Top,
    WizardForm.ProgressGauge.Width, WizardForm.ProgressGauge.Height);
  InstallProgress.Anchors := WizardForm.ProgressGauge.Anchors;
  InstallProgress.Max := 1000;
  WizardForm.ProgressGauge.Visible := False;
  InstallActions := CreateActionLog(WizardForm.InstallingPage, 0, 0, 0);
  AttachActionLog(WizardForm.InstallingPage, InstallProgress);
  ReportedProgress := -1;
  ReportProgress(0);
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  if CurPageID = wpReady then
    ShowInstallationConfirmation
  else if CurPageID = wpInstalling then
  begin
    ProgressActive := True;
    // Preserve the total and operation history across the native page change.
    AttachActionLog(WizardForm.InstallingPage, InstallProgress);
    InstallProgress.Position := ReportedProgress;
    RefreshProgressHeader;
  end
  else if CurPageID = wpFinished then
    ProgressActive := False;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
  begin
    if CheckForMutexes('Local\Host2VMRelay.Desktop') then
    begin
      SetPhase(CustomMessage('ActionClose'));
      RecordAction(CustomMessage('ActionClose'));
    end;
  end
  else if CurStep = ssPostInstall then
  begin
    SetPhase(CustomMessage('ActionComplete'));
    ReportProgress(1000);
    RecordAction(CustomMessage('ActionComplete'));
  end;
end;

procedure CurInstallProgressChanged(CurProgress, MaxProgress: Integer);
begin
  // Preparation and old-version removal occupy 0..250. Reserve the last
  // 5% for shortcuts, registry entries and other finalization, so a completed
  // byte counter never advertises success before ssPostInstall. Ignore the
  // engine's initial zero callback until the old-version removal has finished.
  if PreviousVersionRemoved and (MaxProgress > 0) then
  begin
    ReportProgress(InstallationProgressBase +
      MulDiv(CurProgress, 950 - InstallationProgressBase, MaxProgress));
  end;
end;

// Register the old location too when the user changes the installation path.
// The framework owns the application list, close/cancel choice and shutdown.
procedure RegisterExtraCloseApplicationsResources;
begin
  if PreviousUninstaller <> '' then
  begin
    RegisterExtraCloseApplicationsResource(
      AddBackslash(ExtractFileDir(PreviousUninstaller)) + 'Host2VMRelay.exe');
    RegisterExtraCloseApplicationsResource(
      AddBackslash(ExtractFileDir(PreviousUninstaller)) + 'native\h2vm-core.exe');
  end;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  Result := '';
  ProgressActive := True;
  Preparing := True;
  PreparationPage.SetProgress(ReportedProgress, 1000);
  AttachActionLog(PreparationPage.Surface, PreparationPage.ProgressBar);
  PreparationPage.Show;
  try
    SetPhase(CustomMessage('ActionPrerequisites'));
    // Do not close the application or remove files until prerequisites pass.
    if not HasDesktopRuntime then
    begin
      Result := CustomMessage('RuntimeMissing');
      RecordAction(Result);
      Exit;
    end;
    ReportProgress(30);
    RecordAction(CustomMessage('ActionDetect'));
    InstallationProgressBase := 250;
    DetectInstallation;
    SetVersion(InstallationSummary);
    // Returning lets the native Preparing page ask to close running apps.
    // Removing the old version here would run before Restart Manager shutdown.
  finally
    Preparing := False;
    PreparationPage.Hide;
    ProgressActive := False;
  end;
end;

procedure RemovePreviousVersion;
var
  Command, Failure: String;
  ResultCode: Integer;
  Started: Boolean;
begin
  if PreviousVersionRemoved then Exit;
  // A user can decline automatic closure, or Windows may fail to close an
  // elevated/unresponsive instance. Never uninstall or overwrite in that case.
  if CheckForMutexes('Local\Host2VMRelay.Desktop') then
    RaiseException(CustomMessage('ApplicationStillRunning'));
  if PreviousUninstaller <> '' then
  begin
    SetPhase(FmtMessage(CustomMessage('PreparingOperation'), [CustomMessage(InstallationOperation)]));
    ReportProgress(50);
    RecordAction(PhaseText);
    UpgradeLogPath := ExpandConstant('{tmp}\previous-version-uninstall.log');
    DeleteFile(UpgradeLogPath);
    UpgradeLogLines := 0;
    PreviousFiles.Clear;
    InventoryPreviousFiles(ExtractFileDir(PreviousUninstaller));
    Command := '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /LOG="' + UpgradeLogPath + '"';
    RecordAction(CustomMessage('ActionCommand') + ' "' + PreviousUninstaller + '" ' + Command);
    try
      UpgradeTimer := SetTimer(0, 0, 250, CreateCallback(@UpgradeLogTimer));
      Started := Exec(PreviousUninstaller, Command, '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
      ReadUpgradeLog;
      Failure := '';
      if not Started then
        Failure := FmtMessage(CustomMessage('UpgradeLaunchFailed'), [SysErrorMessage(ResultCode)])
      else if ResultCode <> 0 then
        Failure := FmtMessage(CustomMessage('UpgradeRemoveFailed'), [IntToStr(ResultCode)]);
      if Failure <> '' then
      begin
        RecordAction(Failure);
        RaiseException(Failure);
      end;
      RecordAction(CustomMessage('ActionPrepared'));
    finally
      if UpgradeTimer <> 0 then KillTimer(0, UpgradeTimer);
      UpgradeTimer := 0;
      ReadUpgradeLog;
    end;
  end
  else if InstallationOperation = 'OperationNew' then
    RecordAction(CustomMessage('ActionFresh'));
  PreviousVersionRemoved := True;
  ReportProgress(250);
  SetPhase(CustomMessage('ActionInstall'));
  RecordAction(CustomMessage('ActionInstall'));
end;

function InitializeUninstall: Boolean;
begin
  // AppMutex would block Setup before its close-applications page. Preserve
  // the same protection for a standalone uninstall through this event instead.
  Result := True;
  while CheckForMutexes('Local\Host2VMRelay.Desktop') do
    if SuppressibleMsgBox(CustomMessage('UninstallApplicationRunning'),
      mbError, MB_RETRYCANCEL, IDCANCEL) <> IDRETRY then
    begin
      Result := False;
      Exit;
    end;
end;

// User configuration and its location pointer are intentionally retained.

[CustomMessages]
zhcn.TotalProgress=总进度：
en.TotalProgress=Overall progress:
zhcn.ActionPrerequisites=正在检查安装条件。
en.ActionPrerequisites=Checking installation requirements.
zhcn.ActionWrite=正在写入文件：
zhcn.ActionWritten=文件处理完成：
zhcn.ActionShortcut=正在创建快捷方式：
zhcn.ActionCommand=正在执行命令：
en.ActionWrite=Writing file:
en.ActionWritten=File processed:
en.ActionShortcut=Creating shortcut:
en.ActionCommand=Executing command:
zhcn.ActionDetect=正在检测已有版本。
zhcn.ActionPrepared=已有版本处理完成，现有配置已保留。
zhcn.ActionFresh=未检测到已有版本，将进行首次安装。
zhcn.ActionInstall=正在安装文件和创建快捷方式。
zhcn.ActionComplete=安装完成。
en.ActionDetect=Checking for an existing version.
en.ActionPrepared=Previous version removed. Existing settings have been retained.
en.ActionFresh=No existing version found. Starting a new installation.
en.ActionInstall=Installing files and creating shortcuts.
en.ActionComplete=Installation complete.
zhcn.PreparingOperation=正在准备%1……
zhcn.UpgradeLaunchFailed=无法启动旧版本卸载程序：%1。请处理后重试。
zhcn.UpgradeRemoveFailed=旧版本卸载失败（退出代码：%1）。安装已停止，请处理后重试。
en.PreparingOperation=Preparing to %1...
en.UpgradeLaunchFailed=Could not start the previous version's uninstaller: %1. Resolve the problem and retry.
en.UpgradeRemoveFailed=The previous version could not be removed (exit code: %1). Installation has stopped. Resolve the problem and retry.

zhcn.ActionClose=正在关闭运行中的应用。当前转发连接将断开。
en.ActionClose=Closing running applications. Active relay connections will be disconnected.
zhcn.ApplicationStillRunning=Host2VMRelay 仍在运行，尚未移除旧版本。请重新运行安装程序，并允许安装向导自动关闭应用。
en.ApplicationStillRunning=Host2VMRelay is still running. The previous version has not been removed. Run Setup again and allow the wizard to close the application automatically.
zhcn.UninstallApplicationRunning=Host2VMRelay 仍在运行。请退出应用后点击“重试”，或取消卸载。
en.UninstallApplicationRunning=Host2VMRelay is still running. Exit the application and click Retry, or cancel uninstall.
