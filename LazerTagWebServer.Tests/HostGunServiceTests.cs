using System.Reflection;
using LazerTagHostLibrary;
using LazerTagWebServer.Hubs;
using LazerTagWebServer.Models;
using LazerTagWebServer.Security;
using LazerTagWebServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed class HostGunServiceTests
{
    [Fact]
    public async Task A_full_history_backlog_preserves_every_game_and_drains_after_storage_recovers()
    {
        var directory = CreateTemporaryDirectory();
        var service = CreateService(directory);
        try
        {
            string history = Path.Combine(directory, "game-history");
            await File.WriteAllTextAsync(history, "temporarily unavailable");
            var capture = typeof(HostGunService).GetMethod("TrackHistoryWrite", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var records = Enumerable.Range(0, 64)
                .Select(_ => new CompletedGameRecord { GameId = Guid.NewGuid().ToString("N") }).ToArray();
            foreach (var record in records)
            {
                capture.Invoke(service, [record]);
                capture.Invoke(service, [record]);
            }
            Assert.Equal(records.Length, service.Telemetry.PendingHistoryWrites);
            var admission = await service.StartHostingAsync(new StartGameRequest());
            Assert.False(admission.Success);
            Assert.Contains("waiting to save", admission.Error);
            Assert.False(service.IsHosting);

            File.Delete(history);
            await service.DisposeAsync();
            Assert.Equal(0, service.Telemetry.PendingHistoryWrites);
            Assert.Equal(records.Length, service.Telemetry.HistoryWritesCompleted);
            Assert.Equal(records.Select(record => record.GameId).Order(),
                Directory.GetFiles(history, "*.json").Select(Path.GetFileNameWithoutExtension).Order());
        }
        finally
        {
            await service.DisposeAsync();
            TryDeleteDirectory(directory);
        }
    }

    [Fact]
    public async Task Countdown_requires_connected_relay_and_every_declared_team()
    {
        string dataDirectory = CreateTemporaryDirectory();
        var service = CreateService(dataDirectory);
        try
        {
            var host = CreateJoinedHost(GameType.CustomLazerTagThreeTeams, [1, 2, 2]);
            AttachHost(service, host, plannedPlayerCount: 3);

            SetRelayConnected(service, connected: false);
            Assert.False(await service.StartCountdownAsync());
            Assert.Equal(HostGun.HostingStates.Adding, host.HostingState);

            SetRelayConnected(service, connected: true);
            Assert.False(await service.StartCountdownAsync());
            Assert.Equal(HostGun.HostingStates.Adding, host.HostingState);
        }
        finally
        {
            await service.DisposeAsync();
            TryDeleteDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Countdown_accepts_confirmed_players_covering_every_declared_team()
    {
        string dataDirectory = CreateTemporaryDirectory();
        var service = CreateService(dataDirectory);
        try
        {
            var host = CreateJoinedHost(GameType.CustomLazerTagThreeTeams, [1, 2, 3]);
            AttachHost(service, host, plannedPlayerCount: 3);
            SetRelayConnected(service, connected: true);

            Assert.True(await service.StartCountdownAsync());
            Assert.Equal(HostGun.HostingStates.Countdown, host.HostingState);
        }
        finally
        {
            await service.DisposeAsync();
            TryDeleteDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Solo_snapshot_uses_global_slots_and_does_not_publish_protocol_teams()
    {
        string dataDirectory = CreateTemporaryDirectory();
        var service = CreateService(dataDirectory);
        try
        {
            var preferences = Enumerable.Repeat<int?>(null, 10).ToArray();
            var host = CreateJoinedHost(GameType.CustomLazerTag, preferences);
            AttachHost(service, host, plannedPlayerCount: 10);

            GameStateSnapshot snapshot = service.GetGameState();

            Assert.False(snapshot.IsTeamGame);
            Assert.Equal(0, snapshot.TeamCount);
            Assert.Empty(snapshot.Teams);
            Assert.Equal(Enumerable.Range(1, 10), snapshot.Players.Select(player => player.Slot));
            Assert.All(snapshot.Players, player => Assert.Equal(0, player.Team));
        }
        finally
        {
            await service.DisposeAsync();
            TryDeleteDirectory(dataDirectory);
        }
    }

    [Fact]
    public async Task Dropped_player_is_not_reported_as_successfully_debriefed()
    {
        string dataDirectory = CreateTemporaryDirectory();
        var service = CreateService(dataDirectory);
        try
        {
            var host = CreateJoinedHost(GameType.CustomLazerTagTwoTeams, [1, 2]);
            AttachHost(service, host, plannedPlayerCount: 2);

            var dropped = host.Players.First();
            dropped.Dropped = true;
            dropped.TagSummaryReceived = true;

            GameStateSnapshot snapshot = service.GetGameState();
            var player = snapshot.Players.Single(p => p.TaggerId == (byte)dropped.TaggerId);

            Assert.True(player.Dropped);
            Assert.False(player.AllDebriefed);
        }
        finally
        {
            await service.DisposeAsync();
            TryDeleteDirectory(dataDirectory);
        }
    }

    [Fact]
    public void Forced_solo_history_keeps_diagnostics_without_claiming_final_scores_or_teams()
    {
        var preferences = Enumerable.Repeat<int?>(null, 10).ToArray();
        var host = CreateJoinedHost(GameType.CustomLazerTag, preferences);
        try
        {
            host.SetGameStartCountdownTime(0);
            Assert.True(host.StartCountdown());
            host.Update();
            host.EndGame();
            foreach (var player in host.Players.Skip(1)) player.TagSummaryReceived = true;
            Assert.True(host.FinishDebrief());
            Assert.Equal(HostGun.HostingStates.GameOver, host.HostingState);

            Type sessionType = typeof(HostGunService).GetNestedType(
                "ActiveGameHistory", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ActiveGameHistory type was not found.");
            object session = Activator.CreateInstance(sessionType)
                ?? throw new InvalidOperationException("ActiveGameHistory could not be created.");
            sessionType.GetProperty("GameTypeLabel")!.SetValue(session, "Solo Test");
            sessionType.GetProperty("HostedAtUtc")!.SetValue(session, DateTimeOffset.UtcNow.AddMinutes(-10));
            sessionType.GetProperty("EndedAtUtc")!.SetValue(session, DateTimeOffset.UtcNow);

            MethodInfo projector = typeof(HostGunService).GetMethod(
                "CreateCompletedGameRecord", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("History projector was not found.");
            var record = (CompletedGameRecord)(projector.Invoke(null, [host, session])
                ?? throw new InvalidOperationException("History projector returned no record."));

            Assert.False(record.AllPlayersDebriefed);
            Assert.True(record.Forced);
            Assert.False(record.ResultsFinalized);
            Assert.True(record.PartialResults);
            Assert.Empty(record.Teams);
            Assert.Equal(Enumerable.Range(1, 10), record.Players.Select(player => player.Slot));
            Assert.All(record.Players, player => Assert.Equal(0, player.Team));
        }
        finally
        {
            host.Protocol.Dispose();
        }
    }

    [Fact]
    public void Pi_host_requires_a_network_relay_address()
    {
        string directory = CreateTemporaryDirectory();
        try
        {
            var error = Assert.Throws<InvalidOperationException>(() => CreateService(directory, "/dev/ttyUSB0"));
            Assert.Contains("host:port", error.Message);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static HostGunService CreateService(string dataDirectory, string relayAddress = "127.0.0.1:9")
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arena:RelayAddress"] = relayAddress,
                ["Arena:DisableRelayReconnect"] = "true",
                ["Arena:DataDirectory"] = dataDirectory,
            })
            .Build();
        var history = new GameHistoryStore(configuration, NullLogger<GameHistoryStore>.Instance);
        var sessions = new OperatorSessionService(configuration,
            new Microsoft.Extensions.Hosting.Internal.HostingEnvironment { EnvironmentName = "Development" },
            NullLogger<OperatorSessionService>.Instance);
        return new HostGunService(null!, NullLogger<HostGunService>.Instance, history, configuration, new GameHubConnections(sessions));
    }

    private static HostGun CreateJoinedHost(GameType gameType, int?[] teamPreferences)
    {
        var host = new HostGun(string.Empty);
        host.Protocol.PacketSending += (_, args) => args.Cancel = true;
        host.SetPlannedTeamPreferences(teamPreferences);
        host.StartServer(new GameDefinition
        {
            GameType = gameType,
            GameId = new RandomId(0x5A),
            GameTimeMinutes = 10,
            Tags = 10,
            Reloads = 0xFF,
            Mega = 0,
            CountdownTimeSeconds = 30,
            UseLtarEnhancedProtocol = true,
        });

        for (int index = 0; index < teamPreferences.Length; index++)
        {
            byte taggerId = checked((byte)(0x20 + index));
            byte gameId = host.GameDefinition.GameId;
            Assert.True(host.ProcessPacket(PacketOf(PacketType.RequestJoinGameEnhanced, gameId, taggerId)));
            Assert.True(host.ProcessPacket(PacketOf(PacketType.AcknowledgePlayerAssignmentEnhanced, gameId, taggerId)));
        }

        return host;
    }

    private static void AttachHost(HostGunService service, HostGun host, int plannedPlayerCount)
    {
        SetField(service, "_hostGun", host);
        SetField(service, "_plannedNames",
            Enumerable.Range(1, plannedPlayerCount).Select(index => $"Player {index}").ToList());
    }

    private static void SetRelayConnected(HostGunService service, bool connected)
        => SetField(service, "_telemetry", new TelemetrySnapshot { RelayConnected = connected });

    private static void SetField<T>(HostGunService service, string name, T value)
    {
        var field = typeof(HostGunService).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException($"HostGunService field {name} was not found.");
        field.SetValue(service, value);
    }

    private static Packet PacketOf(PacketType type, params ushort[] data)
    {
        var packet = new Packet(type);
        foreach (ushort value in data)
            packet.Data.Add(new Signature(SignatureType.Data, value));
        packet.PopulateChecksum();
        return packet;
    }

    private static string CreateTemporaryDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "lazertag-host-service-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Best-effort test cleanup on Windows.
        }
    }
}
