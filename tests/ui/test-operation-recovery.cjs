const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const oldId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',newId='bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb';
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const key=(base,server='fixture-server')=>'scoped-operation:v2:'+encodeURIComponent(base)+':'+encodeURIComponent(server.toLowerCase());
const missing=(id=oldId)=>({status:404,body:{Code:'ScopedImport.OperationNotFound',OperationId:id}});
const state=(id=newId,terminal=false)=>({Id:id,State:terminal?'Completed':'Metadata',Message:'fixture',Created:[],Skipped:[],Warnings:[],MetadataProviders:[],Movies:[],EnumeratedDirectories:[],ReadFiles:[]});
async function setup(kind,{base='https://fixture.invalid/jellyfin/',server='fixture-server',storage=new Map(),lookup=missing(),ready=true,preferences=new Map(),pick=null,libraries=[]}={}){
 const nodes={},calls=[],scheduled=[];let lookupReply=lookup,readyReply={Ready:ready,Libraries:libraries},postReply={status:202,body:{OperationId:newId}},terminal=false;
 const html=fs.readFileSync(__dirname+'/../../src/ScopedImport/'+(kind==='app'?'app.html':'import.html'),'utf8');
 const get=id=>nodes[id]??={value:'',hidden:new RegExp('id="'+id+'"[^>]*\\bhidden\\b').test(html),disabled:false,textContent:'',children:[],listeners:{},addEventListener(e,f){this.listeners[e]=f;},showModal(){this.open=true;},close(){this.open=false;this.listeners.close?.();},replaceChildren(...items){this.children=items;},appendChild(e){this.children.push(e);}};
 const page={listeners:{},querySelector:id=>get(id.slice(1)),addEventListener(e,f){this.listeners[e]=f;}};
 const respond=async(method,url,body)=>{
  calls.push({method,url,body});const parsed=new URL(url),path=parsed.pathname;
  if(method==='POST'&&path.endsWith('/PickFolder')&&pick)return pick(JSON.parse(body));
  if(path.endsWith('/AuthenticateByName'))return{status:200,body:{AccessToken:'mock-token',ServerId:server}};
  if(path.endsWith('/Libraries'))return readyReply instanceof Error||readyReply.status?readyReply:{status:200,body:readyReply};
  if(method==='POST'&&path.endsWith('/Import'))return postReply;
  if(path.endsWith('/Operations/'+newId))return{status:200,body:state(newId,terminal)};
  if(path.includes('/Operations/'))return lookupReply;
  throw Error('Unexpected request '+url);
 };
 const context={URL,localStorage:{getItem:k=>preferences.get(k),setItem:(k,v)=>preferences.set(k,v)},document:{getElementById:get,querySelector:()=>page,createElement:()=>get('created-'+Math.random())},sessionStorage:{getItem:k=>storage.get(k)||null,setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},setTimeout:f=>{scheduled.push(f);return f;},clearTimeout:f=>{const i=scheduled.indexOf(f);if(i>=0)scheduled.splice(i,1);}};
 if(kind==='app'){
  class Xhr{open(method,url){this.method=method;this.url=url;}setRequestHeader(){}send(body){respond(this.method,this.url,body).then(r=>{if(r instanceof Error||r?.network||r&&'reject' in r){this.onerror();return;}this.status=r.status;this.responseText=typeof r.body==='string'?r.body:JSON.stringify(r.body);this.onload();});}}
  Object.assign(context,{location:{href:base+'ScopedImport/App'},XMLHttpRequest:Xhr});
 }else context.ApiClient={getUrl:p=>base+p,serverId:()=>server,serverAddress:()=>base.replace(/\/$/,''),ajax:async r=>{const reply=await respond(r.type||'GET',r.url,r.data);if(reply instanceof Error)throw reply;if(reply&&'reject' in reply)throw reply.reject;if(reply.network)throw new TypeError('fixture network failure');if(reply.status>=400){if(reply.errorShape==='jqJSON')throw{status:reply.status,responseJSON:reply.body};if(reply.errorShape==='jqText')throw{status:reply.status,responseText:JSON.stringify(reply.body)};throw{status:reply.status,json:async()=>typeof reply.body==='string'?JSON.parse(reply.body):reply.body};}return reply.body;}};
 vm.runInNewContext(html.match(/<script>([\s\S]*?)<\/script>/)[1],context);
 const h={kind,storage,calls,scheduled,get,page,setLookup:v=>lookupReply=v,setPost:v=>postReply=v,setReady:v=>readyReply=v,setTerminal:()=>terminal=true,form:()=>get(kind==='app'?'import':'siForm'),button:()=>get(kind==='app'?'submit':'siSubmit'),resume:()=>get(kind==='app'?'resume':'siResume'),forget:()=>get(kind==='app'?'forget':'siForget'),result:()=>get(kind==='app'?'result':'siResult'),mode:v=>get(kind==='app'?'metadata':'siMetadata').value=v,posts:()=>calls.filter(c=>c.method==='POST'&&c.url.endsWith('/Import'))};
 h.show=async()=>{if(kind==='app')await get('login').listeners.submit({preventDefault(){}});else await page.listeners.viewshow();await tick();};
 h.click=async node=>{assert.equal(node.hidden,false,'control hidden');assert.ok(node.listeners.click,'control missing');await node.listeners.click();await tick();};
 h.submit=async()=>{await h.form().listeners.submit({preventDefault(){}});await tick();};
 await h.show();return h;
}
(async()=>{
 let passed=0,failed=0;
 async function test(name,run){try{await run();passed++;console.log('PASS: '+name);}catch(e){failed++;console.error('FAIL: '+name+'\n'+e.stack);}}
 for(const kind of ['app','config'])await test(kind+' legacy 404 recovery regression',async()=>{
  const storage=new Map([['scoped-operation',oldId]]),h=await setup(kind,{storage});
  await h.click(h.resume());
  assert.match(h.result().textContent,/记录已失效，原操作结果无法从该记录确认/);
  assert.equal(h.button().disabled,true);assert.equal(h.forget().hidden,false);assert.equal(h.posts().length,0);
  await h.click(h.forget());assert.equal(h.button().disabled,false);assert.equal(storage.size,0);assert.equal(h.posts().length,0);
 });

 for(const kind of ['app','config']){
  await test(kind+' Windows picker success, cancellation, errors and no duplicate requests',async()=>{
   const root=String.raw`H:\影视\动漫`,show=root+String.raw`\生活 大爆炸`,movies=String.raw`H:\影视\电影`;
   const libraries=[{Name:'漫',Locations:[root],ItemId:oldId,CollectionType:'tvshows'},
    {Name:'电影',Locations:[movies],ItemId:newId,CollectionType:'movies'}];
   const preferences=new Map();
   let reply,finish;
   const pick=()=>reply??new Promise(resolve=>{finish=resolve;});
   const options={preferences,libraries,pick},h=await setup(kind,options);
   const input=h.get(kind==='app'?'path':'siPath'), folders=h.get('siFolders');
   const result=(Status,Path,Message)=>({status:200,body:{Status,Path,Message}});
   input.value='original';
   await h.click(h.get('siBrowseOpen'));
   assert.equal(folders.children.length,2);assert.match(folders.children[0].textContent,/漫.*动漫/);
   const pending=folders.children[0].listeners.click();
   await folders.children[1].listeners.click();
   assert.equal(h.calls.filter(c=>c.url.endsWith('/PickFolder')).length,1);
   assert.match(h.get('siBrowseStatus').textContent,/等待 Windows/);
   finish(result('Cancelled'));await pending;
   assert.equal(input.value,'original');assert.equal(h.get('siBrowser').open,false);
   await h.click(h.get('siSettingsOpen'));
   h.get('siDefaultPath').value=root;await h.click(h.get('siSettingsSave'));
   const reloaded=await setup(kind,options);
   reply=result('Error',null,'无法访问当前交互桌面');
   await reloaded.click(reloaded.get('siBrowseOpen'));
   await reloaded.click(reloaded.get('siFolders').children[1]);
   assert.deepEqual(JSON.parse(reloaded.calls.at(-1).body),{LibraryId:newId,LocationIndex:0,PreferredPath:root});
   assert.match(reloaded.get('siPickFeedback').textContent,/交互桌面/);
   reply=result('Selected',show);
   await h.click(h.get('siBrowseOpen'));await h.click(folders.children[0]);
   assert.equal(input.value,show);assert.match(h.get('siTarget').textContent,/漫/);
   assert.equal(h.posts().length,0);assert.equal(reloaded.posts().length,0);
   await h.submit();assert.equal(JSON.parse(h.posts()[0].body).Path,show);
  });
  await test(kind+' scoped missing record: repeated query, refresh, explicit end and new mode/id',async()=>{
   const base='https://fixture.invalid/jellyfin/',storage=new Map([[key(base),oldId]]),h=await setup(kind,{storage});
   assert.match(h.result().textContent,/记录已失效，原操作结果无法从该记录确认/);assert.equal(storage.get(key(base)),oldId);
   const initialQueries=h.calls.filter(c=>c.url.includes('/Operations/')).length;
   await h.click(h.resume());await h.click(h.resume());assert.equal(h.calls.filter(c=>c.url.includes('/Operations/')).length,initialQueries+2);assert.equal(h.posts().length,0);
   const refreshed=await setup(kind,{storage});assert.equal(refreshed.button().disabled,true);assert.equal(refreshed.forget().hidden,false);assert.equal(refreshed.posts().length,0);
   const beforeReady=refreshed.calls.filter(c=>c.url.endsWith('/Libraries')).length;
   await refreshed.click(refreshed.forget());assert.equal(refreshed.button().disabled,false);assert.equal(refreshed.calls.filter(c=>c.url.endsWith('/Libraries')).length,beforeReady+1);
   assert.equal(storage.has(key(base)),false);assert.equal(refreshed.posts().length,0);
   refreshed.mode('local');await refreshed.submit();assert.equal(refreshed.posts().length,1);assert.equal(JSON.parse(refreshed.posts()[0].body).FetchMetadata,false);assert.equal(storage.get(key(base)),newId);assert.match(refreshed.get('siLog').textContent,new RegExp(newId));
   const afterSubmit=await setup(kind,{storage});assert.equal(afterSubmit.posts().length,0);assert.ok(afterSubmit.calls.some(c=>c.url.endsWith('/Operations/'+newId)));assert.equal(afterSubmit.button().disabled,true);
   refreshed.setTerminal();await refreshed.scheduled.shift()();await tick();refreshed.mode('library');await refreshed.submit();assert.equal(refreshed.posts().length,2);assert.equal(JSON.parse(refreshed.posts()[1].body).FetchMetadata,true);
  });
  for(const [name,reply,expected] of [
   ['network',new Error('HTTP 404 ScopedImport.OperationNotFound; text must not classify'),/网络连接失败或请求超时/],
   ['undefined timeout',{reject:undefined},/网络连接失败或请求超时/],
   ['401',{status:401,body:{}},/HTTP 401/],
   ['403',{status:403,body:{}},/HTTP 403/],
   ['proxy 404',{status:404,body:'<html>not found</html>'},/未收到插件对该编号的不存在确认/],
   ['404 wrong id',missing(newId),/未收到插件对该编号的不存在确认/],
   ['404 wrong code',{status:404,body:{Code:'Other.OperationNotFound',OperationId:oldId}},/未收到插件对该编号的不存在确认/],
   ['503',{status:503,body:{}},/服务故障 HTTP 503/]
  ])await test(kind+' '+name+' stays unknown and cannot unlock/repost',async()=>{
   const base='https://fixture.invalid/jellyfin/',storage=new Map([[key(base),oldId]]),h=await setup(kind,{storage,lookup:reply});
   assert.match(h.result().textContent,expected);assert.equal(h.forget().hidden,true);assert.equal(h.button().disabled,true);assert.equal(storage.get(key(base)),oldId);
   await h.submit();await h.click(h.resume());await h.submit();assert.equal(h.posts().length,0);assert.equal(h.button().disabled,true);assert.equal(h.forget().hidden,true);
   const reloaded=await setup(kind,{storage,lookup:reply});assert.equal(reloaded.button().disabled,true);assert.equal(reloaded.forget().hidden,true);assert.equal(reloaded.posts().length,0);
   if(name==='401'||name==='403'){
    if(kind==='app')assert.equal(h.get('login').hidden,false);
    h.setLookup({status:200,body:state(oldId,true)});await h.show();assert.equal(h.button().disabled,false);assert.equal(storage.size,0);assert.equal(h.posts().length,0);
   }
  });
  await test(kind+' overlapping queries and end clicks cannot race the current operation',async()=>{
   const base='https://fixture.invalid/jellyfin/',storage=new Map([[key(base),oldId]]),h=await setup(kind,{storage});let resolve;
   h.setLookup(new Promise(done=>resolve=done));const before=h.calls.filter(c=>c.url.includes('/Operations/')).length;
   const first=h.resume().listeners.click();await tick();assert.equal(h.forget().disabled,true);
   await h.resume().listeners.click();await h.forget().listeners.click();await h.submit();assert.equal(storage.get(key(base)),oldId);assert.equal(h.posts().length,0);
   assert.equal(h.calls.filter(c=>c.url.includes('/Operations/')).length,before+1);resolve(missing());await first;await tick();assert.equal(h.forget().hidden,false);
  });
  await test(kind+' confirmed missing then network failure requires a fresh confirmation',async()=>{
   const h=await setup(kind,{storage:new Map([[key('https://fixture.invalid/jellyfin/'),oldId]])});assert.equal(h.forget().hidden,false);
   h.setLookup(new Error('network'));await h.click(h.resume());assert.equal(h.forget().hidden,true);assert.equal(h.button().disabled,true);assert.equal(h.posts().length,0);
  });
  await test(kind+' explicit end must pass readiness; backend conflict/503 does not retry',async()=>{
   const h=await setup(kind,{storage:new Map([[key('https://fixture.invalid/jellyfin/'),oldId]])});h.setReady({Ready:false});await h.click(h.forget());
   assert.equal(h.button().disabled,true);assert.match(h.result().textContent,/追加保护未启用/);await h.submit();assert.equal(h.posts().length,0);
   h.setReady({status:503,body:{}});await h.click(h.resume());assert.equal(h.button().disabled,true);assert.match(h.result().textContent,/服务故障 HTTP 503/);
   h.setReady({Ready:true});await h.click(h.resume());assert.equal(h.button().disabled,false);
   h.setPost({status:409,body:{}});h.mode('library');await h.submit();assert.equal(h.posts().length,1);assert.match(h.result().textContent,/HTTP 409/);assert.equal(h.scheduled.length,0);
   h.setPost({status:503,body:{}});await h.submit();assert.equal(h.posts().length,2);assert.match(h.result().textContent,/服务故障 HTTP 503/);assert.equal(h.scheduled.length,0);
  });
  await test(kind+' same-origin servers/base paths never restore another scoped id',async()=>{
   const first='https://fixture.invalid/a/',second='https://fixture.invalid/b/',storage=new Map([[key(first),oldId]]);
   const other=await setup(kind,{base:second,storage});assert.equal(other.button().disabled,false);assert.equal(other.calls.filter(c=>c.url.includes('/Operations/')).length,0);
   other.mode('local');await other.submit();assert.equal(storage.get(key(second)),newId);assert.equal(storage.get(key(first)),oldId);
   const samePathNewServer=await setup(kind,{base:first,server:'another-server',storage});assert.equal(samePathNewServer.button().disabled,false);assert.equal(samePathNewServer.calls.filter(c=>c.url.includes('/Operations/')).length,0);
   const original=await setup(kind,{base:first,storage});assert.equal(original.forget().hidden,false);await original.click(original.forget());assert.equal(storage.has(key(first)),false);assert.equal(storage.get(key(second)),newId);
  });
  await test(kind+' unbound legacy id is never automatically queried or assigned',async()=>{
   const base='https://fixture.invalid/new-instance/',storage=new Map([['scoped-operation',oldId]]),h=await setup(kind,{base,storage});
   assert.match(h.result().textContent,/无法确认其归属/);assert.equal(h.calls.filter(c=>c.url.includes('/Operations/')).length,0);assert.equal(storage.has(key(base)),false);assert.equal(h.button().disabled,true);
   await h.click(h.forget());assert.equal(storage.size,0);assert.equal(h.posts().length,0);assert.equal(h.button().disabled,false);assert.doesNotMatch(h.result().textContent,/记录已失效/);
  });
 }
 for(const shape of ['jqJSON','jqText'])await test('config structured '+shape+' missing response',async()=>{
  const h=await setup('config',{storage:new Map([[key('https://fixture.invalid/jellyfin/'),oldId]]),lookup:{...missing(),errorShape:shape}});
  assert.equal(h.forget().hidden,false);assert.match(h.result().textContent,/记录已失效/);
 });
 await test('both pages use the same server/base/identity namespace',async()=>{
  const base='https://fixture.invalid/shared/',storage=new Map(),app=await setup('app',{base,storage});app.mode('local');await app.submit();
  const config=await setup('config',{base,storage});assert.ok(config.calls.some(c=>c.url.endsWith('/Operations/'+newId)));assert.equal(config.posts().length,0);
 });
 console.log(`RESULT: ${passed} passed, ${failed} failed`);if(failed)process.exitCode=1;
})().catch(e=>{console.error(e);process.exitCode=1;});
