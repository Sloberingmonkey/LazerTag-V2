using System.Text.Json;
using System.Text.Json.Serialization;
using System.Globalization;

namespace LazerTagWebServer;

/// <summary>
/// Persists the player setup grid between sessions.
/// Saves to players.json in the configured arena data directory.
/// </summary>
public static class PlayerSettings
{
    private const int MaxPlayers = 24;
    private const int MinPlayers = 2;
    private static readonly object FileGate = new();
    private static string _filePath = Path.Combine(AppContext.BaseDirectory, "players.json");
    private static string TempFilePath => _filePath + ".tmp";

    public static void ConfigureDataDirectory(string? dataDirectory)
    {
        if (string.IsNullOrWhiteSpace(dataDirectory)) return;

        lock (FileGate)
        {
            var fullPath = Path.GetFullPath(dataDirectory);
            Directory.CreateDirectory(fullPath);
            _filePath = Path.Combine(fullPath, "players.json");
        }
    }

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
                    PlayerCount = Math.Clamp(playerCount, MinPlayers, MaxPlayers),
                    Rows = normalizedRows,
                };

                // Write beside the destination and replace it atomically. A power loss or process
                // stop during serialization can no longer leave a half-written players.json file.
                File.WriteAllText(TempFilePath, JsonSerializer.Serialize(data));
                File.Move(TempFilePath, _filePath, overwrite: true);
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
                if (!File.Exists(_filePath)) return false;

                var data = JsonSerializer.Deserialize<SaveData>(File.ReadAllText(_filePath));
                if (data == null || data.PlayerCount is < MinPlayers or > MaxPlayers) return false;

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
        row.Name = (row.Name ?? "").Trim();
        if (row.Name.Length > 40) row.Name = row.Name[..40];

        var team = (row.TeamPreference ?? "").Trim();
        row.TeamPreference = team is "Team 1" or "Team 2" or "Team 3" ? team : "Auto";
        row.Hp           = NormalizeNumber(row.Hp, 1, 254);
        row.Shields      = NormalizeNumber(row.Shields, 0, 254);
        row.Reloads      = NormalizeNumber(row.Reloads, 0, 99);
        row.Ammo         = NormalizeNumber(row.Ammo, 0, 254);
        row.SleepTimeout = NormalizeNumber(row.SleepTimeout, 0, 99);
        return row;
    }

    private static string NormalizeNumber(string? value, int min, int max)
    {
        return int.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out int parsed)
               && parsed >= min && parsed <= max
            ? parsed.ToString(CultureInfo.InvariantCulture)
            : "";
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
