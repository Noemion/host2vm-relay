"""Deterministic pre-existing corporate DNS for namespace acceptance.

The relay generator must preserve this resolver. A VM-only hosts alias is deliberately
absent to verify the distinction between SSH TCP and Mihomo UDP DNS.
"""
import socket
import struct
import sys

records = {'intranet.h2vm.test': '198.19.0.3', 'shared.h2vm.test': '198.19.0.2'}

def answer(query):
    if len(query) < 17 or struct.unpack('!H', query[4:6])[0] != 1:
        return None
    position, labels = 12, []
    while position < len(query) and query[position] != 0:
        count = query[position]
        if count > 63 or position + count + 1 >= len(query):
            return None
        position += 1
        labels.append(query[position:position + count].decode('ascii').lower())
        position += count
    position += 1
    if position + 4 > len(query):
        return None
    name = '.'.join(labels)
    qtype, qclass = struct.unpack('!HH', query[position:position + 4])
    question = query[12:position + 4]
    address = records.get(name)
    flags = 0x8180 if address else 0x8183
    answers = 1 if address and qtype == 1 and qclass == 1 else 0
    header = query[:2] + struct.pack('!HHHHH', flags, 1, answers, 0, 0)
    body = b'' if not answers else b'\xc0\x0c' + struct.pack('!HHIH', 1, 1, 5, 4) + socket.inet_aton(address)
    return header + question + body

with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as server:
    server.bind((sys.argv[1], int(sys.argv[2])))
    while True:
        packet, peer = server.recvfrom(4096)
        try:
            reply = answer(packet)
            if reply:
                server.sendto(reply, peer)
        except (ValueError, UnicodeError, struct.error):
            continue
