'use strict';
const fs=require('fs'),assert=require('assert/strict');
const png=Buffer.from('89504e470d0a1a0a','hex');
function frames(data){
 assert(data.length>=6 && data.readUInt16LE(0)===0 && data.readUInt16LE(2)===1,'ICO header');
 const n=data.readUInt16LE(4), out=[]; assert(n>0 && 6+n*16<=data.length,'ICO directory');
 for(let i=0;i<n;i++){
  const p=6+i*16,w=data[p]||256,h=data[p+1]||256,length=data.readUInt32LE(p+8),offset=data.readUInt32LE(p+12);
  assert(w===h && offset>=6+n*16 && length>0 && offset+length<=data.length,'ICO frame bounds');
  out.push({w,h,bytes:data.subarray(offset,offset+length)});
 }
 return out;
}
function check(file){
 const images=frames(fs.readFileSync(file));
 assert.deepEqual(images.map(f=>f.w),[16,20,24,32,40,48,64,128,256]);
 for(const f of images){
  if(f.w===256){assert(f.bytes.subarray(0,8).equals(png));assert.equal(f.bytes.readUInt32BE(16),256);continue;}
  const b=f.bytes,n=f.w,stride=Math.ceil(n/32)*4;
  assert.equal(b.readUInt32LE(0),40);assert.equal(b.readInt32LE(4),n);assert.equal(b.readInt32LE(8),n*2);
  assert.equal(b.readUInt16LE(12),1);assert.equal(b.readUInt16LE(14),32);assert.equal(b.readUInt32LE(16),0);
  assert.equal(b.length,40+4*n*n+stride*n);
  let visible=0;
  for(let y=0;y<n;y++)for(let x=0;x<n;x++){
   const a=b[40+(y*n+x)*4+3]; if(a)visible++;
   const mask=(b[40+4*n*n+y*stride+Math.floor(x/8)]>>(7-x%8))&1;
   assert.equal(mask,a===0?1:0,'AND mask agrees with zero-alpha pixels');
  }
  assert(visible>n*n/10,'Frame must not be blank');
 }
 console.log('PASS setup ICO: DIB frames 16–128, PNG 256, alpha/mask/bounds');
}
if(require.main===module)check(process.argv[2]||'artifacts/assets/Host2VMRelay.Setup.ico');
module.exports={frames,check};
