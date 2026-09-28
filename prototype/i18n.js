/* English source keys. Translate display text only; commands and user data stay intact. */
(() => {
  'use strict';
  const DEFAULT_LOCALE = 'en-US';
  const escapeRegex = value => value.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
  const slots = value => [...value.matchAll(/\{([\w]+)\}/g)].map(match=>match[1]).sort();
  function validateMessages(messages) {
    if (!messages || typeof messages!=='object' || Array.isArray(messages)) throw new Error('Invalid messages');
    for (const [key,value] of Object.entries(messages)) {
      if (/\p{Script=Han}/u.test(key) || typeof value!=='string') throw new Error('Keys must be English and translations must be strings');
      if (slots(key).join('|')!==slots(value).join('|')) throw new Error('Placeholder mismatch: '+key);
    }
    return messages;
  }
  function createTranslator(messages, fallback = {}, sourceCatalogs = []) {
    const dictionary={...validateMessages(fallback),...validateMessages(messages)};
    const own=key=>Object.prototype.hasOwnProperty.call(dictionary,key);
    const aliases=new Map(), variants=[];
    sourceCatalogs.forEach(validateMessages);
    for(const key of Object.keys(dictionary)){
      for(const source of new Set([key,...sourceCatalogs.map(catalog=>catalog[key]).filter(value=>typeof value==='string')])){
        if(source===dictionary[key])continue;
        if(!aliases.has(source))aliases.set(source,key);
        if(/\{\w+\}/.test(source)&&source.replace(/\{\w+\}/g,'').replace(/[^A-Za-z\p{Script=Han}]/gu,'').length>=3)
          variants.push({key,source});
      }
    }
    const templates=variants.sort((a,b)=>b.source.replace(/\{\w+\}/g,'').length-a.source.replace(/\{\w+\}/g,'').length)
      .map(({key,source})=>{
        const names=[];let cursor=0,pattern='^';
        for(const match of source.matchAll(/\{(\w+)\}/g)){
          pattern+=escapeRegex(source.slice(cursor,match.index))+'([\\s\\S]*?)';
          names.push(match[1]);cursor=match.index+match[0].length;
        }
        pattern+=escapeRegex(source.slice(cursor))+'$';
        return {key,names,pattern:new RegExp(pattern)};
      });
    const cache=new Map();
    function format(value,parameters) {
      return value.replace(/\{(\w+)\}/g,(match,name)=>Object.prototype.hasOwnProperty.call(parameters,name)?String(parameters[name]):match);
    }
    const nestedMessages=new Set(['Command failed:{0}','Service error:{0}']);
    const render=(value,parameters={},depth=0)=>{
      const source=String(value??''), key=source.trim();
      let result=cache.get(source);
      if(result===undefined){
        let translated=own(key)?dictionary[key]:aliases.has(key)?dictionary[aliases.get(key)]:key;
        if(!own(key)&&!aliases.has(key)&&/[A-Za-z\p{Script=Han}]/u.test(key)){
          for(const template of templates){
            const match=template.pattern.exec(key);
            if(!match)continue;
            const captured={};let valid=true;
            template.names.forEach((name,index)=>{if(name in captured&&captured[name]!==match[index+1])valid=false;captured[name]=match[index+1];});
            if(!valid)continue;
            if(depth<3&&nestedMessages.has(template.key))captured['0']=render(captured['0'],{},depth+1);
            translated=format(dictionary[template.key],captured);break;
          }
        }
        const start=source.indexOf(key);
        result=key?source.slice(0,start)+translated+source.slice(start+key.length):source;
        if(cache.size>=2048)cache.clear();
        cache.set(source,result);
      }
      return format(result,parameters);
    };
    return render;
  }
  if(typeof module!=='undefined'&&module.exports)module.exports={createTranslator,validateMessages,DEFAULT_LOCALE};
  if(typeof document==='undefined')return;

  const originals=new WeakMap(), attributes=new WeakMap();
  const skip='script,style,textarea,[data-l10n-skip]';
  let locale=DEFAULT_LOCALE, request=0, translate=value=>String(value??'');
  let serverLanguage=null;
  const sourceText=element=>{
    if(!element)return '';
    if(!element.childNodes)return element.textContent||'';
    return [...element.childNodes].map(node=>{
      if(node.nodeType!==3)return sourceText(node);
      const record=originals.get(node);
      return record&&node.data===record.translated?record.source:node.data;
    }).join('');
  };
  const ignored=element=>!element||Boolean(element.closest(skip));
  function text(node,force) {
    if(ignored(node.parentElement)||!node.data.trim())return;
    let record=originals.get(node);
    if(record&&node.data===record.translated){if(!force)return;}
    else record={source:node.data};
    const option=node.parentElement.closest('option');
    if(option&&!option.hasAttribute('value'))option.setAttribute('value',sourceText(option));
    record.translated=translate(record.source); originals.set(node,record);
    if(node.data!==record.translated)node.data=record.translated;
  }
  function labels(element,force) {
    if(ignored(element))return;
    let records=attributes.get(element);
    if(!records){records={};attributes.set(element,records);}
    for(const name of ['aria-label','title','placeholder','alt']){
      const value=element.getAttribute(name);if(value===null)continue;
      let record=records[name];
      if(record&&value===record.translated){if(!force)continue;}
      else record={source:value};
      record.translated=translate(record.source);records[name]=record;
      if(value!==record.translated)element.setAttribute(name,record.translated);
    }
  }
  function scan(root,force=false) {
    if(root.nodeType===3){text(root,force);return;}
    if(root.nodeType!==1&&root.nodeType!==9)return;
    if(root.nodeType===1){if(ignored(root))return;labels(root,force);}
    const walker=document.createTreeWalker(root,NodeFilter.SHOW_ELEMENT|NodeFilter.SHOW_TEXT);
    while(walker.nextNode()){
      const node=walker.currentNode;
      if(node.nodeType===3)text(node,force);else labels(node,force);
    }
  }
  async function json(file) {
    let token='';
    try{token=new URLSearchParams(location.hash.slice(1)).get('token')||localStorage.getItem('armorControlToken')||'';}catch{}
    const response=await fetch('Localization/'+file+(token?'?token='+encodeURIComponent(token):''),{cache:'no-cache'});
    if(!response.ok)throw new Error('Localization HTTP '+response.status);
    return response.json();
  }
  function status(value) {const element=document.getElementById('languageStatus');if(element)element.textContent=translate(value);}
  async function setLanguage(code) {
    if(!window.ArmorI18n.languages.some(item=>item.code===code))return false;
    const revision=++request;
    try{
      const catalog=await json(code+'.json');
      if(revision!==request)return false;
      if(catalog.locale!==code)throw new Error('Locale mismatch');
      // Recognize whole messages from either game language without Chinese keys.
      let other=null;
      try{
        const otherCode=code==='en-US'?'zh-CN':'en-US';
        other=await json(otherCode+'.json');
        if(other.locale!==otherCode)throw new Error('Fallback locale mismatch');
        validateMessages(other.messages);
      }catch{other=null;}
      if(revision!==request)return false;
      const next=createTranslator(catalog.messages,code==='en-US'?{}:(other?.messages||{}),other?[other.messages]:[]);
      translate=next;locale=code;document.documentElement.lang=code;
      scan(document.body,true);
      const select=document.getElementById('languageSelect');
      if(select)select.value=code;
      status('');
      document.dispatchEvent(new CustomEvent('armorcontrol:languagechange',{detail:{locale:code}}));
      return true;
    }catch(error){
      if(revision===request){
        status('Unable to load language file; keeping current language');
        console.warn('ArmorControl localization:',error.message);
      }
      return false;
    }
  }
  function followServerLanguage(code) {
    if(code!=='en-US'&&code!=='zh-CN')return Promise.resolve(false);
    serverLanguage=code;
    return window.ArmorI18n.languages.length?setLanguage(code):Promise.resolve(false);
  }
  window.ArmorI18n={t:(value,parameters)=>translate(value,parameters),sourceText,followServerLanguage,get locale(){return locale;},languages:[]};
  async function start() {
    const observer=new MutationObserver(records=>{
      const pending=new Set();
      for(const record of records){
        if(record.type==='childList')record.addedNodes.forEach(node=>pending.add(node));
        else pending.add(record.target);
      }
      pending.forEach(node=>{if(node.isConnected)scan(node);});
    });
    observer.observe(document.body,{subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:['aria-label','title','placeholder','alt']});
    try{
      const manifest=await json('languages.json');
      if(!Array.isArray(manifest.languages))throw new Error('Invalid language manifest');
      const seen=new Set();
      window.ArmorI18n.languages=manifest.languages.filter(item=>{
        if(!item||typeof item.code!=='string'||typeof item.name!=='string'||!/^([a-z]{2,3})(-[A-Za-z0-9]+)*$/.test(item.code)||seen.has(item.code))return false;
        seen.add(item.code);return true;
      });
      if(!seen.has(DEFAULT_LOCALE))throw new Error('English fallback missing');
      // Defaults only bootstrap text; the WebSocket snapshot is the authority.
      await setLanguage(serverLanguage||DEFAULT_LOCALE);
    }catch(error){
      status('Unable to load language file; keeping current language');
      console.warn('ArmorControl language manifest:',error.message);
    }
  }
  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start,{once:true});else start();
})();
