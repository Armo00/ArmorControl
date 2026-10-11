// Offline regression suite: no KSP process or live commands.
const assert=require('node:assert/strict'),fs=require('node:fs'),path=require('node:path'),vm=require('node:vm');
const root=path.resolve(__dirname,'..'),web=path.join(root,'prototype');
const code=fs.readFileSync(path.join(web,'i18n.js'),'utf8');
const {createTranslator,validateMessages,DEFAULT_LOCALE}=require(path.join(web,'i18n.js'));
const manifest=JSON.parse(fs.readFileSync(path.join(web,'Localization/languages.json')));
const en=JSON.parse(fs.readFileSync(path.join(web,'Localization/en-US.json'))).messages;
const zh=JSON.parse(fs.readFileSync(path.join(web,'Localization/zh-CN.json'))).messages;
assert.equal(DEFAULT_LOCALE,'en-US');assert.equal(manifest.default,'en-US');
validateMessages(en);validateMessages(zh);
assert.deepEqual(Object.keys(en).sort(),Object.keys(zh).sort());
assert(Object.keys(en).every(key=>!/\p{Script=Han}/u.test(key)));
assert(Object.values(en).every(value=>!/\p{Script=Han}/u.test(value)));
const english=createTranslator(en,{},[zh]),chinese=createTranslator(zh,en,[en]);
assert.equal(english('飞行'),'Flight');
assert.equal(english('启动散热'),'Activate cooling');
assert.equal(chinese('Activate cooling'),'启动散热');
assert.equal(chinese('New user vessel: Flight'),'New user vessel: Flight','No substring translation');
assert.equal(chinese('Offset'),'Offset');
assert.equal(chinese('Maximum 111.84 MN'),'最大 111.84 MN');
assert.equal(chinese('About 3.25 g'),'约 3.25 g');
assert.equal(chinese('Left axis · 1.20 km–55.97 km'),'左轴 · 1.20 km–55.97 km');
assert.equal(chinese('Stop reactor'),'停止反应堆');
assert.equal(chinese('VIEW / ISOMETRIC'),'视图 / 等轴测');
assert.equal(chinese(' Fast telemetry 123 ms ago '),' 快速遥测距今 123 毫秒 ');
assert.equal(chinese('Command failed:No MechJeb core is available on the active vessel.'),'命令失败：当前载具没有可用的 MechJeb 核心。');
assert.equal(createTranslator({'A {0}': '值 {0}'})('A {0}',{0:'<img onerror=1>'}),'值 <img onerror=1>');
assert.equal(createTranslator({}, {'Flight':'Flight'})('Flight'),'Flight');
assert.throws(()=>validateMessages({'Flight {0}':'飞行'}),/Placeholder/);
assert.throws(()=>validateMessages({'中文':'value'}),/English/);
assert.throws(()=>validateMessages({'Flight':2}),/strings/);
for(const [key,value] of Object.entries(en)){
  if(!key.includes('{'))continue;
  const params=Object.fromEntries([...key.matchAll(/\{(\w+)\}/g)].map(m=>[m[1],'123']));
  const expected=zh[key].replace(/\{(\w+)\}/g,(_,n)=>params[n]);
  assert.equal(chinese(key,params),expected,'Explicit template: '+key);
}
assert.match(fs.readFileSync(path.join(web,'index.html'),'utf8'),/<html lang="en-US">/);
assert.match(fs.readFileSync(path.join(root,'settings.example.cfg'),'utf8'),/language = en-US/);
assert.match(fs.readFileSync(path.join(root,'Source/ArmorControl/Core/ArmorControlSettings.cs'),'utf8'),/Language = "en-US"/);
assert(!code.includes('.part-control-title'),'Never exclude entire interactive cards');
assert(!code.includes('armorControlLanguage'),'No per-browser language preference');
assert(fs.readFileSync(path.join(web,'index.html'),'utf8').includes('id="languageSelect"'),'Global language selector restored');
const app=fs.readFileSync(path.join(web,'app.js'),'utf8');
assert(app.includes("sendCommand('server.language.set', { language })"),'Selector sends a server command');
const addon=fs.readFileSync(path.join(root,'Source/ArmorControl/ArmorControlAddon.cs'),'utf8');
const commandHandler=addon.slice(addon.indexOf('private CommandExecution ExecuteCommand'));
assert(commandHandler.indexOf('server.language.set')<commandHandler.indexOf('!HighLogic.LoadedSceneIsFlight'),'Server setting works without a vessel');
assert(!fs.readFileSync(path.join(web,'app.js'),'utf8').includes("toLocaleString('zh-CN'"));
console.log('PASS '+Object.keys(en).length+' English keys, bilingual templates, defaults, fallbacks and nested command errors');

