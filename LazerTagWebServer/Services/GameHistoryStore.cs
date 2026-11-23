using System.Text.Json;
using LazerTagWebServer.Models;

namespace LazerTagWebServer.Services;

/// <summary>
/// Persists one immutable JSON document per completed game. Writes are committed with
/// a same-directory rename so a power loss cannot expose a partially written result.
/// History errors are logged and returned to the caller instead of interrupting play.
/// </summary>
public sealed class GameHistoryStore
{
    private const int DefaultRetention = 1000;
    private const int MaximumRetention = 10000;
    private readonly SemaphoreSlim _fileGate = new(1, 1);
    private readonly ILogger<GameHistoryStore> _log;
    private readonly string _historyDirectory;
    private readonly int _retention;
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public GameHistoryStore(IConfiguration configuration, ILogger<GameHistoryStore> log)
    {
        _log = log;

        string configuredDirectory = configuration["Arena:DataDirectory"] ?? "";
        string dataDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? AppContext.BaseDirectory
            : configuredDirectory;

        _historyDirectory = Path.Combine(Path.GetFullPath(dataDirectory), "game-history");
        _retention = Math.Clamp(
            configuration.GetValue<int?>("Arena:HistoryRetention") ?? DefaultRetention,
            1,
            MaximumRetention);
    }

    public async Task<bool> SaveAsync(CompletedGameRecord record, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeId(record.GameId, out string gameId))
        {
            _log.LogError("Game history record has an invalid game ID: {GameId}", record.GameId);
            return false;
        }

        await _fileGate.WaitAsync(cancellationToken);
        string? temporaryPath = null;
        try
        {
            Directory.CreateDirectory(_historyDirectory);
            string destinationPath = GetRecordPath(gameId);

            // A completed game is immutable. A retry after an uncertain write is successful
            // when the final file already exists, but never replaces its original contents.
            if (File.Exists(destinationPath)) return true;

            byte[] json = JsonSerializer.SerializeToUtf8Bytes(record, _jsonOptions);
            temporaryPath = Path.Combine(_historyDirectory, $".{gameId}.{Guid.NewGuid():N}.tmp");
            await using (var stream = new FileStream(
                temporaryPath,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(json, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temporaryPath, destinationPath, overwrite: false);
            temporaryPath = null;
            PruneOldRecords();
            _log.LogInformation("Archived completed game {GameId}", gameId);
            return true;
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unable to save completed game {GameId}; gameplay will continue", gameId);
            return false;
        }
        finally
        {
            if (temporaryPath != null) TryDelete(temporaryPath);
            _fileGate.Release();
        }
    }

    public async Task<IReadOnlyList<CompletedGameSummary>> GetRecentAsync(
        int limit,
        CancellationToken cancellationToken = default)
    {
        limit = Math.Clamp(limit, 1, 100);
        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            if (!Directory.Exists(_historyDirectory)) return [];

            var results = new List<CompletedGameSummary>(limit);
            foreach (var file in new DirectoryInfo(_historyDirectory)
                         .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                         .OrderByDescending(file => file.LastWriteTimeUtc))
            {
                var record = await TryReadAsync(file.FullName, cancellationToken);
                if (record == null) continue;

                results.Add(new CompletedGameSummary
                {
                    GameId = record.GameId,
                    GameTypeLabel = record.GameTypeLabel,
                    StartedAtUtc = record.StartedAtUtc,
                    EndedAtUtc = record.EndedAtUtc,
                    EndedEarly = record.EndedEarly,
                    Forced = record.Forced,
                    AllPlayersDebriefed = record.AllPlayersDebriefed,
                    PlayerCount = record.Players.Count,
                });

                if (results.Count == limit) break;
            }

            return results.OrderByDescending(result => result.EndedAtUtc).ToList();
        }
        catch (Exception ex)
        {
            _log.LogError(ex, "Unable to read recent game history");
            return [];
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<CompletedGameRecord?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeId(id, out string gameId)) return null;

        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            return await TryReadAsync(GetRecordPath(gameId), cancellationToken);
        }
        finally
        {
            _fileGate.Release();
        }
    }

    public async Task<byte[]?> GetRawJsonAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!TryNormalizeId(id, out string gameId)) return null;

        await _fileGate.WaitAsync(cancellationToken);
        try
        {
            string path = GetRecordPath(gameId);
            return File.Exists(path) ? await File.ReadAllBytesAsync(path, cancellationToken) : null;
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unable to read game history document {GameId}", gameId);
            return null;
        }
        finally
        {
            _fileGate.Release();
        }
    }

    private async Task<CompletedGameRecord?> TryReadAsync(string path, CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path)) return null;
            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                bufferSize: 16 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            return await JsonSerializer.DeserializeAsync<CompletedGameRecord>(stream, _jsonOptions, cancellationToken);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Skipping unreadable game history file {HistoryFile}", Path.GetFileName(path));
            return null;
        }
    }

    private void PruneOldRecords()
    {
        try
        {
            var expired = new DirectoryInfo(_historyDirectory)
                .EnumerateFiles("*.json", SearchOption.TopDirectoryOnly)
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Skip(_retention);

            foreach (var file in expired)
                TryDelete(file.FullName);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Game history was saved, but old-record retention could not be applied");
        }
    }

    private string GetRecordPath(string normalizedId) => Path.Combine(_historyDirectory, normalizedId + ".json");

    private static bool TryNormalizeId(string? value, out string normalized)
    {
        if (Guid.TryParseExact(value, "N", out Guid id))
        {
            normalized = id.ToString("N");
            return true;
        }

        normalized = "";
        return false;
    }

    private void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Unable to delete game history file {HistoryFile}", Path.GetFileName(path));
        }
    }
}
