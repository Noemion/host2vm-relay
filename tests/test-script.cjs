'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert/strict');
const path = require('path');
const root = path.join(__dirname, '..');
function execute(source, config) {
  const context = vm.createContext({});
  vm.runInContext(source, context, {timeout: 1000});
  context.input = config;
  return vm.runInContext('main(input,"test")', context, {timeout: 1000});
}
const base = () => ({proxies: [{name: 'old', type: 'mieru', udp: false}], rules: ['MATCH,DIRECT'], dns: {'fake-ip-filter': ['+.lan', '*.local', 'exact.example']}});
const fixture = process.argv[3];
assert(fixture && fs.existsSync(fixture), 'Run the C# self-test to create script-cases.json first');
const scripts = JSON.parse(fs.readFileSync(fixture, 'utf8'));
for (const mode of ['blacklist', 'whitelist', 'rule']) {
  const input = base(); input.dns['fake-ip-filter-mode'] = mode;
  if (mode === 'rule') input.dns['fake-ip-filter'] = ['DOMAIN,old.example,real-ip', 'MATCH,fake-ip'];
  const result = execute(scripts.empty, input);
  assert.equal(result.proxies[0].udp, true);
  assert.equal(result.proxies.length, 2);
  assert.equal(result.rules.at(-1), 'MATCH,DIRECT');
  assert.equal(result.dns['fake-ip-filter'][0], 'RULE-SET,host2vm-relay-rules,fake-ip');
  assert(result.dns['fake-ip-filter'].some(r => r.includes(mode === 'rule' ? 'old.example' : 'exact.example')));
  const again = execute(scripts.empty, result);
  assert.equal(again.proxies.length, 2); assert.equal(again.rules.length, 5);
  assert.equal(again.dns['fake-ip-filter'].filter(x => x === 'RULE-SET,host2vm-relay-rules,fake-ip').length, 1);
  assert.equal(again['proxy-groups'].length, 2);
  for (const group of again['proxy-groups']) assert.deepEqual(Array.from(group.proxies), ['PASS','Host2VMRelay']);
}
let result = execute(scripts.merge, base());
assert.equal(result.label, '中文 😀 __SOCKS_PORT__:kept'); assert.equal(result.profile, 'test');
assert(result.rules.includes('DOMAIN,user.example,DIRECT'));
assert(execute(scripts.arrow, base()).arrow); assert(execute(scripts.mutating, base()).mutated);
assert(execute(scripts.early, base()).early); assert(execute(scripts.comment, base()).comment);
for (const key of ['throws','missing','async','null','array']) assert.throws(() => execute(scripts[key], base()), undefined, key + ' should fail loudly');
result = execute(scripts.regenerated, base());
assert.equal(result.proxies.find(p => p.name === 'Host2VMRelay').port, 1081);
assert(result.rules.includes('IP-CIDR6,fd00::8/128,DIRECT,no-resolve'));
assert.equal((scripts.regenerated.match(/Host2VMRelay composed script v[12]/g) || []).length, 1);
const legacy = base();
legacy.proxies.push({name:'Host2VM Relay',type:'socks5',server:'127.0.0.1',port:9999});
legacy.rules.unshift('RULE-SET,host2vm-relay-rules,Host2VM Relay');
legacy['proxy-groups'] = [{name:'choose',type:'select',proxies:['Host2VM Relay','Host2VMRelay','old']}];
legacy['rule-providers'] = {'host2vm-relay-rules':{type:'http',url:'http://127.0.0.1:17861/rules.txt'}, keep:{type:'inline',payload:[]}};
result = execute(scripts.empty, legacy);
assert(!result.proxies.some(p=>p.name==='Host2VM Relay')); assert.equal(result.proxies.length,2);
assert.deepEqual(Array.from(result['proxy-groups'][0].proxies),['Host2VMRelay','old']);
assert(!result.rules.some(r=>r.includes(',Host2VM Relay'))); assert(result['rule-providers'].keep);
const provider = result['rule-providers']['host2vm-relay-rules'];
assert.equal(provider.type,'file'); assert.equal(provider.path,'./rules/host2vm-relay-rules.txt'); assert(!('url' in provider));
const guiTunKeys = ['enable','stack','device','auto-route','route-exclude-address',
  'auto-redirect','auto-detect-interface','dns-hijack','strict-route','mtu'];
