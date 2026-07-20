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
let signalRRestarting = false;
let signalRRetryTimer = null;
let refreshTimer = null;
let freshnessTimer = null;
let historyRefreshRetryTimer = null;
let actionInProgress = null;
let operatorToken = null;
let operatorAuthenticated = false;
let activeSetup = null;
let savedPlannedCount = null;
let lastHistoryState = '';
let lastStateUpdateMs = 0;
let lastTelemetryUpdateMs = 0;
let _lastFreshnessKey = '';
let _lastStateKey = null;
let _lastTelemetryKey = null;

const MAX_LOG = 1000;
const PRESETS_KEY = 'lazertag_presets';
const OPERATOR_TOKEN_KEY = 'lazertag_operator_token';
const ACTIVE_SETUP_KEY = 'lazertag_active_setup';
const SNAPSHOT_STALE_AFTER_MS = 12000;

const connection = new signalR.HubConnectionBuilder()
    .withUrl('/gamehub', { accessTokenFactory: () => operatorToken || '' })
    .withAutomaticReconnect([0, 2000, 5000, 10000])
    .build();

connection.on('Log', msg => appendLog(msg));
connection.on('State', snap => applyState(snap));
connection.on('Telemetry', tel => applyTelemetry(tel));
connection.onreconnecting(() => {
    appendLog('Control display reconnecting to the server…');
    markControlConnectionInterrupted();
});
connection.onreconnected(async () => {
    appendLog('Control display reconnected.');
    await refreshSnapshots();
});
connection.onclose(() => {
    if (signalRRestarting) return;
    appendLog('Live connection closed. Retrying…');
    markControlConnectionInterrupted();
    scheduleSignalRStart();
});

window.addEventListener('DOMContentLoaded', async () => {
    updateActionBar('idle');
    renderPresets();

    document.getElementById('login-form').addEventListener('submit', loginOperator);
    operatorToken = readSessionValue(OPERATOR_TOKEN_KEY);
    activeSetup = readActiveSetup();
    await checkOperatorSession();

    await loadGameTypes();
    if (operatorAuthenticated) await Promise.all([loadSavedPlayers(), loadHistory()]);
    buildPlayerSlots(savedPlayers.rows);
    updateOverview();

    await refreshSnapshots();
    await startSignalR();

    refreshTimer = window.setInterval(refreshSnapshots, 5000);
    freshnessTimer = window.setInterval(refreshFreshnessUi, 1000);
    document.getElementById('player-count').addEventListener('change', () => buildPlayerSlots());

    const dialog = document.getElementById('preset-dialog');
    dialog.addEventListener('click', event => {
        if (event.target === dialog) closePresetDialog();
    });
});

window.addEventListener('beforeunload', () => {
    if (refreshTimer) window.clearInterval(refreshTimer);
    if (freshnessTimer) window.clearInterval(freshnessTimer);
    if (historyRefreshRetryTimer) window.clearTimeout(historyRefreshRetryTimer);
    if (signalRRetryTimer) window.clearTimeout(signalRRetryTimer);
});

// ── Server communication ──────────────────────────────────────────────────────
async function fetchJson(url, options = {}) {
    const headers = new Headers(options.headers || {});
    if (operatorToken) headers.set('Authorization', `Bearer ${operatorToken}`);

    const response = await fetch(url, { ...options, headers });
    const text = await response.text();
    let data = {};

    if (text) {
        try { data = JSON.parse(text); }
        catch { data = { error: text }; }
    }

    if (!response.ok) {
        if (response.status === 401 && url !== '/api/operator/login') {
            setOperatorAuthenticated(false, true);
        }

        const error = new Error(data.error || data.title || response.statusText || `Request failed (${response.status})`);
        error.status = response.status;
        throw error;
    }

    return data;
}

// ── Operator access ──────────────────────────────────────────────────────────
async function checkOperatorSession() {
    if (!operatorToken) {
        setOperatorAuthenticated(false, true);
        return;
    }

    try {
        await fetchJson('/api/operator/status');
        setOperatorAuthenticated(true, false);
    } catch {
        setOperatorAuthenticated(false, true);
    }
}

async function loginOperator(event) {
    event.preventDefault();
    const pinInput = document.getElementById('operator-pin');
    const submit = document.getElementById('login-submit');
    const errorText = document.getElementById('login-error');

    if (!pinInput.checkValidity()) {
        pinInput.reportValidity();
        return;
    }

    submit.disabled = true;
    errorText.hidden = true;
    try {
        const result = await fetchJson('/api/operator/login', {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ pin: pinInput.value }),
        });

        operatorToken = result.token;
        writeSessionValue(OPERATOR_TOKEN_KEY, operatorToken);
        pinInput.value = '';
        setOperatorAuthenticated(true, false);
        await restartSignalRForOperator();
        await Promise.all([loadSavedPlayers(), loadHistory()]);
        // PlayerSettings is saved when hosting starts, so rebuilding here gives a
        // newly authenticated mid-game browser the real locked roster instead of
        // the page's default three placeholder rows.
        buildPlayerSlots(savedPlayers.rows);
        updateActionBar(currentSnapshot?.hostingState || 'idle');
        showToast('Operator controls unlocked.', 'success');
    } catch (error) {
        errorText.textContent = error.status === 429
            ? 'Too many attempts. Wait one minute and try again.'
            : error.message;
        errorText.hidden = false;
        pinInput.select();
    } finally {
        submit.disabled = false;
    }
}

