'use strict';

// ── Application state ─────────────────────────────────────────────────────────
let savedPlayers = { playerCount: 3, rows: [] };
let gameTypes = [];
let isTeamGame = false;
let currentTeamCount = 1;
let currentSnapshot = null;
let currentTelemetry = null;
let logLines = [];
let signalRStarting = false;
let signalRRetryTimer = null;
let refreshTimer = null;
let actionInProgress = null;
let _lastStateKey = null;
let _lastTelemetryKey = null;

const MAX_LOG = 1000;
const PRESETS_KEY = 'lazertag_presets';

const connection = new signalR.HubConnectionBuilder()
    .withUrl('/gamehub')
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .build();

connection.on('Log', msg => appendLog(msg));
connection.on('State', snap => applyState(snap));
connection.on('Telemetry', tel => applyTelemetry(tel));
connection.onreconnecting(() => appendLog('Control display reconnecting to the server…'));
connection.onreconnected(async () => {
    appendLog('Control display reconnected.');
    await refreshSnapshots();
});
connection.onclose(() => {
    appendLog('Live connection closed. Retrying…');
    scheduleSignalRStart();
});

window.addEventListener('DOMContentLoaded', async () => {
    updateActionBar('idle');
    renderPresets();

    await loadGameTypes();
    await loadSavedPlayers();
    buildPlayerSlots(savedPlayers.rows);
    updateOverview();

    await refreshSnapshots();
    await startSignalR();

    refreshTimer = window.setInterval(refreshSnapshots, 5000);
    document.getElementById('player-count').addEventListener('change', () => buildPlayerSlots());

    const dialog = document.getElementById('preset-dialog');
    dialog.addEventListener('click', event => {
        if (event.target === dialog) closePresetDialog();
    });
});

window.addEventListener('beforeunload', () => {
    if (refreshTimer) window.clearInterval(refreshTimer);
    if (signalRRetryTimer) window.clearTimeout(signalRRetryTimer);
});

// ── Server communication ──────────────────────────────────────────────────────
async function fetchJson(url, options = {}) {
    const response = await fetch(url, options);
    const text = await response.text();
    let data = {};

    if (text) {
        try { data = JSON.parse(text); }
        catch { data = { error: text }; }
    }

    if (!response.ok) {
        throw new Error(data.error || data.title || response.statusText || `Request failed (${response.status})`);
    }

    return data;
}

async function refreshSnapshots() {
    try {
        const [state, telemetry] = await Promise.all([
            fetchJson('/api/game/state'),
            fetchJson('/api/game/telemetry'),
        ]);
        applyState(state);
        applyTelemetry(telemetry);
    } catch (error) {
        // SignalR may still be delivering state. Avoid filling the log on every poll.
        console.debug('Snapshot refresh failed:', error);
    }
}

async function startSignalR() {
    if (signalRStarting || connection.state !== signalR.HubConnectionState.Disconnected) return;

    signalRStarting = true;
    try {
        await connection.start();
        appendLog('Live control connection established.');
        await refreshSnapshots();
    } catch (error) {
        console.debug('SignalR start failed:', error);
        scheduleSignalRStart();
    } finally {
        signalRStarting = false;
    }
}

function scheduleSignalRStart() {
    if (signalRRetryTimer) return;
    signalRRetryTimer = window.setTimeout(async () => {
        signalRRetryTimer = null;
        await startSignalR();
    }, 3000);
}

// ── Game types and defaults ───────────────────────────────────────────────────
async function loadGameTypes() {
    const select = document.getElementById('game-type-select');

    try {
        gameTypes = await fetchJson('/api/game/gametypes');
        select.innerHTML = gameTypes
            .map(type => `<option value="${type.id}">${esc(type.label)}</option>`)
            .join('');

        const preferred = gameTypes.find(type => type.isDefault) || gameTypes[0];
        if (preferred) select.value = String(preferred.id);
        await applyGameTypeMetadata(true);
    } catch (error) {
        appendLog(`Failed to load game types: ${error.message}`);
        showToast('Game types could not be loaded.', 'error');
    }
}

