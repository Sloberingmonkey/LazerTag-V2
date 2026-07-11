using System.Text.Json;
using System.Text.Json.Serialization;

namespace LazerTagWebServer;

/// <summary>
/// Persists the player setup grid between sessions.
/// Saves to players.json next to the app binary on game start, loads on startup.
/// </summary>
public static class PlayerSettings
{
    private const int MaxPlayers = 24;
    private static readonly object FileGate = new();
    private static readonly string FilePath = Path.Combine(AppContext.BaseDirectory, "players.json");
    private static readonly string TempFilePath = FilePath + ".tmp";

    public class PlayerRow
    {
        [JsonPropertyName("Name")]           public string Name           { get; set; } = "";
        [JsonPropertyName("TeamPreference")] public string TeamPreference { get; set; } = "Auto";
        [JsonPropertyName("Hp")]             public string Hp             { get; set; } = "";
        [JsonPropertyName("Shields")]        public string Shields        { get; set; } = "";
        [JsonPropertyName("Reloads")]        public string Reloads        { get; set; } = "";
        [JsonPropertyName("AutoReload")]     public bool   AutoReload     { get; set; }
        [JsonPropertyName("FullAuto")]       public bool   FullAuto       { get; set; }
        [JsonPropertyName("Ammo")]           public string Ammo           { get; set; } = "";
        [JsonPropertyName("SleepTimeout")]   public string SleepTimeout   { get; set; } = "";
    }

    private class SaveData
    {
        [JsonPropertyName("PlayerCount")] public int             PlayerCount { get; set; }
        [JsonPropertyName("Rows")]        public List<PlayerRow> Rows        { get; set; } = [];
    }

    public static void Save(int playerCount, IEnumerable<PlayerRow> rows)
    {
        lock (FileGate)
        {
            try
            {
                var normalizedRows = rows.Take(MaxPlayers).Select(Normalize).ToList();
                var data = new SaveData
                {
                    PlayerCount = Math.Clamp(playerCount, 1, MaxPlayers),
                    Rows = normalizedRows,
                };

                // Write beside the destination and replace it atomically. A power loss or process
                // stop during serialization can no longer leave a half-written players.json file.
                File.WriteAllText(TempFilePath, JsonSerializer.Serialize(data));
                File.Move(TempFilePath, FilePath, overwrite: true);
            }
            catch (Exception ex)
            {
                TryDeleteTempFile();
                Console.Error.WriteLine($"PlayerSettings.Save failed: {ex.Message}");
            }
        }
    }

    public static bool TryLoad(out int playerCount, out List<PlayerRow> rows)
    {
        lock (FileGate)
        {
            playerCount = 0;
            rows = [];

            try
            {
                if (!File.Exists(FilePath)) return false;

                var data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(FilePath));
                if (data == null || data.PlayerCount is < 1 or > MaxPlayers) return false;

                playerCount = data.PlayerCount;
                rows = (data.Rows ?? []).Take(MaxPlayers).Select(Normalize).ToList();
                return true;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"PlayerSettings.TryLoad failed: {ex.Message}");
                return false;
            }
        }
    }

    private static PlayerRow Normalize(PlayerRow? row)
    {
        row ??= new PlayerRow();
        row.Name           = (row.Name ?? "").Trim();
        row.TeamPreference = string.IsNullOrWhiteSpace(row.TeamPreference) ? "Auto" : row.TeamPreference.Trim();
        row.Hp             = (row.Hp ?? "").Trim();
        row.Shields        = (row.Shields ?? "").Trim();
        row.Reloads        = (row.Reloads ?? "").Trim();
        row.Ammo           = (row.Ammo ?? "").Trim();
        row.SleepTimeout   = (row.SleepTimeout ?? "").Trim();
        return row;
    }

    private static void TryDeleteTempFile()
    {
        try
        {
            if (File.Exists(TempFilePath)) File.Delete(TempFilePath);
        }
        catch
        {
            // Best-effort cleanup only; the next save overwrites the temporary file.
        }
    }
}