function setOperatorAuthenticated(authenticated, showDialog) {
    operatorAuthenticated = Boolean(authenticated);
    if (!operatorAuthenticated) {
        operatorToken = null;
        removeSessionValue(OPERATOR_TOKEN_KEY);
    }

    const button = document.getElementById('operator-button');
    const text = document.getElementById('operator-button-text');
    if (button && text) {
        button.classList.toggle('operator-ready', operatorAuthenticated);
        button.classList.toggle('operator-locked', !operatorAuthenticated);
        button.setAttribute('aria-label', operatorAuthenticated ? 'Lock operator controls' : 'Unlock operator controls');
        text.textContent = operatorAuthenticated ? 'Operator ready' : 'Operator locked';
    }

    const dialog = document.getElementById('login-dialog');
    if (dialog) dialog.hidden = operatorAuthenticated || !showDialog;
    const historyRefresh = document.getElementById('history-refresh');
    if (historyRefresh) historyRefresh.disabled = !operatorAuthenticated;
    if (!operatorAuthenticated) renderHistoryLocked();
    updateActionBar(currentSnapshot?.hostingState || 'idle');

    if (!operatorAuthenticated && showDialog) {
        window.setTimeout(() => document.getElementById('operator-pin')?.focus(), 0);
    }
}

function showLoginDialog() {
    const dialog = document.getElementById('login-dialog');
    if (dialog) dialog.hidden = false;
    window.setTimeout(() => document.getElementById('operator-pin')?.focus(), 0);
}

async function toggleOperatorAccess() {
    if (!operatorAuthenticated) {
        showLoginDialog();
        return;
    }

    if (!window.confirm('Lock operator controls on this device?')) return;
    try { await fetchJson('/api/operator/logout', { method: 'POST' }); }
    catch { /* Local lock still takes effect if the network disappeared. */ }
    setOperatorAuthenticated(false, true);
    await restartSignalRForOperator();
}

async function restartSignalRForOperator() {
    if (signalRRetryTimer) {
        window.clearTimeout(signalRRetryTimer);
        signalRRetryTimer = null;
    }

    signalRRestarting = true;
    try {
        if (connection.state !== signalR.HubConnectionState.Disconnected) {
            try { await connection.stop(); }
            catch { /* The regular reconnect path will recover. */ }
        }
    } finally {
        signalRRestarting = false;
    }
    await startSignalR();
}

function readSessionValue(key) {
    try { return sessionStorage.getItem(key); }
    catch { return null; }
}

function writeSessionValue(key, value) {
    try { sessionStorage.setItem(key, value); }
    catch { /* The current page still retains the token in memory. */ }
}

function removeSessionValue(key) {
    try { sessionStorage.removeItem(key); }
    catch { /* Nothing else to do. */ }
}

function readActiveSetup() {
    const raw = readSessionValue(ACTIVE_SETUP_KEY);
    if (!raw) return null;

    try {
        const value = JSON.parse(raw);
        const plannedCount = Number(value?.plannedCount);
        const teamCount = value?.teamCount === null ? null : Number(value?.teamCount);
        if (!Number.isInteger(plannedCount) || plannedCount < 2 || plannedCount > 24) return null;
        if (teamCount !== null && (!Number.isInteger(teamCount) || teamCount < 1 || teamCount > 3)) return null;
        return { plannedCount, teamCount };
    } catch {
        return null;
    }
}

function rememberActiveSetup(setup) {
    activeSetup = setup;
    writeSessionValue(ACTIVE_SETUP_KEY, JSON.stringify(setup));
}

function forgetActiveSetup() {
    activeSetup = null;
    removeSessionValue(ACTIVE_SETUP_KEY);
}

async function refreshSnapshots() {
    const [stateResult, telemetryResult] = await Promise.allSettled([
        fetchJson('/api/game/state'),
        fetchJson('/api/game/telemetry'),
    ]);

    if (stateResult.status === 'fulfilled') applyState(stateResult.value);
    else console.debug('State snapshot refresh failed:', stateResult.reason);

    if (telemetryResult.status === 'fulfilled') applyTelemetry(telemetryResult.value);
    else console.debug('Telemetry snapshot refresh failed:', telemetryResult.reason);

    refreshFreshnessUi();
}

function isSnapshotFresh(updatedAtMs) {
    return updatedAtMs > 0 && Date.now() - updatedAtMs <= SNAPSHOT_STALE_AFTER_MS;
}

function isStateSnapshotFresh() {
    return isSnapshotFresh(lastStateUpdateMs);
}

function isTelemetrySnapshotFresh() {
    return isSnapshotFresh(lastTelemetryUpdateMs);
}

function markControlConnectionInterrupted() {
    lastStateUpdateMs = 0;
    lastTelemetryUpdateMs = 0;
    currentTelemetry = null;
    _lastTelemetryKey = null;
    renderTelemetryUnknown();
    refreshFreshnessUi(true);
}

function renderTelemetryUnknown() {
    const telemetryBar = document.getElementById('telemetry-bar');
    if (telemetryBar) telemetryBar.textContent = 'Connection interrupted · waiting for fresh arena telemetry';

    const badge = document.getElementById('relay-badge');
    if (badge) badge.className = 'relay-badge relay-offline';
    const badgeText = document.getElementById('relay-badge-text');
    if (badgeText) badgeText.textContent = 'Relay status unknown';
}

function refreshFreshnessUi(force = false) {
    const stateFresh = isStateSnapshotFresh();
    const telemetryFresh = isTelemetrySnapshotFresh();
    const key = `${stateFresh ? 1 : 0}:${telemetryFresh ? 1 : 0}`;
    if (!force && key === _lastFreshnessKey) return;
    _lastFreshnessKey = key;

    if (!telemetryFresh) renderTelemetryUnknown();
    updateActionBar(currentSnapshot?.hostingState || 'idle');
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
    if (!operatorAuthenticated) return;
    try {
        savedPlayers = await fetchJson('/api/game/savedplayers');
        savedPlannedCount = clamp(parseInt(savedPlayers.playerCount, 10) || 3, 2, 24);
        if (!isSetupLocked()) {
            const count = clamp(savedPlannedCount, 2, getMaxPlayers());
            savedPlayers.playerCount = count;
            document.getElementById('player-count').value = count;
        }
        updateOverview();
    } catch (error) {
        console.debug('Saved player settings unavailable:', error);
    }
}

