'use strict';
const fs=require('fs'),path=require('path'),assert=require('assert/strict');
const root=path.join(__dirname,'..');
const ico=fs.readFileSync(path.join(root,'artifacts/assets/Host2VMRelay.ico'));
assert.equal(ico.readUInt16LE(0),0);assert.equal(ico.readUInt16LE(2),1);
const sizes=[];
for(let i=0;i<ico.readUInt16LE(4);i++){
 const p=6+i*16,n=ico[p]||256,length=ico.readUInt32LE(p+8),offset=ico.readUInt32LE(p+12);
 assert.equal(ico[p+1]||256,n);assert(offset+length<=ico.length);
 assert.equal(ico.subarray(offset+1,offset+4).toString(),'PNG');
 assert.equal(ico.readUInt32BE(offset+16),n);assert.equal(ico.readUInt32BE(offset+20),n);sizes.push(n);
}
assert.deepEqual(sizes,[16,20,24,32,40,48,64,128,256]);
for(const file of ['MainForm.cs','ScriptDialog.cs']){
 const text=fs.readFileSync(path.join(root,'src/UI',file),'utf8');
 assert(text.includes('AutoScaleMode.Dpi'));assert(text.includes('96F, 96F'));assert(!text.includes('GraphicsUnit.Pixel'));
}
const project=fs.readFileSync(path.join(root,'src/Host2VMRelay.csproj'),'utf8');
assert(project.includes('<ApplicationHighDpiMode>PerMonitorV2</ApplicationHighDpiMode>'));
assert(project.includes('<ApplicationIcon>$(AppIconPath)</ApplicationIcon>'));
assert(project.includes('LogicalName="Host2VMRelay.AppIcon"'));
assert(fs.readFileSync(path.join(root,'packaging/Host2VMRelay.iss'),'utf8').includes('SetupIconFile='));
console.log('PASS multi-resolution ICO frames, embedded icon, installer icon and DPI configuration');
