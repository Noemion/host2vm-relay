[CmdletBinding()]
param(
    [switch]$Sync,
    [switch]$Package,
    [switch]$Portable,
    [switch]$Install,
    [switch]$Run,
    [switch]$Test,
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
    if ($Sync) { Invoke-Checked 'git' @('-C', $repoRoot, 'pull', '--ff-only') }
    Invoke-Checked $DotNet @('restore', $project)
    Invoke-Checked $DotNet @('build', $project, '-c', $Configuration, '--no-restore')
    $exe = Join-Path $repoRoot "artifacts\build\bin\$Configuration\net8.0-windows\Host2VMRelay.exe"
    if (!(Test-Path -LiteralPath $exe)) { throw "Executable not found: $exe" }
    if ($Test) { & (Join-Path $repoRoot 'scripts\test.ps1') -Executable $exe -Smoke }
    if ($Package -or $Portable -or $Install) {
        $releaseArgs = @{ DotNet = $DotNet; SkipInstaller = ($Portable -and !$Package -and !$Install) }
        if ($Iscc) { $releaseArgs.Iscc = $Iscc }
        & (Join-Path $repoRoot 'scripts\build-release.ps1') @releaseArgs
    }
    if ($Install) {
        [xml]$projectXml = Get-Content -LiteralPath $project
        $version = [string]$projectXml.Project.PropertyGroup.Version
        $setup = Join-Path $repoRoot "artifacts\release\Host2VMRelay-$version-Setup.exe"
        if (!(Test-Path -LiteralPath $setup)) { throw 'Installer was not generated.' }
        $process = Start-Process -FilePath $setup -Wait -PassThru
        if ($process.ExitCode -ne 0) { throw "Installer failed with exit code $($process.ExitCode)" }
    }
    if ($Run) { Start-Process -FilePath $exe }
    Write-Host "Host2VMRelay $Configuration build completed."
    Write-Host "Build output: $exe"
}
finally { Pop-Location }
