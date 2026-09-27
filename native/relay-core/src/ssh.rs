use crate::control::{Config, Output};
use anyhow::{Context, Result, ensure};
use russh::{
    ChannelMsg, client,
    keys::{HashAlg, PrivateKeyWithHashAlg, PublicKeyOrCertificate, load_secret_key},
};
use serde_json::json;
use std::{sync::Arc, time::Duration};
use tokio::{
    sync::{Notify, mpsc, oneshot},
    time::timeout,
};

pub struct Handler {
    out: Output,
    trust: Option<oneshot::Receiver<bool>>,
    accepted: Option<String>,
    key_seen: Arc<Notify>,
}
impl client::Handler for Handler {
    type Error = anyhow::Error;
    async fn check_server_key(&mut self, key: &PublicKeyOrCertificate) -> Result<bool> {
        let fingerprint = key.public_key().fingerprint(HashAlg::Sha256).to_string();
        if let Some(accepted) = &self.accepted {
            return Ok(accepted == &fingerprint);
        }
        self.key_seen.notify_one();
        self.out
            .send(json!({"event":"hostKey","fingerprint":fingerprint}))
            .await?;
        let accepted = timeout(
            Duration::from_secs(120),
            self.trust.take().context("missing trust response")?,
        )
        .await??;
        if accepted {
            self.accepted = Some(fingerprint);
        }
        Ok(accepted)
    }
}
pub type Ssh = client::Handle<Handler>;
pub async fn connect(
    config: &mut Config,
    input: &mut mpsc::Receiver<String>,
    out: Output,
) -> Result<Ssh> {
    let (trust, answer) = oneshot::channel();
    let key_seen = Arc::new(Notify::new());
    let handler = Handler {
        out,
        trust: Some(answer),
        accepted: None,
        key_seen: key_seen.clone(),
    };
    let ssh_config = client::Config {
        keepalive_interval: Some(Duration::from_secs(5)),
        keepalive_max: 3,
        window_size: 256 * 1024,
        maximum_packet_size: 32 * 1024,
        channel_buffer_size: 16,
        nodelay: true,
        ..Default::default()
    };
    let connection = timeout(
        Duration::from_secs(135),
        client::connect(
            Arc::new(ssh_config),
            (config.host.as_str(), config.port),
            handler,
        ),
    );
    tokio::pin!(connection);
    let initial = tokio::time::sleep(Duration::from_secs(12));
    tokio::pin!(initial);
    let mut deadline_active = true;
    let mut trust = Some(trust);
    let mut ssh = loop {
        tokio::select! {
            result = &mut connection => break result??,
            _ = &mut initial, if deadline_active => anyhow::bail!("SSH handshake timed out"),
            _ = key_seen.notified(), if trust.is_some() => { deadline_active = false; },
            line = input.recv(), if trust.is_some() => {
                let line = line.context("parent closed")?;
                let value: serde_json::Value = serde_json::from_str(&line)?;
                ensure!(value["op"] == "trust", "expected trust response");
                let _ = trust.take().unwrap().send(value["accept"].as_bool().unwrap_or(false));
                initial.as_mut().reset(tokio::time::Instant::now() + Duration::from_secs(12));
                deadline_active = true;
            }
        }
    };
    let auth = async {
        if config.use_key {
            let path = config.key_path.clone();
            let secret = zeroize::Zeroizing::new(config.secret.clone());
            let key = tokio::task::spawn_blocking(move || {
                load_secret_key(
                    path,
                    if secret.is_empty() {
                        None
                    } else {
                        Some(secret.as_str())
                    },
                )
            })
            .await??;
            let hash = ssh.best_supported_rsa_hash().await?.flatten();
            ensure!(
                ssh.authenticate_publickey(
                    &config.user,
                    PrivateKeyWithHashAlg::new(Arc::new(key), hash)
                )
                .await?
                .success(),
                "SSH authentication failed"
            );
        } else {
            ensure!(
                ssh.authenticate_password(&config.user, &config.secret)
                    .await?
                    .success(),
                "SSH authentication failed"
            );
        }
        Ok::<_, anyhow::Error>(())
    };
    timeout(Duration::from_secs(15), auth).await??;
    Ok(ssh)
}
/// Capture a small command response, requiring an exit status. Remote stderr is
/// deliberately not relayed into logs because it can contain paths or secrets.
pub async fn command(ssh: &Ssh, command: &str) -> Result<String> {
    let mut owned = CommandChannel(Some(ssh.channel_open_session().await?));
    let channel = owned.0.as_mut().context("missing command channel")?;
    channel.exec(true, command).await?;
    channel.eof().await?;
    let mut bytes = Vec::new();
    let mut status = None;
    while let Some(message) = channel.wait().await {
        match message {
            ChannelMsg::Data { data } => {
                ensure!(
                    bytes.len() + data.len() <= 4096,
                    "remote response too large"
                );
                bytes.extend(&data[..]);
            }
            ChannelMsg::ExitStatus { exit_status } => status = Some(exit_status),
            _ => {}
        }
    }
    ensure!(status == Some(0), "remote command failed");
    Ok(String::from_utf8(bytes)?)
}

// A cancelled command must close its SSH channel even if the remote command
// never exits. ChannelStream supplies russh's close-on-drop ownership guard.
struct CommandChannel(Option<russh::Channel<client::Msg>>);
impl Drop for CommandChannel {
    fn drop(&mut self) {
        drop(self.0.take().map(|channel| channel.into_stream()));
    }
}
pub async fn probe(ssh: &Ssh) -> Result<()> {
    let reply = timeout(Duration::from_secs(3), command(ssh, "printf h2vm-alive")).await??;
    ensure!(reply == "h2vm-alive", "SSH probe mismatch");
    Ok(())
}
