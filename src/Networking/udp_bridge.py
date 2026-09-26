"""Session-scoped UDP bridge. Binary stdin/stdout are carried inside authenticated SSH.

No listening service, filesystem state, root privilege or external Python package.
Protocol: uint32 network-order length, one opcode byte, uint32 association ID, body.
D carries a SOCKS5 UDP datagram. P/R are a nonce-checked UDP-loopback probe.
"""
import ipaddress
import os
import queue
import selectors
import socket
import struct
import sys
import threading
import time

MAX_FRAME = 66048
MAX_SESSIONS = 512
IDLE_SECONDS = 120
incoming = queue.Queue(128)
outgoing = queue.Queue(128)
stopping = threading.Event()


def read_exact(stream, size):
    data = bytearray()
    while len(data) < size:
        chunk = stream.read(size - len(data))
        if not chunk:
            raise EOFError()
        data.extend(chunk)
    return bytes(data)


def read_input():
    try:
        while not stopping.is_set():
            size = struct.unpack('!I', read_exact(sys.stdin.buffer, 4))[0]
            if size < 5 or size > MAX_FRAME:
                raise ValueError('invalid frame size')
            incoming.put(read_exact(sys.stdin.buffer, size))
    except (EOFError, OSError, ValueError):
        stopping.set()


def write_output():
    try:
        while not stopping.is_set():
            try:
                packet = outgoing.get(timeout=.2)
            except queue.Empty:
                continue
            sys.stdout.buffer.write(struct.pack('!I', len(packet)) + packet)
            sys.stdout.buffer.flush()
    except (OSError, BrokenPipeError):
        stopping.set()


def emit(opcode, ident, body=b''):
    try:
        outgoing.put_nowait(opcode + struct.pack('!I', ident) + body)
    except queue.Full:
        pass  # UDP overload drops packets, never allocates an unbounded backlog.


def decode(packet):
    if len(packet) < 7 or packet[:3] != b'\0\0\0':
        raise ValueError('fragmented or malformed SOCKS datagram')
    kind = packet[3]
    if kind == 1:
        end = 8
        host = socket.inet_ntop(socket.AF_INET, packet[4:end])
    elif kind == 4:
        end = 20
        host = socket.inet_ntop(socket.AF_INET6, packet[4:end])
    elif kind == 3:
        end = 5 + packet[4]
        host = packet[5:end].decode('ascii')
        if not host or len(packet[5:end]) != packet[4] or '\0' in host:
            raise ValueError('invalid destination name')
    else:
        raise ValueError('unsupported destination type')
    if len(packet) < end + 2:
        raise ValueError('truncated destination')
    port = struct.unpack('!H', packet[end:end + 2])[0]
    if not port or len(packet) - end - 2 > 65507:
        raise ValueError('invalid destination port or payload size')
    return host, port, packet[end + 2:]


def encode(address, data):
    host, port = address[:2]
    ip = ipaddress.ip_address(host.split('%')[0])
    return b'\0\0\0' + (b'\1' if ip.version == 4 else b'\4') + ip.packed + struct.pack('!H', port) + data


def probe_udp(nonce):
    # Exercise real UDP send/receive on the VM, not just a stdout heartbeat.
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as receiver:
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
            receiver.bind(('127.0.0.1', 0))
            receiver.settimeout(.5)
            sender.sendto(nonce, receiver.getsockname())
            return receiver.recvfrom(64)[0] == nonce


def main():
    selector = selectors.DefaultSelector()
    sessions = {}
    reader = threading.Thread(target=read_input, daemon=True)
    writer = threading.Thread(target=write_output, daemon=True)
    reader.start()
    writer.start()
    emit(b'H', 0, b'Host2VMRelay-UDP/1')

    def remove(key):
        item = sessions.pop(key, None)
        if item:
            try:
                selector.unregister(item[0])
            except (KeyError, ValueError):
                pass
            item[0].close()

    last_input = time.monotonic()
    try:
        while not stopping.is_set():
            if time.monotonic() - last_input > 30:
                break  # A dead SSH peer must not leave a relay process behind.
            # Bound each batch so a busy sender cannot starve incoming replies.
            for _ in range(32):
                try:
                    frame = incoming.get_nowait()
                except queue.Empty:
                    break
                last_input = time.monotonic()
                opcode, ident, body = frame[:1], struct.unpack('!I', frame[1:5])[0], frame[5:]
                if opcode == b'P':
                    if 1 <= len(body) <= 64 and probe_udp(body):
                        emit(b'R', ident, body)
                    continue
                if opcode == b'C':
                    for key in list(sessions):
                        if key[0] == ident:
                            remove(key)
                    continue
                if opcode != b'D' or ident == 0:
                    continue
                try:
                    host, port, data = decode(body)
                    key = (ident, host, port)
                    if key not in sessions:
                        if len(sessions) >= MAX_SESSIONS:
                            raise ValueError('UDP session limit reached')
                        addresses = socket.getaddrinfo(host, port, socket.AF_UNSPEC, socket.SOCK_DGRAM)
                        last = None
                        for family, socktype, proto, _, remote in addresses:
                            target = ipaddress.ip_address(remote[0].split('%')[0])
                            if target.is_multicast or target.is_unspecified or str(target) == '255.255.255.255':
                                continue
                            udp = socket.socket(family, socktype, proto)
                            try:
                                udp.connect(remote)
                                udp.setblocking(False)
                                selector.register(udp, selectors.EVENT_READ, key)
                                sessions[key] = [udp, time.monotonic()]
                                break
                            except OSError as exc:
                                last = exc
                                udp.close()
                        if key not in sessions:
                            raise OSError('no usable unicast destination') from last
                    udp, _ = sessions[key]
                    udp.send(data)
                    sessions[key][1] = time.monotonic()
                except (ValueError, OSError, UnicodeError, IndexError, struct.error) as exc:
                    emit(b'E', ident, str(exc).encode('utf-8', errors='replace')[:256])
            # Windows select() rejects an empty descriptor set; idle bridges must
            # still process control/probe frames before the first UDP association.
            if selector.get_map():
                events = selector.select(.01)
            else:
                time.sleep(.01)
                events = ()
            for event, _ in events:
                key = event.data
                try:
                    udp = event.fileobj
                    data = udp.recv(65507)
                    emit(b'D', key[0], encode(udp.getpeername(), data))
                    if key in sessions:
                        sessions[key][1] = time.monotonic()
                except (OSError, ValueError):
                    remove(key)
            now = time.monotonic()
            for key, item in list(sessions.items()):
                if now - item[1] > IDLE_SECONDS:
                    remove(key)
    finally:
        stopping.set()
        for key in list(sessions):
            remove(key)
        selector.close()


if __name__ == '__main__':
    main()
