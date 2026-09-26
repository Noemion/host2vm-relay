[CmdletBinding()]
param(
    [string]$Executable = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\build\bin\Release\net8.0-windows\Host2VMRelay.exe'),
    [switch]$Smoke
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$checks = Join-Path $repoRoot 'artifacts\checks'
New-Item -ItemType Directory -Force $checks | Out-Null
if (!(Test-Path -LiteralPath $Executable)) { throw "Build the app first: $Executable" }
if (!(Get-Command node -ErrorAction SilentlyContinue)) { throw 'Node.js is required for script regression tests.' }
function Invoke-AppCheck {
    param([string]$Arguments, [string]$ResultPath)
    if (Test-Path -LiteralPath $ResultPath) { Remove-Item -LiteralPath $ResultPath -Force }
    $process = Start-Process -FilePath $Executable -ArgumentList $Arguments -PassThru
    if (!$process.WaitForExit(60000)) { $process.Kill(); throw "Application check timed out: $Arguments" }
    $process.Refresh()
    if (Test-Path -LiteralPath $ResultPath) { Get-Content -LiteralPath $ResultPath }
    if ($process.ExitCode -ne 0 -or !(Test-Path -LiteralPath $ResultPath)) { throw "Application check failed: $Arguments" }
}
$resultPath = Join-Path $checks 'self-test.txt'
Invoke-AppCheck "--self-test `"$resultPath`"" $resultPath
& node (Join-Path $repoRoot 'tests\test-script.cjs') (Join-Path $checks 'mihomo.json') (Join-Path $checks 'script-cases.json')
if ($LASTEXITCODE -ne 0) { throw 'JavaScript regression tests failed.' }
& node (Join-Path $repoRoot 'tests\test-assets.cjs')
if ($LASTEXITCODE -ne 0) { throw 'Icon and DPI configuration checks failed.' }
if ($Smoke) {
    # All five plateaus, including separate 175% and 200% acceptance cases.
    & (Join-Path $PSScriptRoot 'test-dpi.ps1') -Executable $Executable -Scale @(100,125,150,175,200)
    if ($env:GITHUB_ACTIONS -eq 'true') {
        # Hosted runner baseline only. Never label injected 120/144/168/192 DPI as native.
        & (Join-Path $PSScriptRoot 'test-dpi.ps1') -Executable $Executable -Native -Scale 100
    }
}
Write-Host "Checks completed: $checks"
