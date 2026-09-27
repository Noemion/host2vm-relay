#ifndef PayloadRoot
  #define PayloadRoot "..\artifacts\publish"
#endif
#ifndef OutputRoot
  #define OutputRoot "..\artifacts\release"
#endif
#ifndef AppVersion
  #define AppVersion "0.6.4"
#endif
#ifndef AppArch
  #define AppArch "x64"
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
#if AppArch == "x64"
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
#elif AppArch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#elif AppArch == "x86"
ArchitecturesAllowed=x86os
#else
  #error Unsupported AppArch
#endif
OutputDir={#OutputRoot}
OutputBaseFilename=Host2VMRelay-{#AppVersion}-win-{#AppArch}-Setup
Compression=lzma2/fast
SolidCompression=no
SetupIconFile=..\artifacts\assets\Host2VMRelay.Setup.ico
UninstallDisplayIcon={app}\Host2VMRelay.exe
CloseApplications=yes
RestartApplications=no
AppMutex=Local\Host2VMRelay.Desktop
SetupLogging=yes
VersionInfoDescription=Host2VMRelay {#AppArch} installer

[Languages]
Name: "zhcn"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PayloadRoot}\installer\win-{#AppArch}\Host2VMRelay.exe"; DestDir: "{app}"; Flags: ignoreversion; BeforeInstall: BeforeFileInstall; AfterInstall: AfterFileInstall
Source: "{#PayloadRoot}\common\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs; BeforeInstall: BeforeFileInstall; AfterInstall: AfterFileInstall

[Icons]
Name: "{group}\Host2VMRelay"; Filename: "{app}\Host2VMRelay.exe"; BeforeInstall: RecordShortcut('{group}\Host2VMRelay')
Name: "{autodesktop}\Host2VMRelay"; Filename: "{app}\Host2VMRelay.exe"; Tasks: desktopicon; BeforeInstall: RecordShortcut('{autodesktop}\Host2VMRelay')

[Run]
Filename: "{app}\Host2VMRelay.exe"; Description: "{cm:LaunchProgram,Host2VMRelay}"; Flags: nowait postinstall skipifsilent

#define UninstallRegistryKey "Software\Microsoft\Windows\CurrentVersion\Uninstall\{B49F96CE-C602-4C52-A415-61A77A0B4BE7}_is1"
#include "RuntimeRequirement.iss"
#include "InstallerFlow.iss"
#include "InstallerAppearance.iss"
