const fs=require('node:fs'),vm=require('node:vm'),assert=require('node:assert/strict');
const html=fs.readFileSync(__dirname+'/../../src/ScopedImport/app.html','utf8');
const script=html.match(/<script>([\s\S]*?)<\/script>/)[1];
async function run(responses,href='http://127.0.0.1:8096/ScopedImport/App'){
 const elements={}; const requests=[];
 const get=id=>elements[id]??=( {value:id==='user'?' admin ':id==='password'?'mock password':'',listeners:{},addEventListener(name,fn){this.listeners[name]=fn;}} );
 class Xhr {open(method,url){assert.equal(typeof url,'string');this.method=method;this.url=url;this.headers={};}setRequestHeader(k,v){this.headers[k]=v;}send(body){requests.push({url:this.url,method:this.method,body,headers:this.headers});const r=responses.shift();assert.ok(r,'Unexpected request');queueMicrotask(()=>{if(r.timeout){this.ontimeout();return;}this.status=r.status??200;this.responseText=typeof r.body==='string'?r.body:JSON.stringify(r.body);this.onload();});}}
 vm.runInNewContext(script,{document:{getElementById:get},location:{href},URL,XMLHttpRequest:Xhr,sessionStorage:{getItem(){return null},setItem(){},removeItem(){}},setTimeout,clearTimeout,fetch(){throw Error("Cannot read properties of undefined (reading 'includes')");}});
 if(get('login').listeners.submit)await get('login').listeners.submit({preventDefault(){}});
 assert.ok(!requests.some(r=>r.url.endsWith('/Import')),'Login triggered an import');
 return {elements,requests};
}
(async()=>{
 let r=await run([{body:{AccessToken:'mock-token'}},{body:{Ready:true}}]);
 assert.equal(r.elements.connection.textContent,'已连接，追加保护已启用。');
 assert.equal(r.requests[1].headers['X-Emby-Token'],'mock-token');
 assert.equal(JSON.parse(r.requests[0].body).Username,'admin');
 assert.equal(JSON.parse(r.requests[0].body).Pw,'mock password');
 assert.equal(r.elements.password.value,'');
 r=await run([{status:401,body:{}}]);assert.match(r.elements.connection.textContent,/身份认证失败.*401/);
 r=await run([{body:{AccessToken:'mock-token'}},{status:403,body:{}}]);assert.match(r.elements.connection.textContent,/插件权限和就绪检查失败.*403/);
 r=await run([{body:{}}]);assert.match(r.elements.connection.textContent,/认证响应缺少令牌/);
 r=await run([{body:'bad json'}]);assert.match(r.elements.connection.textContent,/格式异常/);
 r=await run([{timeout:true}]);assert.match(r.elements.connection.textContent,/20 秒/);
 r=await run([{body:{AccessToken:'mock-token'}},{body:{Ready:false}}]);assert.match(r.elements.connection.textContent,/追加保护未启用/);
 r=await run([],'file:///fixture/ScopedImport/app.html');assert.equal(r.requests.length,0);assert.equal(r.elements.login.hidden,true);assert.equal(r.elements.localFileHelp.hidden,false);assert.match(r.elements.connection.textContent,/本地文件/);
 console.log('PASS: 8 login cases; file URL rejected before credentials; no media import submitted');
})().catch(e=>{console.error(e);process.exitCode=1});
