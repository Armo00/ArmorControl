/* Read-only map subscriptions. Images are fetched once per body; paths use WebSocket. */
(() => {
  'use strict';
  const wrap = n => ((n + 180) % 360 + 360) % 360 - 180;
  const vesselKey = value => typeof value === 'string' ? value.replace(/-/g, '').toLowerCase() : value;
  const waypointPreferenceKey = 'armorControlMapShowWaypoints';
  function readWaypointVisibility(storage) {
    try { return (storage ?? globalThis.localStorage)?.getItem(waypointPreferenceKey) !== 'false'; }
    catch { return true; }
  }
  function saveWaypointVisibility(visible, storage) {
    try { (storage ?? globalThis.localStorage)?.setItem(waypointPreferenceKey, visible ? 'true' : 'false'); }
    catch { /* Display controls remain usable when browser storage is unavailable. */ }
  }
  async function decodeBasemap(blob) {
    // No blob: URL: the server's img-src policy intentionally allows self/data only.
    if (typeof createImageBitmap === 'function') return createImageBitmap(blob);
    const dataUrl = await new Promise((resolve, reject) => {
      const reader = new FileReader(); reader.onload = () => resolve(reader.result);
      reader.onerror = () => reject(new Error('Image data could not be read')); reader.readAsDataURL(blob);
    });
    const image = new Image();
    await new Promise((resolve, reject) => { image.onload = resolve; image.onerror = () => reject(new Error('Image could not be decoded')); image.src = dataUrl; });
    return image;
  }
  function splitDateline(points) {
    const lines = []; let line = [];
    for (const point of points) {
      if (!Array.isArray(point) || ![point[1], point[2]].every(Number.isFinite)) { if (line.length > 1) lines.push(line); line = []; continue; }
      const next = [point[1], wrap(point[2])];
      if (line.length && Math.abs(next[1] - line.at(-1)[1]) > 180) {
        const previous = line.at(-1), adjusted = next[1] + (next[1] < previous[1] ? 360 : -360), edge = adjusted > 180 ? 180 : -180;
        const latitude = previous[0] + (next[0] - previous[0]) * (edge - previous[1]) / (adjusted - previous[1]);
        line.push([latitude, edge]); lines.push(line); line = [[latitude, -edge]];
      }
      line.push(next);
    }
    if (line.length > 1) lines.push(line);
    return lines;
  }
  function targetVessel(packet) {
    const target = packet?.target;
    return packet?.available && target?.available && target.body === packet.body && target.id
      && vesselKey(target.id) !== vesselKey(packet.vesselId) && Number.isFinite(target.latitude) && Math.abs(target.latitude) <= 90
      && Number.isFinite(target.longitude) && Number.isFinite(target.altitude) ? target : null;
  }
  if (typeof module !== 'undefined' && module.exports) module.exports = { wrap, splitDateline, vesselKey, decodeBasemap, readWaypointVisibility, saveWaypointVisibility, targetVessel };
  if (typeof document === 'undefined') return;
  const root = document.getElementById('view-map'); if (!root) return;
  const $ = id => root.querySelector('#' + id), canvas = $('planetMapCanvas'), context = canvas.getContext('2d');
  $('mapWaypointLayer').checked = readWaypointVisibility();
  const state = { supported: false, connected: false, send: null, api: null, automatic: true, wantedKey: 'auto', packet: null, vesselId: null,
    active: false, image: null, imageKey: null, imageRequest: null, imageState: 'waiting', retryKey: null, retryAt: 0, frame: 0, timer: null, lastPacketAt: 0, scale: 1, panX: 0, panY: 0, touches: new Map(), regular: null };
  let waypointListKey = null;
  const t = (key, values) => window.ArmorI18n?.t(key, values) ?? key.replace(/\{(\w+)\}/g, (_, name) => values?.[name] ?? '');
  const km = value => Number.isFinite(value) ? `${(value / 1000).toLocaleString(undefined, { maximumFractionDigits: 1 })} km` : '—';
  const duration = seconds => { const total = Math.round(Math.abs(seconds) / 60), days = Math.floor(total / 1440), hours = Math.floor(total % 1440 / 60), minutes = total % 60; return `${days ? days + ' d ' : ''}${hours} h ${String(minutes).padStart(2, '0')} min`; };
  const relative = seconds => Number.isFinite(seconds) ? `${seconds < 0 ? '−' : '+'} ${duration(seconds)}` : '—';
  const controls = ['mapStartDirection', 'mapStartDays', 'mapStartHours', 'mapStartMinutes', 'mapWindowHours'];
  for (const [id, max] of [['mapStartDays', 365], ['mapStartHours', 23], ['mapStartMinutes', 59]]) {
    $(id).replaceChildren(...Array.from({ length: max + 1 }, (_, value) => { const option = document.createElement('option'); option.value = value; option.textContent = String(value).padStart(2, '0'); return option; }));
  }
  function desiredWindow() {
    const offset = (Number($('mapStartDays').value) * 86400 + Number($('mapStartHours').value) * 3600 + Number($('mapStartMinutes').value) * 60) * ($('mapStartDirection').value === 'before' ? -1 : 1);
    return { type: 'map.subscribe', active: state.active && !document.hidden, automatic: state.automatic, startOffset: offset, hours: Number($('mapWindowHours').value) };
  }
  function subscribe() {
    if (!state.connected || !state.supported) return;
    const request = desiredWindow(); state.wantedKey = request.automatic ? 'auto' : `${Math.round(request.startOffset)}:${request.hours}`;
    state.send?.(request);
  }
  function invalidateImage() {
    state.imageRequest?.abort(); state.imageRequest = null; state.image?.close?.(); state.image = null; state.imageKey = null; state.imageState = 'waiting'; state.retryKey = null; state.retryAt = 0;
  }
  async function loadImage(packet) {
    if (!packet.mapReady || !packet.mapKey || state.imageKey === packet.mapKey || state.retryKey === packet.mapKey && Date.now() < state.retryAt) return;
    state.imageRequest?.abort(); const controller = new AbortController(); state.imageRequest = controller;
    const key = packet.mapKey; state.image?.close?.(); state.image = null; state.imageKey = key; state.imageState = 'loading'; readouts();
    const timeout = setTimeout(() => controller.abort(), 10000);
    try {
      const base = state.api('/api/v1/planet-map');
      const response = await fetch(`${base}${base.includes('?') ? '&' : '?'}key=${encodeURIComponent(key)}`, { signal: controller.signal });
      if (!response.ok) throw new Error('map image unavailable');
      const image = await decodeBasemap(await response.blob());
      if (state.imageRequest === controller && state.imageKey === key && !controller.signal.aborted) {
        state.image = image; state.imageState = 'ready'; state.retryKey = null; readouts(); redraw();
      } else image.close?.();
    } catch (error) {
      if (state.imageRequest === controller && state.imageKey === key) {
        state.imageKey = null; state.imageState = 'failed'; state.retryKey = key; state.retryAt = Date.now() + 10000; readouts(); redraw();
        console.warn('[ArmorControl] Basemap download/decode failed:', error.name || 'Error');
      }
    } finally { clearTimeout(timeout); if (state.imageRequest === controller) state.imageRequest = null; }
  }
  function waypoints(packet) {
    if (!packet?.available || packet.waypointsAvailable === false || !Array.isArray(packet.waypoints)) return [];
    return packet.waypoints.filter(point => point && Number.isFinite(point.latitude) && Math.abs(point.latitude) <= 90 && Number.isFinite(point.longitude));
  }
  function waypointReadouts(packet) {
    const visibility = $('mapWaypointVisibility');
    visibility.hidden = $('mapWaypointLayer').checked;
    const visibilityText = t('Waypoints are hidden on the map.');
    if (visibility.textContent !== visibilityText) visibility.textContent = visibilityText;
    const points = waypoints(packet), available = packet?.available && packet.waypointsAvailable !== false && Array.isArray(packet.waypoints);
    $('mapWaypointCount').textContent = !packet?.available ? t('Waiting for waypoint data') : !available ? t('Waypoint telemetry unavailable')
      : points.length ? t('{0} waypoints', { 0: points.length }) : t('No waypoints on this body');
    const key = JSON.stringify([document.documentElement.lang, points]);
    if (waypointListKey === key) return;
    waypointListKey = key;
    $('mapWaypointList').replaceChildren(...[...points].sort((a,b) => Number(!!b.navigating) - Number(!!a.navigating) || String(a.name).localeCompare(String(b.name))).map(point => {
      const button = document.createElement('button'); button.type = 'button'; button.dataset.waypointId = String(point.id); button.classList.toggle('is-navigating', !!point.navigating);
      const name = document.createElement('span'); name.setAttribute('data-l10n-skip', ''); name.textContent = point.name || t('Unnamed waypoint');
      const detail = document.createElement('small'); detail.setAttribute('data-l10n-skip', '');
      detail.textContent = `${point.navigating ? t('Current navigation') + ' · ' : ''}${point.latitude.toFixed(4)}°, ${wrap(point.longitude).toFixed(4)}°`;
      button.append(name, detail);
      button.addEventListener('click', () => {
        state.scale = Math.max(4, state.scale); const f = frame();
        state.panX = -wrap(point.longitude) * f.scale; state.panY = point.latitude * f.scale; redraw();
      });
      return button;
    }));
  }
  function targetReadouts(packet) {
    const target = targetVessel(packet), raw = packet?.target;
    $('mapTargetName').textContent = packet?.available && raw?.name ? raw.name : '—';
    $('mapTargetCoordinates').textContent = target ? `${target.latitude.toFixed(4)}°, ${wrap(target.longitude).toFixed(4)}°` : '—';
    $('mapTargetAltitude').textContent = target ? km(target.altitude) : '—';
    $('mapCenterTarget').disabled = !target;
    const statuses = { ground: 'Ground target · position only', orbit: 'Target orbit · purple', atmosphere: 'Target is in the atmosphere; only its current position is shown.',
      missing_impact: 'Target impact prediction unavailable; only its current position is shown.', unavailable: 'Target vessel data unavailable.' };
    const messages = [];
    if (!packet?.available) messages.push(t('Waiting for active flight vessel'));
    else if (!raw) messages.push(t('Target map server upgrade required.'));
    else if (raw.status === 'other_body') messages.push(t('Target is on {0}; not shown on this body.', { 0: String(raw.bodyName || raw.body || '—').replace(/<[^>]*>/g, '').replace(/\^[A-Za-z]+$/, '') }));
    else if (!raw.id || raw.status === 'none') messages.push(t('No vessel target selected.'));
    else {
      messages.push(t(target ? statuses[raw.status] || statuses.orbit : statuses.unavailable));
      if (target?.endReason === 'atmosphere') messages.push(t('Target track ends at atmosphere entry; no target atmospheric prediction is available.'));
      if (target?.endReason === 'impact') messages.push(t('Target track ends at predicted terrain impact.'));
      if (target?.endReason === 'soi') messages.push(t('Target track ends at the SOI transition.'));
      if (target?.endReason === 'maneuver') messages.push(t('Target track ends at the next maneuver.'));
      if (target?.historyMissing) messages.push(t('Past target positions were not recorded; only future target track is shown.'));
      if (target && target.altitude > packet.radius * 12) messages.push(t('Target position exceeds the height limit.'));
    }
    if (!$('mapTargetLayer').checked) messages.push(t('Target vessel is hidden on the map.'));
    $('mapTargetStatus').textContent = messages.join(' ');
  }
  function readouts() {
    const p = state.packet;
    $('mapTextureSource').dataset.imageState = state.imageState;
    waypointReadouts(p);
    targetReadouts(p);
    const stale = state.connected && p?.available && performance.now() - state.lastPacketAt > 3500;
    $('mapLinkStatus').textContent = !state.connected ? t('Waiting for game') : !state.supported ? t('Map server upgrade required') : stale ? t('Map data stale') : t('Live map · 1 Hz paths');
    if (!p?.available) {
      $('mapPredictionStatus').textContent = t(p?.status === 'capture_failed' ? 'Map capture failed; retrying.' : 'Waiting for active flight vessel');
      for (const id of ['mapBodyName','mapApoapsis','mapPeriapsis','mapInclination','mapEccentricity','mapPeriod','mapCurrentCoordinates','mapCurrentAltitude','mapWindowReadout','mapEntryTime','mapImpactTime']) $(id).textContent = '—';
      $('mapTextureSource').textContent = t('Waiting for planet texture'); return;
    }
    $('mapBodyName').textContent = String(p.bodyName || p.body || '—').replace(/<[^>]*>/g, '').replace(/\^[A-Za-z]+$/, '');
    const textureFailure = 'Unable to extract planet color map: ';
    const textureStatus = p.mapStatus?.startsWith(textureFailure) ? t('Unable to extract planet color map: {0}', { 0: p.mapStatus.slice(textureFailure.length) })
      : t(p.mapStatus || 'Planet color map is not available.');
    $('mapTextureSource').textContent = !p.mapReady ? textureStatus
      : state.imageState === 'failed' ? t('Basemap download or decode failed.') : !state.image ? t('Loading planet basemap.') : t(p.mapSource || 'Planet color map ready.');
    $('mapApoapsis').textContent = p.eccentricity >= 1 ? t('None · escape') : km(p.apoapsis);
    $('mapPeriapsis').textContent = km(p.periapsis); $('mapPeriapsis').classList.toggle('is-negative', p.periapsis < 0);
    $('mapInclination').textContent = Number.isFinite(p.inclination) ? p.inclination.toFixed(2) + '°' : '—';
    $('mapEccentricity').textContent = Number.isFinite(p.eccentricity) ? p.eccentricity.toFixed(4) : '—';
    $('mapPeriod').textContent = Number.isFinite(p.period) ? duration(p.period) : t('Not applicable');
    const location = state.regular?.vesselId === p.vesselId && state.regular.body === p.body ? state.regular : p;
    $('mapCurrentCoordinates').textContent = Number.isFinite(location.latitude) && Number.isFinite(location.longitude) ? `${location.latitude.toFixed(4)}°, ${wrap(location.longitude).toFixed(4)}°` : '—'; $('mapCurrentAltitude').textContent = km(p.altitude);
    $('mapWindowReadout').textContent = `${relative(p.start - p.ut)} → ${relative(p.end - p.ut)}`;
    $('mapEntryTime').textContent = Number.isFinite(p.entryUt) ? relative(p.entryUt - p.ut) : '—';
    $('mapImpactTime').textContent = Number.isFinite(p.impactUt) ? relative(p.impactUt - p.ut) : '—';
    const reasons = { current: 'Now · next 5 hours', atmosphere: '10 minutes before atmosphere entry · 3 hours', impact: '3 hours before impact · 3 hours' };
    $('mapDefaultRule').textContent = t(reasons[p.defaultReason] || reasons.current);
    $('mapHeightLimit').textContent = t('Solid below {0}; hidden above {1}.', { 0: km(p.radius * .1), 1: km(p.radius * 12) });
    if (state.automatic && !controls.includes(document.activeElement?.id)) {
      const offset = p.start - p.ut, minutes = Math.round(Math.abs(offset) / 60);
      $('mapStartDirection').value = offset < 0 ? 'before' : 'after'; $('mapStartDays').value = Math.floor(minutes / 1440);
      $('mapStartHours').value = Math.floor(minutes % 1440 / 60); $('mapStartMinutes').value = minutes % 60; $('mapWindowHours').value = Math.round((p.end - p.start) / 3600);
    }
    const messages = [];
    if (!state.connected) messages.push(t('Disconnected · map data frozen'));
    if (stale) messages.push(t('Map data stale'));
    if (p.historyMissing) messages.push(t('History unavailable for part of this window; missing history is not reconstructed.'));
    if (p.entryTimeUnknown) messages.push(t('Atmosphere entry time was not recorded; the default window starts now.'));
    if (p.status === 'missing_aero') messages.push(t('Atmospheric prediction unavailable. Enable Trajectories computation to obtain the atmospheric path.'));
    if (p.status === 'missing_impact') messages.push(t('Terrain impact time is unavailable; the potentially colliding orbit is not extended.'));
    if (p.status === 'above_height_limit') messages.push(t('All predicted points in this window exceed the height limit.'));
    if (p.status === 'landed') messages.push(t('No flight prediction while landed or on EVA.'));
    if (p.endReason === 'soi') messages.push(t('Ground track ends at the SOI transition.'));
    if (p.endReason === 'impact') messages.push(t('Ground track ends at predicted impact.'));
    if (p.endReason === 'prediction_end') messages.push(t('Trajectories does not cover the entire selected window.'));
    if (p.endReason === 'maneuver') messages.push(t('Current orbit preview stops at the next maneuver.'));
    if (!p.mapReady) messages.push(t('Basemap unavailable; coordinates and track remain visible.'));
    if (p.mapReady && state.imageState === 'failed') messages.push(t('Basemap could not be displayed; retrying. Coordinates and track remain visible.'));
    $('mapPredictionStatus').textContent = messages.join(' ') || t('Live body-fixed ground track. Dashed sections indicate altitude, not later orbits.');
    $('mapPredictionStatus').classList.toggle('is-warning', messages.length > 0);
  }
  function redraw() { if (state.active && !state.frame) state.frame = requestAnimationFrame(() => { state.frame = 0; draw(); }); }
  function frame() {
    const rect = canvas.getBoundingClientRect(), width = rect.width, height = rect.height;
    const scale = Math.min((width - 38) / 360, (height - 78) / 180) * state.scale;
    return { width, height, scale, left: width / 2 - 180 * scale + state.panX, top: height / 2 - 90 * scale + state.panY };
  }
  function draw() {
    const f = frame(); if (f.width <= 1 || f.height <= 1) return;
    const ratio = Math.min(window.devicePixelRatio || 1, 2, 4096 / f.width);
    const width = Math.round(f.width * ratio), height = Math.round(f.height * ratio);
    if (canvas.width !== width) canvas.width = width;
    if (canvas.height !== height) canvas.height = height;
    context.setTransform(ratio, 0, 0, ratio, 0, 0);
    const css = getComputedStyle(root), color = name => css.getPropertyValue(name).trim();
    context.fillStyle = color('--ink-2'); context.fillRect(0, 0, f.width, f.height);
    const p = state.packet, project = (lat, lon) => [f.left + (lon + 180) * f.scale, f.top + (90 - lat) * f.scale];
    context.save(); context.beginPath(); context.rect(0, 0, f.width, f.height); context.clip();
    if (state.image) { context.globalAlpha = .72; context.drawImage(state.image, f.left, f.top, 360 * f.scale, 180 * f.scale); context.globalAlpha = 1; }
    if ($('mapGridLayer').checked) {
      context.strokeStyle = color('--line'); context.lineWidth = 1; context.setLineDash([]);
      for (let lon = -180; lon <= 180; lon += 30) { const [x] = project(0, lon); context.beginPath(); context.moveTo(x, f.top); context.lineTo(x, f.top + 180 * f.scale); context.stroke(); }
      for (let lat = -90; lat <= 90; lat += 30) { const [, y] = project(lat, 0); context.beginPath(); context.moveTo(f.left, y); context.lineTo(f.left + 360 * f.scale, y); context.stroke(); }
    }
    function track(segments, target = false) {
      for (const segment of segments || []) {
        context.setLineDash(segment.dashed ? [6, 4] : []);
        for (const line of splitDateline(segment.points || [])) {
          context.beginPath(); line.forEach(([lat, lon], index) => { const xy = project(lat, lon); if (index) context.lineTo(...xy); else context.moveTo(...xy); });
          context.lineWidth = 4.5; context.strokeStyle = color('--ink'); context.stroke();
          context.lineWidth = 2; context.strokeStyle = color(target ? '--map-target' : segment.source === 'atmosphere' ? '--coral' : segment.source === 'history' ? '--muted' : '--cyan'); context.stroke();
        }
      }
    }
    if (p?.available && $('mapOrbitLayer').checked) track(p.segments);
    const target = targetVessel(p);
    if (target && !target.onSurface && $('mapTargetLayer').checked) track(target.segments, true);
    context.setLineDash([]); context.font = '12px "Microsoft YaHei UI", "Segoe UI", sans-serif';
    const boxes = [];
    function label(text, x, y) {
      const width = context.measureText(text).width, left = Math.max(8, Math.min(f.width - width - 8, x + 12)), top = Math.max(45, Math.min(f.height - 48, y - 14));
      if (boxes.some(b => left < b.right + 4 && left + width > b.left - 4 && top < b.bottom + 4 && top + 15 > b.top - 4)) return;
      boxes.push({ left, right: left + width, top, bottom: top + 15 }); context.lineWidth = 4; context.strokeStyle = color('--ink'); context.strokeText(text, left, top); context.fillStyle = color('--paper'); context.fillText(text, left, top);
    }
    function marker(lat, lon, text, kind = 'vessel') {
      const [x, y] = project(lat, lon); if (x < 6 || y < 6 || x > f.width - 6 || y > f.height - 6) return;
      context.beginPath();
      if (kind === 'event') { context.moveTo(x, y - 6); context.lineTo(x + 6, y); context.lineTo(x, y + 6); context.lineTo(x - 6, y); context.closePath(); }
      else if (kind === 'target') context.rect(x - 5, y - 5, 10, 10);
      else if (kind === 'waypoint' || kind === 'navigation') { context.moveTo(x, y + 8); context.lineTo(x - 5, y - 2); context.arc(x, y - 4, 5, Math.PI, 0); context.lineTo(x, y + 8); context.closePath(); }
      else context.arc(x, y, 6, 0, Math.PI * 2);
      context.fillStyle = color(kind === 'navigation' ? '--amber' : '--ink-2'); context.fill();
      context.strokeStyle = color(kind === 'target' ? '--map-target' : kind === 'event' || kind === 'navigation' ? '--amber' : kind === 'waypoint' ? '--paper' : '--cyan');
      context.lineWidth = 2; context.stroke(); if (text) label(text, x, y);
    }
    if (p?.available && Number.isFinite(p.altitude) && p.altitude <= p.radius * 12) {
      const current = state.regular?.vesselId === p.vesselId && state.regular.body === p.body ? state.regular : p;
      if (Number.isFinite(current.latitude) && Number.isFinite(current.longitude)) marker(current.latitude, wrap(current.longitude), t('Current vessel'));
    }
    if (target && $('mapTargetLayer').checked && target.altitude <= p.radius * 12) marker(target.latitude, wrap(target.longitude), target.name || t('Target vessel'), 'target');
    if ($('mapWaypointLayer').checked) for (const point of waypoints(p)) marker(point.latitude, wrap(point.longitude), point.name || t('Unnamed waypoint'), point.navigating ? 'navigation' : 'waypoint');
    if (p?.available && $('mapEventLayer').checked) {
      for (const [ut, key] of [[p.entryUt, 'Atmosphere entry'], [p.impactUt, 'Predicted impact']]) {
        if (!Number.isFinite(ut) || ut < p.start || ut > p.end) continue;
        let point;
        for (const segment of p.segments || []) for (let i = 1; i < segment.points.length; i++) {
          const a = segment.points[i - 1], b = segment.points[i];
          if (a[0] <= ut && b[0] >= ut) { const q = b[0] === a[0] ? 0 : (ut - a[0]) / (b[0] - a[0]); point = [a[1] + (b[1] - a[1]) * q, wrap(a[2] + wrap(b[2] - a[2]) * q)]; }
        }
        if (point) marker(...point, t(key), 'event');
      }
    }
    context.restore();
    context.font = '11px "Microsoft YaHei UI", "Segoe UI", sans-serif'; context.fillStyle = color('--muted');
    if ($('mapGridLayer').checked && state.scale <= 1.3) for (const lon of [-120, -60, 0, 60, 120]) { const [x] = project(0, lon); context.textAlign = 'center'; context.fillText(`${Math.abs(lon)}°${lon < 0 ? 'W' : lon > 0 ? 'E' : ''}`, x, f.height - 12); }
    context.textAlign = 'left';
    if (p?.radius > 0) {
      const latitude = Math.max(-80, Math.min(80, 90 - (f.height / 2 - f.top) / f.scale)), kmPerPixel = p.radius * Math.PI / 180 * Math.cos(latitude * Math.PI / 180) / f.scale / 1000;
      const distance = [1,2,5,10,20,50,100,200,500,1000,2000,5000].filter(n => n <= 100 * kmPerPixel).at(-1) || 1;
      context.setLineDash([]); context.strokeStyle = color('--paper'); context.lineWidth = 1; context.beginPath(); context.moveTo(15, f.height - 32); context.lineTo(15, f.height - 27); context.lineTo(15 + distance / kmPerPixel, f.height - 27); context.lineTo(15 + distance / kmPerPixel, f.height - 32); context.stroke(); context.fillStyle = color('--paper'); context.fillText(`${distance.toLocaleString()} km`, 15, f.height - 38);
    }
  }
  function zoom(scale, x, y) {
    const f = frame(), next = Math.max(1, Math.min(40, scale)), factor = next / state.scale;
    state.panX = (state.panX - (x - f.width / 2)) * factor + x - f.width / 2; state.panY = (state.panY - (y - f.height / 2)) * factor + y - f.height / 2; state.scale = next; redraw();
  }
  canvas.addEventListener('pointerdown', event => { const r = canvas.getBoundingClientRect(); state.touches.set(event.pointerId, [event.clientX - r.left, event.clientY - r.top]); canvas.setPointerCapture(event.pointerId); });
  canvas.addEventListener('pointermove', event => {
    if (!state.touches.has(event.pointerId)) return; const r = canvas.getBoundingClientRect(), old = [...state.touches.values()], previous = state.touches.get(event.pointerId), point = [event.clientX - r.left, event.clientY - r.top]; state.touches.set(event.pointerId, point);
    if (old.length === 1) { state.panX += point[0] - previous[0]; state.panY += point[1] - previous[1]; redraw(); }
    else if (old.length === 2) { const next = [...state.touches.values()], a = Math.hypot(old[1][0] - old[0][0], old[1][1] - old[0][1]), b = Math.hypot(next[1][0] - next[0][0], next[1][1] - next[0][1]); if (a > 0) zoom(state.scale * b / a, (next[0][0] + next[1][0]) / 2, (next[0][1] + next[1][1]) / 2); }
  });
  for (const event of ['pointerup','pointercancel','lostpointercapture']) canvas.addEventListener(event, e => state.touches.delete(e.pointerId));
  canvas.addEventListener('contextmenu', event => event.preventDefault());
  canvas.addEventListener('wheel', event => { event.preventDefault(); const r = canvas.getBoundingClientRect(); zoom(state.scale * Math.exp(-event.deltaY * .0015), event.clientX - r.left, event.clientY - r.top); }, { passive: false });
  $('mapZoomIn').addEventListener('click', () => { const f = frame(); zoom(state.scale * 1.6, f.width / 2, f.height / 2); });
  $('mapZoomOut').addEventListener('click', () => { const f = frame(); zoom(state.scale / 1.6, f.width / 2, f.height / 2); });
  $('mapWorld').addEventListener('click', () => { state.scale = 1; state.panX = state.panY = 0; redraw(); });
  $('mapFitTrack').addEventListener('click', () => {
    const points = (state.packet?.segments?.flatMap(segment => segment.points) || []).filter(p => Number.isFinite(p?.[1]) && Number.isFinite(p?.[2])); if (!points.length) return;
    const latitudes = points.map(p => p[1]), longitudes = points.map(p => p[2]), left = Math.min(...longitudes), right = Math.max(...longitudes), bottom = Math.min(...latitudes), top = Math.max(...latitudes);
    const f = frame(); state.scale = 1; state.panX = state.panY = 0; const base = frame();
    const scale = Math.max(1, Math.min(40, (f.width - 100) / ((right - left + 8) * base.scale), (f.height - 100) / ((top - bottom + 8) * base.scale)));
    state.scale = scale; state.panX = -(left + right) * .5 * base.scale * scale; state.panY = (bottom + top) * .5 * base.scale * scale; redraw();
  });
  for (const id of controls) $(id).addEventListener('change', () => { state.automatic = false; $('mapAutomaticWindow').checked = false; clearTimeout(state.timer); state.timer = setTimeout(subscribe, 100); });
  $('mapAutomaticWindow').addEventListener('change', () => { state.automatic = $('mapAutomaticWindow').checked; subscribe(); });
  for (const id of ['mapOrbitLayer','mapEventLayer','mapGridLayer']) $(id).addEventListener('change', redraw);
  $('mapWaypointLayer').addEventListener('change', () => { saveWaypointVisibility($('mapWaypointLayer').checked); readouts(); redraw(); });
  $('mapTargetLayer').addEventListener('change', () => { readouts(); redraw(); });
  $('mapCenterTarget').addEventListener('click', () => {
    const target = targetVessel(state.packet); if (!target) return;
    state.scale = Math.max(4, state.scale); const f = frame();
    state.panX = -wrap(target.longitude) * f.scale; state.panY = target.latitude * f.scale; redraw();
  });
  document.addEventListener('visibilitychange', () => { state.touches.clear(); subscribe(); redraw(); });
  document.addEventListener('armorcontrol:languagechange', () => { readouts(); redraw(); });
  new ResizeObserver(redraw).observe($('planetMapCanvas'));
  window.ArmorMap = {
    connect({ supported, send, api }) { state.supported = supported; state.connected = true; state.send = send; state.api = api; subscribe(); readouts(); },
    disconnect() { state.connected = false; readouts(); },
    active(active) { state.active = active; subscribe(); redraw(); },
    context(vesselId) { vesselId = vesselKey(vesselId); if (state.vesselId === vesselId) return; state.vesselId = vesselId; state.packet = null; state.regular = null; state.automatic = true; $('mapAutomaticWindow').checked = true; state.scale = 1; state.panX = state.panY = 0; invalidateImage(); subscribe(); readouts(); redraw(); },
    accept(packet) { packet = { ...packet, vesselId: vesselKey(packet.vesselId) }; if (packet.available && (packet.requestKey !== state.wantedKey || state.vesselId && packet.vesselId !== state.vesselId)) return; if (state.packet?.body !== packet.body) invalidateImage(); state.lastPacketAt = performance.now(); state.packet = packet; readouts(); if (packet.available) loadImage(packet); redraw(); },
    regular(packet) { state.regular = { ...packet, vesselId: vesselKey(packet.vesselId) }; if (state.active) { readouts(); redraw(); } }
  };
  setInterval(() => { if (state.active && !document.hidden && state.packet) readouts(); }, 1000);
  readouts();
})();
