[CmdletBinding()]
param(
    [string]$DotNet = 'dotnet',
    [string]$Iscc = '',
    [string]$OutputDirectory = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts'),
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$project = Join-Path $repoRoot 'src/Host2VMRelay.csproj'
[xml]$projectXml = Get-Content -LiteralPath $project
$version = [string]$projectXml.Project.PropertyGroup.Version
$OutputDirectory = [IO.Path]::GetFullPath($OutputDirectory)
$publishRoot = Join-Path $OutputDirectory 'publish'
$releaseRoot = Join-Path $OutputDirectory 'release'
if (!$SkipInstaller -and !$Iscc) {
    $command = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($command) { $Iscc = $command.Source }
    else {
        foreach ($parent in @($env:ProgramFiles, ${env:ProgramFiles(x86)}, (Join-Path $env:LOCALAPPDATA 'Programs'))) {
            if (!$parent) { continue }
            $candidate = Join-Path $parent 'Inno Setup 7\ISCC.exe'
            if (Test-Path -LiteralPath $candidate) { $Iscc = $candidate; break }
        }
    }
    if (!$Iscc) { throw 'Inno Setup 7 was not found. Pass -Iscc or use -SkipInstaller for portable packages.' }
}
New-Item -ItemType Directory -Force $publishRoot,$releaseRoot | Out-Null
foreach ($rid in @('win-x64','win-x86','win-arm64')) {
    $target = Join-Path $publishRoot $rid
    if (Test-Path -LiteralPath $target) { Remove-Item -LiteralPath $target -Recurse -Force }
    & $DotNet publish $project -c Release -r $rid -p:PublishProfile=Standalone -o $target
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $rid" }
    if (!(Test-Path -LiteralPath (Join-Path $target 'Host2VMRelay.exe'))) { throw "Missing executable: $rid" }
    $unexpected = Get-ChildItem -LiteralPath $target -File | Where-Object { $_.Extension -in '.dll','.pdb','.json' }
    if ($unexpected) { throw "Loose dependency files found in $target." }
}
$common = Join-Path $publishRoot 'common'
if (Test-Path -LiteralPath $common) { Remove-Item -LiteralPath $common -Recurse -Force }
New-Item -ItemType Directory -Force $common | Out-Null
Copy-Item -LiteralPath (Join-Path $repoRoot 'licenses') -Destination $common -Recurse -Force
Copy-Item -LiteralPath (Join-Path $repoRoot 'docs') -Destination $common -Recurse -Force
foreach ($doc in @('README.md','CHANGELOG.md')) { Copy-Item -LiteralPath (Join-Path $repoRoot $doc) -Destination $common -Force }
foreach ($rid in @('win-x64','win-x86','win-arm64')) {
    Compress-Archive -Path (Join-Path $publishRoot "$rid\Host2VMRelay.exe"), (Join-Path $common '*') -DestinationPath (Join-Path $releaseRoot "Host2VMRelay-$version-$rid-Portable.zip") -Force
}
if (!$SkipInstaller) {
    & $Iscc "/DAppVersion=$version" "/DPayloadRoot=$publishRoot" "/DOutputRoot=$releaseRoot" (Join-Path $repoRoot 'packaging/Host2VMRelay.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
    & powershell.exe -NoProfile -NonInteractive -ExecutionPolicy Bypass -File (Join-Path $repoRoot 'scripts\test-installer-icon.ps1') -Executable (Join-Path $releaseRoot "Host2VMRelay-$version-win-universal-Setup.exe")
    if ($LASTEXITCODE -ne 0) { throw 'Installer shell icon validation failed' }
}
Get-ChildItem -LiteralPath $releaseRoot -File | Where-Object { $_.Name -like "Host2VMRelay-$version-*" -and $_.Extension -in '.exe','.zip' } | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } |
    Set-Content -LiteralPath (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Release artifacts: $releaseRoot"
