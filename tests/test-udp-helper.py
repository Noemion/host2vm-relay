"""Runs the shipped helper through real pipes and real UDP sockets (no mock server)."""
import os, pathlib, queue, socket, struct, subprocess, sys, threading, time, unittest
sys.path.insert(0, str(pathlib.Path(__file__).resolve().parents[1] / 'src' / 'Networking'))
import udp_bridge as wire

class HelperTests(unittest.TestCase):
    def setUp(self):
        # Only reserved test names are injected. All other calls use the OS resolver;
        # the production helper runs unchanged through real pipes and sockets.
        bootstrap = '''import socket,time,runpy
real = socket.getaddrinfo
calls = {}
def resolve(host, port, *args):
    if host.endswith('.review.test'):
        calls[host] = calls.get(host, 0) + 1
        if host.startswith('slow'): time.sleep(4)
        if host.startswith('close'): time.sleep(.5)
        if host.startswith('cached') and calls[host] > 1: raise OSError('cache miss')
        host = '127.0.0.1'
    return real(host, port, *args)
socket.getaddrinfo = resolve
runpy.run_path(''' + repr(wire.__file__) + ''', run_name='__main__')'''
        self.process = subprocess.Popen([sys.executable, '-I', '-u', '-c', bootstrap], stdin=subprocess.PIPE, stdout=subprocess.PIPE, stderr=subprocess.PIPE)
        self.replies = queue.Queue()
        def read():
            try:
                while True:
                    n = struct.unpack('!I', wire.read_exact(self.process.stdout, 4))[0]
                    self.replies.put(wire.read_exact(self.process.stdout, n))
            except (EOFError, OSError):
                pass
        threading.Thread(target=read, daemon=True).start()
        self.assertEqual(self.replies.get(timeout=5), b'H\0\0\0\0Host2VMRelay-UDP/1')
    def tearDown(self):
        self.process.stdin.close()
        try: self.process.wait(timeout=3)
        except subprocess.TimeoutExpired: self.process.kill(); self.fail('helper outlived its input/SSH session')
        self.process.stdout.close(); self.process.stderr.close()
    def send(self, opcode, ident, data=b''):
        b = opcode + struct.pack('!I', ident) + data
        self.process.stdin.write(struct.pack('!I', len(b)) + b); self.process.stdin.flush()
    def domain(self, host, port, data=b'request'):
        name = host.encode('ascii')
        return b'\0\0\0\3' + bytes([len(name)]) + name + struct.pack('!H', port) + data

    def test_slow_dns_does_not_block_probe_or_numeric_traffic(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as srv:
            srv.bind(('127.0.0.1', 0)); srv.settimeout(1)
            for ident in range(1, 5):
                self.send(b'D', ident, self.domain('slow%d.review.test' % ident, srv.getsockname()[1]))
            start = time.monotonic()
            self.send(b'P', 50, b'responsive')
            self.assertEqual(self.replies.get(timeout=1), b'R' + struct.pack('!I', 50) + b'responsive')
            self.assertLess(time.monotonic() - start, 1)
            self.send(b'D', 60, wire.encode(srv.getsockname(), b'numeric'))
            packet, address = srv.recvfrom(100)
            self.assertEqual(packet, b'numeric')
            srv.sendto(b'reply', address)
            self.assertEqual(wire.decode(self.replies.get(timeout=1)[5:])[-1], b'reply')
            for _ in range(4):
                self.assertIn(b'DNS lookup timed out', self.replies.get(timeout=3))
            time.sleep(2.3)  # The OS lookup eventually returns after its logical deadline.
            with self.assertRaises(socket.timeout): srv.recvfrom(100)
            self.send(b'P', 51, b'still alive')
            self.assertEqual(self.replies.get(timeout=1)[5:], b'still alive')

    def test_close_discards_pending_dns_and_success_is_cached(self):
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as srv:
            srv.bind(('127.0.0.1', 0)); srv.settimeout(1)
            port = srv.getsockname()[1]
            self.send(b'D', 1, self.domain('close.review.test', port))
            self.send(b'C', 1)
            with self.assertRaises(socket.timeout): srv.recvfrom(100)
            for ident in (2, 3):
                self.send(b'D', ident, self.domain('cached.review.test', port))
                data, address = srv.recvfrom(100)
                self.assertEqual(data, b'request')
                srv.sendto(b'cached', address)
                self.assertEqual(wire.decode(self.replies.get(timeout=1)[5:])[-1], b'cached')

    def test_shutdown_does_not_wait_for_stuck_resolver(self):
        self.send(b'D', 1, self.domain('slow.review.test', 12345))
        self.send(b'P', 2, b'ready')
        self.assertEqual(self.replies.get(timeout=1)[5:], b'ready')
        # tearDown closes stdin and requires the process to exit before DNS completes.
    def test_sessions_binary_domain_and_size(self):
        with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as srv:
            srv.bind(('127.0.0.1',0));srv.settimeout(5)
            port=struct.pack('!H',srv.getsockname()[1]); data=os.urandom(1200)
            addrs=[]
            for ident in (1,2):
                self.send(b'D',ident,b'\0\0\0\3\t127.0.0.1'+port+data)
                actual,addr=srv.recvfrom(65535);self.assertEqual(actual,data);addrs.append(addr)
                srv.sendto(data[::-1],addr)
                result=self.replies.get(timeout=5);self.assertEqual(struct.unpack('!I',result[1:5])[0],ident)
                self.assertEqual(wire.decode(result[5:])[-1],data[::-1])
            self.assertNotEqual(addrs[0],addrs[1])
            self.send(b'C',1); self.send(b'P',4,b'closed');self.assertEqual(self.replies.get(timeout=3)[:1],b'R')
            srv.sendto(b'stale',addrs[0])
            with self.assertRaises(queue.Empty):self.replies.get(timeout=.2)
    def test_bad_packets_do_not_kill_process(self):
        for data in (b'\0\0\1\1'+b'\x7f\0\0\1'+b'\0\1',b'\0\0\0\x04',wire.encode(('224.0.0.1',123),b'data')):
            self.send(b'D',5,data);self.assertEqual(self.replies.get(timeout=3)[:1],b'E')
        self.send(b'P',2,b'ok');self.assertEqual(self.replies.get(timeout=3)[5:],b'ok')
    def test_ipv6(self):
        try:
            srv=socket.socket(socket.AF_INET6,socket.SOCK_DGRAM);srv.bind(('::1',0))
        except OSError:self.skipTest('IPv6 loopback unavailable')
        with srv:
            srv.settimeout(3);self.send(b'D',8,wire.encode(srv.getsockname(),b'v6'))
            data,addr=srv.recvfrom(4096);self.assertEqual(data,b'v6');srv.sendto(b'v6reply',addr)
            self.assertEqual(wire.decode(self.replies.get(timeout=3)[5:])[-1],b'v6reply')

if __name__=='__main__':unittest.main(verbosity=2)