async function loadSavedPlayers() {
    try {
        savedPlayers = await fetchJson('/api/game/savedplayers');
        const count = clamp(parseInt(savedPlayers.playerCount, 10) || 3, 1, 24);
        savedPlayers.playerCount = count;
        document.getElementById('player-count').value = count;
    } catch (error) {
        console.debug('Saved player settings unavailable:', error);
    }
}

async function onGameTypeChange() {
    await applyGameTypeMetadata(true);
}

async function applyGameTypeMetadata(applyDefaults) {
    const selectedId = parseInt(document.getElementById('game-type-select').value, 10);
    const metadata = gameTypes.find(type => Number(type.id) === selectedId);

    currentTeamCount = Math.max(1, Number(metadata?.teamCount) || 1);
    isTeamGame = currentTeamCount > 1;
    updateTeamSelectors();

    if (applyDefaults && Number.isFinite(selectedId)) {
        try {
            const defaults = await fetchJson(`/api/game/gametypedefaults/${selectedId}`);
            setVal('minutes', defaults.defaultMinutes);
            setVal('global-hp', defaults.defaultHp);
            setVal('global-shields', defaults.defaultShields);
            setVal('global-reloads', defaults.defaultReloads);
            setVal('global-mega', defaults.defaultMega);
            setChk('opt-slow-tags', defaults.defaultSlowTags);
        } catch (error) {
            showToast(error.message, 'error');
        }
    }

    updateOverview();
}

function updateTeamSelectors() {
    document.querySelectorAll('.p-team').forEach(select => {
        const previous = select.value;
        select.innerHTML = '<option value="">Auto</option>' +
            Array.from({ length: currentTeamCount }, (_, index) =>
                `<option value="${index + 1}">Team ${index + 1}</option>`).join('');

        select.hidden = !isTeamGame;
        select.closest('.team-select')?.classList.toggle('is-hidden', !isTeamGame);
        select.value = isTeamGame && Number(previous) <= currentTeamCount ? previous : '';
    });
}

// ── Phase-aware controls ──────────────────────────────────────────────────────
function updateActionBar(state) {
    const normalized = (state || '').toLowerCase();
    const isIdle = normalized === 'idle' || normalized === '';
    const isLobby = normalized === 'adding' || normalized === 'acknowledgeplayerassignment';
    const isActive = normalized === 'countdown' || normalized === 'resendcountdown' || normalized === 'playing';
    const isOver = ['gameover', 'summary', 'debriefing', 'waitingfordebriefing'].includes(normalized);

    setBtn('btn-start', isIdle);
    setBtn('btn-countdown', isLobby);
    setBtn('btn-end', isLobby || isActive);
    setBtn('btn-next', isOver);
}

function setBtn(id, enabled) {
    const button = document.getElementById(id);
    if (!button) return;
    button.dataset.available = enabled ? 'true' : 'false';
    button.disabled = !enabled || actionInProgress !== null;
    button.classList.toggle('is-busy', actionInProgress === id);
}

function setActionBusy(buttonId) {
    actionInProgress = buttonId;
    updateActionBar(currentSnapshot?.hostingState || 'idle');
}

function clearActionBusy() {
    actionInProgress = null;
    updateActionBar(currentSnapshot?.hostingState || 'idle');
}

