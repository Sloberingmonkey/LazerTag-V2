namespace LazerTagWebServer.Models;

/// <summary>
/// Immutable-on-disk record of a completed arena game. SchemaVersion allows future
/// releases to add fields while retaining the ability to read Version 1 results.
/// </summary>
public sealed record CompletedGameRecord
{
    public int SchemaVersion { get; init; } = 1;
    public string GameId { get; init; } = "";
    public string ProtocolGameId { get; init; } = "";
    public string SoftwareVersion { get; init; } = "unknown";
    public int GameTypeId { get; init; }
    public string GameTypeName { get; init; } = "";
    public string GameTypeLabel { get; init; } = "";
    public bool IsTeamGame { get; init; }
    public DateTimeOffset HostedAtUtc { get; init; }
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset EndedAtUtc { get; init; }
    public bool EndedEarly { get; init; }
    public bool Forced { get; init; }
    public bool AllPlayersDebriefed { get; init; }

    /// <summary>Forced or incomplete debriefs retain diagnostics but not authoritative scores.</summary>
    public bool ResultsFinalized => AllPlayersDebriefed && !Forced;

    /// <summary>
    /// The operator finished with missing reports and scores were calculated from the
    /// reports that arrived. Records saved before this field existed read as false.
    /// </summary>
    public bool PartialResults { get; init; }
    public GameHistorySettings Settings { get; init; } = new();
    public IReadOnlyList<GameHistoryPlayer> Players { get; init; } = [];
    public IReadOnlyList<GameHistoryTeam> Teams { get; init; } = [];
}

public sealed record GameHistorySettings
{
    public int Minutes { get; init; }
    public int CountdownSeconds { get; init; }
    public int GlobalHp { get; init; }
    public int GlobalShields { get; init; }
    public int GlobalReloads { get; init; }
    public int GlobalMega { get; init; }
    public int GlobalStartingAmmo { get; init; }
    public int GlobalSleepTimeout { get; init; }
    public bool SlowTags { get; init; }
    public bool TeamTags { get; init; }
    public bool MedicMode { get; init; }
    public bool AutoReload { get; init; }
    public bool FullAuto { get; init; }
    public bool DisableIff { get; init; }
    public bool DisableHitRegister { get; init; }
    public bool EnhancedProtocol { get; init; }
}

public sealed record GameHistoryPlayer
{
    public string Id { get; init; } = "";
    public int Team { get; init; }
    public int Slot { get; init; }
    public string Name { get; init; } = "";
    public int TaggerId { get; init; }
    public int Score { get; init; }
    public int Rank { get; init; }
    public int HitsScored { get; init; }
    public int HitsTaken { get; init; }
    public bool Confirmed { get; init; }
    public bool Dropped { get; init; }
    public bool DebriefComplete { get; init; }
    public int Hp { get; init; }
    public int Shields { get; init; }
    public int Reloads { get; init; }
    public bool AutoReload { get; init; }
    public bool FullAuto { get; init; }
    public int StartingAmmo { get; init; }
    public int SleepTimeoutMinutes { get; init; }
}

public sealed record GameHistoryTeam
{
    public int Team { get; init; }
    public int Score { get; init; }
    public int Rank { get; init; }
    public int PlayerCount { get; init; }
    public int Survivors { get; init; }
}

public sealed record CompletedGameSummary
{
    public string GameId { get; init; } = "";
    public string GameTypeLabel { get; init; } = "";
    public DateTimeOffset StartedAtUtc { get; init; }
    public DateTimeOffset EndedAtUtc { get; init; }
    public bool EndedEarly { get; init; }
    public bool Forced { get; init; }
    public bool AllPlayersDebriefed { get; init; }
    public bool ResultsFinalized => AllPlayersDebriefed && !Forced;
    public bool PartialResults { get; init; }
    public int PlayerCount { get; init; }
}
