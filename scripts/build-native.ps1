[CmdletBinding(DefaultParameterSetName = 'BuildAndBundle')]
param(
    [ValidateSet('x64','x86','arm64')][string[]]$Architectures = @('x64','x86','arm64'),
    [string]$Cargo = 'cargo',
    [Parameter(ParameterSetName = 'Workers')][switch]$WorkersOnly,
    [Parameter(ParameterSetName = 'Bundle')][switch]$BundleOnly
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$targets = @{ x64 = 'x86_64-pc-windows-msvc'; x86 = 'i686-pc-windows-msvc'; arm64 = 'aarch64-pc-windows-msvc' }
$agentDir = Join-Path $root 'artifacts/native/agents'
if (!$WorkersOnly) {
    foreach ($name in @('h2vm-agent-linux-x64','h2vm-agent-linux-arm64')) {
        if (!(Test-Path (Join-Path $agentDir $name))) { throw 'Build/download both Linux agents before the Windows bundle.' }
    }
}
$previousFlags = $env:RUSTFLAGS
$previousTarget = $env:CARGO_TARGET_DIR
try {
    $env:RUSTFLAGS = '-C target-feature=+crt-static'
    $env:CARGO_TARGET_DIR = Join-Path $root 'artifacts/native-target'
    foreach ($arch in $Architectures) {
        if (!$targets.ContainsKey($arch)) { throw "Unsupported Windows architecture: $arch" }
        $target = $targets[$arch]
        $destination = Join-Path $root "artifacts/native/win-$arch"
        New-Item -ItemType Directory -Force $destination | Out-Null
        if (!$BundleOnly) {
            & $Cargo build --manifest-path "$root/native/Cargo.toml" --locked --release -p h2vm-core --target $target
            if ($LASTEXITCODE -ne 0) { throw "Rust core build failed: $target" }
            Copy-Item "$env:CARGO_TARGET_DIR/$target/release/h2vm-core.exe" $destination -Force
        }
        if (!$WorkersOnly) {
            Copy-Item "$agentDir/*" $destination -Force
            Get-FileHash (Join-Path $destination 'h2vm-core.exe'),(Join-Path $destination 'h2vm-agent-linux-x64'),(Join-Path $destination 'h2vm-agent-linux-arm64') -Algorithm SHA256 |
                ForEach-Object { "$($_.Hash.ToLowerInvariant())  $([IO.Path]::GetFileName($_.Path))" } |
                Set-Content (Join-Path $destination 'SHA256SUMS') -Encoding ascii
        }
    }
    # Native Windows helper is a test fixture only; shipped VM assets remain two Linux binaries.
    if (!$BundleOnly -and $Architectures -contains 'x64') {
        & $Cargo build --manifest-path "$root/native/Cargo.toml" --locked --release -p h2vm-agent --target $targets.x64
        if ($LASTEXITCODE -ne 0) { throw 'Windows UDP fixture build failed' }
        New-Item -ItemType Directory -Force "$root/artifacts/native/tests" | Out-Null
        Copy-Item "$env:CARGO_TARGET_DIR/$($targets.x64)/release/h2vm-agent.exe" "$root/artifacts/native/tests/h2vm-agent.exe" -Force
    }
}
finally { $env:RUSTFLAGS = $previousFlags; $env:CARGO_TARGET_DIR = $previousTarget }
