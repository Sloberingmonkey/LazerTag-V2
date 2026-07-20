using LazerTagHostLibrary;
using LazerTagWebServer.Hubs;
using LazerTagWebServer.Models;
using Microsoft.AspNetCore.SignalR;
using System.Reflection;

namespace LazerTagWebServer.Services;

/// <summary>
/// Manages the LTAR host gun and relay connection.
/// Automatically connects to the relay on startup and maintains the connection.
/// Handles game lifecycle from lobby through scoring.
/// </summary>
public sealed class HostGunService : IAsyncDisposable
{
    private sealed class ActiveGameHistory
    {
        public string GameId { get; init; } = Guid.NewGuid().ToString("N");
        public string GameTypeLabel { get; init; } = "";
        public DateTimeOffset HostedAtUtc { get; init; }
        public DateTimeOffset? StartedAtUtc { get; set; }
        public DateTimeOffset? EndedAtUtc { get; set; }
        public bool EndedEarly { get; set; }
        public bool Forced { get; set; }
        public bool ArchiveQueued { get; set; }
        public GameHistorySettings Settings { get; init; } = new();
    }

    // ── Constants ────────────────────────────────────────────────────────────────

    private const string DefaultRelayAddress   = "192.168.4.100:8266";
    private const int    ReconnectIntervalMs   = 5000;
    private const int    UpdateIntervalMs      = 100;
    private const int    ClientBroadcastIntervalMs = 1000;
    private const int    StartDelayMs          = 500;  // Wait after disconnect before new HostGun connects
    private const int    HistorySaveAttempts   = 3;
    private static readonly TimeSpan HistoryShutdownWait = TimeSpan.FromSeconds(3);

    // ── Dependencies ──────────────────────────────────────────────────────────────

    private readonly IHubContext<GameHub>       _hub;
    private readonly ILogger<HostGunService>    _log;
    private readonly GameHistoryStore           _history;
    private readonly string                     _relayAddress;

    // ── State ─────────────────────────────────────────────────────────────────────

    private readonly object   _lock             = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private HostGun?          _hostGun;
    private LazerTagSerial?   _preConnectSerial;
    private int               _isUpdating;        // 0 = idle, 1 = in progress (Interlocked)
    private int               _isPeriodicBroadcasting;
    private long              _nextPeriodicBroadcastAtMs;
    private int               _sessionGameCount;
    private int               _disposed;
    private ActiveGameHistory? _activeGameHistory;
    private readonly HashSet<Task> _pendingHistoryWrites = [];
    private readonly CancellationTokenSource _historyWriteCancellation = new();

    // Planned player settings - indexed by join order
    private List<string>  _plannedNames      = [];
    private List<int?>    _plannedTeams      = [];
    private List<int?>    _plannedHp         = [];
    private List<int?>    _plannedShields    = [];
    private List<int?>    _plannedReloads    = [];
    private List<bool?>   _plannedAutoReload = [];
    private List<bool?>   _plannedFullAuto   = [];
    private List<int?>    _plannedAmmo       = [];
    private List<int?>    _plannedSleep      = [];
    private int           _lastNamedPlayerCount;

    // Timestamps used by TV to keep timers in sync after reconnect
    private long _countdownStartedAtMs;
    private long _gameStartedAtMs;

    // ── Public state ──────────────────────────────────────────────────────────────

    private TelemetrySnapshot _telemetry = new();
    public TelemetrySnapshot Telemetry
    {
        get { lock (_lock) { return _telemetry; } }
    }
    public bool IsHosting
    {
        get { lock (_lock) { return _hostGun != null; } }
    }
    public bool IsRelayConnected => Telemetry.RelayConnected;
    public string RelayAddress => _relayAddress;

    // ── Timers ────────────────────────────────────────────────────────────────────

    private readonly Timer _reconnectTimer;
    private readonly Timer _updateTimer;

    // ── Constructor ───────────────────────────────────────────────────────────────

    public HostGunService(
        IHubContext<GameHub> hub,
        ILogger<HostGunService> log,
        GameHistoryStore history,
        IConfiguration configuration)
    {
        _hub = hub;
        _log = log;
        _history = history;
        _relayAddress = ValidateRelayAddress(configuration["Arena:RelayAddress"] ?? DefaultRelayAddress);

        Log.MinimumSeverity = Log.Severity.Information;
        Log.EntryAdded     += OnLibraryLog;

        _updateTimer = new Timer(OnUpdateTick, null, Timeout.Infinite, Timeout.Infinite);
        bool disableReconnect = configuration.GetValue<bool>("Arena:DisableRelayReconnect");
        _reconnectTimer = new Timer(
            OnReconnectTick,
            null,
            disableReconnect ? Timeout.Infinite : 2000,
            disableReconnect ? Timeout.Infinite : ReconnectIntervalMs);
    }

    // ── Thread-safe telemetry mutation ────────────────────────────────────────────

    /// <summary>
    /// All telemetry mutations go through this method to prevent read-tearing
    /// when concurrent threads (timer callbacks, library events) update counters.
    /// </summary>
    private void MutateTelemetry(Func<TelemetrySnapshot, TelemetrySnapshot> mutator)
    {
        lock (_lock)
        {
            _telemetry = mutator(_telemetry);
        }
    }

    // ── Relay auto-connect ────────────────────────────────────────────────────────

    private void OnReconnectTick(object? _) => _ = EnsureRelayConnectedCoreAsync(waitForTurn: false);