const json = value => JSON.parse(JSON.stringify(value));
const guiTun = () => ({enable:true,stack:'mixed',device:'Mihomo','auto-route':false,
  'route-exclude-address':['192.168.229.10/32'],'auto-redirect':false,
  'auto-detect-interface':true,'dns-hijack':['any:53'],'strict-route':false,mtu:1500,
  'udp-timeout':120});
const ownedTun = tun => Object.fromEntries(guiTunKeys
  .filter(key => Object.prototype.hasOwnProperty.call(tun ?? {}, key))
  .map(key => [key, json(tun[key])]));
let tunChecks = 0;
function checkTun(name, callback) { callback(); tunChecks++; console.log('PASS TUN ' + name); }
checkTun('unchanged app settings and custom fields', () => {
  const input = {...base(),tun:guiTun()}, expected = json(input.tun);
  const output = execute(scripts.empty, input);
  assert.deepEqual(json(output.tun), expected);
  assert(output.rules.includes('IP-CIDR,192.168.229.10/32,DIRECT,no-resolve'));
  assert.equal(output['rule-providers']['host2vm-relay-rules'].type, 'file');
});
checkTun('reproduce dns-hijack-only conflict', () => {
  const input = {...base(),tun:guiTun()}; input.tun['auto-route'] = true;
  const app = ownedTun(input.tun), output = execute(scripts.empty, input);
  const discarded = Object.keys(app).filter(key => JSON.stringify(output.tun[key]) !== JSON.stringify(app[key]));
  assert.deepEqual(discarded, [], 'Settings-owned fields would be discarded by Verge');
});
for (const name of ['legacy writes', 'in-place array changes', 'replacement tun', 'deleted tun', 'replacement config']) {
  checkTun('merged ' + name, () => {
    const input = {...base(),tun:guiTun()}, expected = ownedTun(input.tun);
    const output = execute(scripts['tun-' + name], input);
    assert.deepEqual(ownedTun(output.tun), expected); assert.equal(output.custom, 'preserved');
    if (name === 'replacement tun') assert.equal(output.tun['udp-timeout'], 300);
  });
}
checkTun('absent TUN settings stay absent', () => {
  const output = execute(scripts.empty, base()); assert(!Object.prototype.hasOwnProperty.call(output, 'tun'));
});
checkTun('null TUN is preserved', () => { assert.equal(execute(scripts.empty, {...base(),tun:null}).tun, null); });
checkTun('old imports cannot inject defaults into absent settings', () => {
  const output = execute(scripts['tun-legacy writes'], base());
  assert.deepEqual(ownedTun(output.tun), {}); assert.equal(output.custom, 'preserved');
});
checkTun('custom non-GUI TUN fields remain supported', () => {
  const output = execute(scripts['tun-custom'], base());
  assert.deepEqual(json(output.tun), {'udp-timeout':300});
});
checkTun('repeated merged script application is stable', () => {
  const script = scripts['tun-legacy writes'];
  const output = execute(script, {...base(),tun:guiTun()}), expected = json(output);
  assert.deepEqual(json(execute(script, output)), expected);
});
console.log('PASS ' + tunChecks + ' managed TUN regression cases');
const config = execute(scripts.empty, {'mixed-port':17891,mode:'rule','log-level':'info',dns:{enable:true,listen:'127.0.0.1:10553',nameserver:['1.1.1.1'],'fake-ip-filter':['+.lan','*.local']},rules:['MATCH,DIRECT']});
config.tun = {...(config.tun ?? {}), enable:false};
for (const name of ['host2vm-relay-rules','host2vm-relay-udp-rules']) config['rule-providers'][name]={type:'inline',behavior:'classical',payload:['DOMAIN,code.example.com','IP-CIDR,10.20.30.40/32,no-resolve']};
const output = process.argv[2] || path.join(root,'artifacts/checks/mihomo.json');
fs.mkdirSync(path.dirname(path.resolve(output)),{recursive:true}); fs.writeFileSync(output,JSON.stringify(config,null,2));
console.log('PASS script composition, preserved helpers, Unicode, return modes, failure propagation, wrapper regeneration, migration, DNS, idempotence and original-policy fallback configuration');
console.log('PASS executed actual C# generator output for every script case');
