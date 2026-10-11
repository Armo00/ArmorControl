const assert = require('node:assert/strict');
const fs = require('node:fs');
const vm = require('node:vm');
const path = require('node:path');
const root = path.resolve(__dirname, '..');
const source = fs.readFileSync(path.join(root, 'prototype/app.js'), 'utf8');
const begin = source.indexOf('  const ascentBindings =');
const end = source.indexOf('  function updateAscentPathPanel()', begin);
const inputs = new Map(), sent = [], timers = new Map(), toasts = [];
let sequence = 0;
const $ = selector => {
  if (!inputs.has(selector)) inputs.set(selector, {value:'', checked:false, disabled:false, tagName:'INPUT', handlers:{},
    addEventListener(name, handler) {this.handlers[name]=handler;},
    blur() {this.handlers.blur?.();}});
  return inputs.get(selector);
};
const context = vm.createContext({$, state:{serverCapabilities:new Set(['mechjeb.ascent.setting']), lastAutomation:{}},
  sendCommand:(name, payload)=>{sent.push({name,payload}); return 'c'+(++sequence);},
  showToast:message=>toasts.push(message), setTimeout:fn=>{timers.set(sequence,fn);return sequence;},
  clearTimeout:id=>timers.delete(id)});
vm.runInContext(source.slice(begin,end),context);
const run = code => vm.runInContext(code,context);
run("syncAscentField('#ascentInclination','value','19.6'); syncAscentField('#ascentOrbitAltitude','value','150.0');");
const input=$('#ascentInclination');
input.handlers.focus();
input.value='29';
run("syncAscentField('#ascentInclination','value','20.0'); syncAscentField('#ascentOrbitAltitude','value','160.0');");
assert.equal(input.value,'29'); assert.equal($('#ascentOrbitAltitude').value,'160.0');
input.handlers.blur();
assert.equal(sent.length,1); assert.equal(sent[0].name,'mechjeb.ascent.setting');
assert.deepEqual(JSON.parse(JSON.stringify(sent[0].payload)),{field:'inclination',value:'29'});
run("syncAscentField('#ascentInclination','value','20.0');");
assert.equal(input.value,'29');
run("acknowledgeAscentSetting({commandId:'c1',success:true}); syncAscentField('#ascentInclination','value','29.0');");
assert.equal(input.value,'29.0'); assert.equal(input.disabled,false);
input.handlers.focus(); input.value='30'; input.handlers.keydown({key:'Escape',preventDefault(){}});
assert.equal(input.value,'29.0'); assert.equal(sent.length,1);
input.handlers.focus(); input.value=''; input.handlers.blur(); assert.equal(sent.length,1);
input.handlers.focus(); input.value='31'; input.handlers.keydown({key:'Enter',preventDefault(){}});
assert.equal(sent.length,2);
run("acknowledgeAscentSetting({commandId:'c2',success:false});");
assert.equal(input.value,'29.0');
input.handlers.focus(); input.value='32'; input.handlers.blur(); timers.get(3)();
assert.equal(input.value,'29.0'); assert.equal(input.disabled,false);
input.handlers.focus(); input.value='33'; input.handlers.blur(); run('resetAscentEdits()');
assert.equal(input.value,'29.0');
assert(source.includes("useCurrentSettings: true"));
const setter = fs.readFileSync(path.join(root,'Source/ArmorControl/Core/AscentSettingBridge.cs'),'utf8');
assert(!/ascent\.enabled\s*=|StartCountdown|ClearAscentLaunchFlags/.test(setter));
for (const field of run('Object.values(ascentBindings).map(x=>x.field)')) assert(setter.includes('case "'+field+'":'),field);
console.log('PASS ascent field isolation, readback/ack, Enter, Esc, validation, failure, timeout, disconnect and binding coverage');