    /// <summary>
    /// Ensures the standby relay connection exists. Lifecycle operations share one gate so an
    /// automatic reconnect cannot race a game start or cleanup operation.
    /// </summary>
    public Task<bool> EnsureRelayConnectedAsync() => EnsureRelayConnectedCoreAsync(waitForTurn: true);

    private async Task<bool> EnsureRelayConnectedCoreAsync(bool waitForTurn)
    {
        if (Volatile.Read(ref _disposed) != 0) return false;

        bool entered;
        if (waitForTurn)
        {
            await _lifecycleGate.WaitAsync();
            entered = true;
        }
        else
        {
            entered = _lifecycleGate.Wait(0);
        }

        if (!entered) return IsRelayConnected;

        try
        {
            if (Volatile.Read(ref _disposed) != 0) return false;
            if (_hostGun != null) return Telemetry.RelayConnected;

            if (_preConnectSerial is { IsConnected: true })
            {
                if (!Telemetry.RelayConnected)
                {
                    MutateTelemetry(t => t with { RelayConnected = true });
                    await PushTelemetryAsync();
                }
                return true;
            }

            DisconnectPreConnect();
            _log.LogInformation("Auto-connect: attempting {Address}", _relayAddress);

            var serial = new LazerTagSerial();
            MutateTelemetry(t => t with { RelayConnectAttempts = t.RelayConnectAttempts + 1 });

            bool connected;
            try
            {
                connected = await Task.Run(() => serial.Connect(_relayAddress));
            }
            catch (Exception ex)
            {
                _log.LogWarning("Auto-connect exception: {Message}", ex.Message);
                connected = false;
            }

            if (connected)
            {
                serial.IoError += PreConnectSerial_IoError;
                _preConnectSerial = serial;

                // The library's "TCP relay connected" log entry owns the connection counter.
                // Only set the current status here to avoid counting one connection twice.
                MutateTelemetry(t => t with { RelayConnected = true });
                _log.LogInformation("Auto-connect: relay connected");
                await PushLogAsync($"Relay connected at {_relayAddress}.");
            }
            else
            {
                serial.Dispose();
                bool wasConnected = Telemetry.RelayConnected;
                MutateTelemetry(t => t with { RelayConnected = false });
                _log.LogInformation("Auto-connect: relay not reachable");
                if (wasConnected)
                    await PushLogAsync("Relay disconnected. Retrying...");
            }

            await PushTelemetryAsync();
            return connected;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Relay reconnect error");
            MutateTelemetry(t => t with { RelayConnected = false });
            return false;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // ── Game lifecycle ────────────────────────────────────────────────────────────

    public async Task<(bool Success, string Error)> StartHostingAsync(StartGameRequest req)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            lock (_lock)
            {
                if (_hostGun != null)
                    return (false, "A game is already running.");
            }

            var players = req.Players.OrderBy(p => p.Slot).ToList();

            // Planned settings are indexed by join order, so keep them in stable slot order.
            var plannedNames      = players.Select(p => string.IsNullOrWhiteSpace(p.Name) ? $"Player {p.Slot}" : p.Name.Trim()).ToList();
            var plannedTeams      = players.Select(p => p.TeamPreference).ToList();
            var plannedHp         = players.Select(p => p.Hp).ToList();
            var plannedShields    = players.Select(p => p.Shields).ToList();
            var plannedReloads    = players.Select(p => p.Reloads).ToList();
            var plannedAutoReload = players.Select(p => p.AutoReload).ToList();
            var plannedFullAuto   = players.Select(p => p.FullAuto).ToList();
            var plannedAmmo       = players.Select(p => p.Ammo).ToList();
            var plannedSleep      = players.Select(p => p.SleepTimeout).ToList();

            lock (_lock)
            {
                _plannedNames      = plannedNames;
                _plannedTeams      = plannedTeams;
                _plannedHp         = plannedHp;
                _plannedShields    = plannedShields;
                _plannedReloads    = plannedReloads;
                _plannedAutoReload = plannedAutoReload;
                _plannedFullAuto   = plannedFullAuto;
                _plannedAmmo       = plannedAmmo;
                _plannedSleep      = plannedSleep;
                _lastNamedPlayerCount = 0;
            }
            _countdownStartedAtMs = 0;
            _gameStartedAtMs = 0;

            // Persist player names/settings so they reload next session.
            PlayerSettings.Save(players.Count, players.Select(p => new PlayerSettings.PlayerRow
            {
                Name           = p.Name ?? "",
                TeamPreference = p.TeamPreferenceLabel ?? "Auto",
                Hp             = p.Hp?.ToString()      ?? "",
                Shields        = p.Shields?.ToString() ?? "",
                Reloads        = p.Reloads?.ToString() ?? "",
                AutoReload     = p.AutoReload ?? false,
                FullAuto       = p.FullAuto   ?? false,
                Ammo           = p.Ammo?.ToString()         ?? "",
                SleepTimeout   = p.SleepTimeout?.ToString() ?? "",
            }));

            // Release the standby connection before HostGun opens its own connection.
            DisconnectPreConnect();
            MutateTelemetry(t => t with { RelayConnected = false });
            await Task.Delay(StartDelayMs);

            int reloads = req.GlobalReloads ?? 0;
            bool needsEnhanced = req.DisableIff || req.DisableHitRegister
                              || req.AutoReload  || req.FullAuto
                              || players.Any(p => p.AutoReload == true || p.FullAuto == true);

            var def = new GameDefinition
            {
                GameType             = req.GameType,
                GameId               = new RandomId(),
                GameTimeMinutes      = req.Minutes,
                Tags                 = req.GlobalHp,
                Shields              = req.GlobalShields,
                Reloads              = reloads == 0 ? 0xff : reloads,
                Mega                 = req.GlobalMega,
                TeamTags             = req.TeamTags,
                MedicMode            = req.MedicMode,
                SlowTags             = req.SlowTags,
                AutomaticReload      = req.AutoReload,
                AllowFullAuto        = req.FullAuto,
                CountdownTimeSeconds = req.CountdownSeconds,
                StartingAmmo         = req.GlobalStartingAmmo,
                SleepTimeoutMinutes  = req.GlobalSleepTimeout,
                ResendCountdownTimeSeconds = 10,
                UseLtarEnhancedProtocol          = needsEnhanced,
                DisableLtarIffProximityBeacons   = req.DisableIff,
                DisableLtarTagReceivedBeacons    = req.DisableHitRegister,
                UseStandardLtarBeacons           = false,
            };

            var hg = new HostGun(_relayAddress);
            hg.PlayerListChanged   += HostGun_PlayerListChanged;
            hg.HostingStateChanged += HostGun_HostingStateChanged;
            hg.Protocol.IoError    += Protocol_IoError;
            hg.SetPlannedTeamPreferences(plannedTeams);
            hg.SetPlannedTagsOverrides(plannedHp);
            hg.SetPlannedShieldsOverrides(plannedShields);
            hg.SetPlannedReloadsOverrides(plannedReloads);
            hg.SetPlannedAutoReloadOverrides(plannedAutoReload);
            hg.SetPlannedFullAutoOverrides(plannedFullAuto);
            hg.SetPlannedStartingAmmoOverrides(plannedAmmo);
            hg.SetPlannedSleepTimeoutOverrides(plannedSleep);

            var activeGameHistory = new ActiveGameHistory
            {
                GameTypeLabel = string.IsNullOrWhiteSpace(req.GameTypeLabel)
                    ? req.GameType.ToString()
                    : req.GameTypeLabel,
                HostedAtUtc = DateTimeOffset.UtcNow,
                Settings = new GameHistorySettings
                {
                    Minutes = req.Minutes,
                    CountdownSeconds = req.CountdownSeconds,
                    GlobalHp = req.GlobalHp,
                    GlobalShields = req.GlobalShields,
                    GlobalReloads = req.GlobalReloads ?? 0,
                    GlobalMega = req.GlobalMega,
                    GlobalStartingAmmo = req.GlobalStartingAmmo,
                    GlobalSleepTimeout = req.GlobalSleepTimeout,
                    SlowTags = req.SlowTags,
                    TeamTags = req.TeamTags,
                    MedicMode = req.MedicMode,
                    AutoReload = req.AutoReload,
                    FullAuto = req.FullAuto,
                    DisableIff = req.DisableIff,
                    DisableHitRegister = req.DisableHitRegister,
                    EnhancedProtocol = needsEnhanced,
                },
            };

            // Publish the host before StartServer because the library may raise synchronous
            // lifecycle events during startup. A failed start is rolled back below.
            lock (_lock)
            {
                _hostGun = hg;
                _activeGameHistory = activeGameHistory;
            }

            try
            {
                hg.StartServer(def);
            }
            catch
            {
                await CleanupHostCoreAsync(pushUpdates: false);
                throw;
            }

            _sessionGameCount++;
            MutateTelemetry(t => t with { GamesStarted = t.GamesStarted + 1 });
            Volatile.Write(ref _nextPeriodicBroadcastAtMs, Environment.TickCount64 + ClientBroadcastIntervalMs);
            _updateTimer.Change(UpdateIntervalMs, UpdateIntervalMs);

            await PushLogAsync($"=== Game {_sessionGameCount} - {DateTime.Now:HH:mm} - {req.GameTypeLabel} ===");
            await PushLogAsync($"Hosting on {_relayAddress}. Waiting for players to join.");
            await PushStateAsync();
            return (true, "");
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "StartHostingAsync failed");
            await CleanupHostCoreAsync(pushUpdates: true);
            return (false, ex.Message);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    /// <summary>Clear a completed/aborted host and prepare for another game.</summary>
    public async Task<bool> NextAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var hg = _hostGun;
            if (hg != null && hg.HostingState is not HostGun.HostingStates.GameOver and not HostGun.HostingStates.Idle)
                return false;

            await CleanupHostCoreAsync(pushUpdates: true);
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> StartCountdownAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var hg = _hostGun;
            if (hg == null) return false;

            if (!IsRelayConnected)
            {
                await PushLogAsync("Countdown blocked: the ESP32 relay is disconnected.");
                return false;
            }

            string[] plannedNames;
            lock (_lock) { plannedNames = _plannedNames.ToArray(); }

            var joinedPlayers = hg.Players.ToList();
            int joined = joinedPlayers.Count;
            if (plannedNames.Length > 0 && joined != plannedNames.Length)
            {
                if (joined < plannedNames.Length)
                {
                    string waiting = plannedNames[joined];
                    await PushLogAsync($"Still waiting for {waiting} to connect.");
                }
                else
                {
                    await PushLogAsync("Countdown blocked: more taggers joined than were planned. End hosting and restart the roster.");
                }
                return false;
            }

            var unconfirmed = joinedPlayers.Where(player => !player.Confirmed).ToList();
            if (unconfirmed.Count > 0)
            {
                await PushLogAsync($"Countdown blocked: {unconfirmed.Count} tagger(s) have not confirmed their assignment.");
                return false;
            }

            var definition = hg.GameDefinition;
            if (definition.IsTeamGame)
            {
                var assignedTeams = joinedPlayers
                    .Select(player => player.TeamPlayerId.TeamNumber)
                    .ToList();
                if (assignedTeams.Any(team => team < 1 || team > definition.TeamCount))
                {
                    await PushLogAsync("Countdown blocked: a tagger has an invalid team assignment.");
                    return false;
                }

                var representedTeams = assignedTeams.ToHashSet();
                var missingTeams = Enumerable.Range(1, definition.TeamCount)
                    .Where(team => !representedTeams.Contains(team))
                    .ToList();
                if (missingTeams.Count > 0)
                {
                    await PushLogAsync(
                        $"Countdown blocked: every team needs a joined player (missing Team {string.Join(" and Team ", missingTeams)}).");
                    return false;
                }
            }

            bool ok = hg.StartCountdown();
            if (!ok) await PushLogAsync("Countdown not started - need at least two players.");
            return ok;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> EndGameAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var hg = _hostGun;
            if (hg == null) return false;

            lock (_lock)
            {
                if (_activeGameHistory != null)
                {
                    if (hg.HostingState is HostGun.HostingStates.Playing
                        or HostGun.HostingStates.ResendCountdown
                        or HostGun.HostingStates.Countdown)
                    {
                        _activeGameHistory.EndedEarly = true;
                    }
                    else if (hg.HostingState == HostGun.HostingStates.Summary)
                    {
                        // A second End Game during debriefing tells the library to stop waiting
                        // for missing reports and transition immediately to GameOver.
                        _activeGameHistory.Forced = true;
                    }
                }
            }

            await PushLogAsync($"Game ended early (state: {hg.HostingState}).");
            hg.EndGame();
            await Task.Delay(StartDelayMs);

            // Ending from the lobby/countdown transitions the library directly to Idle. Dispose
            // that host now so the next Start Hosting action cannot dead-end on a stale instance.
            if (hg.HostingState == HostGun.HostingStates.Idle)
                await CleanupHostCoreAsync(pushUpdates: false);

            await PushStateAsync();
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task CleanupHostAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            await CleanupHostCoreAsync(pushUpdates: true);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task CleanupHostCoreAsync(bool pushUpdates)
    {
        _updateTimer.Change(Timeout.Infinite, Timeout.Infinite);
        await WaitForUpdateTickAsync();

        HostGun? hg;
        lock (_lock)
        {
            hg = _hostGun;
        }

        if (hg?.HostingState == HostGun.HostingStates.GameOver)
            QueueCompletedGameArchive(hg);

        lock (_lock)
        {
            if (ReferenceEquals(_hostGun, hg)) _hostGun = null;
            _activeGameHistory = null;
            _lastNamedPlayerCount = 0;
        }

        if (hg != null)
        {
            hg.PlayerListChanged   -= HostGun_PlayerListChanged;
            hg.HostingStateChanged -= HostGun_HostingStateChanged;
            hg.Protocol.IoError    -= Protocol_IoError;
            try { hg.Protocol.Dispose(); } catch { /* best-effort */ }
        }

        ClearPlannedPlayers();
        _countdownStartedAtMs = 0;
        _gameStartedAtMs = 0;
        MutateTelemetry(t => t with { RelayConnected = false });

        if (pushUpdates)
            await PushStateAndTelemetryAsync();
    }

    // ── State snapshot ────────────────────────────────────────────────────────────

    private void QueueCompletedGameArchive(HostGun hg)
    {
        ActiveGameHistory? session;
        lock (_lock)
        {
            session = _activeGameHistory;
            if (session == null || session.ArchiveQueued || !ReferenceEquals(hg, _hostGun)) return;
            session.ArchiveQueued = true;
            session.EndedAtUtc ??= DateTimeOffset.UtcNow;
        }

        try
        {
            var record = CreateCompletedGameRecord(hg, session);
            TrackHistoryWrite(record);
            _log.LogInformation("Queued completed game {GameId} for history persistence", session.GameId);
        }
        catch (Exception ex)
        {
            lock (_lock)
            {
                if (ReferenceEquals(session, _activeGameHistory)) session.ArchiveQueued = false;
            }
            _log.LogError(ex, "Unable to capture completed game {GameId}; cleanup will retry", session.GameId);
        }
    }

    private static CompletedGameRecord CreateCompletedGameRecord(HostGun hg, ActiveGameHistory session)
    {
        var definition = hg.GameDefinition;
        bool isTeamGame = definition.IsTeamGame;
        var players = hg.Players.ToList();
        bool allPlayersDebriefed = players.Count > 0
            && players.All(player => !player.Dropped && player.AllTagReportsReceived());

        return new CompletedGameRecord
        {
            GameId = session.GameId,
            ProtocolGameId = definition.GameId?.ToString() ?? "",
            SoftwareVersion = GetSoftwareVersion(),
            GameTypeId = (int)definition.GameType,
            GameTypeName = definition.GameType.ToString(),
            GameTypeLabel = session.GameTypeLabel,
            IsTeamGame = isTeamGame,
            HostedAtUtc = session.HostedAtUtc,
            StartedAtUtc = session.StartedAtUtc ?? session.HostedAtUtc,
            EndedAtUtc = session.EndedAtUtc!.Value,
            EndedEarly = session.EndedEarly,
            Forced = session.Forced,
            AllPlayersDebriefed = allPlayersDebriefed,
            Settings = session.Settings,
            Players = players
                .OrderBy(player => player.TeamPlayerId.PlayerNumber)
                .Select(player => new GameHistoryPlayer
                {
                    Id = player.TeamPlayerId.ToString(isTeamGame),
                    Team = isTeamGame ? player.TeamPlayerId.TeamNumber : 0,
                    Slot = isTeamGame
                        ? player.TeamPlayerId.TeamPlayerNumber
                        : player.TeamPlayerId.PlayerNumber,
                    Name = player.Name ?? "",
                    TaggerId = (byte)player.TaggerId,
                    Score = player.Score,
                    Rank = player.Rank,
                    HitsScored = hg.CountEffectiveHitsScored(player),
                    HitsTaken = player.TagsTaken,
                    Confirmed = player.Confirmed,
                    Dropped = player.Dropped,
                    DebriefComplete = !player.Dropped && player.AllTagReportsReceived(),
                    Hp = player.TagsOverride ?? definition.Tags,
                    Shields = player.ShieldsOverride ?? definition.Shields,
                    Reloads = player.ReloadsOverride ?? definition.Reloads,
                    AutoReload = player.AutoReloadOverride ?? definition.AutomaticReload,
                    FullAuto = player.FullAutoOverride ?? definition.AllowFullAuto,
                    StartingAmmo = player.StartingAmmoOverride ?? definition.StartingAmmo,
                    SleepTimeoutMinutes = player.SleepTimeoutOverride ?? definition.SleepTimeoutMinutes,
                })
                .ToList(),
            Teams = isTeamGame
                ? hg.Teams
                    .OrderBy(team => team.Number)
                    .Select(team => new GameHistoryTeam
                    {
                        Team = team.Number,
                        Score = team.Score,
                        Rank = team.Rank,
                        PlayerCount = players.Count(player => player.TeamPlayerId.TeamNumber == team.Number),
                    })
                    .ToList()
                : [],
        };
    }

    private void TrackHistoryWrite(CompletedGameRecord record)
    {
        // The initial yield guarantees no file operation runs inline while the lifecycle gate
        // is held. The immutable record no longer references HostGun or the active session.
        var writeTask = PersistHistoryWithRetryAsync(record, _historyWriteCancellation.Token);
        lock (_lock) { _pendingHistoryWrites.Add(writeTask); }
        _ = ObserveHistoryWriteAsync(writeTask, record.GameId);
    }

    private async Task PersistHistoryWithRetryAsync(
        CompletedGameRecord record,
        CancellationToken cancellationToken)
    {
        await Task.Yield();

        for (int attempt = 1; attempt <= HistorySaveAttempts; attempt++)
        {
            if (cancellationToken.IsCancellationRequested) return;

            bool saved;
            try
            {
                saved = await _history.SaveAsync(record, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "History save attempt {Attempt} failed for game {GameId}",
                    attempt, record.GameId);
                saved = false;
            }

            if (saved)
            {
                _log.LogInformation("Archived completed game {GameId}", record.GameId);
                return;
            }

            if (attempt == HistorySaveAttempts) break;

            var retryDelay = TimeSpan.FromMilliseconds(250 * (1 << (attempt - 1)));
            _log.LogWarning(
                "Retrying history save for game {GameId} in {RetryDelayMs} ms (attempt {NextAttempt} of {Attempts})",
                record.GameId, retryDelay.TotalMilliseconds, attempt + 1, HistorySaveAttempts);
            try
            {
                await Task.Delay(retryDelay, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }

        _log.LogError("Unable to archive completed game {GameId} after {Attempts} attempts; gameplay continued",
            record.GameId, HistorySaveAttempts);
    }

    private async Task ObserveHistoryWriteAsync(Task writeTask, string gameId)
    {
        try
        {
            await writeTask;
        }
        catch (Exception ex)
        {
            // PersistHistoryWithRetryAsync contains its own operational error handling. Keep
            // this final guard so an unexpected task fault is always observed and logged.
            _log.LogError(ex, "Unexpected history writer failure for game {GameId}", gameId);
        }
        finally
        {
            lock (_lock) { _pendingHistoryWrites.Remove(writeTask); }
        }
    }

    private static string GetSoftwareVersion()
    {
        var assembly = typeof(HostGunService).Assembly;
        return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? assembly.GetName().Version?.ToString(3)
            ?? "unknown";
    }

    public GameStateSnapshot GetGameState()
    {
        var hg = _hostGun;
        if (hg == null)
            return new GameStateSnapshot { State = "Idle", GuidedJoinHint = GetJoinHint() };

        var definition = hg.GameDefinition;
        var hostingState = hg.HostingState;
        bool isTeam = definition?.IsTeamGame ?? false;
        int plannedPlayerCount;
        string gameTypeLabel;
        GameHistorySettings? activeSettings;
        bool hasActiveHistory;
        bool forcedCompletion;
        lock (_lock)
        {
            plannedPlayerCount = _plannedNames.Count;
            gameTypeLabel = _activeGameHistory?.GameTypeLabel ?? definition?.GameType.ToString() ?? "";
            activeSettings = _activeGameHistory?.Settings;
            hasActiveHistory = _activeGameHistory != null;
            forcedCompletion = _activeGameHistory?.Forced == true;
        }

        var players = hg.Players
            .OrderBy(player => player.TeamPlayerId.PlayerNumber)
            .ToList();
        bool resultsFinalized = hasActiveHistory
            && hostingState == HostGun.HostingStates.GameOver
            && !forcedCompletion
            && players.Count > 0
            && players.All(player => !player.Dropped && player.AllTagReportsReceived());

        return new GameStateSnapshot
        {
            State                = hg.GetGameStateText(),
            HostingState         = hostingState.ToString(),
            GameTypeId           = definition == null ? 0 : (int)definition.GameType,
            GameTypeLabel        = gameTypeLabel,
            IsTeamGame           = isTeam,
            TeamCount            = isTeam ? definition?.TeamCount ?? 0 : 0,
            PlannedPlayerCount   = plannedPlayerCount,
            ResultsFinalized     = resultsFinalized,
            Settings             = activeSettings,
            GuidedJoinHint       = GetJoinHint(),
            CountdownSeconds     = definition?.CountdownTimeSeconds ?? 30,
            GameMinutes          = definition?.GameTimeMinutes ?? 10,
            ServerTimeMs         = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CountdownStartedAtMs = _countdownStartedAtMs,
            GameStartedAtMs      = _gameStartedAtMs,
            Players = players
                .Select(p => new PlayerSnapshot
                {
                    Id           = p.TeamPlayerId.ToString(isTeam),
                    Team         = isTeam ? p.TeamPlayerId.TeamNumber : 0,
                    Slot         = isTeam
                        ? p.TeamPlayerId.TeamPlayerNumber
                        : p.TeamPlayerId.PlayerNumber,
                    Name         = p.Name ?? "",
                    Confirmed    = p.Confirmed,
                    // Detailed LTAR reports may count repeated IR frames. Publish only
                    // effective accepted hits so the TV agrees with HP and final score.
                    HitsScored   = hg.CountEffectiveHitsScored(p),
                    HitsTaken    = p.TagsTaken,
                    Score        = p.Score,
                    Rank         = p.Rank,
                    TaggerId     = p.TaggerId,
                    AllDebriefed = p.AllTagReportsReceived(),
                }).ToList(),
            Teams = isTeam
                ? hg.Teams
                    .OrderBy(team => team.Number)
                    .Select(team => new TeamSnapshot
                    {
                        Number = team.Number,
                        Score = team.Score,
                        Rank = team.Rank,
                    }).ToList()
                : [],
        };
    }

    public (int Count, List<PlayerSettings.PlayerRow> Rows) LoadSavedPlayers()
        => PlayerSettings.TryLoad(out int count, out var rows) ? (count, rows) : (3, []);

    // ── Guided join hint ──────────────────────────────────────────────────────────

    private string GetJoinHint()
    {
        HostGun? hg;
        string[] plannedNames;
        lock (_lock)
        {
            hg = _hostGun;
            plannedNames = _plannedNames.ToArray();
        }

        if (hg == null)
            return plannedNames.Length == 0 ? "" : $"Turn on {plannedNames[0]}'s gun first.";

        int joined = hg.Players.Count;
        if (joined < plannedNames.Length)
            return $"Waiting for {plannedNames[joined]} to connect.";
        if (plannedNames.Length > 0)
            return "All players connected - tap Countdown when ready.";
        return "Players may join.";
    }

    private void ApplyGuidedNames()
    {
        HostGun? hg;
        string[] plannedNames;
        int?[] plannedHp;
        int?[] plannedShields;
        int?[] plannedReloads;
        bool?[] plannedAutoReload;
        bool?[] plannedFullAuto;
        int?[] plannedAmmo;
        int?[] plannedSleep;

        lock (_lock)
        {
            hg = _hostGun;
            plannedNames      = _plannedNames.ToArray();
            plannedHp         = _plannedHp.ToArray();
            plannedShields    = _plannedShields.ToArray();
            plannedReloads    = _plannedReloads.ToArray();
            plannedAutoReload = _plannedAutoReload.ToArray();
            plannedFullAuto   = _plannedFullAuto.ToArray();
            plannedAmmo       = _plannedAmmo.ToArray();
            plannedSleep      = _plannedSleep.ToArray();
        }

        if (hg == null || plannedNames.Length == 0) return;

        var joined = hg.Players.ToList();
        for (int i = 0; i < joined.Count && i < plannedNames.Length; i++)
        {
            var p = joined[i];
            var name = plannedNames[i];

            // Set name if not already set correctly
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name != name)
                hg.SetPlayerName(p.TeamPlayerId, name);

            // Apply per-player overrides only on first assignment (HasValue guards prevent re-applying)
            if (i < plannedHp.Length         && !p.TagsOverride.HasValue         && plannedHp[i].HasValue)
                hg.SetPlayerTagsOverride(p.TeamPlayerId, plannedHp[i]);
            if (i < plannedShields.Length    && !p.ShieldsOverride.HasValue      && plannedShields[i].HasValue)
                hg.SetPlayerShieldsOverride(p.TeamPlayerId, plannedShields[i]);
            if (i < plannedReloads.Length    && !p.ReloadsOverride.HasValue      && plannedReloads[i].HasValue)
                hg.SetPlayerReloadsOverride(p.TeamPlayerId, plannedReloads[i]);
            if (i < plannedAutoReload.Length && !p.AutoReloadOverride.HasValue   && plannedAutoReload[i].HasValue)
                hg.SetPlayerAutoReloadOverride(p.TeamPlayerId, plannedAutoReload[i]);
            if (i < plannedFullAuto.Length   && !p.FullAutoOverride.HasValue     && plannedFullAuto[i].HasValue)
                hg.SetPlayerFullAutoOverride(p.TeamPlayerId, plannedFullAuto[i]);
            if (i < plannedAmmo.Length       && !p.StartingAmmoOverride.HasValue && plannedAmmo[i].HasValue)
                hg.SetPlayerStartingAmmoOverride(p.TeamPlayerId, plannedAmmo[i]);
            if (i < plannedSleep.Length      && !p.SleepTimeoutOverride.HasValue && plannedSleep[i].HasValue)
                hg.SetPlayerSleepTimeoutOverride(p.TeamPlayerId, plannedSleep[i]);
        }

        // Log when a new player joins. Update the counter under the same lock used by cleanup.
        bool shouldLog;
        lock (_lock)
        {
            shouldLog = joined.Count > _lastNamedPlayerCount;
            if (shouldLog) _lastNamedPlayerCount = joined.Count;
        }

        if (shouldLog)
        {
            var newest = joined.Last();
            string displayName = joined.Count <= plannedNames.Length
                ? plannedNames[joined.Count - 1]
                : newest.DisplayName;
            _ = PushLogAsync($"Guided Join: {displayName} -> {newest.TeamPlayerId.ToString(hg.GameDefinition?.IsTeamGame ?? false)}.");
        }
    }

    // ── Event handlers ────────────────────────────────────────────────────────────

    private async void OnUpdateTick(object? _)
    {
        var hg = _hostGun;
        if (hg == null) return;

        if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0) return;

        try
        {
            hg.Update();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Update tick error");
            return;
        }
        finally
        {
            Interlocked.Exchange(ref _isUpdating, 0);
        }

        if (!TryBeginPeriodicBroadcast()) return;

        try
        {
            await PushStateAndTelemetryAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Periodic broadcast error");
        }
        finally
        {
            Interlocked.Exchange(ref _isPeriodicBroadcasting, 0);
        }
    }

    private async void HostGun_PlayerListChanged(object? sender, HostGun.PlayerListChangedEventArgs e)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsCurrentHost(sender)) return;

