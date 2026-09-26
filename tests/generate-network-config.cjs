'use strict';
const fs=require('fs'),vm=require('vm');
const [source,output]=process.argv.slice(2);
const c={
 mode:'rule','log-level':'debug','external-controller':'127.0.0.1:19090',
 'mixed-port':19080,'allow-lan':false,
 tun:{enable:true,stack:'gvisor',device:'h2vmtest','auto-route':true,'auto-detect-interface':false,
   'route-address':['198.18.0.0/16','198.19.0.0/24'], 'route-exclude-address':['10.203.0.0/24'],'dns-hijack':['any:53']},
 'interface-name':'eth0',
 dns:{enable:true,listen:'127.0.0.1:53',nameserver:['udp://10.203.0.1:15353'],'fake-ip-range':'198.18.0.1/16'},
 'proxy-groups':[{name:'ExistingHostPolicy',type:'select',proxies:['DIRECT']}],
 rules:['IP-CIDR,198.19.0.0/24,ExistingHostPolicy,no-resolve','MATCH,DIRECT']
};
const box=vm.createContext({config:c});vm.runInContext(fs.readFileSync(source,'utf8'),box);const result=vm.runInContext('main(config,"integration")',box);
for(const name of ['host2vm-relay-rules','host2vm-relay-udp-rules'])result['rule-providers'][name]={type:'inline',behavior:'classical',payload:['IP-CIDR,198.19.0.0/24,no-resolve','DOMAIN,intranet.h2vm.test','DOMAIN,shared.h2vm.test','DOMAIN,vm-only.h2vm.test']};
fs.writeFileSync(output,JSON.stringify(result,null,2));
