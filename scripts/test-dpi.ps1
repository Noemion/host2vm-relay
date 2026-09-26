[CmdletBinding()]
param(
    [string]$Executable = (Join-Path (Split-Path $PSScriptRoot -Parent) 'artifacts\build\bin\Release\net8.0-windows\Host2VMRelay.exe'),
    [ValidateSet(100,125,150,175,200)]
    [int[]]$Scale = @(100,125,150,175,200),
    [switch]$Native,
    [ValidateRange(0,31)][int]$Monitor = 0
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path $PSScriptRoot -Parent
$checks = Join-Path $repoRoot 'artifacts\checks'
New-Item -ItemType Directory -Force $checks | Out-Null
if (!(Test-Path -LiteralPath $Executable)) { throw "Build the application first: $Executable" }
if ($Native -and $Scale.Count -ne 1) { throw 'Native checks require one explicit -Scale value matching the selected monitor. The script never changes Windows display settings.' }
$mode = if ($Native) { 'native' } else { 'dpi-message' }
$rows = @()
$failed = $false
foreach ($percent in $Scale) {
    $folder = Join-Path $checks "$mode-$percent"
    New-Item -ItemType Directory -Force $folder | Out-Null
    $image = Join-Path $folder 'report.png'
    $json = [IO.Path]::ChangeExtension($image, '.json')
    $text = [IO.Path]::ChangeExtension($image, '.txt')
    foreach ($old in @($json,$text)) { if (Test-Path -LiteralPath $old) { Remove-Item -LiteralPath $old -Force } }
    $nativeFlag = if ($Native) { '--native-dpi' } else { '' }
    $arguments = "--smoke --ui-scale=$percent --monitor=$Monitor $nativeFlag `"$image`""
    $process = Start-Process -FilePath $Executable -ArgumentList $arguments -PassThru
    if (!$process.WaitForExit(180000)) {
        $process.Kill()
        $rows += [pscustomobject]@{ Scale=$percent; Mode=$mode; Status='FAIL'; NativeDpi=$null; Assertions=0; Error='Timed out after 180 seconds' }
        $failed = $true
        continue
    }
    $process.Refresh()
    if (Test-Path -LiteralPath $text) { Get-Content -LiteralPath $text }
    if (!(Test-Path -LiteralPath $json)) {
        $rows += [pscustomobject]@{ Scale=$percent; Mode=$mode; Status='FAIL'; NativeDpi=$null; Assertions=0; Error='Missing structured evidence' }
        $failed = $true
        continue
    }
    $result = Get-Content -LiteralPath $json -Raw | ConvertFrom-Json
    $ok = $process.ExitCode -eq 0 -and $result.Status -eq 'PASS' -and $result.TargetPercent -eq $percent -and $result.Assertions -gt 0
    $rows += [pscustomobject]@{ Scale=$percent; Mode=$mode; Status=$result.Status; NativeDpi=$result.Stages[0].NativeDpi; Assertions=$result.Assertions; Error=($result.Errors -join '; ') }
    if (!$ok) { $failed = $true }
}
$rows | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $checks "$mode-summary.json") -Encoding utf8
$markdown = @("## DPI acceptance: $mode", '', '| Scale | Method | Actual monitor DPI | Assertions | Result |', '|---|---|---|---|---|')
foreach ($row in $rows) { $markdown += "| $($row.Scale)% | $mode | $($row.NativeDpi) | $($row.Assertions) | $($row.Status) |" }
$markdown += ''
$markdown += 'DPI-message tests inject WM_DPICHANGED into the application. They do not change the monitor DPI and do not certify physical multi-monitor behavior. Native mode refuses a mismatched actual DPI.'
$markdown | Set-Content -LiteralPath (Join-Path $checks "$mode-summary.md") -Encoding utf8
if ($env:GITHUB_STEP_SUMMARY) { $markdown | Out-File $env:GITHUB_STEP_SUMMARY -Append -Encoding utf8 }
if ($failed) { throw "DPI acceptance failed or was blocked; inspect artifacts/checks/$mode-*/report.json and screenshots." }
Write-Host "PASS $mode acceptance for $($Scale -join ', ') percent. Evidence: $checks"
