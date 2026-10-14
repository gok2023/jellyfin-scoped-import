const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const html=fs.readFileSync(__dirname+'/../../src/ScopedImport/import.html','utf8');
assert.match(html,/<select id="siMetadata"/);assert.doesNotMatch(html,/emby-checkbox|FetchTmdb/);
async function check(value){
 const nodes={},calls=[];const get=id=>nodes[id]??={value:id==='#siMetadata'?value:'D:\\lab\\new movie',listeners:{},addEventListener(e,f){this.listeners[e]=f;}};
 const page={querySelector:get,addEventListener(){}};
 vm.runInNewContext(html.match(/<script>([\s\S]*?)<\/script>/)[1],{document:{querySelector:()=>page},setTimeout,clearTimeout,ApiClient:{getUrl:x=>x,ajax:async request=>{calls.push(request);if(request.type==='POST')return{OperationId:'fixture'};return{State:'Completed',Message:'done',Created:[],Skipped:[],Warnings:[],MetadataProviders:[]};}}});
 await get('#siForm').listeners.submit({preventDefault(){}});await Promise.resolve();
 const sent=calls.find(x=>x.type==='POST');assert.equal(sent.url,'ScopedImport/Import');
 assert.equal(JSON.parse(sent.data).FetchMetadata,value==='library');
 assert.equal(calls.filter(x=>x.type==='POST').length,1);assert.ok(calls.some(x=>x.url==='ScopedImport/Operations/fixture'));
}
(async()=>{await check('library');await check('local');console.log('PASS: configuration selector sends both choices; status polling is GET-only');})().catch(e=>{console.error(e);process.exitCode=1});
