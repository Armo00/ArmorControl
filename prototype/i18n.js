/* Presentation-only localization. Wire commands, input values and user names stay unchanged. */
(() => {
  'use strict';
  function createTranslator(messages) {
    const own = key => Object.prototype.hasOwnProperty.call(messages, key);
    const keys = Object.keys(messages).filter(key => key && messages[key] !== key).sort((a,b) => b.length-a.length);
    const expression = keys.length ? new RegExp(keys.map(key => key.replace(/[.*+?^${}()|[\]\\]/g,'\\$&')).join('|'),'g') : null;
    const cache = new Map();
    return (value, parameters = {}) => {
      const source = String(value ?? '');
      let result = cache.get(source);
      if (result === undefined) {
        result = own(source) ? messages[source] : expression ? source.replace(expression, (key, offset) => {
          // Never replace an English token embedded in a name or another word.
          if (/^[A-Za-z]/.test(key) && /[A-Za-z]/.test(source[offset-1] || '')) return key;
          if (/[A-Za-z]$/.test(key) && /[A-Za-z]/.test(source[offset+key.length] || '')) return key;
          return messages[key];
        }) : source;
        if (cache.size >= 2048) cache.clear();
        cache.set(source, result);
      }
      return result.replace(/\{([\w]+)\}/g, (match,key) => Object.prototype.hasOwnProperty.call(parameters,key) ? String(parameters[key]) : match);
    };
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = {createTranslator};
  if (typeof document === 'undefined') return;
  const original = new WeakMap(), attributes = new WeakMap();
  const skip = 'script,style,textarea,[data-l10n-skip],#activeVesselName,#crewVesselName,#targetCurrentName,#targetSelectedName,#targetParentBody,#crewCabins .cabin-count>span,.target-node-copy>b,.crew-identity b,.part-index-item b,#part-action-title,.part-control-title,.part-control-option-name';
  let translate = value => String(value ?? ''), locale = 'zh-CN', request = 0;
  const sourceText = element => {
    if (!element) return '';
    if (!element.childNodes) return element.textContent || '';
    return [...element.childNodes].map(node => {
      if (node.nodeType !== 3) return sourceText(node);
      const record = original.get(node);
      return record && node.data === record.translated ? record.source : node.data;
    }).join('');
  };
  const ignored = element => !element || Boolean(element.closest(skip));
  function text(node, force) {
    if (ignored(node.parentElement) || !node.data.trim()) return;
    let record = original.get(node);
    if (record && node.data === record.translated) {
      if (!force) return;
    } else record = {source: node.data};
    // Option.value defaults to its display text unless explicitly fixed first.
    const option = node.parentElement.closest('option');
    if (option && !option.hasAttribute('value')) option.setAttribute('value',sourceText(option));
    record.translated = translate(record.source);
    original.set(node,record);
    if (node.data !== record.translated) node.data = record.translated;
  }
  function labels(element, force) {
    if (ignored(element)) return;
    let records = attributes.get(element);
    if (!records) {records={}; attributes.set(element,records);}
    for (const name of ['aria-label','title','placeholder','alt']) {
      const current = element.getAttribute(name);
      if (current === null) continue;
      let record = records[name];
      if (record && current === record.translated) {if (!force) continue;}
      else record = {source:current};
      record.translated = translate(record.source); records[name]=record;
      if (current !== record.translated) element.setAttribute(name,record.translated);
    }
  }
  function scan(root, force=false) {
    if (root.nodeType === 3) {text(root,force); return;}
    if (root.nodeType !== 1 && root.nodeType !== 9) return;
    if (root.nodeType === 1) labels(root,force);
    const walker = document.createTreeWalker(root,NodeFilter.SHOW_ELEMENT|NodeFilter.SHOW_TEXT);
    while (walker.nextNode()) {
      const node=walker.currentNode;
      if (node.nodeType===3) text(node,force); else labels(node,force);
    }
  }
  function readPreference() {try {return localStorage.getItem('armorControlLanguage');} catch {return null;}}
  function catalogUrl(file) {
    let token='';
    try {token=new URLSearchParams(location.hash.slice(1)).get('token') || localStorage.getItem('armorControlToken') || '';} catch {}
    return `Localization/${file}${token?'?token='+encodeURIComponent(token):''}`;
  }
  async function json(file) {
    const response=await fetch(catalogUrl(file),{cache:'no-cache'});
    if (!response.ok) throw new Error(`Localization HTTP ${response.status}`);
    return response.json();
  }
  async function setLanguage(code) {
    if (!window.ArmorI18n.languages.some(item=>item.code===code)) return false;
    const revision=++request;
    const select=document.getElementById('languageSelect');
    try {
      const catalog=await json(`${code}.json`);
      if (revision!==request) return false;
      if (catalog.locale!==code || !catalog.messages || Object.values(catalog.messages).some(value=>typeof value!=='string')) throw new Error('Invalid localization catalog');
      translate=createTranslator(catalog.messages); locale=code;
      document.documentElement.lang=code;
      try {localStorage.setItem('armorControlLanguage',code);} catch {}
      scan(document.body,true);
      if(select)select.value=code;
      const status=document.getElementById('languageStatus');
      if(status)status.textContent='';
      document.dispatchEvent(new CustomEvent('armorcontrol:languagechange',{detail:{locale:code}}));
      return true;
    } catch (error) {
      if(revision===request) {
        if(select)select.value=locale;
        const status=document.getElementById('languageStatus');
        if(status)status.textContent=translate('语言文件加载失败，保留当前语言');
        console.warn('ArmorControl localization:',error.message);
      }
      return false;
    }
  }
  window.ArmorI18n={t:(value,parameters)=>translate(value,parameters),sourceText,setLanguage,get locale(){return locale;},languages:[]};
  async function start() {
    const select=document.getElementById('languageSelect');
    const observer=new MutationObserver(records=>{
      const pending=new Set();
      for(const record of records) {
        if(record.type==='childList')record.addedNodes.forEach(node=>pending.add(node));
        else pending.add(record.target);
      }
      pending.forEach(node=>{if(node.isConnected)scan(node);});
    });
    observer.observe(document.body,{subtree:true,childList:true,characterData:true,attributes:true,attributeFilter:['aria-label','title','placeholder','alt']});
    try {
      const manifest=await json('languages.json');
      window.ArmorI18n.languages=manifest.languages.filter(item=>/^[a-z]{2,3}(?:-[A-Za-z0-9]+)*$/.test(item.code));
      if(select) {
        select.replaceChildren(...window.ArmorI18n.languages.map(item=>{const option=document.createElement('option');option.value=item.code;option.textContent=item.name;return option;}));
        select.addEventListener('change',()=>setLanguage(select.value));
      }
      const preferred=readPreference();
      const selected=window.ArmorI18n.languages.some(item=>item.code===preferred)?preferred:manifest.default;
      if(!await setLanguage(selected) && selected!=='zh-CN')await setLanguage('zh-CN');
    } catch(error) {
      const status=document.getElementById('languageStatus');
      if(status)status.textContent='语言文件加载失败，保留当前语言';
      if(select)select.disabled=true;
      console.warn('ArmorControl language manifest:',error.message);
    }
  }
  if(document.readyState==='loading')document.addEventListener('DOMContentLoaded',start,{once:true});else start();
})();