async function loadHistory({ preserveDetail = false } = {}) {
    const list = document.getElementById('history-list');
    const summary = document.getElementById('history-summary');
    const detail = document.getElementById('history-detail');
    if (!list || !summary) return;

    if (!operatorAuthenticated) {
        renderHistoryLocked();
        return;
    }

    list.innerHTML = '<div class="history-empty">Loading saved results…</div>';
    if (detail && !preserveDetail) detail.hidden = true;
    try {
        const response = await fetchJson('/api/history?limit=8');
        const games = Array.isArray(response)
            ? response.filter(game => isHistoryId(game?.gameId))
            : [];
        if (!games.length) {
            summary.textContent = 'Completed games will appear here automatically.';
            list.innerHTML = '<div class="history-empty">No completed games have been saved yet.</div>';
            return;
        }

        summary.textContent = `${games.length} most recent saved game${games.length === 1 ? '' : 's'}.`;
        list.innerHTML = games.map(game => {
            const complete = game.resultsFinalized === true
                ? (game.allPlayersDebriefed ? 'Complete' : 'Finalized with incomplete debrief')
                : 'Results unavailable';
            const flags = [complete, `${Number(game.playerCount) || 0} players`, formatHistoryDate(game.endedAtUtc)];
            if (game.endedEarly) flags.unshift('Ended early');
            if (game.forced) flags.unshift('Forced');
            const id = String(game.gameId);
            return `<article class="history-row">
              <span class="history-row-main">
                <strong>${esc(game.gameTypeLabel || 'Completed Game')}</strong>
                <span>${esc(flags.join(' · '))}</span>
              </span>
              <span class="history-row-actions">
                <button type="button" class="pill-btn" onclick="showHistory('${id}')">View</button>
                <button type="button" class="pill-btn" onclick="downloadHistory('${id}', 'csv')">CSV</button>
              </span>
            </article>`;
        }).join('');
    } catch (error) {
        summary.textContent = 'Saved results are temporarily unavailable.';
        list.innerHTML = `<div class="history-empty">${esc(error.message)}</div>`;
    }
}

function scheduleHistoryRefreshRetry() {
    if (historyRefreshRetryTimer) window.clearTimeout(historyRefreshRetryTimer);
    historyRefreshRetryTimer = window.setTimeout(() => {
        historyRefreshRetryTimer = null;
        const state = (currentSnapshot?.hostingState || '').toLowerCase();
        if (operatorAuthenticated && state === 'gameover') {
            void loadHistory({ preserveDetail: true });
        }
    }, 1500);
}

function renderHistoryLocked() {
    const list = document.getElementById('history-list');
    const summary = document.getElementById('history-summary');
    const detail = document.getElementById('history-detail');
    if (summary) summary.textContent = 'Unlock operator controls to view saved games.';
    if (list) list.innerHTML = '<div class="history-empty">Saved results require operator access.</div>';
    if (detail) detail.hidden = true;
}

async function showHistory(gameId) {
    const detail = document.getElementById('history-detail');
    if (!detail || !operatorAuthenticated || !isHistoryId(gameId)) return;

    detail.hidden = false;
    detail.innerHTML = '<div class="history-empty">Loading result…</div>';
    try {
        const game = await fetchJson(`/api/history/${encodeURIComponent(gameId)}`);
        const resultsFinalized = game.resultsFinalized === true;
        const players = Array.isArray(game.players) ? [...game.players] : [];
        if (resultsFinalized) {
            players.sort((a, b) => historyRank(a) - historyRank(b)
                || historyNumber(b.score, 0) - historyNumber(a.score, 0));
        } else {
            players.sort((a, b) => historyNumber(a.team, 0) - historyNumber(b.team, 0)
                || historyNumber(a.slot, 0) - historyNumber(b.slot, 0)
                || String(a.name || a.id || '').localeCompare(String(b.name || b.id || '')));
        }
        const flags = [
            resultsFinalized ? 'Results finalized' : 'Results unavailable',
            game.allPlayersDebriefed ? 'All taggers debriefed' : 'Incomplete debrief',
            game.endedEarly ? 'Ended early' : 'Normal duration',
            game.forced ? 'Forced completion' : 'Normal completion',
        ];
        const id = isHistoryId(game.gameId) ? String(game.gameId) : String(gameId);
        const teamResults = renderHistoryTeamResults(game, resultsFinalized);
        detail.innerHTML = `<div class="history-detail-header">
          <span><strong>${esc(game.gameTypeLabel || 'Completed Game')}</strong><span>${esc(formatHistoryDate(game.endedAtUtc))}</span></span>
          <span class="history-row-actions">
            <button type="button" class="pill-btn" onclick="downloadHistory('${id}', 'json')">JSON</button>
            <button type="button" class="pill-btn" onclick="downloadHistory('${id}', 'csv')">CSV</button>
            <button type="button" class="pill-btn" onclick="closeHistoryDetail()">Close</button>
          </span>
        </div>
        <div class="history-flags">${esc(flags.join(' · '))}</div>
        ${teamResults}
        <div class="history-flags"><strong>Player results</strong></div>
        <div class="history-score-list">${players.map((player, index) => `
          <div class="history-score-row">
            <span>${historyPlayerRankLabel(player, index, resultsFinalized)}</span>
            <strong>${esc(player.name || player.id)}</strong>
            <span class="history-score-value">${historyPlayerScoreLabel(player, resultsFinalized)}</span>
            <span class="history-score-hits">${historyNumber(player.hitsScored, 0)}/${historyNumber(player.hitsTaken, 0)} reported hits</span>
          </div>`).join('')}</div>`;
        detail.scrollIntoView({ behavior: 'smooth', block: 'nearest' });
    } catch (error) {
        detail.innerHTML = `<div class="history-empty">${esc(error.message)}</div>`;
    }
}

