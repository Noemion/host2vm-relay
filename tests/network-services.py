"""Deterministic local services for namespace integration checks."""
import argparse, socket, threading, socketserver, time
p=argparse.ArgumentParser();p.add_argument('--host',required=True);p.add_argument('--label',required=True);a=p.parse_args()
class TCP(socketserver.BaseRequestHandler):
 def handle(self):
  self.request.settimeout(5);self.request.recv(8192)
  body=a.label.encode();self.request.sendall(b'HTTP/1.1 200 OK\r\nContent-Length: '+str(len(body)).encode()+b'\r\nConnection: close\r\n\r\n'+body)
class Server(socketserver.ThreadingTCPServer):allow_reuse_address=True;daemon_threads=True
server=Server((a.host,18080),TCP);threading.Thread(target=server.serve_forever,daemon=True).start()
with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as udp:
 udp.bind((a.host,18081))
 while True:
  data,peer=udp.recvfrom(65535);udp.sendto(a.label.encode()+b':'+data,peer)
