'use strict';
// Execute actual C# self-test exports. There is intentionally no fallback composer.
const fs = require('fs'), vm = require('vm'), assert = require('assert/strict');
const file = process.argv[2];
assert(file && fs.existsSync(file), 'Run the C# self-test to create incremental-cases.json first');
const cases = JSON.parse(fs.readFileSync(file, 'utf8'));
let checks = 0;
for (const name of ['fresh', 'updated', 'v040Updated', 'v1Updated', 'v1UserEdited', 'v2UserEdited']) {
  const code = cases[name];
  assert.equal(typeof code, 'string', name);
  assert.equal((code.match(/Host2VMRelay composed script v2/g) || []).length, 1);
  assert(!code.includes('Host2VMRelay composed script v1'));
  const config = {proxies:[{name:'public-mieru',type:'mieru',udp:false}],rules:['MATCH,DIRECT'],dns:{},tun:{'dns-hijack':['any:53'],'auto-route':false}};
  const sandbox = vm.createContext({config});
  vm.runInContext(code, sandbox, {timeout:1000});
  const result = vm.runInContext('main(config, "test")', sandbox, {timeout:1000});
  assert.equal(result.userValue, '保留 😀');
  assert.equal(result.proxies.filter(p=>p.name==='Host2VMRelay').length,1);
  assert.equal(result.proxies.find(p=>p.name==='Host2VMRelay').port,name==='fresh'?1080:1081);
  assert.equal(result.proxies.find(p=>p.name==='public-mieru').udp,true);
  assert.equal(result.proxies.find(p=>p.name==='Host2VMRelay').udp,true,'Advertise the implemented UDP ASSOCIATE endpoint');
  assert.equal(result.rules.filter(r=>r==='AND,((NETWORK,tcp),(RULE-SET,host2vm-relay-rules)),Host2VMRelay-TCP').length,1);
  for (const kind of ['TCP','UDP']) {
    const group=result['proxy-groups'].find(g=>g.name==='Host2VMRelay-'+kind);
    assert.equal(JSON.stringify(group.proxies),JSON.stringify(['PASS','Host2VMRelay']));
    assert.equal(group['expected-status'],'204');
  }
  assert(result.rules.includes('DOMAIN,custom.example,DIRECT'));
  assert.equal(JSON.stringify(result.tun),JSON.stringify({'dns-hijack':['any:53'],'auto-route':false}));
  if (name.endsWith('UserEdited')) assert.equal(result.edited,true);
  checks++; console.log('PASS actual C# incremental result: '+name);
}
console.log('PASS '+checks+' incremental C# outputs, preserved custom logic, managed TUN settings and Mieru UDP');
