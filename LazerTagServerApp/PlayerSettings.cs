using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;

namespace LazerTagServerApp
{
    /// <summary>
    /// Persists the player setup grid between sessions.
    /// Saves to players.json next to the exe on game start, loads on startup.
    /// </summary>
    public static class PlayerSettings
    {
        private static readonly string FilePath = Path.Combine(
            AppDomain.CurrentDomain.BaseDirectory, "players.json");

        [DataContract]
        public class PlayerRow
        {
            [DataMember] public string Name           { get; set; }
            [DataMember] public string TeamPreference { get; set; }
            [DataMember] public string Hp             { get; set; }
            [DataMember] public string Shields     { get; set; }
            [DataMember] public string Reloads     { get; set; }
            [DataMember] public bool   AutoReload  { get; set; }
            [DataMember] public bool   FullAuto    { get; set; }
            [DataMember] public string Ammo        { get; set; }
            [DataMember] public string SleepTimeout{ get; set; }
        }

        [DataContract]
        private class SaveData
        {
            [DataMember] public int             PlayerCount { get; set; }
            [DataMember] public List<PlayerRow> Rows        { get; set; }
        }

        public static void Save(int playerCount, IEnumerable<PlayerRow> rows)
        {
            try
            {
                var data = new SaveData { PlayerCount = playerCount, Rows = rows.ToList() };
                var ser  = new DataContractJsonSerializer(typeof(SaveData));
                using (var fs = new FileStream(FilePath, FileMode.Create, FileAccess.Write))
                    ser.WriteObject(fs, data);
            }
            catch { /* non-fatal */ }
        }

        public static bool TryLoad(out int playerCount, out List<PlayerRow> rows)
        {
            playerCount = 0;
            rows        = new List<PlayerRow>();
            try
            {
                if (!File.Exists(FilePath)) return false;
                var ser = new DataContractJsonSerializer(typeof(SaveData));
                using (var fs = new FileStream(FilePath, FileMode.Open, FileAccess.Read))
                {
                    var data = (SaveData)ser.ReadObject(fs);
                    playerCount = data.PlayerCount;
                    rows        = data.Rows ?? new List<PlayerRow>();
                    return true;
                }
            }
            catch { return false; }
        }
    }
}
