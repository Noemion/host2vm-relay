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
    if (!$process.WaitForExit(60000)) {
        $process.Kill()
        throw "Application check timed out: $Arguments"
    }
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
    foreach ($scale in @(100,125,150,200)) {
        $folder = Join-Path $checks "ui-$scale"
        New-Item -ItemType Directory -Force $folder | Out-Null
        $image = Join-Path $folder 'main.png'
        Invoke-AppCheck "--smoke --ui-scale=$scale `"$image`"" ([IO.Path]::ChangeExtension($image, '.txt'))
    }
}
Write-Host "Checks completed: $checks"
