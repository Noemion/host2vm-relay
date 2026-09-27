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
MAX_SESSIONS = 2048
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


class Resolution:
    def __init__(self, host, port):
        self.host, self.port = host, port
        self.deadline = time.monotonic() + 2
        self.done = threading.Event()
        self.addresses, self.error = None, None


class Resolver:
    """Bounded daemon workers: libc DNS cannot be cancelled, but never blocks the relay.

    Expired jobs are ignored; four stuck OS lookups cannot spawn more threads or
    prevent probes, numeric-IP traffic, shutdown, or existing sessions from working.
    Cache access and job consumption belong exclusively to the main event loop.
    """
    def __init__(self):
        self.jobs = queue.Queue(32)
        self.cache = {}
        for _ in range(4):
            threading.Thread(target=self.worker, daemon=True).start()

    def worker(self):
        while not stopping.is_set():
            try:
                job = self.jobs.get(timeout=.2)
            except queue.Empty:
                continue
            if time.monotonic() >= job.deadline:
                continue
            try:
                job.addresses = socket.getaddrinfo(job.host, job.port, socket.AF_UNSPEC, socket.SOCK_DGRAM)
            except (OSError, UnicodeError) as exc:
                job.error = str(exc)
            finally:
                job.done.set()

    def request(self, host, port, allow_pending=True):
        job = Resolution(host, port)
        try:
            ip = ipaddress.ip_address(host)
        except ValueError:
            ip = None
        if ip is not None:
            # Numeric destinations do not need a resolver worker, even under DNS overload.
            family = socket.AF_INET if ip.version == 4 else socket.AF_INET6
            remote = (str(ip), port) if ip.version == 4 else (str(ip), port, 0, 0)
            job.addresses = [(family, socket.SOCK_DGRAM, 0, '', remote)]
            job.done.set()
        elif (cached := self.cache.get((host, port))) and cached[0] > time.monotonic():
            job.addresses = cached[1]
            job.done.set()
        else:
            if not allow_pending:
                raise ValueError('pending destination limit reached')
            try:
                self.jobs.put_nowait(job)
            except queue.Full:
                raise ValueError('DNS queue limit reached')
        return job

    def remember(self, job):
        if len(self.cache) >= 128:
            self.cache.pop(next(iter(self.cache)))
        self.cache[(job.host, job.port)] = (time.monotonic() + 60, job.addresses)


def main():
    selector = selectors.DefaultSelector()
    sessions = {}
    pending = {}
    resolver = Resolver()
    reader = threading.Thread(target=read_input, daemon=True)
    writer = threading.Thread(target=write_output, daemon=True)
    reader.start()
    writer.start()
    emit(b'H', 0, b'Host2VMRelay-UDP/1')

    def error(ident, exc):
        emit(b'E', ident, str(exc).encode('utf-8', errors='replace')[:256])

    def remove(key):
        # A late DNS result must never resurrect a closed association.
        pending.pop(key, None)
        item = sessions.pop(key, None)
        if item:
            try:
                selector.unregister(item[0])
            except (KeyError, ValueError):
                pass
            item[0].close()

    def open_session(key, addresses):
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
                return
            except OSError as exc:
                last = exc
                udp.close()
        raise OSError('no usable unicast destination') from last

    def send(key, data):
        sessions[key][0].send(data)
        sessions[key][1] = time.monotonic()

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
                    try:
                        if 1 <= len(body) <= 64 and probe_udp(body):
                            emit(b'R', ident, body)
                    except OSError as exc:
                        error(ident, exc)
                    continue
                if opcode == b'C':
                    for key in list(sessions) + list(pending):
                        if key[0] == ident:
                            remove(key)
                    continue
                if opcode != b'D' or ident == 0:
                    continue
                try:
                    host, port, data = decode(body)
                    key = (ident, host, port)
                    if key in sessions:
                        send(key, data)
                    elif key in pending:
                        # UDP overload drops packets; never grow an unbounded DNS backlog.
                        if len(pending[key][1]) < 8:
                            pending[key][1].append(data)
                    else:
                        if len(sessions) + len(pending) >= MAX_SESSIONS:
                            raise ValueError('UDP session limit reached')
                        job = resolver.request(host, port, len(pending) < 32)
                        if job.done.is_set():
                            open_session(key, job.addresses)
                            send(key, data)
                        else:
                            pending[key] = (job, [data])
                except (ValueError, OSError, UnicodeError, IndexError, struct.error) as exc:
                    error(ident, exc)
            for key, (job, packets) in list(pending.items()):
                try:
                    if time.monotonic() >= job.deadline:
                        raise TimeoutError('destination DNS lookup timed out')
                    if not job.done.is_set():
                        continue
                    if job.error is not None:
                        raise OSError(job.error)
                    resolver.remember(job)
                    open_session(key, job.addresses)
                    for data in packets:
                        send(key, data)
                except (OSError, ValueError) as exc:
                    error(key[0], exc)
                pending.pop(key, None)
            # Windows select() rejects an empty descriptor set.
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
        pending.clear()
        for key in list(sessions):
            remove(key)
        selector.close()


if __name__ == '__main__':
    main()
