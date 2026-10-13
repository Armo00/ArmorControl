(() => {
  const sourceText = element => window.ArmorI18n ? window.ArmorI18n.sourceText(element) : (element?.textContent || '');
  const uiLocale = () => window.ArmorI18n?.locale || 'en-US';
  function userText(element, value, fallback = '—') {
    if (!element) return;
    if (value) element.setAttribute('data-l10n-skip', '');
    else element.removeAttribute('data-l10n-skip');
    element.textContent = value || fallback;
  }
  const $ = (selector, scope = document) => scope.querySelector(selector);
  const $$ = (selector, scope = document) => [...scope.querySelectorAll(selector)];
  const fragmentToken = new URLSearchParams(location.hash.slice(1)).get('token');
  if (fragmentToken) localStorage.setItem('armorControlToken', fragmentToken);
  const accessToken = fragmentToken || localStorage.getItem('armorControlToken') || '';
  const state = {
    start: Date.now(), paused: false, toastTimer: null,
    connection: 'connecting', socket: null, reconnectTimer: null, heartbeatTimer: null,
    vesselSocket: null, vesselReconnectTimer: null, vesselObjectUrl: null,
    reconnectAttempt: 0, liveSince: 0, lastMessageAt: 0, lastTelemetryAt: 0, lastPingAt: 0,
    lastRegularTelemetryAt: 0, lastPongAt: 0, protocolBlocked: false, previousSample: null,
    currentSample: null, regularSample: null, sampleTimes: [], commandSequence: 0, clientId: null,
    structureRevision: 0, recorderSamples: [], recorderDirty: false, recorderRenderedAt: 0,
    recorderGroup: 'trajectory', recorderCursorIndex: null, recorderHiddenFields: new Set(), recorderDomains: null,
    porkchopRevision: 0, porkchopResult: null, porkchopDepartureIndex: 0, porkchopDurationIndex: 0,
    quietCommands: new Set(), controlTimer: null,
    ascentDraft: false, smartAssDraft: false, smartAssPendingTarget: null, smartAssPendingCommandId: null,
    smartAssOffsetStep: 1,
    smartAssActualTarget: 'OFF', serverCapabilities: new Set(), languagePendingCommandId: null,
    control: { pitch: 0, yaw: 0, roll: 0, throttle: 0, engaged: false, throttleLocked: false }, accessToken,
    vesselStructure: null, crewSignature: '', selectedPartId: null, highlightedPartId: null, partGroup: 'all',
    targetCatalog: [], targetCatalogSignature: '', selectedTargetId: null, expandedTargets: new Set(), lastAutomation: null,
    ascentVisual: null, envelopeDraft: false, trajectorySettingsDraft: false, trajectorySettingsPendingCommandId: null,
    trajectoryProfileDraft: false, trajectoryTargetDraft: false
  };

  function showToast(message) {
    const toast = $('#toast');
    toast.textContent = message;
    toast.classList.add('is-visible');
    clearTimeout(state.toastTimer);
    state.toastTimer = setTimeout(() => toast.classList.remove('is-visible'), 2400);
  }

  function setConnectionState(mode, label, detail) {
    state.connection = mode;
    const pill = $('#connectionPill');
    pill.classList.remove('is-connecting', 'is-live', 'is-demo', 'is-offline', 'is-stale');
    pill.classList.add(`is-${mode}`);
    $('#connectionState').textContent = label;
    $('#connectionDetail').textContent = detail;
    const link = $('#armorLinkState');
    if (link) {
      link.textContent = mode === 'live' ? 'CONNECTED' : mode === 'stale' ? 'STALE' : 'OFFLINE';
      link.parentElement.classList.toggle('status-ok', mode === 'live');
    }
  }

  function socketUrl() {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const host = location.host || '127.0.0.1:8765';
    const token = state.accessToken ? `?token=${encodeURIComponent(state.accessToken)}` : '';
    return `${protocol}//${host}/ws/realtime${token}`;
  }

  function vesselSocketUrl() {
    const protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const host = location.host || '127.0.0.1:8765';
    const token = state.accessToken ? `?token=${encodeURIComponent(state.accessToken)}` : '';
    return `${protocol}//${host}/ws/vessel${token}`;
  }

  function vesselViewIsActive() {
    return $('#view-vessel')?.classList.contains('is-active');
  }

  function vesselImageIsDesired() {
    return vesselViewIsActive() && !$('#vesselRenderStage')?.hidden;
  }

  function disconnectVesselImage() {
    clearTimeout(state.vesselReconnectTimer);
    state.vesselReconnectTimer = null;
    const socket = state.vesselSocket;
    state.vesselSocket = null;
    if (socket && socket.readyState < WebSocket.CLOSING) socket.close(1000, 'vessel view hidden');
  }

  function connectVesselImage() {
    clearTimeout(state.vesselReconnectTimer);
    if (!vesselImageIsDesired() || state.connection !== 'live') return;
    if (state.vesselSocket && state.vesselSocket.readyState < WebSocket.CLOSING) return;
    const stage = $('#vesselRenderStage');
    const status = $('#vesselRenderStatus span');
    if (status && !stage.classList.contains('has-image')) status.textContent = 'Waiting for vessel rendering';
    let socket;
    try { socket = new WebSocket(vesselSocketUrl()); } catch { return; }
    socket.binaryType = 'blob';
    state.vesselSocket = socket;
    socket.addEventListener('message', event => {
      // WebSocket binary Blobs are delivered without a media type in Chromium.
      // Re-wrap every payload so <img> decodes the low-frequency frame as JPEG.
      const blob = new Blob([event.data], { type: 'image/jpeg' });
      const image = $('#vesselRenderImage');
      const reader = new FileReader();
      reader.addEventListener('load', () => {
        image.onload = () => { syncVesselRenderSurface(); stage.classList.add('has-image'); };
        image.onerror = () => stage.classList.remove('has-image');
        image.src = reader.result;
      });
      reader.readAsDataURL(blob);
    });
    socket.addEventListener('close', () => {
      resetAscentEdits();
      if (state.vesselSocket === socket) state.vesselSocket = null;
      if (vesselImageIsDesired() && state.connection === 'live') {
        state.vesselReconnectTimer = setTimeout(connectVesselImage, 1200);
      }
    });
    socket.addEventListener('error', () => socket.close());
  }

  function apiUrl(path) {
    return state.accessToken ? `${path}?token=${encodeURIComponent(state.accessToken)}` : path;
  }

  function scheduleReconnect() {
    clearTimeout(state.reconnectTimer);
    if (state.protocolBlocked) return;
    const delay = Math.min(5000, 750 * Math.pow(1.65, state.reconnectAttempt++));
    const detail = `Check server status, port and network. Retrying in ${(delay / 1000).toFixed(1)} s.`;
    setConnectionState('offline', 'Waiting for game', detail);
    state.reconnectTimer = setTimeout(connectRealtime, delay);
  }

  function connectRealtime() {
    state.serverCapabilities = new Set();
    state.languagePendingCommandId = null;
    updateLanguageControl();
    clearTimeout(state.reconnectTimer);
    clearInterval(state.heartbeatTimer);
    if (state.socket && state.socket.readyState < 2) state.socket.close();
    setConnectionState('connecting', 'Connecting', 'WebSocket · /ws/realtime');
    let socket;
    try { socket = new WebSocket(socketUrl()); }
    catch { scheduleReconnect(); return; }
    state.socket = socket;

    socket.addEventListener('open', () => {
      state.reconnectAttempt = 0;
      state.highlightedPartId = null;
      setConnectionState('connecting', 'Link established', 'Waiting for first telemetry frame');
      state.heartbeatTimer = setInterval(() => {
        if (socket.readyState === WebSocket.OPEN) {
          state.lastPingAt = performance.now();
          socket.send(JSON.stringify({ type: 'ping', sequence: ++state.commandSequence }));
        }
      }, 1000);
    });
    socket.addEventListener('message', event => {
      let message;
      try { message = JSON.parse(event.data); } catch { return; }
      state.lastMessageAt = performance.now();
      if (message.type === 'hello') {
        if (message.protocol !== 1) {
          state.protocolBlocked = true;
          setConnectionState('offline', 'Version mismatch', `Web v1 · server v${message.protocol ?? '?'}`);
          showToast('ArmorControl Protocol mismatch. Update the web files or DLL.');
          socket.close(1000, 'protocol mismatch');
          return;
        }
        state.clientId = message.clientId;
        window.ArmorMap?.connect({ supported: message.mapVersion === 1, api: apiUrl,
          send: payload => { if (socket.readyState === WebSocket.OPEN) socket.send(JSON.stringify(payload)); } });
        state.recorderConnection = (state.recorderConnection || 0) + 1;
        state.recorderRevision = undefined;
        state.recorderSamples = [];
        state.serverCapabilities = new Set(Array.isArray(message.capabilities) ? message.capabilities : []);
        updateLanguageControl();
        const quickloadButton = $('#holdQuickload');
        if (quickloadButton) {
          const supported = state.serverCapabilities.has('game.quicksave.load');
          quickloadButton.disabled = !supported;
          quickloadButton.title = supported ? '' : 'Restart KSP to load an ArmorControl DLL with quicksave support.';
        }
        fetchStructuralData();
        fetchRecorderHistory();
        if (vesselViewIsActive()) connectVesselImage();
        return;
      }
      if (message.type === 'server.settings') window.ArmorI18n?.followServerLanguage(message.language);
      if (message.type === 'telemetry.fast') acceptTelemetry(message);
      if (message.type === 'telemetry.regular') acceptRegularTelemetry(message);
      if (message.type === 'telemetry.flightPanel') renderFlightPanel(message);
      if (message.type === 'telemetry.automation') renderAutomation(message);
      if (message.type === 'telemetry.map') window.ArmorMap?.accept(message);
      if (message.type === 'vessel.structure') acceptVesselStructure(message);
      if (message.type === 'recorder.sample') acceptRecorderSample(message);
      if (message.type === 'recorder.reset') acceptRecorderRevision(message.revision);
      if (message.type === 'event.context') {
        window.ArmorMap?.context(message.vesselId);
        state.structureRevision = 0;
        fetchStructuralData();
        fetchRecorderHistory();
      }
      if (message.type === 'event.vessel' && message.changes?.some(change => change === 'parts' || change === 'crew')) {
        fetchStructuralData();
      }
      if (message.type === 'pong') {
        state.lastPongAt = performance.now();
        if ($('#armorLinkLatency')) $('#armorLinkLatency').textContent = `RTT ${Math.max(0, state.lastPongAt - state.lastPingAt).toFixed(0)} ms`;
      }
      if (message.type === 'command.ack') {
        acknowledgeAscentSetting(message);
        if (message.commandId === state.languagePendingCommandId) {
          state.languagePendingCommandId = null;
          updateLanguageControl();
        }
        const quiet = state.quietCommands.delete(message.commandId);
        if (message.commandId === state.smartAssPendingCommandId && message.success) state.smartAssPendingCommandId = null;
        if (message.commandId === state.smartAssPendingCommandId && !message.success) {
          state.smartAssDraft = false;
          state.smartAssPendingTarget = null;
          state.smartAssPendingCommandId = null;
        }
        if (message.commandId === state.trajectorySettingsPendingCommandId) {
          state.trajectorySettingsPendingCommandId = null;
          state.trajectorySettingsDraft = false;
        }
        if (quiet && !message.success && state.control.engaged) disarmContinuousControl(false);
        if (!quiet || !message.success) showToast(message.success ? `Command ${message.commandId} Executed` : `Command failed:${message.message}`);
      }
      if (message.type === 'error') showToast(`Server error:${message.message || message.code || 'Unknown error'}`);
    });
    socket.addEventListener('close', () => {
      window.ArmorMap?.disconnect();
      state.languagePendingCommandId = null;
      updateLanguageControl();
      clearInterval(state.heartbeatTimer);
      disarmContinuousControl(false);
      state.quietCommands.clear();
      if (state.socket === socket) scheduleReconnect();
    });
    socket.addEventListener('error', () => socket.close());
  }

  function acceptTelemetry(message) {
    const now = performance.now();
    const previousConnection = state.connection;
    state.lastTelemetryAt = now;
    state.previousSample = state.currentSample;
    state.currentSample = { receivedAt: now, data: message };
    state.sampleTimes.push(now);
    while (state.sampleTimes.length && state.sampleTimes[0] < now - 1000) state.sampleTimes.shift();
    if (previousConnection !== 'live') {
      state.liveSince = now;
      showToast(previousConnection === 'stale' ? 'Live telemetry restored' : 'Connected to ArmorControl game server');
    }
    setConnectionState('live', 'Live', `${state.sampleTimes.length} Hz · #${message.sequence}`);
    userText($('#activeVesselName'), message.vesselName, 'No active vessel');
    $('#vesselOrdinal').textContent = message.vesselId ? message.vesselId.slice(0, 2).toUpperCase() : '—';
    if (!message.vesselId) {
      $('#currentThrust').textContent = '—';
      $('#localTwr').textContent = '—';
    }
    updateAscentFlightProgress(message.altitude);
  }

  function acceptRegularTelemetry(message) {
    window.ArmorMap?.regular(message);
    state.lastRegularTelemetryAt = performance.now();
    state.regularSample = message;
    renderAscentOrbitReadouts(message);
    if (state.vesselStructure) renderAerodynamicCenter(state.vesselStructure.aerodynamicCenter);
    const kilometers = meters => `${(Number(meters || 0) / 1000).toFixed(1)} km`;
    $('#currentApoapsis').textContent = kilometers(message.apoapsis);
    const plannerApsides = $('#plannerCurrentApsides');
    if (plannerApsides) plannerApsides.textContent = `${kilometers(message.apoapsis)} / ${kilometers(message.periapsis)}`;
    $('#timeToApoapsis').textContent = `T− ${formatDuration(message.timeToApoapsis)}`;
    const periapsisKm = Number(message.periapsis || 0) / 1000;
    const pe = $('#currentPeriapsis');
    pe.textContent = `${periapsisKm < 0 ? '−' : ''}${Math.abs(periapsisKm).toFixed(1)} km`;
    pe.dataset.pe = periapsisKm;
    pe.classList.toggle('is-negative', periapsisKm < 0);
    $('#periapsisStatus').textContent = periapsisKm < 0 ? 'SUB-ORBITAL' : `T− ${formatDuration(message.timeToPeriapsis)}`;
    const situations = { ORBITING: 'Orbiting', SUB_ORBITAL: 'Suborbital', FLYING: 'Atmospheric flight', LANDED: 'Landed', SPLASHED: 'Splashed', ESCAPING: 'Escaping' };
    $('#flightSituation').textContent = situations[message.situation] || message.situation || 'No active vessel';
    const body = message.body || '—';
    $('#orbitMapBody').textContent = body.toUpperCase();
    $('#porkOriginBody').textContent = body;
    const target = message.targetName || 'Not selected';
    $('#porkTargetBody').textContent = target;
    $('#porkRouteLabel').textContent = `INTERPLANETARY SOLVER / ${body.toUpperCase()} → ${target.toUpperCase()}`;
    renderOrbitSummaries(message);
  }

  function renderOrbitSummaries(message) {
    if (!message) return;
    const situations = { ORBITING: 'Orbiting', SUB_ORBITAL: 'Suborbital', FLYING: 'Atmospheric flight', LANDED: 'Landed', SPLASHED: 'Splashed', ESCAPING: 'Escaping' };
    const apoapsis = Number(message.apoapsis || 0) / 1000;
    const periapsis = Number(message.periapsis || 0) / 1000;
    $$('.orbit-summary-values').forEach(summary => {
      const values = summary.querySelectorAll('b');
      if (values[0]) values[0].textContent = `AP ${apoapsis.toFixed(1)} km`;
      if (values[1]) {
        values[1].textContent = `PE ${periapsis < 0 ? '−' : ''}${Math.abs(periapsis).toFixed(1)} km`;
        values[1].classList.toggle('is-negative', periapsis < 0);
        values[1].dataset.pe = periapsis;
      }
    });
    $$('.ledger-feature').filter(item => sourceText(item.querySelector(':scope > span')).trim() === 'Current orbit')
      .forEach(item => {
        const detail = item.querySelector(':scope > small');
        if (detail) detail.innerHTML = `<span data-l10n-skip>${escapeHtml(message.body || '—')}</span> · <span>${escapeHtml(situations[message.situation] || message.situation || '—')}</span>`;
      });
  }

  function renderFlightPanel(message) {
    $('#ascentActualInclination').textContent = Number.isFinite(message.orbitInclination) ? `${message.orbitInclination.toFixed(3)}°` : '—';
    $('#ascentActualPeriod').textContent = Number.isFinite(message.orbitPeriod) && message.orbitPeriod > 0 && message.orbitEccentricity < 1 ? formatDuration(message.orbitPeriod) : 'N/A';
    const finite = value => Number.isFinite(Number(value));
    const fixed = (value, digits = 1) => finite(value) ? Number(value).toFixed(digits) : '—';
    const duration = value => finite(value) && Number(value) >= 0 ? formatDuration(Number(value)) : 'N/A';
    const setRows = (label, value) => {
      $$('.fp-row, .data-cell').filter(row => sourceText(row.querySelector(':scope > span')).trim() === label)
        .forEach(row => { const output = row.querySelector(':scope > b'); if (output) output.textContent = value; });
    };
    const textOrUnavailable = value => {
      const text = String(value ?? '').trim();
      return !text || /^(inf(?:inity)?|nan)$/i.test(text) ? 'N/A' : text;
    };
    const map = {
      'Inclination': `${fixed(message.orbitInclination, 3)}°`, 'Period': duration(message.orbitPeriod),
      'Orbital speed': `${fixed(message.orbitalSpeed, 0)} m/s`, 'Time to apoapsis': duration(message.timeToApoapsis),
      'Time to periapsis': duration(message.timeToPeriapsis), 'SOI transition': message.timeToSoiTransition || 'N/A',
      'Eccentricity': fixed(message.orbitEccentricity, 4), 'Bottom altitude': `${fixed(Number(message.altitudeBottom) / 1000, 2)} km`,
      'Coordinates': message.coordinates || 'N/A', 'Heading': `${fixed(message.heading, 1)}°`,
      'Surface gravity': `${fixed(message.surfaceGravity, 2)} m/s²`, 'Surface speed': `${fixed(message.surfaceSpeed, 0)} m/s`,
      'Vertical speed': `${finite(message.verticalSpeed) && Number(message.verticalSpeed) >= 0 ? '+' : ''}${fixed(message.verticalSpeed, 1)} m/s`,
      'Horizontal surface speed': `${fixed(message.horizontalSurfaceSpeed, 0)} m/s`, 'Maximum acceleration': `${fixed(message.maximumAcceleration, 2)} m/s²`,
      'Thrust acceleration': `${fixed(message.currentAcceleration, 2)} m/s²`, 'Maximum thrust': `${fixed(Number(message.maximumThrust) / 1000, 2)} MN`,
      'Current thrust': `${fixed(Number(message.currentThrust) / 1000, 2)} MN`, 'Sea-level TWR': fixed(message.surfaceTwr, 2),
      'Local TWR': fixed(message.localTwr, 2), 'Current throttle TWR': fixed(message.throttleTwr, 2),
      'Current G-force': `${fixed(message.geeForce, 2)} g`, 'Stage Δv · ATM / VAC': `${fixed(message.stageDeltaVAtmosphere, 0)} / ${fixed(message.stageDeltaVVacuum, 0)} m/s`,
      'Total Δv · ATM / VAC': `${fixed(message.totalDeltaVAtmosphere, 0)} / ${fixed(message.totalDeltaVVacuum, 0)} m/s`,
      'Time at current throttle': duration(message.stageTimeCurrentThrottle), 'Time at full throttle': duration(message.stageTimeFullThrottle),
      'Hover time remaining': duration(message.stageTimeHover), 'Drag acceleration': `${fixed(message.atmosphericDrag, 2)} m/s²`,
      'Terminal velocity': `${fixed(message.terminalVelocity, 0)} m/s`, 'Crew capacity': fixed(message.crewCapacity, 0),
      'Current crew': fixed(message.crewCount, 0), 'Dry mass': `${fixed(message.dryMass, 3)} t`,
      'Current mass': `${fixed(message.vesselMass, 3)} t`, 'Part count': fixed(message.partCount, 0),
      'Vessel cost': finite(message.vesselCost) ? Number(message.vesselCost).toLocaleString(uiLocale(), { maximumFractionDigits: 0 }) : '—',
      'Angle of attack': `${signed(Number(message.angleOfAttack) || 0)}°`, 'Angle of sideslip': `${signed(Number(message.angleOfSideslip) || 0)}°`,
      'Atmospheric pressure': `${fixed(message.staticPressureKpa, 2)} kPa`, 'Current biome': message.currentBiome || 'N/A',
      'Dynamic pressure': `${fixed(message.dynamicPressureKpa, 1)} kPa`, 'Suicide burn countdown': textOrUnavailable(message.suicideBurnCountdown),
      'Time to impact': textOrUnavailable(message.timeToImpact), 'Closest approach': textOrUnavailable(message.targetClosestApproachDistance),
      'Current target distance': textOrUnavailable(message.targetDistance), 'Heading': `${fixed(message.heading, 1)}°`
    };
    Object.entries(map).forEach(([label, value]) => setRows(label, value));
    $('#currentThrust').textContent = fixed(Number(message.currentThrust) / 1000, 2);
    $('#localTwr').textContent = fixed(message.localTwr, 2);
    $('#maximumThrust').textContent = `Maximum ${fixed(Number(message.maximumThrust) / 1000, 2)} MN`;
    $('#throttleTwr').textContent = `Throttle TWR ${fixed(message.throttleTwr, 2)}`;
    $('#totalDeltaV').textContent = `${fixed(message.totalDeltaVVacuum, 0)} m/s`;
    $('#stageDeltaV').textContent = `STAGE ${fixed(message.stageDeltaVVacuum, 0)} m/s`;
    const orbitSummary = $('.fp-row-wide .orbit-summary-values');
    if (orbitSummary && message.currentOrbit) orbitSummary.title = message.currentOrbit;
    renderOrbitSummaries(state.regularSample);
    $('#flightPanelState').textContent = message.vesselId ? 'Data ready' : 'Waiting for active vessel';
    $('#flightPanelRate').textContent = 'FAST Live · REGULAR live · MECHJEB live';
    const summary = $$('.fp-summary > div');
    const summaryValues = [
      [message.verticalSpeed, 1], [message.surfaceSpeed, 0], [state.currentSample?.data.altitude / 1000, 2],
      [state.regularSample?.apoapsis / 1000, 1], [state.regularSample?.periapsis / 1000, 1],
      [message.totalDeltaVVacuum, 0], [message.dynamicPressureKpa, 1], [message.vesselMass, 2]
    ];
    summary.forEach((item, index) => {
      const output = item.querySelector('b');
      const value = summaryValues[index];
      if (output && value && finite(value[0])) output.textContent = (index === 0 && Number(value[0]) >= 0 ? '+' : '') + fixed(value[0], value[1]);
    });
  }

  function renderTargetTree() {
    const query = String($('#targetSearch')?.value || '').trim().toLowerCase();
    const entries = state.targetCatalog;
    const byId = new Map(entries.map(entry => [entry.id, entry]));
    const children = new Map();
    entries.forEach(entry => {
      const parent = entry.parentId || '__root__';
      if (!children.has(parent)) children.set(parent, []);
      children.get(parent).push(entry);
    });
    children.forEach(list => list.sort((a, b) => (a.kind === b.kind ? String(a.name).localeCompare(String(b.name), 'zh-CN') : a.kind === 'body' ? -1 : 1)));
    const visible = new Set();
    if (query) entries.forEach(entry => {
      if (![entry.name, entry.bodyName, entry.kind].some(value => String(value || '').toLowerCase().includes(query))) return;
      let cursor = entry;
      while (cursor && !visible.has(cursor.id)) { visible.add(cursor.id); cursor = byId.get(cursor.parentId); }
    });
    const typeLabels = { body: 'Body', vessel: 'Vessel', dockingPort: 'Docking ports', other: 'Target' };
    const renderBranch = (entry, depth, visiting = new Set()) => {
      if (visiting.has(entry.id) || (query && !visible.has(entry.id))) return '';
      const nextVisiting = new Set(visiting); nextVisiting.add(entry.id);
      const childEntries = children.get(entry.id) || [];
      const expanded = query || state.expandedTargets.has(entry.id);
      const selected = entry.id === state.selectedTargetId;
      const current = entry.name === state.lastAutomation?.targetName && entry.kind === state.lastAutomation?.targetKind;
      const childMarkup = childEntries.map(child => renderBranch(child, depth + 1, nextVisiting)).join('');
      return `<div class="target-branch" style="--depth:${depth}"><button class="target-node${selected ? ' is-selected' : ''}${current ? ' is-current' : ''}" data-target-id="${escapeHtml(entry.id)}"><span class="twisty">${childEntries.length ? (expanded ? '▾' : '▸') : '·'}</span><span class="target-node-copy"><b data-l10n-skip>${escapeHtml(entry.name || entry.id)}</b><small>${escapeHtml(typeLabels[entry.kind] || entry.kind)}${entry.bodyName ? ` · ${escapeHtml(entry.bodyName)}` : ''}</small></span><em>${current ? 'CURRENT' : ''}</em></button>${childEntries.length ? `<div class="target-children"${expanded ? '' : ' hidden'}>${childMarkup}</div>` : ''}</div>`;
    };
    const roots = children.get('__root__') || [];
    $('#targetTree').innerHTML = roots.map(entry => renderBranch(entry, 0)).join('') || '<p class=\"target-empty\">No matching targets</p>';
    $$('.target-node').forEach(button => button.addEventListener('click', () => {
      const id = button.dataset.targetId;
      const hasChildren = (children.get(id) || []).length > 0;
      if (hasChildren) state.expandedTargets.has(id) ? state.expandedTargets.delete(id) : state.expandedTargets.add(id);
      state.selectedTargetId = id;
      renderTargetTree();
      renderSelectedTarget();
    }));
  }

  function renderSelectedTarget() {
    const entry = state.targetCatalog.find(item => item.id === state.selectedTargetId);
    const labels = { body: 'Body', vessel: 'Vessel', dockingPort: 'Docking ports', other: 'Target' };
    userText($('#targetSelectedName'), entry?.name, 'Select a target');
    $('#targetSelectedType').textContent = entry ? labels[entry.kind] || entry.kind : 'Body / vessel / docking port';
    userText($('#targetParentBody'), entry?.bodyName);
    $('#targetKindBadge').textContent = entry ? String(entry.kind).toUpperCase() : 'NO TARGET';
    $('#applyTarget').disabled = !entry;
  }

  function renderTargetCatalog(message) {
    state.lastAutomation = message;
    const catalog = Array.isArray(message.targetCatalog) ? message.targetCatalog : [];
    const signature = catalog.map(entry => `${entry.id}:${entry.parentId || ''}:${entry.name}`).join('|');
    if (signature !== state.targetCatalogSignature) {
      state.targetCatalogSignature = signature;
      state.targetCatalog = catalog;
      const roots = catalog.filter(entry => !entry.parentId);
      roots.forEach(entry => state.expandedTargets.add(entry.id));
      if (!catalog.some(entry => entry.id === state.selectedTargetId)) {
        state.selectedTargetId = catalog.find(entry => entry.name === message.targetName && entry.kind === message.targetKind)?.id || null;
      }
      renderTargetTree();
      renderSelectedTarget();
    }
    $('#targetCatalogCount').textContent = `${catalog.length} TARGETS`;
    userText($('#targetCurrentName'), message.targetName, 'Not selected');
    const meters = value => Number(value) > 0 ? `${Number(value).toLocaleString(uiLocale(), { maximumFractionDigits: 1 })} m` : '—';
    $('#targetDistance').textContent = meters(message.targetDistance);
    $('#targetRelativeSpeed').textContent = Number(message.targetRelativeSpeed) >= 0 && message.targetName ? `${Number(message.targetRelativeSpeed).toFixed(2)} m/s` : '—';
    $('#targetApoapsis').textContent = Number(message.targetOrbitApoapsis) ? `${(Number(message.targetOrbitApoapsis) / 1000).toFixed(1)} km` : '—';
    $('#targetPeriapsis').textContent = Number(message.targetOrbitPeriapsis) ? `${(Number(message.targetOrbitPeriapsis) / 1000).toFixed(1)} km` : '—';
    $('#targetInclination').textContent = message.targetName ? `${Number(message.targetOrbitInclination || 0).toFixed(3)}°` : '—';
    $('#targetPeriod').textContent = Number(message.targetOrbitPeriod) > 0 ? formatDuration(message.targetOrbitPeriod) : '—';
  }

  function renderAutomation(message) {
    renderTargetCatalog(message);
    syncThrottleLock(message);
    renderFlightEnvelope(message);
    renderTrajectoryPrediction(message.trajectory || {});
    const target = message.targetName || 'No target selected';
    const targetChip = $('.planner-status .mode-chip');
    if (targetChip) targetChip.innerHTML = `<i></i> Target:${escapeHtml(target)}`;
    const nodeCount = $('.planner-status > b');
    const maneuverNodeCount = Number(message.maneuverNodeCount || 0);
    if (nodeCount) nodeCount.textContent = `${maneuverNodeCount} NODES`;
    const queueCount = $('#nodeQueueCount');
    if (queueCount) queueCount.textContent = `${maneuverNodeCount} QUEUED`;
    const emptyNodeQueue = $('#emptyNodeQueue');
    if (emptyNodeQueue) emptyNodeQueue.hidden = maneuverNodeCount > 0;
    const nodeQueueEntries = $('#nodeQueueEntries');
    if (nodeQueueEntries) {
      const nodes = Array.isArray(message.maneuverNodes) ? message.maneuverNodes : [];
      nodeQueueEntries.innerHTML = nodes.map((node, index) => {
        const countdown = Math.max(0, Number(node.ut || 0) - Number(state.regularSample?.ut || 0));
        return `<div class="node-entry"><b>${String(index + 1).padStart(2, '0')}</b><p><span>${index === 0 ? 'Next KSP maneuver node' : 'KSP Maneuver node'}</span><small>UT ${Number(node.ut || 0).toFixed(1)} · Δv ${Number(node.deltaV || 0).toFixed(1)} m/s</small></p><em>T− ${formatDuration(countdown)}</em></div>`;
      }).join('');
    }
    if (maneuverNodeCount > 0) {
      const countdown = Math.max(0, Number(message.nextNodeUt || 0) - Number(state.regularSample?.ut || 0));
      $('#nodeDv').textContent = `${Number(message.nextNodeDeltaV || 0).toFixed(1)} m/s`;
      $('#plannedOrbit').textContent = 'KSP Maneuver node created';
      $('#plannedBurn').textContent = message.nodeExecutorEnabled ? 'Executing' : 'Awaiting execution';
      $('#plannedTime').textContent = `T− ${formatDuration(countdown)}`;
      $('#plannerNodeMapLabel').textContent = `NODE 01 · T− ${formatDuration(countdown)}`;
    } else {
      $('#nodeDv').textContent = 'Waiting for solution';
      $('#plannedOrbit').textContent = 'No KSP maneuver nodes';
      $('#plannedBurn').textContent = '—';
      $('#plannedTime').textContent = '—';
      $('#plannerNodeMapLabel').textContent = 'Waiting for KSP node';
    }
    const autopilot = $('.autopilot-state');
    const modes = [];
    if (message.ascentEnabled) modes.push('Ascent guidance');
    if (message.landingEnabled) modes.push(message.landingAtTarget ? 'Targeted landing' : 'Landing guidance');
    if (message.smartAssEnabled && message.smartAssTarget !== 'OFF') modes.push(`Smart A.S.S. / ${message.smartAssTarget || message.smartAssMode || 'ACTIVE'}`);
    const smartTargetLabels = {
      OFF: 'Off', KILLROT: 'Kill rotation', NODE: 'Nodes', SURFACE: 'Surface', PROGRADE: 'Prograde', RETROGRADE: 'Retrograde',
      NORMAL_PLUS: 'Normal +', NORMAL_MINUS: 'Normal −', RADIAL_PLUS: 'Radial out', RADIAL_MINUS: 'Radial in',
      RELATIVE_PLUS: 'Relative velocity +', RELATIVE_MINUS: 'Relative velocity −', TARGET_PLUS: 'Target +', TARGET_MINUS: 'Target −',
      PARALLEL_PLUS: 'Parallel +', PARALLEL_MINUS: 'Parallel −', SURFACE_PROGRADE: 'Surface prograde',
      SURFACE_RETROGRADE: 'Surface retrograde', HORIZONTAL_PLUS: 'Horizontal +', HORIZONTAL_MINUS: 'Horizontal −', VERTICAL_PLUS: 'Up'
    };
    state.orbitGeometry = message.orbitGeometry || null;
    captureAscentSpatialHistory(message.vesselId, state.orbitGeometry);
    renderOrbitGeometry($('#plannerOrbitChart'), state.orbitGeometry,
      [message.orbitGeometry?.current || [], message.orbitGeometry?.planned || []], true);
    renderTrajectoryGroundMap(message.trajectory || {});
    $('#abortNodeExecution').disabled = !message.nodeExecutorEnabled;
    state.smartAssTelemetry = message;
    state.smartAssActualTarget = message.smartAssTarget || 'OFF';
    if (state.smartAssPendingTarget && !state.smartAssPendingCommandId && state.smartAssActualTarget === state.smartAssPendingTarget) {
      state.smartAssDraft = false;
      state.smartAssPendingTarget = null;
      state.smartAssPendingCommandId = null;
    }
    const smartLabel = smartTargetLabels[state.smartAssActualTarget];
    if (!state.smartAssDraft && smartLabel) {
      $$('.attitude-grid button').forEach(button => button.classList.toggle('is-active', sourceText(button).trim() === smartLabel));
      syncSmartAssOffsets(true);
    }
    if (document.activeElement !== $('.smartass-disable input')) $('.smartass-disable input').checked = Boolean(message.smartAssAutoDisable);
    if (message.rendezvousEnabled) modes.push('Rendezvous autopilot');
    if (message.dockingEnabled) modes.push('Docking autopilot');
    const active = modes.length ? modes.map(mode => {
      if (!mode.startsWith('Smart A.S.S. / ')) return `<span>${escapeHtml(mode)}</span>`;
      const target = message.smartAssTarget || message.smartAssMode || 'ACTIVE';
      return `Smart A.S.S. / <span>${escapeHtml(smartTargetLabels[target] || target)}</span>`;
    }).join(' + ') : 'Not engaged';
    if (autopilot) {
      autopilot.querySelector('b').innerHTML = message.mechJebAvailable ? active : 'MechJeb Unavailable';
      autopilot.querySelector('small').textContent = modes.length ? 'MECHJEB CONTROL ACTIVE' : 'MANUAL CONTROL';
      autopilot.classList.toggle('is-active', modes.length > 0);
    }
    const ascentPathLabels = { GRAVITYTURN: 'GRAVITY TURN', PVG: 'PRIMER VECTOR GUIDANCE', CLASSIC: 'CLASSIC ASCENT' };
    const ascentPath = String(message.ascentPath || 'GRAVITYTURN').toUpperCase();
    const ascentAltitudeKm = Number(message.ascentDesiredOrbitAltitude || 0) / 1000;
    const ascentInclination = Number(message.ascentDesiredInclination || 0);
    const ascentTimed = Boolean(message.ascentTimedLaunch);
    const ascentEnabled = Boolean(message.ascentEnabled);
    const launchModeLabels = {
      IMMEDIATE: 'Launch now', COUNTDOWN: 'Manual countdown', TARGET_PLANE: 'Launch into target plane',
      TARGET_LAN: 'Launch to target LAN', RENDEZVOUS: 'Launch to rendezvous', MANUAL_LAN: 'Manual LAN (in-game MJ)'
    };
    const ascentLaunchMode = String(message.ascentLaunchMode || 'IMMEDIATE').toUpperCase();
    $('#ascentStateChip').textContent = ascentEnabled ? (ascentTimed ? 'COUNTDOWN' : 'ACTIVE') : 'STANDBY';
    $('#ascentStatus').textContent = message.mechJebAvailable ? (message.ascentStatus || (ascentEnabled ? 'Ascent guidance engaged' : 'Waiting to start')) : 'MechJeb Unavailable';
    const ascentApoapsisKm = Number(message.ascentPvgDesiredApoapsis || 0) / 1000;
    {
      $('#ascentOrbitBadge').textContent = ascentPath === 'PVG' ? `PE ${ascentAltitudeKm.toFixed(0)} / AP ${ascentApoapsisKm.toFixed(0)} km` : `${ascentAltitudeKm.toFixed(0)} km`;
      $('#ascentReadoutAltitude').textContent = ascentPath === 'PVG' ? `PE ${ascentAltitudeKm.toFixed(1)} · AP ${ascentApoapsisKm.toFixed(1)} km` : `${ascentAltitudeKm.toFixed(1)} km`;
      $('#ascentReadoutInclination').textContent = `${ascentInclination.toFixed(1)}°`;
      $('#ascentReadoutPath').textContent = ascentPathLabels[ascentPath] || ascentPath;
      renderAscentVisuals({
        path: ascentPath, periapsisKm: ascentAltitudeKm,
        apoapsisKm: ascentPath === 'PVG' ? ascentApoapsisKm : ascentAltitudeKm,
        inclination: ascentInclination, enabled: ascentEnabled,
        gtStartAltitudeKm: Number(message.ascentGtTurnStartAltitude || 0) / 1000,
        gtIntermediateAltitudeKm: Number(message.ascentGtIntermediateAltitude || 0) / 1000,
        gtHoldApTime: Number(message.ascentGtHoldApTime || 0),
        classicStartAltitudeKm: Number(message.ascentClassicTurnStartAltitude || 0) / 1000,
        classicEndAltitudeKm: Number(message.ascentClassicTurnEndAltitude || 0) / 1000,
        classicEndAngle: Number(message.ascentClassicTurnEndAngle || 0),
        classicShapeExponent: Number(message.ascentClassicTurnShapeExponent || 1),
        pvgPitchStartVelocity: Number(message.ascentPvgPitchStartVelocity || 0),
        pvgPitchRate: Number(message.ascentPvgPitchRate || 0),
        pvgAttachAltitudeKm: Number(message.ascentPvgDesiredAttachAltitude || 0) / 1000
      });
    }
    $('#ascentTMinus').textContent = ascentTimed ? `T− ${formatDuration(Math.max(0, Number(message.ascentTimeToLaunch || 0)))}` : (ascentEnabled ? 'In flight' : 'Launch now');
    $('#ascentReadoutLaunchMode').textContent = launchModeLabels[ascentLaunchMode] || ascentLaunchMode;
    const launchTargetState = $('#ascentLaunchTargetState');
    launchTargetState.classList.toggle('is-ready', Boolean(message.ascentLaunchTargetAvailable));
    userText(launchTargetState.querySelector('b'), message.ascentLaunchTargetAvailable ? message.ascentLaunchTargetName : null,
      message.ascentLaunchTargetAvailable ? 'Target orbit available' : 'No valid target selected');
    launchTargetState.querySelector('small').textContent = message.ascentLaunchTargetAvailable ? 'Target orbits the launch body · MJ can compute a window' : 'Select a target orbiting the current body on the Target page first.';
    if (message.ascentPath) {
      syncAscentField('#ascentOrbitAltitude', 'value', ascentAltitudeKm.toFixed(1));
      syncAscentField('#ascentInclination', 'value', ascentInclination.toFixed(1));
      syncAscentField('#ascentPath', 'value', ascentPath);
      const launchModeOption = Array.from($('#ascentLaunchMode').options).find(option => option.value === ascentLaunchMode);
      if (launchModeOption) syncAscentField('#ascentLaunchMode', 'value', ascentLaunchMode);
      syncAscentField('#ascentLaunchPhaseAngle', 'value', Number(message.ascentLaunchPhaseAngle || 0).toFixed(1));
      syncAscentField('#ascentLaunchLanDifference', 'value', Number(message.ascentLaunchLanDifference || 0).toFixed(1));
      syncAscentField('#ascentAutoThrottle', 'checked', Boolean(message.ascentAutoThrottle));
      syncAscentField('#ascentCorrectiveSteering', 'checked', Boolean(message.ascentCorrectiveSteering));
      syncAscentField('#ascentAutoStage', 'checked', Boolean(message.ascentAutoStage));
      syncAscentField('#ascentSkipCircularization', 'checked', Boolean(message.ascentSkipCircularization));
      syncAscentField('#ascentLimitAoA', 'checked', Boolean(message.ascentLimitAoA));
      syncAscentField('#ascentMaxAoA', 'value', Number(message.ascentMaxAoA || 0).toFixed(1));
      syncAscentField('#ascentLimitQ', 'checked', Boolean(message.ascentLimitQEnabled));
      syncAscentField('#ascentMaxQ', 'value', (Number(message.ascentLimitQ || 0) / 1000).toFixed(1));
      syncAscentField('#ascentForceRoll', 'checked', Boolean(message.ascentForceRoll));
      syncAscentField('#ascentVerticalRoll', 'value', Number(message.ascentVerticalRoll || 0).toFixed(1));
      syncAscentField('#ascentTurnRoll', 'value', Number(message.ascentTurnRoll || 0).toFixed(1));
      syncAscentField('#ascentDesiredLan', 'value', Number(message.ascentDesiredLan || 0).toFixed(1));
      syncAscentField('#ascentCorrectiveGain', 'value', Number(message.ascentCorrectiveSteeringGain || 0).toFixed(2));
      syncAscentField('#ascentDeploySolar', 'checked', Boolean(message.ascentDeploySolarPanels));
      syncAscentField('#ascentDeployAntennas', 'checked', Boolean(message.ascentDeployAntennas));
      syncAscentField('#ascentRollAltitude', 'value', (Number(message.ascentRollAltitude || 0) / 1000).toFixed(2));
      syncAscentField('#ascentAoAFadePressure', 'value', (Number(message.ascentAoAFadeoutPressure || 0) / 1000).toFixed(2));
      syncAscentField('#gtStartAltitude', 'value', (Number(message.ascentGtTurnStartAltitude || 0) / 1000).toFixed(2));
      syncAscentField('#gtStartVelocity', 'value', Number(message.ascentGtTurnStartVelocity || 0).toFixed(1));
      syncAscentField('#gtStartPitch', 'value', Number(message.ascentGtTurnStartPitch || 0).toFixed(1));
      syncAscentField('#gtIntermediateAltitude', 'value', (Number(message.ascentGtIntermediateAltitude || 0) / 1000).toFixed(1));
      syncAscentField('#gtHoldApTime', 'value', Number(message.ascentGtHoldApTime || 0).toFixed(1));
      syncAscentField('#classicStartAltitude', 'value', (Number(message.ascentClassicTurnStartAltitude || 0) / 1000).toFixed(2));
      syncAscentField('#classicStartVelocity', 'value', Number(message.ascentClassicTurnStartVelocity || 0).toFixed(1));
      syncAscentField('#classicEndAltitude', 'value', (Number(message.ascentClassicTurnEndAltitude || 0) / 1000).toFixed(1));
      syncAscentField('#classicEndAngle', 'value', Number(message.ascentClassicTurnEndAngle || 0).toFixed(1));
      syncAscentField('#classicShapeExponent', 'value', Number(message.ascentClassicTurnShapeExponent || 0).toFixed(2));
      syncAscentField('#classicAutoPath', 'checked', Boolean(message.ascentClassicAutoPath));
      syncAscentField('#pvgPitchStartVelocity', 'value', Number(message.ascentPvgPitchStartVelocity || 0).toFixed(1));
      syncAscentField('#pvgPitchRate', 'value', Number(message.ascentPvgPitchRate || 0).toFixed(2));
      syncAscentField('#pvgDesiredApoapsis', 'value', (Number(message.ascentPvgDesiredApoapsis || 0) / 1000).toFixed(1));
      syncAscentField('#pvgAttachAltitudeEnabled', 'checked', Boolean(message.ascentPvgAttachAltitudeEnabled));
      syncAscentField('#pvgAttachAltitude', 'value', (Number(message.ascentPvgDesiredAttachAltitude || 0) / 1000).toFixed(2));
      syncAscentField('#pvgDynamicPressureTrigger', 'value', (Number(message.ascentPvgDynamicPressureTrigger || 0) / 1000).toFixed(2));
      syncAscentField('#pvgStagingTriggerEnabled', 'checked', Boolean(message.ascentPvgStagingTriggerEnabled));
      syncAscentField('#pvgStagingTrigger', 'value', Number(message.ascentPvgStagingTrigger || 0).toFixed(0));
      syncAscentField('#pvgFixedCoast', 'checked', Boolean(message.ascentPvgFixedCoast));
      syncAscentField('#pvgFixedCoastLength', 'value', Number(message.ascentPvgFixedCoastLength || 0).toFixed(1));
      updateAscentPathPanel();
      updateAscentLaunchMode();
    }
    [$('#autoWarpPlanner'), $('#autoWarpAutopilot')].forEach(input => {
      if (input && document.activeElement !== input) input.checked = Boolean(message.autoWarp);
    });
    userText($('#rendezvousTargetName'), message.targetName, 'Not selected');
    $('#rendezvousTargetDistance').textContent = Number(message.targetDistance) > 0 ? `${Number(message.targetDistance).toFixed(1)} m` : '—';
    $('#rendezvousRelativeSpeed').textContent = message.targetName ? `${Number(message.targetRelativeSpeed || 0).toFixed(2)} m/s` : '—';
    $('#rendezvousReadiness').textContent = message.targetName ? (message.rendezvousEnabled ? 'ACTIVE' : 'READY') : 'NO TARGET';
    $('#rendezvousStatus').textContent = message.rendezvousStatus || (message.rendezvousEnabled ? 'Rendezvous autopilot running' : 'Waiting to start');
    if (document.activeElement !== $('#rendezvousDistance')) $('#rendezvousDistance').value = Number(message.rendezvousDesiredDistance || 0).toFixed(1);
    if (document.activeElement !== $('#rendezvousPhasingOrbits')) $('#rendezvousPhasingOrbits').value = Number(message.rendezvousMaxPhasingOrbits || 0).toFixed(1);
    if (document.activeElement !== $('#rendezvousClosingSpeed')) $('#rendezvousClosingSpeed').value = Number(message.rendezvousMaxClosingSpeed || 0).toFixed(2);
    const landingStatus = $('[data-auto-view="landing"] .guidance-status');
    if (landingStatus) {
      landingStatus.querySelector('b').textContent = message.landingStatus || (message.landingEnabled ? 'Landing autopilot engaged' : 'Waiting to start');
      landingStatus.querySelector('small').textContent = message.landingPredictionSimulationRunning
        ? 'MJ Computing landing prediction' : (message.landingPredictionReady ? 'Landing prediction available' : 'Waiting for MJ landing prediction');
    }
    const landingOutcomeLabels = { LANDED: 'Landing predicted', AEROBRAKED: 'Aerocapture predicted', TIMED_OUT: 'Simulation timeout', NO_REENTRY: 'Orbit does not reenter', ERROR: 'Simulation error' };
    const landingOutcome = String(message.landingPredictionOutcome || '').toUpperCase();
    $('#landingPredictionState').textContent = message.landingPredictionSimulationRunning
      ? 'Computing' : (landingOutcomeLabels[landingOutcome] || (message.landingPredictionReady ? 'Prediction available' : 'Waiting'));
    $('#landingAutopilotStep').textContent = message.landingStep || 'N/A';
    const descentMode = message.landingDescentMode || 'N/A';
    $('#landingDescentMode').textContent = message.landingUsesAtmosphere ? `${descentMode}（aerodynamic)` : descentMode;
    $('#landingTimeToLand').textContent = message.landingPredictionReady ? formatDuration(Number(message.landingPredictionTimeToLand || 0)) : '—';
    $('#landingPredictedCoordinates').textContent = message.landingPredictionReady
      ? `${Number(message.landingPredictedLatitude || 0).toFixed(4)}°, ${Number(message.landingPredictedLongitude || 0).toFixed(4)}°` : '—';
    $('#landingPredictedAltitude').textContent = message.landingPredictionReady ? `${Number(message.landingPredictedAltitude || 0).toFixed(1)} m` : '—';
    $('#landingTargetDifference').textContent = message.landingPredictionReady && message.positionTargetExists
      ? `${Number(message.landingPredictionTargetDistance || 0).toFixed(1)} m` : '—';
    $('#landingMaxDrag').textContent = message.landingPredictionReady ? `${Number(message.landingPredictionMaxDrag || 0).toFixed(2)} g` : '—';
    $('#landingDeltaVNeeded').textContent = message.landingPredictionReady ? `${Number(message.landingPredictionDeltaV || 0).toFixed(1)} m/s` : '—';
    $('#landingAerobrakeOrbit').textContent = message.landingPredictionAerobrake
      ? `PE ${(Number(message.landingAerobrakePeriapsis || 0) / 1000).toFixed(1)} / AP ${(Number(message.landingAerobrakeApoapsis || 0) / 1000).toFixed(1)} km · e ${Number(message.landingAerobrakeEccentricity || 0).toFixed(3)}`
      : '—';
    const syncLandingNumber = (selector, value, digits) => {
      const input = $(selector);
      if (input && document.activeElement !== input && Number.isFinite(Number(value))) input.value = Number(value).toFixed(digits);
    };
    const syncLandingCheck = (selector, value) => {
      const input = $(selector);
      if (input && document.activeElement !== input) input.checked = Boolean(value);
    };
    syncLandingNumber('#touchdownSpeed', message.landingTouchdownSpeed, 2);
    syncLandingNumber('#landingGearStageLimit', message.landingGearStageLimit, 0);
    syncLandingNumber('#landingChuteStageLimit', message.landingChuteStageLimit, 0);
    syncLandingCheck('#landingDeployGears', message.landingDeployGears);
    syncLandingCheck('#landingDeployChutes', message.landingDeployChutes);
    syncLandingCheck('#landingRcsAdjustment', message.landingRcsAdjustment);
    syncLandingCheck('#landingPredictionsEnabled', message.landingPredictionsEnabled);
    syncLandingCheck('#landingAerobrakeNodes', message.landingMakeAerobrakeNodes);
    syncLandingCheck('#landingShowTrajectory', message.landingShowTrajectory);
    syncLandingCheck('#landingWorldTrajectory', message.landingWorldTrajectory);
    syncLandingCheck('#landingCameraTrajectory', message.landingCameraTrajectory);
    updateLandingOptionAvailability();
    $('#landingTargetBody').textContent = message.targetBody || 'Not set';
    userText($('#landingTargetName'), message.positionTargetExists ? message.targetName : null,
      message.positionTargetExists ? 'Surface coordinate target' : 'No surface target');
    $('#landingTargetCoordinates').textContent = message.positionTargetExists
      ? `${Number(message.targetLatitude || 0).toFixed(4)}°, ${Number(message.targetLongitude || 0).toFixed(4)}°`
      : 'Enter coordinates and apply';
    if (message.positionTargetExists && document.activeElement !== $('#landingLatitude')) $('#landingLatitude').value = `${Number(message.targetLatitude || 0).toFixed(4)}°`;
    if (message.positionTargetExists && document.activeElement !== $('#landingLongitude')) $('#landingLongitude').value = `${Number(message.targetLongitude || 0).toFixed(4)}°`;
    const dockingStatus = $('[data-auto-view="docking"] .guidance-status');
    if (dockingStatus) {
      dockingStatus.querySelector('b').textContent = message.dockingStatus || (message.mechJebAvailable ? 'Waiting for target docking port' : 'MechJeb Unavailable');
      dockingStatus.querySelector('small').textContent = message.dockingStep ? `Step:${message.dockingStep}` : 'Valid target and control ports required';
    }
    const dockingAxisAvailable = Boolean(message.dockingAxisAvailable);
    const dockCraftMarker = $('#dockCraftMarker');
    if (dockingAxisAvailable) {
      const axisX = Number(message.dockingAxisErrorX || 0);
      const axisY = Number(message.dockingAxisErrorY || 0);
      const axial = Number(message.dockingAxialSeparation || 0);
      const lateral = Number(message.dockingLateralSeparation || 0);
      const configuredRange = Math.max(
        message.dockingOverrideSafeDistance ? Number(message.dockingSafeDistance || 0) : 0,
        message.dockingOverrideStartDistance ? Number(message.dockingStartDistance || 0) : 0
      );
      const displayRange = Math.max(1, configuredRange, lateral * 1.15);
      dockCraftMarker.hidden = false;
      dockCraftMarker.style.left = `${50 + Math.max(-1, Math.min(1, axisX / displayRange)) * 40}%`;
      dockCraftMarker.style.top = `${50 - Math.max(-1, Math.min(1, axisY / displayRange)) * 40}%`;
      const signedMeters = value => `${value >= 0 ? '+' : ''}${value.toFixed(2)} m`;
      $('#dockErrorX').textContent = signedMeters(axisX);
      $('#dockErrorY').textContent = signedMeters(axisY);
      $('#dockAxialDistance').textContent = signedMeters(axial);
      $('#dockLateralDistance').textContent = `${lateral.toFixed(2)} m`;
      $('#dockReticleError').textContent = `${lateral.toFixed(2)} m Lateral · viewport ±${displayRange.toFixed(1)} m`;
    } else {
      dockCraftMarker.hidden = true;
      ['#dockErrorX', '#dockErrorY', '#dockAxialDistance', '#dockLateralDistance'].forEach(selector => { $(selector).textContent = '—'; });
      $('#dockReticleError').textContent = message.targetName ? 'Current target is not a valid docking port' : 'Waiting for a valid docking port target';
    }
    userText($('#dockingTargetName'), message.targetName, 'Not selected');
    $('#dockingReadiness').textContent = dockingAxisAvailable ? (message.dockingEnabled ? 'ACTIVE' : 'READY') : 'NO DOCKING PORT';
    const syncDockingNumber = (selector, value, digits) => {
      const input = $(selector);
      if (input && document.activeElement !== input && Number.isFinite(Number(value))) input.value = Number(value).toFixed(digits);
    };
    syncDockingNumber('#dockingSpeedLimit', message.dockingSpeedLimit, 2);
    syncDockingNumber('#dockingSafeDistance', message.dockingSafeDistance, 2);
    syncDockingNumber('#dockingStartDistance', message.dockingStartDistance, 2);
    syncDockingNumber('#dockingRoll', message.dockingRoll, 1);
    const syncDockingCheck = (selector, value) => {
      const input = $(selector);
      if (input && document.activeElement !== input) input.checked = Boolean(value);
    };
    syncDockingCheck('#dockingOverrideSafeDistance', message.dockingOverrideSafeDistance);
    syncDockingCheck('#dockingOverrideStartDistance', message.dockingOverrideStartDistance);
    syncDockingCheck('#dockingForceRoll', message.dockingForceRoll);
    syncDockingCheck('#dockingDrawBoundingBox', message.dockingDrawBoundingBox);
    updateDockingOptionAvailability();
    const sasHeader = $('#sasHeaderState');
    if (sasHeader) {
      sasHeader.textContent = message.sas ? 'SAS ON' : 'SAS OFF';
      sasHeader.classList.toggle('is-on', Boolean(message.sas));
    }
    const systemStates = [
      [message.sas, message.sas ? 'Stability assist' : 'Off'], [message.rcs, message.rcs ? 'Enable' : 'Off'],
      [message.gear, message.gear ? 'Deployed' : 'Retracted'], [message.brakes, message.brakes ? 'Locked' : 'Release'],
      [message.lights, message.lights ? 'On' : 'Off'], [message.solarPanels === 'extended', deploymentLabel(message.solarPanels)],
      [message.antennas === 'extended', deploymentLabel(message.antennas)], [message.autoStage, message.autoStage ? 'Managed by MechJeb' : 'Off']
    ];
    $$('.system-switch').forEach((button, index) => {
      const value = systemStates[index];
      if (!value) return;
      button.classList.toggle('is-on', Boolean(value[0]));
      button.querySelector('small').textContent = value[1];
    });
    $$('.action-pad button').forEach((button, index) => button.classList.toggle('is-on', (Number(message.actionGroupMask || 0) & (1 << index)) !== 0));
    const operationStates = {
      autostage: [Boolean(message.autoStage), message.autoStage ? 'Managed by MechJeb · Enable' : 'Off'],
      solar: [message.solarPanels === 'extended', deploymentLabel(message.solarPanels)],
      antenna: [message.antennas === 'extended', deploymentLabel(message.antennas)]
    };
    Object.entries(operationStates).forEach(([name, value]) => {
      const row = $(`[data-operation-state="${name}"]`);
      if (!row) return;
      row.querySelector('.operation-light')?.classList.toggle('is-on', value[0]);
      const detail = row.querySelector('small');
      if (detail) detail.textContent = value[1];
    });
    const solver = $('.solver-state');
    if (solver) {
      const status = message.porkchopStatus || 'idle';
      const labels = { idle: 'Waiting for solution', computing: 'Solving', ready: 'Solution ready', failed: 'Solver failed' };
      solver.querySelector('b').textContent = labels[status] || status;
      solver.querySelector('small').textContent = status === 'computing' ? `${Number(message.porkchopProgress || 0)}%` : (status === 'ready' ? 'PORKCHOP MATRIX READY' : 'ALLGRAPH TRANSFER CALCULATOR');
      if (status === 'ready' && Number(message.porkchopRevision) > state.porkchopRevision) fetchPorkchopResult(Number(message.porkchopRevision));
    }
  }

  function renderFlightEnvelope(message) {
    const available = Boolean(message.mechJebAvailable && message.envelopeAvailable);
    $('#envelopeState').textContent = available ? 'Thrust controller online' : 'MJ thrust controllerUnavailable';
    $('#envelopeLimiterState').textContent = available ? 'Limits are shared with in-game MJ' : 'Requires an active vessel and MechJeb';
    $('.envelope-state')?.classList.toggle('is-active', available);
    const activeLimits = [];
    if (message.envelopeDynamicPressure) activeLimits.push('Maximum dynamic pressure');
    if (message.envelopeAcceleration) activeLimits.push('Maximum acceleration');
    if (message.envelopeMaximumThrottle) activeLimits.push('Maximum throttle');
    if (message.envelopeMinimumThrottle) activeLimits.push('Minimum throttle');
    if (message.envelopePreventOverheat) activeLimits.push('Thermal protection');
    if (message.envelopePreventFlameout) activeLimits.push('Flameout prevention');
    $('#envelopePrimaryConstraint').textContent = activeLimits.length ? activeLimits.join(' / ') : 'No active limit';
    const maxQ = Number(message.envelopeMaximumDynamicPressure || 0) / 1000;
    const maxAcceleration = Number(message.envelopeMaximumAcceleration || 0);
    const minThrottle = Number(message.envelopeMinimumThrottleValue || 0) * 100;
    const maxThrottle = Number(message.envelopeMaximumThrottleValue || 0) * 100;
    $('#envelopeQLimitReadout').textContent = message.envelopeDynamicPressure ? `Limit ${maxQ.toFixed(1)} kPa` : 'Limit disabled';
    $('#envelopeAccelerationLimitReadout').textContent = message.envelopeAcceleration ? `Limit ${maxAcceleration.toFixed(1)} m/s²` : 'Limit disabled';
    $('#envelopeThrottleBandReadout').textContent = `Allowed ${message.envelopeMinimumThrottle ? minThrottle.toFixed(0) : '0'}–${message.envelopeMaximumThrottle ? maxThrottle.toFixed(0) : '100'}%`;
    $('#envelopeThrottleFloor').style.left = `${Math.max(0, Math.min(100, message.envelopeMinimumThrottle ? minThrottle : 0))}%`;
    if (!state.envelopeDraft) {
      $('#envelopeLimitQ').checked = Boolean(message.envelopeDynamicPressure);
      $('#envelopeMaximumQ').value = maxQ.toFixed(1);
      $('#envelopeLimitAcceleration').checked = Boolean(message.envelopeAcceleration);
      $('#envelopeMaximumAcceleration').value = maxAcceleration.toFixed(1);
      $('#envelopeLimitMaximumThrottle').checked = Boolean(message.envelopeMaximumThrottle);
      $('#envelopeMaximumThrottle').value = maxThrottle.toFixed(0);
      $('#envelopeLimitMinimumThrottle').checked = Boolean(message.envelopeMinimumThrottle);
      $('#envelopeMinimumThrottle').value = minThrottle.toFixed(0);
      $('#envelopePreventOverheat').checked = Boolean(message.envelopePreventOverheat);
      $('#envelopeTerminalVelocity').checked = Boolean(message.envelopeTerminalVelocity);
      $('#envelopePreventFlameout').checked = Boolean(message.envelopePreventFlameout);
      $('#envelopeFlameoutMargin').value = Number(message.envelopeFlameoutSafetyPercent || 0).toFixed(1);
      $('#envelopePreventUnstableIgnition').checked = Boolean(message.envelopePreventUnstableIgnition);
      $('#envelopeAutoRcsUllage').checked = Boolean(message.envelopeAutoRcsUllage);
      $('#envelopeSmoothThrottle').checked = Boolean(message.envelopeSmoothThrottle);
      $('#envelopeSmoothingTime').value = Number(message.envelopeThrottleSmoothingTime || 0).toFixed(2);
      $('#envelopeManageIntakes').checked = Boolean(message.envelopeManageIntakes);
      $('#envelopeDifferentialThrottle').checked = Boolean(message.envelopeDifferentialThrottle);
      $('#envelopeAutoStage').checked = Boolean(message.autoStage);
      updateEnvelopeAccelerationEquivalent();
    }
  }

  function trajectoryMapPoint(latitude, longitude, origin, radius) {
    const radians = Math.PI/180, lat=latitude*radians, lat0=origin[0]*radians;
    const lon = ((longitude-origin[1]+540)%360-180)*radians;
    const cosine = Math.max(-1,Math.min(1,Math.sin(lat0)*Math.sin(lat)+Math.cos(lat0)*Math.cos(lat)*Math.cos(lon)));
    const angle = Math.acos(cosine);
    if(angle > Math.PI-.001) return null;
    const k = angle < 1e-8 ? 1 : angle/Math.sin(angle);
    return [radius*k*Math.cos(lat)*Math.sin(lon),
      radius*k*(Math.cos(lat0)*Math.sin(lat)-Math.sin(lat0)*Math.cos(lat)*Math.cos(lon))];
  }

  function renderTrajectoryGroundMap(t) {
    const svg=$('#trajectoryOrbitChart');
    if (!svg) return;
    if(t.surfaceMapVersion !== 1) {
      svg.innerHTML='<text x=\"30\" y=\"185\" fill=\"#ffcf73\">New DLL required: legacy trajectory coordinates and speed frame are incorrect</text>';
      return;
    }
    const radius=Number(t.bodyRadius), valid=p=>Array.isArray(p)&&p.length>=2&&p.slice(0,2).every(Number.isFinite)&&Math.abs(p[0])<=90;
    const target=t.targetAvailable ? [t.targetLatitude,t.targetLongitude] : null;
    const impact=t.impactAvailable ? [t.impactLatitude,t.impactLongitude] : null;
    const paths=(t.groundPaths || []).map(points=>points.filter(valid));
    const origin=valid(target)?target:valid(impact)?impact:paths.find(p=>p.length)?.[0];
    if(!origin || !(radius>0)) {svg.innerHTML='<text x=\"30\" y=\"185\" fill=\"#8ea8ad\">Waiting for target or impact coordinates</text>';return;}
    const project=p=>trajectoryMapPoint(p[0],p[1],origin,radius);
    const projected=paths.map(points=>points.map(project));
    const targetXY=valid(target)?project(target):null, impactXY=valid(impact)?project(impact):null;
    const bounds=projected.flat().filter(Boolean);
    if(targetXY)bounds.push(targetXY);
    if(impactXY)bounds.push(impactXY);
    if(!bounds.length)return;
    const minX=Math.min(...bounds.map(p=>p[0])),maxX=Math.max(...bounds.map(p=>p[0]));
    const minY=Math.min(...bounds.map(p=>p[1])),maxY=Math.max(...bounds.map(p=>p[1]));
    const scale=Math.min(600/Math.max(200,maxX-minX),245/Math.max(200,maxY-minY))*.88;
    const screen=p=>[380+(p[0]-(minX+maxX)/2)*scale,190-(p[1]-(minY+maxY)/2)*scale];
    let markup='<path d="M720 70V28l-5 10m5-10 5 10" stroke="#d6e6e8" fill="none"/><text x="714" y="20" fill="#d6e6e8">N</text>';
    for(let x=60;x<710;x+=65)markup+=`<path d="M${x} 65V320" stroke="#24424a" stroke-width=".5"/>`;
    for(let y=65;y<=320;y+=51)markup+=`<path d="M60 ${y}H710" stroke="#24424a" stroke-width=".5"/>`;
    projected.forEach(points=>{
      let pen=false;
      const d=points.map(p=>{if(!p){pen=false;return '';} const q=screen(p);const command=pen?'L':'M';pen=true;return `${command}${q[0].toFixed(2)} ${q[1].toFixed(2)}`;}).join(' ');
      if(d)markup+=`<path d="${d}" fill="none" stroke="#67d8d4" stroke-width="2.5"/>`;
    });
    if(targetXY && impactXY) {
      const a=screen(targetXY),b=screen(impactXY);
      markup+=`<path d="M${a[0]} ${a[1]}L${b[0]} ${b[1]}" stroke="#ffcf73" stroke-dasharray="5 5" fill="none"/>`;
    }
    const marker=(p,label,color,isTarget)=>{
      if(!p)return '';
      const [x,y]=screen(p),textY=isTarget?y-15:y+24;
      return `<g stroke="${color}" stroke-width="2" fill="none"><circle cx="${x}" cy="${y}" r="${isTarget?9:5}"/>${isTarget?`<path d="M${x-14} ${y}h28m-14-14v28"/>`:''}</g><text x="${x}" y="${textY}" text-anchor="middle" fill="${color}">${label}</text>`;
    };
    markup+=marker(targetXY,'Target','#ffcf73',true)+marker(impactXY,'Predicted impact','#ff7769',false);
    const raw=110/scale, power=10**Math.floor(Math.log10(raw)), unit=[5,2,1].find(n=>n*power<=raw)*power;
    const width=unit*scale, distance=unit>=1000?`${unit/1000} km`:`${unit} m`;
    markup+=`<path d="M25 342h${width}m-${width}-4v8m${width}-8v8" stroke="#d6e6e8"/><text x="25" y="365" fill="#d6e6e8">${distance}</text>`;
    if(!projected.some(p=>p.filter(Boolean).length>1))markup+=`<text x="230" y="365" fill="#ffcf73">${t.impactAvailable?'Impact available; trajectory samples not yet received':'Waiting for impact and trajectory'}</text>`;
    svg.innerHTML=markup;
  }

  function renderTrajectoryPrediction(trajectory) {
    $('#trajectoryPathStatus').textContent = trajectory.surfaceMapVersion !== 1 ? 'Load the new ArmorControl DLL. Legacy impact coordinates and speeds are hidden because their reference frame is incorrect.'
      : trajectory.pathStatus || ((trajectory.groundPaths || []).some(p=>p.length>1) ? 'Predicted ground track received' : trajectory.impactAvailable ? 'Impact received; no trajectory samples yet.' : 'Waiting for Trajectories. Computation may pause outside map view if Always Update is off.');
    // Older DLLs used inertial speeds and body-relative positions as world coordinates.
    if (trajectory.surfaceMapVersion !== 1) trajectory = {...trajectory, impactAvailable: false};
    const available = Boolean(trajectory.available);
    $('#trajectoryState').textContent = available ? `Trajectories ${trajectory.version || ''}`.trim() : 'Trajectories Unavailable';
    $('#trajectoryModel').textContent = available ? `Aerodynamic model ${trajectory.aerodynamicModel || 'Waiting for initialization'}` : (trajectory.status || 'Plugin not loaded');
    $('.trajectory-state')?.classList.toggle('is-active', available);
    $('#trajectoryImpactStatus').textContent = trajectory.impactAvailable ? 'SOLUTION READY' : 'NO SOLUTION';
    $('#trajectoryTimeToImpact').textContent = trajectory.impactAvailable ? formatDuration(trajectory.timeToImpact) : '—';
    $('#trajectoryImpactCoordinates').textContent = trajectory.impactAvailable
      ? `${Number(trajectory.impactLatitude).toFixed(5)}°, ${Number(trajectory.impactLongitude).toFixed(5)}°` : '—';
    $('#trajectoryImpactSpeed').textContent = trajectory.impactAvailable ? `${Number(trajectory.impactSpeed).toFixed(1)} m/s` : '—';
    $('#trajectoryImpactComponents').textContent = trajectory.impactAvailable
      ? `${Number(trajectory.impactVerticalSpeed).toFixed(1)} / ${Number(trajectory.impactHorizontalSpeed).toFixed(1)} m/s` : '—';
    $('#trajectoryMaximumG').textContent = trajectory.impactAvailable ? `${Number(trajectory.maximumDecelerationG || 0).toFixed(2)} g` : '—';
    $('#trajectoryComputationTime').textContent = available ? `${Number(trajectory.computationTimeMs || 0).toFixed(2)} ms` : '—';
    $('#trajectoryErrors').textContent = available ? String(Number(trajectory.errorCount || 0)) : '—';
    $('#trajectoryTargetReadout').textContent = trajectory.targetAvailable
      ? `${Number(trajectory.targetLatitude).toFixed(5)}°, ${Number(trajectory.targetLongitude).toFixed(5)}° · ${Number(trajectory.targetAltitude).toFixed(0)} m`
      : 'No target set';
    $('#trajectoryMapTarget').textContent = trajectory.targetAvailable ? `${Number(trajectory.targetLatitude).toFixed(5)}°, ${Number(trajectory.targetLongitude).toFixed(5)}°` : 'No target set';
    $('#trajectoryMissDistance').textContent = trajectory.impactAvailable && trajectory.targetAvailable && Number.isFinite(trajectory.impactTargetDistance) ? `${(trajectory.impactTargetDistance/1000).toFixed(2)} km` : '—';
    // Coordinates remain in the readouts; never place target/impact at a decorative fixed position.
    $('#trajectoryTargetMarker').hidden = true;
    $('#trajectoryImpactMarker').hidden = true;
    if (!state.trajectorySettingsDraft) {
      $('#trajectoryDisplay').checked = Boolean(trajectory.display);
      $('#trajectoryDisplayInFlight').checked = Boolean(trajectory.displayInFlight);
      $('#trajectoryAlwaysUpdate').checked = Boolean(trajectory.alwaysUpdate);
      $('#trajectoryComplete').checked = Boolean(trajectory.complete);
      $('#trajectoryBodyFixed').checked = Boolean(trajectory.bodyFixed);
      $('#trajectoryAutoAero').checked = Boolean(trajectory.autoUpdateAero);
      $('#trajectoryUseCache').checked = Boolean(trajectory.useCache);
      $('#trajectoryDefaultRetrograde').checked = Boolean(trajectory.defaultRetrograde);
      $('#trajectoryIntegrationStep').value = Number(trajectory.integrationStep || 0).toFixed(2);
      $('#trajectoryMaxPatches').value = Number(trajectory.maxPatches || 0).toFixed(0);
      $('#trajectoryMaxFrames').value = Number(trajectory.maxFramesPerPatch || 0).toFixed(0);
    }
    if (!state.trajectoryProfileDraft) {
      $('#trajectoryEntryMode').value = trajectory.entryMode || 'VELOCITY';
      $('#trajectoryEntryGrade').value = trajectory.entryRetrograde ? 'RETROGRADE' : 'PROGRADE';
      $('#trajectoryEntryAngle').value = Number(trajectory.entryAngle || 0).toFixed(1);
      $('#trajectoryHighMode').value = trajectory.highMode || 'VELOCITY';
      $('#trajectoryHighGrade').value = trajectory.highRetrograde ? 'RETROGRADE' : 'PROGRADE';
      $('#trajectoryHighAngle').value = Number(trajectory.highAngle || 0).toFixed(1);
      $('#trajectoryLowMode').value = trajectory.lowMode || 'VELOCITY';
      $('#trajectoryLowGrade').value = trajectory.lowRetrograde ? 'RETROGRADE' : 'PROGRADE';
      $('#trajectoryLowAngle').value = Number(trajectory.lowAngle || 0).toFixed(1);
      $('#trajectoryFinalMode').value = trajectory.finalMode || 'VELOCITY';
      $('#trajectoryFinalGrade').value = trajectory.finalRetrograde ? 'RETROGRADE' : 'PROGRADE';
      $('#trajectoryFinalAngle').value = Number(trajectory.finalAngle || 0).toFixed(1);
      syncTrajectoryProfileInputs();
    }
    if (trajectory.targetAvailable && !state.trajectoryTargetDraft) {
      $('#trajectoryTargetLatitude').value = Number(trajectory.targetLatitude).toFixed(5);
      $('#trajectoryTargetLongitude').value = Number(trajectory.targetLongitude).toFixed(5);
      $('#trajectoryTargetAltitude').value = Number(trajectory.targetAltitude).toFixed(0);
    }
  }

  function deploymentLabel(value) {
    return ({ extended: 'Extended', retracted: 'Retracted', moving: 'Moving', mixed: 'Mixed state', unavailable: 'Unavailable' })[value] || 'Unavailable';
  }

  function updateEnvelopeAccelerationEquivalent() {
    const acceleration = Number($('#envelopeMaximumAcceleration')?.value || 0);
    $('#envelopeAccelerationG').textContent = Number.isFinite(acceleration) ? `About ${(acceleration / 9.80665).toFixed(2)} g` : 'Enter a valid number';
  }

  function syncTrajectoryProfileInputs() {
    ['Entry', 'High', 'Low', 'Final'].forEach(suffix => {
      const angle = $(`#trajectory${suffix}Angle`);
      if (angle) angle.disabled = false;
    });
  }

  function updateEnvelopeBar(element, percentage) {
    if (!element) return;
    const amount = Math.max(0, Math.min(100, Number(percentage) || 0));
    element.style.width = `${amount}%`;
    element.classList.toggle('is-warning', amount >= 75 && amount < 93);
    element.classList.toggle('is-critical', amount >= 93);
  }

  async function fetchPorkchopResult(expectedRevision) {
    try {
      const response = await fetch(apiUrl('/api/v1/porkchop'), { cache: 'no-store' });
      if (!response.ok) return;
      const result = await response.json();
      if (result.protocol !== 1 || Number(result.revision) !== expectedRevision || !Array.isArray(result.costs)) return;
      state.porkchopRevision = expectedRevision;
      state.porkchopResult = result;
      renderPorkchopHeatmap(result);
      choosePorkchopCell(Number(result.bestDepartureIndex), Number(result.bestDurationIndex));
    } catch {
      showToast('Porkchop Failed to retrieve matrix');
    }
  }

  const porkchopPalette = [
    [0, [31, 68, 164]], [.12, [46, 118, 218]], [.25, [57, 191, 235]],
    [.39, [70, 224, 190]], [.53, [116, 226, 119]], [.66, [202, 235, 91]],
    [.78, [255, 218, 86]], [.9, [255, 139, 78]], [1, [235, 72, 91]]
  ];

  function percentile(sorted, fraction) {
    if (!sorted.length) return NaN;
    const position = Math.max(0, Math.min(sorted.length - 1, fraction * (sorted.length - 1)));
    const lower = Math.floor(position);
    const mix = position - lower;
    return sorted[lower] + ((sorted[Math.min(sorted.length - 1, lower + 1)] - sorted[lower]) * mix);
  }

  function porkchopColor(t) {
    const value = Math.max(0, Math.min(1, t));
    let upper = 1;
    while (upper < porkchopPalette.length - 1 && value > porkchopPalette[upper][0]) upper++;
    const [lowStop, lowColor] = porkchopPalette[upper - 1];
    const [highStop, highColor] = porkchopPalette[upper];
    const mix = (value - lowStop) / Math.max(.0001, highStop - lowStop);
    return lowColor.map((channel, index) => Math.round(channel + (highColor[index] - channel) * mix));
  }

  function formatKspDate(ut) {
    const dayLength = 21600;
    const yearLength = 426 * dayLength;
    const safeUt = Math.max(0, Number(ut) || 0);
    const year = Math.floor(safeUt / yearLength) + 1;
    const day = Math.floor((safeUt % yearLength) / dayLength) + 1;
    return `Y${year} D${String(day).padStart(3, '0')}`;
  }

  function formatPorkchopCost(cost) {
    return Number.isFinite(cost) ? `${(cost / 1000).toFixed(cost < 10000 ? 2 : 1)} km/s` : '—';
  }

  function isPorkchopCost(value) {
    return value !== null && value !== '' && Number.isFinite(Number(value));
  }

  function renderPorkchopHeatmap(result) {
    const finiteCosts = result.costs.filter(isPorkchopCost).map(Number).sort((a, b) => a - b);
    if (!finiteCosts.length) {
      $('#porkHeatmap').setAttribute('opacity', '0');
      showToast('Porkchop Solver returned no valid trajectory');
      return;
    }

    // MechJeb matrices usually contain a narrow low-energy valley plus a long high-cost
    // tail. An asinh scale preserves absolute ordering while expanding the useful valley.
    const low = finiteCosts[0];
    const high = Math.max(low + 1, percentile(finiteCosts, .96));
    const scale = Math.max(1, percentile(finiteCosts, .35) - low, (high - low) * .025);
    const scaleDenominator = Math.asinh((high - low) / scale);
    const normalize = cost => Math.max(0, Math.min(1, Math.asinh(Math.max(0, cost - low) / scale) / scaleDenominator));
    const denormalize = t => low + scale * Math.sinh(Math.max(0, Math.min(1, t)) * scaleDenominator);

    const canvas = document.createElement('canvas');
    canvas.width = 640;
    canvas.height = 328;
    const context = canvas.getContext('2d');
    const imageData = context.createImageData(canvas.width, canvas.height);
    const normalized = new Float32Array(canvas.width * canvas.height);
    normalized.fill(-1);

    const sample = (pixelX, pixelY) => {
      const sourceX = pixelX / Math.max(1, canvas.width - 1) * (result.width - 1);
      const sourceY = (1 - pixelY / Math.max(1, canvas.height - 1)) * (result.height - 1);
      const x0 = Math.floor(sourceX), x1 = Math.min(result.width - 1, x0 + 1);
      const y0 = Math.floor(sourceY), y1 = Math.min(result.height - 1, y0 + 1);
      const fx = sourceX - x0, fy = sourceY - y0;
      const samples = [
        [result.costs[y0 * result.width + x0], (1 - fx) * (1 - fy)],
        [result.costs[y0 * result.width + x1], fx * (1 - fy)],
        [result.costs[y1 * result.width + x0], (1 - fx) * fy],
        [result.costs[y1 * result.width + x1], fx * fy]
      ];
      let value = 0, weight = 0;
      samples.forEach(([cost, sampleWeight]) => {
        if (isPorkchopCost(cost)) { value += Number(cost) * sampleWeight; weight += sampleWeight; }
      });
      return weight > .5 ? value / weight : NaN;
    };

    for (let y = 0; y < canvas.height; y++) {
      for (let x = 0; x < canvas.width; x++) {
        const index = y * canvas.width + x;
        const cost = sample(x, y);
        const t = Number.isFinite(cost) ? normalize(cost) : -1;
        normalized[index] = t;
        const color = t >= 0 ? porkchopColor(t) : (((x + y) % 12) < 6 ? [91, 29, 42] : [66, 24, 35]);
        const target = index * 4;
        imageData.data[target] = color[0];
        imageData.data[target + 1] = color[1];
        imageData.data[target + 2] = color[2];
        imageData.data[target + 3] = 255;
      }
    }

    // Thin isolines make launch-window ridges readable without obscuring the color field.
    const contourCount = 18;
    for (let y = 1; y < canvas.height; y++) {
      for (let x = 1; x < canvas.width; x++) {
        const index = y * canvas.width + x;
        const t = normalized[index];
        if (t < 0) continue;
        const band = Math.floor(t * contourCount);
        const left = normalized[index - 1];
        const above = normalized[index - canvas.width];
        if ((left >= 0 && Math.floor(left * contourCount) !== band) || (above >= 0 && Math.floor(above * contourCount) !== band)) {
          const target = index * 4;
          imageData.data[target] = Math.round(imageData.data[target] * .58);
          imageData.data[target + 1] = Math.round(imageData.data[target + 1] * .58);
          imageData.data[target + 2] = Math.round(imageData.data[target + 2] * .58);
        }
      }
    }

    context.putImageData(imageData, 0, 0);
    const heatmap = $('#porkHeatmap');
    heatmap.setAttribute('href', canvas.toDataURL('image/png'));
    heatmap.setAttribute('opacity', '1');
    $$('.porkchop-chart .contour').forEach(path => path.style.display = 'none');

    $$('[data-pork-departure-tick]').forEach(tick => {
      const fraction = Number(tick.dataset.porkDepartureTick);
      tick.textContent = formatKspDate(result.minimumDepartureTime + fraction * (result.maximumDepartureTime - result.minimumDepartureTime));
    });
    $$('[data-pork-duration-tick]').forEach(tick => {
      const fraction = Number(tick.dataset.porkDurationTick);
      const seconds = result.minimumTransferTime + fraction * (result.maximumTransferTime - result.minimumTransferTime);
      tick.textContent = `${Math.round(seconds / 21600)} d`;
    });
    $$('[data-pork-dv-tick]').forEach(tick => {
      tick.textContent = formatPorkchopCost(denormalize(Number(tick.dataset.porkDvTick)));
    });

    const minimumX = 82 + Number(result.bestDepartureIndex) / Math.max(1, result.width - 1) * 640;
    const minimumY = 352 - Number(result.bestDurationIndex) / Math.max(1, result.height - 1) * 328;
    $('#porkMinimum').setAttribute('transform', `translate(${minimumX.toFixed(1)} ${minimumY.toFixed(1)})`);
    $('.solver-state small').textContent = `${result.width} × ${result.height} CELLS · ${formatPorkchopCost(low)} MIN`;
  }

  function choosePorkchopCell(departureIndex, durationIndex) {
    const result = state.porkchopResult;
    if (!result) return;
    departureIndex = Math.max(0, Math.min(result.width - 1, departureIndex));
    durationIndex = Math.max(0, Math.min(result.height - 1, durationIndex));
    state.porkchopDepartureIndex = departureIndex;
    state.porkchopDurationIndex = durationIndex;
    const x = 82 + departureIndex / Math.max(1, result.width - 1) * 640;
    const y = 352 - durationIndex / Math.max(1, result.height - 1) * 328;
    selection.setAttribute('transform', `translate(${x.toFixed(1)} ${y.toFixed(1)})`);
    const departure = result.minimumDepartureTime + departureIndex / Math.max(1, result.width - 1) * (result.maximumDepartureTime - result.minimumDepartureTime);
    const duration = result.minimumTransferTime + durationIndex / Math.max(1, result.height - 1) * (result.maximumTransferTime - result.minimumTransferTime);
    const rawCost = result.costs[durationIndex * result.width + departureIndex];
    const cost = isPorkchopCost(rawCost) ? Number(rawCost) : NaN;
    const now = Number(state.regularSample?.ut || 0);
    const transferDuration = seconds => {
      const days = Math.floor(seconds / 21600);
      const remainder = seconds - days * 21600;
      return days > 0 ? `${days} d ${formatDuration(remainder)}` : formatDuration(remainder);
    };
    $('#porkDeparture').textContent = `T− ${transferDuration(Math.max(0, departure - now))}`;
    $('#porkDuration').textContent = transferDuration(duration);
    $('#porkEjection').textContent = 'Generated after exact solving';
    $('#porkCapture').textContent = $('#captureBurn').checked ? 'Included in cost' : 'Not included';
    $('#porkTotal').textContent = Number.isFinite(cost) ? `${cost.toFixed(0)} m/s` : 'No valid solution';
  }

  function sendCommand(name, parameters = {}, quiet = false) {
    const serverSetting = name === 'server.language.set' && state.serverCapabilities.has(name);
    if (!state.socket || state.socket.readyState !== WebSocket.OPEN || (!serverSetting && state.connection !== 'live')) return false;
    const commandId = `${state.clientId || 'client'}-${Date.now()}-${++state.commandSequence}`;
    if (quiet) state.quietCommands.add(commandId);
    state.socket.send(JSON.stringify({ type: 'command', commandId, name, sequence: state.commandSequence, ...parameters }));
    return commandId;
  }

  function updateLanguageControl() {
    const select = $('#languageSelect');
    if (!select) return;
    select.disabled = !state.socket || state.socket.readyState !== WebSocket.OPEN ||
      !state.serverCapabilities.has('server.language.set') || Boolean(state.languagePendingCommandId);
  }

  $('#languageSelect').addEventListener('change', event => {
    const language = event.target.value;
    // Keep showing the confirmed language until the server saves and broadcasts it.
    event.target.value = window.ArmorI18n?.locale || 'en-US';
    if (language !== 'en-US' && language !== 'zh-CN') return;
    state.languagePendingCommandId = sendCommand('server.language.set', { language }) || null;
    updateLanguageControl();
    if (!state.languagePendingCommandId) showToast('Server language change requires a connected, compatible server.');
  });

  async function fetchStructuralData() {
    try {
      const [crewResponse, vesselResponse] = await Promise.all([
        fetch(apiUrl('/api/v1/crew'), { cache: 'no-store' }),
        fetch(apiUrl('/api/v1/vessel'), { cache: 'no-store' })
      ]);
      if (!crewResponse.ok || !vesselResponse.ok) return;
      const [crew, vessel] = await Promise.all([crewResponse.json(), vesselResponse.json()]);
      if (crew.protocol !== 1 || vessel.protocol !== 1) return;
      state.structureRevision = Math.max(state.structureRevision, Number(vessel.revision || 0));
      acceptCrewManifest(crew);
      renderVesselStructure(vessel);
    } catch {
      // The live transport may be an older DLL; retain the last authoritative structure view.
    }
  }

  async function fetchRecorderHistory() {
    const connection = state.recorderConnection;
    try {
      const response = await fetch(apiUrl('/api/v1/recorder'), { cache: 'no-store' });
      if (!response.ok) return;
      const history = await response.json();
      if (connection !== state.recorderConnection) return;
      if (history.protocol !== 1 || !Array.isArray(history.samples)) return;
      if (!acceptRecorderRevision(history.revision)) return;
      // Preserve samples received over WebSocket while the HTTP request was in flight.
      const merged = new Map(history.samples.map(sample => [Number(sample.sequence), sample]));
      state.recorderSamples.forEach(sample => merged.set(Number(sample.sequence), sample));
      state.recorderSamples = [...merged.values()].sort((a, b) => a.sequence - b.sequence).slice(-3000);
      state.recorderDirty = true;
    } catch {
      // Keep the last local graph when an older server does not expose recorder history.
    }
  }

  function acceptRecorderRevision(revision) {
    if (!Number.isFinite(revision)) return true; // Older DLL compatibility.
    if (state.recorderRevision != null && revision < state.recorderRevision) return false;
    if (revision !== state.recorderRevision) {
      state.recorderRevision = revision;
      state.recorderSamples = [];
      state.recorderCursorIndex = null;
      state.recorderRenderedAt = 0;
      state.recorderDirty = true;
    }
    return true;
  }

  function acceptRecorderSample(sample) {
    if (!acceptRecorderRevision(sample.revision) || state.paused) return;
    const last = state.recorderSamples[state.recorderSamples.length - 1];
    if (last && Number(last.sequence) >= Number(sample.sequence)) return;
    state.recorderSamples.push(sample);
    if (state.recorderSamples.length > 3000) state.recorderSamples.splice(0, state.recorderSamples.length - 3000);
    state.recorderDirty = true;
  }

  const recorderSeries = {
    altitudeAsl: { label: 'Altitude ASL', color: '#6bd1cf', axis: 'left', value: raw => raw / 1000, format: value => `${value.toFixed(2)} km` },
    altitudeTrue: { label: 'True altitude', color: '#85aef2', axis: 'left', value: raw => raw / 1000, format: value => `${value.toFixed(2)} km` },
    speedSurface: { label: 'Surface speed', color: '#e8b85f', axis: 'left', value: raw => raw, format: value => `${value.toFixed(0)} m/s` },
    speedOrbital: { label: 'Orbital speed', color: '#a98ff3', axis: 'left', value: raw => raw, format: value => `${value.toFixed(0)} m/s` },
    dynamicPressure: { label: 'Dynamic pressure Q', color: '#ef796f', axis: 'left', value: raw => raw / 1000, format: value => `${value.toFixed(3)} kPa` },
    angleOfAttack: { label: 'Angle of attack', color: '#5fd39d', axis: 'right', value: raw => raw, format: value => `${signed(value)}°` },
    angleOfSideslip: { label: 'Sideslip angle', color: '#f39bce', axis: 'right', value: raw => raw, format: value => `${signed(value)}°` },
    pitch: { label: 'Pitch angle', color: '#88c2ff', axis: 'left', value: raw => raw, format: value => `${signed(value)}°` },
    angleOfDisplacement: { label: 'Displacement angle', color: '#66b8aa', axis: 'left', value: raw => raw, format: value => `${signed(value)}°` },
    gravityLosses: { label: 'Gravity loss', color: '#e8b85f', axis: 'left', value: raw => raw, format: value => `${value.toFixed(1)} m/s` },
    dragLosses: { label: 'Drag loss', color: '#ef796f', axis: 'left', value: raw => raw, format: value => `${value.toFixed(1)} m/s` },
    steeringLosses: { label: 'Steering loss', color: '#b8a5dc', axis: 'left', value: raw => raw, format: value => `${value.toFixed(1)} m/s` },
    deltaVExpended: { label: 'Expended Δv', color: '#7fd0e8', axis: 'left', value: raw => raw, format: value => `${value.toFixed(1)} m/s` },
    mass: { label: 'Mass', color: '#d7d9c8', axis: 'left', value: raw => raw, format: value => `${value.toFixed(2)} t` },
    acceleration: { label: 'G-force', color: '#f5e488', axis: 'right', value: raw => raw, format: value => `${value.toFixed(2)} g` }
  };

  const recorderGroups = {
    trajectory: { kicker: 'TRAJECTORY', title: 'Altitude profile', hint: 'ASL and true altitude share the same scale', fields: ['altitudeAsl', 'altitudeTrue'], left: 'Altitude / km', leftZero: false },
    velocity: { kicker: 'VELOCITY', title: 'Speed profile', hint: 'Compare surface and orbital speed directly', fields: ['speedSurface', 'speedOrbital'], left: 'Speed / m·s⁻¹', leftZero: true },
    aero: { kicker: 'AERODYNAMICS', title: 'Aerodynamic loads', hint: 'Dynamic pressure on left axis; airflow angles on right', fields: ['dynamicPressure', 'angleOfAttack', 'angleOfSideslip'], left: 'Dynamic pressure / kPa', right: 'Angle / °', leftZero: true, rightZero: true },
    attitude: { kicker: 'ATTITUDE', title: 'Attitude and airflow deviation', hint: 'Pitch and displacement angle share an angular scale', fields: ['pitch', 'angleOfDisplacement'], left: 'Angle / °', leftZero: true },
    losses: { kicker: 'DELTA-V LEDGER', title: 'Δv Loss budget', hint: 'Accumulated losses and expended Δv share the same speed scale', fields: ['gravityLosses', 'dragLosses', 'steeringLosses', 'deltaVExpended'], left: 'Speed / m·s⁻¹', leftZero: true },
    vehicle: { kicker: 'VEHICLE STATE', title: 'Mass and acceleration', hint: 'Mass on left axis; G-force on right', fields: ['mass', 'acceleration'], left: 'Mass / t', right: 'G force / g', leftZero: false, rightZero: true }
  };

  const recorderPaths = new Map();
  Object.entries(recorderSeries).forEach(([field, series]) => {
    const path = document.createElementNS('http://www.w3.org/2000/svg', 'path');
    path.classList.add('chart-line');
    path.dataset.recorderField = field;
    path.style.stroke = series.color;
    $('#recorderLineGroup').appendChild(path);
    recorderPaths.set(field, path);
  });

  function niceRecorderDomain(values, includeZero) {
    const finiteValues = values.filter(Number.isFinite);
    if (!finiteValues.length) return { min: 0, max: 1, ticks: [1, .75, .5, .25, 0] };
    let min = Math.min(...finiteValues);
    let max = Math.max(...finiteValues);
    if (includeZero) { min = Math.min(0, min); max = Math.max(0, max); }
    if (Math.abs(max - min) < 1e-9) {
      const padding = Math.max(Math.abs(max) * .1, 1);
      min -= padding;
      max += padding;
    } else {
      const padding = (max - min) * .06;
      min -= padding;
      max += padding;
      if (includeZero && min < 0 && finiteValues.every(value => value >= 0)) min = 0;
    }
    const rawStep = (max - min) / 4;
    const exponent = Math.pow(10, Math.floor(Math.log10(rawStep)));
    const fraction = rawStep / exponent;
    const niceFraction = fraction <= 1 ? 1 : fraction <= 2 ? 2 : fraction <= 5 ? 5 : 10;
    const step = niceFraction * exponent;
    const niceMin = includeZero && min >= 0 ? 0 : Math.floor(min / step) * step;
    const niceMax = Math.ceil(max / step) * step;
    const span = Math.max(step, niceMax - niceMin);
    return { min: niceMin, max: niceMax, ticks: Array.from({ length: 5 }, (_, index) => niceMax - span * index / 4) };
  }

  function recorderTick(value) {
    const magnitude = Math.abs(value);
    if (magnitude >= 1000) return Math.round(value).toLocaleString(uiLocale());
    if (magnitude >= 100) return value.toFixed(0);
    if (magnitude >= 10) return value.toFixed(1);
    if (magnitude >= 1) return value.toFixed(2).replace(/0+$/, '').replace(/\.$/, '');
    return value.toFixed(3).replace(/0+$/, '').replace(/\.$/, '');
  }

  function recorderAxisDomain(samples, fields, axis, includeZero) {
    const values = [];
    fields.filter(field => !state.recorderHiddenFields.has(field) && recorderSeries[field].axis === axis).forEach(field => {
      const series = recorderSeries[field];
      samples.forEach(sample => values.push(series.value(Number(sample[field]) || 0)));
    });
    return niceRecorderDomain(values, includeZero);
  }

  function recorderPath(samples, field, xField, xMin, xSpan, domain) {
    const series = recorderSeries[field];
    const ySpan = Math.max(1e-9, domain.max - domain.min);
    return samples.map((sample, index) => {
      const x = ((Number(sample[xField]) || 0) - xMin) / xSpan * 1200;
      const value = series.value(Number(sample[field]) || 0);
      const y = 440 - (value - domain.min) / ySpan * 410;
      return `${index ? 'L' : 'M'}${x.toFixed(1)} ${y.toFixed(1)}`;
    }).join('');
  }

  function recorderXTick(value, xField) {
    if (xField === 'downRange') {
      const kilometers = value / 1000;
      return `${Math.abs(kilometers) >= 100 ? kilometers.toFixed(0) : kilometers.toFixed(1)} km`;
    }
    return formatDuration(value);
  }

  function renderRecorderGroupUi() {
    const group = recorderGroups[state.recorderGroup];
    $('#recorderGroupKicker').textContent = group.kicker;
    $('#recorderChartTitle').textContent = group.title;
    $('#recorderChartHint').textContent = group.hint;
    $('#recorderLeftLabel').textContent = group.left;
    $('#recorderLeftLabel').hidden = false;
    $('#recorderLeftTicks').hidden = false;
    $('#recorderRightLabel').textContent = group.right || '';
    $('#recorderRightLabel').hidden = !group.right;
    $('#recorderRightTicks').hidden = !group.right;
    $$('#recorderGroups button').forEach(button => button.classList.toggle('is-active', button.dataset.recorderGroup === state.recorderGroup));
    const grid = $('#recorderSeriesGrid');
    grid.innerHTML = group.fields.map(field => {
      const series = recorderSeries[field];
      const on = !state.recorderHiddenFields.has(field);
      return `<button class="series-chip${on ? ' is-on' : ''}" data-recorder-field="${field}" style="--series-color:${series.color}"><i></i><span>${series.label}<small>—</small></span><em>${series.axis === 'right' ? 'Right axis' : 'Left axis'}</em></button>`;
    }).join('');
    $$('.series-chip', grid).forEach(button => button.addEventListener('click', () => {
      const field = button.dataset.recorderField;
      const visible = group.fields.filter(item => !state.recorderHiddenFields.has(item));
      if (!state.recorderHiddenFields.has(field) && visible.length === 1) {
        showToast('Keep at least one channel visible');
        return;
      }
      if (state.recorderHiddenFields.has(field)) state.recorderHiddenFields.delete(field);
      else state.recorderHiddenFields.add(field);
      renderRecorderGroupUi();
      state.recorderDirty = true;
      state.recorderRenderedAt = 0;
    }));
    const visibleCount = group.fields.filter(field => !state.recorderHiddenFields.has(field)).length;
    $('#toggleAllRecorder').textContent = visibleCount > 1 ? 'Primary channel only' : 'Show all in group';
  }

  function updateRecorderSeriesValues(sample) {
    $$('#recorderSeriesGrid [data-recorder-field]').forEach(button => {
      const field = button.dataset.recorderField;
      const series = recorderSeries[field];
      $('small', button).textContent = series.format(series.value(Number(sample[field]) || 0));
      const values = state.recorderSamples.map(item => series.value(Number(item[field]) || 0));
      const minimum = Math.min(...values);
      const maximum = Math.max(...values);
      $('em', button).textContent = `${series.axis === 'right' ? 'Right axis' : 'Left axis'} · ${series.format(minimum)}–${series.format(maximum)}`;
    });
  }

  function renderRecorder(now) {
    if (!state.recorderDirty || now - state.recorderRenderedAt < 100) return;
    state.recorderDirty = false;
    state.recorderRenderedAt = now;
    const samples = state.recorderSamples;
    if (!samples.length) {
      $('#sampleCount').textContent = '0';
      $('#recState').textContent = 'Waiting for samples';
      recorderPaths.forEach(path => path.setAttribute('d', ''));
      $('#stageLineGroup').innerHTML = '';
      $('#recorderCursorGroup').hidden = true;
      ['#recAltitude', '#recDownrange', '#recStage', '#recSampleRate', '#recSnapshotTime'].forEach(selector => { const element = $(selector); if (element) element.textContent = '—'; });
      $('#recorderCursorTime').textContent = 'Waiting for Flight Recorder';
      $$('#recorderSeriesGrid small').forEach(element => { element.textContent = '—'; });
      return;
    }

    $('#recState').textContent = state.paused ? 'Display paused' : 'Recording';
    const group = recorderGroups[state.recorderGroup];
    const activeFields = group.fields.filter(field => !state.recorderHiddenFields.has(field));
    const hasLeftAxis = activeFields.some(field => recorderSeries[field].axis === 'left');
    const hasRightAxis = activeFields.some(field => recorderSeries[field].axis === 'right');
    $('#recorderLeftLabel').hidden = !hasLeftAxis;
    $('#recorderLeftTicks').hidden = !hasLeftAxis;
    $('#recorderRightLabel').hidden = !hasRightAxis;
    $('#recorderRightTicks').hidden = !hasRightAxis;
    const xField = $('.axis-toggle button.is-active')?.dataset.axis === 'range' ? 'downRange' : 'timeSinceMark';
    const xValues = samples.map(sample => Number(sample[xField]) || 0);
    const xMin = Math.min(...xValues);
    const xMax = Math.max(...xValues);
    const xSpan = Math.max(.0001, xMax - xMin);
    const leftDomain = recorderAxisDomain(samples, activeFields, 'left', group.leftZero);
    const rightDomain = recorderAxisDomain(samples, activeFields, 'right', group.rightZero);
    state.recorderDomains = { left: leftDomain, right: rightDomain, xMin, xSpan, xField };

    recorderPaths.forEach((path, field) => {
      const visible = activeFields.includes(field);
      path.style.display = visible ? '' : 'none';
      if (visible) path.setAttribute('d', recorderPath(samples, field, xField, xMin, xSpan, recorderSeries[field].axis === 'right' ? rightDomain : leftDomain));
    });

    $$('#recorderLeftTicks span').forEach((element, index) => { element.textContent = recorderTick(leftDomain.ticks[index]); });
    $$('#recorderRightTicks span').forEach((element, index) => { element.textContent = recorderTick(rightDomain.ticks[index]); });
    $$('#recorderXTicks span').forEach((element, index) => { element.textContent = recorderXTick(xMin + xSpan * index / 5, xField); });

    const stageChanges = [];
    for (let index = 1; index < samples.length; index++) {
      if (samples[index].currentStage !== samples[index - 1].currentStage) {
        const x = ((Number(samples[index][xField]) || 0) - xMin) / xSpan * 1200;
        stageChanges.push(`<path d="M${x.toFixed(1)} 30V440"/><text x="${Math.min(x + 8, 1125).toFixed(1)}" y="24">STAGE ${escapeHtml(samples[index].currentStage)}</text>`);
      }
    }
    $('#stageLineGroup').innerHTML = stageChanges.join('');

    const cursorIndex = state.recorderCursorIndex === null ? samples.length - 1 : Math.min(state.recorderCursorIndex, samples.length - 1);
    const cursorSample = samples[cursorIndex];
    const cursorX = ((Number(cursorSample[xField]) || 0) - xMin) / xSpan * 1200;
    const cursorGroup = $('#recorderCursorGroup');
    cursorGroup.hidden = false;
    $('.cursor-line', cursorGroup).setAttribute('x1', cursorX.toFixed(1));
    $('.cursor-line', cursorGroup).setAttribute('x2', cursorX.toFixed(1));
    $('#recorderCursorPoints').innerHTML = activeFields.map(field => {
      const series = recorderSeries[field];
      const domain = series.axis === 'right' ? rightDomain : leftDomain;
      const y = 440 - (series.value(Number(cursorSample[field]) || 0) - domain.min) / Math.max(1e-9, domain.max - domain.min) * 410;
      return `<circle class="cursor-point" cx="${cursorX.toFixed(1)}" cy="${y.toFixed(1)}" r="5" style="stroke:${series.color}"/>`;
    }).join('');

    const inspecting = state.recorderCursorIndex !== null;
    $('#recorderCursorMode').textContent = inspecting ? 'Historical sample' : 'Latest sample';
    $('#recSnapshotLabel').textContent = inspecting ? 'Selected time' : 'Latest sample';
    $('#recorderCursorTime').textContent = `T+ ${formatDuration(Number(cursorSample.timeSinceMark) || 0)}`;
    $('#recSnapshotTime').textContent = `T+ ${formatDuration(Number(cursorSample.timeSinceMark) || 0)}`;
    $('#recDownrange').textContent = `${(Number(cursorSample.downRange || 0) / 1000).toFixed(1)} km`;
    $('#recStage').textContent = `STAGE ${cursorSample.currentStage}`;
    $('#recAltitude').textContent = `${(Number(cursorSample.altitudeAsl || 0) / 1000).toFixed(2)} km`;
    const elapsed = Number(samples[samples.length - 1].timeSinceMark) - Number(samples[0].timeSinceMark);
    $('#recSampleRate').textContent = elapsed > 0 ? `${((samples.length - 1) / elapsed).toFixed(1)} Hz` : '—';
    $('#sampleCount').textContent = samples.length.toLocaleString(uiLocale());
    updateRecorderSeriesValues(cursorSample);
  }

  function selectRecorderSampleAt(clientX) {
    const samples = state.recorderSamples;
    const svg = $('#flightChart');
    if (!samples.length || !state.recorderDomains || !svg) return;
    const bounds = svg.getBoundingClientRect();
    const ratio = Math.max(0, Math.min(1, (clientX - bounds.left) / Math.max(1, bounds.width)));
    const { xField, xMin, xSpan } = state.recorderDomains;
    const target = xMin + ratio * xSpan;
    let low = 0;
    let high = samples.length - 1;
    while (low < high) {
      const middle = Math.floor((low + high) / 2);
      if ((Number(samples[middle][xField]) || 0) < target) low = middle + 1;
      else high = middle;
    }
    if (low > 0) {
      const previousDistance = Math.abs((Number(samples[low - 1][xField]) || 0) - target);
      const currentDistance = Math.abs((Number(samples[low][xField]) || 0) - target);
      if (previousDistance < currentDistance) low -= 1;
    }
    state.recorderCursorIndex = low;
    state.recorderDirty = true;
    state.recorderRenderedAt = 0;
    renderRecorder(performance.now());
  }

  function resetRecorderCursor() {
    state.recorderCursorIndex = null;
    state.recorderDirty = true;
    state.recorderRenderedAt = 0;
    renderRecorder(performance.now());
  }

  function escapeHtml(value) {
    return String(value ?? '').replace(/[&<>"']/g, character => ({
      '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;'
    })[character]);
  }

  function renderCrewManifest(data) {
    const crew = Array.isArray(data.crew) ? data.crew : [];
    const capacity = Number(data.capacity || 0);
    const counts = { Pilot: 0, Engineer: 0, Scientist: 0 };
    const cabins = new Map();
    crew.forEach(member => {
      counts[member.profession] = (counts[member.profession] || 0) + 1;
      cabins.set(member.location || 'Unknown compartment', (cabins.get(member.location || 'Unknown compartment') || 0) + 1);
    });
    $('#crewTotal').textContent = String(crew.length).padStart(2, '0');
    $('#crewCapacity').textContent = `Capacity ${capacity} crew`;
    $('#crewPilots').textContent = counts.Pilot || 0;
    $('#crewEngineers').textContent = counts.Engineer || 0;
    $('#crewScientists').textContent = counts.Scientist || 0;
    $('#crewOccupied').textContent = `${crew.length} / ${capacity}`;
    userText($('#crewVesselName'), data.vesselName, 'No active vessel');
    $('#crewVesselTotal').textContent = `${crew.length} / ${capacity}`;
    $('#crewCabins').innerHTML = [...cabins.entries()].map(([location, count]) =>
      `<div class="cabin-count"><span ${location === 'Unknown compartment' ? '' : 'data-l10n-skip'}>${escapeHtml(location)}</span><b>${count}</b></div>`
    ).join('') || '<div class=\"cabin-count\"><span>No crew aboard this vessel</span><b>0</b></div>';

    const roles = {
      Pilot: ['Pilot', 'pilot'], Engineer: ['Engineer', 'engineer'], Scientist: ['Scientist', 'scientist']
    };
    const header = '<header class=\"manifest-head\"><span>Kerbal</span><span>Profession</span><span>Seat location</span><span>Status</span><span>Actions</span></header>';
    const rows = crew.map(member => {
      const role = roles[member.profession] || [member.profession || 'Crew', ''];
      const initials = String(member.name || '?').split(/\s+/).map(word => word[0]).join('').slice(0, 2).toUpperCase();
      const evaTitle = member.evaAvailable ? 'Click, then slide to confirm EVA' : (member.evaUnavailableReason || 'EVA currently unavailable');
      return `<article class="crew-row" data-crew-place="active"><div class="crew-identity"><span class="crew-avatar ${role[1]}">${escapeHtml(initials)}</span><p><b data-l10n-skip>${escapeHtml(member.name)}</b><small>Level ${Number(member.level || 0)}</small></p></div><span class="role-badge ${role[1]}">${escapeHtml(role[0])}</span><div class="crew-place"><b ${data.vesselName ? 'data-l10n-skip' : ''}>${escapeHtml(data.vesselName || 'Current vessel')}</b><small ${member.location ? 'data-l10n-skip' : ''}>${escapeHtml(member.location || 'Unknown compartment')}</small></div><span class="crew-status on-duty"><i></i>Aboard</span><div class="crew-eva-control"><button class="crew-eva confirm-trigger" data-crew-name="${escapeHtml(member.name)}" data-part-id="${Number(member.partId)}" title="${escapeHtml(evaTitle)}" aria-expanded="false" ${member.evaAvailable ? '' : 'disabled'}><span>EVA</span></button><div class="slide-confirm" hidden><div><b>Slide to EVA</b><button type="button" data-slide-cancel>Cancel</button></div><input type="range" min="0" max="100" step="1" value="0" aria-label="Slide to execute ${escapeHtml(member.name)} EVA"></div></div></article>`;
    }).join('');
    $('#crewManifest').innerHTML = header + (rows || '<article class=\"crew-row\"><div class=\"crew-identity\"><p><b>No crew aboard this vessel</b><small>Uncrewed vessel</small></p></div></article>');
    $$('.crew-eva-control').forEach(control => {
      const button = control.querySelector('.crew-eva');
      bindSlideConfirmation(button, control.querySelector('.slide-confirm'), () => {
        if (!sendCommand('vessel.crew.eva', { crewName: button.dataset.crewName, partId: Number(button.dataset.partId) }))
        showToast('No live connection; EVA not executed');
      });
    });
  }

  function acceptCrewManifest(data) {
    const crew = Array.isArray(data.crew) ? data.crew : [];
    const normalized = {
      vesselId: data.vesselId || null,
      vesselName: data.vesselName || '',
      capacity: Number(data.capacity ?? data.crewCapacity ?? 0),
      crew: crew.map(member => ({
        name: member.name || '',
        profession: member.profession || '',
        level: Number(member.level || 0),
        location: member.location || '',
        partId: Number(member.partId),
        evaAvailable: Boolean(member.evaAvailable),
        evaUnavailableReason: member.evaUnavailableReason || ''
      }))
    };
    const signature = JSON.stringify(normalized);
    if (signature === state.crewSignature) return;
    state.crewSignature = signature;
    renderCrewManifest(normalized);
  }

  let activeSlideReset = null;
  function bindSlideConfirmation(trigger, panel, action) {
    if (!trigger || !panel) return;
    const slider = panel.querySelector('input[type="range"]');
    const output = panel.querySelector('output');
    let fired = false;
    const setProgress = value => {
      const percent = Math.max(0, Math.min(100, Number(value) || 0));
      slider.value = String(percent);
      panel.style.setProperty('--slide-progress', `${percent}%`);
      panel.classList.toggle('is-ready', percent >= 100);
      if (output) output.textContent = `${Math.round(percent)}%`;
    };
    const reset = () => {
      fired = false;
      setProgress(0);
      panel.hidden = true;
      trigger.hidden = false;
      trigger.setAttribute('aria-expanded', 'false');
      if (activeSlideReset === reset) activeSlideReset = null;
    };
    trigger.addEventListener('click', () => {
      if (trigger.disabled) return;
      if (activeSlideReset && activeSlideReset !== reset) activeSlideReset();
      activeSlideReset = reset;
      trigger.hidden = true;
      panel.hidden = false;
      trigger.setAttribute('aria-expanded', 'true');
      setProgress(0);
      slider.focus({ preventScroll: true });
    });
    panel.querySelector('[data-slide-cancel]')?.addEventListener('click', reset);
    slider.addEventListener('input', () => {
      setProgress(slider.value);
      if (Number(slider.value) < 100 || fired) return;
      fired = true;
      action();
      reset();
    });
    const snapBack = () => { if (!fired && Number(slider.value) < 100) setProgress(0); };
    slider.addEventListener('pointerup', snapBack);
    slider.addEventListener('pointercancel', snapBack);
    slider.addEventListener('change', snapBack);
    [trigger, panel].forEach(element => element.addEventListener('contextmenu', event => event.preventDefault()));
  }

  function partHeatRatio(part) {
    return Math.max(
      Number(part.temperature || 0) / Math.max(1, Number(part.maximumTemperature || 1)),
      Number(part.skinTemperature || 0) / Math.max(1, Number(part.maximumSkinTemperature || 1))
    );
  }

  function syncVesselRenderSurface() {
    const stage = $('#vesselRenderStage');
    const surface = $('#vesselRenderSurface');
    if (!stage || !surface) return;
    const size = Math.max(1, Math.min(stage.clientWidth, stage.clientHeight));
    surface.style.width = `${size}px`;
    surface.style.height = `${size}px`;
  }

  function renderAerodynamicCenter(aero) {
    const marker = $('#aeroCenterMarker');
    const readout = $('#aeroCenterReadout');
    if (!marker || !readout) return;
    const pressureKpa = Number(state.regularSample?.staticPressureKpa);
    const atmosphereKnown = Boolean(state.regularSample) && Number.isFinite(pressureKpa);
    const outsideAtmosphere = atmosphereKnown && pressureKpa <= 1e-6;
    if (!atmosphereKnown || outsideAtmosphere) {
      marker.hidden = true;
      readout.hidden = true;
      return;
    }
    readout.hidden = false;
    const available = Boolean(aero?.available);
    const projected = available && Boolean(aero?.projected)
      && Number.isFinite(Number(aero.normalizedX)) && Number.isFinite(Number(aero.normalizedY));
    marker.hidden = !projected;
    if (projected) {
      marker.style.left = `${Math.max(0, Math.min(1, Number(aero.normalizedX))) * 100}%`;
      marker.style.top = `${Math.max(0, Math.min(1, Number(aero.normalizedY))) * 100}%`;
    }
    if (!available) {
      readout.querySelector('b').textContent = 'Currently unavailable';
      readout.querySelector('small').textContent = aero?.status || 'Waiting for FAR aerodynamic data';
      return;
    }
    const forward = Number(aero.forwardOffset || 0);
    const longitudinal = Math.abs(forward) < .01 ? 'Longitudinally aligned with CoM' : `${Math.abs(forward).toFixed(2)} m ${forward >= 0 ? 'ahead' : 'behind'}`;
    readout.querySelector('b').textContent = `${aero.source || 'FAR'} · ${Number(aero.distanceFromCom || 0).toFixed(2)} m`;
    readout.querySelector('small').textContent = `CoM Reference:${longitudinal} · Right ${Number(aero.rightOffset || 0).toFixed(2)} m · Up ${Number(aero.upOffset || 0).toFixed(2)} m${projected ? '' : ' · Outside current view'}`;
  }

  function selectVesselPart(partId) {
    const data = state.vesselStructure;
    const parts = Array.isArray(data?.parts) ? data.parts : [];
    const selected = parts.find(part => Number(part.id) === Number(partId));
    if (!selected) return;
    if (Number(state.selectedPartId) !== Number(selected.id)) state.partControlEditingUntil = 0;
    state.selectedPartId = selected.id;
    if (Number(state.highlightedPartId) !== Number(selected.id)) {
      if (sendCommand('vessel.view.selectPart', { partId: Number(selected.id) }, true)) state.highlightedPartId = selected.id;
    }
    const byId = new Map(parts.map(part => [part.id, part]));
    const inspector = $('.part-inspector');
    userText($('#partName'), selected.title || selected.name);
    userText($('#part-action-title'), selected.title || selected.name);
    const partMeta = `<span data-l10n-skip>${escapeHtml(selected.name || '')}</span> · <span>${Number(selected.stage) < 0 ? 'Unstaged' : `Stage ${Number(selected.stage)}`}</span>`;
    $('#partActionMeta').innerHTML = `${partMeta} · <span>PART ${Number(selected.id)}</span>`;
    inspector.querySelector('header .eyebrow').textContent = `PART / ${selected.id}`;
    inspector.querySelector('header small').innerHTML = partMeta;
    inspector.querySelector('.part-health').innerHTML = `<div><span>Internal temperature</span><b>${Number(selected.temperature || 0).toFixed(0)} K</b><small>/ ${Number(selected.maximumTemperature || 0).toFixed(0)} K</small></div><div><span>Skin temperature</span><b>${Number(selected.skinTemperature || 0).toFixed(0)} K</b><small>/ ${Number(selected.maximumSkinTemperature || 0).toFixed(0)} K</small></div>`;
    inspector.querySelector('.resource-stack').innerHTML = '<h3>Resources</h3>' + (selected.resources || []).map(resource => {
      const ratio = resource.maximum > 0 ? Math.max(0, Math.min(100, resource.amount / resource.maximum * 100)) : 0;
      return `<div class="resource"><span><span data-l10n-skip>${escapeHtml(resource.name)}</span> <b>${ratio.toFixed(0)}%</b></span><i><em style="width:${ratio.toFixed(1)}%"></em></i><small>${Number(resource.amount).toFixed(1)} / ${Number(resource.maximum).toFixed(1)} u</small></div>`;
    }).join('');
    const parent = selected.parentId == null ? null : byId.get(selected.parentId);
    const children = parts.filter(part => part.parentId === selected.id).length;
    inspector.querySelector('.part-facts').innerHTML = `<div><dt>Dry mass</dt><dd>${Number(selected.mass || 0).toFixed(3)} t</dd></div><div><dt>Parent part</dt><dd${parent?.title ? ' data-l10n-skip' : ''}>${escapeHtml(parent?.title || 'Root')}</dd></div><div><dt>Child parts</dt><dd>${children}</dd></div><div><dt>Structure revision</dt><dd>#${Number(data.revision || 0)}</dd></div>`;
    if (!state.partControlEditingUntil || performance.now() > state.partControlEditingUntil) renderPartControls(selected);
    $$('.part-index-item').forEach(button => button.classList.toggle('is-selected', Number(button.dataset.partId) === Number(selected.id)));
    $$('.vehicle-parts .part').forEach(part => part.classList.toggle('selected', Number(part.dataset.partId) === Number(selected.id)));
    $('#vesselSelectedId').textContent = `SELECTED / ${selected.id}`;
    userText($('#vesselSelectedName'), String(selected.title || selected.name || 'PART').toUpperCase().slice(0, 24));
  }

  function renderPartControls(part) {
    const moduleMeta = (kind, index, status) => {
      const labels = { rcs: 'RCS', light: 'Lights', gear: 'Landing gear', cargo: 'Cargo bay door',
        clamp: 'Launch clamp', parachute: 'Parachute', radiator: 'Radiator', fairing: 'Fairing',
        decoupler: 'Decoupler', docking: 'Docking ports', b9: 'B9 Part Switch', generator: 'Generator',
        reactor: 'Nuclear reactor', converter: 'Resource converter', control: 'Control reference' };
      return `<span>${escapeHtml(labels[kind] || kind || 'Module')}</span>${status ? ` · <span>${escapeHtml(status)}</span>` : ''} · <span>MODULE ${Number(index)}</span>`;
    };
    const engines = Array.isArray(part.engines) ? part.engines : [];
    const toggles = Array.isArray(part.toggles) ? part.toggles : [];
    const actions = Array.isArray(part.actions) ? part.actions : [];
    const engineCards = engines.map((engine, ordinal) => {
      const limit = Math.max(0, Math.min(100, Number(engine.thrustLimit ?? 100)));
      const name = engine.name || engine.engineId || `ENGINE ${ordinal + 1}`;
      const gimbal = Number(engine.gimbalModuleIndex) >= 0
        ? `<button class="part-toggle${engine.gimbalEnabled ? ' is-on' : ''}" data-part-command="vessel.part.gimbal.set" data-module-index="${Number(engine.gimbalModuleIndex)}" data-enabled="${engine.gimbalEnabled ? 'false' : 'true'}">GIMBAL ${engine.gimbalEnabled ? 'ON' : 'OFF'}</button>` : '<button class="part-toggle" disabled>NO GIMBAL</button>';
      return `<div class="part-control-card engine-control"><div class="part-control-title"><span><b data-l10n-skip>${escapeHtml(name)}</b><small>ENGINE ${ordinal + 1} · MODULE ${Number(engine.moduleIndex)}</small></span><button class="part-toggle${engine.ignited ? ' is-on' : ''}" data-part-command="vessel.part.engine.setActive" data-module-index="${Number(engine.moduleIndex)}" data-enabled="${engine.ignited ? 'false' : 'true'}" ${(engine.ignited ? engine.canShutdown : engine.canActivate) ? '' : 'disabled'}>${engine.ignited ? 'Running' : 'Off'}</button></div><div class="engine-limit"><label><span>Thrust limiter · live</span><output>${limit.toFixed(0)}%</output></label><input type="range" min="0" max="100" step="1" value="${limit.toFixed(0)}" data-engine-limit="${Number(engine.moduleIndex)}" data-part-id="${Number(part.id)}" aria-label="${escapeHtml(name)} Thrust limit"></div><div class="engine-switches">${gimbal}<span></span></div></div>`;
    }).join('');
    const labels = { rcs: ['RCS Thruster', 'Enable', 'Deactivate'], light: ['Lights', 'On', 'Off'], gear: ['Landing gear', 'Deploy', 'Retract'], cargo: ['Cargo bay door', 'Open', 'Closed'] };
    const toggleCards = toggles.map(toggle => {
      const label = labels[toggle.kind] || [toggle.name || toggle.kind, 'On', 'Off'];
      return `<div class="part-control-card"><div class="part-control-title"><span><b>${escapeHtml(toggle.name || label[0])}</b><small>${moduleMeta(toggle.kind, toggle.moduleIndex, toggle.transitioning ? 'Moving' : '')}</small></span><button class="part-toggle${toggle.enabled ? ' is-on' : ''}" data-part-command="vessel.part.${escapeHtml(toggle.kind)}.set" data-module-index="${Number(toggle.moduleIndex)}" data-enabled="${toggle.enabled ? 'false' : 'true'}" ${toggle.transitioning ? 'disabled' : ''}>${toggle.enabled ? label[1] : label[2]}</button></div></div>`;
    }).join('');
    const actionCards = actions.map(action => {
      const options = Array.isArray(action.options) ? action.options : [];
      const optionControl = options.length
        ? `<select data-part-action-option="${Number(action.moduleIndex)}" aria-label="${escapeHtml(action.name || 'Module')} Configuration">${options.map(option => `<option value="${escapeHtml(option.value)}" ${option.value === action.selectedOption ? 'selected' : ''}>${escapeHtml(option.label || option.value)}</option>`).join('')}</select>`
        : '';
      const danger = ['parachute.cut', 'partEvent.GUICut', 'partEvent.Release', 'partEvent.DeployFairing',
        'partEvent.Jettison', 'partEvent.OnJettisonFairing', 'partEvent.DeployEvent',
        'partEvent.Decouple', 'partEvent.Undock', 'partEvent.UndockSameVessel'].includes(action.command);
      const button = `<button class="part-action-button${action.active ? ' is-active' : ''}${danger ? ' is-danger' : ''}" data-part-action="${escapeHtml(action.command || '')}" data-module-index="${Number(action.moduleIndex)}" ${action.available && action.command ? '' : 'disabled'}>${escapeHtml(action.commandLabel || 'Not actionable')}</button>`;
      return `<div class="part-control-card part-module-action${options.length ? ' has-options' : ''}"><div class="part-control-title"><span><b>${escapeHtml(action.name || action.kind)}</b><small>${moduleMeta(action.kind, action.moduleIndex, action.status || 'Waiting for state')}</small></span></div>${optionControl}${button}</div>`;
    }).join('');
    $('#partControlStack').innerHTML = engineCards + toggleCards + actionCards || '<p class=\"part-control-empty\">This part has no remotely controllable devices</p>';
    $('#partControlStack').querySelectorAll('[data-part-command], [data-part-action]').forEach(button => {
      button.dataset.partId = String(part.id);
    });
  }

  function renderPartIndex() {
    const parts = Array.isArray(state.vesselStructure?.parts) ? state.vesselStructure.parts : [];
    const query = String($('#partSearch').value || '').trim().toLowerCase();
    const categoryPatterns = {
      control: /ModuleControlSurface|FARControllableSurface|ModuleAeroSurface|control.?surface|aileron|rudder|elevon/i,
      dock: /ModuleDockingNode|docking|dock|对接/i,
      power: /ModuleDeployableSolarPanel|ModuleGenerator|FissionGenerator|ModuleResourceConverter|solar|reactor|battery|电池|太阳能|反应堆/i
    };
    const bulkLabels = {
      engine: ['ENGINE GROUP', 'Turn all off', 'Start all'],
      rcs: ['RCS GROUP', 'Deactivate all', 'Enable all'],
      cargo: ['CARGO GROUP', 'Turn all off', 'Open all'],
      gear: ['GEAR GROUP', 'Retract all', 'Deploy all'],
      light: ['LIGHT GROUP', 'Turn all off', 'Turn all on']
    };
    const groupActions = $('#partGroupActions');
    const groupLabels = bulkLabels[state.partGroup];
    groupActions.hidden = !groupLabels;
    if (groupLabels) {
      $('#partGroupActionLabel').textContent = groupLabels[0];
      groupActions.querySelector('[data-group-enabled="false"]').textContent = groupLabels[1];
      groupActions.querySelector('[data-group-enabled="true"]').textContent = groupLabels[2];
    }
    const inGroup = part => {
      if (state.partGroup === 'all') return true;
      if (state.partGroup === 'engine') return Array.isArray(part.engines) && part.engines.length > 0;
      if (['rcs', 'cargo', 'gear', 'light'].includes(state.partGroup))
        return Array.isArray(part.toggles) && part.toggles.some(toggle => toggle.kind === state.partGroup);
      const terms = [part.title, part.name, ...(part.modules || [])].join(' ');
      return categoryPatterns[state.partGroup]?.test(terms) || false;
    };
    const grouped = parts.filter(inGroup);
    const visible = query ? grouped.filter(part => {
      const terms = [part.title, part.name, ...(part.modules || []), ...(part.resources || []).map(resource => resource.name)];
      return terms.some(term => String(term || '').toLowerCase().includes(query));
    }) : grouped;
    $('#partIndexList').innerHTML = visible.map(part => {
      const heat = Math.max(0, partHeatRatio(part) * 100);
      const stage = Number(part.stage) < 0 ? 'Unstaged' : `STAGE ${Number(part.stage)}`;
      return `<button class="part-index-item${Number(part.id) === Number(state.selectedPartId) ? ' is-selected' : ''}" data-part-id="${Number(part.id)}"><span><b data-l10n-skip>${escapeHtml(part.title || part.name)}</b><small><span data-l10n-skip>${escapeHtml(part.name)}</span> · <span>${stage}</span></small></span><em>${heat.toFixed(0)}%</em></button>`;
    }).join('') || '<div class=\"part-index-item\"><span><b>No matching parts</b><small>Adjust search filters</small></span></div>';
    $$('.part-index-item[data-part-id]').forEach(button => button.addEventListener('click', () => selectVesselPart(Number(button.dataset.partId))));
  }

  function renderVesselDiagram(parts) {
    const byId = new Map(parts.map(part => [Number(part.id), part]));
    const depthMemo = new Map();
    const depthOf = (part, visiting = new Set()) => {
      const id = Number(part.id);
      if (depthMemo.has(id)) return depthMemo.get(id);
      if (part.parentId == null || !byId.has(Number(part.parentId)) || visiting.has(id)) return 0;
      visiting.add(id);
      const depth = depthOf(byId.get(Number(part.parentId)), visiting) + 1;
      depthMemo.set(id, depth);
      return depth;
    };
    const levels = new Map();
    parts.forEach(part => {
      const depth = depthOf(part);
      if (!levels.has(depth)) levels.set(depth, []);
      levels.get(depth).push(part);
    });
    const maximumDepth = Math.max(0, ...levels.keys());
    const positions = new Map();
    levels.forEach((level, depth) => level.forEach((part, index) => {
      positions.set(Number(part.id), {
        x: 52 + (index + 1) * 576 / (level.length + 1),
        y: 80 + depth * 600 / Math.max(1, maximumDepth)
      });
    }));
    $('#vehicleConnections').innerHTML = parts.map(part => {
      const from = part.parentId == null ? null : positions.get(Number(part.parentId));
      const to = positions.get(Number(part.id));
      return from && to ? `<path d="M${from.x.toFixed(1)} ${from.y.toFixed(1)}L${to.x.toFixed(1)} ${to.y.toFixed(1)}"/>` : '';
    }).join('');
    $('#vehicleParts').innerHTML = parts.map(part => {
      const position = positions.get(Number(part.id));
      const heatClass = partHeatRatio(part) < .8 ? 0 : Math.min(5, 1 + Math.floor((partHeatRatio(part) - .8) / .04));
      return `<circle class="part heat-${heatClass}" data-part-id="${Number(part.id)}" cx="${position.x.toFixed(1)}" cy="${position.y.toFixed(1)}" r="8"><title>${escapeHtml(part.title || part.name)}</title></circle>`;
    }).join('');
    $$('.vehicle-parts .part').forEach(part => part.addEventListener('click', () => selectVesselPart(Number(part.dataset.partId))));
  }

  function acceptVesselStructure(data) {
    const parts = Array.isArray(data.parts) ? data.parts : [];
    const previous = state.vesselStructure;
    // Older running DLLs do not include crew in vessel.structure; keep the
    // authoritative HTTP manifest until the updated transport is loaded.
    if (Array.isArray(data.crew)) acceptCrewManifest(data);
    const topologyChanged = !previous
      || previous.vesselId !== data.vesselId
      || !Array.isArray(previous.parts)
      || previous.parts.length !== parts.length
      || previous.parts.some((part, index) => Number(part.id) !== Number(parts[index]?.id)
        || Number(part.parentId) !== Number(parts[index]?.parentId));
    state.structureRevision = Math.max(state.structureRevision, Number(data.revision || 0));
    if (topologyChanged) {
      renderVesselStructure(data);
      return;
    }
    state.vesselStructure = data;
    renderAerodynamicCenter(data.aerodynamicCenter);
    $('#vesselPartCount').textContent = `${parts.length} PARTS`;
    selectVesselPart(state.selectedPartId);
    if ($('#vehicleSvg') && !$('#vehicleSvg').hidden) renderVesselDiagram(parts);
  }

  function renderVesselStructure(data) {
    const parts = Array.isArray(data.parts) ? data.parts : [];
    state.vesselStructure = data;
    renderAerodynamicCenter(data.aerodynamicCenter);
    $('#vesselPartCount').textContent = `${parts.length} PARTS`;
    if (!parts.length) {
      state.selectedPartId = null;
      state.highlightedPartId = null;
      userText($('#part-action-title'), null, 'No active vessel');
      $('#partActionMeta').textContent = 'Waiting for selectable parts';
      $('#partControlStack').innerHTML = '<p class=\"part-control-empty\">No controllable parts</p>';
      $('#vehicleConnections').innerHTML = '';
      $('#vehicleParts').innerHTML = '';
      $('#partIndexList').innerHTML = '<div class=\"part-index-item\"><span><b>No active vessel</b></span></div>';
      return;
    }
    renderVesselDiagram(parts);
    if (!parts.some(part => Number(part.id) === Number(state.selectedPartId))) {
      state.selectedPartId = parts.reduce((hottest, part) => {
        const ratio = partHeatRatio(part);
        return !hottest || ratio > hottest.ratio ? { id: part.id, ratio } : hottest;
      }, null).id;
    }
    renderPartIndex();
    selectVesselPart(state.selectedPartId);
  }

  $('#partSearch').addEventListener('input', renderPartIndex);
  $$('.part-quick-groups button').forEach(button => button.addEventListener('click', () => {
    state.partGroup = button.dataset.partGroup || 'all';
    $$('.part-quick-groups button').forEach(item => item.classList.toggle('is-active', item === button));
    renderPartIndex();
  }));
  $('#partGroupActions').addEventListener('click', event => {
    const button = event.target.closest('[data-group-enabled]');
    if (!button || !['engine', 'rcs', 'cargo', 'gear', 'light'].includes(state.partGroup)) return;
    if (!sendCommand('vessel.partGroup.set', {
      kind: state.partGroup, enabled: button.dataset.groupEnabled === 'true'
    })) showToast('No live connection; group command not sent');
  });
  $('#partControlStack').addEventListener('pointerdown', event => {
    if (event.target.matches('[data-engine-limit], [data-part-action-option]')) state.partControlEditingUntil = performance.now() + 60000;
  });
  $('#partControlStack').addEventListener('pointerup', event => {
    if (event.target.matches('[data-engine-limit]')) state.partControlEditingUntil = performance.now() + 800;
  });
  $('#partControlStack').addEventListener('input', event => {
    if (!event.target.matches('[data-engine-limit]')) return;
    const output = event.target.closest('.engine-limit')?.querySelector('output');
    if (output) output.textContent = `${Number(event.target.value).toFixed(0)}%`;
    scheduleEngineLimit(event.target, false);
  });
  $('#partControlStack').addEventListener('change', event => {
    if (event.target.matches('[data-engine-limit]')) scheduleEngineLimit(event.target, true);
    if (event.target.matches('[data-part-action-option]')) state.partControlEditingUntil = performance.now() + 5000;
  });
  function scheduleEngineLimit(input, immediate) {
    const send = () => {
      clearTimeout(input._engineLimitTimer);
      input._engineLimitTimer = null;
      input._engineLimitLastSent = performance.now();
      if (!sendCommand('vessel.part.engine.setThrustLimit', {
        partId: Number(input.dataset.partId), moduleIndex: Number(input.dataset.engineLimit), percent: Number(input.value)
      })) showToast('No live connection; thrust limit not updated');
    };
    const elapsed = performance.now() - Number(input._engineLimitLastSent || 0);
    if (immediate || elapsed >= 80) { send(); return; }
    clearTimeout(input._engineLimitTimer);
    input._engineLimitTimer = setTimeout(send, 80 - elapsed);
  }
  $('#partControlStack').addEventListener('click', event => {
    const commandButton = event.target.closest('[data-part-command]');
    if (commandButton) {
      if (Number(commandButton.dataset.partId) !== Number(state.selectedPartId)) return;
      const parameters = {
        partId: Number(commandButton.dataset.partId),
        moduleIndex: Number(commandButton.dataset.moduleIndex),
        enabled: commandButton.dataset.enabled === 'true'
      };
      if (!sendCommand(commandButton.dataset.partCommand, parameters)) showToast('No live connection; part command not sent');
      return;
    }
    const actionButton = event.target.closest('[data-part-action]');
    if (actionButton) {
      if (Number(actionButton.dataset.partId) !== Number(state.selectedPartId)) return;
      const option = actionButton.closest('.part-module-action')?.querySelector('[data-part-action-option]');
      state.partControlEditingUntil = performance.now() + 800;
      if (!sendCommand('vessel.part.action', {
        partId: Number(actionButton.dataset.partId),
        moduleIndex: Number(actionButton.dataset.moduleIndex),
        action: actionButton.dataset.partAction,
        value: option?.value || ''
      })) showToast('No live connection; part action not sent');
      return;
    }
  });
  $('#targetSearch').addEventListener('input', renderTargetTree);
  $('#expandTargetTree').addEventListener('click', () => {
    const expandable = state.targetCatalog.filter(entry => state.targetCatalog.some(child => child.parentId === entry.id));
    const expand = expandable.some(entry => !state.expandedTargets.has(entry.id));
    state.expandedTargets.clear();
    if (expand) expandable.forEach(entry => state.expandedTargets.add(entry.id));
    $('#expandTargetTree').textContent = expand ? 'Collapse all' : 'Expand all';
    renderTargetTree();
  });
  $('#applyTarget').addEventListener('click', () => {
    const entry = state.targetCatalog.find(item => item.id === state.selectedTargetId);
    if (!entry) return;
    if (!sendCommand('target.set', {
      kind: entry.kind, bodyName: entry.bodyName, vesselId: entry.vesselId, partId: Number(entry.partId || 0)
    })) showToast('No live connection; target not set');
  });
  $('#clearTarget').addEventListener('click', () => {
    if (!sendCommand('target.clear')) showToast('No live connection; target not cleared');
  });

  function reconnectRealtime() {
    state.protocolBlocked = false;
    state.reconnectAttempt = 0;
    connectRealtime();
  }

  window.ArmorControlConnection = { reconnect: reconnectRealtime, sendCommand };
  initializeUnavailableState();
  connectRealtime();

  $$('.nav-item').forEach(button => {
    button.addEventListener('click', () => {
      const name = button.dataset.view;
      $$('.nav-item').forEach(item => item.classList.toggle('is-active', item === button));
      $$('.view').forEach(view => view.classList.toggle('is-active', view.id === `view-${name}`));
      window.ArmorMap?.active(name === 'map');
      if (name === 'vessel') connectVesselImage();
      else disconnectVesselImage();
      window.scrollTo({ top: 0, behavior: 'smooth' });
    });
  });

  $$('.envelope-view input').forEach(input => input.addEventListener('input', () => {
    state.envelopeDraft = true;
    if (input.id === 'envelopeMaximumAcceleration') updateEnvelopeAccelerationEquivalent();
  }));
  $('#applyEnvelope').addEventListener('click', () => {
    const parameters = {
      dynamicPressure: $('#envelopeLimitQ').checked,
      maximumDynamicPressureKpa: Number($('#envelopeMaximumQ').value),
      acceleration: $('#envelopeLimitAcceleration').checked,
      maximumAcceleration: Number($('#envelopeMaximumAcceleration').value),
      maximumThrottle: $('#envelopeLimitMaximumThrottle').checked,
      maximumThrottlePercent: Number($('#envelopeMaximumThrottle').value),
      minimumThrottle: $('#envelopeLimitMinimumThrottle').checked,
      minimumThrottlePercent: Number($('#envelopeMinimumThrottle').value),
      preventOverheat: $('#envelopePreventOverheat').checked,
      terminalVelocity: $('#envelopeTerminalVelocity').checked,
      preventFlameout: $('#envelopePreventFlameout').checked,
      flameoutSafetyPercent: Number($('#envelopeFlameoutMargin').value),
      preventUnstableIgnition: $('#envelopePreventUnstableIgnition').checked,
      autoRcsUllage: $('#envelopeAutoRcsUllage').checked,
      smoothThrottle: $('#envelopeSmoothThrottle').checked,
      smoothingTime: Number($('#envelopeSmoothingTime').value),
      manageIntakes: $('#envelopeManageIntakes').checked,
      differentialThrottle: $('#envelopeDifferentialThrottle').checked,
      autoStage: $('#envelopeAutoStage').checked
    };
    if (parameters.minimumThrottlePercent > parameters.maximumThrottlePercent) {
      showToast('Minimum throttle cannot exceed maximum throttle'); return;
    }
    if (sendCommand('mechjeb.envelope.set', parameters)) state.envelopeDraft = false;
  });

  function sendTrajectorySettings() {
    state.trajectorySettingsDraft = true;
    const commandId = sendCommand('trajectories.settings.set', {
      display: $('#trajectoryDisplay').checked, displayInFlight: $('#trajectoryDisplayInFlight').checked,
      alwaysUpdate: $('#trajectoryAlwaysUpdate').checked, complete: $('#trajectoryComplete').checked,
      bodyFixed: $('#trajectoryBodyFixed').checked, autoUpdateAero: $('#trajectoryAutoAero').checked,
      useCache: $('#trajectoryUseCache').checked, defaultRetrograde: $('#trajectoryDefaultRetrograde').checked,
      integrationStep: Number($('#trajectoryIntegrationStep').value), maxPatches: Number($('#trajectoryMaxPatches').value),
      maxFramesPerPatch: Number($('#trajectoryMaxFrames').value)
    });
    if (!commandId) {
      state.trajectorySettingsDraft = false;
      showToast('No live connection; Trajectories settings not sent');
      return false;
    }
    state.trajectorySettingsPendingCommandId = commandId;
    return true;
  }
  $$('.trajectory-display-controls input[type="checkbox"],.trajectory-quality input[type="checkbox"]').forEach(input => input.addEventListener('change', () => {
    if (input.id === 'trajectoryDisplayInFlight' && input.checked) $('#trajectoryDisplay').checked = true;
    if (input.id === 'trajectoryDisplay' && !input.checked) $('#trajectoryDisplayInFlight').checked = false;
    sendTrajectorySettings();
  }));
  $$('.trajectory-quality input:not([type="checkbox"])').forEach(input => input.addEventListener('input', () => { state.trajectorySettingsDraft = true; }));
  $$('.trajectory-profile input,.trajectory-profile select').forEach(input => input.addEventListener('input', () => { state.trajectoryProfileDraft = true; syncTrajectoryProfileInputs(); }));
  $$('.trajectory-target input').forEach(input => input.addEventListener('input', () => { state.trajectoryTargetDraft = true; }));
  $('#applyTrajectorySettings').addEventListener('click', () => {
    sendTrajectorySettings();
  });
  $('#updateTrajectoryNow').addEventListener('click', () => sendCommand('trajectories.update'));
  $('#applyTrajectoryProfile').addEventListener('click', () => {
    const sent = sendCommand('trajectories.profile.set', {
      entryMode: $('#trajectoryEntryMode').value, entryAngle: Number($('#trajectoryEntryAngle').value),
      entryRetrograde: $('#trajectoryEntryGrade').value === 'RETROGRADE',
      highMode: $('#trajectoryHighMode').value, highAngle: Number($('#trajectoryHighAngle').value),
      highRetrograde: $('#trajectoryHighGrade').value === 'RETROGRADE',
      lowMode: $('#trajectoryLowMode').value, lowAngle: Number($('#trajectoryLowAngle').value),
      lowRetrograde: $('#trajectoryLowGrade').value === 'RETROGRADE',
      finalMode: $('#trajectoryFinalMode').value, finalAngle: Number($('#trajectoryFinalAngle').value),
      finalRetrograde: $('#trajectoryFinalGrade').value === 'RETROGRADE'
    });
    if (sent) state.trajectoryProfileDraft = false;
  });
  $('#setTrajectoryTarget').addEventListener('click', () => {
    const sent = sendCommand('trajectories.target.set', {
      latitude: Number($('#trajectoryTargetLatitude').value), longitude: Number($('#trajectoryTargetLongitude').value),
      altitude: Number($('#trajectoryTargetAltitude').value)
    });
    if (sent) state.trajectoryTargetDraft = false;
  });
  $('#clearTrajectoryTarget').addEventListener('click', () => {
    if (sendCommand('trajectories.target.clear')) state.trajectoryTargetDraft = false;
  });
  syncTrajectoryProfileInputs();
  $('#pinFlightView').addEventListener('click', event => {
    const pinned = localStorage.getItem('armorControlDefaultView') === 'flight';
    if (pinned) localStorage.removeItem('armorControlDefaultView');
    else localStorage.setItem('armorControlDefaultView', 'flight');
    event.currentTarget.classList.toggle('is-active', !pinned);
    showToast(pinned ? 'Default Flight page cleared' : 'Flight is now the default page');
  });
  if (localStorage.getItem('armorControlDefaultView') === 'flight') $('#pinFlightView').classList.add('is-active');

  $$('.segment-tabs button').forEach(button => {
    button.addEventListener('click', () => {
      $$('.segment-tabs button').forEach(item => item.classList.toggle('is-active', item === button));
      $$('.ledger-group').forEach(group => group.classList.toggle('is-active', group.dataset.ledger === button.dataset.group));
    });
  });

  const updatePeColors = () => {
    $$('[data-pe]').forEach(element => {
      element.classList.toggle('is-negative', Number(element.dataset.pe) < 0);
    });
  };
  updatePeColors();
  const peObserver = new MutationObserver(updatePeColors);
  $$('[data-pe]').forEach(element => peObserver.observe(element, { attributes: true, attributeFilter: ['data-pe'] }));

  const timeField = options => `<label><span>Execution timing</span><select data-time-reference>${options.map(option => `<option value="${option[0]}">${option[1]}</option>`).join('')}</select></label><label class="time-lead-field"><span>Fixed delay</span><div class="unit-input"><input data-time-lead value="60" inputmode="decimal"><b>s</b></div></label>`;
  const unitField = (label, value, unit) => `<label><span>${label}</span><div class="unit-input"><input value="${value}" inputmode="decimal"><b>${unit}</b></div></label>`;
  const targetField = (name = 'Current KSP target', detail = 'Provided by the game target system') => `<div class="setup-target"><span>Target<b>${name}</b></span><em>${detail}</em></div>`;
  const note = text => `<p class="setup-note">${text}</p>`;
  const operations = {
    circularize: { code: 'CIRCULARIZE', fields: timeField([['APOAPSIS', 'At next apoapsis'], ['PERIAPSIS', 'At next periapsis'], ['X_FROM_NOW', 'After a fixed delay']]) + note('Circularization needs no target altitude; the selected burn position determines the final altitude.') },
    periapsis: { code: 'CHANGE PERIAPSIS', fields: timeField([['APOAPSIS', 'At next apoapsis'], ['X_FROM_NOW', 'After a fixed delay']]) + unitField('New periapsis altitude', '85.0', 'km') },
    apoapsis: { code: 'CHANGE APOAPSIS', fields: timeField([['PERIAPSIS', 'At next periapsis'], ['X_FROM_NOW', 'After a fixed delay']]) + unitField('New apoapsis altitude', '420.0', 'km') },
    apsides: { code: 'CHANGE APSIDES', fields: timeField([['APOAPSIS', 'At next apoapsis'], ['X_FROM_NOW', 'After a fixed delay']]) + unitField('New periapsis altitude', '85.0', 'km') + unitField('New apoapsis altitude', '420.0', 'km') },
    inclination: { code: 'CHANGE INCLINATION', fields: timeField([['EQ_NEAREST_AD', 'At nearest equatorial AN / DN'], ['EQ_HIGHEST_AD', 'At cheapest equatorial AN / DN'], ['EQ_ASCENDING', 'At equatorial ascending node'], ['EQ_DESCENDING', 'At equatorial descending node'], ['X_FROM_NOW', 'After a fixed delay']]) + unitField('New inclination', '0.00', 'deg') },
    lan: { code: 'CHANGE LAN', fields: timeField([['APOAPSIS', 'At next apoapsis'], ['PERIAPSIS', 'At next periapsis'], ['X_FROM_NOW', 'After a fixed delay']]) + unitField('New LAN', '74.56', 'deg') + note('LAN is only defined for non-equatorial orbits; MechJeb warns near zero inclination.') },
    semimajor: { code: 'CHANGE SEMI-MAJOR AXIS', fields: timeField([['APOAPSIS', 'At next apoapsis'], ['PERIAPSIS', 'At next periapsis'], ['X_FROM_NOW', 'After a fixed delay']]) + unitField('New semi-major axis', '782.4', 'km') },
    longitude: { code: 'SHIFT APSIS LONGITUDE', fields: unitField('New ground longitude', '110.0', 'deg') + note('MechJeb The node time for the period change is calculated automatically.') },
    hohmann: { code: 'HOHMANN TRANSFER', dv: '214.9 m/s', result: 'Rendezvous distance 1.8 km', burn: '00:12.9', time: 'T− 14:32', fields: targetField() + note('Finds the next Hohmann window for a target orbiting the same body.') },
    correction: { code: 'COURSE CORRECTION', fields: targetField('Duna', 'Current KSP target') + unitField('Target periapsis', '80.0', 'km') + timeField([['COMPUTED', 'MechJeb calculates optimal timing'], ['X_FROM_NOW', 'After a fixed delay']]) },
    intercept: { code: 'INTERCEPT AT TIME', dv: '347.6 m/s', result: 'Rendezvous T+ 02:00:00', burn: '00:20.8', time: 'T− 00:10', fields: targetField() + unitField('Transfer time', '2:00:00', 'h:m:s') + note('Lambert Computes an intercept at the target position after the chosen transfer time.') },
    planes: { code: 'MATCH ORBIT PLANES', fields: targetField() + timeField([['REL_NEAREST_AD', 'At nearest relative AN / DN'], ['REL_HIGHEST_AD', 'At cheapest relative AN / DN'], ['REL_ASCENDING', 'At next relative ascending node'], ['REL_DESCENDING', 'At next relative descending node']]) },
    velocity: { code: 'MATCH VELOCITIES', fields: targetField() + timeField([['CLOSEST_APPROACH', 'At closest approach'], ['X_FROM_NOW', 'After a fixed delay']]) + note('Cancels relative velocity at the chosen time, usually the final rendezvous maneuver.') },
    resonant: { code: 'RESONANT ORBIT', fields: timeField([['APOAPSIS', 'At next apoapsis'], ['PERIAPSIS', 'At next periapsis']]) + unitField('Vessel orbit count', '2', 'rev') + unitField('Target orbit count', '3', 'rev') },
    moonreturn: { code: 'MOON RETURN', fields: unitField('Parent-body return periapsis', '32.0', 'km') + note('MechJeb Computes a return window when orbiting a moon with a parent-body orbit.') },
    planettransfer: { code: 'TRANSFER TO PLANET', fields: targetField('Current target', 'Current KSP planet target') + note('MechJeb Computes the next phase-angle window; target and current body must share a parent.') },
    advanced: { code: 'ADVANCED TRANSFER', fields: '' }
  };

  function renderOperation(button, announce = true) {
    const operation = operations[button.dataset.operation];
    $$('.maneuver-list button').forEach(item => item.classList.toggle('is-active', item === button));
    $('.planner-workspace').classList.toggle('is-porkchop', button.dataset.operation === 'advanced');
    $('#maneuverName').textContent = button.dataset.maneuver;
    $('#operationCode').textContent = operation.code;
    $('#operationFields').innerHTML = operation.fields;
    const timeReference = $('#operationFields [data-time-reference]');
    const leadTimeField = $('#operationFields .time-lead-field');
    const syncLeadTime = () => { if (leadTimeField) leadTimeField.hidden = timeReference?.value !== 'X_FROM_NOW'; };
    timeReference?.addEventListener('change', syncLeadTime);
    syncLeadTime();
    $('#nodeDv').textContent = 'Waiting for solution';
    $('#plannedOrbit').textContent = 'MechJeb result after submission';
    $('#plannedBurn').textContent = '—';
    $('#plannedTime').textContent = '—';
    if (announce) showToast(`Maneuver:${button.dataset.maneuver}`);
  }

  $$('.maneuver-list button').forEach(button => button.addEventListener('click', () => renderOperation(button)));
  renderOperation($('.maneuver-list button.is-active'), false);

  function maneuverRequest() {
    const operation = $('.maneuver-list button.is-active')?.dataset.operation;
    if (!operation || operation === 'advanced') return null;
    const inputs = $$('#operationFields input:not([data-time-lead])').map(input => input.value.trim());
    const timeReference = $('#operationFields [data-time-reference]')?.value || '';
    const numeric = index => Number(String(inputs[index] || '').replace(/[^0-9+\-.]/g, ''));
    const parameters = { operation, replaceLast: Boolean($('.node-mode button:nth-child(2).is-active')) };
    if (timeReference) parameters.timeReference = timeReference;
    if (timeReference === 'X_FROM_NOW') parameters.leadTime = Math.max(.1, Number($('#operationFields [data-time-lead]')?.value) || 60);
    if (operation === 'periapsis') parameters.periapsis = numeric(0) * 1000;
    if (operation === 'apoapsis') parameters.apoapsis = numeric(0) * 1000;
    if (operation === 'apsides') { parameters.periapsis = numeric(0) * 1000; parameters.apoapsis = numeric(1) * 1000; }
    if (operation === 'inclination') parameters.inclination = numeric(0);
    if (operation === 'lan') parameters.lan = numeric(0);
    if (operation === 'semimajor') parameters.semiMajorAxis = numeric(0) * 1000;
    if (operation === 'longitude') parameters.longitude = numeric(0);
    if (operation === 'correction') parameters.targetPeriapsis = numeric(0) * 1000;
    if (operation === 'intercept') {
      const pieces = String(inputs[0] || '1:00:00').split(':').map(Number);
      parameters.interceptInterval = (pieces[0] || 0) * 3600 + (pieces[1] || 0) * 60 + (pieces[2] || 0);
    }
    if (operation === 'resonant') { parameters.numerator = numeric(0); parameters.denominator = numeric(1); }
    if (operation === 'moonreturn') parameters.returnPeriapsis = numeric(0) * 1000;
    return parameters;
  }

  function requestManeuver(execute) {
    const parameters = maneuverRequest();
    if (!parameters) {
      showToast('Solve the porkchop plot and select a point before creating an advanced transfer');
      return;
    }
    parameters.execute = execute;
    if (!sendCommand('mechjeb.maneuver.create', parameters)) {
      showToast('No live connection; node not created');
      return;
    }
  }
  $('#createManeuver').addEventListener('click', () => requestManeuver(false));
  $('#createExecuteManeuver').addEventListener('click', () => requestManeuver(true));

  $$('.transfer-modes button').forEach(button => {
    button.addEventListener('click', () => {
      $$('.transfer-modes button').forEach(item => item.classList.toggle('is-active', item === button));
      $('#porkchopPanel').classList.toggle('is-limited', button.dataset.transferMode === 'limited');
    });
  });

  const chart = $('#porkchopChart');
  const selection = $('#porkSelection');
  function selectPorkchop(clientX, clientY) {
    const box = chart.getBoundingClientRect();
    const x = Math.max(82, Math.min(722, (clientX - box.left) / box.width * 760));
    const y = Math.max(24, Math.min(352, (clientY - box.top) / box.height * 420));
    if (state.porkchopResult) {
      const departureIndex = Math.round((x - 82) / 640 * (state.porkchopResult.width - 1));
      const durationIndex = Math.round((352 - y) / 328 * (state.porkchopResult.height - 1));
      choosePorkchopCell(departureIndex, durationIndex);
      return;
    }
    showToast('Run the porkchop solver first');
  }
  chart.addEventListener('pointerdown', event => { chart.setPointerCapture(event.pointerId); selectPorkchop(event.clientX, event.clientY); });
  chart.addEventListener('pointermove', event => { if (chart.hasPointerCapture(event.pointerId)) selectPorkchop(event.clientX, event.clientY); });
  $('#lowestDv').addEventListener('click', () => {
    if (state.porkchopResult) choosePorkchopCell(state.porkchopResult.bestDepartureIndex, state.porkchopResult.bestDurationIndex);
    else { showToast('Run the porkchop solver first'); return; }
    showToast('Selected lowest delta-v solution');
  });
  $('#asapTransfer').addEventListener('click', () => {
    if (state.porkchopResult) {
      let bestDuration = 0;
      let bestCost = Infinity;
      for (let y = 0; y < state.porkchopResult.height; y++) {
        const rawCost = state.porkchopResult.costs[y * state.porkchopResult.width];
        const cost = isPorkchopCost(rawCost) ? Number(rawCost) : NaN;
        if (Number.isFinite(cost) && cost < bestCost) { bestCost = cost; bestDuration = y; }
      }
      choosePorkchopCell(0, bestDuration);
    } else { showToast('Run the porkchop solver first'); return; }
    showToast('Selected lowest delta-v solution in the earliest departure column');
  });

  function requestPorkchopSolve() {
    const targetPeriapsis = Number($('.transfer-options input')?.value || 60) * 1000;
    state.porkchopResult = null;
    $('#porkHeatmap').setAttribute('opacity', '0');
    $('#porkDeparture').textContent = 'Solving';
    $('#porkDuration').textContent = '—';
    $('#porkEjection').textContent = 'Generated when node is created';
    $('#porkCapture').textContent = '—';
    $('#porkTotal').textContent = '—';
    if (!sendCommand('mechjeb.porkchop.solve', {
      width: 160,
      height: 200,
      includeCaptureBurn: $('#captureBurn').checked,
      targetPeriapsis
    })) showToast('No live connection; porkchop solver not started');
  }
  $('#resetPorkchop').textContent = 'Recompute';
  $('#resetPorkchop').addEventListener('click', requestPorkchopSolve);
  $('#captureBurn').addEventListener('change', requestPorkchopSolve);

  function createPorkchop(execute) {
    if (!state.porkchopResult) { showToast('Complete the porkchop calculation first'); return; }
    if (!sendCommand('mechjeb.porkchop.create', {
      departureIndex: state.porkchopDepartureIndex,
      durationIndex: state.porkchopDurationIndex,
      replaceLast: Boolean($('.node-mode button:nth-child(2).is-active')),
      execute
    })) { showToast('No live connection; transfer node not created'); return; }
  }
  $('#createPorkchop').addEventListener('click', () => createPorkchop(false));
  $('#createExecutePorkchop').addEventListener('click', () => createPorkchop(true));

  $$('.node-mode button').forEach(button => {
    button.addEventListener('click', () => {
      $$('.node-mode button').forEach(item => item.classList.toggle('is-active', item === button));
    });
  });

  $$('.plan-action').forEach(button => button.addEventListener('click', () => showToast(button.dataset.planAction)));

  $$('.autopilot-tabs button').forEach(button => {
    button.addEventListener('click', () => {
      $$('.autopilot-tabs button').forEach(item => item.classList.toggle('is-active', item === button));
      $$('.auto-panel').forEach(panel => panel.classList.toggle('is-active', panel.dataset.autoView === button.dataset.autoPanel));
    });
  });

  const ascentBindings = {
  "#ascentOrbitAltitude": {
    "field": "orbitAltitudeKm",
    "property": "value"
  },
  "#ascentInclination": {
    "field": "inclination",
    "property": "value"
  },
  "#ascentPath": {
    "field": "ascentPath",
    "property": "value"
  },
  "#ascentLaunchPhaseAngle": {
    "field": "launchPhaseAngle",
    "property": "value"
  },
  "#ascentLaunchLanDifference": {
    "field": "launchLanDifference",
    "property": "value"
  },
  "#ascentAutoThrottle": {
    "field": "autoThrottle",
    "property": "checked"
  },
  "#ascentCorrectiveSteering": {
    "field": "correctiveSteering",
    "property": "checked"
  },
  "#ascentAutoStage": {
    "field": "autoStage",
    "property": "checked"
  },
  "#ascentSkipCircularization": {
    "field": "skipCircularization",
    "property": "checked"
  },
  "#ascentLimitAoA": {
    "field": "limitAoA",
    "property": "checked"
  },
  "#ascentMaxAoA": {
    "field": "maxAoA",
    "property": "value"
  },
  "#ascentLimitQ": {
    "field": "limitQ",
    "property": "checked"
  },
  "#ascentMaxQ": {
    "field": "maxQKpa",
    "property": "value"
  },
  "#ascentForceRoll": {
    "field": "forceRoll",
    "property": "checked"
  },
  "#ascentVerticalRoll": {
    "field": "verticalRoll",
    "property": "value"
  },
  "#ascentTurnRoll": {
    "field": "turnRoll",
    "property": "value"
  },
  "#ascentDesiredLan": {
    "field": "desiredLan",
    "property": "value"
  },
  "#ascentCorrectiveGain": {
    "field": "correctiveSteeringGain",
    "property": "value"
  },
  "#ascentDeploySolar": {
    "field": "deploySolarPanels",
    "property": "checked"
  },
  "#ascentDeployAntennas": {
    "field": "deployAntennas",
    "property": "checked"
  },
  "#ascentRollAltitude": {
    "field": "rollAltitudeKm",
    "property": "value"
  },
  "#ascentAoAFadePressure": {
    "field": "aoaFadeoutKpa",
    "property": "value"
  },
  "#gtStartAltitude": {
    "field": "gtTurnStartAltitudeKm",
    "property": "value"
  },
  "#gtStartVelocity": {
    "field": "gtTurnStartVelocity",
    "property": "value"
  },
  "#gtStartPitch": {
    "field": "gtTurnStartPitch",
    "property": "value"
  },
  "#gtIntermediateAltitude": {
    "field": "gtIntermediateAltitudeKm",
    "property": "value"
  },
  "#gtHoldApTime": {
    "field": "gtHoldApTime",
    "property": "value"
  },
  "#classicAutoPath": {
    "field": "classicAutoPath",
    "property": "checked"
  },
  "#classicStartAltitude": {
    "field": "classicTurnStartAltitudeKm",
    "property": "value"
  },
  "#classicStartVelocity": {
    "field": "classicTurnStartVelocity",
    "property": "value"
  },
  "#classicEndAltitude": {
    "field": "classicTurnEndAltitudeKm",
    "property": "value"
  },
  "#classicEndAngle": {
    "field": "classicTurnEndAngle",
    "property": "value"
  },
  "#classicShapeExponent": {
    "field": "classicTurnShapeExponent",
    "property": "value"
  },
  "#pvgPitchStartVelocity": {
    "field": "pvgPitchStartVelocity",
    "property": "value"
  },
  "#pvgPitchRate": {
    "field": "pvgPitchRate",
    "property": "value"
  },
  "#pvgDesiredApoapsis": {
    "field": "pvgDesiredApoapsisKm",
    "property": "value"
  },
  "#pvgAttachAltitudeEnabled": {
    "field": "pvgAttachAltitudeEnabled",
    "property": "checked"
  },
  "#pvgAttachAltitude": {
    "field": "pvgDesiredAttachAltitudeKm",
    "property": "value"
  },
  "#pvgDynamicPressureTrigger": {
    "field": "pvgDynamicPressureTriggerKpa",
    "property": "value"
  },
  "#pvgStagingTriggerEnabled": {
    "field": "pvgStagingTriggerEnabled",
    "property": "checked"
  },
  "#pvgStagingTrigger": {
    "field": "pvgStagingTrigger",
    "property": "value"
  },
  "#pvgFixedCoast": {
    "field": "pvgFixedCoast",
    "property": "checked"
  },
  "#pvgFixedCoastLength": {
    "field": "pvgFixedCoastLength",
    "property": "value"
  }
};
  const ascentEdits = new Map();
  const ascentServerValues = new Map();
  let ascentLaunchDraft = false;

  function resetAscentField(selector) {
    const edit = ascentEdits.get(selector);
    if (edit?.timer) clearTimeout(edit.timer);
    ascentEdits.delete(selector);
    const input = $(selector);
    const saved = ascentServerValues.get(selector);
    if (saved) input[saved.property] = saved.value;
    input.disabled = false;
  }
  function resetAscentEdits() {
    Array.from(ascentEdits.keys()).forEach(resetAscentField);
  }
  function sameAscentValue(binding, left, right) {
    if (binding.property === 'checked' || binding.field === 'ascentPath') return left === right;
    const digits = String(right).includes('.') ? String(right).split('.')[1].length : 0;
    return Number(Number(left).toFixed(digits)) === Number(right);
  }
  function syncAscentField(selector, property, value) {
    ascentServerValues.set(selector, { property, value });
    const input = $(selector);
    const binding = ascentBindings[selector];
    const edit = ascentEdits.get(selector);
    if (edit?.commandId && edit.acknowledged && sameAscentValue(binding, edit.value, value)) resetAscentField(selector);
    if (!ascentEdits.has(selector) && !(selector === '#ascentLaunchMode' && ascentLaunchDraft)) input[property] = value;
    if (binding) {
      const locked = (selector === '#ascentPath' && Boolean(state.lastAutomation?.ascentEnabled)) ||
        (selector === '#ascentInclination' && Boolean(state.lastAutomation?.ascentTimedLaunch) &&
          state.lastAutomation?.ascentLaunchMode === 'TARGET_PLANE');
      input.disabled = !state.serverCapabilities.has('mechjeb.ascent.setting') || Boolean(ascentEdits.get(selector)?.commandId) || locked;
      input.title = locked ? (selector === '#ascentPath' ? 'Stop ascent guidance before changing the ascent profile.' :
        'Target-plane launch controls the inclination.') : '';
    }
  }
  function acknowledgeAscentSetting(message) {
    for (const [selector, edit] of ascentEdits) {
      if (edit.commandId !== message.commandId) continue;
      if (!message.success) resetAscentField(selector);
      else {
        edit.acknowledged = true;
        // Wait for readback; an acknowledgement is not itself a telemetry value.
      }
    }
  }
  function commitAscentField(selector) {
    const input = $(selector), binding = ascentBindings[selector];
    let edit = ascentEdits.get(selector);
    if (edit?.commandId) return;
    const raw = input[binding.property];
    const baseline = edit?.baseline ?? ascentServerValues.get(selector)?.value;
    if (raw === baseline || (binding.property !== 'checked' && binding.field !== 'ascentPath' &&
      String(raw).trim() && Number(raw) === Number(baseline))) { resetAscentField(selector); return; }
    if (binding.property !== 'checked' && binding.field !== 'ascentPath' &&
      (!String(raw).trim() || !Number.isFinite(Number(raw)))) {
      resetAscentField(selector);
      showToast('Invalid ascent setting value.');
      return;
    }
    if (!state.serverCapabilities.has('mechjeb.ascent.setting')) {
      resetAscentField(selector);
      showToast('Ascent setting updates require a compatible server.');
      return;
    }
    const commandId = sendCommand('mechjeb.ascent.setting', {field: binding.field, value: String(raw)});
    if (!commandId) { resetAscentField(selector); showToast('No live connection; ascent setting not updated.'); return; }
    edit = { commandId, value: raw, acknowledged: false };
    ascentEdits.set(selector, edit);
    input.disabled = true;
    edit.timer = setTimeout(() => {
      if (ascentEdits.get(selector) !== edit) return;
      resetAscentField(selector);
      showToast('Ascent setting readback timed out; showing the latest game value.');
    }, 5000);
  }
  for (const [selector, binding] of Object.entries(ascentBindings)) {
    const input = $(selector);
    if (input.tagName === 'SELECT' || binding.property === 'checked') {
      input.addEventListener('change', () => commitAscentField(selector));
    } else {
      input.addEventListener('focus', () => {
        if (!ascentEdits.has(selector)) ascentEdits.set(selector, {baseline: input.value});
      });
      input.addEventListener('blur', () => commitAscentField(selector));
      input.addEventListener('keydown', event => {
        if (event.key === 'Enter') { event.preventDefault(); commitAscentField(selector); input.blur(); }
        if (event.key === 'Escape') { event.preventDefault(); resetAscentField(selector); input.blur(); }
      });
    }
  }
  // Launch timing is an action prepared locally, not a parameter write that arms a launch.
  $('#ascentLaunchMode').addEventListener('change', () => { ascentLaunchDraft = true; });


  function updateAscentPathPanel() {
    const path = String($('#ascentPath').value || 'GRAVITYTURN').toUpperCase();
    $$('.ascent-path-panel').forEach(panel => panel.classList.toggle('is-active', panel.dataset.ascentPath === path));
    const labels = { GRAVITYTURN: 'GRAVITY TURN', PVG: 'PRIMER VECTOR GUIDANCE', CLASSIC: 'CLASSIC ASCENT' };
    $('#ascentPathSettingsLabel').textContent = labels[path] || path;
    $$('.pvg-target-only').forEach(element => { element.hidden = path !== 'PVG'; });
    $('#ascentOrbitAltitudeLabel').textContent = path === 'PVG' ? 'Target periapsis PE' : 'Target orbit altitude';
    const launchMode = $('#ascentLaunchMode');
    const targetLanOption = Array.from(launchMode.options).find(option => option.value === 'TARGET_LAN');
    const rendezvousOption = Array.from(launchMode.options).find(option => option.value === 'RENDEZVOUS');
    if (targetLanOption) targetLanOption.disabled = path !== 'PVG';
    if (rendezvousOption) rendezvousOption.disabled = path === 'PVG';
    if (launchMode.selectedOptions[0]?.disabled) launchMode.value = 'TARGET_PLANE';
    updateAscentLaunchMode();
  }
  function ascentDraftNumber(selector) {
    return Number(String($(selector)?.value || '').replace('−', '-').replace(/[^0-9+\.\-]/g, ''));
  }

  function estimatedBodyRadiusKm() {
    const orbit = state.regularSample;
    if (!orbit) return 600;
    const semiMajorAxis = Number(orbit.semiMajorAxis) / 1000;
    const apoapsis = Number(orbit.apoapsis) / 1000;
    const periapsis = Number(orbit.periapsis) / 1000;
    const estimate = semiMajorAxis - (apoapsis + periapsis) / 2;
    return Number.isFinite(estimate) && estimate > 1 ? estimate : 600;
  }

  function renderOrbitGeometry(svg, geometry, paths, wholeBody) {
    if (!svg) return;
    const layer = svg.querySelector('.live-orbit-geometry');
    if (!geometry || !(geometry.radius > 0)) {
      layer.innerHTML = '<text x=\"30\" y=\"195\" fill=\"#8ea8ad\">Waiting for actual orbit data</text>';
      return;
    }
    const valid = p => Array.isArray(p) && p.length >= 3 && p.every(Number.isFinite);
    paths = paths.map(points => points.filter(valid));
    const hasPaths = paths.some(points => points.length > 1);
    const dot = (a,b) => a.reduce((sum,v,i) => sum + v*b[i],0);
    const cross = (a,b) => [a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
    const unit = (p, fallback) => { const n = Math.hypot(...p); return n > 1e-8 ? p.map(v=>v/n) : fallback; };
    const current = (geometry.current || []).filter(valid);
    const normal = current.length > 2 ? unit(cross(current[0], current[1]),[0,1,0]) : [0,1,0];
    const right = unit(cross(normal,[0,1,0]),[1,0,0]);
    const up = unit(cross(normal,right),[0,0,1]);
    const project = p => [dot(p,right), .88*dot(p,up)+.475*dot(p,normal)];
    const projected = paths.map(points=>points.map(project));
    const bounds = projected.flat();
    const craft = valid(geometry.craft) ? project(geometry.craft) : [0,0];
    bounds.push(craft);
    if (wholeBody || !hasPaths) bounds.push([-geometry.radius,-geometry.radius],[geometry.radius,geometry.radius]);
    const minX = Math.min(...bounds.map(p=>p[0])), maxX = Math.max(...bounds.map(p=>p[0]));
    const minY = Math.min(...bounds.map(p=>p[1])), maxY = Math.max(...bounds.map(p=>p[1]));
    const scale = Math.min(680/Math.max(1,maxX-minX),290/Math.max(1,maxY-minY));
    const screen = p => [380+(p[0]-(minX+maxX)/2)*scale,195-(p[1]-(minY+maxY)/2)*scale];
    const center = screen([0,0]);
    let markup = `<circle cx="${center[0]}" cy="${center[1]}" r="${geometry.radius*scale}" fill="#173741" stroke="#386571"/>`;
    projected.forEach((points,index)=> {
      if (points.length < 2) return;
      const d=points.map((p,i)=>`${i?'L':'M'}${screen(p).map(v=>v.toFixed(2)).join(' ')}`).join(' ');
      markup += `<path d="${d}" fill="none" stroke="${wholeBody && index===0?'#67d8d4':'#ffcf73'}" stroke-width="2.5" ${wholeBody && index>0?'stroke-dasharray="7 4"':''}/>`;
    });
    const craftXY=screen(craft);
    markup += `<circle cx="${craftXY[0]}" cy="${craftXY[1]}" r="4" fill="#fff"/>`;
    if(wholeBody && valid(geometry.node)) {
      const node=screen(project(geometry.node));
      markup += `<circle cx="${node[0]}" cy="${node[1]}" r="7" fill="none" stroke="#ffcf73" stroke-width="2"/>`;
    }
    markup += `<text x="20" y="325" fill="#c9d8da">${wholeBody ? 'Cyan: current · Dashed gold: after first node · White: vessel' : 'Gold: Trajectories prediction · White: vessel'}</text>`;
    markup += '<text x=\"20\" y=\"342\" fill=\"#8ea8ad\">Current body only · oblique orbital projection · uniform auto scale</text>';
    if (!hasPaths) markup += '<text x=\"20\" y=\"359\" fill=\"#c9d8da\">No trajectory samples to draw</text>';
    else if (wholeBody && !geometry.plannedAvailable) markup += '<text x=\"20\" y=\"350\" fill=\"#c9d8da\">No post-node orbit around the same body</text>';
    layer.innerHTML=markup;
  }

  function renderAscentTargetOrbit(model) {
    const periapsisKm = Math.max(0, Number(model.periapsisKm) || 0);
    const apoapsisKm = Math.max(periapsisKm, Number(model.apoapsisKm) || periapsisKm);
    const bodyRadiusKm = Number(state.orbitGeometry?.radius) / 1000;
    $('#ascentTargetBody').style.opacity = bodyRadiusKm > 0 ? '1' : '0';
    $('.ascent-target-focus').style.opacity = bodyRadiusKm > 0 ? '1' : '0';
    $('#ascentTargetOrbitGeometry').style.opacity = bodyRadiusKm > 0 ? '1' : '0';
    if (!(bodyRadiusKm > 0)) return;
    const radiusAtPe = bodyRadiusKm + periapsisKm;
    const radiusAtAp = bodyRadiusKm + apoapsisKm;
    const semiMajor = (radiusAtAp + radiusAtPe) / 2;
    const focusOffset = (radiusAtAp - radiusAtPe) / 2;
    const semiMinor = Math.sqrt(Math.max(1, radiusAtAp * radiusAtPe));
    const focusY = 80;
    const pixelsPerKm = 66 / Math.max(1, radiusAtAp);
    const radiusX = semiMinor * pixelsPerKm;
    const radiusY = semiMajor * pixelsPerKm;
    const centerY = focusY - focusOffset * pixelsPerKm;
    const apoapsisY = centerY - radiusY;
    const periapsisY = centerY + radiusY;
    const bodyRadius = bodyRadiusKm * pixelsPerKm;
    const inclination = Number.isFinite(Number(model.inclination)) ? Number(model.inclination) : 0;

    const ellipse = $('#ascentTargetOrbitEllipse');
    ellipse.setAttribute('cy', centerY.toFixed(2));
    ellipse.setAttribute('rx', radiusX.toFixed(2));
    ellipse.setAttribute('ry', radiusY.toFixed(2));
    $('#ascentTargetApsisAxis').setAttribute('y1', apoapsisY.toFixed(2));
    $('#ascentTargetApsisAxis').setAttribute('y2', Math.min(158, periapsisY).toFixed(2));
    $('#ascentTargetApMarker').setAttribute('cy', apoapsisY.toFixed(2));
    $('#ascentTargetApLabel').setAttribute('y', Math.max(10, apoapsisY + 3).toFixed(2));
    $('#ascentTargetApLabel').textContent = `AP ${apoapsisKm.toFixed(0)}`;
    const peVisible = periapsisY <= 154;
    $('#ascentTargetPeMarker').style.display = peVisible ? '' : 'none';
    $('#ascentTargetPeMarker').setAttribute('cy', Math.min(154, periapsisY).toFixed(2));
    $('#ascentTargetPeLabel').setAttribute('y', Math.min(151, periapsisY - 5).toFixed(2));
    $('#ascentTargetPeLabel').textContent = peVisible ? `PE ${periapsisKm.toFixed(0)}` : `PE ${periapsisKm.toFixed(0)} ↓`;
    $('#ascentTargetBody').setAttribute('r', bodyRadius.toFixed(2));
    $('#ascentTargetBody').setAttribute('cy', String(focusY));
    $('.ascent-target-focus').setAttribute('cy', String(focusY));
    $('#ascentTargetOrbit').setAttribute('aria-label', `Target orbit: periapsis ${periapsisKm.toFixed(1)} km, apoapsis ${apoapsisKm.toFixed(1)} km, inclination ${inclination.toFixed(1)} degrees`);
  }


  function renderAscentOrbitReadouts(message) {
    const km = value => Number.isFinite(value) ? `${(value / 1000).toFixed(1)} km` : '—';
    const duration = value => Number.isFinite(value) && value >= 0 ? formatDuration(value) : 'N/A';
    $('#ascentActualAp').textContent = km(message.apoapsis);
    $('#ascentActualPe').textContent = km(message.periapsis);
    $('#ascentActualPe').classList.toggle('is-negative', message.periapsis < 0);
    $('#ascentActualTimeAp').textContent = duration(message.timeToApoapsis);
    $('#ascentActualTimePe').textContent = duration(message.timeToPeriapsis);
  }

  function captureAscentSpatialHistory(vesselId, geometry) {
    if (!geometry || !vesselId || !Array.isArray(geometry.craft)) {
      state.ascentSpatialHistory = null;
      return;
    }
    const key = `${vesselId}:${geometry.radius}`;
    let history = state.ascentSpatialHistory;
    if (!history || history.key !== key || geometry.ut < history.ut) {
      history = state.ascentSpatialHistory = {key, ut: -Infinity, points: [], bounds: null};
    }
    if (geometry.ut <= history.ut) return;
    history.ut = geometry.ut;
    if (geometry.craft.length === 3 && geometry.craft.every(Number.isFinite)) {
      history.points.push(geometry.craft.slice());
      if (history.points.length > 2400) history.points.shift();
    }
  }

  function renderAscentProfile(model) {
    state.ascentVisual = {periapsisKm: Number(model.periapsisKm), apoapsisKm: Number(model.apoapsisKm)};
    updateAscentFlightProgress(state.currentSample?.data.altitude);
  }

  function updateAscentFlightProgress(altitudeMeters) {
    const svg = $('#ascentProfileChart'), geometry = state.orbitGeometry;
    if (!svg) return;
    const valid = p => Array.isArray(p) && p.length === 3 && p.every(Number.isFinite);
    if (!geometry || !(geometry.radius > 0) || !valid(geometry.craft)) {
      svg.innerHTML = '<text x=\"24\" y=\"190\" fill=\"#8ea8ad\">Waiting for spatial coordinates</text>';
      state.ascentSpatialRenderKey = null;
      return;
    }
    const renderKey = `${state.ascentSpatialHistory?.key}:${geometry.ut}:${JSON.stringify(state.ascentVisual)}`;
    if (state.ascentSpatialRenderKey === renderKey) return;
    state.ascentSpatialRenderKey = renderKey;
    // Orbit-plane orthographic projection: one physical scale for both axes.
    const dot = (a,b) => a.reduce((sum,v,i) => sum + v*b[i],0);
    const cross = (a,b) => [a[1]*b[2]-a[2]*b[1],a[2]*b[0]-a[0]*b[2],a[0]*b[1]-a[1]*b[0]];
    const unit = p => {const n=Math.hypot(...p); return n>1e-9 ? p.map(v=>v/n) : null;};
    const predicted = (geometry.current || []).filter(valid);
    const history = state.ascentSpatialHistory;
    const flown = history?.points || [];
    const radial = unit(geometry.craft);
    let normal = predicted.length > 1 ? unit(cross(predicted[0],predicted[1])) : null;
    if (!normal && flown.length > 1) normal = unit(cross(flown[flown.length-2],geometry.craft));
    normal ||= unit(cross(radial,Math.abs(radial[1])<.9 ? [0,1,0] : [1,0,0]));
    // Anchor the camera to the first observed position; do not pin the craft to the right edge.
    if (history && !history.up) history.up = radial;
    const anchor = history?.up || radial;
    const up = unit(anchor.map((v,i)=>v-dot(anchor,normal)*normal[i])) || radial;
    const right = unit(cross(normal,up)) || [1,0,0];
    const project = p => [dot(p,right),dot(p,up)];
    const current = project(geometry.craft);
    const all = [...flown,...predicted,geometry.craft].map(project);
    all.push([current[0] * geometry.radius / Math.hypot(...geometry.craft),
      current[1] * geometry.radius / Math.hypot(...geometry.craft)]);
    const minSpan = Math.max(10000,geometry.radius*.02);
    const x0=Math.min(...all.map(p=>p[0])), x1=Math.max(...all.map(p=>p[0]));
    const y0=Math.min(...all.map(p=>p[1])), y1=Math.max(...all.map(p=>p[1]));
    // Quantized scale leaves breathing room; only zoom out when the data exceeds the view.
    const needed=Math.max(minSpan,(x1-x0)*1.25,(y1-y0)*1.25*700/290);
    const span=Math.max(history?.span || 0,Math.pow(2,Math.ceil(Math.log2(needed))));
    if (history) history.span=span;
    const cx=(x0+x1)/2, cy=(y0+y1)/2, scale=700/span;
    const screen = p => [380+(p[0]-cx)*scale,190-(p[1]-cy)*scale];
    const center=screen([0,0]), craft=screen(current);
    const path = points => points.map((p,i)=>`${i?'L':'M'}${screen(project(p)).map(v=>v.toFixed(2)).join(' ')}`).join(' ');
    let markup=`<circle cx="${center[0]}" cy="${center[1]}" r="${geometry.radius*scale}" fill="#173741" stroke="#789496" stroke-width="1.5"/>`;
    const target=state.ascentVisual || {};
    for (const [label,altitude] of [['PE',target.periapsisKm],['AP',target.apoapsisKm]]) {
      if (!Number.isFinite(altitude) || altitude<0) continue;
      markup+=`<circle cx="${center[0]}" cy="${center[1]}" r="${(geometry.radius+altitude*1000)*scale}" fill="none" stroke="#ffcf73" stroke-width="1" stroke-dasharray="5 6" opacity=".55" data-target="${label}"/>`;
    }
    if(predicted.length>1) markup+=`<path data-series="coast" d="${path(predicted)}" fill="none" stroke="#99bafa" stroke-width="2" stroke-dasharray="8 5"/>`;
    if(flown.length>1) markup+=`<path data-series="flown" d="${path(flown)}" fill="none" stroke="#67d8d4" stroke-width="2.5"/>`;
    markup+=`<circle cx="${craft[0]}" cy="${craft[1]}" r="5" fill="#fff" stroke="#081b21" stroke-width="2"/>`;
    markup+=`<text x="24" y="24" fill="#d6e6e8">Altitude ${((Math.hypot(...geometry.craft)-geometry.radius)/1000).toFixed(1)} km · White marker shows position, not attitude</text>`;
    markup+=`<path d="M24 337h100m-100-4v8m100-8v8" stroke="#bcd0d4"/><text x="24" y="359" fill="#bcd0d4">${(100/scale/1000).toFixed(1)} km · Equal horizontal and vertical scale</text>`;
    if(flown.length<2) markup+='<text x=\"400\" y=\"359\" fill=\"#8ea8ad\">Recording observed flight path…</text>';
    svg.innerHTML=markup;
  }

  function renderAscentVisuals(model) {
    renderAscentTargetOrbit(model);
    renderAscentProfile(model);
  }

  function renderAscentDraft() {
    const path = String($('#ascentPath').value || 'GRAVITYTURN').toUpperCase();
    const altitude = ascentDraftNumber('#ascentOrbitAltitude');
    const apoapsis = ascentDraftNumber('#pvgDesiredApoapsis');
    const inclination = ascentDraftNumber('#ascentInclination');
    const pathLabels = { GRAVITYTURN: 'GRAVITY TURN', PVG: 'PRIMER VECTOR GUIDANCE', CLASSIC: 'CLASSIC ASCENT' };
    const validAltitude = Number.isFinite(altitude) ? altitude : 0;
    const validApoapsis = Number.isFinite(apoapsis) ? apoapsis : 0;
    $('#ascentOrbitBadge').textContent = path === 'PVG' ? `PE ${validAltitude.toFixed(0)} / AP ${validApoapsis.toFixed(0)} km` : `${validAltitude.toFixed(0)} km`;
    $('#ascentReadoutAltitude').textContent = path === 'PVG' ? `PE ${validAltitude.toFixed(1)} · AP ${validApoapsis.toFixed(1)} km` : `${validAltitude.toFixed(1)} km`;
    $('#ascentReadoutInclination').textContent = Number.isFinite(inclination) ? `${inclination.toFixed(1)}°` : '—';
    $('#ascentReadoutPath').textContent = pathLabels[path] || path;
    const launchModeLabels = {
      IMMEDIATE: 'Launch now', COUNTDOWN: 'Manual countdown', TARGET_PLANE: 'Launch into target plane',
      TARGET_LAN: 'Launch to target LAN', RENDEZVOUS: 'Launch to rendezvous'
    };
    const launchMode = String($('#ascentLaunchMode').value || 'IMMEDIATE').toUpperCase();
    $('#ascentReadoutLaunchMode').textContent = launchModeLabels[launchMode] || launchMode;
    renderAscentVisuals({
      path, periapsisKm: validAltitude, apoapsisKm: path === 'PVG' ? validApoapsis : validAltitude,
      inclination, enabled: Boolean(state.lastAutomation?.ascentEnabled),
      gtStartAltitudeKm: ascentDraftNumber('#gtStartAltitude'),
      gtIntermediateAltitudeKm: ascentDraftNumber('#gtIntermediateAltitude'),
      gtHoldApTime: ascentDraftNumber('#gtHoldApTime'),
      classicStartAltitudeKm: ascentDraftNumber('#classicStartAltitude'),
      classicEndAltitudeKm: ascentDraftNumber('#classicEndAltitude'),
      classicEndAngle: ascentDraftNumber('#classicEndAngle'),
      classicShapeExponent: ascentDraftNumber('#classicShapeExponent'),
      pvgPitchStartVelocity: ascentDraftNumber('#pvgPitchStartVelocity'),
      pvgPitchRate: ascentDraftNumber('#pvgPitchRate'),
      pvgAttachAltitudeKm: ascentDraftNumber('#pvgAttachAltitude')
    });
  }
  $('#ascentPath').addEventListener('change', () => { updateAscentPathPanel(); });
  function updateAscentLaunchMode() {
    const mode = String($('#ascentLaunchMode').value || 'IMMEDIATE').toUpperCase();
    $('#ascentCountdownField').hidden = mode !== 'COUNTDOWN';
    $$('.target-launch-option').forEach(label => {
      label.hidden = !String(label.dataset.launchOption || '').split(/\s+/).includes(mode);
    });
  }
  $('#ascentLaunchMode').addEventListener('change', () => { updateAscentLaunchMode(); });
  updateAscentPathPanel();
  updateAscentLaunchMode();
  renderAscentDraft();

  function ascentParameters() {
    const numeric = id => Number(String($(id).value).replace('−', '-').replace(/[^0-9+\.\-]/g, ''));
    return {
      orbitAltitudeKm: numeric('#ascentOrbitAltitude'),
      inclination: numeric('#ascentInclination'),
      ascentPath: $('#ascentPath').value,
      launchMode: $('#ascentLaunchMode').value,
      launchPhaseAngle: numeric('#ascentLaunchPhaseAngle'),
      launchLanDifference: numeric('#ascentLaunchLanDifference'),
      autoThrottle: $('#ascentAutoThrottle').checked,
      correctiveSteering: $('#ascentCorrectiveSteering').checked,
      autoStage: $('#ascentAutoStage').checked,
      skipCircularization: $('#ascentSkipCircularization').checked,
      limitAoA: $('#ascentLimitAoA').checked,
      maxAoA: numeric('#ascentMaxAoA'),
      limitQ: $('#ascentLimitQ').checked,
      maxQKpa: numeric('#ascentMaxQ'),
      forceRoll: $('#ascentForceRoll').checked,
      verticalRoll: numeric('#ascentVerticalRoll'),
      turnRoll: numeric('#ascentTurnRoll'),
      desiredLan: numeric('#ascentDesiredLan'),
      correctiveSteeringGain: numeric('#ascentCorrectiveGain'),
      deploySolarPanels: $('#ascentDeploySolar').checked,
      deployAntennas: $('#ascentDeployAntennas').checked,
      rollAltitudeKm: numeric('#ascentRollAltitude'),
      aoaFadeoutKpa: numeric('#ascentAoAFadePressure'),
      gtTurnStartAltitudeKm: numeric('#gtStartAltitude'),
      gtTurnStartVelocity: numeric('#gtStartVelocity'),
      gtTurnStartPitch: numeric('#gtStartPitch'),
      gtIntermediateAltitudeKm: numeric('#gtIntermediateAltitude'),
      gtHoldApTime: numeric('#gtHoldApTime'),
      classicAutoPath: $('#classicAutoPath').checked,
      classicTurnStartAltitudeKm: numeric('#classicStartAltitude'),
      classicTurnStartVelocity: numeric('#classicStartVelocity'),
      classicTurnEndAltitudeKm: numeric('#classicEndAltitude'),
      classicTurnEndAngle: numeric('#classicEndAngle'),
      classicTurnShapeExponent: numeric('#classicShapeExponent'),
      pvgPitchStartVelocity: numeric('#pvgPitchStartVelocity'),
      pvgPitchRate: numeric('#pvgPitchRate'),
      pvgDesiredApoapsisKm: numeric('#pvgDesiredApoapsis'),
      pvgAttachAltitudeEnabled: $('#pvgAttachAltitudeEnabled').checked,
      pvgDesiredAttachAltitudeKm: numeric('#pvgAttachAltitude'),
      pvgDynamicPressureTriggerKpa: numeric('#pvgDynamicPressureTrigger'),
      pvgStagingTriggerEnabled: $('#pvgStagingTriggerEnabled').checked,
      pvgStagingTrigger: numeric('#pvgStagingTrigger'),
      pvgFixedCoast: $('#pvgFixedCoast').checked,
      pvgFixedCoastLength: numeric('#pvgFixedCoastLength'),
      countdown: Math.max(0, numeric('#ascentCountdown') || 0)
    };
  }

  $('#startAscent').addEventListener('click', () => {
    if (!state.serverCapabilities.has('mechjeb.ascent.setting')) {
      showToast('Ascent setting updates require a compatible server.'); return;
    }
    if (Array.from(ascentEdits.values()).some(edit => edit.commandId)) {
      showToast('Wait for ascent settings to be confirmed.'); return;
    }
    const countdown = Number($('#ascentCountdown').value);
    if (!Number.isFinite(countdown) || countdown < 0 || countdown > 86400) {
      showToast('Launch countdown must be between 0 and 86,400 seconds.'); return;
    }
    if (sendCommand('mechjeb.ascent.start', {useCurrentSettings: true, launchMode: $('#ascentLaunchMode').value, countdown})) {
      ascentLaunchDraft = false;
    } else showToast('No live connection; ascent not started');
  });

  [$('#autoWarpPlanner'), $('#autoWarpAutopilot')].forEach(input => input?.addEventListener('change', () => {
    const enabled = input.checked;
    [$('#autoWarpPlanner'), $('#autoWarpAutopilot')].forEach(peer => { if (peer) peer.checked = enabled; });
    if (!sendCommand('mechjeb.autowarp.set', { enabled })) showToast('No live connection; auto-warp not updated');
  }));

  $('#startRendezvous').addEventListener('click', () => {
    const numeric = id => Number(String($(id).value).replace('−', '-').replace(/[^0-9+\.\-]/g, ''));
    const parameters = {
      desiredDistance: numeric('#rendezvousDistance'),
      maxPhasingOrbits: numeric('#rendezvousPhasingOrbits'),
      maxClosingSpeed: numeric('#rendezvousClosingSpeed')
    };
    if (!(parameters.desiredDistance > 0) || !(parameters.maxPhasingOrbits >= 1) || !(parameters.maxClosingSpeed > 0)) {
      showToast('Check rendezvous distance, phasing orbits and approach speed');
      return;
    }
    if (!sendCommand('mechjeb.rendezvous.start', parameters)) showToast('No live connection; rendezvous not started');
  });

  const smartAssGroups = {
    'Orbit': new Set(['Off', 'Kill rotation', 'Nodes', 'Prograde', 'Retrograde', 'Normal +', 'Normal −', 'Radial out', 'Radial in']),
    'Surface': new Set(['Off', 'Kill rotation', 'Surface', 'Surface prograde', 'Surface retrograde', 'Horizontal +', 'Horizontal −', 'Up']),
    'Target': new Set(['Off', 'Kill rotation', 'Relative velocity +', 'Relative velocity −', 'Target +', 'Target −', 'Parallel +', 'Parallel −'])
  };
  function applySmartAssGroup(name) {
    const allowed = smartAssGroups[name] || smartAssGroups['Orbit'];
    $$('.attitude-grid button').forEach(button => { button.hidden = !allowed.has(sourceText(button).trim()); });
    const selected = $('.attitude-grid button.is-active:not([hidden])');
    if (!selected) {
      const fallback = $$('.attitude-grid button:not([hidden])').find(button => !button.classList.contains('attitude-off'));
      $$('.attitude-grid button').forEach(button => button.classList.toggle('is-active', button === fallback));
    }
  }
  $$('.reference-tabs button').forEach(button => {
    button.addEventListener('click', () => {
      $$('.reference-tabs button').forEach(item => item.classList.toggle('is-active', item === button));
      applySmartAssGroup(sourceText(button).trim());
    });
  });
  applySmartAssGroup('Orbit');

  function syncSmartAssOffsets(refreshValues = false) {
    const selected = sourceText($('.attitude-grid button.is-active')).trim();
    const supportsPitchYaw = ['Surface', 'Surface prograde', 'Surface retrograde'].includes(selected);
    $$('.attitude-offset input').slice(0, 2).forEach(input => {
      input.disabled = !supportsPitchYaw;
      input.title = supportsPitchYaw ? '' : 'This Smart A.S.S. mode only supports roll offset';
      input.closest('.smart-offset-control')?.querySelectorAll('button').forEach(button => { button.disabled = !supportsPitchYaw; });
    });
    const telemetry = state.smartAssTelemetry;
    if (refreshValues && telemetry) {
      const keys = selected === 'Surface' ? ['smartAssSurfacePitch', 'smartAssSurfaceYaw', 'smartAssSurfaceRoll']
        : ['Surface prograde', 'Surface retrograde'].includes(selected) ? ['smartAssVelocityPitch', 'smartAssVelocityYaw', 'smartAssVelocityRoll']
        : [null, null, 'smartAssRoll'];
      $$('.attitude-offset input').forEach((input, index) => {
        if (document.activeElement === input) return;
        const value = keys[index] ? telemetry[keys[index]] : 0;
        if (Number.isFinite(value)) input.value = `${value.toFixed(1)}°`;
      });
    }
  }
  syncSmartAssOffsets();

  $$('.attitude-grid button').forEach(button => {
    button.addEventListener('click', () => {
      $$('.attitude-grid button').forEach(item => item.classList.toggle('is-active', item === button));
      state.smartAssDraft = true;
      state.smartAssPendingTarget = null;
      state.smartAssPendingCommandId = null;
      syncSmartAssOffsets(true);
      showToast(`Selected Smart A.S.S.:${sourceText(button).trim()}`);
    });
  });

  const smartAssTargets = {
    'Off': 'OFF', 'Kill rotation': 'KILLROT', 'Nodes': 'NODE', 'Surface': 'SURFACE',
    'Prograde': 'PROGRADE', 'Retrograde': 'RETROGRADE', 'Normal +': 'NORMAL_PLUS', 'Normal −': 'NORMAL_MINUS',
    'Radial out': 'RADIAL_PLUS', 'Radial in': 'RADIAL_MINUS', 'Relative velocity +': 'RELATIVE_PLUS', 'Relative velocity −': 'RELATIVE_MINUS',
    'Target +': 'TARGET_PLUS', 'Target −': 'TARGET_MINUS', 'Parallel +': 'PARALLEL_PLUS', 'Parallel −': 'PARALLEL_MINUS',
    'Surface prograde': 'SURFACE_PROGRADE', 'Surface retrograde': 'SURFACE_RETROGRADE', 'Horizontal +': 'HORIZONTAL_PLUS',
    'Horizontal −': 'HORIZONTAL_MINUS', 'Up': 'VERTICAL_PLUS'
  };

  function executeSmartAss(requireAttitudeTarget = false) {
    const selected = sourceText($('.attitude-grid button.is-active')).trim();
    const target = smartAssTargets[selected];
    if (!target || (requireAttitudeTarget && target === 'OFF')) {
      showToast('Select a Smart A.S.S. attitude first');
      return false;
    }
    const offsets = $$('.attitude-offset input').map(input => Number(String(input.value).replace('−', '-').replace(/[^0-9+\.\-]/g, '')) || 0);
    const commandId = target === 'OFF' ? sendCommand('mechjeb.smartass.off') : sendCommand('mechjeb.smartass.set', {
      target, pitchOffset: offsets[0], yawOffset: offsets[1], rollOffset: offsets[2],
      autoDisable: Boolean($('.smartass-disable input')?.checked)
    });
    if (commandId) {
      state.smartAssDraft = true;
      state.smartAssPendingTarget = target;
      state.smartAssPendingCommandId = commandId;
      return true;
    } else {
      showToast('No live connection; Smart A.S.S. command not sent');
      return false;
    }
  }

  $$('.smart-offset-step button').forEach(button => {
    button.addEventListener('click', () => {
      state.smartAssOffsetStep = Number(button.dataset.smartStep) || 1;
      $$('.smart-offset-step button').forEach(item => {
        const selected = item === button;
        item.classList.toggle('is-active', selected);
        item.setAttribute('aria-pressed', selected ? 'true' : 'false');
      });
    });
  });

  $$('.smart-offset-control button').forEach(button => {
    button.addEventListener('click', () => {
      const input = button.closest('.smart-offset-control')?.querySelector('input');
      if (!input || input.disabled) return;
      const current = Number(String(input.value).replace('−', '-').replace(/[^0-9+\.\-]/g, '')) || 0;
      const delta = Number(button.dataset.smartOffsetDelta) || 0;
      const next = current + delta * state.smartAssOffsetStep;
      input.value = `${next.toFixed(1)}°`;
      if (executeSmartAss(true)) showToast(`Attitude offsets updated:${input.id.replace('smart', '').replace('Offset', '')} ${input.value}`);
    });
  });

  $('#executeSmartAss').addEventListener('click', () => executeSmartAss(false));
  $$('.attitude-offset input').forEach(input => input.addEventListener('input', () => {
    state.smartAssDraft = true;
  }));

  function landingParameters() {
    return {
      deployGears: $('#landingDeployGears').checked,
      gearStageLimit: Number($('#landingGearStageLimit').value) || 0,
      deployChutes: $('#landingDeployChutes').checked,
      chuteStageLimit: Number($('#landingChuteStageLimit').value) || 0,
      rcsAdjustment: $('#landingRcsAdjustment').checked,
      touchdownSpeed: Number($('#touchdownSpeed').value) || .5,
      predictionsEnabled: $('#landingPredictionsEnabled').checked,
      makeAerobrakeNodes: $('#landingAerobrakeNodes').checked,
      showTrajectory: $('#landingShowTrajectory').checked,
      worldTrajectory: $('#landingWorldTrajectory').checked,
      cameraTrajectory: $('#landingCameraTrajectory').checked
    };
  }
  function updateLandingOptionAvailability() {
    $('#landingGearStageLimit').disabled = !$('#landingDeployGears').checked;
    $('#landingChuteStageLimit').disabled = !$('#landingDeployChutes').checked;
    const predictionOptionsEnabled = $('#landingPredictionsEnabled').checked;
    ['#landingAerobrakeNodes', '#landingShowTrajectory', '#landingWorldTrajectory', '#landingCameraTrajectory']
      .forEach(selector => { $(selector).disabled = !predictionOptionsEnabled; });
  }
  function configureLanding() {
    updateLandingOptionAvailability();
    if (!sendCommand('mechjeb.landing.configure', landingParameters(), true)) showToast('No live connection; landing settings not updated');
  }
  [
    '#landingDeployGears', '#landingGearStageLimit', '#landingDeployChutes', '#landingChuteStageLimit',
    '#landingRcsAdjustment', '#touchdownSpeed', '#landingPredictionsEnabled', '#landingAerobrakeNodes',
    '#landingShowTrajectory', '#landingWorldTrajectory', '#landingCameraTrajectory'
  ].forEach(selector => $(selector)?.addEventListener('change', configureLanding));
  function landingCoordinates() {
    const inputs = $$('[data-auto-view="landing"] .target-coordinates input');
    const parseCoordinate = value => Number(String(value).replace('−', '-').replace(/[^0-9+\.\-]/g, ''));
    return { latitude: parseCoordinate(inputs[0]?.value), longitude: parseCoordinate(inputs[1]?.value) };
  }
  $('#setLandingTarget').addEventListener('click', () => {
    if (!sendCommand('mechjeb.landing.setTarget', landingCoordinates())) showToast('No live connection; landing target not set');
  });
  $('#landUntargeted').addEventListener('click', () => {
    if (!sendCommand('mechjeb.landing.untargeted', landingParameters())) showToast('No live connection; landing not started');
  });
  $('#landAtTarget').addEventListener('click', () => {
    if (!sendCommand('mechjeb.landing.setTarget', landingCoordinates()) || !sendCommand('mechjeb.landing.target', landingParameters())) showToast('No live connection; targeted landing not started');
  });
  function dockingParameters() {
    const numeric = selector => Number(String($(selector)?.value || '').replace('−', '-').replace(/[^0-9+\.\-]/g, ''));
    return {
      speedLimit: numeric('#dockingSpeedLimit'),
      overrideSafeDistance: $('#dockingOverrideSafeDistance').checked,
      safeDistance: numeric('#dockingSafeDistance'),
      overrideStartDistance: $('#dockingOverrideStartDistance').checked,
      startDistance: numeric('#dockingStartDistance'),
      forceRoll: $('#dockingForceRoll').checked,
      roll: numeric('#dockingRoll'),
      drawBoundingBox: $('#dockingDrawBoundingBox').checked
    };
  }
  function updateDockingOptionAvailability() {
    $('#dockingSafeDistance').disabled = !$('#dockingOverrideSafeDistance').checked;
    $('#dockingStartDistance').disabled = !$('#dockingOverrideStartDistance').checked;
    $('#dockingRoll').disabled = !$('#dockingForceRoll').checked;
  }
  function configureDocking() {
    updateDockingOptionAvailability();
    if (!sendCommand('mechjeb.docking.configure', dockingParameters(), true)) showToast('No live connection; docking settings not updated');
  }
  [
    '#dockingSpeedLimit', '#dockingSafeDistance', '#dockingStartDistance', '#dockingRoll',
    '#dockingOverrideSafeDistance', '#dockingOverrideStartDistance', '#dockingForceRoll', '#dockingDrawBoundingBox'
  ].forEach(selector => $(selector)?.addEventListener('change', configureDocking));
  $('#startDocking').addEventListener('click', () => {
    if (!sendCommand('mechjeb.docking.start', dockingParameters())) showToast('No live connection; docking not started');
  });

  $$('[data-command]').forEach(button => button.addEventListener('click', () => {
    if (button.classList.contains('danger-button')) {
      if (button.dataset.confirmArmed !== 'true') {
        button.dataset.confirmArmed = 'true';
        button.dataset.originalText = sourceText(button);
        button.textContent = 'Click again to confirm';
        clearTimeout(button._confirmTimer);
        button._confirmTimer = setTimeout(() => {
          button.dataset.confirmArmed = 'false';
          button.textContent = button.dataset.originalText;
        }, 3000);
        return;
      }
      clearTimeout(button._confirmTimer);
      button.dataset.confirmArmed = 'false';
      button.textContent = button.dataset.originalText;
    }
    if (!sendCommand(button.dataset.command)) showToast('No live connection; command not sent');
  }));

  $$('.auto-action').forEach(button => button.addEventListener('click', () => showToast(button.dataset.autoAction)));

  renderRecorderGroupUi();

  $$('.axis-toggle button').forEach(button => {
    button.addEventListener('click', () => {
      $$('.axis-toggle button').forEach(item => item.classList.toggle('is-active', item === button));
      state.recorderDirty = true;
      state.recorderRenderedAt = 0;
      showToast(button.dataset.axis === 'range' ? 'X axis set to downrange' : 'X axis set to flight time');
    });
  });

  $('#stageLines').addEventListener('change', event => {
    $('#stageLineGroup').style.display = event.target.checked ? '' : 'none';
  });

  $$('#recorderGroups button').forEach(button => button.addEventListener('click', () => {
    state.recorderGroup = button.dataset.recorderGroup;
    renderRecorderGroupUi();
    state.recorderDirty = true;
    state.recorderRenderedAt = 0;
  }));

  $('#toggleAllRecorder').addEventListener('click', () => {
    const fields = recorderGroups[state.recorderGroup].fields;
    const visible = fields.filter(field => !state.recorderHiddenFields.has(field));
    if (visible.length > 1) fields.slice(1).forEach(field => state.recorderHiddenFields.add(field));
    else fields.forEach(field => state.recorderHiddenFields.delete(field));
    renderRecorderGroupUi();
    state.recorderDirty = true;
    state.recorderRenderedAt = 0;
  });

  const recorderTouchTarget = $('.chart-touch-target');
  recorderTouchTarget.addEventListener('pointerdown', event => {
    recorderTouchTarget.setPointerCapture(event.pointerId);
    selectRecorderSampleAt(event.clientX);
  });
  recorderTouchTarget.addEventListener('pointermove', event => {
    if (recorderTouchTarget.hasPointerCapture(event.pointerId)) selectRecorderSampleAt(event.clientX);
  });
  $('#recorderLatest').addEventListener('click', resetRecorderCursor);

  $('#pauseButton').addEventListener('click', event => {
    state.paused = !state.paused;
    event.currentTarget.textContent = state.paused ? '▶ Resume' : 'Ⅱ Pause';
    $('#recState').textContent = state.paused ? 'Display paused' : 'Recording';
    $('.rec-dot').style.animationPlayState = state.paused ? 'paused' : 'running';
    if (!state.paused) fetchRecorderHistory();
    showToast(state.paused ? 'Chart paused; in-game recording continues' : 'Chart resumed and missing samples synchronized');
  });

  $('#clearRecorder').addEventListener('click', () => {
    if (!sendCommand('mechjeb.recorder.clear')) showToast('No live connection; recording not cleared');
  });
  $('#exportRecorder').addEventListener('click', () => {
    const link = document.createElement('a');
    link.href = apiUrl('/api/v1/recorder.csv');
    link.download = `ArmorControl-FlightRecorder-${new Date().toISOString().replace(/[:.]/g, '-')}.csv`;
    link.click();
    showToast('Exporting Flight Recorder history');
  });

  $$('.mode-selector button').forEach(button => {
    button.addEventListener('click', () => {
      $$('.mode-selector button').forEach(item => item.classList.toggle('is-active', item === button));
      const svg = $('#vehicleSvg');
      const stage = $('#vesselRenderStage');
      const topology = button.dataset.mode === 'topology';
      svg.hidden = !topology;
      stage.hidden = topology;
      if (topology) {
        svg.classList.remove('heat-mode', 'state-mode', 'stage-mode', 'resource-mode');
        svg.classList.add('heat-mode');
        if (state.vesselStructure?.parts) renderVesselDiagram(state.vesselStructure.parts);
        disconnectVesselImage();
      } else {
        connectVesselImage();
        if (!sendCommand('vessel.view.mode', { mode: button.dataset.mode })) showToast('No live connection; vessel coloring not changed');
      }
      $('.heat-legend').hidden = button.dataset.mode !== 'heat';
      showToast(`Vessel view: ${sourceText(button)}`);
    });
  });

  let vesselCanvasScale = 1;
  let vesselCanvasRotation = 0;
  let vesselCanvasX = 0;
  let vesselCanvasY = 0;
  function applyVesselCanvasTransform() {
    const transform = `translate(${vesselCanvasX}px,${vesselCanvasY}px) scale(${vesselCanvasScale}) rotate(${vesselCanvasRotation}deg)`;
    $('#vehicleSvg').style.transform = transform;
    $('#vesselRenderSurface').style.transform = transform;
  }
  $$('.canvas-tools button').forEach(button => button.addEventListener('click', () => {
    const action = button.dataset.canvasAction;
    if (action === 'in') vesselCanvasScale = Math.min(1.8, vesselCanvasScale + .15);
    if (action === 'out') vesselCanvasScale = Math.max(.55, vesselCanvasScale - .15);
    if (action === 'rotate') {
      if ($('#vehicleSvg').hidden) {
        const labels = ['ISOMETRIC', 'XY', 'XZ', 'YZ'];
        const current = sourceText($('#vesselViewLabel')).replace('VIEW / ', '');
        const next = labels[(Math.max(0, labels.indexOf(current)) + 1) % labels.length];
        $('#vesselViewLabel').textContent = `VIEW / ${next}`;
        if (!sendCommand('vessel.view.rotate')) showToast('No live connection; view direction not changed');
      } else vesselCanvasRotation = (vesselCanvasRotation + 90) % 360;
    }
    if (action === 'fit') {
      vesselCanvasScale = 1; vesselCanvasRotation = 0; vesselCanvasX = 0; vesselCanvasY = 0;
      if ($('#vehicleSvg').hidden) sendCommand('vessel.view.fit', {}, true);
    }
    applyVesselCanvasTransform();
  }));

  const vesselStage = $('#vesselRenderStage');
  syncVesselRenderSurface();
  if ('ResizeObserver' in window) new ResizeObserver(syncVesselRenderSurface).observe(vesselStage);
  else window.addEventListener('resize', syncVesselRenderSurface);
  let vesselDrag = null;
  vesselStage.addEventListener('pointerdown', event => {
    vesselDrag = { id: event.pointerId, x: event.clientX, y: event.clientY, originX: vesselCanvasX, originY: vesselCanvasY };
    vesselStage.setPointerCapture(event.pointerId);
  });
  vesselStage.addEventListener('pointermove', event => {
    if (!vesselDrag || vesselDrag.id !== event.pointerId) return;
    vesselCanvasX = vesselDrag.originX + event.clientX - vesselDrag.x;
    vesselCanvasY = vesselDrag.originY + event.clientY - vesselDrag.y;
    applyVesselCanvasTransform();
  });
  const endVesselDrag = event => { if (vesselDrag?.id === event.pointerId) vesselDrag = null; };
  vesselStage.addEventListener('pointerup', endVesselDrag);
  vesselStage.addEventListener('pointercancel', endVesselDrag);

  const systemCommands = ['sas', 'rcs', 'gear', 'brakes', 'lights', 'solar', 'antenna', 'autostage'];
  $$('.system-switch').forEach((button, index) => {
    button.addEventListener('click', () => {
      const system = systemCommands[index];
      const command = system === 'solar' ? 'vessel.solar.toggle' : system === 'antenna' ? 'vessel.antenna.toggle' : system === 'autostage' ? 'mechjeb.autostage.toggle' : 'vessel.system.toggle';
      const parameters = command === 'vessel.system.toggle' ? { system } : {};
      if (!sendCommand(command, parameters)) showToast('No live connection; system command not sent');
    });
  });

  $$('.action-pad button').forEach((button, index) => {
    button.addEventListener('click', () => {
      if (!sendCommand('vessel.actionGroup.toggle', { group: index + 1 })) showToast('No live connection; action group command not sent');
    });
  });

  const controlJoystick = $('#controlJoystick');
  const controlStick = $('#controlStick');
  const controlRoll = $('#controlRoll');
  const controlRollTrack = $('#controlRoll > i');
  const controlRollKnob = $('#controlRollKnob');
  const controlRollValue = $('#controlRollValue');
  const controlThrottle = $('#controlThrottle');
  const controlThrottleTrack = $('#controlThrottle > i');
  const controlThrottleFill = $('#controlThrottleFill');
  const controlThrottleValue = $('#controlThrottleValue');
  const releaseControl = $('#releaseControl');

  function syncThrottleLock(automation = state.lastAutomation) {
    const locked = Boolean(automation?.ascentEnabled || automation?.nodeExecutorEnabled || automation?.landingEnabled);
    const justLocked = locked && !state.control.throttleLocked;
    state.control.throttleLocked = locked;
    if (locked) state.control.throttle = 0;
    updateControlDisplay();
    if (justLocked && state.control.engaged) sendContinuousControl();
  }

  function updateControlDisplay() {
    controlStick.style.setProperty('--stick-x', `${state.control.yaw * 35}px`);
    controlStick.style.setProperty('--stick-y', `${-state.control.pitch * 35}px`);
    const rollTravel = Math.max(0, (controlRollTrack.clientWidth - 14) / 2);
    controlRollKnob.style.setProperty('--roll-x', `${state.control.roll * rollTravel}px`);
    controlRollValue.textContent = `${state.control.roll >= 0 ? '+' : '−'}${Math.round(Math.abs(state.control.roll) * 100)}%`;
    controlThrottleFill.style.height = `${state.control.throttle * 100}%`;
    controlThrottleValue.textContent = state.control.throttleLocked ? 'MJ LOCK' : `${Math.round(state.control.throttle * 100)}%`;
    controlThrottle.classList.toggle('is-locked', state.control.throttleLocked);
    controlThrottle.setAttribute('aria-disabled', state.control.throttleLocked ? 'true' : 'false');
    controlThrottle.title = state.control.throttleLocked ? 'MechJeb Web throttle locked during ascent, node execution or landing' : '';
    releaseControl.textContent = state.control.engaged ? 'Controlling · release' : 'Released';
    releaseControl.classList.toggle('is-armed', state.control.engaged);
  }

  function sendContinuousControl() {
    if (!state.control.engaged) return false;
    return sendCommand('vessel.control.set', {
      pitch: state.control.pitch, yaw: state.control.yaw, roll: state.control.roll,
      throttle: state.control.throttleLocked ? 0 : state.control.throttle, holdMilliseconds: 350
    }, true);
  }

  function armContinuousControl() {
    if (state.connection !== 'live') { showToast('No live connection; continuous control not enabled'); return false; }
    state.control.engaged = true;
    if (!state.controlTimer) state.controlTimer = setInterval(sendContinuousControl, 100);
    updateControlDisplay();
    sendContinuousControl();
    return true;
  }

  function disarmContinuousControl(sendRelease = true) {
    if (sendRelease && state.control.engaged) sendCommand('vessel.control.release', {}, true);
    state.control.engaged = false;
    state.control.pitch = state.control.yaw = state.control.roll = state.control.throttle = 0;
    clearInterval(state.controlTimer);
    state.controlTimer = null;
    updateControlDisplay();
  }

  function setJoystick(event) {
    const box = controlStick.getBoundingClientRect();
    state.control.yaw = Math.max(-1, Math.min(1, (event.clientX - box.left - box.width / 2) / (box.width / 2)));
    state.control.pitch = Math.max(-1, Math.min(1, -(event.clientY - box.top - box.height / 2) / (box.height / 2)));
    updateControlDisplay();
    sendContinuousControl();
  }

  controlJoystick.addEventListener('pointerdown', event => {
    event.preventDefault();
    if (!armContinuousControl()) return;
    controlJoystick.setPointerCapture(event.pointerId);
    setJoystick(event);
  });
  controlJoystick.addEventListener('pointermove', event => {
    if (controlJoystick.hasPointerCapture(event.pointerId)) setJoystick(event);
  });
  const releaseJoystick = event => {
    if (!controlJoystick.hasPointerCapture(event.pointerId)) return;
    state.control.pitch = state.control.yaw = 0;
    updateControlDisplay();
    sendContinuousControl();
    if (state.control.throttle === 0) disarmContinuousControl(true);
  };
  controlJoystick.addEventListener('pointerup', releaseJoystick);
  controlJoystick.addEventListener('pointercancel', releaseJoystick);

  function setRoll(event) {
    const box = controlRollTrack.getBoundingClientRect();
    state.control.roll = Math.max(-1, Math.min(1, (event.clientX - box.left - box.width / 2) / (box.width / 2)));
    updateControlDisplay();
    sendContinuousControl();
  }

  controlRoll.addEventListener('pointerdown', event => {
    event.preventDefault();
    if (!armContinuousControl()) return;
    controlRoll.setPointerCapture(event.pointerId);
    setRoll(event);
  });
  controlRoll.addEventListener('pointermove', event => {
    if (controlRoll.hasPointerCapture(event.pointerId)) setRoll(event);
  });
  const releaseRoll = event => {
    if (!controlRoll.hasPointerCapture(event.pointerId)) return;
    state.control.roll = 0;
    updateControlDisplay();
    sendContinuousControl();
    if (state.control.pitch === 0 && state.control.yaw === 0 && state.control.throttle === 0) disarmContinuousControl(true);
  };
  controlRoll.addEventListener('pointerup', releaseRoll);
  controlRoll.addEventListener('pointercancel', releaseRoll);

  function setThrottle(event) {
    if (state.control.throttleLocked) return;
    const box = controlThrottleTrack.getBoundingClientRect();
    state.control.throttle = Math.max(0, Math.min(1, 1 - (event.clientY - box.top) / box.height));
    updateControlDisplay();
    sendContinuousControl();
  }

  controlThrottle.addEventListener('pointerdown', event => {
    event.preventDefault();
    if (state.control.throttleLocked) {
      showToast('MechJeb is controlling thrust. Throttle becomes available after ascent, node execution or landing ends.');
      return;
    }
    if (!armContinuousControl()) return;
    controlThrottle.setPointerCapture(event.pointerId);
    setThrottle(event);
  });
  controlThrottle.addEventListener('pointermove', event => {
    if (controlThrottle.hasPointerCapture(event.pointerId)) setThrottle(event);
  });
  releaseControl.addEventListener('click', () => disarmContinuousControl(true));
  window.addEventListener('pagehide', () => disarmContinuousControl(true));
  document.addEventListener('visibilitychange', () => { if (document.hidden) disarmContinuousControl(true); });
  updateControlDisplay();

  bindSlideConfirmation($('#holdStage'), $('#stageSlideConfirm'), () => {
    if (!sendCommand('vessel.stage')) showToast('No live connection; stage not activated');
  });
  bindSlideConfirmation($('#holdQuickload'), $('#quickloadSlideConfirm'), () => {
    if (!sendCommand('game.quicksave.load')) showToast('No live connection; quicksave not loaded');
  });
  bindSlideConfirmation($('#ascentStageTrigger'), $('#ascentStageSlideConfirm'), () => {
    if (!sendCommand('vessel.stage')) showToast('No live connection; stage not activated');
  });

  const lerp = (from, to, amount) => from + (to - from) * amount;
  const lerpAngle = (from, to, amount) => from + ((((to - from) % 360) + 540) % 360 - 180) * amount;
  const signed = (value, digits = 1) => `${value >= 0 ? '+' : '−'}${Math.abs(value).toFixed(digits)}`;
  function drawNavball(canvas, pitchDegrees, rollDegrees, headingDegrees, compact = false) {
    if (!canvas) return;
    const previous = canvas._navballAngles;
    const delta = (a, b) => Math.abs((((a - b) % 360) + 540) % 360 - 180);
    if (previous && delta(previous.pitch, pitchDegrees) < .12 && delta(previous.roll, rollDegrees) < .12 && delta(previous.heading, headingDegrees) < .2) return;
    canvas._navballAngles = { pitch: pitchDegrees, roll: rollDegrees, heading: headingDegrees };
    const size = compact ? 170 : 230;
    if (canvas.width !== size || canvas.height !== size) { canvas.width = size; canvas.height = size; }
    const context = canvas.getContext('2d', { alpha: false });
    const image = context.createImageData(size, size);
    const data = image.data;
    const pitch = pitchDegrees * Math.PI / 180;
    const roll = rollDegrees * Math.PI / 180;
    const heading = headingDegrees * Math.PI / 180;
    const cp = Math.cos(pitch), sp = Math.sin(pitch), cr = Math.cos(roll), sr = Math.sin(roll);
    const sh = Math.sin(heading), ch = Math.cos(heading);
    const forward = [sh * cp, sp, ch * cp];
    const right = [ch, 0, -sh];
    const pitchedUp = [-sh * sp, cp, -ch * sp];
    const rolledRight = [right[0] * cr + pitchedUp[0] * sr, pitchedUp[1] * sr, right[2] * cr + pitchedUp[2] * sr];
    const rolledUp = [pitchedUp[0] * cr - right[0] * sr, pitchedUp[1] * cr, pitchedUp[2] * cr - right[2] * sr];
    const radius = size / 2;
    const mix = (a, b, t) => Math.round(a + (b - a) * t);
    for (let py = 0; py < size; py++) {
      const y = -(py + .5 - radius) / radius;
      for (let px = 0; px < size; px++) {
        const x = (px + .5 - radius) / radius;
        const rr = x * x + y * y;
        const offset = (py * size + px) * 4;
        if (rr > 1) { data[offset] = 5; data[offset + 1] = 17; data[offset + 2] = 22; data[offset + 3] = 255; continue; }
        const z = Math.sqrt(1 - rr);
        const wx = rolledRight[0] * x + rolledUp[0] * y + forward[0] * z;
        const wy = rolledRight[1] * x + rolledUp[1] * y + forward[1] * z;
        const wz = rolledRight[2] * x + rolledUp[2] * y + forward[2] * z;
        const latitude = Math.asin(Math.max(-1, Math.min(1, wy))) * 180 / Math.PI;
        const longitude = Math.atan2(wx, wz) * 180 / Math.PI;
        const intensity = .58 + .42 * z;
        let base;
        if (wy >= 0) {
          const t = Math.min(1, Math.abs(latitude) / 90);
          base = [mix(22, 43, t), mix(72, 113, t), mix(111, 154, t)];
        } else {
          const t = Math.min(1, Math.abs(latitude) / 90);
          base = [mix(139, 82, t), mix(99, 59, t), mix(57, 37, t)];
        }
        const latitudeStep = Math.abs(latitude - Math.round(latitude / 10) * 10);
        const longitudeStep = Math.abs(longitude - Math.round(longitude / 30) * 30);
        const equator = Math.abs(latitude) < .65;
        const latitudeLine = !equator && latitudeStep < .34 && Math.abs(latitude) < 89;
        const longitudeLine = longitudeStep < .38 && Math.abs(latitude) < 84;
        if (equator) base = [225, 226, 208];
        else if (latitudeLine || longitudeLine) base = base.map(channel => mix(channel, 220, latitudeLine ? .48 : .32));
        const edge = rr > .88 ? Math.max(.45, (1 - rr) / .12) : 1;
        data[offset] = Math.round(base[0] * intensity * edge);
        data[offset + 1] = Math.round(base[1] * intensity * edge);
        data[offset + 2] = Math.round(base[2] * intensity * edge);
        data[offset + 3] = 255;
      }
    }
    context.putImageData(image, 0, 0);
    context.save();
    context.strokeStyle = 'rgba(241,238,211,.72)';
    context.fillStyle = 'rgba(255,239,171,.9)';
    context.font = `${compact ? 8 : 9}px Cascadia Mono, monospace`;
    context.textAlign = 'center';
    context.fillText(`${Math.round(((headingDegrees % 360) + 360) % 360).toString().padStart(3, '0')}°`, radius, compact ? 15 : 18);
    context.restore();
  }
  function formatDuration(seconds) {
    const value = Number(seconds);
    // Round the total first so a displayed 60.0 seconds carries into minutes/hours.
    const tenths = Math.round(Math.max(0, Number.isFinite(value) ? value : 0) * 10);
    const hours = Math.floor(tenths / 36000);
    const minutes = Math.floor((tenths % 36000) / 600);
    const secs = (tenths % 600) / 10;
    return `${String(hours).padStart(2, '0')}:${String(minutes).padStart(2, '0')}:${secs.toFixed(1).padStart(4, '0')}`;
  }

  function renderLiveTelemetry(now) {
    const current = state.currentSample;
    if (!current) return;
    const previous = state.previousSample || current;
    const interval = Math.max(16, current.receivedAt - previous.receivedAt || 33);
    const amount = Math.max(0, Math.min(1, (now - current.receivedAt) / interval));
    const value = name => lerp(Number(previous.data[name]) || 0, Number(current.data[name]) || 0, amount);
    const angleValue = name => lerpAngle(Number(previous.data[name]) || 0, Number(current.data[name]) || 0, amount);
    const pitch = value('pitch');
    const roll = angleValue('roll');
    $('#pitchValue').textContent = `${signed(pitch)}°`;
    $('#rollValue').textContent = `${signed(roll)}°`;
    const speed = value('surfaceSpeed');
    const radarKilometers = value('radarAltitude') / 1000;
    const heading = ((angleValue('heading') % 360) + 360) % 360;
    drawNavball($('#flightNavball'), pitch, roll, heading, false);
    drawNavball($('#smartNavball'), pitch, roll, heading, true);
    $('#surfaceSpeed').textContent = Math.round(speed).toLocaleString(uiLocale());
    $('#radarAlt').textContent = radarKilometers.toFixed(radarKilometers < 10 ? 2 : 1);
    $('#verticalSpeed').textContent = signed(value('verticalSpeed'));
    $('#headingValue').textContent = `${heading.toFixed(1)}°`;
    $('#dynamicPressure').textContent = value('dynamicPressureKpa').toFixed(1);
    $('#gForceValue').textContent = `${value('geeForce').toFixed(2)} g`;
    const envelope = state.lastAutomation || {};
    const currentQ = value('dynamicPressureKpa');
    const currentAcceleration = value('geeForce') * 9.80665;
    const currentThrottle = value('throttle') * 100;
    const qLimit = Number(envelope.envelopeMaximumDynamicPressure || 0) / 1000;
    const accelerationLimit = Number(envelope.envelopeMaximumAcceleration || 0);
    $('#envelopeCurrentQ').textContent = `${currentQ.toFixed(1)} kPa`;
    $('#envelopeCurrentAcceleration').textContent = `${currentAcceleration.toFixed(1)} m/s²`;
    $('#envelopeCurrentThrottle').textContent = `${currentThrottle.toFixed(0)}%`;
    updateEnvelopeBar($('#envelopeQBar'), qLimit > 0 ? currentQ / qLimit * 100 : 0);
    updateEnvelopeBar($('#envelopeAccelerationBar'), accelerationLimit > 0 ? currentAcceleration / accelerationLimit * 100 : 0);
    updateEnvelopeBar($('#envelopeThrottleBar'), currentThrottle);
    $('#missionTime').textContent = formatDuration(value('met'));
    $('#smartPitch').textContent = `${signed(pitch)}°`;
    $('#smartHeading').textContent = `${heading.toFixed(1)}°`;
    $('#smartRoll').textContent = `${signed(roll)}°`;
    const speedStep = speed < 20 ? 1 : speed < 200 ? 10 : speed < 2000 ? 25 : 50;
    const speedTicks = [speed - speedStep * 2, speed - speedStep, speed + speedStep, speed + speedStep * 2];
    $$('.tape-left .tape-scale i').forEach((tick, index) => { tick.textContent = speedTicks[index] < 0 ? '—' : Math.round(speedTicks[index]).toLocaleString(uiLocale()); });
    const altitudeStep = radarKilometers < 1 ? .01 : radarKilometers < 20 ? .1 : radarKilometers < 200 ? .2 : 1;
    const altitudeTicks = [radarKilometers - altitudeStep * 2, radarKilometers - altitudeStep, radarKilometers + altitudeStep, radarKilometers + altitudeStep * 2];
    $$('.tape-right .tape-scale i').forEach((tick, index) => { tick.textContent = Math.max(0, altitudeTicks[index]).toFixed(altitudeStep < .1 ? 2 : 1); });
    const wrapHeading = degrees => ((degrees % 360) + 360) % 360;
    const headingTicks = [heading - 20, heading - 10, heading + 10, heading + 20];
    $$('.heading-band span').forEach((tick, index) => { tick.textContent = Math.round(wrapHeading(headingTicks[index])).toString().padStart(3, '0'); });

    if (now - state.lastTelemetryAt > 1000 && state.connection === 'live') {
      setConnectionState('stale', 'Telemetry delayed', `Fast telemetry ${(now - state.lastTelemetryAt).toFixed(0)} ms ago`);
    }
  }

  function initializeUnavailableState() {
    const set = (selector, value = '—') => { const element = $(selector); if (element) userText(element, null, value); };
    [
      '#activeVesselName', '#surfaceSpeed', '#pitchValue', '#rollValue', '#headingValue', '#radarAlt',
      '#verticalSpeed', '#currentThrust', '#localTwr', '#dynamicPressure', '#gForceValue', '#missionTime',
      '#currentApoapsis', '#timeToApoapsis', '#currentPeriapsis', '#periapsisStatus', '#flightSituation',
      '#ascentActualAp', '#ascentActualPe', '#ascentActualInclination', '#ascentActualPeriod', '#ascentActualTimeAp', '#ascentActualTimePe',
      '#totalDeltaV', '#stageDeltaV', '#smartPitch', '#smartHeading', '#smartRoll'
    ].forEach(selector => set(selector));
    set('#maximumThrust', 'Waiting for Flight Panel');
    set('#throttleTwr', 'Waiting for Flight Panel');
    set('#flightPanelState', 'Waiting for data');
    set('#flightPanelRate', 'FAST / REGULAR / MECHJEB');
    set('#sampleCount', '0');
    set('#recState', 'Waiting for samples');
    $$('.telemetry-ledger .data-cell b, .telemetry-ledger .ledger-feature b, .telemetry-ledger .ledger-feature small, .fp-row > b, .fp-summary b, .chart-now b, .series-chip small')
      .forEach(element => { element.textContent = '—'; });
    $$('.system-switch').forEach(button => {
      button.classList.remove('is-on');
      const status = button.querySelector('small');
      if (status) status.textContent = 'Waiting for vessel state';
    });
    $$('.action-pad button').forEach(button => button.classList.remove('is-on'));
    ['#crewTotal', '#crewPilots', '#crewEngineers', '#crewScientists', '#crewOccupied', '#crewVesselTotal']
      .forEach(selector => set(selector));
    set('#crewCapacity', 'Waiting for vessel');
    set('#crewVesselName', 'Waiting for active vessel');
    const cabins = $('#crewCabins');
    if (cabins) cabins.innerHTML = '<div class=\"cabin-count\"><span>Waiting for compartment data</span><b>—</b></div>';
    const manifest = $('#crewManifest');
    if (manifest) manifest.innerHTML = '<article class=\"crew-row\"><div class=\"crew-identity\"><p><b>Waiting for crew data</b><small>Loads automatically after connecting to KSP</small></p></div></article>';
    const partIndex = $('#partIndexList');
    if (partIndex) partIndex.innerHTML = '<div class=\"part-index-item\"><span><b>Waiting for vessel structure</b><small>Loads automatically after connecting to KSP</small></span></div>';
    set('#partName', 'Waiting for part data');
    set('#part-action-title', 'Waiting for part data');
    set('#partActionMeta', 'Selected part actions appear after connecting to KSP');
    const inspector = $('.part-inspector');
    if (inspector) {
      set('.part-inspector header small', 'Loads automatically after connecting to KSP');
      $$('.part-inspector .part-health b, .part-inspector .part-health small, .part-inspector .part-facts dd').forEach(element => { element.textContent = '—'; });
      const resources = inspector.querySelector('.resource-stack');
      if (resources) resources.innerHTML = '<h3>Resources</h3><p class=\"setup-note\">Waiting for selected part resources</p>';
    }
    const plannerTarget = $('.planner-status .mode-chip');
    if (plannerTarget) plannerTarget.innerHTML = '<i></i> Target: waiting for KSP';
    set('.planner-status > b', '0 NODES');
    set('#plannerCurrentApsides', 'Waiting for telemetry');
    set('#plannerNodeMapLabel', 'Waiting for KSP node');
    set('#nodeQueueCount', '0 QUEUED');
    set('#porkDeparture', 'Waiting for solution');
    set('#porkDuration'); set('#porkTotal'); set('#porkCapture');
    set('#porkEjection', 'Generated when node is created');
    $('#porkHeatmap').setAttribute('opacity', '0');
    $$('.porkchop-chart .contour').forEach(path => { path.style.display = 'none'; });
    $('#vehicleConnections').innerHTML = '';
    $('#vehicleParts').innerHTML = '';
    set('#vesselSelectedId', 'SELECTED / —');
    set('#vesselSelectedName', 'WAITING');
    $$('[data-operation-state]').forEach(row => {
      row.querySelector('.operation-light')?.classList.remove('is-on');
      const detail = row.querySelector('small'); if (detail) detail.textContent = 'Waiting for state';
    });
    drawNavball($('#flightNavball'), 0, 0, 0, false);
    drawNavball($('#smartNavball'), 0, 0, 0, true);
  }

  function renderFrameSafely(name, render, now) {
    try {
      render();
    } catch (error) {
      state.renderErrorAt ||= {};
      const lastReported = Number(state.renderErrorAt[name] || 0);
      if (now - lastReported >= 5000) {
        state.renderErrorAt[name] = now;
        console.error(`[ArmorControl] ${name} render failed`, error);
      }
    }
  }

  function tick(now) {
    // Schedule first so a panel-specific rendering error can never stop the live loop.
    requestAnimationFrame(tick);
    if ((state.connection === 'live' || state.connection === 'stale') && state.currentSample) {
      renderFrameSafely('flight telemetry', () => renderLiveTelemetry(now), now);
    }
    renderFrameSafely('flight recorder', () => renderRecorder(now), now);
  }
  requestAnimationFrame(tick);
})();
