#ifndef PayloadRoot
  #define PayloadRoot "..\artifacts\publish"
#endif
#ifndef OutputRoot
  #define OutputRoot "..\artifacts\release"
#endif
#define AppVersion "0.2.0"

[Setup]
AppId={{B49F96CE-C602-4C52-A415-61A77A0B4BE7}
AppName=Host2VM Relay
AppVersion={#AppVersion}
AppPublisher=Host2VM Relay contributors
AppComments=Selective host-to-VM forwarding through SSH and Clash TUN
DefaultDirName={localappdata}\Programs\Host2VMRelay
DefaultGroupName=Host2VM Relay
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
UninstallDisplayIcon={app}\Host2VMRelay.exe
CloseApplications=yes
RestartApplications=no
AppMutex=Local\KylinTunnel.Desktop
SetupLogging=yes
VersionInfoDescription=Host2VM Relay offline installer

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
Name: "{group}\Host2VM Relay"; Filename: "{app}\Host2VMRelay.exe"
Name: "{autodesktop}\Host2VM Relay"; Filename: "{app}\Host2VMRelay.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\Host2VMRelay.exe"; Description: "{cm:LaunchProgram,Host2VM Relay}"; Flags: nowait postinstall skipifsilent

; User settings live in LocalAppData\Host2VMRelay and are intentionally retained.
