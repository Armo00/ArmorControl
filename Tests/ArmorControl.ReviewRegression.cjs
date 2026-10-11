// Runs production frontend functions with an isolated DOM; never connects to KSP.
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const assert = require('node:assert/strict');
const root = path.resolve(__dirname, '..');
const source = fs.readFileSync(path.join(root, 'prototype/app.js'), 'utf8');
const html = fs.readFileSync(path.join(root, 'prototype/index.html'), 'utf8');
assert(html.includes('id="recSnapshotLabel"') && html.includes('id="recSnapshotTime"'));
assert(!source.includes('#recorderSnapshotLabel') && !source.includes('#recorderSnapshotTime'),
  'recorder renderer selectors must match the HTML IDs');
function extract(name) {
  const start = source.search(new RegExp(`  (?:async )?function ${name}\\(`));
  assert(start >= 0, `missing production function ${name}`);
  const end = source.indexOf('\n  }', start);
  assert(end > start);
  return source.slice(start, end + 5);
}
function handler(prefix) {
  const start = source.indexOf(prefix);
  assert(start >= 0);
  return source.slice(start, source.indexOf('\n  });', start) + 7);
}
async function main() {
  let languageChange;
  const languageSelect = {value:'zh-CN', addEventListener: (type, fn) => {languageChange=fn;}};
  const sentSettings = [];
  const languageState = {socket:{readyState:1,send:value=>sentSettings.push(JSON.parse(value))},
    connection:'connecting',serverCapabilities:new Set(['server.language.set']),commandSequence:0,clientId:'test',quietCommands:new Set()};
  const languageContext = {state:languageState,$:()=>languageSelect,window:{ArmorI18n:{locale:'en-US'}},WebSocket:{OPEN:1},showToast(){}};
  vm.createContext(languageContext);
  vm.runInContext(extract('sendCommand')+extract('updateLanguageControl')+handler("  $('#languageSelect').addEventListener('change'"),languageContext);
  languageContext.updateLanguageControl();
  assert.equal(languageSelect.disabled,false,'Global setting available without flight telemetry');
  languageChange({target:languageSelect});
  assert.equal(sentSettings[0].name,'server.language.set');assert.equal(sentSettings[0].language,'zh-CN');
  assert.equal(languageSelect.value,'en-US','No optimistic local language change');
  assert.equal(languageSelect.disabled,true,'Wait for command acknowledgement');
  assert.equal(languageContext.sendCommand('vessel.stage'),false,'Flight command gate remains unchanged');
  languageState.socket.readyState=3;languageContext.updateLanguageControl();
  assert.equal(languageSelect.disabled,true,'Disconnected selector disabled');
  console.log('PASS web selector sends global setting and waits for server, including non-flight and disconnected states');
  const durationContext = vm.createContext({});
  vm.runInContext(extract('formatDuration'), durationContext);
  for (const [seconds, expected] of [
    [0, '00:00:00.0'], [12.34, '00:00:12.3'],
    [59.94, '00:00:59.9'], [59.95, '00:01:00.0'], [59.96, '00:01:00.0'],
    [599.96, '00:10:00.0'], [3599.94, '00:59:59.9'], [3599.95, '01:00:00.0'],
    [86399.96, '24:00:00.0'], [360000, '100:00:00.0'], ['599.96', '00:10:00.0'],
    [-1, '00:00:00.0'], [NaN, '00:00:00.0'], [Infinity, '00:00:00.0'],
    [undefined, '00:00:00.0']
  ]) assert.equal(durationContext.formatDuration(seconds), expected, 'Duration: ' + seconds);
  console.log('PASS duration rounding, minute/hour carry, long durations and invalid input');
  const orbitDetail = {};
  const ledger = {querySelector: selector => selector === ':scope > span' ? {textContent:'Current orbit'} : orbitDetail};
  const orbitLabels = {$$: selector => selector === '.ledger-feature' ? [ledger] : [],
    sourceText: element => element.textContent, escapeHtml: value => String(value).replace(/&/g,'&amp;').replace(/</g,'&lt;')};
  vm.createContext(orbitLabels);
  vm.runInContext(extract('renderOrbitSummaries'), orbitLabels);
  orbitLabels.renderOrbitSummaries({body:'Flight',situation:'SUB_ORBITAL',apoapsis:1000,periapsis:-10});
  assert.equal(orbitDetail.innerHTML,'<span data-l10n-skip>Flight</span> · <span>Suborbital</span>');
  const stack = {querySelectorAll: () => []};
  const partLabels = {$: () => stack, escapeHtml: orbitLabels.escapeHtml};
  vm.createContext(partLabels);
  vm.runInContext(extract('renderPartControls'), partLabels);
  partLabels.renderPartControls({id:12,actions:[{kind:'reactor',moduleIndex:3,name:'Nuclear reactor',status:'Running',command:'reactor.disable',commandLabel:'Stop reactor',available:true}]});
  assert(stack.innerHTML.includes('<span>Running</span>'));
  assert(stack.innerHTML.includes('data-part-action="reactor.disable"'));
  assert(stack.innerHTML.includes('>Stop reactor</button>'));
  console.log('PASS dynamic orbital-state and part-action labels without changing names or commands');
  const elements = new Map();
  const getElement = key => {
    if (!elements.has(key)) elements.set(key, {style: {}, attributes: {}, textContent: '', classList: {toggle() {}},
      setAttribute(name, value) {this.attributes[name] = value;}});
    return elements.get(key);
  };
  const geometryState = {};
  const chartCtx = {state: geometryState, $: getElement, Number, Math, formatDuration: value => String(value)};
  vm.createContext(chartCtx);
  vm.runInContext(extract('trajectoryMapPoint') + extract('renderTrajectoryGroundMap'), chartCtx);
  const north = chartCtx.trajectoryMapPoint(1, 0, [0,0], 6371000);
  const east = chartCtx.trajectoryMapPoint(0, 1, [0,0], 6371000);
  assert(north[1] > 111000 && Math.abs(north[0]) < 1e-6, 'north must project upward');
  assert(east[0] > 111000 && Math.abs(east[1]) < 1e-6, 'east must project right');
  const seam = chartCtx.trajectoryMapPoint(0, -179.9, [0,179.9], 6371000);
  assert(seam[0] > 22000 && seam[0] < 23000, 'date-line crossing must not wrap around the planet');
  const t = {surfaceMapVersion:1, bodyRadius:6371000, targetAvailable:true, targetLatitude:19,targetLongitude:110,
    impactAvailable:true,impactLatitude:19.1,impactLongitude:110.2,groundPaths:[[[19.2,109.9,1000],[19.1,110.2,0]]]};
  chartCtx.renderTrajectoryGroundMap(t);
  const ground = getElement('#trajectoryOrbitChart').innerHTML;
  assert(ground.includes('Target') && ground.includes('Predicted impact') && ground.includes('km'));
  assert(!ground.includes('NaN') && !ground.includes('undefined'));
  chartCtx.renderTrajectoryGroundMap({...t,groundPaths:[]});
  assert(getElement('#trajectoryOrbitChart').innerHTML.includes('Predicted impact'), 'valid impact remains visible without path samples');
  chartCtx.renderTrajectoryGroundMap({});
  assert(getElement('#trajectoryOrbitChart').innerHTML.includes('New DLL required'), 'old coordinate semantics must not be drawn');
  console.log('PASS north/east projection + date-line + map markers/scale + missing path + old DLL guard');
  for (const name of ['renderOrbitGeometry', 'renderAscentProfile', 'updateAscentFlightProgress', 'renderAscentTargetOrbit', 'captureAscentSpatialHistory', 'renderAscentOrbitReadouts'])
    vm.runInContext(extract(name), chartCtx);
  const layer = {innerHTML: ''}, svg = {querySelector: () => layer};
  chartCtx.renderOrbitGeometry(svg, null, [], true);
  assert(!layer.innerHTML.includes('<path'), 'missing telemetry must not produce a synthetic orbit');
  const orbit = [[200, 0, 0], [0, 0, 200], [-200, 0, 0], [0, 0, -200], [200, 0, 0]];
  const after = orbit.map(p => p.map(v => v * 2));
  const geometry = {radius: 100, current: orbit, craft: orbit[0], node: orbit[1], plannedAvailable: true};
  chartCtx.renderOrbitGeometry(svg, geometry, [orbit, after], true);
  const curves = [...layer.innerHTML.matchAll(/<path d="([^"]+)"/g)].map(m => m[1]);
  assert.equal(curves.length, 2);
  assert.notEqual(curves[0], curves[1], 'current and planned orbit must use separate physical samples');
  assert(!layer.innerHTML.includes('NaN'));
  chartCtx.renderOrbitGeometry(svg, geometry, [[], after], true);
  assert(layer.innerHTML.includes('stroke-dasharray="7 4"'), 'planned orbit keeps its identity without current samples');
  geometryState.orbitGeometry = {...geometry, ut: 1};
  chartCtx.captureAscentSpatialHistory('vessel', geometryState.orbitGeometry);
  chartCtx.renderAscentProfile({apoapsisKm: 100});
  const flown = getElement('#ascentProfileChart').innerHTML;
  geometryState.orbitGeometry.craft = [150, 0, 150];
  geometryState.orbitGeometry.ut = 2;
  chartCtx.captureAscentSpatialHistory('vessel', geometryState.orbitGeometry);
  chartCtx.captureAscentSpatialHistory('vessel', geometryState.orbitGeometry);
  assert.equal(geometryState.ascentSpatialHistory.points.length, 2, 'duplicate UT must not duplicate samples');
  chartCtx.updateAscentFlightProgress(3000);
  const markup = getElement('#ascentProfileChart').innerHTML;
  assert.notEqual(markup, flown);
  assert(markup.includes('data-series="flown"') && markup.includes('data-series="coast"'));
  assert(!markup.includes('NaN') && !markup.includes('rotate('), 'position marker must not imply fabricated attitude');
  chartCtx.captureAscentSpatialHistory('other-vessel', geometryState.orbitGeometry);
  assert.equal(geometryState.ascentSpatialHistory.points.length, 1, 'vessel change resets history');
  chartCtx.renderAscentOrbitReadouts({apoapsis: 120000, periapsis: -5000, timeToApoapsis: 23, timeToPeriapsis: NaN});
  assert.equal(getElement('#ascentActualAp').textContent, '120.0 km');
  assert.equal(getElement('#ascentActualPe').textContent, '-5.0 km');
  assert.equal(getElement('#ascentActualTimePe').textContent, 'N/A');
  chartCtx.renderAscentTargetOrbit({periapsisKm: 1, apoapsisKm: 1});
  const circular = getElement('#ascentTargetOrbitEllipse').attributes.rx;
  chartCtx.renderAscentTargetOrbit({periapsisKm: 1, apoapsisKm: 10});
  assert.notEqual(getElement('#ascentTargetOrbitEllipse').attributes.rx, circular);
  assert(html.includes('data-command="mechjeb.node.abort"'));
  console.log('PASS physical orbit samples + planned orbit identity + real ascent history + target orbit scaling + abort control');
  const commands = [];
  const handlers = {};
  let renderedPart = 1;
  let target = 'Surface';
  const controls = [{value: '0.0°'}, {value: '0.0°'}, {value: '0.0°'}];
  controls.forEach(input => { input.closest = () => ({querySelectorAll: () => []}); });
  const node = {querySelector() { return this; }, classList: {toggle() {}},
    addEventListener(name, fn) { handlers[name] = fn; }};
  const state = {vesselStructure: {parts: [{id: 1, title: 'A', name: 'A'}, {id: 2, title: 'B', name: 'B'}]},
    selectedPartId: 1, partControlEditingUntil: 60000, recorderSamples: [], recorderConnection: 1,
    smartAssTelemetry: {smartAssSurfacePitch: 30, smartAssSurfaceYaw: 90, smartAssSurfaceRoll: 180,
      smartAssVelocityPitch: 5, smartAssVelocityYaw: 10, smartAssVelocityRoll: 15, smartAssRoll: 45}};
  const ctx = {state, sourceText: element => element?.textContent || '', userText: (element,value,fallback) => {element.textContent=value||fallback;}, uiLocale: () => 'en-US', performance: {now: () => 1000}, document: {activeElement: null},
    $: selector => selector === '.attitude-grid button.is-active' ? {textContent: target} : node,
    $$: selector => selector === '.attitude-offset input' ? controls : [],
    sendCommand: (...args) => {commands.push(args); return 'test-command';}, escapeHtml: String,
    renderPartControls: part => {renderedPart = part.id;}, showToast() {}, apiUrl: String,
    smartAssTargets: {'Surface': 'SURFACE', 'Surface prograde': 'SURFACE_PROGRADE', 'Prograde': 'PROGRADE', 'Off': 'OFF'},
    Map, Number};
  vm.createContext(ctx);
  for (const name of ['selectVesselPart', 'syncSmartAssOffsets', 'executeSmartAss', 'acceptRecorderRevision',
    'acceptRecorderSample', 'fetchRecorderHistory']) vm.runInContext(extract(name), ctx);

  ctx.selectVesselPart(2);
  assert.equal(renderedPart, 2, 'selection must immediately refresh frozen controls');
  vm.runInContext(handler("  $('#partControlStack').addEventListener('click', event => {"), ctx);
  const click = (partId, action = false) => handlers.click({target: {closest(selector) {
    if (selector !== (action ? '[data-part-action]' : '[data-part-command]')) return null;
    return {dataset: {partId: String(partId), moduleIndex: '0', enabled: 'false',
      partCommand: 'vessel.part.engine.setActive', partAction: 'generator.stop'}, closest: () => null};
  }}});
  const count = commands.length;
  click(1); click(1, true);
  assert.equal(commands.length, count, 'stale buttons must not send any command');
  click(2); click(2, true);
  assert.equal(commands.length, count + 2);
  assert.equal(commands.at(-1)[1].partId, 2);
  console.log('PASS part selection refresh + stale engine/action button rejection');

  ctx.syncSmartAssOffsets(true);
  assert.deepEqual(controls.map(input => input.value), ['30.0°', '90.0°', '180.0°']);
  controls[2].value = '181.0°';
  ctx.executeSmartAss(true);
  assert.equal(commands.at(-1)[1].pitchOffset, 30);
  assert.equal(commands.at(-1)[1].yawOffset, 90);
  assert.equal(commands.at(-1)[1].rollOffset, 181);
  target = 'Surface prograde'; ctx.syncSmartAssOffsets(true);
  assert.deepEqual(controls.map(input => input.value), ['5.0°', '10.0°', '15.0°']);
  ctx.document.activeElement = controls[0]; controls[0].value = '8.0°';
  ctx.syncSmartAssOffsets(true); assert.equal(controls[0].value, '8.0°');
  ctx.document.activeElement = null;
  target = 'Prograde'; ctx.syncSmartAssOffsets(true);
  assert.equal(controls[2].value, '45.0°'); assert.equal(controls[0].disabled, true);
  target = 'Off'; ctx.executeSmartAss();
  assert.equal(commands.at(-1)[0], 'mechjeb.smartass.off');
  console.log('PASS Smart ASS mode-specific readback + single-axis adjustment + focused draft');

  ctx.acceptRecorderSample({sequence: 100, revision: 1, timeSinceMark: 360});
  ctx.acceptRecorderRevision(2);
  assert.equal(state.recorderSamples.length, 0);
  ctx.acceptRecorderSample({sequence: 101, revision: 2, timeSinceMark: 0});
  ctx.acceptRecorderSample({sequence: 100, revision: 1, timeSinceMark: 360});
  ctx.acceptRecorderRevision(1);
  assert.equal(state.recorderSamples.length, 1);
  assert.equal(state.recorderSamples[0].timeSinceMark, 0);
  state.paused = true; ctx.acceptRecorderRevision(3);
  assert.equal(state.recorderSamples.length, 0, 'reset must work while paused');
  state.paused = false;
  let resolveHistory;
  ctx.fetch = async () => ({ok: true, json: () => new Promise(resolve => {resolveHistory = resolve;})});
  let pending = ctx.fetchRecorderHistory(); await new Promise(setImmediate);
  ctx.acceptRecorderSample({sequence: 103, revision: 3, timeSinceMark: 1});
  resolveHistory({protocol: 1, revision: 2, samples: [{sequence: 102, revision: 2}]});
  await pending;
  assert.equal(state.recorderSamples.length, 1);
  assert.equal(state.recorderSamples[0].sequence, 103, 'stale HTTP history must not restore an old recording');
  pending = ctx.fetchRecorderHistory(); await new Promise(setImmediate);
  ctx.acceptRecorderSample({sequence: 104, revision: 3, timeSinceMark: 2});
  resolveHistory({protocol: 1, revision: 3, samples: [{sequence: 103, revision: 3, timeSinceMark: 1}]});
  await pending;
  assert.equal(state.recorderSamples.length, 2, 'HTTP history must not erase concurrent live samples');
  console.log('PASS recorder epoch reset + paused reset + stale HTTP/WS isolation + history merge');

  let frames = 0, liveRenders = 0, recorderRenders = 0, logged = 0;
  const frameCtx = {
    state: {connection: 'live', currentSample: {}},
    requestAnimationFrame: () => { frames++; },
    renderLiveTelemetry: () => { liveRenders++; },
    renderRecorder: () => { recorderRenders++; throw new Error('isolated recorder failure'); },
    console: {error: () => { logged++; }}, Number
  };
  vm.createContext(frameCtx);
  vm.runInContext(extract('renderFrameSafely') + extract('tick'), frameCtx);
  frameCtx.tick(5000);
  frameCtx.tick(5016);
  assert.equal(frames, 2, 'every tick must schedule the next animation frame before rendering');
  assert.equal(liveRenders, 2, 'recorder failure must not stop MET and flight telemetry rendering');
  assert.equal(recorderRenders, 2);
  assert.equal(logged, 1, 'repeated panel errors must be rate limited');
  console.log('PASS DOM selector contract + isolated, persistent animation-frame rendering');
}
main().catch(error => {console.error(error); process.exitCode = 1;});
