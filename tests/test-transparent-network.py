"""Linux namespace acceptance: original TCP/UDP clients -> real Mihomo TUN -> SSH.

Requires root in an isolated CI runner. The host and VM expose the same service
address with different replies, making the selected path observable. No external
application service is contacted. Private keys stay outside the uploaded evidence.
"""
import concurrent.futures
import hashlib
import json
import os
from pathlib import Path
import shutil
import signal
import subprocess
import sys
import tempfile
import time
import urllib.request
import gzip

ROOT = Path(__file__).resolve().parents[1]
OUT = ROOT / 'artifacts' / 'checks' / 'network'
OUT.mkdir(parents=True, exist_ok=True)
TMP = Path(tempfile.mkdtemp(prefix='h2vm-network-'))
TMP.chmod(0o755)
CLIENT = 'h2vm-client'
VM = 'h2vm-vm'
BRIDGE = 'h2vmbr'
PROCESSES = []
REPORT = []
FILES = []


def run(*args, check=True):
    return subprocess.run([str(x) for x in args], check=check, text=True, capture_output=True, timeout=30)


def start(args, log):
    f = open(OUT / log, 'w'); FILES.append(f)
    p = subprocess.Popen([str(x) for x in args], stdout=f, stderr=subprocess.STDOUT)
    PROCESSES.append(p)
    return p


def record(name, detail=''):
    REPORT.append({'name': name, 'status': 'PASS', 'detail': detail})
    print('PASS ' + name, flush=True)


def client(kind, host='198.19.0.2', value='test'):
    result = run('ip', 'netns', 'exec', CLIENT, sys.executable, ROOT / 'tests/network-client.py',
                 kind, host, 18080 if kind == 'tcp' else 18081, value, check=False)
    return result.stdout.strip() if result.returncode == 0 else 'ERROR'


def expect_path(kind, expected, timeout=30, host='198.19.0.2', value='probe'):
    until = time.monotonic() + timeout
    last = None
    while time.monotonic() < until:
        last = client(kind, host, value)
        if last == expected:
            return
        time.sleep(.7)
    raise AssertionError(f'{kind} {host}: expected {expected!r}, got {last!r}')


def service(namespace, host, label):
    return start((['ip', 'netns', 'exec', namespace] if namespace else []) +
                 [sys.executable, '-u', ROOT / 'tests/network-services.py', '--host', host, '--label', label], label + '-service.log')


def download_mihomo():
    name = 'mihomo-linux-amd64-v1-v1.19.31.gz'
    url = 'https://api.github.com/repos/MetaCubeX/mihomo/releases/tags/v1.19.31'
    headers = {'User-Agent': 'Host2VMRelay-integration'}
    if os.environ.get('GH_TOKEN'):
        headers['Authorization'] = 'Bearer ' + os.environ['GH_TOKEN']
    data = json.load(urllib.request.urlopen(urllib.request.Request(url, headers=headers), timeout=30))
    assets = {a['name']: a for a in data['assets']}
    asset = assets.get(name) or assets.get('mihomo-linux-amd64-compatible-v1.19.31.gz')
    if asset is None or not str(asset.get('digest', '')).startswith('sha256:'):
        raise RuntimeError('Pinned Mihomo release asset/digest unavailable')
    blob = urllib.request.urlopen(asset['browser_download_url'], timeout=60).read()
    digest = hashlib.sha256(blob).hexdigest()
    if 'sha256:' + digest != asset['digest']:
        raise RuntimeError('Mihomo download digest mismatch')
    exe = TMP / 'mihomo'; exe.write_bytes(gzip.decompress(blob)); exe.chmod(0o755)
    (OUT / 'mihomo-version.json').write_text(json.dumps({'tag': data['tag_name'], 'asset': asset['name'], 'sha256': digest}, indent=2))
    return exe