            ApplyGuidedNames();
            await PushStateAndTelemetryAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PlayerListChanged handler error");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async void HostGun_HostingStateChanged(object? sender, HostGun.HostingStateChangedEventArgs e)
    {
        // Capture the transition time before waiting behind an operator action so history
        // timestamps represent the game event, not a later disk/network scheduling delay.
        var transitionAtUtc = DateTimeOffset.UtcNow;
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsCurrentHost(sender)) return;

            // Record timestamps so the TV can sync its timers on late connect
            if (e.State == HostGun.HostingStates.Countdown)
                _countdownStartedAtMs = transitionAtUtc.ToUnixTimeMilliseconds();
            else if (e.State == HostGun.HostingStates.Playing)
            {
                _gameStartedAtMs = transitionAtUtc.ToUnixTimeMilliseconds();
                lock (_lock)
                {
                    if (_activeGameHistory != null)
                        _activeGameHistory.StartedAtUtc ??= transitionAtUtc;
                }
            }

            if (e.State == HostGun.HostingStates.GameOver)
            {
                MutateTelemetry(t => t with { GamesCompleted = t.GamesCompleted + 1 });
                lock (_lock)
                {
                    if (_activeGameHistory != null)
                        _activeGameHistory.EndedAtUtc ??= transitionAtUtc;
                }
                if (sender is HostGun completedHost)
                    QueueCompletedGameArchive(completedHost);
            }

