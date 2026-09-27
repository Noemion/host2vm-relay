//! One task per destination, bounded datagram storage and DNS concurrency. No
//! listening network service: only the authenticated SSH channel owns this agent.
use crate::packet::{self, MAX_FRAME};
use anyhow::{Context, Result, ensure};
use std::{
    collections::HashMap,
    net::{IpAddr, SocketAddr, ToSocketAddrs},
    sync::Arc,
    time::Duration,
};
use tokio::{
    io::{AsyncReadExt, AsyncWriteExt},
    net::UdpSocket,
    sync::{OwnedSemaphorePermit, Semaphore, mpsc},
    task::JoinSet,
    time::timeout,
};

type Key = (u32, String, u16);
type Output = mpsc::Sender<Vec<u8>>;
struct Datagram {
    bytes: Vec<u8>,
    start: usize,
    _budget: OwnedSemaphorePermit,
}
struct Session {
    input: mpsc::Sender<Datagram>,
    abort: tokio::task::AbortHandle,
}

fn emit(out: &Output, op: u8, id: u32, body: &[u8]) {
    let mut frame = Vec::with_capacity(9 + body.len());
    frame.extend(((5 + body.len()) as u32).to_be_bytes());
    frame.push(op);
    frame.extend(id.to_be_bytes());
    frame.extend(body);
    let _ = out.try_send(frame); // UDP overload drops instead of growing memory.
}
async fn resolve(host: String, port: u16, dns: Arc<Semaphore>) -> Result<Vec<SocketAddr>> {
    if let Ok(ip) = host.parse::<IpAddr>() {
        return Ok(vec![SocketAddr::new(ip, port)]);
    }
    let permit = dns.try_acquire_owned().context("DNS busy")?;
    // The permit belongs to the blocking lookup, not its timed-out awaiter. A
    // stalled libc resolver cannot cause unbounded replacement worker threads.
    Ok(timeout(
        Duration::from_secs(2),
        tokio::task::spawn_blocking(move || {
            let _permit = permit;
            (host.as_str(), port)
                .to_socket_addrs()
                .map(|a| a.take(16).collect())
        }),
    )
    .await???)
}
async fn destination(
    key: Key,
    mut input: mpsc::Receiver<Datagram>,
    out: Output,
    dns: Arc<Semaphore>,
) -> Result<()> {
    let addresses = resolve(key.1, key.2, dns).await?;
    let mut connected = None;
    for peer in addresses.into_iter().filter(|a| packet::unicast(a.ip())) {
        let bind = if peer.is_ipv4() {
            "0.0.0.0:0"
        } else {
            "[::]:0"
        };
        if let Ok(socket) = UdpSocket::bind(bind).await
            && socket.connect(peer).await.is_ok()
        {
            connected = Some((socket, peer));
            break;
        }
    }
    let (socket, peer) = connected.context("no usable unicast destination")?;
    let mut buffer = vec![0; 65507];
    loop {
        tokio::select! {
            value = input.recv() => match value {
                Some(packet) => { socket.send(&packet.bytes[packet.start..]).await?; }
                None => return Ok(()),
            },
            value = socket.recv(&mut buffer) => {
                emit(&out, b'D', key.0, &packet::encode(peer, &buffer[..value?]));
            },
            _ = tokio::time::sleep(Duration::from_secs(120)) => return Ok(()),
        }
    }
}
async fn probe(body: &[u8]) -> Result<()> {
    ensure!((1..=64).contains(&body.len()), "invalid probe");
    let receiver = UdpSocket::bind("127.0.0.1:0").await?;
    let sender = UdpSocket::bind("127.0.0.1:0").await?;
    receiver.connect(sender.local_addr()?).await?;
    sender.send_to(body, receiver.local_addr()?).await?;
    let mut response = [0; 64];
    let n = timeout(Duration::from_millis(500), receiver.recv(&mut response)).await??;
    ensure!(&response[..n] == body, "probe mismatch");
    Ok(())
}
pub async fn run() -> Result<()> {
    let (out, mut output) = mpsc::channel::<Vec<u8>>(128);
    let mut writer = tokio::spawn(async move {
        let mut stdout = tokio::io::stdout();
        while let Some(frame) = output.recv().await {
            stdout.write_all(&frame).await?;
            stdout.flush().await?;
        }
        Ok::<_, std::io::Error>(())
    });
    let mut stdin = tokio::io::stdin();
    let mut sessions: HashMap<Key, Session> = HashMap::new();
    let mut tasks = JoinSet::new();
    let mut probes = JoinSet::new();
    let budget = Arc::new(Semaphore::new(16 * 1024 * 1024));
    let dns = Arc::new(Semaphore::new(4));
    emit(&out, b'H', 0, b"Host2VMRelay-UDP/1");
    loop {
        // Reading a full frame is itself bounded, including a peer that sends a
        // header and then stalls. Partial EOF exits the process and all sockets.
        let read = async {
            let n = stdin.read_u32().await? as usize;
            ensure!((5..=MAX_FRAME).contains(&n), "invalid frame size");
            let mut frame = vec![0; n];
            stdin.read_exact(&mut frame).await?;
            Ok::<_, anyhow::Error>(frame)
        };
        let frame = tokio::select! {
            frame = timeout(Duration::from_secs(30), read) => match frame { Ok(Ok(f)) => f, _ => break },
            _ = &mut writer => break,
        };
        while tasks.try_join_next().is_some() {}
        while probes.try_join_next().is_some() {}
        sessions.retain(|_, s| !s.input.is_closed());
        let id = u32::from_be_bytes(frame[1..5].try_into()?);
        match frame[0] {
            b'P' if probes.len() < 4 => {
                let out = out.clone();
                probes.spawn(async move {
                    if probe(&frame[5..]).await.is_ok() {
                        emit(&out, b'R', id, &frame[5..]);
                    }
                });
            }
            b'C' => sessions.retain(|key, session| {
                if key.0 == id {
                    session.abort.abort();
                    false
                } else {
                    true
                }
            }),
            b'D' if id != 0 => {
                let decoded = packet::decode(&frame[5..]);
                if let Ok((host, port, start)) = decoded {
                    let key = (id, host, port);
                    if !sessions.contains_key(&key) && sessions.len() < 2048 {
                        let (tx, rx) = mpsc::channel(8);
                        let out = out.clone();
                        let dns = dns.clone();
                        let target = key.clone();
                        let abort = tasks.spawn(async move {
                            if destination(target, rx, out.clone(), dns).await.is_err() {
                                emit(&out, b'E', id, b"UDP destination unavailable");
                            }
                        });
                        sessions.insert(key.clone(), Session { input: tx, abort });
                    }
                    if let Some(session) = sessions.get(&key) {
                        if let Ok(permit) =
                            budget.clone().try_acquire_many_owned(frame.len() as u32)
                        {
                            let _ = session.input.try_send(Datagram {
                                bytes: frame,
                                start: start + 5,
                                _budget: permit,
                            });
                        }
                    } else {
                        emit(&out, b'E', id, b"UDP destination limit reached");
                    }
                } else {
                    emit(&out, b'E', id, b"Invalid UDP datagram");
                }
            }
            _ => {}
        }
    }
    tasks.abort_all();
    probes.abort_all();
    writer.abort();
    Ok(())
}