// ── Player setup ──────────────────────────────────────────────────────────────
function buildPlayerSlots(rows) {
    const countInput = document.getElementById('player-count');
    const count = clamp(parseInt(countInput.value, 10) || 3, 1, 24);
    countInput.value = count;

    let sourceRows = rows;
    if (!sourceRows && document.querySelector('.player-row')) sourceRows = readGrid();
    sourceRows = sourceRows || savedPlayers.rows || [];

    const list = document.getElementById('player-list');
    list.innerHTML = '';

    for (let slot = 1; slot <= count; slot++) {
        const saved = sourceRows[slot - 1] || {};
        const savedTeam = normalizeSavedTeam(saved.teamPreference, saved.teamPreferenceLabel);

        const row = document.createElement('div');
        row.className = 'player-row';
        row.dataset.slot = String(slot);
        row.innerHTML = `
          <div class="player-main">
            <span class="slot-badge" title="Join order ${slot}">${slot}</span>
            <label class="sr-only" for="player-name-${slot}">Player ${slot} name</label>
            <input id="player-name-${slot}" class="p-name" type="text" maxlength="40"
                   placeholder="Player ${slot}" value="${esc(saved.name || '')}">
            <div class="team-select${isTeamGame ? '' : ' is-hidden'}">
              <label class="sr-only" for="player-team-${slot}">Player ${slot} team</label>
              <select id="player-team-${slot}" class="p-team" ${isTeamGame ? '' : 'hidden'}></select>
            </div>
            <button type="button" class="advanced-toggle" aria-expanded="false"
                    aria-controls="player-advanced-${slot}" title="Player overrides"
                    onclick="toggleAdv(this)">＋</button>
          </div>
          <div class="player-advanced" id="player-advanced-${slot}">
            <div class="override-grid">
              ${overrideNumberField(slot, 'hp', 'HP', 1, 254, saved.hp)}
              ${overrideNumberField(slot, 'shields', 'Shields', 0, 254, saved.shields)}
              ${overrideNumberField(slot, 'reloads', 'Reloads', 0, 99, saved.reloads)}
              ${overrideNumberField(slot, 'ammo', 'Ammo', 0, 254, saved.ammo)}
              ${overrideNumberField(slot, 'sleep', 'Sleep', 0, 99, saved.sleepTimeout)}
            </div>
            <div class="override-options">
              <label><input type="checkbox" class="p-ar" ${saved.autoReload ? 'checked' : ''}> Force Auto Reload</label>
              <label><input type="checkbox" class="p-fa" ${saved.fullAuto ? 'checked' : ''}> Force Full Auto</label>
            </div>
          </div>`;

        const teamSelect = row.querySelector('.p-team');
        teamSelect.innerHTML = '<option value="">Auto</option>' +
            Array.from({ length: currentTeamCount }, (_, index) =>
                `<option value="${index + 1}">Team ${index + 1}</option>`).join('');
        teamSelect.value = isTeamGame && savedTeam <= currentTeamCount ? String(savedTeam || '') : '';
        list.appendChild(row);
    }

    document.getElementById('guided-join-hint').textContent = '';
    updateOverview();
}

function overrideNumberField(slot, className, label, min, max, value) {
    const safeValue = value === null || value === undefined ? '' : value;
    return `<label>${label}
      <input type="number" class="p-${className}" min="${min}" max="${max}"
             inputmode="numeric" placeholder="Global" value="${esc(safeValue)}"
             aria-label="Player ${slot} ${label} override">
    </label>`;
}

function normalizeSavedTeam(value, label) {
    if (Number(value) > 0) return Number(value);
    const match = String(value || label || '').match(/(\d+)/);
    return match ? Number(match[1]) : 0;
}

function toggleAdv(button) {
    const row = button.closest('.player-row');
    const body = row?.querySelector('.player-advanced');
    if (!body) return;

    const open = !body.classList.contains('is-open');
    body.classList.toggle('is-open', open);
    button.setAttribute('aria-expanded', String(open));
    button.textContent = open ? '−' : '＋';
}

function readGrid() {
    return [...document.querySelectorAll('.player-row')].map((row, index) => {
        const teamSelect = row.querySelector('.p-team');
        const teamValue = isTeamGame ? teamSelect.value : '';
        return {
            slot: index + 1,
            name: row.querySelector('.p-name').value.trim(),
            teamPreferenceLabel: teamValue ? `Team ${teamValue}` : 'Auto',
            teamPreference: teamValue ? parseInt(teamValue, 10) : null,
            hp: num(row.querySelector('.p-hp').value),
            shields: num(row.querySelector('.p-shields').value),
            reloads: num(row.querySelector('.p-reloads').value),
            autoReload: row.querySelector('.p-ar').checked ? true : null,
            fullAuto: row.querySelector('.p-fa').checked ? true : null,
            ammo: num(row.querySelector('.p-ammo').value),
            sleepTimeout: num(row.querySelector('.p-sleep').value),
        };
    });
}

// ── Presets ───────────────────────────────────────────────────────────────────
function getPresets() {
    try {
        const value = JSON.parse(localStorage.getItem(PRESETS_KEY) || '[]');
        return Array.isArray(value) ? value : [];
    } catch {
        return [];
    }
}

