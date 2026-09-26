#ifndef PayloadRoot
  #define PayloadRoot "..\artifacts\publish"
#endif
#ifndef OutputRoot
  #define OutputRoot "..\artifacts\release"
#endif
#ifndef AppVersion
  #define AppVersion "0.5.0"
#endif

[Setup]
AppId={{B49F96CE-C602-4C52-A415-61A77A0B4BE7}
AppName=Host2VMRelay
AppVersion={#AppVersion}
AppPublisher=Host2VMRelay contributors
AppComments=Selective host-to-VM forwarding through SSH and Clash TUN
DefaultDirName={localappdata}\Programs\Host2VMRelay
DefaultGroupName=Host2VMRelay
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
MinVersion=10.0.14393
SetupArchitecture=x86
ArchitecturesInstallIn64BitMode=x64os or arm64
OutputDir={#OutputRoot}
OutputBaseFilename=Host2VMRelay-{#AppVersion}-Setup
Compression=lzma2/fast
SolidCompression=no
WizardStyle=modern
SetupIconFile=..\artifacts\assets\Host2VMRelay.Setup.ico
UninstallDisplayIcon={app}\Host2VMRelay.exe
CloseApplications=yes
RestartApplications=no
AppMutex=Local\Host2VMRelay.Desktop
SetupLogging=yes
VersionInfoDescription=Host2VMRelay offline installer

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadRoot}\win-x64\Host2VMRelay.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: IsX64OS
Source: "{#PayloadRoot}\win-x86\Host2VMRelay.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: IsX86OS
Source: "{#PayloadRoot}\win-arm64\Host2VMRelay.exe"; DestDir: "{app}"; Flags: ignoreversion; Check: IsArm64
Source: "{#PayloadRoot}\common\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\Host2VMRelay"; Filename: "{app}\Host2VMRelay.exe"
Name: "{autodesktop}\Host2VMRelay"; Filename: "{app}\Host2VMRelay.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Host2VMRelay.exe"; Description: "{cm:LaunchProgram,Host2VMRelay}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
var
  UninstallString: String;
  ResultCode: Integer;
begin
  Result := True;
  if RegQueryStringValue(
       HKCU,
       'Software\Microsoft\Windows\CurrentVersion\Uninstall\{B49F96CE-C602-4C52-A415-61A77A0B4BE7}_is1',
       'UninstallString',
       UninstallString) then
  begin
    if not Exec(
         RemoveQuotes(UninstallString),
         '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS',
         '',
         SW_HIDE,
         ewWaitUntilTerminated,
         ResultCode) or (ResultCode <> 0) then
    begin
      MsgBox('无法卸载现有 Host2VMRelay，请先手动卸载后重试。', mbError, MB_OK);
      Result := False;
    end;
  end;
end;

// User configuration and its location pointer are intentionally retained.
