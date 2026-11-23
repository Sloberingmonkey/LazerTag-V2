using LazerTagHostLibrary;

namespace LazerTagWebServer.Models;

// ── Requests ─────────────────────────────────────────────────────────────────

/// <summary>Request body for POST /api/game/start.</summary>
public class StartGameRequest
{
    public GameType GameType         { get; set; } = GameType.CustomLazerTagTwoTeams;
    public string   GameTypeLabel    { get; set; } = "";
    public int      Minutes          { get; set; } = 10;
    public int      CountdownSeconds { get; set; } = 30;

    // Global stats - applied to all players unless overridden per-player
    public int  GlobalHp            { get; set; } = 10;
    public int  GlobalShields       { get; set; } = 0;
    public int? GlobalReloads       { get; set; } = 0;   // null/0 = unlimited (0xFF)
    public int  GlobalMega          { get; set; } = 0;
    public int  GlobalStartingAmmo  { get; set; } = 10;
    public int  GlobalSleepTimeout  { get; set; } = 0;   // 0 = disabled

    // Game options
    public bool SlowTags            { get; set; } = false;
    public bool TeamTags            { get; set; } = false;
    public bool MedicMode           { get; set; } = false;
    public bool AutoReload          { get; set; } = false;
    public bool FullAuto            { get; set; } = false;
    public bool DisableIff          { get; set; } = true;
    public bool DisableHitRegister  { get; set; } = true;

    public List<PlannedPlayerRequest> Players { get; set; } = [];
}

/// <summary>Per-player settings sent with a StartGameRequest. Null = use global value.</summary>
public class PlannedPlayerRequest
{
    public int     Slot                 { get; set; }
    public string? Name                 { get; set; }
    public string? TeamPreferenceLabel  { get; set; }
    public int?    TeamPreference       { get; set; }
    public int?    Hp                   { get; set; }
    public int?    Shields              { get; set; }
    public int?    Reloads              { get; set; }
    public bool?   AutoReload           { get; set; }
    public bool?   FullAuto             { get; set; }
    public int?    Ammo                 { get; set; }
    public int?    SleepTimeout         { get; set; }
}

// ── Snapshots (sent to clients via SignalR) ───────────────────────────────────

/// <summary>Diagnostic counters broadcast on every tick.</summary>
public record TelemetrySnapshot
{
    public string ServerInstance { get; init; } = "";
    public long Revision { get; init; }
    public bool RelayConnected       { get; init; }
    public int  RelayConnectedCount  { get; init; }
    public int  RelayConnectAttempts { get; init; }
    public int  RelayTimeouts        { get; init; }
    public int  RelayConnectFailures { get; init; }
    public int  RelayConnectionRefusals { get; init; }
    public int  RelayHandshakeFailures  { get; init; }
    public int RelayWriteRejections { get; init; }
    public int  IoErrors             { get; init; }
    public int  UnexpectedPackets    { get; init; }
    public int  StateTimeouts        { get; init; }
    public int  GamesStarted         { get; init; }
    public int  GamesCompleted       { get; init; }
    public int  PendingHistoryWrites { get; init; }
    public int  RetryingHistoryWrites { get; init; }
    public int  HistoryWritesCompleted { get; init; }
}

/// <summary>Full game state snapshot sent to all clients on every state change.</summary>
public class GameStateSnapshot
{
    public string ServerInstance { get; init; } = "";
    public long Revision { get; init; }
    public string? GameId        { get; set; }
    public string State          { get; set; } = "Idle";
    public string HostingState   { get; set; } = "Idle";
    public int    GameTypeId     { get; set; }
    public string GameTypeCode   { get; set; } = "";
    public string GameTypeLabel  { get; set; } = "";
    public string GameObjective  { get; set; } = "";
    public string GameOperatorNote { get; set; } = "";
    public int    RoleCycleSeconds { get; set; }
    public bool   ReverseHuntDirection { get; set; }
    public bool   RequiresStandardHosting { get; set; }
    public bool   IsTeamGame     { get; set; }
    public int    TeamCount      { get; set; }
    public int    PlannedPlayerCount { get; set; }

    /// <summary>True only after a complete, non-forced debrief finalized library scores.</summary>
    public bool   ResultsFinalized { get; set; }
    public GameHistorySettings? Settings { get; set; }
    public string GuidedJoinHint { get; set; } = "";

    // Timer data - TV uses these to sync its countdown/game clock after late connect
    public int  CountdownSeconds     { get; set; } = 30;
    public int  GameMinutes          { get; set; } = 10;
    public long? PhaseRemainingMs { get; set; }
    public long ServerTimeMs         { get; set; }
    public long CountdownStartedAtMs { get; set; }
    public long GameStartedAtMs      { get; set; }

    public List<PlayerSnapshot> Players { get; set; } = [];
    public List<TeamSnapshot> Teams { get; set; } = [];
}

/// <summary>Per-player data included in GameStateSnapshot.</summary>
public class PlayerSnapshot
{
    public string Id          { get; set; } = "";
    public int    Team        { get; set; }
    public int    Slot        { get; set; }
    public string Name        { get; set; } = "";
    public bool   Confirmed   { get; set; }

    /// <summary>Effective accepted tags landed, with repeated IR frames capped per target HP.</summary>
    public int HitsScored   { get; set; }

    /// <summary>Total tags received, reported by the tagger.</summary>
    public int HitsTaken    { get; set; }

    /// <summary>Calculated score from the library (accounts for game type scoring rules).</summary>
    public int Score        { get; set; }
    public int Rank         { get; set; }

    public int  TaggerId     { get; set; }
    public bool Dropped      { get; set; }
    public bool AllDebriefed { get; set; }
}

/// <summary>Authoritative team result calculated by the game engine.</summary>
public class TeamSnapshot
{
    public int Number { get; set; }
    public int Score  { get; set; }
    public int Rank   { get; set; }
}

// ── Responses ─────────────────────────────────────────────────────────────────

public class SavedPlayersResponse
{
    public int PlayerCount { get; set; }
    public List<PlayerSettings.PlayerRow> Rows { get; set; } = [];
}