function putPresets(presets) {
    localStorage.setItem(PRESETS_KEY, JSON.stringify(presets));
}

function captureConfig() {
    const select = document.getElementById('game-type-select');
    return {
        gameType: parseInt(select.value, 10),
        gameTypeLabel: select.options[select.selectedIndex]?.text || '',
        minutes: intVal('minutes'),
        countdown: intVal('countdown'),
        globalHp: intVal('global-hp'),
        globalShields: intVal('global-shields'),
        globalReloads: intVal('global-reloads'),
        globalMega: intVal('global-mega'),
        globalAmmo: intVal('global-ammo'),
        globalSleep: intVal('global-sleep'),
        slowTags: isChecked('opt-slow-tags'),
        teamTags: isChecked('opt-team-tags'),
        autoReload: isChecked('opt-auto-reload'),
        fullAuto: isChecked('opt-full-auto'),
        disableIff: isChecked('opt-disable-iff'),
        disableHitRegister: isChecked('opt-disable-hitreg'),
        playerCount: clamp(intVal('player-count'), 1, 24),
        players: readGrid(),
    };
}

async function applyConfig(config) {
    if (!config) return;

    setVal('game-type-select', config.gameType);
    await applyGameTypeMetadata(false);
    setVal('minutes', config.minutes ?? 10);
    setVal('countdown', config.countdown ?? 30);
    setVal('global-hp', config.globalHp ?? 10);
    setVal('global-shields', config.globalShields ?? 0);
    setVal('global-reloads', config.globalReloads ?? 0);
    setVal('global-mega', config.globalMega ?? 0);
    setVal('global-ammo', config.globalAmmo ?? 10);
    setVal('global-sleep', config.globalSleep ?? 0);
    setChk('opt-slow-tags', config.slowTags);
    setChk('opt-team-tags', config.teamTags);
    setChk('opt-auto-reload', config.autoReload);
    setChk('opt-full-auto', config.fullAuto);
    setChk('opt-disable-iff', config.disableIff ?? true);
    setChk('opt-disable-hitreg', config.disableHitRegister ?? true);
    setVal('player-count', clamp(Number(config.playerCount) || 3, 1, 24));
    buildPlayerSlots(config.players || []);
    updateOverview();
}

function renderPresets() {
    const list = document.getElementById('preset-list');
    if (!list) return;

    const presets = getPresets();
    if (!presets.length) {
        list.innerHTML = '<span class="muted-hint">No presets saved yet.</span>';
        return;
    }

    list.innerHTML = presets.map((preset, index) => {
        const config = preset.config || {};
        const details = `${esc(config.gameTypeLabel || 'Game')} · ${Number(config.playerCount) || 0} players`;
        return `<div class="preset-row">
          <button type="button" class="preset-load" onclick="loadPreset(${index})">
            <strong>${esc(preset.name)}</strong><span>${details}</span>
          </button>
          <button type="button" class="preset-delete" onclick="deletePreset(${index})" aria-label="Delete ${esc(preset.name)}">×</button>
        </div>`;
    }).join('');
}

function savePreset() {
    const dialog = document.getElementById('preset-dialog');
    const input = document.getElementById('preset-name-input');
    input.value = '';
    dialog.hidden = false;
    window.setTimeout(() => input.focus(), 30);
}

function closePresetDialog() {
    document.getElementById('preset-dialog').hidden = true;
}

function confirmSavePreset() {
    const input = document.getElementById('preset-name-input');
    const name = input.value.trim();
    if (!name) {
        input.focus();
        showToast('Enter a preset name.', 'warning');
        return;
    }

    const presets = getPresets();
    const existingIndex = presets.findIndex(preset => preset.name.toLowerCase() === name.toLowerCase());
    const entry = { name, config: captureConfig() };

    if (existingIndex >= 0) presets[existingIndex] = entry;
    else presets.push(entry);

    putPresets(presets);
    renderPresets();
    closePresetDialog();
    appendLog(`Preset "${name}" saved.`);
    showToast(`Preset “${name}” saved.`, 'success');
}

