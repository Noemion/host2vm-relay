use anyhow::{Result, bail, ensure};
use std::net::{IpAddr, Ipv4Addr, Ipv6Addr, SocketAddr};

pub const MAX_FRAME: usize = 66048;
pub fn decode(packet: &[u8]) -> Result<(String, u16, usize)> {
    ensure!(
        packet.len() >= 7 && packet[..3] == [0, 0, 0],
        "invalid datagram"
    );
    let (host, end) = match packet[3] {
        1 if packet.len() >= 10 => (
            Ipv4Addr::from(<[u8; 4]>::try_from(&packet[4..8])?).to_string(),
            8,
        ),
        4 if packet.len() >= 22 => (
            Ipv6Addr::from(<[u8; 16]>::try_from(&packet[4..20])?).to_string(),
            20,
        ),
        3 => {
            let end = 5 + packet[4] as usize;
            ensure!(end > 5 && packet.len() >= end + 2, "invalid domain length");
            let name = std::str::from_utf8(&packet[5..end])?;
            ensure!(
                name.is_ascii() && !name.bytes().any(|b| b <= 32 || b == 127),
                "invalid domain"
            );
            (name.to_owned(), end)
        }
        _ => bail!("invalid address"),
    };
    let port = u16::from_be_bytes([packet[end], packet[end + 1]]);
    ensure!(
        port != 0 && packet.len() - end - 2 <= 65507,
        "invalid port or payload length"
    );
    Ok((host, port, end + 2))
}

pub fn unicast(ip: IpAddr) -> bool {
    !ip.is_multicast()
        && !ip.is_unspecified()
        && ip != IpAddr::V4(Ipv4Addr::BROADCAST)
        && !matches!(ip, IpAddr::V6(ip) if ip.to_ipv4_mapped().is_some_and(|v4| !unicast(IpAddr::V4(v4))))
}
pub fn encode(peer: SocketAddr, data: &[u8]) -> Vec<u8> {
    let mut result = vec![0, 0, 0];
    match peer.ip() {
        IpAddr::V4(ip) => {
            result.push(1);
            result.extend(ip.octets());
        }
        IpAddr::V6(ip) => {
            result.push(4);
            result.extend(ip.octets());
        }
    }
    result.extend(peer.port().to_be_bytes());
    result.extend(data);
    result
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn address_roundtrip() {
        for peer in ["127.0.0.1:53", "[::1]:53"] {
            let peer: SocketAddr = peer.parse().unwrap();
            let bytes = encode(peer, b"payload");
            let (host, port, start) = decode(&bytes).unwrap();
            assert_eq!(host, peer.ip().to_string());
            assert_eq!(port, 53);
            assert_eq!(&bytes[start..], b"payload");
            for n in 0..start {
                assert!(decode(&bytes[..n]).is_err());
            }
        }
    }
    #[test]
    fn reject_fragment_and_broadcast() {
        let mut bytes = encode("127.0.0.1:53".parse().unwrap(), b"");
        bytes[2] = 1;
        assert!(decode(&bytes).is_err());
        for ip in [
            "0.0.0.0",
            "255.255.255.255",
            "224.0.0.1",
            "::",
            "ff02::1",
            "::ffff:255.255.255.255",
        ] {
            assert!(!unicast(ip.parse().unwrap()));
        }
    }
}
