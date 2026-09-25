const dgram = require('dgram');
const assert = require('assert');
const socket = dgram.createSocket('udp4');
const header = Buffer.from([0x43,0x21,1,0,0,1,0,0,0,0,0,0]);
const qname = Buffer.concat('code.example.com'.split('.').map(s=>Buffer.concat([Buffer.from([s.length]),Buffer.from(s)])));
const query = Buffer.concat([header,qname,Buffer.from([0,0,1,0,1])]);
const timer = setTimeout(()=>{socket.close();console.error('DNS timeout');process.exitCode=1;},5000);
socket.on('message',message=>{
  clearTimeout(timer);
  try {
    assert.equal(message.readUInt16BE(0),0x4321);
    assert.equal(message[3]&15,0);
    assert(message.readUInt16BE(6)>0);
    const ip = [...message.subarray(message.length-4)];
    assert.equal(ip[0],198); assert.equal(ip[1],18);
    console.log('PASS actual Mihomo DNS returns company Fake-IP: '+ip.join('.'));
  } catch(e) { console.error(e);process.exitCode=1; }
  socket.close();
});
socket.send(query,10553,'127.0.0.1');