async function lifecycle(earlyServerLanguage){
  let startup,callback,fail=false,pending=null;
  function element(tag='span',attrs={}){
    return {nodeType:1,tag,attrs,childNodes:[],parentElement:null,isConnected:true,
      closest(selector){if(selector==='option')return this.tag==='option'?this:null;
        if('data-l10n-skip' in this.attrs||['script','style','textarea'].includes(this.tag))return this;
        return this.parentElement?this.parentElement.closest(selector):null;},
      getAttribute(name){return this.attrs[name]??null;},hasAttribute(name){return name in this.attrs;},
      setAttribute(name,value){this.attrs[name]=value;},removeAttribute(name){delete this.attrs[name];},
      replaceChildren(...children){this.childNodes=children;},addEventListener(){}};
  }
  function text(parent,data){const node={nodeType:3,data,parentElement:parent,isConnected:true};parent.childNodes.push(node);return node;}
  const body=element('body'),button=element('button',{'data-command':'mechjeb.smartass.set',title:'Running'});
  const label=text(button,'Surface'),option=element('option'),optionText=text(option,'Prograde');
  const name=element('b',{'data-l10n-skip':''}),nameText=text(name,'Flight');
  const empty=element('b'),emptyText=text(empty,'No active vessel');
  const dynamic=element('span'),dynamicText=text(dynamic,'About 3.25 g');
  const indicator=element('span'),status=element('span');
  body.childNodes=[button,option,name,empty,dynamic];
  const nodes=[button,label,option,optionText,name,nameText,empty,emptyText,dynamic,dynamicText];
  const storage=new Map([['armorControlLanguage','zh-CN']]);
  const doc={readyState:'loading',body,documentElement:{lang:'en-US'},
    addEventListener:(event,fn)=>{startup=fn;},dispatchEvent(){},
    getElementById:id=>id==='languageSelect'?indicator:id==='languageStatus'?status:null,
    createElement:tag=>element(tag),
    createTreeWalker:()=>{let i=-1;return{nextNode:()=>++i<nodes.length,get currentNode(){return nodes[i];}};}};
  const context={document:doc,window:{},console:{warn(){}},URLSearchParams,location:{hash:''},
    localStorage:{getItem:key=>storage.get(key),setItem:(key,value)=>storage.set(key,value)},
    NodeFilter:{SHOW_ELEMENT:1,SHOW_TEXT:4},CustomEvent:class{},
    MutationObserver:class{constructor(fn){callback=fn;}observe(){}},
    fetch:async url=>{
      if(fail&&url.includes('zh-CN'))throw Error('offline');
      if(pending&&url.includes('zh-CN'))await pending.promise;
      return{ok:true,json:async()=>JSON.parse(fs.readFileSync(path.join(web,url),'utf8'))};
    }};
  vm.createContext(context);vm.runInContext(code,context);
  const api=context.window.ArmorI18n;
  if(earlyServerLanguage)await api.followServerLanguage(earlyServerLanguage);
  await startup();
  assert.equal(api.locale,earlyServerLanguage||'en-US');
  assert.equal(storage.get('armorControlLanguage'),'zh-CN','Browser preference is ignored, not rewritten');
  assert.equal(api.setLanguage,undefined,'No independent client language setter');
  await api.followServerLanguage('zh-CN');
  assert.equal(label.data,zh.Surface);assert.equal(api.sourceText(button),'Surface');
  assert.equal(button.attrs.title,'运行中');assert.equal(button.attrs['data-command'],'mechjeb.smartass.set');
  assert.equal(option.attrs.value,'Prograde');assert.equal(optionText.data,'顺向');
  assert.equal(nameText.data,'Flight');assert.equal(emptyText.data,'无活动载具');
  assert.equal(dynamicText.data,'约 3.25 g');
  label.data='Surface prograde';
  assert.equal(api.sourceText(button),'Surface prograde','New telemetry must replace old canonical source');
  callback([{type:'characterData',target:label}]);assert.equal(label.data,'地速 +');
  await api.followServerLanguage('en-US');assert.equal(label.data,'Surface prograde');
  assert.equal(option.attrs.value,'Prograde');assert.equal(emptyText.data,'No active vessel');
  fail=true;assert.equal(await api.followServerLanguage('zh-CN'),false);assert.equal(api.locale,'en-US');
  fail=false;assert.equal(await api.followServerLanguage('../invalid'),false);
  pending={};pending.promise=new Promise(resolve=>pending.resolve=resolve);
  const old=api.followServerLanguage('zh-CN');pending.resolve();pending=null;
  const latest=api.followServerLanguage('en-US');await Promise.all([old,latest]);assert.equal(api.locale,'en-US');
  assert.equal(nameText.data,'Flight');assert.equal(storage.get('armorControlLanguage'),'zh-CN');
  assert.equal(indicator.value,'en-US');
  console.log('PASS server-owned language, early snapshot, ignored browser preferences, live DOM, failures and racing requests');
}
Promise.all([lifecycle(),lifecycle('zh-CN')]).catch(error=>{console.error(error);process.exitCode=1;});