async function loadPreset(index) {
    const preset = getPresets()[index];
    if (!preset) return;

    await applyConfig(preset.config);
    appendLog(`Preset "${preset.name}" loaded.`);
    showToast(`Preset “${preset.name}” loaded.`, 'success');
}

function deletePreset(index) {
    const presets = getPresets();
    const preset = presets[index];
    if (!preset) return;
    if (!window.confirm(`Delete the preset “${preset.name}”?`)) return;

    presets.splice(index, 1);
    putPresets(presets);
    renderPresets();
    appendLog(`Preset "${preset.name}" deleted.`);
}

document.addEventListener('keydown', event => {
    const dialog = document.getElementById('preset-dialog');
    if (!dialog || dialog.hidden) return;
    if (event.key === 'Enter') confirmSavePreset();
    if (event.key === 'Escape') closePresetDialog();
});

// ── Game actions ──────────────────────────────────────────────────────────────
async function startHosting() {
    if (!validateConfiguration()) return;

    const countdownSeconds = intVal('countdown');
    if (countdownSeconds < 10) {
        showToast('A countdown of at least 10 seconds is recommended for tagger synchronization.', 'warning');
    }

    const select = document.getElementById('game-type-select');
    const request = {
        gameType: parseInt(select.value, 10),
        gameTypeLabel: select.options[select.selectedIndex]?.text || '',
        minutes: intVal('minutes'),
        countdownSeconds,
        globalHp: intVal('global-hp'),
        globalShields: intVal('global-shields'),
        globalReloads: intVal('global-reloads'),
        globalMega: intVal('global-mega'),
        globalStartingAmmo: intVal('global-ammo'),
        globalSleepTimeout: intVal('global-sleep'),
        slowTags: isChecked('opt-slow-tags'),
        teamTags: isChecked('opt-team-tags'),
        medicMode: false,
        autoReload: isChecked('opt-auto-reload'),
        fullAuto: isChecked('opt-full-auto'),
        disableIff: isChecked('opt-disable-iff'),
        disableHitRegister: isChecked('opt-disable-hitreg'),
        players: readGrid(),
    };

    await runAction('btn-start', '/api/game/start', {
        body: request,
        successMessage: 'Hosting started. Turn on the taggers in player order.',
    });
}

async function startCountdown() {
    await runAction('btn-countdown', '/api/game/countdown', {
        successMessage: 'Countdown started.',
    });
}

async function endGame() {
    if (!window.confirm('End the current game now?')) return;
    await runAction('btn-end', '/api/game/endgame', {
        successMessage: 'Game ended.',
    });
}

async function nextStep() {
    await runAction('btn-next', '/api/game/cleanup', {
        successMessage: 'Game cleared. The next setup is ready.',
    });
}

async function runAction(buttonId, url, { body = null, successMessage = '' } = {}) {
    if (actionInProgress) return;
    setActionBusy(buttonId);

    try {
        const options = { method: 'POST' };
        if (body !== null) {
            options.headers = { 'Content-Type': 'application/json' };
            options.body = JSON.stringify(body);
        }

        await fetchJson(url, options);
        if (successMessage) showToast(successMessage, 'success');
        await refreshSnapshots();
    } catch (error) {
        appendLog(`Action failed: ${error.message}`);
        showToast(error.message, 'error');
    } finally {
        clearActionBusy();
    }
}

function validateConfiguration() {
    const numberInputs = [...document.querySelectorAll('input[type="number"]')];
    for (const input of numberInputs) {
        if (!input.checkValidity()) {
            input.reportValidity();
            input.focus();
            return false;
        }
    }

    const names = [...document.querySelectorAll('.p-name')];
    for (const name of names) {
        if (name.value.trim().length > 40) {
            name.focus();
            showToast('Player names may contain at most 40 characters.', 'error');
            return false;
        }
    }

    return true;
}

// ── Diagnostics ───────────────────────────────────────────────────────────────
function toggleDebug() {
    const toggle = document.getElementById('debug-toggle');
    const body = document.getElementById('debug-body');
    const open = toggle.getAttribute('aria-expanded') !== 'true';
    toggle.setAttribute('aria-expanded', String(open));
    body.hidden = !open;
}

