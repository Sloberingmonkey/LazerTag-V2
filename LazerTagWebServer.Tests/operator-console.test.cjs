const assert = require('node:assert/strict');
const { readFileSync } = require('node:fs');
const path = require('node:path');
const test = require('node:test');
const vm = require('node:vm');

const app = readFileSync(path.join(__dirname, '../LazerTagWebServer/wwwroot/js/app.js'), 'utf8');

function openConsole() {
    const controls = new Map();
    const renderedRows = [];
    function control(id) {
        if (!controls.has(id)) controls.set(id, { value: '', dataset: {}, classList: { toggle() {} }, closest: () => null });
        return controls.get(id);
    }
    control('player-list').appendChild = row => renderedRows.push(row);
    const document = {
        addEventListener() {},
        getElementById: control,
        querySelector: () => null,
        querySelectorAll: selector => selector.split(',').map(name => control(name.replace(/^#/, ''))),
        createElement() {
            const team = {};
            return { dataset: {}, querySelector: () => team };
        },
    };
    class HubConnectionBuilder {
        withUrl() { return this; }
        withAutomaticReconnect() { return this; }
        build() { return { on() {}, onreconnecting() {}, onreconnected() {}, onclose() {} }; }
    }
    const page = vm.createContext({ document, performance, window: { addEventListener() {} }, signalR: { HubConnectionBuilder } });
    vm.runInContext(readFileSync(path.join(__dirname, '../LazerTagWebServer/wwwroot/js/snapshots.js'), 'utf8'), page);
    vm.runInContext(app, page);
    page.updateOverview = () => {};
    vm.runInContext('operatorAuthenticated = true; isTeamGame = true; currentTeamCount = 2;', page);
    return { page, control, renderedRows };
}

test('loading a saved roster renders names, teams, zero values and individual overrides', async () => {
    const { page, control, renderedRows } = openConsole();
    page.fetchJson = async url => {
        assert.equal(url, '/api/game/savedplayers');
        return {
            playerCount: 2,
            rows: [
                { Name: 'Rowan', TeamPreference: 'Team 2', Hp: '12', Shields: '0', Reloads: '3',
                  AutoReload: true, FullAuto: false, Ammo: '18', SleepTimeout: '4' },
                { Name: 'Finley', TeamPreference: 'Auto', Hp: '', FullAuto: true },
            ],
        };
    };
    await page.loadSavedPlayers();
    vm.runInContext('buildPlayerSlots(savedPlayers.rows)', page);
    assert.equal(control('player-count').value, 2);
    assert.equal(renderedRows.length, 2);
    const [first, second] = renderedRows;
    assert.match(first.innerHTML, /class="p-name"[^>]+value="Rowan"/);
    assert.equal(first.querySelector('.p-team').value, '2');
    for (const [field, value] of Object.entries({ hp: 12, shields: 0, reloads: 3, ammo: 18, sleep: 4 })) {
        assert.match(first.innerHTML, new RegExp(`class="p-${field}"[^>]+value="${value}"`));
    }
    assert.match(first.innerHTML, /class="p-ar" checked/);
    assert.doesNotMatch(first.innerHTML, /class="p-fa" checked/);
    assert.match(second.innerHTML, /class="p-fa" checked/);
    assert.equal(second.querySelector('.p-team').value, '');
});

test('the next setup uses the roster from the game that just started', async () => {
    const { page, control, renderedRows } = openConsole();
    const players = [{ slot: 1, name: 'Rowan', teamPreference: 2, hp: 12 }, { slot: 2, name: 'Finley' }];
    control('game-type-select').options = [{ text: 'Battle' }];
    control('game-type-select').selectedIndex = 0;
    control('countdown').value = 30;
    control('player-count').value = 2;
    page.readGrid = () => players;
    page.validateConfiguration = () => true;
    page.runAction = async () => true;
    page.updateActionBar = () => {};
    await page.startHosting();
    vm.runInContext('buildPlayerSlots(savedPlayers.rows)', page);
    assert.match(renderedRows[0].innerHTML, /class="p-name"[^>]+value="Rowan"/);
    assert.equal(renderedRows[0].querySelector('.p-team').value, '2');
});

test('switching into and out of Kings updates global and per-player numeric limits', () => {
    const { page, control } = openConsole();
    for (const [standard, statMax, countdownMax] of [[true, 99, 99], [false, 254, 255]]) {
        page.applyModeCapabilities({ requiresStandardHosting: standard, maxHpAndShields: statMax, maxCountdownSeconds: countdownMax });
        assert.equal(control('countdown').max, String(countdownMax));
        for (const input of ['global-hp', 'global-shields', '.p-hp', '.p-shields'])
            assert.equal(control(input).max, String(statMax));
    }
});

test('End Game and Finish with missing reports are available in distinct phases', () => {
    const { page, control } = openConsole();
    const html = readFileSync(path.join(__dirname, '../LazerTagWebServer/wwwroot/index.html'), 'utf8');
    assert.match(html, /id="btn-end"[^>]*onclick="endGame\(\)"[^>]*>[\s\S]*?<span>End Game<\/span>/);
    assert.match(html, /id="live-card"[\s\S]*?id="btn-finish-debrief"[^>]*onclick="finishDebrief\(\)"[^>]*hidden[^>]*>Finish with missing reports<\/button>/);
    page.getCountdownReadiness = () => ({ ready: false });
    page.renderCountdownReadiness = page.setSetupLocked = () => {};
    vm.runInContext("lastStateUpdateMs = performance.now(); currentSnapshot = { gameId: 'a', players: [{ allDebriefed: false }] };", page);
    const end = control('btn-end'), finish = control('btn-finish-debrief');
    for (const phase of ['Adding', 'Countdown', 'Playing']) {
        page.updateActionBar(phase);
        assert.equal(end.disabled, false);
        assert.equal(finish.hidden, true);
    }
    page.updateActionBar('Summary');
    assert.equal(end.disabled, true);
    assert.equal(finish.hidden, false);
    assert.equal(finish.disabled, false);
    vm.runInContext('lastStateUpdateMs = 0;', page);
    page.updateActionBar('Summary');
    assert.equal(finish.disabled, true);
    vm.runInContext('lastStateUpdateMs = performance.now(); operatorAuthenticated = false;', page);
    page.updateActionBar('Summary');
    assert.equal(finish.disabled, true);
    vm.runInContext('operatorAuthenticated = true; currentSnapshot.players[0].allDebriefed = true;', page);
    page.updateActionBar('Summary');
    assert.equal(finish.hidden, true);
    page.updateActionBar('GameOver');
    assert.equal(end.disabled, true);
    assert.equal(finish.hidden, true);
});

test('ordinary and confirmed finish requests use the match the operator was viewing', async () => {
    const { page } = openConsole();
    const actions = [];
    page.runAction = async (button, url) => actions.push([button, url]);
    vm.runInContext("lastStateUpdateMs = performance.now(); currentSnapshot = { gameId: 'first', hostingState: 'Playing', players: [{ allDebriefed: false }] };", page);
    page.window.confirm = () => {
        vm.runInContext("currentSnapshot = { ...currentSnapshot, gameId: 'second', hostingState: 'Summary' };", page);
        return true;
    };
    await page.finishDebrief();
    await page.endGame();
    await page.endGame();
    assert.deepEqual(actions, [['btn-end', '/api/game/first/endgame']]);
    page.window.confirm = prompt => { assert.match(prompt, /1 tagger.*reports received.*partial/i); return false; };
    await page.finishDebrief();
    assert.equal(actions.length, 1);
    page.window.confirm = () => {
        vm.runInContext("currentSnapshot = { ...currentSnapshot, gameId: 'third' };", page);
        return true;
    };
    await page.finishDebrief();
    assert.deepEqual(actions[1], ['btn-finish-debrief', '/api/game/second/finish-debrief']);
    vm.runInContext('lastStateUpdateMs = 0;', page);
    await page.finishDebrief();
    assert.equal(actions.length, 2);
});

test('recent results shows saving, storage retry, and recovery only to the operator', () => {
    const { page, control } = openConsole();
    const status = control('history-write-status');
    page.renderHistoryWriteStatus({ pendingHistoryWrites: 2 });
    assert.equal(status.hidden, false);
    assert.match(status.textContent, /Saving 2 completed games/);
    page.renderHistoryWriteStatus({ pendingHistoryWrites: 1, retryingHistoryWrites: 1 });
    assert.match(status.textContent, /Waiting to save 1 completed game\./);
    page.renderHistoryWriteStatus({ pendingHistoryWrites: 0 });
    assert.equal(status.hidden, true);
    vm.runInContext('operatorAuthenticated = false;', page);
    page.renderHistoryWriteStatus({ pendingHistoryWrites: 1, retryingHistoryWrites: 1 });
    assert.equal(status.hidden, true);
});

test('a recovered history write refreshes Recent Results even when another game is still pending', () => {
    const { page } = openConsole();
    let reloads = 0;
    page.updateActionBar = page.refreshFreshnessUi = () => {};
    page.loadHistory = () => { reloads++; };
    const telemetry = { pendingHistoryWrites: 1, retryingHistoryWrites: 1, historyWritesCompleted: 0 };
    page.applyTelemetry(telemetry);
    page.applyTelemetry({ ...telemetry, historyWritesCompleted: 1 });
    page.applyTelemetry({ ...telemetry, historyWritesCompleted: 1 });
    assert.equal(reloads, 1);
});

test('relay diagnostics show failed attempts with refusal and handshake counts', () => {
    const { page, control } = openConsole();
    page.updateActionBar = page.refreshFreshnessUi = () => {};
    const totals = { relayConnectedCount: 1, relayConnectAttempts: 4, relayTimeouts: 1, relayConnectFailures: 3 };
    page.applyTelemetry({ ...totals, relayConnectionRefusals: 1, relayHandshakeFailures: 2 });
    assert.match(control('telemetry-bar').textContent, /Relay 1\/4 · Timeouts 1 · Failures 3 \(refused 1, handshake 2\)/);
    page.applyTelemetry(totals);
    assert.match(control('telemetry-bar').textContent, /refused 0, handshake 0/);
});

test('phone and TV discard old HTTP results across a new match and a server restart', () => {
    const { page } = openConsole();
    const order = vm.runInContext('new ArenaSnapshotOrder()', page);
    const snapshot = (serverInstance, revision) => ({ serverInstance, revision });
    const initialRequest = order.generation;
    assert.equal(order.accept('state', snapshot('one', 10)), true);
    assert.equal(order.accept('state', snapshot('one', 8), initialRequest), false);
    assert.equal(order.accept('telemetry', snapshot('one', 9), initialRequest), true);
    assert.equal(order.needsRefresh(), false);
    const beforeRestart = order.generation;
    assert.equal(order.accept('state', snapshot('two', 1)), true);
    assert.equal(order.accept('state', snapshot('one', 100), beforeRestart), false);
    assert.equal(order.accept('telemetry', snapshot('one', 101)), false);
    assert.equal(order.accept('telemetry', snapshot('two', 2)), true);
    assert.equal(order.accept('state', snapshot('two', 1)), false);
    for (const filename of ['index.html', 'tv.html']) {
        const html = readFileSync(path.join(__dirname, '../LazerTagWebServer/wwwroot', filename), 'utf8');
        assert.match(html, /<script src="\/js\/snapshots\.js"><\/script>/);
    }
});

test('HTTP fallback shares overlapping reads and resumes when live updates stop', async () => {
    const context = vm.createContext({ performance: { now: () => now }, AbortController, setTimeout, clearTimeout });
    let now = 10;
    vm.runInContext(readFileSync(path.join(__dirname, '../LazerTagWebServer/wwwroot/js/snapshots.js'), 'utf8'), context);
    const order = vm.runInContext('new ArenaSnapshotOrder()', context);
    let finish, calls = 0;
    const load = () => { calls++; return new Promise(resolve => { finish = resolve; }); };
    const first = order.refresh(load);
    assert.equal(order.refresh(load), first);
    assert.equal(calls, 1);
    finish();
    await first;
    order.accept('state', { serverInstance: 'a', revision: 1 });
    order.accept('telemetry', { serverInstance: 'a', revision: 2 });
    assert.equal(order.needsRefresh(), false);
    now += 5000;
    assert.equal(order.needsRefresh(), true);
    order.reconnect();
    assert.equal(order.accept('state', { hostingState: 'Idle' }), true);
    assert.equal(order.accept('state', { serverInstance: 'a', revision: 99 }), false);
});

test('the TV countdown uses server remaining time and ignores a delayed lobby response', () => {
    const html = readFileSync(path.join(__dirname, '../LazerTagWebServer/wwwroot/tv.html'), 'utf8');
    const { page, control } = openConsole();
    let elapsed = 500;
    const tv = vm.createContext({
        document: page.document, window: page.window, signalR: page.signalR,
        performance: { now: () => elapsed },
    });
    vm.runInContext(readFileSync(path.join(__dirname, '../LazerTagWebServer/wwwroot/js/snapshots.js'), 'utf8'), tv);
    vm.runInContext([...html.matchAll(/<script>([\s\S]*?)<\/script>/g)].at(-1)[1], tv);
    vm.runInContext('showPhase = name => { currentPhase = name; }; startTimer = () => {}; markStateUpdate = () => {};', tv);
    control('header-status').classList.add = () => {};
    tv.applyState({ serverInstance: 'pi', revision: 12, hostingState: 'Countdown', countdownSeconds: 30,
        phaseRemainingMs: 17000, serverTimeMs: 100, countdownStartedAtMs: 90000 });
    assert.equal(control('countdown-number').textContent, '17');
    tv.applyState({ serverInstance: 'pi', revision: 11, hostingState: 'Adding' }, 0);
    elapsed += 4000;
    tv.tickTimer();
    assert.equal(control('countdown-number').textContent, '13');
});