def setup():
    if os.geteuid() != 0:
        raise RuntimeError('Run in a disposable Linux test environment with sudo')
    run('ip', 'link', 'add', BRIDGE, 'type', 'bridge')
    run('ip', 'addr', 'add', '10.203.0.1/24', 'dev', BRIDGE); run('ip', 'link', 'set', BRIDGE, 'up')
    for ns, ip, tag in [(CLIENT, '10.203.0.10', 'hc'), (VM, '10.203.0.20', 'hv')]:
        run('ip', 'netns', 'add', ns)
        run('ip', 'link', 'add', tag + 'b', 'type', 'veth', 'peer', 'name', tag + 'n')
        run('ip', 'link', 'set', tag + 'b', 'master', BRIDGE); run('ip', 'link', 'set', tag + 'b', 'up')
        run('ip', 'link', 'set', tag + 'n', 'netns', ns)
        run('ip', '-n', ns, 'link', 'set', tag + 'n', 'name', 'eth0')
        run('ip', '-n', ns, 'addr', 'add', ip + '/24', 'dev', 'eth0')
        run('ip', '-n', ns, 'link', 'set', 'lo', 'up'); run('ip', '-n', ns, 'link', 'set', 'eth0', 'up')
        run('ip', '-n', ns, 'route', 'add', 'default', 'via', '10.203.0.1')
        mount = Path('/etc/netns') / ns; mount.mkdir(parents=True, exist_ok=True)
        (mount / 'hosts').write_text('127.0.0.1 localhost\n::1 localhost\n' + ('198.19.0.3 intranet.h2vm.test vm-only.h2vm.test\n198.19.0.2 shared.h2vm.test\n' if ns == VM else ''))
        (mount / 'resolv.conf').write_text('nameserver 127.0.0.1\noptions timeout:1 attempts:1\n')
    run('ip', 'addr', 'add', '198.19.0.2/32', 'dev', 'lo')
    run('ip', '-n', VM, 'addr', 'add', '198.19.0.2/32', 'dev', 'lo')
    run('ip', '-n', VM, 'addr', 'add', '198.19.0.3/32', 'dev', 'lo')
    run('ip', 'netns', 'exec', CLIENT, 'sysctl', '-w', 'net.ipv4.conf.all.rp_filter=0', 'net.ipv4.conf.eth0.rp_filter=0')
    run('ip', 'netns', 'exec', VM, 'sysctl', '-w', 'net.ipv4.conf.all.rp_filter=0', 'net.ipv4.conf.eth0.rp_filter=0')
    start([sys.executable, '-u', ROOT / 'tests/network-dns.py', '10.203.0.1', '15353'], 'corporate-dns.log')
    service(None, '198.19.0.2', 'HOST'); service(VM, '198.19.0.2', 'VM'); service(VM, '198.19.0.3', 'PRIVATE')
    run('useradd', '-M', '-s', '/bin/sh', 'h2vmtest', check=False)
    run('usermod', '-p', 'x', 'h2vmtest')
    run('ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-f', TMP / 'hostkey')
    run('ssh-keygen', '-q', '-t', 'ed25519', '-N', '', '-f', TMP / 'clientkey')
    auth = TMP / 'authorized_keys'; shutil.copyfile(TMP / 'clientkey.pub', auth); auth.chmod(0o644)
    (TMP / 'bin').mkdir(); python = TMP / 'bin/python3'
    python.write_text('#!/bin/sh\n[ ! -e "' + str(TMP / 'block-udp') + '" ] || exit 7\nexec /usr/bin/python3 "$@"\n'); python.chmod(0o755)
    cfg = TMP / 'sshd_config'
    cfg.write_text(f'Port 2222\nListenAddress 10.203.0.20\nHostKey {TMP}/hostkey\nPidFile {TMP}/sshd.pid\nAuthorizedKeysFile {auth}\nStrictModes no\nPasswordAuthentication no\nKbdInteractiveAuthentication no\nUsePAM no\nAllowUsers h2vmtest\nAllowTcpForwarding yes\nSetEnv PATH={TMP}/bin:/usr/bin:/bin\n')
    Path('/run/sshd').mkdir(exist_ok=True)
    start(['ip', 'netns', 'exec', VM, '/usr/sbin/sshd', '-D', '-e', '-f', cfg], 'sshd.log')
    fingerprint = run('ssh-keygen', '-lf', TMP / 'hostkey.pub', '-E', 'sha256').stdout.split()[1]
    return fingerprint