function closeHistoryDetail() {
    const detail = document.getElementById('history-detail');
    if (detail) detail.hidden = true;
}

async function downloadHistory(gameId, format) {
    if (!operatorAuthenticated || !operatorToken || !isHistoryId(gameId)) return;
    const safeFormat = format === 'json' ? 'json' : 'csv';
    try {
        const response = await fetch(`/api/history/${encodeURIComponent(gameId)}/download.${safeFormat}`, {
            headers: { Authorization: `Bearer ${operatorToken}` },
        });
        if (!response.ok) throw new Error(`Download failed (${response.status}).`);
        const blob = await response.blob();
        const url = URL.createObjectURL(blob);
        const anchor = document.createElement('a');
        anchor.href = url;
        anchor.download = `lazertag-${gameId}.${safeFormat}`;
        document.body.appendChild(anchor);
        anchor.click();
        anchor.remove();
        window.setTimeout(() => URL.revokeObjectURL(url), 1000);
    } catch (error) {
        showToast(error.message, 'error');
    }
}

function isHistoryId(value) {
    return /^[a-f0-9]{32}$/i.test(String(value || ''));
}

function historyRank(player) {
    const rank = Number(player?.rank);
    return Number.isInteger(rank) && rank > 0 ? rank : Number.MAX_SAFE_INTEGER;
}

function historyNumber(value, fallback = null) {
    if (value === null || value === undefined || value === '' || typeof value === 'boolean') return fallback;
    const number = Number(value);
    return Number.isFinite(number) ? number : fallback;
}

function historyPlayerRankLabel(player, _index, resultsFinalized) {
    if (!resultsFinalized) return '—';
    const rank = historyNumber(player?.rank);
    return Number.isInteger(rank) && rank > 0 ? `#${rank}` : '—';
}

function historyPlayerScoreLabel(player, resultsFinalized) {
    if (!resultsFinalized) return 'Unavailable';
    const value = historyNumber(player?.score);
    return value === null ? 'Unavailable' : `${value} pts`;
}

function renderHistoryTeamResults(game, resultsFinalized) {
    const teams = Array.isArray(game?.teams) ? [...game.teams] : [];
    if (!game?.isTeamGame && !teams.length) return '';

    if (!resultsFinalized) {
        return `<div class="history-flags"><strong>Team results</strong></div>
          <div class="history-empty">Team ranks and winner are unavailable because results were not finalized.</div>`;
    }

    const validTeams = teams
        .filter(team => Number.isInteger(historyNumber(team?.team)) && historyNumber(team?.team) > 0)
        .sort((a, b) => historyRank(a) - historyRank(b)
            || historyNumber(b.score, 0) - historyNumber(a.score, 0)
            || historyNumber(a.team, 0) - historyNumber(b.team, 0));
    if (!validTeams.length) {
        return `<div class="history-flags"><strong>Team results</strong></div>
          <div class="history-empty">Authoritative team results are unavailable for this game.</div>`;
    }

    const rows = validTeams.map(team => {
        const number = historyNumber(team.team, 0);
        const rank = historyNumber(team.rank);
        const score = historyNumber(team.score);
        const playerCount = historyNumber(team.playerCount);
        const rankLabel = Number.isInteger(rank) && rank > 0 ? `#${rank}` : '—';
        const scoreLabel = score === null ? 'Unavailable' : `${score} pts`;
        const detail = rank === 1
            ? 'Winner'
            : playerCount === null ? '' : `${playerCount} player${playerCount === 1 ? '' : 's'}`;
        return `<div class="history-score-row">
          <span>${rankLabel}</span>
          <strong>${esc(`Team ${number}`)}</strong>
          <span class="history-score-value">${scoreLabel}</span>
          <span class="history-score-hits">${esc(detail)}</span>
        </div>`;
    }).join('');

    return `<div class="history-flags"><strong>Team results</strong></div>
      <div class="history-score-list">${rows}</div>`;
}

function formatHistoryDate(value) {
    const date = new Date(value);
    return Number.isNaN(date.getTime()) ? 'Unknown time' : date.toLocaleString();
}

async function onGameTypeChange() {
    await applyGameTypeMetadata(true);
}

async function applyGameTypeMetadata(applyDefaults) {
    const selectedId = parseInt(document.getElementById('game-type-select').value, 10);
    const metadata = gameTypes.find(type => Number(type.id) === selectedId);

    currentTeamCount = Math.max(1, Number(metadata?.teamCount) || 1);
    isTeamGame = currentTeamCount > 1;
    syncPlayerCountLimit(metadata?.maxPlayers);
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
    const stateFresh = isStateSnapshotFresh();
    const isIdle = normalized === 'idle' || normalized === '';
    const isLobby = normalized === 'adding' || normalized === 'acknowledgeplayerassignment';
    const isActive = normalized === 'countdown' || normalized === 'resendcountdown' || normalized === 'playing';
    const isSummary = ['summary', 'debriefing', 'waitingfordebriefing'].includes(normalized);
    const isOver = normalized === 'gameover';

    const endLabel = document.getElementById('end-action-label');
    if (endLabel) endLabel.textContent = isSummary ? 'Force Finish' : 'End Game';

    const readiness = getCountdownReadiness(normalized);
    setBtn('btn-start', stateFresh && isIdle);
    setBtn('btn-countdown', isLobby && readiness.ready);
    setBtn('btn-end', stateFresh && (isLobby || isActive || isSummary));
    setBtn('btn-next', stateFresh && isOver);
    setSetupLocked(!isIdle, normalized);
    renderCountdownReadiness(readiness);
}

