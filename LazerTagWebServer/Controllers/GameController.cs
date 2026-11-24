using LazerTagHostLibrary;
using LazerTagWebServer.Models;
using LazerTagWebServer.Security;
using LazerTagWebServer.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace LazerTagWebServer.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class GameController : ControllerBase
{
    private static readonly IReadOnlyDictionary<int, GameModeOption> GameTypesById =
        GameModeCatalog.Options.ToDictionary(option => option.Id);


    private readonly HostGunService _service;
    public GameController(HostGunService service) => _service = service;

    [HttpGet("state")]
    public IActionResult GetState() => Ok(_service.GetGameState());

    [HttpGet("telemetry")]
    public IActionResult GetTelemetry() => Ok(_service.Telemetry);

    [HttpGet("gametypes")]
    public IActionResult GetGameTypes() => Ok(GameModeCatalog.Options);

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
                DefaultTeamTags = info.DefaultTeamTags,
                TeamCount       = option.TeamCount,
                MinMinutes      = option.MinMinutes,
                MaxMinutes      = option.MaxMinutes,
                MinuteStep      = option.MinuteStep,
            });
        }
        catch (Exception)
        {
            return BadRequest(new { Error = "Game defaults could not be loaded." });
        }
    }

    [HttpGet("savedplayers")]
    [OperatorAuthorize]
    public IActionResult GetSavedPlayers()
    {
        var (count, rows) = _service.LoadSavedPlayers();
        return Ok(new SavedPlayersResponse { PlayerCount = count, Rows = rows });
    }

    [HttpPost("connect")]
    [OperatorAuthorize]
    [EnableRateLimiting("operator-control")]
    public async Task<IActionResult> Connect()
    {
        bool connected = await _service.EnsureRelayConnectedAsync();
        return connected
            ? Ok(new { Success = true })
            : StatusCode(StatusCodes.Status503ServiceUnavailable,
                new { Error = "The relay is not reachable. Check its power and network connection." });
    }

    [HttpPost("start")]
    [OperatorAuthorize]
    [EnableRateLimiting("operator-control")]
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
    [OperatorAuthorize]
    [EnableRateLimiting("operator-control")]
    public async Task<IActionResult> Countdown()
    {
        bool started = await _service.StartCountdownAsync();
        return started
            ? Ok(new { Success = true })
            : Conflict(new { Error = "Countdown could not start. Confirm the exact planned roster is connected and every tagger shows Ready." });
    }

    [HttpPost("endgame")]
    [HttpPost("{gameId:guid}/endgame")]
    [OperatorAuthorize]
    [EnableRateLimiting("operator-control")]
    public async Task<IActionResult> EndGame([FromRoute] Guid? gameId = null)
    {
        bool ended = await _service.EndGameAsync(gameId);
        return ended
            ? Ok(new { Success = true })
            : Conflict(new { Error = "This game is no longer active." });
    }

    [HttpPost("{gameId:guid}/finish-debrief")]
    [OperatorAuthorize]
    [EnableRateLimiting("operator-control")]
    public async Task<IActionResult> FinishDebrief(Guid gameId)
    {
        bool finished = await _service.FinishDebriefAsync(gameId);
        return finished
            ? Ok(new { Success = true })
            : Conflict(new { Error = "This game is no longer waiting for reports." });
    }

    [HttpPost("{gameId:guid}/players/{taggerId:int}/remove")]
    [OperatorAuthorize]
    [EnableRateLimiting("operator-control")]
    public async Task<IActionResult> RemovePlayer(Guid gameId, int taggerId)
    {
        if (taggerId is < 0 or > 255)
            return BadRequest(new { Error = "Tagger ID must be between 0 and 255." });
        bool removed = await _service.RemoveLobbyPlayerAsync(gameId, (byte)taggerId);
        return removed
            ? Ok(new { Success = true })
            : Conflict(new { Error = "Players can only be removed from the lobby, before the countdown starts." });
    }

    [HttpPost("next")]
    [OperatorAuthorize]
    [EnableRateLimiting("operator-control")]
    public async Task<IActionResult> Next()
    {
        bool cleared = await _service.NextAsync();
        return cleared
            ? Ok(new { Success = true })
            : Conflict(new { Error = "Wait for Game Over before clearing this game." });
    }

    [HttpPost("cleanup")]
    [OperatorAuthorize]
    [EnableRateLimiting("operator-control")]
    public async Task<IActionResult> Cleanup()
    {
        bool cleared = await _service.NextAsync();
        return cleared
            ? Ok(new { Success = true })
            : Conflict(new { Error = "The game cannot be cleared until debriefing reaches Game Over." });
    }

    private static bool TryValidateAndNormalize(StartGameRequest req, out string error)
    {
        error = "";

        if (!GameTypesById.TryGetValue((int)req.GameType, out var gameType))
            return Fail("Select a supported game type.", out error);

        req.GameTypeLabel = gameType.Label; // Never trust a client-supplied label in logs.
        // LTAR does not implement hosted Medic Mode. Keep the wire state and saved history truthful
        // even if an older preset or API client sends the field as true.
        req.MedicMode = false;

        if (!InRange(req.Minutes, gameType.MinMinutes, gameType.MaxMinutes))
            return Fail($"Minutes for {gameType.Label} must be between {gameType.MinMinutes} and {gameType.MaxMinutes}.", out error);
        if ((req.Minutes - gameType.MinMinutes) % gameType.MinuteStep != 0)
            return Fail($"Minutes for {gameType.Label} must use {gameType.MinuteStep}-minute steps.", out error);
        if (!InRange(req.CountdownSeconds, 5, gameType.MaxCountdownSeconds))
            return Fail($"Countdown for {gameType.Label} must be between 5 and {gameType.MaxCountdownSeconds} seconds.", out error);
        if (!InRange(req.GlobalHp, 1, gameType.MaxHpAndShields))
            return Fail($"Global HP for {gameType.Label} must be between 1 and {gameType.MaxHpAndShields}.", out error);
        if (!InRange(req.GlobalShields, 0, gameType.MaxHpAndShields))
            return Fail($"Global shields for {gameType.Label} must be between 0 and {gameType.MaxHpAndShields}.", out error);
        if (req.GlobalReloads is < 0 or > 99)
            return Fail("Global reloads must be between 0 and 99; use 0 for unlimited.", out error);
        if (!InRange(req.GlobalMega, 0, 99))
            return Fail("Global mega shots must be between 0 and 99.", out error);
        if (!InRange(req.GlobalStartingAmmo, 0, 254))
            return Fail("Global starting ammo must be between 0 and 254; use 0 for unlimited.", out error);
        if (!InRange(req.GlobalSleepTimeout, 0, 99))
            return Fail("Global sleep timeout must be between 0 and 99 minutes.", out error);

        if (req.Players is null || req.Players.Count < gameType.MinPlayers || req.Players.Count > gameType.MaxPlayers)
            return Fail($"Player count for {gameType.Label} must be between {gameType.MinPlayers} and {gameType.MaxPlayers}.", out error);

        var usedSlots = new HashSet<int>();
        foreach (var player in req.Players)
        {
            if (player is null)
                return Fail("Player entries cannot be empty.", out error);

            if (!InRange(player.Slot, 1, gameType.MaxPlayers) || !usedSlots.Add(player.Slot))
                return Fail($"Each player must have a unique slot from 1 to {gameType.MaxPlayers}.", out error);

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
                return Fail($"Player {player.Slot}'s team must be Auto or Team 1-{gameType.TeamCount}.", out error);
            }
            else
            {
                player.TeamPreferenceLabel = player.TeamPreference.HasValue
                    ? $"Team {player.TeamPreference.Value}"
                    : "Auto";
            }

            if (player.Hp.HasValue && !InRange(player.Hp.Value, 1, gameType.MaxHpAndShields))
                return Fail($"Player {player.Slot}'s HP must be blank or 1-{gameType.MaxHpAndShields}.", out error);
            if (player.Shields.HasValue && !InRange(player.Shields.Value, 0, gameType.MaxHpAndShields))
                return Fail($"Player {player.Slot}'s shields must be blank or 0-{gameType.MaxHpAndShields}.", out error);
            if (player.Reloads is < 0 or > 99)
                return Fail($"Player {player.Slot}'s reloads must be blank or 0-99.", out error);
            if (player.Ammo.HasValue && !InRange(player.Ammo.Value, 0, 254))
                return Fail($"Player {player.Slot}'s ammo must be blank or 0-254.", out error);
            if (player.SleepTimeout.HasValue && !InRange(player.SleepTimeout.Value, 0, 99))
                return Fail($"Player {player.Slot}'s sleep timeout must be blank or 0-99 minutes.", out error);
        }

        if (gameType.TeamCount > 1)
        {
            if (req.Players.Count < gameType.TeamCount)
                return Fail($"{gameType.Label} needs at least one player on each of its {gameType.TeamCount} teams.", out error);

            // Resolve Auto rows now, in stable roster order. HostGun treats a planned team as
            // authoritative, so this guarantees every declared team is represented and prevents
            // an otherwise valid lobby from becoming impossible to start after all taggers join.
            var teamCounts = new int[gameType.TeamCount + 1];
            var autoPlayers = req.Players
                .Where(player => !player.TeamPreference.HasValue)
                .OrderBy(player => player.Slot)
                .ToList();

            foreach (var player in req.Players.Where(player => player.TeamPreference.HasValue))
                teamCounts[player.TeamPreference!.Value]++;

            int overCapacityTeam = Enumerable.Range(1, gameType.TeamCount)
                .FirstOrDefault(team => teamCounts[team] > 8);
            if (overCapacityTeam != 0)
                return Fail($"Team {overCapacityTeam} has more than the protocol limit of 8 planned players.", out error);

            var missingTeams = Enumerable.Range(1, gameType.TeamCount)
                .Where(team => teamCounts[team] == 0)
                .ToList();
            if (autoPlayers.Count < missingTeams.Count)
            {
                return Fail(
                    $"Every team needs a planned player. Add Auto players or assign Team {string.Join(" or Team ", missingTeams)}.",
                    out error);
            }

            int autoIndex = 0;
            foreach (int team in missingTeams)
            {
                autoPlayers[autoIndex++].TeamPreference = team;
                teamCounts[team]++;
            }

            while (autoIndex < autoPlayers.Count)
            {
                int team = Enumerable.Range(1, gameType.TeamCount)
                    .OrderBy(candidate => teamCounts[candidate])
                    .ThenBy(candidate => candidate)
                    .First();
                autoPlayers[autoIndex++].TeamPreference = team;
                teamCounts[team]++;
            }
        }

        if (gameType.RequiresStandardHosting)
        {
            // Kings relies on the original game announcement so LTARs retain the hidden-King
            // IFF behavior. Enhanced-only options are intentionally cleared for this mode.
            req.AutoReload = false;
            req.FullAuto = false;
            req.DisableIff = false;
            req.DisableHitRegister = false;
            req.GlobalStartingAmmo = 0;
            req.GlobalSleepTimeout = 0;
            foreach (var player in req.Players)
            {
                player.AutoReload = null;
                player.FullAuto = null;
                player.Ammo = null;
                player.SleepTimeout = null;
            }
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
