[CmdletBinding()]
param(
    [switch]$Sync,
    [switch]$Package,
    [switch]$Install,
    [switch]$Run,
    [ValidateSet('Debug','Release')]
    [string]$Configuration = 'Release',
    [string]$DotNet = 'dotnet',
    [string]$Iscc = ''
)

$ErrorActionPreference = 'Stop'
$repoRoot = $PSScriptRoot
$project = Join-Path $repoRoot 'src\Host2VMRelay.csproj'

function Invoke-Checked {
    param([string]$FilePath, [string[]]$Arguments)
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) { throw "$FilePath failed with exit code $LASTEXITCODE" }
}

Push-Location $repoRoot
try {
    if ($Sync) {
        Invoke-Checked 'git' @('-C', $repoRoot, 'pull', '--ff-only')
    }

    Invoke-Checked $DotNet @('restore', $project)
    Invoke-Checked $DotNet @('build', $project, '-c', $Configuration, '--no-restore')

    $exe = Join-Path $repoRoot "artifacts\build\bin\$Configuration\net8.0-windows\Host2VMRelay.exe"
    if (!(Test-Path $exe)) { throw "Executable not found: $exe" }

    if ($Package -or $Install) {
        $args = @{ DotNet = $DotNet }
        if ($Iscc) { $args.Iscc = $Iscc }
        & (Join-Path $repoRoot 'scripts\build-release.ps1') @args
    }

    if ($Install) {
        $setup = Get-ChildItem (Join-Path $repoRoot 'artifacts\release') -Filter 'Host2VMRelay-*-Setup.exe' -File |
            Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if (!$setup) { throw 'Installer was not generated.' }
        $process = Start-Process $setup.FullName -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Installer failed with exit code $($process.ExitCode)" }
    }

    if ($Run) { Start-Process $exe }

    Write-Host "Host2VMRelay $Configuration build completed." -ForegroundColor Green
    Write-Host "Build output: $exe"
    if ($Package -or $Install) {
        Write-Host "Release artifacts: $(Join-Path $repoRoot 'artifacts\release')"
    }
}
finally {
    Pop-Location
}