function setBtn(id, enabled) {
    const button = document.getElementById(id);
    if (!button) return;
    button.dataset.available = enabled ? 'true' : 'false';
    button.disabled = !enabled || actionInProgress !== null || !operatorAuthenticated;
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

function isLobbyState(state) {
    const normalized = (state || '').toLowerCase();
    return normalized === 'adding' || normalized === 'acknowledgeplayerassignment';
}

function isSetupLocked() {
    const state = (currentSnapshot?.hostingState || 'idle').toLowerCase();
    return state !== '' && state !== 'idle';
}

function setSetupLocked(locked, state) {
    document.body.classList.toggle('setup-is-locked', locked);

    document.querySelectorAll('[data-setup-section]').forEach(section => {
        section.classList.toggle('is-locked', locked);
        section.setAttribute('aria-disabled', String(locked));

        section.querySelectorAll('button, input, select').forEach(control => {
            if (control.dataset.setupBaseDisabled === undefined) {
                control.dataset.setupBaseDisabled = control.disabled ? 'true' : 'false';
            }
            control.disabled = locked || control.dataset.setupBaseDisabled === 'true';
        });
    });

    const banner = document.getElementById('setup-lock-message');
    if (banner) {
        banner.hidden = !locked;
        const detail = banner.querySelector('span');
        if (detail) {
            detail.textContent = state === 'gameover'
                ? 'Results are fixed. Select New Game to unlock settings and the roster.'
                : 'Game settings and the roster stay fixed until this session is ended and cleared.';
        }
    }

    if (locked) {
        const presetDialog = document.getElementById('preset-dialog');
        if (presetDialog) presetDialog.hidden = true;
    }
}

function configuredPlannedCount() {
    const serverPlannedCount = Number(currentSnapshot?.plannedPlayerCount);
    if (Number.isInteger(serverPlannedCount) && serverPlannedCount >= 2 && serverPlannedCount <= 24) {
        return serverPlannedCount;
    }

    const fallback = isSetupLocked() && savedPlannedCount !== null
        ? savedPlannedCount
        : intVal('player-count');
    return clamp(activeSetup?.plannedCount ?? fallback ?? 3, 2, 24);
}

function evaluateTeamCoverage(state, players, stateFresh) {
    if (!stateFresh) {
        return { ok: false, waiting: true, detail: 'Waiting for a fresh arena state update.' };
    }

    if (!isLobbyState(state)) {
        return { ok: false, waiting: true, detail: 'Checked while taggers are joining.' };
    }

    if (!currentSnapshot?.isTeamGame) {
        return { ok: true, detail: 'Solo game — team coverage is not required.' };
    }

    if (!players.length) {
        return { ok: false, waiting: true, detail: 'Waiting for assigned taggers.' };
    }

    const teams = players.map(player => Number(player.team));
    if (teams.some(team => !Number.isInteger(team) || team < 1)) {
        return { ok: false, detail: 'A joined tagger does not have a valid team assignment.' };
    }

    const represented = new Set(teams);
    const serverTeamCount = Number(currentSnapshot?.teamCount);
    const expectedTeamCount = Number.isInteger(serverTeamCount) && serverTeamCount > 0
        ? serverTeamCount
        : Number(activeSetup?.teamCount);
    if (Number.isInteger(expectedTeamCount) && expectedTeamCount > 1) {
        const outOfRange = teams.some(team => team > expectedTeamCount);
        if (outOfRange) {
            return { ok: false, detail: `A tagger is assigned outside Team 1–${expectedTeamCount}.` };
        }

        const missing = Array.from({ length: expectedTeamCount }, (_, index) => index + 1)
            .filter(team => !represented.has(team));
        if (missing.length) {
            return {
                ok: false,
                detail: `Missing ${missing.map(team => `Team ${team}`).join(', ')}. Every team needs a player.`,
            };
        }

        return { ok: true, detail: `All ${expectedTeamCount} teams have at least one player.` };
    }

    // A browser opened after hosting began does not know the selected game's declared
    // team count. It can still reject invalid assignments and one-sided team rosters.
    if (represented.size < 2) {
        return { ok: false, detail: 'Only one team is represented; at least two teams are required.' };
    }

    return { ok: true, detail: `${represented.size} assigned teams are represented.` };
}

function getCountdownReadiness(stateOverride) {
    const state = (stateOverride || currentSnapshot?.hostingState || 'idle').toLowerCase();
    const players = Array.isArray(currentSnapshot?.players) ? currentSnapshot.players : [];
    const planned = configuredPlannedCount();
    const confirmed = players.filter(player => player.confirmed).length;
    const stateFresh = isStateSnapshotFresh();
    const telemetryFresh = isTelemetrySnapshotFresh();
    const isLobby = stateFresh && isLobbyState(state);
    const relayKnown = telemetryFresh && currentTelemetry !== null;
    const relayConnected = Boolean(currentTelemetry?.relayConnected);

    const checks = [
        {
            id: 'relay',
            ok: relayKnown && relayConnected,
            waiting: !relayKnown,
            detail: !telemetryFresh
                ? 'Relay status is stale; waiting for fresh arena telemetry.'
                : !relayKnown
                    ? 'Checking relay status.'
                : relayConnected ? 'ESP32 relay is connected.' : 'ESP32 relay is offline.',
        },
        {
            id: 'phase',
            ok: isLobby,
            waiting: !stateFresh || state === 'idle' || state === '',
            detail: !stateFresh
                ? 'Connection interrupted; waiting for a fresh arena state.'
                : isLobby
                ? 'Hosting lobby is accepting taggers.'
                : (state === 'idle' || state === '')
                    ? 'Start hosting to open the lobby.'
                    : 'The lobby phase has ended.',
        },
        {
            id: 'roster',
            ok: isLobby && players.length === planned,
            waiting: !isLobby || players.length < planned,
            detail: !isLobby
                ? 'Waiting for the hosting lobby.'
                : players.length === planned
                    ? `Exact roster connected: ${players.length} of ${planned}.`
                    : players.length < planned
                        ? `Connected ${players.length} of ${planned}; waiting for ${planned - players.length}.`
                        : `Connected ${players.length} of ${planned}; ${players.length - planned} extra tagger${players.length - planned === 1 ? '' : 's'}.`,
        },
        {
            id: 'confirmed',
            ok: isLobby && players.length > 0 && confirmed === players.length,
            waiting: !isLobby || players.length === 0 || confirmed < players.length,
            detail: !isLobby
                ? 'Waiting for the hosting lobby.'
                : players.length === 0
                    ? 'Waiting for joined taggers.'
                    : confirmed === players.length
                        ? `All ${confirmed} joined taggers confirmed their assignments.`
                        : `${confirmed} of ${players.length} joined taggers confirmed.`,
        },
        { id: 'teams', ...evaluateTeamCoverage(state, players, stateFresh) },
    ];

    const ready = stateFresh && telemetryFresh && isLobby && checks.every(check => check.ok);
    let blocker = '';
    if (!stateFresh) {
        blocker = 'Connection interrupted; waiting for a fresh arena state.';
    } else if (!telemetryFresh) {
        blocker = 'Relay status is stale; waiting for fresh arena telemetry.';
    } else if (state === 'idle' || state === '') {
        blocker = 'Start hosting to begin the tagger readiness checks.';
    } else if (isLobby) {
        blocker = checks.find(check => !check.ok)?.detail || '';
    }

    return { state, checks, ready, blocker, stateFresh, telemetryFresh };
}

function renderCountdownReadiness(readiness) {
    if (!readiness) return;

    readiness.checks.forEach(check => {
        const item = document.getElementById(`readiness-${check.id}`);
        if (!item) return;

        const statusClass = check.ok ? 'is-ready' : check.waiting ? 'is-waiting' : 'is-blocked';
        item.className = `readiness-item ${statusClass}`;
        const icon = item.querySelector('.readiness-icon');
        const detail = item.querySelector('small');
        if (icon) icon.textContent = check.ok ? '✓' : check.waiting ? '…' : '!';
        if (detail) detail.textContent = check.detail;
    });

    const summary = document.getElementById('readiness-summary');
    if (summary) {
        summary.classList.toggle('is-ready', readiness.ready);
        if (readiness.ready) summary.textContent = 'Ready — all checks passed. Countdown is enabled.';
        else if (!readiness.stateFresh) summary.textContent = `Not ready: ${readiness.blocker}`;
        else if (!readiness.telemetryFresh && isLobbyState(readiness.state)) summary.textContent = `Not ready: ${readiness.blocker}`;
        else if (readiness.state === 'idle' || readiness.state === '') summary.textContent = 'Configure the game, then start hosting.';
        else if (isLobbyState(readiness.state)) summary.textContent = `Not ready: ${readiness.blocker}`;
        else if (readiness.state === 'countdown' || readiness.state === 'resendcountdown') summary.textContent = 'Countdown is already in progress.';
        else if (readiness.state === 'playing') summary.textContent = 'Game in progress.';
        else if (readiness.state === 'gameover') summary.textContent = 'Game complete. Select New Game to prepare another session.';
        else summary.textContent = 'Session active; countdown is unavailable.';
    }

    const actionStatus = document.getElementById('countdown-action-status');
    const countdownButton = document.getElementById('btn-countdown');
    const showActionStatus = isLobbyState(readiness.state);
    if (actionStatus) {
        actionStatus.hidden = !showActionStatus;
        actionStatus.classList.toggle('is-ready', readiness.ready);
        actionStatus.textContent = readiness.ready
            ? 'All checks passed — countdown is ready.'
            : readiness.blocker;
    }
    if (countdownButton) {
        countdownButton.title = showActionStatus && !readiness.ready ? readiness.blocker : '';
    }
}

// ── Player setup ──────────────────────────────────────────────────────────────
function buildPlayerSlots(rows) {
    const countInput = document.getElementById('player-count');
    const count = clamp(parseInt(countInput.value, 10) || 3, 2, getMaxPlayers());
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
    const plannedPlayers = readGrid();
    if (!validateConfiguration(plannedPlayers)) return;

    const countdownSeconds = intVal('countdown');
    if (countdownSeconds < 10) {
        showToast('A countdown of at least 10 seconds is recommended for tagger synchronization.', 'warning');
    }

    const select = document.getElementById('game-type-select');
    const pendingSetup = {
        plannedCount: plannedPlayers.length,
        teamCount: isTeamGame ? currentTeamCount : 1,
    };
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
        players: plannedPlayers,
    };

    const started = await runAction('btn-start', '/api/game/start', {
        body: request,
        successMessage: 'Hosting started. Turn on the taggers in player order.',
    });
    if (started) {
        savedPlannedCount = pendingSetup.plannedCount;
        rememberActiveSetup(pendingSetup);
        updateActionBar(currentSnapshot?.hostingState || 'idle');
    }
}

