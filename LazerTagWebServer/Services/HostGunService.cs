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
        public bool ArchiveCaptured { get; set; }
        public FileStream? UpdateLease { get; set; }
        public GameHistorySettings Settings { get; init; } = new();
    }

    // ── Constants ────────────────────────────────────────────────────────────────

    private const string DefaultRelayAddress   = "192.168.4.100:8266";
    private const int    ReconnectIntervalMs   = 5000;
    private const int    UpdateIntervalMs      = 100;
    private const int    ClientBroadcastIntervalMs = 1000;
    private const int    IdleBroadcastIntervalMs = 3500;
    private const int    StartDelayMs          = 500;  // Wait after disconnect before new HostGun connects
    private const int    MaxPendingHistoryWrites = 64;
    private static readonly TimeSpan HistoryShutdownWait = TimeSpan.FromSeconds(3);

    // ── Dependencies ──────────────────────────────────────────────────────────────

    private readonly IHubContext<GameHub>       _hub;
    private readonly GameHubConnections         _connections;
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
    private sealed class PendingHistoryWrite
    {
        public Task Write { get; set; } = Task.CompletedTask;
        public bool Retrying { get; set; }
        public FileStream? UpdateLease { get; set; }
    }

    private readonly Dictionary<string, PendingHistoryWrite> _pendingHistoryWrites = [];
    private readonly CancellationTokenSource _historyWriteCancellation = new();
    private readonly TaskCompletionSource _historyDraining = new(TaskCreationOptions.RunContinuationsAsynchronously);

    // The host binds each tagger to an index in this ordered setup roster.
    private List<string> _plannedNames = [];

    // Timestamps used by TV to keep timers in sync after reconnect
    private long _countdownStartedAtMs;
    private long _gameStartedAtMs;

    // ── Public state ──────────────────────────────────────────────────────────────

    private readonly string _serverInstance = Guid.NewGuid().ToString("N");
    private long _snapshotRevision;
    private TelemetrySnapshot _telemetry = new();
    public TelemetrySnapshot Telemetry
    {
        get
        {
            lock (_lock)
                return _telemetry with
                {
                    ServerInstance = _serverInstance,
                    Revision = ++_snapshotRevision,
                    PendingHistoryWrites = _pendingHistoryWrites.Count,
                    RetryingHistoryWrites = _pendingHistoryWrites.Values.Count(write => write.Retrying),
                };
        }
    }
    public bool IsHosting
    {
        get { lock (_lock) { return _hostGun != null; } }
    }
    public bool IsRelayConnected => Telemetry.RelayConnected;
    public string RelayAddress => _relayAddress;
    private readonly string? _updateLockFile;
    public bool UsesApplianceUpdateLock => OperatingSystem.IsLinux() && _updateLockFile == UpdateAdmission.ApplianceLock;

    // ── Timers ────────────────────────────────────────────────────────────────────

    private readonly Timer _reconnectTimer;
    private readonly Timer _updateTimer;

    // ── Constructor ───────────────────────────────────────────────────────────────

    public HostGunService(
        IHubContext<GameHub> hub,
        ILogger<HostGunService> log,
        GameHistoryStore history,
        IConfiguration configuration,
        GameHubConnections connections)
    {
        _hub = hub;
        _connections = connections;
        _updateLockFile = OperatingSystem.IsLinux() && AppContext.BaseDirectory.StartsWith("/opt/lazertag/", StringComparison.Ordinal)
            ? UpdateAdmission.ApplianceLock : configuration["Arena:UpdateLockFile"];
        _log = log;
        _history = history;
        _relayAddress = ValidateRelayAddress(configuration["Arena:RelayAddress"] ?? DefaultRelayAddress);

        Log.MinimumSeverity = Log.Severity.Information;
        Log.EntryAdded     += OnLibraryLog;

        _updateTimer = new Timer(OnUpdateTick, null, IdleBroadcastIntervalMs, IdleBroadcastIntervalMs);
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
    /// Reconnects the current game's transport, or opens a standby connection while idle.
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
            if (_hostGun is { } activeHost)
            {
                var protocol = activeHost.Protocol;
                if (protocol.IsConnected && IsRelayConnected) return true;

                // A handshake can take several seconds. Let End Game and state transitions
                // proceed while the transport retries, then check that it still owns the session.
                _lifecycleGate.Release();
                entered = false;
                await protocol.EnsureConnectedAsync();
                bool activeConnected;
                lock (_lock)
                {
                    if (!ReferenceEquals(activeHost, _hostGun)) return false;
                    activeConnected = protocol.IsConnected;
                    _telemetry = _telemetry with { RelayConnected = activeConnected };
                }
                await PushTelemetryAsync();
                return activeConnected;
            }

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

            var serial = new LazerTagSerial(RecordDiagnostic);
            serial.IoError += PreConnectSerial_IoError;

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

            connected = connected && serial.IsConnected;
            if (connected)
            {
                _preConnectSerial = serial;

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
            if (entered) MutateTelemetry(t => t with { RelayConnected = false });
            return false;
        }
        finally
        {
            if (entered) _lifecycleGate.Release();
        }
    }

    // ── Game lifecycle ────────────────────────────────────────────────────────────

    public async Task<(bool Success, string Error)> StartHostingAsync(StartGameRequest req)
    {
        await _lifecycleGate.WaitAsync();
        HostGun? startingHost = null;
        FileStream? updateLease = null;
        try
        {
            lock (_lock)
            {
                if (_hostGun != null)
                    return (false, "A game is already running.");
                // Reserve room for this match before admitting it, so no captured result is evicted.
                if (_pendingHistoryWrites.Count >= MaxPendingHistoryWrites)
                    return (false, $"{MaxPendingHistoryWrites} completed games are waiting to save. Restore history storage to start another game.");
            }

            if (_updateLockFile != null && (updateLease = UpdateAdmission.TryAcquire(_updateLockFile)) == null)
                return (false, "Arena maintenance is in progress or the update lock is unavailable. Try again when maintenance finishes.");

            var players = req.Players.OrderBy(p => p.Slot).ToList();

            // Every setting uses the same ordered roster as the operator's join hint.
            var plannedNames      = players.Select(p => string.IsNullOrWhiteSpace(p.Name) ? $"Player {p.Slot}" : p.Name.Trim()).ToList();
            var plannedTeams      = players.Select(p => p.TeamPreference).ToList();
            var plannedHp         = players.Select(p => p.Hp).ToList();
            var plannedShields    = players.Select(p => p.Shields).ToList();
            // 0 reloads means unlimited, matching the global setting, which maps 0 to 0xff.
            var plannedReloads    = players.Select(p => p.Reloads == 0 ? 0xff : p.Reloads).ToList();
            var plannedAutoReload = players.Select(p => p.AutoReload).ToList();
            var plannedFullAuto   = players.Select(p => p.FullAuto).ToList();
            var plannedAmmo       = players.Select(p => p.Ammo).ToList();
            var plannedSleep      = players.Select(p => p.SleepTimeout).ToList();

            // Release the standby connection before HostGun opens its own connection.
            DisconnectPreConnect();
            MutateTelemetry(t => t with { RelayConnected = false });
            await Task.Delay(StartDelayMs);

            int reloads = req.GlobalReloads ?? 0;
            bool forceStandardHosting = GameModeCatalog.GetOrNull(req.GameType)?.RequiresStandardHosting == true;
            bool needsEnhanced = !forceStandardHosting && (
                                  req.DisableIff || req.DisableHitRegister
                               || req.AutoReload || req.FullAuto
                               || req.GlobalStartingAmmo > 0 || req.GlobalSleepTimeout > 0
                               || req.GlobalHp > BinaryCodedDecimal.MaxDecimalValue || req.GlobalShields > BinaryCodedDecimal.MaxDecimalValue
                               || req.CountdownSeconds > BinaryCodedDecimal.MaxDecimalValue
                               || players.Any(p => p.AutoReload == true || p.FullAuto == true
                                                || p.Ammo.HasValue || p.SleepTimeout.HasValue
                                                || p.Hp > BinaryCodedDecimal.MaxDecimalValue || p.Shields > BinaryCodedDecimal.MaxDecimalValue));

            var def = new GameDefinition
            {
                GameType             = req.GameType,
                GameId               = new RandomId(),
                GameTimeMinutes      = req.Minutes,
                PlannedPlayerCount   = players.Count,
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

            var hg = startingHost = await Task.Run(() => new HostGun(_relayAddress, RecordDiagnostic));
            if (!hg.Protocol.IsConnected)
            {
                MutateTelemetry(t => t with { RelayConnected = false });
                await PushTelemetryAsync();
                return (false, $"Unable to connect to relay at {_relayAddress}.");
            }

            hg.PlayerListChanged   += HostGun_PlayerListChanged;
            hg.HostingStateChanged += HostGun_HostingStateChanged;
            hg.Protocol.IoError    += Protocol_IoError;
            hg.SetPlannedPlayerNames(plannedNames);
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

            // Startup callbacks wait on the lifecycle gate. Publish the opened lobby and its
            // roster together before releasing them to the callbacks and operator consoles.
            hg.StartServer(def);
            lock (_lock)
            {
                _hostGun = hg;
                activeGameHistory.UpdateLease = updateLease;
                updateLease = null;
                _activeGameHistory = activeGameHistory;
                _plannedNames = plannedNames;
                _telemetry = _telemetry with { RelayConnected = hg.Protocol.IsConnected };
            }
            startingHost = null;

            PlayerSettings.Save(players.Count, players.Select(p => new PlayerSettings.PlayerRow
            {
                Name           = p.Name ?? "",
                TeamPreference = p.TeamPreferenceLabel ?? "Auto",
                Hp             = p.Hp?.ToString() ?? "",
                Shields        = p.Shields?.ToString() ?? "",
                Reloads        = p.Reloads?.ToString() ?? "",
                AutoReload     = p.AutoReload ?? false,
                FullAuto       = p.FullAuto ?? false,
                Ammo           = p.Ammo?.ToString() ?? "",
                SleepTimeout   = p.SleepTimeout?.ToString() ?? "",
            }));

            _sessionGameCount++;
            Volatile.Write(ref _nextPeriodicBroadcastAtMs, Environment.TickCount64 + ClientBroadcastIntervalMs);
            _updateTimer.Change(UpdateIntervalMs, UpdateIntervalMs);

            await PushLogAsync($"=== Game {_sessionGameCount} - {DateTime.Now:HH:mm} - {req.GameTypeLabel} ===");
            await PushLogAsync($"Hosting on {_relayAddress}. Waiting for players to join.");
            await PushStateAndTelemetryAsync();
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
            updateLease?.Dispose();
            try { startingHost?.Protocol.Dispose(); }
            finally { _lifecycleGate.Release(); }
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

            var (joinedPlayers, nextSlot) = hg.WithStateLock(() => (hg.Players.ToList(), hg.NextPlannedSlotIndex));
            int joined = joinedPlayers.Count;
            if (plannedNames.Length > 0 && joined != plannedNames.Length)
            {
                if (joined < plannedNames.Length)
                {
                    string waiting = plannedNames[nextSlot];
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

            bool ok = hg.WithStateLock(() => hg.StartCountdown());
            if (!ok) await PushLogAsync("Countdown not started - need at least two players.");
            return ok;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    // Callers hold the lifecycle gate from the identity check through the state change.
    private bool IsCurrentGame(Guid gameId) => _activeGameHistory?.GameId == gameId.ToString("N");

    public async Task<bool> EndGameAsync(Guid? gameId = null)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var hg = _hostGun;
            if (hg == null || (gameId.HasValue && !IsCurrentGame(gameId.Value))) return false;

            var previousState = hg.WithStateLock(() =>
            {
                var state = hg.HostingState;
                if (state is HostGun.HostingStates.Playing or HostGun.HostingStates.ResendCountdown
                    or HostGun.HostingStates.Countdown)
                {
                    lock (_lock)
                        if (_activeGameHistory != null) _activeGameHistory.EndedEarly = true;
                }
                hg.EndGame();
                return state;
            });
            if (previousState is HostGun.HostingStates.Summary or HostGun.HostingStates.GameOver)
                return true;

            await PushLogAsync($"End Game requested during {previousState}.");
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

    /// <summary>
    /// Frees a lobby seat, for example after a tagger was power-cycled and came back with a
    /// new tagger ID. Only allowed before the countdown, so no tagger is removed mid-game.
    /// </summary>
    public async Task<bool> RemoveLobbyPlayerAsync(Guid gameId, byte taggerId)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var hg = _hostGun;
            if (hg == null || !IsCurrentGame(gameId)) return false;

            string? removedName = hg.WithStateLock(() =>
            {
                if (hg.HostingState is not (HostGun.HostingStates.Adding or HostGun.HostingStates.AcknowledgePlayerAssignment))
                    return null;
                var player = hg.Players.FirstOrDefault(p => (byte)p.TaggerId == taggerId);
                if (player == null) return null;
                string name = player.NameAndNumber;
                hg.DropPlayer(player.TeamPlayerId);
                return hg.Players.Contains(player) ? null : name;
            });
            if (removedName == null) return false;

            await PushLogAsync($"Removed {removedName} (tagger 0x{taggerId:X2}) from the lobby. Its seat is open again.");
            await PushStateAsync();
            return true;
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task<bool> FinishDebriefAsync(Guid gameId)
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsCurrentGame(gameId) || _hostGun?.FinishDebrief() != true) return false;
            await PushLogAsync("Operator confirmed Finish with missing reports.");
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
        // Let a tick already inside Update finish, so the GameOver check below sees its result.
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
            _activeGameHistory?.UpdateLease?.Dispose();
            _activeGameHistory = null;
            _plannedNames = [];
            _countdownStartedAtMs = 0;
            _gameStartedAtMs = 0;
        }

        // A tick that read the host before it was cleared above may still be running.
        // Ticks that start now see no host, so after this wait nothing uses the old one.
        await WaitForUpdateTickAsync();

        if (hg != null)
        {
            hg.PlayerListChanged   -= HostGun_PlayerListChanged;
            hg.HostingStateChanged -= HostGun_HostingStateChanged;
            hg.Protocol.IoError    -= Protocol_IoError;
            try { hg.Protocol.Dispose(); } catch { /* best-effort */ }
        }

        MutateTelemetry(t => t with { RelayConnected = false });

        if (Volatile.Read(ref _disposed) == 0) _updateTimer.Change(IdleBroadcastIntervalMs, IdleBroadcastIntervalMs);
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
            if (session == null || session.ArchiveCaptured || !ReferenceEquals(hg, _hostGun)) return;
            session.ArchiveCaptured = true;
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
                if (ReferenceEquals(session, _activeGameHistory)) session.ArchiveCaptured = false;
            }
            _log.LogError(ex, "Unable to capture completed game {GameId}; cleanup will retry", session.GameId);
        }
    }

    private static CompletedGameRecord CreateCompletedGameRecord(HostGun hg, ActiveGameHistory session)
    {
        return hg.WithStateLock(() =>
        {
            var definition = hg.GameDefinition;
            bool isTeamGame = definition.IsTeamGame;
            var players = hg.Players.ToList();
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
                Forced = hg.DebriefForced,
                AllPlayersDebriefed = hg.AllPlayersDebriefed,
                PartialResults = hg.PartialScores,
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
                            // team.Score is the game type's ranking key (1024 per survivor in
                            // team games), not points; publish the players' points instead.
                            Score = players.Where(player => player.TeamPlayerId.TeamNumber == team.Number).Sum(player => player.Score),
                            Rank = team.Rank,
                            PlayerCount = players.Count(player => player.TeamPlayerId.TeamNumber == team.Number),
                            Survivors = players.Count(player => player.TeamPlayerId.TeamNumber == team.Number && player.Survived),
                        })
                        .ToList()
                    : [],
            };
        });
    }

    private void TrackHistoryWrite(CompletedGameRecord record)
    {
        lock (_lock)
        {
            var pending = new PendingHistoryWrite();
            if (!_pendingHistoryWrites.TryAdd(record.GameId, pending)) return;
            if (_activeGameHistory?.GameId == record.GameId)
            {
                pending.UpdateLease = _activeGameHistory.UpdateLease;
                _activeGameHistory.UpdateLease = null;
            }
            pending.Write = PersistHistoryWithRetryAsync(record, pending, _historyWriteCancellation.Token);
        }
    }

    private async Task PersistHistoryWithRetryAsync(
        CompletedGameRecord record,
        PendingHistoryWrite pending,
        CancellationToken cancellationToken)
    {
        await Task.Yield();
        int delayMs = 250;
        bool saved = false;
        try
        {
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    saved = await _history.SaveAsync(record, cancellationToken);
                    if (saved) return;
                }
                catch (Exception) when (!cancellationToken.IsCancellationRequested)
                {
                    // The store logs its I/O failures. A log provider can also fail during
                    // a storage outage. Keep the record eligible for its next attempt.
                }
                lock (_lock) { pending.Retrying = true; }
                if (_historyDraining.Task.IsCompleted)
                    await Task.Delay(250, cancellationToken);
                else
                    await Task.WhenAny(Task.Delay(delayMs, cancellationToken), _historyDraining.Task);
                delayMs = Math.Min(delayMs * 2, 30000);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Shutdown reports the outstanding writes together.
        }
        finally
        {
            lock (_lock)
            {
                pending.UpdateLease?.Dispose();
                _pendingHistoryWrites.Remove(record.GameId);
                if (saved) _telemetry = _telemetry with { HistoryWritesCompleted = _telemetry.HistoryWritesCompleted + 1 };
            }
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
        while (true)
        {
            HostGun? host;
            lock (_lock)
            {
                host = _hostGun;
                if (host == null)
                    return new GameStateSnapshot { ServerInstance = _serverInstance, Revision = ++_snapshotRevision };
            }

            var snapshot = host.WithStateLock(() => CaptureGameState(host));
            if (snapshot != null) return snapshot;
        }
    }

    private GameStateSnapshot? CaptureGameState(HostGun hg)
    {
        string[] plannedNames;
        string gameTypeLabel;
        GameHistorySettings? activeSettings;
        string? gameId;
        long revision;
        lock (_lock)
        {
            if (!ReferenceEquals(hg, _hostGun)) return null;
            revision = ++_snapshotRevision;
            plannedNames = _plannedNames.ToArray();
            gameTypeLabel = _activeGameHistory?.GameTypeLabel ?? "";
            activeSettings = _activeGameHistory?.Settings;
            gameId = _activeGameHistory?.GameId;
        }

        var definition = hg.GameDefinition;
        var hostingState = hg.HostingState;
        bool isTeam = definition?.IsTeamGame ?? false;
        var mode = definition == null ? null : GameModeCatalog.GetOrNull(definition.GameType);
        var players = hg.Players
            .OrderBy(player => player.TeamPlayerId.PlayerNumber)
            .ToList();

        bool resultsFinalized = gameId != null
            && hostingState == HostGun.HostingStates.GameOver
            && hg.AllPlayersDebriefed;
        bool partialResults = gameId != null
            && hostingState == HostGun.HostingStates.GameOver
            && hg.PartialScores;

        int nextSlot = hg.NextPlannedSlotIndex;
        string guidedJoinHint;
        if (nextSlot < plannedNames.Length)
            guidedJoinHint = $"Waiting for {plannedNames[nextSlot]} to connect.";
        else if (plannedNames.Length > 0)
            guidedJoinHint = "All players connected - tap Countdown when ready.";
        else
            guidedJoinHint = "Players may join.";

        return new GameStateSnapshot
        {
            ServerInstance = _serverInstance,
            Revision = revision,
            GameId               = gameId,
            State                = hg.GetGameStateText(),
            HostingState         = hostingState.ToString(),
            GameTypeId           = definition == null ? 0 : (int)definition.GameType,
            GameTypeCode         = mode?.Code ?? "",
            GameTypeLabel        = string.IsNullOrWhiteSpace(gameTypeLabel)
                ? mode?.Label ?? definition?.GameType.ToString() ?? ""
                : gameTypeLabel,
            GameObjective        = mode?.Objective ?? "",
            GameOperatorNote     = mode?.OperatorNote ?? "",
            RoleCycleSeconds     = mode?.RoleCycleSeconds ?? 0,
            ReverseHuntDirection = definition?.GameTypeInfo.ReverseHuntDirection ?? false,
            RequiresStandardHosting = mode?.RequiresStandardHosting ?? false,
            IsTeamGame           = isTeam,
            TeamCount            = isTeam ? definition?.TeamCount ?? 0 : 0,
            PlannedPlayerCount   = plannedNames.Length,
            ResultsFinalized     = resultsFinalized,
            PartialResults       = partialResults,
            Settings             = activeSettings,
            GuidedJoinHint       = guidedJoinHint,
            PhaseRemainingMs     = hostingState is HostGun.HostingStates.Playing or HostGun.HostingStates.Countdown or HostGun.HostingStates.ResendCountdown
                ? hg.PhaseRemainingMilliseconds : null,
            CountdownSeconds     = hostingState == HostGun.HostingStates.ResendCountdown
                ? definition?.ResendCountdownTimeSeconds ?? 10 : definition?.CountdownTimeSeconds ?? 30,
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
                    Dropped      = p.Dropped,
                    // Detailed LTAR reports may count repeated IR frames. Publish only
                    // effective accepted hits so the TV agrees with HP and final score.
                    HitsScored   = hg.CountEffectiveHitsScored(p),
                    HitsTaken    = p.TagsTaken,
                    Score        = p.Score,
                    Rank         = p.Rank,
                    TaggerId     = p.TaggerId,
                    AllDebriefed = !p.Dropped && p.AllTagReportsReceived(),
                }).ToList(),
            Teams = isTeam
                ? hg.Teams
                    .OrderBy(team => team.Number)
                    .Select(team => new TeamSnapshot
                    {
                        Number = team.Number,
                        // team.Score is the ranking key, not points (see CreateCompletedGameRecord).
                        Score = players.Where(p => p.TeamPlayerId.TeamNumber == team.Number).Sum(p => p.Score),
                        Rank = team.Rank,
                        Survivors = players.Count(p => p.TeamPlayerId.TeamNumber == team.Number && p.Survived),
                    }).ToList()
                : [],
        };
    }

    public (int Count, List<PlayerSettings.PlayerRow> Rows) LoadSavedPlayers()
        => PlayerSettings.TryLoad(out int count, out var rows) ? (count, rows) : (3, []);

    // ── Guided join hint ──────────────────────────────────────────────────────────

    // ── Event handlers ────────────────────────────────────────────────────────────

    private async void OnUpdateTick(object? _)
    {
        // Mark the tick busy before reading the host. Cleanup clears _hostGun first and then
        // waits while this flag is set, so a tick never updates a host after it is disposed.
        if (Interlocked.CompareExchange(ref _isUpdating, 1, 0) != 0) return;
        try
        {
            HostGun? hg;
            lock (_lock) hg = _hostGun;
            hg?.Update();
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
                if (e.PreviousState != HostGun.HostingStates.Playing)
                    MutateTelemetry(t => t with { GamesStarted = t.GamesStarted + 1 });
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

            var protocol = (LazerTagProtocol)sender!;
            MutateTelemetry(t => t with { IoErrors = t.IoErrors + 1, RelayConnected = protocol.IsConnected });
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

    private void RecordDiagnostic(HostDiagnostic diagnostic)
    {
        MutateTelemetry(t => diagnostic switch
        {
            HostDiagnostic.RelayConnectAttempt => t with { RelayConnectAttempts = t.RelayConnectAttempts + 1 },
            HostDiagnostic.RelayConnected => t with { RelayConnectedCount = t.RelayConnectedCount + 1 },
            HostDiagnostic.RelayConnectionRefused => t with { RelayConnectFailures = t.RelayConnectFailures + 1, RelayConnectionRefusals = t.RelayConnectionRefusals + 1 },
            HostDiagnostic.RelayConnectTimeout => t with { RelayConnectFailures = t.RelayConnectFailures + 1, RelayTimeouts = t.RelayTimeouts + 1 },
            HostDiagnostic.RelayHandshakeFailed => t with { RelayConnectFailures = t.RelayConnectFailures + 1, RelayHandshakeFailures = t.RelayHandshakeFailures + 1 },
            HostDiagnostic.RelayHandshakeTimeout => t with
            {
                RelayConnectFailures = t.RelayConnectFailures + 1,
                RelayHandshakeFailures = t.RelayHandshakeFailures + 1,
                RelayTimeouts = t.RelayTimeouts + 1,
            },
            HostDiagnostic.RelayConnectFailed => t with { RelayConnectFailures = t.RelayConnectFailures + 1 },
            HostDiagnostic.RelayWriteRejected => t with { RelayWriteRejections = t.RelayWriteRejections + 1 },
            HostDiagnostic.UnexpectedPacket => t with { UnexpectedPackets = t.UnexpectedPackets + 1 },
            HostDiagnostic.StateTimeout => t with { StateTimeouts = t.StateTimeouts + 1 },
            _ => t,
        });
    }

    private void OnLibraryLog(object? sender, Log.EntryAddedEventArgs e)
        => _ = PushLogAsync($"[{e.Entry.Time:HH:mm:ss}] {e.Entry.Severity}: {e.Entry.Message}");

    // ── SignalR push helpers ───────────────────────────────────────────────────────

    private bool TryBeginPeriodicBroadcast()
    {
        if (!_connections.HasClients) return false;

        var now = Environment.TickCount64;
        if (now < Volatile.Read(ref _nextPeriodicBroadcastAtMs)) return false;
        if (Interlocked.CompareExchange(ref _isPeriodicBroadcasting, 1, 0) != 0) return false;

        Volatile.Write(ref _nextPeriodicBroadcastAtMs, now + ClientBroadcastIntervalMs);
        return true;
    }

    private Task PushLogAsync(string msg)  => _connections.SendLogAsync(_hub, msg);
    private Task PushStateAsync()          => _connections.HasClients ? _hub.Clients.All.SendAsync("State", GetGameState()) : Task.CompletedTask;
    private Task PushTelemetryAsync()      => _connections.HasClients ? _hub.Clients.All.SendAsync("Telemetry", Telemetry) : Task.CompletedTask;
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

    private async Task WaitForUpdateTickAsync()
    {
        // A timer callback may already be inside HostGun.Update when cleanup begins. Give it a
        // brief chance to finish before disposing the protocol it is using.
        for (int attempt = 0; attempt < 50 && Volatile.Read(ref _isUpdating) != 0; attempt++)
            await Task.Delay(10);
    }

    private async Task WaitForPendingHistoryWritesAsync()
    {
        _historyDraining.TrySetResult();
        Task[] pending;
        lock (_lock) { pending = _pendingHistoryWrites.Values.Select(entry => entry.Write).ToArray(); }

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

        _historyWriteCancellation.Cancel();
        _log.LogWarning(
            "Shutdown waited {WaitSeconds} seconds for {WriteCount} history write(s); cancelling remaining retries",
            HistoryShutdownWait.TotalSeconds, pending.Length);
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
