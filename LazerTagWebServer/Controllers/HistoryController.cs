using System.Globalization;
using System.Text;
using LazerTagWebServer.Models;
using LazerTagWebServer.Security;
using LazerTagWebServer.Services;
using Microsoft.AspNetCore.Mvc;

namespace LazerTagWebServer.Controllers;

[ApiController]
[Route("api/history")]
[OperatorAuthorize]
public sealed class HistoryController : ControllerBase
{
    private readonly GameHistoryStore _history;

    public HistoryController(GameHistoryStore history) => _history = history;

    [HttpGet]
    public async Task<IActionResult> Recent([FromQuery] int limit = 50, CancellationToken cancellationToken = default)
    {
        var results = await _history.GetRecentAsync(limit, cancellationToken);
        return Ok(results);
    }

    [HttpGet("{id}")]
    public async Task<IActionResult> Get(string id, CancellationToken cancellationToken)
    {
        var result = await _history.GetAsync(id, cancellationToken);
        return result == null ? NotFound() : Ok(result);
    }

    [HttpGet("{id}/download.json")]
    public async Task<IActionResult> DownloadJson(string id, CancellationToken cancellationToken)
    {
        var result = await _history.GetAsync(id, cancellationToken);
        if (result == null) return NotFound();

        byte[]? json = await _history.GetRawJsonAsync(id, cancellationToken);
        return json == null
            ? NotFound()
            : File(json, "application/json; charset=utf-8", $"lazertag-{result.GameId}.json");
    }

    [HttpGet("{id}/download.csv")]
    public async Task<IActionResult> DownloadCsv(string id, CancellationToken cancellationToken)
    {
        var result = await _history.GetAsync(id, cancellationToken);
        if (result == null) return NotFound();

        byte[] csv = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true).GetBytes(CreateCsv(result));
        return File(csv, "text/csv; charset=utf-8", $"lazertag-{result.GameId}.csv");
    }

    private static string CreateCsv(CompletedGameRecord game)
    {
        var csv = new StringBuilder();
        csv.AppendLine("Game ID,Game type,Started UTC,Ended UTC,Ended early,Forced,All debriefed,Results finalized,Player ID,Name,Team,Slot,Tagger ID,Rank,Score,Hits scored,Hits taken,Debrief complete");

        foreach (var player in game.Players.OrderBy(player => player.Team).ThenBy(player => player.Slot))
        {
            AppendRow(csv,
                game.GameId,
                game.GameTypeLabel,
                game.StartedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                game.EndedAtUtc.ToString("O", CultureInfo.InvariantCulture),
                game.EndedEarly,
                game.Forced,
                game.AllPlayersDebriefed,
                game.ResultsFinalized,
                player.Id,
                ProtectSpreadsheetCell(player.Name),
                player.Team,
                player.Slot,
                player.TaggerId,
                game.ResultsFinalized ? player.Rank : null,
                game.ResultsFinalized ? player.Score : null,
                player.HitsScored,
                player.HitsTaken,
                player.DebriefComplete);
        }

        return csv.ToString();
    }

    private static void AppendRow(StringBuilder csv, params object?[] values)
        => csv.AppendLine(string.Join(',', values.Select(value => EscapeCsv(Convert.ToString(value, CultureInfo.InvariantCulture) ?? ""))));

    private static string EscapeCsv(string value)
        => value.IndexOfAny([',', '"', '\r', '\n']) >= 0
            ? $"\"{value.Replace("\"", "\"\"")}\""
            : value;

    private static string ProtectSpreadsheetCell(string value)
        => value.Length > 0 && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r'
            ? "'" + value
            : value;
}