async function startCountdown() {
    const readiness = getCountdownReadiness();
    if (!readiness.ready) {
        renderCountdownReadiness(readiness);
        showToast(readiness.blocker || 'Countdown is not ready yet.', 'warning');
        return;
    }

    await runAction('btn-countdown', '/api/game/countdown', {
        successMessage: 'Countdown started.',
    });
}

async function endGame() {
    const isSummary = (currentSnapshot?.hostingState || '').toLowerCase() === 'summary';
    const message = isSummary
        ? 'Force debriefing to finish? Taggers that have not reported will be dropped and their scores may be incomplete.'
        : 'End the current game now?';
    if (!window.confirm(message)) return;
    await runAction('btn-end', '/api/game/endgame', {
        successMessage: isSummary ? 'Debriefing forced to Game Over.' : 'Game ended.',
    });
}

async function nextStep() {
    await runAction('btn-next', '/api/game/next', {
        successMessage: 'Game cleared. The next setup is ready.',
    });
}

async function runAction(buttonId, url, { body = null, successMessage = '' } = {}) {
    if (!operatorAuthenticated) {
        showLoginDialog();
        return;
    }
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
        return true;
    } catch (error) {
        appendLog(`Action failed: ${error.message}`);
        showToast(error.message, 'error');
        return false;
    } finally {
        clearActionBusy();
    }
}

