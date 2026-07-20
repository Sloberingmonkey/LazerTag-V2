using System.Reflection;
using LazerTagHostLibrary;
using LazerTagWebServer.Models;
using LazerTagWebServer.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed class HostGunServiceTests
{
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
    public void Forced_solo_history_keeps_diagnostics_without_claiming_final_scores_or_teams()
    {
        var preferences = Enumerable.Repeat<int?>(null, 10).ToArray();
        var host = CreateJoinedHost(GameType.CustomLazerTag, preferences);
        try
        {
            // This is the important edge case: every report may arrive immediately before the
            // operator forces Summary to finish, but that forced library path still skips score
            // calculation and therefore must never advertise authoritative results.
            foreach (var player in host.Players)
                player.TagSummaryReceived = true;

            Type sessionType = typeof(HostGunService).GetNestedType(
                "ActiveGameHistory", BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("ActiveGameHistory type was not found.");
            object session = Activator.CreateInstance(sessionType)
                ?? throw new InvalidOperationException("ActiveGameHistory could not be created.");
            sessionType.GetProperty("GameTypeLabel")!.SetValue(session, "Solo Test");
            sessionType.GetProperty("HostedAtUtc")!.SetValue(session, DateTimeOffset.UtcNow.AddMinutes(-10));
            sessionType.GetProperty("EndedAtUtc")!.SetValue(session, DateTimeOffset.UtcNow);
            sessionType.GetProperty("Forced")!.SetValue(session, true);

            MethodInfo projector = typeof(HostGunService).GetMethod(
                "CreateCompletedGameRecord", BindingFlags.Static | BindingFlags.NonPublic)
                ?? throw new InvalidOperationException("History projector was not found.");
            var record = (CompletedGameRecord)(projector.Invoke(null, [host, session])
                ?? throw new InvalidOperationException("History projector returned no record."));

            Assert.True(record.AllPlayersDebriefed);
            Assert.True(record.Forced);
            Assert.False(record.ResultsFinalized);
            Assert.Empty(record.Teams);
            Assert.Equal(Enumerable.Range(1, 10), record.Players.Select(player => player.Slot));
            Assert.All(record.Players, player => Assert.Equal(0, player.Team));
        }
        finally
        {
            host.Protocol.Dispose();
        }
    }

    private static HostGunService CreateService(string dataDirectory)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Arena:RelayAddress"] = "127.0.0.1:9",
                ["Arena:DisableRelayReconnect"] = "true",
                ["Arena:DataDirectory"] = dataDirectory,
            })
            .Build();
        var history = new GameHistoryStore(configuration, NullLogger<GameHistoryStore>.Instance);
        return new HostGunService(null!, NullLogger<HostGunService>.Instance, history, configuration);
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
