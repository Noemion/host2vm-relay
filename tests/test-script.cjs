'use strict';
const fs = require('fs');
const vm = require('vm');
const assert = require('assert/strict');
const path = require('path');
const root = path.join(__dirname, '..');
const cs = fs.readFileSync(path.join(root, 'src/Integration/ClashScript.cs'), 'utf8');
const template = cs.split('private const string Template = """')[1].split('""";')[0].trimStart();
const header = '// Host2VMRelay composed script v1\n// original-length: ';
const open = 'const __h2vmOriginalMain = (() => {\n';
const close = '\n\n  return typeof main === "function" ? main : null;\n})();\n\n';
function compose(source = '', port = 1080, host = '192.168.229.10') {
  source = source || 'function main(config) { return config; }';
  const type = host.includes(':') ? 'IP-CIDR6' : 'IP-CIDR';
  const cidr = host + (host.includes(':') ? '/128' : '/32');
  return header + source.length + '\n' + open + source + close + template
    .replaceAll('__SOCKS_PORT__', String(port)).replaceAll('__VM_CIDR__', cidr).replaceAll('__VM_RULE_TYPE__', type) + '\n';
}
function execute(source, config) {
  const context = vm.createContext({});
  vm.runInContext(source, context, {timeout: 1000});
  context.input = config;
  return vm.runInContext('main(input,"test")', context, {timeout: 1000});
}
const base = () => ({proxies: [{name: 'old', type: 'mieru', udp: false}], rules: ['MATCH,DIRECT'], dns: {'fake-ip-filter': ['+.lan', '*.local', 'exact.example']}});
const originals = {
  empty: '',
  merge: "const label = '中文 😀 __SOCKS_PORT__'; function helper(x) { return x + ':kept'; } function main(config, profileName) { config.label = helper(label); config.profile = profileName; config.rules.unshift('DOMAIN,user.example,DIRECT'); return config; }",
  arrow: 'const main = (config, profileName) => ({ ...config, profile: profileName, arrow: true });',
  mutating: 'function main(config) { config.mutated = true; }',
  early: "function main(config, profileName) { if (profileName === 'test') return { ...config, early: true }; return config; }",
  throws: "function main(config) { throw new Error('user failure'); }",
  missing: "const example = 'function main(config) { return config; }';",
  async: 'async function main(config) { return config; }',
  null: 'function main(config) { return null; }',
  array: 'function main(config) { return []; }',
  comment: 'function main(config) { config.comment = true; return config; } // trailing comment'
};
const scripts = process.argv[3] ? JSON.parse(fs.readFileSync(process.argv[3], 'utf8')) : Object.fromEntries(Object.entries(originals).map(([key,value]) => [key,compose(value)]));
scripts.regenerated ??= compose(originals.merge, 1081, 'fd00::8');
for (const key of Object.keys(originals)) assert.equal(typeof scripts[key], 'string', 'missing generated C# fixture ' + key);
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
  assert.equal(again.proxies.length, 2); assert.equal(again.rules.length, 4);
  assert.equal(again.dns['fake-ip-filter'].filter(x => x === 'RULE-SET,host2vm-relay-rules,fake-ip').length, 1);
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
assert.equal((scripts.regenerated.match(/Host2VMRelay composed script v1/g) || []).length, 1);
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
// No host capabilities (fetch, require, fs) are needed by the generated JavaScript.
const config = execute(scripts.empty, {'mixed-port':17891,mode:'rule','log-level':'info',dns:{enable:true,listen:'127.0.0.1:10553',nameserver:['1.1.1.1'],'fake-ip-filter':['+.lan','*.local']},rules:['MATCH,DIRECT']});
config.tun.enable = false;
config['rule-providers']['host2vm-relay-rules']={type:'inline',behavior:'classical',payload:['DOMAIN,code.example.com','IP-CIDR,10.20.30.40/32,no-resolve']};
const output = process.argv[2] || path.join(root,'artifacts/checks/mihomo.json');
fs.mkdirSync(path.dirname(path.resolve(output)),{recursive:true}); fs.writeFileSync(output,JSON.stringify(config,null,2));
console.log('PASS script composition, lexical scope, preserved helper functions, Unicode, return modes, failure propagation, wrapper regeneration, legacy migration, DNS modes and idempotence');
console.log(process.argv[3] ? 'PASS executed actual C# generator output' : 'INFO template-only local checks; C# generator fixtures are exercised by Windows self-test/CI');