function validateConfiguration(plannedPlayers = readGrid()) {
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

    if (isTeamGame) {
        const teamCount = Math.max(2, Number(currentTeamCount) || 2);
        if (plannedPlayers.length < teamCount) {
            showToast(`This game needs at least ${teamCount} players so every team can be represented.`, 'error');
            document.getElementById('player-count')?.focus();
            return false;
        }

        const fixedTeams = new Set();
        const fixedTeamCounts = new Map();
        let autoCount = 0;
        for (const player of plannedPlayers) {
            const team = Number(player.teamPreference);
            if (Number.isInteger(team) && team >= 1 && team <= teamCount) {
                fixedTeams.add(team);
                fixedTeamCounts.set(team, (fixedTeamCounts.get(team) || 0) + 1);
            }
            else autoCount++;
        }

        const overCapacityTeam = [...fixedTeamCounts.entries()].find(([, count]) => count > 8)?.[0];
        if (overCapacityTeam) {
            showToast(`Team ${overCapacityTeam} has more than 8 planned players. Move at least one player to Auto or another team.`, 'error');
            return false;
        }

        const missingTeams = Array.from({ length: teamCount }, (_, index) => index + 1)
            .filter(team => !fixedTeams.has(team));
        if (autoCount < missingTeams.length) {
            const missingLabel = missingTeams.map(team => `Team ${team}`).join(', ');
            showToast(`Team plan cannot cover ${missingLabel}. Assign those teams or leave at least ${missingTeams.length} player${missingTeams.length === 1 ? '' : 's'} on Auto.`, 'error');
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

function hydrateActiveSessionFields(snapshot) {
    const gameTypeId = Number(snapshot?.gameTypeId);
    const selectedType = gameTypes.find(type => Number(type.id) === gameTypeId);
    const gameTypeSelect = document.getElementById('game-type-select');
    if (gameTypeSelect && selectedType) gameTypeSelect.value = String(selectedType.id);

    const serverTeamCount = Number(snapshot?.teamCount);
    currentTeamCount = Number.isInteger(serverTeamCount) && serverTeamCount > 0
        ? serverTeamCount
        : Math.max(1, Number(selectedType?.teamCount) || 1);
    isTeamGame = Boolean(snapshot?.isTeamGame);

    const minutes = Number(snapshot?.gameMinutes);
    if (Number.isInteger(minutes) && minutes >= 1 && minutes <= 255) setVal('minutes', minutes);
    const countdownSeconds = Number(snapshot?.countdownSeconds);
    if (Number.isInteger(countdownSeconds) && countdownSeconds >= 5 && countdownSeconds <= 255) {
        setVal('countdown', countdownSeconds);
    }

    const settings = snapshot?.settings;
    if (settings && typeof settings === 'object') {
        setVal('global-hp', settings.globalHp);
        setVal('global-shields', settings.globalShields);
        setVal('global-reloads', settings.globalReloads);
        setVal('global-mega', settings.globalMega);
        setVal('global-ammo', settings.globalStartingAmmo);
        setVal('global-sleep', settings.globalSleepTimeout);
        setChk('opt-slow-tags', settings.slowTags);
        setChk('opt-team-tags', settings.teamTags);
        setChk('opt-auto-reload', settings.autoReload);
        setChk('opt-full-auto', settings.fullAuto);
        setChk('opt-disable-iff', settings.disableIff);
        setChk('opt-disable-hitreg', settings.disableHitRegister);
    }

    const plannedCount = Number(snapshot?.plannedPlayerCount);
    const playerCountInput = document.getElementById('player-count');
    if (playerCountInput && Number.isInteger(plannedCount) && plannedCount >= 2 && plannedCount <= 24) {
        const declaredMax = Number(selectedType?.maxPlayers);
        const authoritativeMax = Number.isInteger(declaredMax) && declaredMax >= plannedCount
            ? declaredMax
            : Math.max(plannedCount, currentTeamCount * 8, 2);
        playerCountInput.max = String(Math.min(24, authoritativeMax));
        playerCountInput.value = String(plannedCount);
    }
}

function applyState(snapshot) {
    if (!snapshot) return;
    lastStateUpdateMs = Date.now();
    const previousState = (currentSnapshot?.hostingState || '').toLowerCase();
    currentSnapshot = snapshot;

    const state = (snapshot.hostingState || 'idle').toLowerCase();
    const players = snapshot.players || [];
    if (state === 'gameover' && lastHistoryState !== 'gameover' && operatorAuthenticated) {
        void loadHistory();
        scheduleHistoryRefreshRetry();
    } else if (state !== 'gameover' && historyRefreshRetryTimer) {
        window.clearTimeout(historyRefreshRetryTimer);
        historyRefreshRetryTimer = null;
    }
    lastHistoryState = state;
    if (state === 'idle' || state === '') {
        forgetActiveSetup();
        if (previousState && previousState !== 'idle') {
            buildPlayerSlots(savedPlayers.rows);
        }
    } else {
        hydrateActiveSessionFields(snapshot);
        // An already-authenticated fresh page built its rows before the first server
        // snapshot, when game metadata was still the default two-team mode. Rebuild once
        // with the authoritative solo/three-team metadata, then the normal action update
        // below immediately locks the controls again.
        if (!previousState && operatorAuthenticated) {
            buildPlayerSlots(savedPlayers.rows);
        }
        const serverPlannedCount = Number(snapshot.plannedPlayerCount);
        const configuredCount = Number.isInteger(serverPlannedCount) && serverPlannedCount >= 2
            ? clamp(serverPlannedCount, 2, 24)
            : clamp(savedPlannedCount ?? intVal('player-count'), 2, 24);
        const serverTeamCount = Number(snapshot.teamCount);
        const configuredTeamCount = Number.isInteger(serverTeamCount) && serverTeamCount > 0
            ? serverTeamCount
            : null;
        if (!activeSetup || activeSetup.plannedCount !== configuredCount || activeSetup.teamCount !== configuredTeamCount) {
            activeSetup = { plannedCount: configuredCount, teamCount: configuredTeamCount };
        }
    }
    const stateKey = [
        state,
        snapshot.state || '',
        snapshot.guidedJoinHint || '',
        snapshot.gameTypeId || 0,
        snapshot.gameTypeLabel || '',
        snapshot.teamCount || 0,
        snapshot.plannedPlayerCount || 0,
        snapshot.gameMinutes || 0,
        snapshot.countdownSeconds || 0,
        snapshot.isTeamGame ? 1 : 0,
        ...players.map(player => [
            player.id, player.name, player.team || 0, player.confirmed ? 1 : 0,
            player.hitsScored || 0, player.hitsTaken || 0,
        ].join(':')),
    ].join('|');

    if (stateKey === _lastStateKey) {
        updateOverview();
        return;
    }
    _lastStateKey = stateKey;

    const pill = document.getElementById('phase-pill');
    pill.textContent = String(snapshot.state || 'IDLE').toUpperCase();
    pill.className = `phase-pill ${phaseCss(state)}`;

    updateActionBar(state);
    document.getElementById('guided-join-hint').textContent = snapshot.guidedJoinHint || '';

    const readyCount = players.filter(player => player.confirmed).length;
    const plannedCount = configuredPlannedCount();
    updateOverview();
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
    lastTelemetryUpdateMs = Date.now();
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

    if (telemetryKey === _lastTelemetryKey) {
        refreshFreshnessUi(true);
        return;
    }
    _lastTelemetryKey = telemetryKey;
    updateActionBar(currentSnapshot?.hostingState || 'idle');
}

function updateOverview() {
    const selected = gameTypes.find(type => String(type.id) === document.getElementById('game-type-select')?.value);
    const active = isSetupLocked();
    const count = active
        ? configuredPlannedCount()
        : clamp(intVal('player-count') || 3, 2, getMaxPlayers());
    const authoritativeLabel = String(currentSnapshot?.gameTypeLabel || '').trim();
    const game = active && authoritativeLabel ? authoritativeLabel : selected?.label || '—';
    const connected = currentSnapshot?.players?.length || 0;
    const ready = currentSnapshot?.players?.filter(player => player.confirmed).length || 0;

    setText('overview-game', game);
    setText('overview-players', `${connected} / ${count}`);
    setText('overview-ready', ready);
    updateActionBar(currentSnapshot?.hostingState || 'idle');
}

function overviewMessage(state, connected, planned) {
    if (state === 'idle') return 'Ready to configure a game.';
    if (state === 'adding' || state === 'acknowledgeplayerassignment') {
        if (connected > planned) {
            const extra = connected - planned;
            return `${extra} extra tagger${extra === 1 ? '' : 's'} connected; end hosting and correct the roster.`;
        }
        return connected === planned
            ? 'All planned players are connected.'
            : `Waiting for ${planned - connected} more player${planned - connected === 1 ? '' : 's'}.`;
    }
    if (state === 'countdown' || state === 'resendcountdown') return 'Countdown in progress.';
    if (state === 'playing') return 'Game in progress.';
    if (state === 'summary' || state === 'debriefing' || state === 'waitingfordebriefing') {
        return 'Debriefing taggers; final scores are not ready yet.';
    }
    if (state === 'gameover') {
        return currentSnapshot?.resultsFinalized !== false
            ? 'Final scores are ready.'
            : 'Game complete with incomplete results; scores and winners are unavailable.';
    }
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

function getMaxPlayers() {
    const selectedId = parseInt(document.getElementById('game-type-select')?.value, 10);
    const metadata = gameTypes.find(type => Number(type.id) === selectedId);
    const declared = Number(metadata?.maxPlayers);
    if (Number.isFinite(declared) && declared >= 2) return declared;
    return isTeamGame ? currentTeamCount * 8 : 24;
}

function syncPlayerCountLimit(declaredMax) {
    const input = document.getElementById('player-count');
    if (!input) return;

    const parsed = Number(declaredMax);
    const max = Number.isFinite(parsed) && parsed >= 2
        ? parsed
        : (isTeamGame ? currentTeamCount * 8 : 24);
    input.min = '2';
    input.max = String(max);
    const count = clamp(parseInt(input.value, 10) || 3, 2, max);
    input.value = String(count);

    const existingRows = document.querySelectorAll('.player-row').length;
    if (existingRows > 0 && existingRows !== count) buildPlayerSlots();
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
