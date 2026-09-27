"""Real OpenSSH acceptance in an isolated local account; no production VM access.

Run as root on Linux. Keys/passwords live only in a private temporary directory
and inherited pipes; reports contain assertions and resource samples only.
"""
import asyncio
import hashlib
import json
import os
from pathlib import Path
import resource
import secrets
import shutil
import socket
import struct
import subprocess
import tempfile
import time

ROOT = Path(__file__).resolve().parents[1]
BUNDLE = ROOT / 'artifacts/native/linux-x64'
OUT = ROOT / 'artifacts/checks/rust'
REPORT = []


def check(value, name, **detail):
    if not value:
        raise AssertionError(name)
    REPORT.append(dict(name=name, status='PASS', **detail))
    print('PASS ' + name, flush=True)


def run(*args, **kwargs):
    return subprocess.run(args, check=True, capture_output=True, text=True, **kwargs)


class Core:
    async def start(self, port, user, key, password='', limit=512, accept=True):
        self.token = secrets.token_hex(32)
        self.process = await asyncio.create_subprocess_exec(str(BUNDLE / 'h2vm-core'),
            stdin=asyncio.subprocess.PIPE, stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
        await self.write(dict(version=1, host='127.0.0.1', port=port, user=user, useKey=bool(key),
            keyPath=str(key or ''), secret=password, token=self.token, maxConnections=limit, agentDirectory=str(BUNDLE)))
        event = await self.read()
        check(event.get('event') == 'hostKey' and event['fingerprint'].startswith('SHA256:'), 'host identity precedes authentication')
        await self.write(dict(op='trust', accept=accept))
        if not accept:
            await asyncio.wait_for(self.process.wait(), 5)
            check(self.process.returncode != 0, 'rejected host key never opens forwarding port')
            return
        ready = await self.read()
        check(ready.get('event') == 'ready' and ready['version'] == 1, 'authenticated worker becomes ready')
        self.port = ready['port']
        return self

    async def write(self, value):
        self.process.stdin.write(json.dumps(value).encode() + b'\n')
        await self.process.stdin.drain()

    async def read(self):
        data = await asyncio.wait_for(self.process.stdout.readline(), 45)
        if not data:
            raise AssertionError('worker exited: ' + (await self.process.stderr.read()).decode())
        return json.loads(data)

    async def request(self, op):
        await self.write(dict(id=1, op=op))
        result = await self.read()
        check(result.get('ok'), 'control request ' + op, result=result)

    async def open(self, port=0, op=1, token=None):
        reader, writer = await asyncio.open_connection('127.0.0.1', self.port)
        writer.write(b'\x05\x01\x02')
        assert await reader.readexactly(2) == b'\x05\x02'
        capability = (token if token is not None else self.token).encode()
        writer.write(b'\x01\x04h2vm' + bytes([len(capability)]) + capability)
        reply = await reader.readexactly(2)
        if reply != b'\x01\x00':
            writer.close(); await writer.wait_closed(); return None
        writer.write(bytes([5, op, 0, 1, 127, 0, 0, 1]) + struct.pack('!H', port))
        reply = await reader.readexactly(10)
        if reply[1]:
            writer.close(); await writer.wait_closed(); raise AssertionError('forwarding rejected')
        return reader, writer

    async def close(self):
        if self.process.returncode is None:
            self.process.stdin.close()
            await asyncio.wait_for(self.process.wait(), 5)
        check(self.process.returncode == 0, 'parent pipe EOF terminates worker')


async def read_frame(reader):
    n, = struct.unpack('!I', await reader.readexactly(4))
    assert 5 <= n <= 66048
    data = await reader.readexactly(n)
    return data[0:1], struct.unpack('!I', data[1:5])[0], data[5:]


def frame(op, ident, body):
    return struct.pack('!IcI', len(body) + 5, op, ident) + body


async def udp_test(core):
    await core.request('udp')
    reader, writer = await core.open(op=0xf0)
    check(await read_frame(reader) == (b'H', 0, b'Host2VMRelay-UDP/1'), 'uploaded static helper protocol handshake')
    nonce = secrets.token_bytes(16)
    writer.write(frame(b'P', 42, nonce))
    check(await read_frame(reader) == (b'R', 42, nonce), 'real VM UDP loopback health')
    sock = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock.bind(('127.0.0.1', 0)); sock.setblocking(False)
    loop = asyncio.get_running_loop()
    for body in [b'', secrets.token_bytes(1200), secrets.token_bytes(65507)]:
        address = b'\0\0\0\1\x7f\0\0\1' + struct.pack('!H', sock.getsockname()[1])
        writer.write(frame(b'D', 9, address + body))
        data, peer = await asyncio.wait_for(loop.sock_recvfrom(sock, 65535), 5)
        assert data == body
        await loop.sock_sendto(sock, data, peer)
        check(await asyncio.wait_for(read_frame(reader), 5) == (b'D', 9, address + body),
              'SSH UDP preserves datagram ' + str(len(body)))
    writer.write(frame(b'C', 9, b'')); writer.close(); await writer.wait_closed(); sock.close()


def sample(pid):
    fields = dict(line.split(':', 1) for line in Path(f'/proc/{pid}/status').read_text().splitlines() if ':' in line)
    return dict(rss=fields['VmRSS'].strip(), threads=int(fields['Threads']), handles=len(list(Path(f'/proc/{pid}/fd').iterdir())))


async def scenario(port, user, key, encrypted, password):
    denied = Core(); await denied.start(port, user, key, accept=False)
    for auth_key, secret in [(None, password), (encrypted, 'test-passphrase')]:
        core = await Core().start(port, user, auth_key, secret)
        await core.request('probe'); await core.close()
    async def echo(reader, writer):
        try:
            while data := await reader.read(32768):
                writer.write(data); await writer.drain()
            writer.write(b'after-eof'); await writer.drain()
        except (ConnectionError, asyncio.CancelledError):
            pass
        finally:
            writer.close()
    async with await asyncio.start_server(echo, '127.0.0.1', 0) as server:
        echo_port = server.sockets[0].getsockname()[1]
        if harness := os.environ.get('H2VM_WINDOWS_HARNESS'):
            fingerprint = run('ssh-keygen', '-lf', str(key.parent / 'host.pub'), '-E', 'sha256').stdout.split()[1]
            windows_key = '\\\\wsl.localhost\\alma10' + str(key).replace('/', '\\')
            child = await asyncio.create_subprocess_exec('/mnt/c/Program Files/dotnet/dotnet.exe', harness,
                '--session-check', '127.0.0.1', str(port), user, windows_key, fingerprint, str(echo_port),
                stdout=asyncio.subprocess.PIPE, stderr=asyncio.subprocess.PIPE)
            output, errors = await asyncio.wait_for(child.communicate(), 90)
            if child.returncode:
                raise AssertionError(output.decode(errors='replace') + errors.decode(errors='replace'))
            check(True, 'Windows production RelaySession through Rust and real OpenSSH', detail=output.decode().strip())
        for count in [512, 2048]:
            core = await Core().start(port, user, key, limit=count)
            check(await core.open(echo_port, token='0' * 64) is None, 'internal endpoint rejects wrong capability')
            started = time.monotonic()
            # Bound simultaneous handshakes while retaining every established
            # channel; this measures live concurrency, not a sequential loop.
            admission = asyncio.Semaphore(64)
            async def connect():
                async with admission:
                    return await core.open(echo_port)
            connections = await asyncio.wait_for(asyncio.gather(*(connect() for _ in range(count))), 60)
            stats = sample(core.process.pid)
            await core.request('probe')
            await udp_test(core)
            async def transfer(pair):
                reader, writer = pair
                data = secrets.token_bytes(16384)
                writer.write(data); await writer.drain()
                assert await reader.readexactly(len(data)) == data
                writer.write_eof()
                assert await reader.read() == b'after-eof'
                writer.close(); await writer.wait_closed()
            await asyncio.wait_for(asyncio.gather(*(transfer(c) for c in connections)), 60)
            check(True, f'{count} real SSH channels transfer and preserve half-close', seconds=round(time.monotonic()-started, 3), **stats)
            # Close the parent while a live SSH forwarding task is awaiting data.
            reader, writer = await core.open(echo_port)
            await core.close()
            check(await asyncio.wait_for(reader.read(), 5) == b'', 'parent shutdown closes active TCP channel')
            writer.close(); await writer.wait_closed()


def main():
    OUT.mkdir(parents=True, exist_ok=True)
    Path('/run/sshd').mkdir(exist_ok=True)
    resource.setrlimit(resource.RLIMIT_NOFILE, (16384, 16384))
    temporary = Path(tempfile.mkdtemp(prefix='h2vm-rust-')); temporary.chmod(0o755)
    user = 'h2vmrt' + str(os.getpid())
    sshd = None; created = False
    try:
        home = temporary / 'home'; home.mkdir()
        run('useradd', '-M', '-d', str(home), '-s', '/bin/sh', user); created = True
        run('chown', user, str(home))
        password = secrets.token_hex(24)
        run('chpasswd', input=user + ':' + password + '\n')
        key = temporary / 'client'; hostkey = temporary / 'host'; encrypted = temporary / 'encrypted'
        for path, phrase in [(key, ''), (hostkey, ''), (encrypted, 'test-passphrase')]:
            run('ssh-keygen', '-q', '-t', 'ed25519', '-N', phrase, '-f', str(path))
        auth = temporary / 'authorized_keys'
        auth.write_text(key.with_suffix('.pub').read_text() + encrypted.with_suffix('.pub').read_text()); auth.chmod(0o644)
        listener = socket.socket(); listener.bind(('127.0.0.1', 0)); port = listener.getsockname()[1]; listener.close()
        config = temporary / 'sshd_config'
        config.write_text(f'Port {port}\nListenAddress 127.0.0.1\nHostKey {hostkey}\nAuthorizedKeysFile {auth}\n'
            f'AllowUsers {user}\nStrictModes no\nPasswordAuthentication yes\nUsePAM no\nAllowTcpForwarding yes\n'
            f'PidFile {temporary}/pid\nLogLevel ERROR\n')
        sshd = subprocess.Popen(['/usr/sbin/sshd', '-D', '-e', '-f', str(config)], stderr=subprocess.DEVNULL)
        time.sleep(.4)
        if sshd.poll() is not None:
            raise AssertionError('isolated sshd failed to start')
        asyncio.run(scenario(port, user, key, encrypted, password))
    finally:
        if sshd: sshd.terminate(); sshd.wait(timeout=5)
        if created: run('userdel', user)
        shutil.rmtree(temporary)
        (OUT / 'ssh-acceptance.json').write_text(json.dumps(REPORT, indent=2))


if __name__ == '__main__':
    main()
