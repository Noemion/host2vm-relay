//! Versioned bounded JSON over inherited pipes. No credential-bearing arguments
//! or management TCP port. Closing the parent pipe terminates every owned task.
use anyhow::{Context, Result, ensure};
use serde::Deserialize;
use serde_json::{Value, json};
use std::{path::PathBuf, sync::Arc, time::Duration};
use tokio::{
    io::{AsyncBufReadExt, AsyncReadExt, AsyncWriteExt, BufReader},
    net::TcpListener,
    sync::{Mutex, Semaphore, mpsc, oneshot},
    task::JoinSet,
    time::timeout,
};
use zeroize::Zeroize;

#[derive(Deserialize)]
#[serde(rename_all = "camelCase")]
pub struct Config {
    pub version: u32,
    pub host: String,
    pub port: u16,
    pub user: String,
    pub use_key: bool,
    pub key_path: String,
    pub secret: String,
    pub token: String,
    pub max_connections: usize,
    pub agent_directory: PathBuf,
}
pub type Output = mpsc::Sender<Value>;
pub async fn run() -> Result<()> {
    let (commands, mut input) = mpsc::channel::<String>(8);
    let (eof, mut ended) = oneshot::channel();
    tokio::spawn(async move {
        let mut stdin = BufReader::new(tokio::io::stdin());
        loop {
            let mut line = String::new();
            let result = (&mut stdin).take(65537).read_line(&mut line).await;
            if !matches!(result, Ok(1..)) || line.len() > 65536 || !line.ends_with('\n') {
                break;
            }
            if commands.send(line).await.is_err() {
                break;
            }
        }
        let _ = eof.send(());
    });
    let (out, mut output) = mpsc::channel::<Value>(32);
    let mut writer = tokio::spawn(async move {
        let mut stdout = tokio::io::stdout();
        while let Some(value) = output.recv().await {
            stdout
                .write_all(serde_json::to_string(&value)?.as_bytes())
                .await?;
            stdout.write_all(b"\n").await?;
            stdout.flush().await?;
        }
        Ok::<_, anyhow::Error>(())
    });
    let mut line = timeout(Duration::from_secs(10), input.recv())
        .await?
        .context("missing configuration")?;
    let config: Result<Config, _> = serde_json::from_str(&line);
    line.zeroize();
    let mut config = config.context("invalid configuration")?;
    ensure!(
        config.version == 1
            && (1..=2048).contains(&config.max_connections)
            && config.token.len() == 64
            && config.token.bytes().all(|b| b.is_ascii_hexdigit()),
        "unsupported configuration"
    );
    // The handler can ask for trust without authenticating or creating channels.
    let ssh = tokio::select! {
        connection = crate::ssh::connect(&mut config, &mut input, out.clone()) => connection?,
        _ = &mut ended => return Ok(()),
        _ = &mut writer => return Ok(()),
    };
    config.secret.zeroize();
    let ssh = Arc::new(ssh);
    let listener = TcpListener::bind("127.0.0.1:0").await?;
    let port = listener.local_addr()?.port();
    out.send(json!({"event":"ready", "version":1, "port":port}))
        .await?;
    let config = Arc::new(config);
    let agent = Arc::new(Mutex::new(None::<String>));
    let slots = Arc::new(Semaphore::new(config.max_connections + 8));
    let control_slots = Arc::new(Semaphore::new(4));
    let udp_slot = Arc::new(Semaphore::new(1));
    let mut tasks = JoinSet::new();
    let mut alive = tokio::time::interval(Duration::from_secs(1));
    loop {
        tokio::select! {
            _ = &mut ended => break,
            _ = &mut writer => break,
            _ = alive.tick() => { if ssh.is_closed() { break; } },
            Some(_) = tasks.join_next(), if !tasks.is_empty() => {},
            accepted = listener.accept() => {
                let (socket, _) = accepted?;
                if let Ok(permit) = slots.clone().try_acquire_owned() {
                    let ssh = ssh.clone(); let config = config.clone(); let agent = agent.clone(); let udp = udp_slot.clone();
                    tasks.spawn(async move {
                        let _permit = permit;
                        let _ = crate::forward::serve(socket, ssh, config, agent, udp).await;
                    });
                }
            },
            line = input.recv() => {
                let Some(line) = line else { break };
                let request: Value = serde_json::from_str(&line).context("invalid control message")?;
                let id = request["id"].as_u64().context("missing request id")?;
                let op = request["op"].as_str().unwrap_or("").to_owned();
                let permit = control_slots.clone().try_acquire_owned().context("control overload")?;
                let out = out.clone(); let ssh = ssh.clone(); let agent = agent.clone(); let config = config.clone();
                tasks.spawn(async move {
                    let _permit = permit;
                    let result = match op.as_str() {
                        "probe" => crate::ssh::probe(&ssh).await,
                        "udp" => crate::deployment::prepare(&ssh, &config.agent_directory, agent).await,
                        _ => Err(anyhow::anyhow!("unsupported operation")),
                    };
                    let response = match result {
                        Ok(()) => json!({"id":id,"ok":true}),
                        Err(error) => json!({"id":id,"ok":false,"error":error.to_string()}),
                    };
                    let _ = out.send(response).await;
                });
            },
        }
    }
    tasks.abort_all();
    let _ = timeout(
        Duration::from_secs(1),
        ssh.disconnect(russh::Disconnect::ByApplication, "session closed", "en"),
    )
    .await;
    Ok(())
}
