using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using LazerTagHostLibrary;
using LazerTagWebServer.Models;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace LazerTagWebServer.Tests;

// PlayerSettings has one process-wide data directory. Give these appliance tests exclusive use.
[CollectionDefinition(nameof(ArenaRelayTests), DisableParallelization = true)]
public sealed class ArenaRelayCollection { }

[Collection(nameof(ArenaRelayTests))]
public sealed partial class ArenaRelayTests
{
    [Fact]
    public async Task Starting_a_game_and_installing_an_update_exclude_one_another()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var setup = new StartGameRequest { Players = [new() { Slot = 1 }, new() { Slot = 2 }] };
        var initial = await arena.StateAsync();
        var telemetry = await arena.TelemetryAsync();
        Assert.NotEmpty(initial.ServerInstance);
        Assert.Equal(initial.ServerInstance, telemetry.ServerInstance);
        Assert.True(telemetry.Revision > initial.Revision);
        using (arena.OpenUpdateLock())
        {
            using var blocked = await arena.StartAsync(setup);
            Assert.Equal(HttpStatusCode.Conflict, blocked.StatusCode);
            Assert.Contains("maintenance", await blocked.Content.ReadAsStringAsync());
            Assert.False(arena.Relay.Pending());
        }

        var starting = arena.StartAsync(setup);
        await arena.WaitUntilAsync(() => Task.FromResult(arena.Relay.Pending()));
        Assert.Throws<IOException>(() => arena.OpenUpdateLock());
        using var relay = await arena.AcceptRelayAsync();
        (await starting).EnsureSuccessStatusCode();
        var lobby = await arena.StateAsync();
        Assert.True(lobby.Revision > telemetry.Revision);
        Assert.Throws<IOException>(() => arena.OpenUpdateLock());
        (await arena.PostAsync("endgame")).EnsureSuccessStatusCode();
        var cleared = await arena.StateAsync();
        Assert.Equal(("Idle", initial.ServerInstance), (cleared.HostingState, cleared.ServerInstance));
        Assert.True(cleared.Revision > lobby.Revision);
        using var idle = arena.OpenUpdateLock();
    }

    [Theory]
    [InlineData("ERROR version", 0)]
    [InlineData(null, 1)]
    public async Task Failed_handshake_then_retry_counts_both_attempts_and_one_failure(string? reply, int expectedTimeouts)
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var connect = arena.PostAsync("connect");
        using var rejected = await arena.AcceptRelayAsync(reply: reply);
        using var accepted = await arena.AcceptRelayAsync();
        (await connect).EnsureSuccessStatusCode();
        (await arena.PostAsync("connect")).EnsureSuccessStatusCode();
        var counters = await arena.TelemetryAsync();
        Assert.Equal(2, counters.RelayConnectAttempts);
        Assert.Equal(1, counters.RelayConnectedCount);
        Assert.Equal(1, counters.RelayConnectFailures);
        Assert.Equal(1, counters.RelayHandshakeFailures);
        Assert.Equal(0, counters.RelayConnectionRefusals);
        Assert.Equal(expectedTimeouts, counters.RelayTimeouts);
    }

    [Fact]
    public async Task Unexpected_lobby_packet_is_counted_at_the_normal_log_level()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var start = arena.StartAsync(new StartGameRequest { Players = [new() { Slot = 1 }, new() { Slot = 2 }] });
        using var relay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        byte game = (await relay.ReadEnhancedAdvertisementAsync()).Game;
        await relay.SendPacketAsync(PacketType.RankReport, game, 0);
        await arena.WaitUntilAsync(async () => (await arena.TelemetryAsync()).UnexpectedPackets == 1);
        Assert.Equal("Adding", (await arena.StateAsync()).HostingState);
    }

    [Fact]
    public async Task Debrief_release_interrupted_in_transit_retries_on_the_replacement_connection()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var starting = arena.StartAsync(new StartGameRequest
        {
            CountdownSeconds = 5, Players = [new() { Slot = 1 }, new() { Slot = 2 }],
        });
        using var first = await arena.AcceptRelayAsync();
        (await starting).EnsureSuccessStatusCode();
        byte game = await arena.JoinTwoTaggersAndPlayAsync(first);
        (await arena.PostAsync("endgame")).EnsureSuccessStatusCode();
        await first.WaitForLineAsync("CMD 10 31 9 0");
        first.DisconnectOnNextRelease();
        await first.SendPacketAsync(PacketType.TagSummary, game, 0x08, 0, 0, 0, 0, 0, 0);
        await arena.WaitUntilAsync(async () => !await arena.RelayConnectedAsync());
        var connect = arena.PostAsync("connect");
        using var replacement = await arena.AcceptRelayAsync();
        (await connect).EnsureSuccessStatusCode();
        await replacement.WaitForLineAsync($"CMD 10 {(int)PacketType.AssignPlayerSuccessEnhanced:X} 9 0");
        Assert.Equal(game, await replacement.ReadDataByteAsync());
        Assert.Equal(0x08, await replacement.ReadDataByteAsync());
        Assert.Equal("Summary", (await arena.StateAsync()).HostingState);
    }

    [Fact]
    public async Task Failed_start_keeps_the_last_saved_roster_and_the_next_success_publishes_its_own()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var original = new StartGameRequest { Players = [new() { Slot = 1, Name = "First" }, new() { Slot = 2, Name = "Second" }] };
        var replacement = new StartGameRequest { Players = [new() { Slot = 1, Name = "Third", Hp = 17 }, new() { Slot = 2, Name = "Fourth" }] };
        var start = arena.StartAsync(original);
        using var firstRelay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        (await arena.PostAsync("endgame")).EnsureSuccessStatusCode();
        var saved = await arena.SavedPlayersAsync();
        Assert.Equal("First", saved["rows"]![0]!["Name"]!.GetValue<string>());

        var endpoint = (IPEndPoint)arena.Relay.LocalEndpoint;
        var before = await arena.TelemetryAsync();
        arena.Relay.Stop();
        Assert.Equal(HttpStatusCode.Conflict, (await arena.StartAsync(replacement)).StatusCode);
        var after = await arena.TelemetryAsync();
        int attempts = after.RelayConnectAttempts - before.RelayConnectAttempts;
        Assert.True(attempts > 0);
        Assert.Equal(attempts, after.RelayConnectionRefusals - before.RelayConnectionRefusals);
        Assert.Equal(attempts, after.RelayConnectFailures - before.RelayConnectFailures);
        Assert.Equal(before.RelayHandshakeFailures, after.RelayHandshakeFailures);
        var idle = await arena.StateAsync();
        Assert.Equal("Idle", idle.HostingState);
        Assert.Empty(idle.GuidedJoinHint);
        Assert.True(JsonNode.DeepEquals(saved, await arena.SavedPlayersAsync()));

        using var restored = new TcpListener(endpoint);
        restored.Start();
        var retry = arena.StartAsync(replacement);
        using var relay = await arena.AcceptRelayAsync(restored);
        (await retry).EnsureSuccessStatusCode();
        Assert.Contains("Third", (await arena.StateAsync()).GuidedJoinHint);
        var row = (await arena.SavedPlayersAsync())["rows"]![0]!;
        Assert.Equal("Third", row["Name"]!.GetValue<string>());
        Assert.Equal("17", row["Hp"]!.GetValue<string>());
    }

    [Fact]
    public async Task Completed_game_survives_a_storage_outage_after_the_operator_clears_it()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        string history = Path.Combine(arena.DataDirectory, "game-history");
        await File.WriteAllTextAsync(history, "temporary storage outage");
        var start = arena.StartAsync(new StartGameRequest
        {
            CountdownSeconds = 5, Players = [new() { Slot = 1 }, new() { Slot = 2 }],
        });
        using var relay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        await arena.JoinTwoTaggersAndPlayAsync(relay);
        var id = (await arena.StateAsync()).GameId;
        (await arena.PostAsync($"{id}/endgame")).EnsureSuccessStatusCode();
        (await arena.PostAsync($"{id}/finish-debrief")).EnsureSuccessStatusCode();
        (await arena.PostAsync("next")).EnsureSuccessStatusCode();
        await arena.WaitUntilAsync(async () => (await arena.TelemetryAsync()).RetryingHistoryWrites == 1);
        await Task.Delay(1500);
        Assert.Equal(1, (await arena.TelemetryAsync()).PendingHistoryWrites);
        Assert.Throws<IOException>(() => arena.OpenUpdateLock());
        File.Delete(history);
        var record = await arena.CompletedGameAsync().WaitAsync(TimeSpan.FromSeconds(7));
        Assert.Equal(id, record.GameId);
        Assert.True(record.Forced);
        Assert.Equal(id + ".json", Path.GetFileName(Assert.Single(Directory.GetFiles(history))));
        await arena.WaitUntilAsync(async () => (await arena.TelemetryAsync()).PendingHistoryWrites == 0);
        Assert.Equal(0, (await arena.TelemetryAsync()).RetryingHistoryWrites);
        Assert.Equal(1, (await arena.TelemetryAsync()).HistoryWritesCompleted);
        using var idle = arena.OpenUpdateLock();
    }

    [Fact]
    public async Task Reconnecting_cannot_complete_a_join_packet_from_the_previous_socket()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var start = arena.StartAsync(new StartGameRequest
        {
            Players = [new() { Slot = 1 }, new() { Slot = 2 }],
        });
        using var first = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        await first.WaitForLineAsync("CMD 10 81 9 0");
        byte game = await first.ReadDataByteAsync();
        var incomplete = new Packet(PacketType.RequestJoinGameEnhanced);
        incomplete.DataAdd(game);
        incomplete.DataAdd(1);
        await first.SendLineAsync($"RCV {(int)incomplete.Type:X} 9 0");
        await first.SendLineAsync($"RCV {game:X} 8 0");
        await first.SendLineAsync("RCV 1 8 0");
        first.FinishSending();
        await arena.WaitUntilAsync(async () => !await arena.RelayConnectedAsync());

        var connect = arena.PostAsync("connect");
        using var replacement = await arena.AcceptRelayAsync();
        (await connect).EnsureSuccessStatusCode();
        await replacement.SendLineAsync($"RCV {0x100 | incomplete.PopulateChecksum().Data:X} 9 0");
        await replacement.SendPacketAsync(PacketType.RequestJoinGameEnhanced, game, 2);
        await replacement.SendPacketAsync(PacketType.AcknowledgePlayerAssignmentEnhanced, game, 2);
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).Players.Any(p => p.Confirmed));
        Assert.Equal(2, Assert.Single((await arena.StateAsync()).Players).TaggerId);
    }

    [Fact]
    public async Task Removing_a_lobby_player_frees_the_seat_for_a_rebooted_tagger()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var start = arena.StartAsync(new StartGameRequest
        {
            CountdownSeconds = 5, Players = [new() { Slot = 1 }, new() { Slot = 2 }],
        });
        using var relay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        await relay.WaitForLineAsync("CMD 10 81 9 0");
        byte game = await relay.ReadDataByteAsync();
        foreach (byte id in new byte[] { 1, 2 })
        {
            await relay.SendPacketAsync(PacketType.RequestJoinGameEnhanced, game, id);
            await relay.SendPacketAsync(PacketType.AcknowledgePlayerAssignmentEnhanced, game, id);
        }
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).Players.Count(p => p.Confirmed) == 2);
        string match = (await arena.StateAsync()).GameId!;

        // Tagger 2 is power-cycled and comes back as tagger 3. Freeing its old entry lets it rejoin.
        Assert.Equal(HttpStatusCode.Conflict, (await arena.PostAsync($"{match}/players/9/remove")).StatusCode);
        (await arena.PostAsync($"{match}/players/2/remove")).EnsureSuccessStatusCode();
        Assert.Equal(new[] { 1 }, (await arena.StateAsync()).Players.Select(p => p.TaggerId));
        await relay.SendPacketAsync(PacketType.RequestJoinGameEnhanced, game, 3);
        await relay.SendPacketAsync(PacketType.AcknowledgePlayerAssignmentEnhanced, game, 3);
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).Players.Count(p => p.Confirmed) == 2);
        Assert.Equal(new[] { 1, 3 }, (await arena.StateAsync()).Players.Select(p => p.TaggerId).Order());

        // Once the countdown starts, no one can be removed.
        (await arena.PostAsync("countdown")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await arena.PostAsync($"{match}/players/1/remove")).StatusCode);
        Assert.Equal(2, (await arena.StateAsync()).Players.Count);
    }

    [Fact]
    public async Task Expired_first_reservation_reopens_its_own_guided_seat()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var start = arena.StartAsync(new StartGameRequest
        {
            GameType = GameType.CustomLazerTagTwoTeams, CountdownSeconds = 5,
            Players =
            [
                new() { Slot = 1, Name = "Rowan", TeamPreference = 1, Hp = 12, Ammo = 17, Reloads = 0 },
                new() { Slot = 2, Name = "Finley", TeamPreference = 2, Hp = 23, Ammo = 38, Reloads = 4 },
            ],
        });
        using var relay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        var firstSeat = await relay.ReadEnhancedAdvertisementAsync();
        byte game = firstSeat.Game;
        Assert.Equal((12, 17), (firstSeat.Hp, firstSeat.Ammo));

        await relay.SendPacketAsync(PacketType.RequestJoinGameEnhanced, game, 1);
        await relay.WaitForLineAsync($"CMD 10 {(int)PacketType.AssignPlayerEnhanced:X} 9 0");
        var secondSeat = await relay.ReadEnhancedAdvertisementAsync();
        Assert.Equal((game, 23, 38), secondSeat);
        await relay.SendPacketAsync(PacketType.RequestJoinGameEnhanced, game, 2);
        await relay.SendPacketAsync(PacketType.AcknowledgePlayerAssignmentEnhanced, game, 2);
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).Players.Count == 2);
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).Players.Count == 1);
        var lobby = await arena.StateAsync();
        var survivor = Assert.Single(lobby.Players);
        Assert.Equal(("Finley", 2, true), (survivor.Name, survivor.Team, survivor.Confirmed));
        Assert.Contains("Rowan", lobby.GuidedJoinHint);

        // Discard buffered advertisements sent while both seats were occupied.
        (byte Game, int Hp, int Ammo) reopened;
        do { reopened = await relay.ReadEnhancedAdvertisementAsync(); } while (reopened.Hp != 12);
        Assert.Equal(firstSeat, reopened);
        await relay.SendPacketAsync(PacketType.RequestJoinGameEnhanced, game, 3);
        await relay.SendPacketAsync(PacketType.AcknowledgePlayerAssignmentEnhanced, game, 3);
        await relay.SendPacketAsync(PacketType.RequestJoinGameEnhanced, game, 4);
        await relay.WaitForLineAsync("CMD 10 8F 9 0");
        Assert.Equal(game, await relay.ReadDataByteAsync());
        Assert.Equal(4, await relay.ReadDataByteAsync());
        var filled = (await arena.StateAsync()).Players;
        Assert.Equal(new[] { ("Rowan", 1, 3), ("Finley", 2, 2) },
            filled.Select(p => (p.Name, p.Team, (int)p.TaggerId)));
        (await arena.PostAsync("countdown")).EnsureSuccessStatusCode();
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).HostingState == "Playing");
        (await arena.PostAsync("endgame")).EnsureSuccessStatusCode();
        await relay.SendPacketAsync(PacketType.TagSummary, game, 0x10, 3, 0, 0, 0, 0, 2);
        await relay.SendPacketAsync(PacketType.TeamOneTagReport, game, 0x10, 1, 3);
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).Players.Last().AllDebriefed);
        (await arena.PostAsync($"{lobby.GameId}/finish-debrief")).EnsureSuccessStatusCode();

        var history = await arena.CompletedGameAsync();
        Assert.True(history.Forced);
        Assert.False(history.ResultsFinalized);
        Assert.Equal(new[] { ("Rowan", 3, 12, 17), ("Finley", 2, 23, 38) },
            history.Players.Select(p => (p.Name, (int)p.TaggerId, p.Hp, p.StartingAmmo)));
        Assert.Equal(3, history.Players[0].HitsScored);
        Assert.Equal(3, history.Players[1].HitsTaken);

        // A per-player 0 means unlimited reloads, like the global setting.
        Assert.Equal(new[] { 0xff, 4 }, history.Players.Select(p => p.Reloads));

        // Rowan never reported and was dropped, but Finley's report shows Rowan's 3 hits.
        // The forced finish scores the game from the reports that arrived.
        Assert.True(history.PartialResults);
        Assert.True(history.Players[0].Dropped);
        Assert.Equal((3, 1), (history.Players[0].Score, history.Players[0].Rank));
        Assert.Equal((0, 2), (history.Players[1].Score, history.Players[1].Rank));
        var finalState = await arena.StateAsync();
        Assert.Equal((false, true), (finalState.ResultsFinalized, finalState.PartialResults));
    }

    [Fact]
    public async Task Finishing_after_last_report_publishes_and_archives_calculated_ranks()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var start = arena.StartAsync(new StartGameRequest
        {
            CountdownSeconds = 5, Players = [new() { Slot = 1 }, new() { Slot = 2 }],
        });
        using var relay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        byte game = await arena.JoinTwoTaggersAndPlayAsync(relay);
        (await arena.PostAsync("endgame")).EnsureSuccessStatusCode();
        var beforeDebriefRetry = await arena.TelemetryAsync();
        await relay.WaitForLineAsync("CMD 10 31 9 0");
        await arena.WaitUntilAsync(async () => (await arena.TelemetryAsync()).StateTimeouts > beforeDebriefRetry.StateTimeouts);
        int previousReleases = relay.ReleaseAttempts;
        relay.RejectNextRelease();
        await relay.SendPacketAsync(PacketType.TagSummary, game, 0x08, 0, 0, 0, 0, 0, 0);
        await arena.WaitUntilAsync(() => Task.FromResult(relay.ReleaseAttempts >= previousReleases + 2));
        Assert.Equal(1, (await arena.TelemetryAsync()).RelayWriteRejections);
        await relay.SendPacketAsync(PacketType.TagSummary, game, 0x10, 3, 0, 0, 0, 0, 2);
        await relay.SendPacketAsync(PacketType.TeamOneTagReport, game, 0x10, 1, 3);
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).Players.All(p => p.AllDebriefed));
        var debrief = await arena.StateAsync();
        Assert.Equal("Summary", debrief.HostingState);
        (await arena.PostAsync($"{debrief.GameId}/finish-debrief")).EnsureSuccessStatusCode();
        (await arena.PostAsync("endgame")).EnsureSuccessStatusCode();

        var scoreboard = await arena.StateAsync();
        Assert.True(scoreboard.ResultsFinalized);
        Assert.Equal((3, 1), (scoreboard.Players[0].Score, scoreboard.Players[0].Rank));
        await relay.WaitForLineAsync("CMD 10 32 9 0");
        Assert.Equal(game, await relay.ReadDataByteAsync());
        Assert.Equal(0x11, await relay.ReadDataByteAsync()); // Team 1 placed first.
        Assert.Equal(1, await relay.ReadDataByteAsync());
        var history = await arena.CompletedGameAsync();
        Assert.False(history.Forced);
        Assert.True(history.ResultsFinalized);
        Assert.Equal((3, 1), (history.Players[0].Score, history.Players[0].Rank));
    }

    [Fact]
    public async Task Concurrent_end_requests_and_retries_preserve_reports_and_cannot_target_a_later_match()
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var lostReply = new WithholdFirstEndReply();
        using var secondConsole = arena.CreateConsole(lostReply);
        await arena.LoginAsync(secondConsole);
        var setup = new StartGameRequest
        {
            CountdownSeconds = 5, Players = [new() { Slot = 1 }, new() { Slot = 2 }],
        };

        var start = arena.StartAsync(setup);
        using var firstRelay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        byte wireId = await arena.JoinTwoTaggersAndPlayAsync(firstRelay);
        var match = (await arena.StateAsync()).GameId;
        Assert.True(Guid.TryParseExact(match, "N", out _));
        string end = $"{match}/endgame", finish = $"{match}/finish-debrief";
        Assert.Equal(HttpStatusCode.Conflict, (await arena.PostAsync(finish)).StatusCode);

        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var waitingForReply = secondConsole.PostAsync("/api/game/" + end, null, deadline.Token);
        var otherEnd = arena.PostAsync(end);
        await lostReply.ServerResponded.Task.WaitAsync(deadline.Token);
        deadline.CancelAfter(TimeSpan.FromMilliseconds(10));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waitingForReply);
        (await otherEnd).EnsureSuccessStatusCode();
        (await arena.PostAsync(end, secondConsole)).EnsureSuccessStatusCode();
        (await arena.PostAsync("endgame", secondConsole)).EnsureSuccessStatusCode();
        var waiting = await arena.StateAsync();
        Assert.Equal(("Summary", match, false), (waiting.HostingState, waiting.GameId, waiting.ResultsFinalized));
        Assert.Equal(2, waiting.Players.Count);
        Assert.All(waiting.Players, player => Assert.False(player.Dropped || player.AllDebriefed));

        await firstRelay.SendPacketAsync(PacketType.TagSummary, wireId, 0x08, 0, 0, 0, 0, 0, 0);
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).Players[0].AllDebriefed);
        (await arena.PostAsync(finish, secondConsole)).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await arena.PostAsync(finish)).StatusCode);
        var history = await arena.CompletedGameAsync();
        Assert.Equal(match, history.GameId);
        Assert.True(history.Forced);
        Assert.False(history.ResultsFinalized);
        Assert.True(history.Players[0].DebriefComplete);
        Assert.False(history.Players[0].Dropped);
        Assert.True(history.Players[1].Dropped);
        (await arena.PostAsync("next")).EnsureSuccessStatusCode();
        Assert.Null((await arena.StateAsync()).GameId);

        var restart = arena.StartAsync(setup);
        using var secondRelay = await arena.AcceptRelayAsync();
        (await restart).EnsureSuccessStatusCode();
        await arena.JoinTwoTaggersAndPlayAsync(secondRelay);
        var laterMatch = (await arena.StateAsync()).GameId;
        Assert.NotEqual(match, laterMatch);
        Assert.Equal(HttpStatusCode.Conflict, (await arena.PostAsync(end, secondConsole)).StatusCode);
        Assert.Equal("Playing", (await arena.StateAsync()).HostingState);
        (await arena.PostAsync($"{laterMatch}/endgame")).EnsureSuccessStatusCode();
        Assert.Equal(HttpStatusCode.Conflict, (await arena.PostAsync(finish, secondConsole)).StatusCode);
        var laterDebrief = await arena.StateAsync();
        Assert.Equal(("Summary", laterMatch), (laterDebrief.HostingState, laterDebrief.GameId));
        Assert.All(laterDebrief.Players, player => Assert.False(player.Dropped));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Quiet_battle_recovers_its_relay_and_keeps_the_same_game(bool automatic)
    {
        await using var arena = new Arena(automatic);
        await arena.LoginAsync();
        var connect = arena.PostAsync("connect");
        using var standby = await arena.AcceptRelayAsync();
        (await connect).EnsureSuccessStatusCode();

        var start = arena.StartAsync(new StartGameRequest
        {
            GameType = GameType.CustomLazerTagTwoTeams, CountdownSeconds = 5, Minutes = 2,
            Players = [new() { Slot = 1, Name = "Rowan" }, new() { Slot = 2, Name = "Finley" }],
        });
        using var first = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        await first.WaitForLineAsync("CMD 10 81 9 0");
        byte gameId = await first.ReadDataByteAsync();
        foreach (byte id in new byte[] { 1, 2 })
        {
            await first.SendPacketAsync(PacketType.RequestJoinGameEnhanced, gameId, id);
            await first.SendPacketAsync(PacketType.AcknowledgePlayerAssignmentEnhanced, gameId, id);
        }
        await first.SendPacketAsync(PacketType.RequestJoinGameEnhanced, gameId, 3);
        await first.WaitForLineAsync($"CMD 10 {(int)PacketType.AssignPlayerFailedEnhanced:X} 9 0");
        (await arena.PostAsync("countdown")).EnsureSuccessStatusCode();
        await arena.WaitUntilAsync(async () => (await arena.StateAsync()).GameStartedAtMs > 0);
        var before = await arena.StateAsync();

        if (automatic)
        {
            await first.WaitForLineAsync("PING");
            await first.SendLineAsync("PONG");
            await first.WaitForLineAsync("PING");
            Assert.True(await arena.RelayConnectedAsync());
            // Leave TCP open and stop answering. The game sends no zone beacons.
        }
        else
        {
            first.CloseConnection();
        }
        await arena.WaitUntilAsync(async () => !await arena.RelayConnectedAsync());
        var manualRequests = automatic ? [] : new[] { arena.PostAsync("connect"), arena.PostAsync("connect") };
        using var replacement = await arena.AcceptRelayAsync();
        foreach (var response in await Task.WhenAll(manualRequests)) response.EnsureSuccessStatusCode();
        await arena.WaitUntilAsync(arena.RelayConnectedAsync);
        var after = await arena.StateAsync();
        Assert.Equal("Playing", after.HostingState);
        Assert.Equal(before.GameStartedAtMs, after.GameStartedAtMs);
        Assert.Equal(new[] { "Rowan", "Finley" }, after.Players.Select(p => p.Name));
        Assert.All(after.Players, player => Assert.True(player.Confirmed));
        Assert.False(arena.Relay.Pending());
    }

    [Theory]
    [InlineData(GameType.KingsTwoTeams, PacketType.AnnounceGameKingsTwoTeams)]
    [InlineData(GameType.KingsThreeTeams, PacketType.AnnounceGameKingsThreeTeams)]
    public async Task Kings_rejects_unrepresentable_values_and_transmits_99(GameType mode, PacketType announcement)
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var setup = new StartGameRequest
        {
            GameType = mode, GlobalHp = 99, GlobalShields = 99, CountdownSeconds = 99,
            Players = [new() { Slot = 1, Hp = 99, Shields = 99 }, new() { Slot = 2 }, new() { Slot = 3 }],
        };
        foreach (string field in new[] { "GlobalHp", "GlobalShields", "CountdownSeconds", "Hp", "Shields" })
        {
            var request = JsonSerializer.SerializeToNode(setup)!.AsObject();
            var target = field is "Hp" or "Shields" ? request["Players"]![0]!.AsObject() : request;
            target[field] = 100;
            using var response = await arena.StartAsync(request);
            Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
            Assert.Contains("99", await response.Content.ReadAsStringAsync());
        }
        Assert.False(arena.Relay.Pending());
        var start = arena.StartAsync(setup);
        using var relay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        await relay.WaitForLineAsync($"CMD 10 {(int)announcement:X} 9 0");
        await relay.ReadDataByteAsync(); // game ID
        await relay.ReadDataByteAsync(); // duration
        Assert.Equal(0x99, await relay.ReadDataByteAsync());
        Assert.Equal(0xFF, await relay.ReadDataByteAsync()); // unlimited reloads
        Assert.Equal(0x99, await relay.ReadDataByteAsync());
    }

    [Theory]
    [InlineData("GlobalHp")]
    [InlineData("GlobalShields")]
    [InlineData("CountdownSeconds")]
    [InlineData("Hp")]
    [InlineData("Shields")]
    public async Task A_large_value_alone_selects_enhanced_hosting_for_Battle(string field)
    {
        await using var arena = new Arena();
        await arena.LoginAsync();
        var setup = new StartGameRequest
        {
            DisableIff = false, DisableHitRegister = false, GlobalStartingAmmo = 0,
            Players = [new() { Slot = 1 }, new() { Slot = 2 }],
        };
        var request = JsonSerializer.SerializeToNode(setup)!.AsObject();
        var target = field is "Hp" or "Shields" ? request["Players"]![0]!.AsObject() : request;
        target[field] = 200;
        var start = arena.StartAsync(request);
        using var relay = await arena.AcceptRelayAsync();
        (await start).EnsureSuccessStatusCode();
        await relay.WaitForLineAsync("CMD 10 81 9 0");
        await relay.ReadDataByteAsync();
        await relay.ReadDataByteAsync();
        Assert.Equal(field is "Hp" or "GlobalHp" ? 200 : 10, await relay.ReadDataByteAsync());
        await relay.ReadDataByteAsync();
        await relay.ReadDataByteAsync();
        Assert.Equal(field is "Shields" or "GlobalShields" ? 200 : 0, await relay.ReadDataByteAsync());
        Assert.Equal(field == "CountdownSeconds" ? 200 : 30, (await arena.StateAsync()).CountdownSeconds);
    }

    [Fact]
    public async Task Existing_players_file_loads_with_the_row_fields_used_by_the_operator_console()
    {
        await using var arena = new Arena();
        const string legacy = """
            {"PlayerCount":2,"Rows":[
                {"Name":"Rowan","TeamPreference":"Team 2","Hp":"12","Shields":"0","Reloads":"3",
                 "AutoReload":true,"FullAuto":false,"Ammo":"18","SleepTimeout":"4"},
                {"Name":"Finley","FullAuto":true}]}
            """;
        await File.WriteAllTextAsync(Path.Combine(arena.DataDirectory, "players.json"), legacy);
        await arena.LoginAsync();
        var saved = await arena.SavedPlayersAsync();
        Assert.Equal(2, saved["playerCount"]!.GetValue<int>());
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(legacy)!["Rows"]![0], saved["rows"]![0]));
        Assert.Equal("Finley", saved["rows"]![1]!["Name"]!.GetValue<string>());
        Assert.Equal("", saved["rows"]![1]!["Hp"]!.GetValue<string>());
        Assert.True(saved["rows"]![1]!["FullAuto"]!.GetValue<bool>());
    }

    private sealed class WithholdFirstEndReply : DelegatingHandler
    {
        public TaskCompletionSource ServerResponded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            var response = await base.SendAsync(request, token);
            if (request.RequestUri!.AbsolutePath.EndsWith("/endgame", StringComparison.Ordinal) && ServerResponded.TrySetResult())
            {
                response.Dispose();
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
            }
            return response;
        }
    }

    private sealed class Arena : IAsyncDisposable
    {
        private readonly CancellationTokenSource _deadline = new(TimeSpan.FromSeconds(40));
        private readonly DirectoryInfo _directory = Directory.CreateTempSubdirectory("ltar-relay-test-");
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HttpClient _client;
        public TcpListener Relay { get; } = new(IPAddress.Loopback, 0);
        public string DataDirectory => _directory.FullName;
        private string UpdateLockPath => Path.Combine(DataDirectory, "update.lock");
        public FileStream OpenUpdateLock() => new(UpdateLockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        public Arena(bool automatic = false, TimeProvider? clock = null)
        {
            File.Create(UpdateLockPath).Dispose();
            Relay.Start();
            _factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Development");
                if (clock != null) builder.ConfigureServices(services => services.AddSingleton(clock));
                builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["Arena:OperatorPin"] = "73918426",
                        ["Arena:OperatorSessionHours"] = "0.25",
                        ["Arena:RelayAddress"] = Relay.LocalEndpoint.ToString(),
                        ["Arena:DisableRelayReconnect"] = (!automatic).ToString(),
                        ["Arena:UpdateLockFile"] = UpdateLockPath,
                        ["Arena:DataDirectory"] = _directory.FullName,
                    }));
            });
            _client = _factory.CreateClient();
        }

        public HttpClient CreateConsole(params DelegatingHandler[] handlers) => _factory.CreateDefaultClient(handlers);

        public Task<HubDisplay> OpenDisplayAsync(string? token = null, bool bearerHeader = false)
            => HubDisplay.ConnectAsync(_factory.Server, token, bearerHeader, _deadline.Token);

        public async Task<string> LoginAsync(HttpClient? console = null)
        {
            var client = console ?? _client;
            using var response = await client.PostAsJsonAsync("/api/operator/login", new { pin = "73918426" }, _deadline.Token);
            response.EnsureSuccessStatusCode();
            var login = await response.Content.ReadFromJsonAsync<JsonElement>();
            string token = login.GetProperty("token").GetString()!;
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
            return token;
        }

        public Task<HttpResponseMessage> StartAsync<T>(T setup) =>
            _client.PostAsJsonAsync("/api/game/start", setup, _deadline.Token);
        public Task<HttpResponseMessage> PostAsync(string action, HttpClient? console = null) =>
            (console ?? _client).PostAsync("/api/game/" + action, null, _deadline.Token);
        public async Task<GameStateSnapshot> StateAsync() =>
            (await _client.GetFromJsonAsync<GameStateSnapshot>("/api/game/state", _deadline.Token))!;
        public async Task<JsonObject> SavedPlayersAsync() =>
            (await _client.GetFromJsonAsync<JsonObject>("/api/game/savedplayers", _deadline.Token))!;
        public async Task<TelemetrySnapshot> TelemetryAsync() =>
            (await _client.GetFromJsonAsync<TelemetrySnapshot>("/api/game/telemetry", _deadline.Token))!;
        public async Task<bool> RelayConnectedAsync() => (await TelemetryAsync()).RelayConnected;

        public async Task<CompletedGameRecord> CompletedGameAsync()
        {
            CompletedGameSummary[]? recent = null;
            await WaitUntilAsync(async () =>
            {
                recent = await _client.GetFromJsonAsync<CompletedGameSummary[]>("/api/history", _deadline.Token);
                return recent?.Length > 0;
            });
            string id = Assert.Single(recent!).GameId;
            return (await _client.GetFromJsonAsync<CompletedGameRecord>($"/api/history/{id}", _deadline.Token))!;
        }

        public async Task WaitUntilAsync(Func<Task<bool>> condition)
        {
            while (!await condition()) await Task.Delay(50, _deadline.Token);
        }

        public async Task<RelayPeer> AcceptRelayAsync(TcpListener? listener = null, string? reply = "READY 1")
        {
            var peer = new RelayPeer(await (listener ?? Relay).AcceptTcpClientAsync(_deadline.Token), _deadline.Token);
            await peer.WaitForLineAsync("HELLO 1");
            if (reply != null) await peer.SendLineAsync(reply);
            return peer;
        }

        public async Task<byte> JoinTwoTaggersAndPlayAsync(RelayPeer relay)
        {
            await relay.WaitForLineAsync("CMD 10 81 9 0");
            byte game = await relay.ReadDataByteAsync();
            foreach (byte id in new byte[] { 1, 2 })
            {
                await relay.SendPacketAsync(PacketType.RequestJoinGameEnhanced, game, id);
                await relay.SendPacketAsync(PacketType.AcknowledgePlayerAssignmentEnhanced, game, id);
            }
            await WaitUntilAsync(async () => (await StateAsync()).Players.Count(p => p.Confirmed) == 2);
            (await PostAsync("countdown")).EnsureSuccessStatusCode();
            await WaitUntilAsync(async () => (await StateAsync()).HostingState == "Playing");
            return game;
        }

        public async ValueTask DisposeAsync()
        {
            _client.Dispose();
            await _factory.DisposeAsync();
            Relay.Stop();
            _deadline.Dispose();
            PlayerSettings.ConfigureDataDirectory(AppContext.BaseDirectory);
            _directory.Delete(recursive: true);
        }
    }

    private sealed class RelayPeer : IDisposable
    {
        private readonly TcpClient client;
        private readonly CancellationToken deadline;
        private readonly Channel<string> lines = Channel.CreateUnbounded<string>();
        private readonly SemaphoreSlim sendGate = new(1);
        private readonly Task readLoop;
        private int releaseAttempts, rejectNextRelease, disconnectOnRelease;
        public int ReleaseAttempts => Volatile.Read(ref releaseAttempts);
        public void RejectNextRelease() => Interlocked.Exchange(ref rejectNextRelease, 1);
        public void DisconnectOnNextRelease() => Interlocked.Exchange(ref disconnectOnRelease, 1);

        public RelayPeer(TcpClient client, CancellationToken deadline)
        {
            this.client = client;
            this.deadline = deadline;
            readLoop = ReadCommandsAsync();
        }

        private async Task ReadCommandsAsync()
        {
            try
            {
                using var reader = new StreamReader(client.GetStream(), Encoding.ASCII, leaveOpen: true);
                while (await reader.ReadLineAsync(deadline) is { } line)
                {
                    if (line.StartsWith("CMD ", StringComparison.Ordinal))
                    {
                        bool rejected = false;
                        if (line == $"CMD 10 {(int)PacketType.AssignPlayerSuccessEnhanced:X} 9 0")
                        {
                            Interlocked.Increment(ref releaseAttempts);
                            if (Interlocked.Exchange(ref disconnectOnRelease, 0) != 0)
                            {
                                CloseConnection();
                                break;
                            }
                            rejected = Interlocked.Exchange(ref rejectNextRelease, 0) != 0;
                        }
                        await SendLineAsync(rejected ? "ERROR TX_FAILED" : "OK");
                    }
                    await lines.Writer.WriteAsync(line, deadline);
                }
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException) { }
            finally { lines.Writer.TryComplete(); }
        }

        public async Task WaitForLineAsync(string expected)
        {
            await foreach (string line in lines.Reader.ReadAllAsync(deadline))
                if (line == expected) return;
            throw new EndOfStreamException($"Relay disconnected while waiting for {expected}.");
        }

        public async Task<byte> ReadDataByteAsync()
        {
            var line = await lines.Reader.ReadAsync(deadline);
            Assert.NotNull(line);
            var fields = line!.Split(' ');
            Assert.Equal(new[] { "CMD", "10", "8", "0" }, new[] { fields[0], fields[1], fields[3], fields[4] });
            return Convert.ToByte(fields[2], 16);
        }

        public async Task<(byte Game, int Hp, int Ammo)> ReadEnhancedAdvertisementAsync()
        {
            await WaitForLineAsync("CMD 10 81 9 0");
            byte[] contents = new byte[12];
            for (int i = 0; i < contents.Length; i++) contents[i] = await ReadDataByteAsync();
            return (contents[0], contents[2], contents[10]);
        }

        public async Task SendLineAsync(string line)
        {
            await sendGate.WaitAsync(deadline);
            try { await client.GetStream().WriteAsync(Encoding.ASCII.GetBytes(line + "\r\n"), deadline); }
            finally { sendGate.Release(); }
        }

        public async Task SendPacketAsync(PacketType type, params byte[] payload)
        {
            var packet = new Packet(type);
            await SendLineAsync($"RCV {(int)type:X} 9 0");
            foreach (byte value in payload)
            {
                packet.DataAdd(value);
                await SendLineAsync($"RCV {value:X} 8 0");
            }
            await SendLineAsync($"RCV {0x100 | packet.PopulateChecksum().Data:X} 9 0");
        }

        public void CloseConnection() => client.Client.Shutdown(SocketShutdown.Both);
        public void FinishSending() => client.Client.Shutdown(SocketShutdown.Send);
        public void Dispose()
        {
            client.Dispose();
            readLoop.GetAwaiter().GetResult();
            sendGate.Dispose();
        }
    }
}
