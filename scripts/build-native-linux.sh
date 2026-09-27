#!/usr/bin/env bash
set -euo pipefail
root=$(cd "$(dirname "$0")/.." && pwd)
cd "$root/native"
export CARGO_TARGET_DIR="${CARGO_TARGET_DIR:-$root/artifacts/native-target}"
host=$(rustc -vV | sed -n 's/^host: //p')
export CARGO_TARGET_AARCH64_UNKNOWN_LINUX_MUSL_LINKER="$(rustc --print sysroot)/lib/rustlib/$host/bin/rust-lld"
mkdir -p "$root/artifacts/native/agents"
for pair in x86_64-unknown-linux-musl:x64 aarch64-unknown-linux-musl:arm64; do
    target=${pair%:*}; arch=${pair#*:}
    cargo build --locked --release -p h2vm-agent --target "$target"
    artifact="$CARGO_TARGET_DIR/$target/release/h2vm-agent"
    if readelf -l "$artifact" | grep -q INTERP || readelf -d "$artifact" | grep -q NEEDED; then
        echo "Dynamic dependency found: $target" >&2; exit 1
    fi
    cp "$artifact" "$root/artifacts/native/agents/h2vm-agent-linux-$arch"
done
if [ "${1:-}" = "--with-core" ]; then
    cargo build --locked --release -p h2vm-core
    destination="$root/artifacts/native/linux-x64"
    mkdir -p "$destination"
    cp "$CARGO_TARGET_DIR/release/h2vm-core" "$destination/"
    cp "$root/artifacts/native/agents/"* "$destination/"
    (cd "$destination" && sha256sum h2vm-core h2vm-agent-linux-* > SHA256SUMS)
fi