function applyState(snapshot) {
    if (!snapshot) return;
    currentSnapshot = snapshot;

    const state = (snapshot.hostingState || 'idle').toLowerCase();
    const players = snapshot.players || [];
    const stateKey = [
        state,
        snapshot.state || '',
        snapshot.guidedJoinHint || '',
        snapshot.isTeamGame ? 1 : 0,
        ...players.map(player => [
            player.id, player.name, player.confirmed ? 1 : 0,
            player.hitsScored || 0, player.hitsTaken || 0,
        ].join(':')),
    ].join('|');

    if (stateKey === _lastStateKey) {
        updateActionBar(state);
        return;
    }
    _lastStateKey = stateKey;

    const pill = document.getElementById('phase-pill');
    pill.textContent = String(snapshot.state || 'IDLE').toUpperCase();
    pill.className = `phase-pill ${phaseCss(state)}`;

    updateActionBar(state);
    document.getElementById('guided-join-hint').textContent = snapshot.guidedJoinHint || '';

    const readyCount = players.filter(player => player.confirmed).length;
    const plannedCount = clamp(intVal('player-count'), 1, 24);
    document.getElementById('overview-players').textContent = `${players.length} / ${plannedCount}`;
    document.getElementById('overview-ready').textContent = String(readyCount);
    document.getElementById('overview-message').textContent = overviewMessage(state, players.length, plannedCount);
    document.getElementById('live-summary').textContent = players.length
        ? `${players.length} connected · ${readyCount} ready`
        : 'No taggers connected.';

    const liveList = document.getElementById('live-player-list');
    if (!players.length) {
        liveList.innerHTML = `<div class="empty-state">
          <div class="empty-icon" aria-hidden="true">⌁</div>
          <strong>${state === 'idle' ? 'Waiting for a game' : 'Waiting for taggers'}</strong>
          <span>${state === 'idle' ? 'Start hosting, then turn on each tagger in player order.' : esc(snapshot.guidedJoinHint || 'Turn on the first tagger to join.')}</span>
        </div>`;
        return;
    }

    liveList.innerHTML = players.map(player => {
        const scored = Math.max(0, Math.trunc(Number(player.hitsScored) || 0));
        const taken = Math.max(0, Math.trunc(Number(player.hitsTaken) || 0));
        const identifier = snapshot.isTeamGame ? player.id : `P${player.slot}`;
        const details = state === 'playing' || state === 'summary' || state === 'gameover'
            ? `${scored} scored · ${taken} taken`
            : snapshot.isTeamGame ? `Team ${player.team}` : `Player ${player.slot}`;

        return `<article class="live-player${player.confirmed ? ' is-confirmed' : ''}">
          <span class="live-player-id">${esc(identifier)}</span>
          <span class="live-player-name">
            <strong>${esc(player.name || identifier)}</strong>
            <span>${esc(details)}</span>
          </span>
          <span class="live-player-status">${player.confirmed ? '✓ Ready' : 'Joining…'}</span>
        </article>`;
    }).join('');
}

function applyTelemetry(telemetry) {
    if (!telemetry) return;
    currentTelemetry = telemetry;

    const telemetryKey = [
        telemetry.relayConnected ? 1 : 0,
        telemetry.relayConnectedCount,
        telemetry.relayConnectAttempts,
        telemetry.relayTimeouts,
        telemetry.relayConnectFailures,
        telemetry.ioErrors,
        telemetry.gamesCompleted,
        telemetry.gamesStarted,
        telemetry.unexpectedPackets,
        telemetry.stateTimeouts,
    ].join('|');

    if (telemetryKey === _lastTelemetryKey) return;
    _lastTelemetryKey = telemetryKey;

    document.getElementById('telemetry-bar').textContent =
        `Relay ${telemetry.relayConnectedCount}/${telemetry.relayConnectAttempts} · ` +
        `Timeouts ${telemetry.relayTimeouts} · Failures ${telemetry.relayConnectFailures} · ` +
        `I/O ${telemetry.ioErrors} · Games ${telemetry.gamesCompleted}/${telemetry.gamesStarted} · ` +
        `Unexpected ${telemetry.unexpectedPackets} · State timeouts ${telemetry.stateTimeouts}`;

    const badge = document.getElementById('relay-badge');
    badge.className = `relay-badge ${telemetry.relayConnected ? 'relay-online' : 'relay-offline'}`;
    document.getElementById('relay-badge-text').textContent = telemetry.relayConnected
        ? 'Relay connected'
        : 'Relay offline';
}

