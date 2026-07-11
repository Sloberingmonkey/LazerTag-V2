using LazerTagHostLibrary;
using LazerTagWebServer.Models;
using LazerTagWebServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace LazerTagWebServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class GameController : ControllerBase
{
    private sealed record GameTypeOption(int Id, string Label, int TeamCount, bool IsDefault = false);

    private static readonly GameTypeOption[] GameTypeOptions =
    [
        new((int)GameType.CustomLazerTag,           "Custom Lazer Tag (Solo)",    1),
        new((int)GameType.CustomLazerTagTwoTeams,   "Custom Lazer Tag (2 Teams)", 2, IsDefault: true),
        new((int)GameType.CustomLazerTagThreeTeams, "Custom Lazer Tag (3 Teams)", 3),
        new((int)GameType.RespawnTwoTeams,          "Respawn (2 Teams)",          2),
        new((int)GameType.RespawnThreeTeams,        "Respawn (3 Teams)",          3),
        new((int)GameType.SurvivalTwoTeams,         "Survival (2 Teams)",         2),
    ];

    private static readonly IReadOnlyDictionary<int, GameTypeOption> GameTypesById =
        GameTypeOptions.ToDictionary(option => option.Id);

    private readonly HostGunService _service;
    public GameController(HostGunService service) => _service = service;

    [HttpGet("state")]
    public IActionResult GetState() => Ok(_service.GetGameState());

    [HttpGet("telemetry")]
    public IActionResult GetTelemetry() => Ok(_service.Telemetry);

    [HttpGet("gametypes")]
    public IActionResult GetGameTypes() => Ok(GameTypeOptions);

    [HttpGet("gametypedefaults/{id:int}")]
    public IActionResult GetGameTypeDefaults(int id)
    {
        if (!GameTypesById.TryGetValue(id, out var option))
            return BadRequest(new { Error = "Unknown game type." });

        try
        {
            var info = GameTypes.GetInfo((GameType)id);
            return Ok(new
            {
                DefaultMinutes  = info.DefaultGameTimeMinutes,
                DefaultHp       = info.DefaultTags,
                DefaultShields  = info.DefaultShields,
                DefaultReloads  = info.DefaultReloads == 0xff ? 0 : info.DefaultReloads,
                DefaultMega     = info.DefaultMega    == 0xff ? 0 : info.DefaultMega,
                DefaultSlowTags = info.DefaultSlowTags,
                TeamCount       = option.TeamCount,
            });
        }
        catch (Exception)
        {
            return BadRequest(new { Error = "Game defaults could not be loaded." });
        }
    }

    [HttpGet("savedplayers")]
    public IActionResult GetSavedPlayers()
    {
        var (count, rows) = _service.LoadSavedPlayers();
        return Ok(new SavedPlayersResponse { PlayerCount = count, Rows = rows });
    }

    [HttpPost("connect")]
    public async Task<IActionResult> Connect()
    {
        bool connected = await _service.EnsureRelayConnectedAsync();
        return connected
            ? Ok(new { Success = true })
            : StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { Error = "The relay is not reachable. Check its power and network connection." });
    }

    [HttpPost("start")]
    public async Task<IActionResult> Start([FromBody] StartGameRequest req)
    {
        if (!TryValidateAndNormalize(req, out string error))
            return BadRequest(new { Error = error });

        var (success, startError) = await _service.StartHostingAsync(req);
        if (!success)
            return Conflict(new { Error = string.IsNullOrWhiteSpace(startError) ? "Hosting could not be started." : startError });

        return Ok(new { Success = true });
    }

    [HttpPost("countdown")]
    public async Task<IActionResult> Countdown()
    {
        bool started = await _service.StartCountdownAsync();
        return started
            ? Ok(new { Success = true })
            : Conflict(new { Error = "Countdown could not start. Confirm that every planned player is connected and at least two players have joined." });
    }

    [HttpPost("endgame")]
    public async Task<IActionResult> EndGame()
    {
        bool ended = await _service.EndGameAsync();
        return ended
            ? Ok(new { Success = true })
            : Conflict(new { Error = "There is no active game to end." });
    }

    [HttpPost("next")]
    public async Task<IActionResult> Next()
    {
        await _service.NextAsync();
        return Ok(new { Success = true });
    }

    [HttpPost("cleanup")]
    public async Task<IActionResult> Cleanup()
    {
        await _service.CleanupHostAsync();
        return Ok(new { Success = true });
    }

    private static bool TryValidateAndNormalize(StartGameRequest req, out string error)
    {
        error = "";

        if (!GameTypesById.TryGetValue((int)req.GameType, out var gameType))
            return Fail("Select a supported game type.", out error);

        req.GameTypeLabel = gameType.Label; // Never trust a client-supplied label in logs.

        if (!InRange(req.Minutes, 1, 255))
            return Fail("Minutes must be between 1 and 255.", out error);
        if (!InRange(req.CountdownSeconds, 5, 255))
            return Fail("Countdown must be between 5 and 255 seconds.", out error);
        if (!InRange(req.GlobalHp, 1, 254))
            return Fail("Global HP must be between 1 and 254.", out error);
        if (!InRange(req.GlobalShields, 0, 254))
            return Fail("Global shields must be between 0 and 254.", out error);
        if (req.GlobalReloads is < 0 or > 99)
            return Fail("Global reloads must be between 0 and 99; use 0 for unlimited.", out error);
        if (!InRange(req.GlobalMega, 0, 99))
            return Fail("Global mega shots must be between 0 and 99.", out error);
        if (!InRange(req.GlobalStartingAmmo, 0, 254))
            return Fail("Global starting ammo must be between 0 and 254; use 0 for unlimited.", out error);
        if (!InRange(req.GlobalSleepTimeout, 0, 99))
            return Fail("Global sleep timeout must be between 0 and 99 minutes.", out error);

        if (req.Players is null || req.Players.Count is < 1 or > 24)
            return Fail("Player count must be between 1 and 24.", out error);

        var usedSlots = new HashSet<int>();
        foreach (var player in req.Players)
        {
            if (player is null)
                return Fail("Player entries cannot be empty.", out error);

            if (!InRange(player.Slot, 1, 24) || !usedSlots.Add(player.Slot))
                return Fail("Each player must have a unique slot from 1 to 24.", out error);

            player.Name = (player.Name ?? "").Trim();
            if (player.Name.Length > 40)
                return Fail($"Player {player.Slot}'s name is longer than 40 characters.", out error);

            if (gameType.TeamCount <= 1)
            {
                player.TeamPreference = null;
                player.TeamPreferenceLabel = "Auto";
            }
            else if (player.TeamPreference.HasValue &&
                     !InRange(player.TeamPreference.Value, 1, gameType.TeamCount))
            {
                return Fail($"Player {player.Slot}'s team must be Auto or Team 1–{gameType.TeamCount}.", out error);
            }
            else
            {
                player.TeamPreferenceLabel = player.TeamPreference.HasValue
                    ? $"Team {player.TeamPreference.Value}"
                    : "Auto";
            }

            if (player.Hp.HasValue && !InRange(player.Hp.Value, 1, 254))
                return Fail($"Player {player.Slot}'s HP must be blank or 1–254.", out error);
            if (player.Shields.HasValue && !InRange(player.Shields.Value, 0, 254))
                return Fail($"Player {player.Slot}'s shields must be blank or 0–254.", out error);
            if (player.Reloads is < 0 or > 99)
                return Fail($"Player {player.Slot}'s reloads must be blank or 0–99.", out error);
            if (player.Ammo.HasValue && !InRange(player.Ammo.Value, 0, 254))
                return Fail($"Player {player.Slot}'s ammo must be blank or 0–254.", out error);
            if (player.SleepTimeout.HasValue && !InRange(player.SleepTimeout.Value, 0, 99))
                return Fail($"Player {player.Slot}'s sleep timeout must be blank or 0–99 minutes.", out error);
        }

        return true;
    }

    private static bool InRange(int value, int min, int max) => value >= min && value <= max;

    private static bool Fail(string message, out string error)
    {
        error = message;
        return false;
    }
}