def tests():
    core = download_mihomo(); fingerprint = setup(); time.sleep(1)
    expect_path('tcp', 'HOST'); expect_path('udp', 'HOST:probe')
    assert client('udp', '198.19.0.3') == 'ERROR'
    record('baseline original host access and private destination unreachable')
    dll = ROOT / 'artifacts/transport/bin/Release/net8.0/TransportHarness.dll'
    script = OUT / 'generated.js'
    harness = start(['ip', 'netns', 'exec', CLIENT, 'dotnet', dll, '10.203.0.20', 2222, 'h2vmtest', TMP / 'clientkey', fingerprint, script], 'transport.log')
    for _ in range(150):
        if script.exists(): break
        if harness.poll() is not None: raise RuntimeError('transport process failed')
        time.sleep(.1)
    run('node', ROOT / 'tests/generate-network-config.cjs', script, OUT / 'mihomo.json')
    start(['ip', 'netns', 'exec', CLIENT, core, '-d', TMP, '-f', OUT / 'mihomo.json'], 'mihomo.log')
    time.sleep(1)
    (OUT / 'routes.txt').write_text(run('ip', '-n', CLIENT, 'route', 'show', 'table', 'all').stdout + run('ip', '-n', CLIENT, 'rule', 'show').stdout)
    expect_path('udp', 'VM:probe', timeout=40); expect_path('tcp', 'VM')
    record('original IP/port TCP and UDP clients transparently use VM through TUN')
    expect_path('udp', 'PRIVATE:probe', host='198.19.0.3'); expect_path('tcp', 'PRIVATE', host='198.19.0.3')
    record('VM-only address is reachable without changing the client or address')
    expect_path('udp', 'PRIVATE:probe', host='intranet.h2vm.test'); expect_path('tcp', 'PRIVATE', host='intranet.h2vm.test')
    record('unchanged hostname through fake-IP and preserved original corporate DNS')
    expect_path('tcp', 'PRIVATE', host='vm-only.h2vm.test')
    assert client('udp', 'vm-only.h2vm.test') == 'ERROR'
    record('DNS boundary guard: VM-only hosts name works for TCP but UDP requires Clash DNS',
           'Not certified as automatic VM-only hostname resolution; resolver precondition is documented.')
    expect_path('udp', 'VM:', value='')
    with concurrent.futures.ThreadPoolExecutor(max_workers=8) as pool:
        values = list(pool.map(lambda i: client('udp', value='session-' + str(i)), range(16)))
    assert values == ['VM:session-' + str(i) for i in range(16)]
    record('empty UDP datagrams and 16 isolated concurrent client sessions')
    (TMP / 'block-udp').touch()
    for pid in run('ip', 'netns', 'pids', VM).stdout.split():
        try:
            if b'base64.b64decode' in Path('/proc/' + pid + '/cmdline').read_bytes(): os.kill(int(pid), signal.SIGTERM)
        except ProcessLookupError: pass
    expect_path('udp', 'HOST:probe'); expect_path('tcp', 'VM')
    record('UDP-only failure falls back to original host policy without breaking VM TCP')
    (TMP / 'block-udp').unlink(); expect_path('udp', 'VM:probe', timeout=40)
    record('UDP component automatically recovers and VM routing resumes')
    run('ip', '-n', VM, 'link', 'set', 'eth0', 'down')
    expect_path('udp', 'HOST:probe'); expect_path('tcp', 'HOST')
    expect_path('tcp', 'HOST', host='shared.h2vm.test'); expect_path('udp', 'HOST:probe', host='shared.h2vm.test')
    record('VM network loss automatically falls back for TCP and UDP including unchanged names')
    run('ip', '-n', VM, 'link', 'set', 'eth0', 'up')
    expect_path('udp', 'VM:probe', timeout=50); expect_path('tcp', 'VM')
    record('VM network restoration automatically reconnects and restores VM priority')
    harness.kill(); harness.wait(timeout=5)
    expect_path('udp', 'HOST:probe'); expect_path('tcp', 'HOST')
    expect_path('tcp', 'HOST', host='shared.h2vm.test'); expect_path('udp', 'HOST:probe', host='shared.h2vm.test')
    record('application crash with stale active rules still selects PASS and original host DNS/path')
    text = (OUT / 'mihomo.log').read_text(errors='replace')
    assert 'ExistingHostPolicy' in text and 'Host2VMRelay' in text
    record('Mihomo logs confirm preserved original policy rather than forced DIRECT')


try:
    tests()
except Exception as exc:
    REPORT.append({'name': 'acceptance', 'status': 'FAIL', 'detail': repr(exc)})
    print('FAIL', repr(exc), file=sys.stderr)
    raise
finally:
    (OUT / 'report.json').write_text(json.dumps(REPORT, indent=2))
    for p in reversed(PROCESSES):
        if p.poll() is None:
            p.terminate()
            try: p.wait(timeout=2)
            except subprocess.TimeoutExpired: p.kill()
    for ns in [CLIENT, VM]:
        for pid in run('ip', 'netns', 'pids', ns, check=False).stdout.split():
            try: os.kill(int(pid), signal.SIGKILL)
            except ProcessLookupError: pass
        run('ip', 'netns', 'del', ns, check=False)
        shutil.rmtree('/etc/netns/' + ns, ignore_errors=True)
    run('ip', 'link', 'del', BRIDGE, check=False)
    run('ip', 'addr', 'del', '198.19.0.2/32', 'dev', 'lo', check=False)
    for f in FILES: f.close()
    shutil.rmtree(TMP, ignore_errors=True)
