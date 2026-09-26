"""An ordinary TCP/UDP client: no SOCKS, proxy or alternative destination support."""
import socket, sys
kind,host,port,value=sys.argv[1],sys.argv[2],int(sys.argv[3]),sys.argv[4].encode()
if kind=='tcp':
 with socket.create_connection((host,port),timeout=3) as s:
  s.sendall(b'GET / HTTP/1.1\r\nHost: '+host.encode()+b'\r\nConnection: close\r\n\r\n');b=b''
  while True:
   x=s.recv(8192)
   if not x:break
   b+=x
  print(b.split(b'\r\n\r\n',1)[1].decode())
else:
 with socket.socket(socket.AF_INET,socket.SOCK_DGRAM) as s:
  s.settimeout(3);s.sendto(value,(host,port));print(s.recvfrom(65535)[0].decode())
