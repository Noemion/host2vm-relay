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
    & (Join-Path $PSScriptRoot 'test-dpi.ps1') -Executable $Executable -Scale @(100,125,150,175,200)
    if ($env:GITHUB_ACTIONS -eq 'true') {
        & (Join-Path $PSScriptRoot 'test-dpi.ps1') -Executable $Executable -Native -Scale 100
        # Negative test: a 96-DPI runner must refuse native 200% certification.
        $guardDir = Join-Path $checks 'native-200-guard'
        New-Item -ItemType Directory -Force $guardDir | Out-Null
        $guardImage = Join-Path $guardDir 'report.png'
        $guardJson = [IO.Path]::ChangeExtension($guardImage, '.json')
        if (Test-Path -LiteralPath $guardJson) { Remove-Item -LiteralPath $guardJson -Force }
        $guard = Start-Process -FilePath $Executable -ArgumentList "--smoke --native-dpi --ui-scale=200 --monitor=0 `"$guardImage`"" -PassThru
        if (!$guard.WaitForExit(60000)) { $guard.Kill(); throw 'Native DPI guard timed out.' }
        $guard.Refresh()
        if (!(Test-Path -LiteralPath $guardJson)) { throw 'Native DPI guard produced no evidence.' }
        $result = Get-Content -LiteralPath $guardJson -Raw | ConvertFrom-Json
        if ($guard.ExitCode -ne 2 -or $result.Status -ne 'BLOCKED' -or $result.Stages[0].NativeDpi -ne 96) {
            throw 'Native DPI guard incorrectly accepted a mismatched display scale.'
        }
        Write-Host 'PASS native-200 guard: 96-DPI desktop was correctly reported as BLOCKED, not certified as native 200%.'
    }
}
Write-Host "Checks completed: $checks"
