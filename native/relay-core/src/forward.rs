use crate::{control::Config, ssh::Ssh};
use anyhow::{Result, bail, ensure};
use std::{
    net::{Ipv4Addr, Ipv6Addr},
    sync::Arc,
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::TcpStream,
    sync::{Mutex, Semaphore},
    time::timeout,
};

async fn authenticate(socket: &mut TcpStream, token: &str) -> Result<()> {
    ensure!(socket.read_u8().await? == 5, "invalid protocol");
    let n = socket.read_u8().await? as usize;
    let mut methods = vec![0; n];
    socket.read_exact(&mut methods).await?;
    ensure!(methods.contains(&2), "authentication required");
    socket.write_all(&[5, 2]).await?;
    ensure!(socket.read_u8().await? == 1, "invalid authentication");
    let n = socket.read_u8().await? as usize;
    let mut user = vec![0; n];
    socket.read_exact(&mut user).await?;
    let n = socket.read_u8().await? as usize;
    let mut secret = vec![0; n];
    socket.read_exact(&mut secret).await?;
    // Fixed-size random capability; compare every byte before deciding.
    let valid = secret.len() == token.len()
        && secret
            .iter()
            .zip(token.bytes())
            .fold(0, |a, (b, c)| a | (b ^ c))
            == 0
        && user == b"h2vm";
    socket.write_all(&[1, if valid { 0 } else { 1 }]).await?;
    ensure!(valid, "authentication rejected");
    Ok(())
}
async fn request(socket: &mut TcpStream) -> Result<(u8, String, u16)> {
    ensure!(socket.read_u8().await? == 5, "invalid request");
    let op = socket.read_u8().await?;
    ensure!(socket.read_u8().await? == 0, "invalid reserved byte");
    let host = match socket.read_u8().await? {
        1 => {
            let mut b = [0; 4];
            socket.read_exact(&mut b).await?;
            Ipv4Addr::from(b).to_string()
        }
        4 => {
            let mut b = [0; 16];
            socket.read_exact(&mut b).await?;
            Ipv6Addr::from(b).to_string()
        }
        3 => {
            let n = socket.read_u8().await? as usize;
            ensure!(n > 0, "empty domain");
            let mut b = vec![0; n];
            socket.read_exact(&mut b).await?;
            let s = String::from_utf8(b)?;
            ensure!(!s.contains('\0'), "invalid domain");
            s
        }
        _ => bail!("unsupported address"),
    };
    Ok((op, host, socket.read_u16().await?))
}
pub async fn serve(
    mut socket: TcpStream,
    ssh: Arc<Ssh>,
    config: Arc<Config>,
    agent: Arc<Mutex<Option<String>>>,
    udp: Arc<Semaphore>,
) -> Result<()> {
    socket.set_nodelay(true)?;
    let opened = timeout(Duration::from_secs(10), async {
        authenticate(&mut socket, &config.token).await?;
        let (op, host, port) = request(&mut socket).await?;
        let (channel, permit) = match op {
            1 => (
                ssh.channel_open_direct_tcpip(host, port as u32, "127.0.0.1", 0)
                    .await?,
                None,
            ),
            0xf0 => {
                let permit = udp.try_acquire_owned()?;
                let command = agent
                    .lock()
                    .await
                    .clone()
                    .ok_or_else(|| anyhow::anyhow!("UDP helper not prepared"))?;
                let channel = ssh.channel_open_session().await?;
                channel.exec(true, command).await?;
                (channel, Some(permit))
            }
            _ => bail!("unsupported command"),
        };
        Ok::<_, anyhow::Error>((channel, permit))
    })
    .await;
    let (channel, _permit) = match opened {
        Ok(Ok(value)) => value,
        _ => {
            let _ = timeout(
                Duration::from_secs(1),
                socket.write_all(&[5, 1, 0, 1, 127, 0, 0, 1, 0, 0]),
            )
            .await;
            return Ok(());
        }
    };
    socket.write_all(&[5, 0, 0, 1, 127, 0, 0, 1, 0, 0]).await?;
    let mut stream = channel.into_stream();
    // copy_bidirectional preserves EOF independently: request half-close must
    // not discard a server response. Buffers are fixed, backpressure is awaited.
    tokio::io::copy_bidirectional_with_sizes(&mut socket, &mut stream, 32768, 32768).await?;
    Ok(())
}
