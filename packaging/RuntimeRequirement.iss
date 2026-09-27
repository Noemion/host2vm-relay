[Code]
function HasFramework(const Root, Framework, RequiredFile: String): Boolean;
var
  Versions: TArrayOfString;
  I, Patch: Integer;
  Version: String;
begin
  Result := False;
  // Official .NET installers register every architecture in the 32-bit view.
  if not RegGetValueNames(HKLM32,
    'SOFTWARE\dotnet\Setup\InstalledVersions\{#AppArch}\sharedfx\' + Framework, Versions) then Exit;
  for I := 0 to GetArrayLength(Versions) - 1 do
  begin
    Version := Versions[I];
    if Copy(Version, 1, 4) = '8.0.' then
    begin
      Patch := StrToIntDef(Copy(Version, 5, Length(Version)), -1);
      // Exclude previews and stale registry entries. The host selects the latest patch.
      if (Patch >= 0) and FileExists(AddBackslash(Root) + 'shared\' + Framework + '\' + Version + '\' + RequiredFile) then
      begin
        Result := True;
        Exit;
      end;
    end;
  end;
end;

function HasDesktopRuntime: Boolean;
var
  Root: String;
begin
  Result := RegQueryStringValue(HKLM32,
    'SOFTWARE\dotnet\Setup\InstalledVersions\{#AppArch}', 'InstallLocation', Root);
  if Result then
    Result := FileExists(AddBackslash(Root) + 'dotnet.exe') and
      HasFramework(Root, 'Microsoft.NETCore.App', 'coreclr.dll') and
      HasFramework(Root, 'Microsoft.WindowsDesktop.App', 'System.Windows.Forms.dll');
end;

[CustomMessages]
zhcn.RuntimeMissing=需要安装 {#AppArch} 版 .NET 8 桌面运行时（8.0.x）。请从 https://dotnet.microsoft.com/download/dotnet/8.0 获取 Windows Desktop Runtime，安装后重试。旧版本尚未卸载。
en.RuntimeMissing=Install the {#AppArch} .NET 8 Windows Desktop Runtime (8.0.x) from https://dotnet.microsoft.com/download/dotnet/8.0, then retry. The previous application has not been removed.