function updateOverview() {
    const selected = gameTypes.find(type => String(type.id) === document.getElementById('game-type-select')?.value);
    const count = clamp(intVal('player-count') || 3, 1, 24);
    const game = selected?.label || '—';
    const connected = currentSnapshot?.players?.length || 0;
    const ready = currentSnapshot?.players?.filter(player => player.confirmed).length || 0;

    setText('overview-game', game);
    setText('overview-players', `${connected} / ${count}`);
    setText('overview-ready', ready);
}

function overviewMessage(state, connected, planned) {
    if (state === 'idle') return 'Ready to configure a game.';
    if (state === 'adding' || state === 'acknowledgeplayerassignment') {
        return connected >= planned ? 'All planned players are connected.' : `Waiting for ${planned - connected} more player${planned - connected === 1 ? '' : 's'}.`;
    }
    if (state === 'countdown' || state === 'resendcountdown') return 'Countdown in progress.';
    if (state === 'playing') return 'Game in progress.';
    if (state === 'summary' || state === 'gameover') return 'Final scores are ready.';
    return 'Session active.';
}

// ── Log and notifications ─────────────────────────────────────────────────────
function appendLog(message) {
    const text = String(message ?? '');
    logLines.push(text);
    if (logLines.length > MAX_LOG) logLines.shift();

    const box = document.getElementById('log-box');
    if (!box) return;

    const line = document.createElement('div');
    line.textContent = text;
    box.appendChild(line);
    while (box.children.length > MAX_LOG) box.removeChild(box.firstChild);
    box.scrollTop = box.scrollHeight;
}

function clearLog() {
    logLines = [];
    const box = document.getElementById('log-box');
    if (box) box.textContent = '';
}

function saveLog() {
    const blob = new Blob([logLines.join('\n')], { type: 'text/plain' });
    const url = URL.createObjectURL(blob);
    const anchor = document.createElement('a');
    anchor.href = url;
    anchor.download = `lazertag_log_${new Date().toISOString().replace(/[:.]/g, '-')}.txt`;
    document.body.appendChild(anchor);
    anchor.click();
    anchor.remove();
    window.setTimeout(() => URL.revokeObjectURL(url), 0);
}

function showToast(message, type = 'info') {
    const region = document.getElementById('toast-region');
    if (!region) return;

    const toast = document.createElement('div');
    toast.className = `toast ${type}`;
    toast.textContent = message;
    region.appendChild(toast);

    window.setTimeout(() => toast.remove(), 4200);
}

// ── Utilities ─────────────────────────────────────────────────────────────────
function phaseCss(state) {
    const allowed = new Set([
        'idle', 'adding', 'acknowledgeplayerassignment', 'countdown',
        'resendcountdown', 'playing', 'summary', 'gameover',
    ]);
    return `phase-${allowed.has(state) ? state : 'idle'}`;
}

function num(value) {
    const parsed = parseInt(String(value || '').trim(), 10);
    return Number.isNaN(parsed) ? null : parsed;
}

function intVal(id) {
    const element = document.getElementById(id);
    const parsed = parseInt(element?.value, 10);
    return Number.isNaN(parsed) ? 0 : parsed;
}

function isChecked(id) {
    return Boolean(document.getElementById(id)?.checked);
}

function setVal(id, value) {
    const element = document.getElementById(id);
    if (element && value !== undefined && value !== null) element.value = value;
}

function setChk(id, value) {
    const element = document.getElementById(id);
    if (element) element.checked = Boolean(value);
}

function setText(id, value) {
    const element = document.getElementById(id);
    if (element) element.textContent = String(value);
}

function clamp(value, min, max) {
    return Math.min(max, Math.max(min, Number(value) || min));
}

function esc(value) {
    return String(value ?? '')
        .replace(/&/g, '&amp;')
        .replace(/</g, '&lt;')
        .replace(/>/g, '&gt;')
        .replace(/"/g, '&quot;')
        .replace(/'/g, '&#39;');
}
