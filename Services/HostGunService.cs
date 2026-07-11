using LazerTagHostLibrary;
using LazerTagWebServer.Hubs;
using LazerTagWebServer.Models;
using Microsoft.AspNetCore.SignalR;

namespace LazerTagWebServer.Services;

/// <summary>
/// Manages the LTAR host gun and relay connection.
/// Automatically connects to the relay on startup and maintains the connection.
/// Handles game lifecycle from lobby through scoring.
/// </summary>
public sealed class HostGunService : IAsyncDisposable
{
    // ── Constants ────────────────────────────────────────────────────────────────

    private const string RelayAddress          = "192.168.4.100:8266";
    private const int    ReconnectIntervalMs   = 5000;
    private const int    UpdateIntervalMs      = 100;
    private const int    ClientBroadcastIntervalMs = 1000;
    private const int    StartDelayMs          = 500;  // Wait after disconnect before new HostGun connects

    // ── Dependencies ──────────────────────────────────────────────────────────────

    private readonly IHubContext<GameHub>       _hub;
    private readonly ILogger<HostGunService>    _log;

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

    // Planned player settings — indexed by join order
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

    // ── Timers ────────────────────────────────────────────────────────────────────

    private readonly Timer _reconnectTimer;
    private readonly Timer _updateTimer;

    // ── Constructor ───────────────────────────────────────────────────────────────

    public HostGunService(IHubContext<GameHub> hub, ILogger<HostGunService> log)
    {
        _hub = hub;
        _log = log;

        Log.MinimumSeverity = Log.Severity.Information;
        Log.EntryAdded     += OnLibraryLog;

        _updateTimer    = new Timer(OnUpdateTick,    null, Timeout.Infinite, Timeout.Infinite);
        _reconnectTimer = new Timer(OnReconnectTick, null, 2000, ReconnectIntervalMs);
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
            _log.LogInformation("Auto-connect: attempting {Address}", RelayAddress);

            var serial = new LazerTagSerial();
            MutateTelemetry(t => t with { RelayConnectAttempts = t.RelayConnectAttempts + 1 });

            bool connected;
            try
            {
                connected = await Task.Run(() => serial.Connect(RelayAddress));
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
                await PushLogAsync($"Relay connected at {RelayAddress}.");
            }
            else
            {
                serial.Dispose();
                bool wasConnected = Telemetry.RelayConnected;
                MutateTelemetry(t => t with { RelayConnected = false });
                _log.LogInformation("Auto-connect: relay not reachable");
                if (wasConnected)
                    await PushLogAsync("Relay disconnected. Retrying…");
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

            var hg = new HostGun(RelayAddress);
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
            // Publish the host before StartServer because the library may raise synchronous
            // lifecycle events during startup. A failed start is rolled back below.
            lock (_lock) { _hostGun = hg; }

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

            await PushLogAsync($"=== Game {_sessionGameCount} — {DateTime.Now:HH:mm} — {req.GameTypeLabel} ===");
            await PushLogAsync($"Hosting on {RelayAddress}. Waiting for players to join.");
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

    /// <summary>Advance past game-over state.</summary>
    public Task NextAsync() => CleanupHostAsync();

    public async Task<bool> StartCountdownAsync()
    {
        await _lifecycleGate.WaitAsync();
        try
        {
            var hg = _hostGun;
            if (hg == null) return false;

            string[] plannedNames;
            lock (_lock) { plannedNames = _plannedNames.ToArray(); }

            int joined = hg.Players.Count;
            if (plannedNames.Length > 0 && joined < plannedNames.Length)
            {
                string waiting = joined < plannedNames.Length ? plannedNames[joined] : "next player";
                await PushLogAsync($"Still waiting for {waiting} to connect.");
                return false;
            }

            bool ok = hg.StartCountdown();
            if (!ok) await PushLogAsync("Countdown not started — need at least two players.");
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

            await PushLogAsync($"Game ended early (state: {hg.HostingState}).");
            hg.EndGame();
            await Task.Delay(StartDelayMs);
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
            _hostGun = null;
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

    public GameStateSnapshot GetGameState()
    {
        var hg = _hostGun;
        if (hg == null)
            return new GameStateSnapshot { State = "Idle", GuidedJoinHint = GetJoinHint() };

        bool isTeam = hg.GameDefinition?.IsTeamGame ?? false;

        return new GameStateSnapshot
        {
            State                = hg.GetGameStateText(),
            HostingState         = hg.HostingState.ToString(),
            IsTeamGame           = isTeam,
            GuidedJoinHint       = GetJoinHint(),
            CountdownSeconds     = hg.GameDefinition?.CountdownTimeSeconds ?? 30,
            GameMinutes          = hg.GameDefinition?.GameTimeMinutes ?? 10,
            ServerTimeMs         = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            CountdownStartedAtMs = _countdownStartedAtMs,
            GameStartedAtMs      = _gameStartedAtMs,
            Players = hg.Players
                .OrderBy(p => p.TeamPlayerId.PlayerNumber)
                .Select(p => new PlayerSnapshot
                {
                    Id           = p.TeamPlayerId.ToString(isTeam),
                    Team         = p.TeamPlayerId.TeamNumber,
                    Slot         = p.TeamPlayerId.TeamPlayerNumber,
                    Name         = p.Name ?? "",
                    Confirmed    = p.Confirmed,
                    HitsScored   = p.TaggedPlayerCounts.Sum(),   // total tags landed
                    HitsTaken    = p.TagsTaken,                  // total tags received
                    Score        = p.Score,
                    TaggerId     = p.TaggerId,
                    AllDebriefed = p.AllTagReportsReceived(),
                }).ToList(),
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
            return "All players connected — tap Countdown when ready.";
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
            _ = PushLogAsync($"Guided Join: {displayName} → {newest.TeamPlayerId.ToString(hg.GameDefinition?.IsTeamGame ?? false)}.");
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
        await _lifecycleGate.WaitAsync();
        try
        {
            if (!IsCurrentHost(sender)) return;

            await PushLogAsync($"State: {e.PreviousState} → {e.State}");

            // Record timestamps so the TV can sync its timers on late connect
            if (e.State == HostGun.HostingStates.Countdown)
                _countdownStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            else if (e.State == HostGun.HostingStates.Playing)
                _gameStartedAtMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

            if (e.State == HostGun.HostingStates.GameOver)
            {
                MutateTelemetry(t => t with { GamesCompleted = t.GamesCompleted + 1 });
            }
            else if (e.State == HostGun.HostingStates.Idle
                  && e.PreviousState != HostGun.HostingStates.GameOver
                  && e.PreviousState != HostGun.HostingStates.Idle)
            {
                await PushLogAsync($"Warning: relay dropout during {e.PreviousState} — waiting for recovery.");
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
            // GamesCompleted is tracked via HostingStateChanged — do NOT also increment here
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

    private Task PushLogAsync(string msg)  => GameHub.HasClients ? _hub.Clients.All.SendAsync("Log",       msg) : Task.CompletedTask;
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
    }
}
