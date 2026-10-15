const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const tick=()=>new Promise(resolve=>setImmediate(resolve));
const id1='11111111-1111-1111-1111-111111111111',id2='22222222-2222-2222-2222-222222222222';
function element(){return{value:'',children:[],listeners:{},disabled:false,hidden:false,textContent:'',addEventListener(e,f){this.listeners[e]=f;},replaceChildren(...children){this.children=children;},appendChild(child){this.children.push(child);}};}
async function setup(kind,base,stored=null){
 const nodes={},calls=[],responses=[],scheduled=[],storage=new Map(stored?[['scoped-operation:v2:'+encodeURIComponent(base)+':fixture-server',stored]]:[]);
 const get=id=>nodes[id]??=element();const page=element();page.querySelector=id=>get(id.slice(1));
 const respond=async request=>{calls.push(request);if(kind==='config'&&request.url.endsWith('/Libraries'))return{Ready:true};assert.ok(responses.length,'Unexpected request '+request.url);const reply=responses.shift();if(reply instanceof Error)throw reply;return reply;};
 const context={URL,document:{getElementById:get,querySelector:()=>page,createElement:()=>element()},sessionStorage:{getItem:k=>storage.get(k)||null,setItem:(k,v)=>storage.set(k,v),removeItem:k=>storage.delete(k)},setTimeout:f=>{scheduled.push(f);return f;},clearTimeout:f=>{const i=scheduled.indexOf(f);if(i>=0)scheduled.splice(i,1);}};
 if(kind==='app'){
  class Xhr{open(method,url){this.method=method;this.url=url;}setRequestHeader(){}send(body){respond({method:this.method,url:this.url,body}).then(reply=>{this.status=200;this.responseText=JSON.stringify(reply);this.onload();},()=>this.onerror());}}
  Object.assign(context,{location:{href:base+'ScopedImport/App'},XMLHttpRequest:Xhr});
 }else context.ApiClient={getUrl:p=>base+p,serverId:()=> 'fixture-server',serverAddress:()=>base.replace(/\/$/,''),ajax:r=>respond({method:r.type,url:r.url,body:r.data})};
 const html=fs.readFileSync(__dirname+'/../../src/ScopedImport/'+(kind==='app'?'app.html':'import.html'),'utf8');vm.runInNewContext(html.match(/<script>([\s\S]*?)<\/script>/)[1],context);
 if(kind==='app') {responses.push({AccessToken:'mock-secret-token',ServerId:'fixture-server'},{Ready:true});if(stored)responses.push(status('Rejected'));await get('login').listeners.submit({preventDefault(){}});}else{if(stored)responses.push(status('Rejected'));await page.listeners.viewshow();}
 return{nodes,get,calls,responses,scheduled,storage,page,form:()=>get(kind==='app'?'import':'siForm'),button:()=>get(kind==='app'?'submit':'siSubmit'),resume:()=>get(kind==='app'?'resume':'siResume'),movies:()=>get(kind==='app'?'movies':'siMovies'),result:()=>get(kind==='app'?'result':'siResult'),mode:v=>get(kind==='app'?'metadata':'siMetadata').value=v};
}
function status(state,item=id1,metadata='NoMatch'){return{State:state,Message:state,Created:[],Skipped:[],Warnings:[],MetadataProviders:[{Type:'Movie',Provider:'Fixture',Outcome:'NoMetadata'}],EnumeratedDirectories:[],ReadFiles:[],Movies:[{ItemId:item,Name:'<Movie & fixture>',EntryState:'Created',MetadataStatus:metadata,ManualIdentification:'点击“刮削元数据”，输入名称、年份或 ID。'}]};}
async function submit(h){await h.form().listeners.submit({preventDefault(){}});await tick();}
function links(h){return h.movies().children.flatMap(row=>row.children.filter(x=>x.href));}
function checkLink(h,base,item){const all=links(h);assert.equal(all.length,1);const url=new URL(all[0].href);assert.equal(url.origin+url.pathname,base+'web/index.html');assert.equal(url.hash,'#/details?id='+item.replace(/-/g,'')+'&serverId=fixture-server');assert.ok(!url.href.includes('token'));assert.equal(all[0].rel,'noopener noreferrer');assert.equal(h.movies().children[0].children[0].textContent.startsWith('<Movie & fixture>'),true);assert.match(h.movies().children[0].children.at(-1).textContent,/只打开详情页.*登录/);}
(async()=>{
 let count=0;
 for(const kind of ['app','config'])for(const base of ['http://fixture.invalid/','https://fixture.invalid/jellyfin/'])for(const terminal of ['Completed','Rejected','Partial']){
  const h=await setup(kind,base);h.mode('local');h.responses.push({OperationId:'first'},status(terminal,id1,'LocalOnly'));await submit(h);
  assert.equal(h.button().disabled,false);assert.equal(h.storage.size,0);checkLink(h,base,id1);
  h.mode('library');h.responses.push({OperationId:'second'},new Error('synthetic poll failure'));await submit(h);
  assert.equal(links(h).length,0,'stale movie links survived new submission');assert.equal(h.button().disabled,true,'unknown status should retain busy');assert.equal(h.resume().hidden,false);assert.match(h.result().textContent,/second/);
  const before=h.calls.filter(c=>c.method==='POST').length;await submit(h);assert.equal(h.calls.filter(c=>c.method==='POST').length,before,'unknown status allowed duplicate submission');
  h.responses.push(status('Completed',id2));await h.resume().listeners.click();await tick();checkLink(h,base,id2);assert.equal(h.button().disabled,false);assert.match(h.result().textContent,/无匹配/);
  const posts=h.calls.filter(c=>c.method==='POST'&&c.url.endsWith('/Import'));assert.equal(posts.length,2);assert.equal(JSON.parse(posts[0].body).FetchMetadata,false);assert.equal(JSON.parse(posts[1].body).FetchMetadata,true);
  assert.equal(h.responses.length,0);count++;
 }
 for(const kind of ['app','config']){
  const h=await setup(kind,'https://fixture.invalid/prefix/');h.mode('library');h.responses.push({OperationId:'running'},status('Metadata'));
  await submit(h);assert.equal(h.button().disabled,true);h.responses.push(status('Completed',id2,'FetchedFields'));await h.scheduled.shift()();await tick();assert.equal(h.button().disabled,false);assert.match(h.movies().children[0].children[0].textContent,/部分元数据.*未执行完整刮削/);count++;
  // Persisted operation restoration is GET-only, and terminal releases busy.
  const restored=await setup(kind,'https://fixture.invalid/prefix/','restore');
  await tick();assert.equal(restored.button().disabled,false);assert.equal(restored.calls.filter(c=>c.method==='POST'&&c.url.endsWith('/Import')).length,0);count++;
 }
 for(const kind of ['app','config']){
  const base='https://fixture.invalid/prefix/',h=await setup(kind,base);h.mode('local');
  const existing=status('Completed',id1,'ExistingSkipped');existing.Movies[0].EntryState='ExistingSkipped';
  h.responses.push({OperationId:'existing'},existing);await submit(h);checkLink(h,base,id1);assert.match(h.movies().children[0].children[0].textContent,/已有条目已跳过/);
  const invalid=status('Completed','javascript:alert(1)');h.responses.push({OperationId:'invalid'},invalid);await submit(h);assert.equal(links(h).length,0);count++;
 }
 console.log('PASS: '+count+' import UI scenarios across both pages, base paths, modes, terminal states, retries and exact movie links');
})().catch(e=>{console.error(e);process.exitCode=1;});