            await PushLogAsync($"State: {e.PreviousState} -> {e.State}");

            if (e.State == HostGun.HostingStates.Idle
                && e.PreviousState != HostGun.HostingStates.GameOver
                && e.PreviousState != HostGun.HostingStates.Idle)
            {
                await PushLogAsync($"Warning: relay dropout during {e.PreviousState} - waiting for recovery.");
                return;  // Skip PushStateAsync so TV stays on current screen
            }

            await PushStateAndTelemetryAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "HostingStateChanged handler error");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async void Protocol_IoError(object? sender, LazerTagSerial.IoErrorEventArgs e)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsCurrentProtocol(sender)) return;

            MutateTelemetry(t => t with { IoErrors = t.IoErrors + 1, RelayConnected = false });
            await PushTelemetryAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Protocol_IoError handler error");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private bool IsCurrentHost(object? sender)
    {
        lock (_lock) { return ReferenceEquals(sender, _hostGun); }
    }

    private bool IsCurrentProtocol(object? sender)
    {
        lock (_lock) { return _hostGun != null && ReferenceEquals(sender, _hostGun.Protocol); }
    }

    private void PreConnectSerial_IoError(object? sender, LazerTagSerial.IoErrorEventArgs e)
        => _ = HandlePreConnectSerialIoErrorAsync(sender);

    private async Task HandlePreConnectSerialIoErrorAsync(object? sender)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _disposed) != 0 || !ReferenceEquals(sender, _preConnectSerial))
                return;

            MutateTelemetry(t => t with { IoErrors = t.IoErrors + 1, RelayConnected = false });
            DisconnectPreConnect();
            await PushTelemetryAsync();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "PreConnectSerial_IoError handler error");
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void OnLibraryLog(object? sender, Log.EntryAddedEventArgs e)
    {
        // WARNING: These string prefixes must match LazerTagHostLibrary's Log.Add() calls.
        // If the library log messages change, these counters will silently stop working.
        var m = e.Entry.Message;
        MutateTelemetry(t => m switch
        {
            _ when m.StartsWith("TCP relay connected")                 => t with { RelayConnectedCount  = t.RelayConnectedCount  + 1, RelayConnected = true  },
            _ when m.StartsWith("TCP connection timed out")            => t with { RelayTimeouts        = t.RelayTimeouts        + 1, RelayConnected = false },
            _ when m.StartsWith("Explicit state machine: packet kind") => t with { UnexpectedPackets    = t.UnexpectedPackets    + 1 },
            _ when m.StartsWith("Explicit state machine timeout")      => t with { StateTimeouts        = t.StateTimeouts        + 1 },
            _ when m.StartsWith("Connect() failed")                    => t with { RelayConnectFailures = t.RelayConnectFailures + 1 },
            // GamesCompleted is tracked via HostingStateChanged - do NOT also increment here
            _                                                          => t,
        });

        _ = PushLogAsync($"[{e.Entry.Time:HH:mm:ss}] {e.Entry.Severity}: {m}");
    }

    // ── SignalR push helpers ───────────────────────────────────────────────────────

    private bool TryBeginPeriodicBroadcast()
    {
        if (!GameHub.HasClients) return false;

        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextPeriodicBroadcastAtMs)) return false;
        if (Interlocked.CompareExchange(ref _isPeriodicBroadcasting, 1, 0) != 0) return false;

        Volatile.Write(ref _nextPeriodicBroadcastAtMs, now + ClientBroadcastIntervalMs);
        return true;
    }

    private Task PushLogAsync(string msg)  => GameHub.HasClients ? _hub.Clients.Group(GameHub.OperatorsGroup).SendAsync("Log", msg) : Task.CompletedTask;
    private Task PushStateAsync()          => GameHub.HasClients ? _hub.Clients.All.SendAsync("State",     GetGameState()) : Task.CompletedTask;
    private Task PushTelemetryAsync()      => GameHub.HasClients ? _hub.Clients.All.SendAsync("Telemetry", Telemetry) : Task.CompletedTask;
    private Task PushStateAndTelemetryAsync() => Task.WhenAll(PushStateAsync(), PushTelemetryAsync());

    // ── Relay helpers ─────────────────────────────────────────────────────────────

    private void DisconnectPreConnect()
    {
        var s = _preConnectSerial;
        _preConnectSerial = null;
        if (s == null) return;
        s.IoError -= PreConnectSerial_IoError;
        try { s.Disconnect(); } catch { /* best-effort */ }
        try { s.Dispose();    } catch { /* best-effort */ }
    }

    private void ClearPlannedPlayers()
    {
        lock (_lock)
        {
            // Replace the collections rather than mutating them in place so any event handler
            // already using a snapshot can finish safely during cleanup.
            _plannedNames      = [];
            _plannedTeams      = [];
            _plannedHp         = [];
            _plannedShields    = [];
            _plannedReloads    = [];
            _plannedAutoReload = [];
            _plannedFullAuto   = [];
            _plannedAmmo       = [];
            _plannedSleep      = [];
            _lastNamedPlayerCount = 0;
        }
    }

    private async Task WaitForUpdateTickAsync()
    {
        // A timer callback may already be inside HostGun.Update when cleanup begins. Give it a
        // brief chance to finish before disposing the protocol it is using.
        for (int attempt = 0; attempt < 50 && Volatile.Read(ref _isUpdating) != 0; attempt++)
            await Task.Delay(10);
    }

    private async Task WaitForPendingHistoryWritesAsync()
    {
        Task[] pending;
        lock (_lock) { pending = _pendingHistoryWrites.ToArray(); }

        if (pending.Length == 0)
        {
            _historyWriteCancellation.Dispose();
            return;
        }

        var allWrites = Task.WhenAll(pending);
        var completed = await Task.WhenAny(allWrites, Task.Delay(HistoryShutdownWait));
        if (ReferenceEquals(completed, allWrites))
        {
            try
            {
                await allWrites;
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "Unexpected history writer failure during shutdown");
            }
            finally
            {
                _historyWriteCancellation.Dispose();
            }
            return;
        }

        _log.LogWarning(
            "Shutdown waited {WaitSeconds} seconds for {WriteCount} history write(s); cancelling remaining retries",
            HistoryShutdownWait.TotalSeconds, pending.Length);
        _historyWriteCancellation.Cancel();
        // A storage call already inside an uninterruptible filesystem flush may outlive the
        // service shutdown timeout. Leave the token source undisposed for that bounded case.
    }

    private static string ValidateRelayAddress(string value)
    {
        value = value.Trim();
        int separator = value.LastIndexOf(':');
        if (separator <= 0 || separator == value.Length - 1)
            throw new InvalidOperationException("Arena:RelayAddress must use the form host:port.");

        string host = value[..separator];
        string portText = value[(separator + 1)..];
        if (host.Any(char.IsWhiteSpace) || !int.TryParse(portText, out int port) || port is < 1 or > 65535)
            throw new InvalidOperationException("Arena:RelayAddress must contain a valid host and TCP port.");

        return value;
    }

    // ── IAsyncDisposable ──────────────────────────────────────────────────────────

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _reconnectTimer.Dispose();
        _updateTimer.Dispose();
        Log.EntryAdded -= OnLibraryLog;

        await _lifecycleGate.WaitAsync();
        try
        {
            DisconnectPreConnect();
            await CleanupHostCoreAsync(pushUpdates: false);
        }
        finally
        {
            _lifecycleGate.Release();
        }

        await WaitForPendingHistoryWritesAsync();
    }
}
