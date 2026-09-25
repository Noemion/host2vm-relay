const fs = require('fs');
const vm = require('vm');
const assert = require('assert');
const path = require('path');
const source = fs.readFileSync(path.join(__dirname, '../../src/Host2VMRelay/Integration/ClashScript.cs'), 'utf8').split('private const string Template = """')[1].split('""";')[0].replaceAll('__SOCKS_PORT__', '1080').replaceAll('__VM_CIDR__','192.168.229.10/32').replaceAll('__VM_RULE_TYPE__','IP-CIDR');
const context = vm.createContext({}); vm.runInContext(source, context);
for (const mode of ['blacklist', 'whitelist', 'rule']) {
  const config = {proxies:[{name:'old',type:'mieru',udp:false}], rules:['MATCH,DIRECT'], dns:{'fake-ip-filter-mode':mode,'fake-ip-filter':mode==='rule'?['DOMAIN,old.example,real-ip','MATCH,fake-ip']:['+.lan','*.local','exact.example']}};
  const result = context.main(config,'test');
  assert.equal(result.proxies[0].udp,true);
  assert.equal(result.proxies.length,2);
  assert.equal(result.rules.at(-1),'MATCH,DIRECT');
  assert.equal(result.dns['fake-ip-filter'][0],'RULE-SET,host2vm-relay-rules,fake-ip');
  assert(result.dns['fake-ip-filter'].some(r=>r.includes(mode==='rule'?'old.example':'exact.example')));
  const again = context.main(result,'test');
  assert.equal(again.proxies.length,2);
  assert.equal(again.rules.length,4);
}
const base = {'mixed-port':17891, mode:'rule', 'log-level':'info', dns:{enable:true,listen:'127.0.0.1:10553',nameserver:['1.1.1.1'],'fake-ip-filter':['+.lan','*.local']}, rules:['MATCH,DIRECT']};
const generated = context.main(base,'test');
generated.tun.enable = false;
generated['rule-providers']['host2vm-relay-rules'] = {type:'inline',behavior:'classical',payload:['DOMAIN,code.example.com','IP-CIDR,10.20.30.40/32,no-resolve']};
fs.writeFileSync(process.argv[2],JSON.stringify(generated,null,2));
console.log('PASS script preservation, blacklist/whitelist/rule conversion, idempotence, generated Mihomo config');
