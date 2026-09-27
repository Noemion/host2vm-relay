//! Content-addressed user-owned agent cache. Only locally computed hashes enter
//! shell commands; remote architecture strings are matched, never interpolated.
use crate::ssh::{Ssh, command};
use anyhow::{Context, Result, bail, ensure};
use sha2::{Digest, Sha256};
use std::{path::Path, sync::Arc, time::Duration};
use tokio::{io::AsyncWriteExt, sync::Mutex, time::timeout};

pub async fn prepare(
    ssh: &Ssh,
    directory: &Path,
    cached: Arc<Mutex<Option<String>>>,
) -> Result<()> {
    timeout(Duration::from_secs(30), async {
        let mut cache = cached.lock().await;
        let platform = command(ssh, "uname -s; uname -m").await?;
        let filename = match platform.trim() {
            "Linux\nx86_64" => "h2vm-agent-linux-x64",
            "Linux\naarch64" => "h2vm-agent-linux-arm64",
            _ => bail!("UDP helper supports Linux x86_64 and aarch64 only"),
        };
        let bytes = std::fs::read(directory.join(filename)).context("missing packaged Linux UDP helper")?;
        ensure!(bytes.len() <= 16 * 1024 * 1024 && bytes.starts_with(b"\x7fELF"), "invalid helper artifact");
        let hash = format!("{:x}", Sha256::digest(&bytes));
        let path = format!("\"$HOME/.cache/host2vm-relay/{hash}/agent\"");
        let verify = format!("test -x {path} && printf '%s  %s\\n' '{hash}' {path} | sha256sum -c - >/dev/null 2>&1");
        if command(ssh, &verify).await.is_err() {
            // mktemp prevents concurrent sessions from sharing a partial file.
            // Restrict permissions before receiving any bytes; rename atomically
            // only after validating the complete upload on the destination.
            let script = format!("set -eu; umask 077; d=\"$HOME/.cache/host2vm-relay/{hash}\"; mkdir -p \"$d\"; t=$(mktemp \"$d/upload.XXXXXXXX\"); trap 'rm -f \"$t\"' EXIT HUP INT TERM; cat > \"$t\"; printf '%s  %s\\n' '{hash}' \"$t\" | sha256sum -c - >/dev/null; chmod 700 \"$t\"; mv -f \"$t\" \"$d/agent\"");
            let channel = ssh.channel_open_session().await?;
            channel.exec(true, script).await?;
            let mut stream = channel.into_stream();
            stream.write_all(&bytes).await?; stream.shutdown().await?;
            // Separate verification also catches early remote process failure.
            use tokio::io::AsyncReadExt;
            let mut output = Vec::new(); stream.take(4096).read_to_end(&mut output).await?;
            command(ssh, &verify).await.context("UDP helper upload or verification failed")?;
        }
        *cache = Some(format!("exec {path}")); Ok(())
    }).await?
}
