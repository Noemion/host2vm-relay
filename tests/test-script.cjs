'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert/strict');
function execute(source, config) {
  const context = vm.createContext({});
  vm.runInContext(source, context, {timeout: 1000});
  context.input = config;
  return vm.runInContext('main(input,"test")', context, {timeout: 1000});
}
const base = () => ({proxies: [{name: 'old', type: 'mieru', udp: false}], rules: ['MATCH,DIRECT'], dns: {'fake-ip-filter': ['+.lan', '*.local', 'exact.example']}});
const fixture = process.argv[2];
assert(fixture && fs.existsSync(fixture), 'Run the C# self-test to create script-cases.json first');
const scripts = JSON.parse(fs.readFileSync(fixture, 'utf8'));
{
  const input = base();
  const result = execute(scripts['vpn-gateway'], input);
  assert.deepEqual(Array.from(result.dns['fake-ip-filter'].slice(0, 2)),
    ['DOMAIN,vpn.example.com,real-ip', 'RULE-SET,host2vm-relay-rules,fake-ip']);
  assert.deepEqual(JSON.parse(JSON.stringify(execute(scripts['vpn-gateway'], result))), JSON.parse(JSON.stringify(result)));
  assert(result.rules.some(r => r.includes('NOT,((PROCESS-NAME,vmnat.exe))')));
  console.log('PASS VPN gateway real DNS precedes suffix forwarding; NAT guard and repeated application preserved');
}
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
for (const mode of [undefined, 'off', 'strict', 'always']) {
  const input = base();
  if (mode !== undefined) input['find-process-mode'] = mode;
  const output = execute(scripts.empty, input);
  assert.equal(output['find-process-mode'], mode === 'off' ? 'strict' : mode);
  for (const protocol of ['tcp', 'udp']) {
    const rule = output.rules.find(r => r.startsWith('AND,((NETWORK,' + protocol + ')'));
    assert(rule.includes('(NOT,((PROCESS-NAME,vmnat.exe)))'));
    assert(rule.includes('(NOT,((SRC-IP-CIDR,192.168.229.10/32)))'));
  }
  assert(!output.rules.some(r => r.startsWith('PROCESS-NAME,vmnat.exe,')), 'NAT exclusion must preserve the original routing policy');
}
assert(execute(scripts.regenerated, base()).rules.some(r => r.includes('(NOT,((SRC-IP-CIDR,fd00::8/128)))')));
const previous = base();
const unguarded = 'AND,((NETWORK,tcp),(RULE-SET,host2vm-relay-rules)),Host2VMRelay-TCP';
previous.rules.unshift(unguarded);
const migrated = execute(scripts.empty, previous);
assert(!migrated.rules.includes(unguarded), 'Replace unguarded rules from older versions');
assert.equal(migrated.rules.length, 5);
assert.equal(result.label, '中文 😀 __SOCKS_PORT__:kept'); assert.equal(result.profile, 'test');
assert(result.rules.includes('DOMAIN,user.example,DIRECT'));
assert(execute(scripts.arrow, base()).arrow); assert(execute(scripts.mutating, base()).mutated);
assert(execute(scripts.early, base()).early); assert(execute(scripts.comment, base()).comment);
for (const key of ['throws','async','null','array']) assert.throws(() => execute(scripts[key], base()), undefined, key + ' should fail loudly');
assert.equal(execute(scripts.missing, base()).proxies.length, 2, 'A fragment without main keeps config and receives managed rules');
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
console.log('PASS script composition, preserved helpers, Unicode, return modes, failure propagation, wrapper regeneration, migration, DNS, idempotence and original-policy fallback configuration');
console.log('PASS executed actual C# generator output for every script case');
