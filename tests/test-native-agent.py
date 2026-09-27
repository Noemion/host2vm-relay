"""Black-box acceptance of either static architecture (optionally under QEMU)."""
import argparse
import queue
import socket
import struct
import subprocess
import threading

parser = argparse.ArgumentParser()
parser.add_argument('agent'); parser.add_argument('--runner')
args = parser.parse_args()
process = subprocess.Popen(([args.runner] if args.runner else []) + [args.agent],
    stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
received = queue.Queue()


def exact(stream, size):
    out = bytearray()
    while len(out) < size:
        data = stream.read(size - len(out))
        if not data:
            raise EOFError()
        out.extend(data)
    return bytes(out)


def read():
    try:
        while True:
            size, = struct.unpack('!I', exact(process.stdout, 4))
            assert 5 <= size <= 66048
            received.put(exact(process.stdout, size))
    except (EOFError, OSError):
        pass


def send(op, ident, body=b''):
    process.stdin.write(struct.pack('!IcI', 5 + len(body), op, ident) + body)
    process.stdin.flush()


threading.Thread(target=read, daemon=True).start()
try:
    assert received.get(timeout=10) == b'H\0\0\0\0Host2VMRelay-UDP/1'
    send(b'P', 1, b'nonce')
    assert received.get(timeout=5) == b'R\0\0\0\1nonce'
    with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as echo:
        echo.bind(('127.0.0.1', 0)); echo.settimeout(5)
        address = b'\0\0\0\1\x7f\0\0\1' + struct.pack('!H', echo.getsockname()[1])
        for payload in (b'', b'hello', bytes(range(256)) * 255):
            send(b'D', 2, address + payload)
            data, peer = echo.recvfrom(65535)
            assert data == payload
            echo.sendto(data, peer)
            assert received.get(timeout=5) == b'D\0\0\0\2' + address + payload
        # The SOCKS domain form must also accept an IPv4 literal.
        domain = b'\0\0\0\3\x09127.0.0.1' + struct.pack('!H', echo.getsockname()[1])
        send(b'D', 3, domain + b'domain')
        data, peer = echo.recvfrom(65535); assert data == b'domain'
        echo.sendto(data, peer)
        assert received.get(timeout=5) == b'D\0\0\0\3' + address + b'domain'
        send(b'C', 2); send(b'C', 3)
        # Fragmented datagrams must never reach a destination socket.
        send(b'D', 4, b'\0\0\1' + address[3:] + b'invalid')
        assert received.get(timeout=5).startswith(b'E\0\0\0\4')
    # A valid health probe still works after malformed data and closure.
    send(b'P', 5, b'after-close')
    assert received.get(timeout=5) == b'R\0\0\0\5after-close'
    process.stdin.close(); process.wait(timeout=5)
    assert process.returncode == 0
    print('PASS agent: handshake, UDP health, empty/large datagrams, address forms, malformed input, close and parent EOF')
finally:
    if process.poll() is None:
        process.kill(); process.wait(timeout=5)
