[CmdletBinding()]
param(
    [string]$DotNet = 'dotnet',
    [string]$Iscc = 'ISCC.exe',
    [string]$OutputDirectory = (Join-Path $PSScriptRoot 'artifacts'),
    [switch]$SkipInstaller
)
$ErrorActionPreference = 'Stop'
$project = Join-Path $PSScriptRoot 'Host2VMRelay.csproj'
$publishRoot = Join-Path $OutputDirectory 'publish'
$releaseRoot = Join-Path $OutputDirectory 'release'
New-Item -ItemType Directory -Force $publishRoot,$releaseRoot | Out-Null
foreach ($rid in @('win-x64','win-x86','win-arm64')) {
    $target = Join-Path $publishRoot $rid
    & $DotNet publish $project -c Release -r $rid -p:PublishProfile=Standalone -o $target
    if ($LASTEXITCODE -ne 0) { throw "Publish failed: $rid" }
    if (!(Test-Path (Join-Path $target 'Host2VMRelay.exe'))) { throw "Missing executable: $rid" }
    $unexpected = Get-ChildItem $target -File | Where-Object { $_.Extension -in '.dll','.pdb','.json' }
    if ($unexpected) { throw "Loose dependency files found in $target. Use a clean output directory." }
}
$common = Join-Path $publishRoot 'common'
New-Item -ItemType Directory -Force $common | Out-Null
Copy-Item (Join-Path $PSScriptRoot 'licenses') $common -Recurse -Force
foreach ($doc in @('README.md','DEPLOYMENT.md','CHANGELOG.md','VALIDATION.md')) { Copy-Item (Join-Path $PSScriptRoot $doc) $common -Force }
foreach ($rid in @('win-x64','win-x86','win-arm64')) {
    Compress-Archive -Path (Join-Path $publishRoot "$rid\Host2VMRelay.exe"), (Join-Path $common '*') -DestinationPath (Join-Path $releaseRoot "Host2VMRelay-0.2.0-$rid-Portable.zip") -Force
}
if (!$SkipInstaller) {
    & $Iscc "/DPayloadRoot=$publishRoot" "/DOutputRoot=$releaseRoot" (Join-Path $PSScriptRoot 'installer\Host2VMRelay.iss')
    if ($LASTEXITCODE -ne 0) { throw 'Installer compilation failed' }
}
Get-ChildItem $releaseRoot -File | Where-Object Extension -in '.exe','.zip' | Get-FileHash -Algorithm SHA256 |
    ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } |
    Set-Content (Join-Path $releaseRoot 'SHA256SUMS.txt') -Encoding utf8
Write-Host "Release artifacts: $releaseRoot"
