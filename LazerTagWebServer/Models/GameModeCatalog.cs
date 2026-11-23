using LazerTagHostLibrary;

namespace LazerTagWebServer.Models;

/// <summary>
/// Public game modes that are fully supported by the current web host and LTAR game engine.
/// Specialized Tag Master variants stay hidden until their role/assignment behavior is verified.
/// </summary>
public sealed record GameModeOption(
    GameType GameType,
    string Code,
    string Label,
    string Category,
    int TeamCount,
    string Objective,
    string OperatorNote,
    int MinMinutes = 1,
    int MaxMinutes = 99,
    int MinuteStep = 1,
    int RoleCycleSeconds = 0,
    bool RequiresStandardHosting = false,
    bool IsDefault = false,
    int? MaxPlayersOverride = null)
{
    public int Id => (int)GameType;
    public int MinPlayers => Math.Max(2, TeamCount);
    public int MaxPlayers => MaxPlayersOverride ?? (TeamCount <= 1 ? 24 : TeamCount * 8);
    public int MaxHpAndShields => RequiresStandardHosting ? BinaryCodedDecimal.MaxDecimalValue : 254;
    public int MaxCountdownSeconds => RequiresStandardHosting ? BinaryCodedDecimal.MaxDecimalValue : 255;
}

public static class GameModeCatalog
{
    public static readonly GameModeOption[] Options =
    [
        new(GameType.CustomLazerTag, "CUST", "Custom Lazer Tag (Solo)", "Battle", 1,
            "Every player is an opponent. Stay active and score tags; final scores arrive during debrief.",
            "Good default for free-for-all play."),
        new(GameType.CustomLazerTagTwoTeams, "2TMS", "Custom Lazer Tag (2 Teams)", "Battle", 2,
            "Two teams battle for tags and survival. Final team and player scores arrive during debrief.",
            "Balanced general-purpose team mode.", IsDefault: true),
        new(GameType.CustomLazerTagThreeTeams, "3TMS", "Custom Lazer Tag (3 Teams)", "Battle", 3,
            "Three teams battle for tags and survival. Final team and player scores arrive during debrief.",
            "Each team supports up to eight players."),

        new(GameType.HideAndSeek, "HDSK", "Hide and Seek", "Role Games", 2,
            "One team seeks while the other hides. The roles switch every 60 seconds.",
            "The taggers enforce the hunt/hide rules. The public TV shows the role cycle, not live scores.",
            MinMinutes: 2, MaxMinutes: 98, MinuteStep: 2, RoleCycleSeconds: 60),
        new(GameType.HuntThePrey, "HUNT", "Hunt the Prey", "Role Games", 3,
            "Each team hunts one team while hiding from another. Hunting direction switches every 60 seconds.",
            "The taggers enforce the hunt direction. The public TV shows the direction-change timer.",
            MinMinutes: 2, MaxMinutes: 98, MinuteStep: 2, RoleCycleSeconds: 60),

        new(GameType.KingsTwoTeams, "2KNG", "Kings (2 Teams)", "Kings", 2,
            "Protect your hidden King while trying to tag out the opposing King.",
            "The first assigned player on each team is that team's King. This mode uses standard LTAR hosting so the original IFF King clue remains intact.",
            RequiresStandardHosting: true),
        new(GameType.KingsThreeTeams, "3KNG", "Kings (3 Teams)", "Kings", 3,
            "Protect your hidden King while trying to tag out the opposing Kings.",
            "The first assigned player on each team is that team's King. This mode uses standard LTAR hosting so the original IFF King clue remains intact.",
            RequiresStandardHosting: true),

        new(GameType.OwnTheZone, "OWNZ", "Own the Zone (Solo)", "Zone", 1,
            "Control the relay's IR zone. Each tagger stores its own Zone Time for the post-game debrief.",
            "Place the relay/zone emitter where players can physically contest its IR coverage."),
        new(GameType.OwnTheZoneTwoTeams, "2TOZ", "Own the Zone (2 Teams)", "Zone", 2,
            "Control the relay's IR zone. Taggers accumulate Zone Time locally and report it after the game.",
            "Place the relay/zone emitter where both teams can physically contest its IR coverage."),
        new(GameType.OwnTheZoneThreeTeams, "3TOZ", "Own the Zone (3 Teams)", "Zone", 3,
            "Control the relay's IR zone. Taggers accumulate Zone Time locally and report it after the game.",
            "Place the relay/zone emitter where all three teams can physically contest its IR coverage."),

        new(GameType.Respawn, "RESP", "Respawn (Solo)", "Respawn", 1,
            "When neutralized, return to the relay's IR supply zone to re-enter play.",
            "Keep the relay/zone emitter in a deliberate respawn location."),
        new(GameType.RespawnTwoTeams, "2TRS", "Respawn (2 Teams)", "Respawn", 2,
            "When neutralized, return to the relay's IR supply zone to re-enter play.",
            "Keep the relay/zone emitter in a deliberate respawn location accessible to both teams."),
        new(GameType.RespawnThreeTeams, "3TRS", "Respawn (3 Teams)", "Respawn", 3,
            "When neutralized, return to the relay's IR supply zone to re-enter play.",
            "Keep the relay/zone emitter in a deliberate respawn location accessible to all teams."),

        new(GameType.Survival, "SURV", "Survival (Solo)", "Survival", 1,
            "Stay active as long as possible while scoring tags. Survival time is reported during debrief.",
            "The Pi does not need live hit telemetry; each tagger tracks its own survival data."),
        new(GameType.SurvivalTwoTeams, "2SRV", "Survival (2 Teams)", "Survival", 2,
            "Keep your team active as long as possible while scoring tags. Results arrive during debrief.",
            "Survival time is stored on the taggers during play."),
        new(GameType.SurvivalThreeTeams, "3SRV", "Survival (3 Teams)", "Survival", 3,
            "Keep your team active as long as possible while scoring tags. Results arrive during debrief.",
            "Survival time is stored on the taggers during play."),
    ];

    private static readonly IReadOnlyDictionary<GameType, GameModeOption> ByType =
        Options.ToDictionary(option => option.GameType);

    public static bool TryGet(GameType gameType, out GameModeOption option)
        => ByType.TryGetValue(gameType, out option!);

    public static GameModeOption? GetOrNull(GameType gameType)
        => ByType.TryGetValue(gameType, out var option) ? option : null;
}
