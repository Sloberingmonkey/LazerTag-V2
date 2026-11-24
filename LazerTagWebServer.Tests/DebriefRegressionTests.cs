using System.Reflection;
using LazerTagHostLibrary;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed class DebriefRegressionTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void Only_explicit_finish_closes_debrief_and_only_complete_reports_transmit_ranks(bool useNext, bool complete)
    {
        var host = new HostGun(string.Empty);
        using var protocol = host.Protocol;
        var sent = new List<Packet>();
        protocol.PacketSending += (_, args) => { sent.Add(args.Packet); args.Cancel = true; };
        var game = new GameDefinition
        {
            GameType = GameType.CustomLazerTagTwoTeams, GameId = new RandomId(0x37),
            GameTimeMinutes = 1, Tags = 10, CountdownTimeSeconds = 0, UseLtarEnhancedProtocol = true,
        };
        host.SetPlannedTeamPreferences([1, 2]);
        host.StartServer(game);
        Assert.False(host.FinishDebrief());
        JoinEnhanced(host, 1);
        JoinEnhanced(host, 2);
        Assert.True(host.StartCountdown());
        host.Update();
        host.EndGame();
        var last = TagSummary(game.GameId, host.Players.Last().TeamPlayerId, 0);
        Assert.True(host.ProcessPacket(TagSummary(game.GameId, host.Players.First().TeamPlayerId, 0)));
        if (complete) Assert.True(host.ProcessPacket(last));
        if (useNext) host.Next(); else host.EndGame();
        host.EndGame();
        Assert.Equal(HostGun.HostingStates.Summary, host.HostingState);
        Assert.All(host.Players, player => Assert.False(player.Dropped));
        Assert.True(host.FinishDebrief());
        Assert.False(host.FinishDebrief());
        Assert.Equal(HostGun.HostingStates.GameOver, host.HostingState);
        // A forced finish scores from the reports that arrived and is marked partial.
        Assert.Equal(!complete, host.PartialScores);
        Assert.Equal(!complete, host.DebriefForced);
        if (!complete) Assert.False(host.ProcessPacket(last));

        sent.Clear();
        for (int i = 0; i < 2; i++) { MakeDebriefRequestDue(host); host.Update(); }
        Assert.Equal(!complete, host.Players.Last().Dropped);
        // Complete and forced finishes both rank the players and send the ranks to the taggers.
        Assert.All(host.Players, p => Assert.Equal(1, p.Rank)); // tied at zero
        Assert.Equal(2, sent.Count);
        Assert.All(sent, packet =>
        {
            Assert.Equal(PacketType.RankReport, packet.Type);
            Assert.Equal(1, packet.Data[2].Data);
        });
    }

    [Fact]
    public void Two_player_debrief_requests_each_tagger_without_a_pre_request_release()
    {
        var host = new HostGun(string.Empty);
        var sent = new List<Packet>();
        host.Protocol.PacketSending += (_, args) =>
        {
            sent.Add(new Packet(args.Packet));
            args.Cancel = true;
        };

        try
        {
            var game = new GameDefinition
            {
                GameType = GameType.CustomLazerTagTwoTeams,
                GameId = new RandomId(0x5A),
                GameTimeMinutes = 1,
                Tags = 10,
                Reloads = 0xFF,
                Mega = 0,
                CountdownTimeSeconds = 0,
                UseLtarEnhancedProtocol = true,
            };

            host.SetPlannedTeamPreferences(new int?[] { 1, 2 });
            host.StartServer(game);
            JoinEnhanced(host, 0x21);
            JoinEnhanced(host, 0x22);

            Assert.True(host.StartCountdown());
            host.Update();
            Assert.Equal(HostGun.HostingStates.Playing, host.HostingState);

            sent.Clear();
            host.EndGame();
            Assert.Equal(HostGun.HostingStates.Summary, host.HostingState);
            Assert.Empty(sent); // Summary entry must not broadcast assignment-release packets.

            host.Update();
            Packet firstRequest = Assert.Single(sent);
            Assert.Equal(PacketType.RequestTagReport, firstRequest.Type);
            Assert.Equal(new TeamPlayerId(1, 1).Packed23, firstRequest.Data[1].Data);
            Assert.Equal(0x07, firstRequest.Data[2].Data);

            // Player 1 returns a summary, a valid empty team report, then a populated
            // report. The BCD value 0x10 represents ten hits.
            Assert.True(host.ProcessPacket(TagSummary(game.GameId, new TeamPlayerId(1, 1), 0x06)));
            Assert.True(host.ProcessPacket(TeamReport(PacketType.TeamOneTagReport,
                game.GameId, new TeamPlayerId(1, 1), 0x00)));
            Assert.True(host.ProcessPacket(TeamReport(PacketType.TeamTwoTagReport,
                game.GameId, new TeamPlayerId(1, 1), 0x01, 0x10)));
            Assert.True(host.Players.Player(new TeamPlayerId(1, 1))!.AllTagReportsReceived());
            Assert.False(host.Players.Player(new TeamPlayerId(2, 1))!.AllTagReportsReceived());

            host.EndGame();
            sent.Clear();
            MakeDebriefRequestDue(host);
            host.Update();
            Packet secondRequest = Assert.Single(sent);
            Assert.Equal(PacketType.RequestTagReport, secondRequest.Type);
            Assert.Equal(new TeamPlayerId(2, 1).Packed23, secondRequest.Data[1].Data);

            Assert.True(host.ProcessPacket(TagSummary(game.GameId, new TeamPlayerId(2, 1), 0x00)));
            Assert.True(host.Players.Player(new TeamPlayerId(2, 1))!.AllTagReportsReceived());

            sent.Clear();
            MakeDebriefRequestDue(host);
            host.Update();
            Assert.Equal(HostGun.HostingStates.GameOver, host.HostingState);
            Assert.Empty(sent);
        }
        finally
        {
            host.Protocol.Dispose();
        }
    }


    [Fact]
    public void Noncanonical_hardware_tag_count_does_not_stall_second_player_debrief()
    {
        var host = new HostGun(string.Empty);
        var sent = new List<Packet>();
        host.Protocol.PacketSending += (_, args) =>
        {
            sent.Add(new Packet(args.Packet));
            args.Cancel = true;
        };

        try
        {
            var game = new GameDefinition
            {
                GameType = GameType.CustomLazerTagTwoTeams,
                GameId = new RandomId(0x37),
                GameTimeMinutes = 1,
                Tags = 10,
                Reloads = 0xFF,
                Mega = 0,
                CountdownTimeSeconds = 0,
                UseLtarEnhancedProtocol = true,
            };

            host.SetPlannedTeamPreferences(new int?[] { 1, 2 });
            host.StartServer(game);
            JoinEnhanced(host, 0x5D);
            JoinEnhanced(host, 0x70);

            Assert.True(host.StartCountdown());
            host.Update();
            host.EndGame();

            // Complete player one first.
            host.Update();
            Assert.True(host.ProcessPacket(TagSummary(game.GameId, new TeamPlayerId(1, 1), 0x00)));

            sent.Clear();
            MakeDebriefRequestDue(host);
            host.Update();
            Assert.Single(sent);

            // Field hardware returned 0x8A in the detailed report. It is not
            // canonical BCD (it decodes to 90), but the original host accepted it.
            // Scoring trusts it only up to the 3 hits the target says it took.
            Assert.True(host.ProcessPacket(TagSummary(game.GameId, new TeamPlayerId(2, 1), 0x02, tagsTaken: 0x03)));
            Assert.True(host.ProcessPacket(TeamReport(PacketType.TeamOneTagReport,
                game.GameId, new TeamPlayerId(2, 1), 0x01, 0x8A)));

            var secondPlayer = host.Players.Player(new TeamPlayerId(2, 1));
            var firstPlayer = host.Players.Player(new TeamPlayerId(1, 1));
            Assert.NotNull(secondPlayer);
            Assert.NotNull(firstPlayer);
            Assert.True(secondPlayer!.AllTagReportsReceived());
            Assert.Equal(3, host.CountEffectiveHitsScored(firstPlayer!));

            sent.Clear();
            MakeDebriefRequestDue(host);
            host.Update();
            Assert.Equal(HostGun.HostingStates.GameOver, host.HostingState);
        }
        finally
        {
            host.Protocol.Dispose();
        }
    }

    [Theory]
    [InlineData(GameType.Survival)]
    [InlineData(GameType.SurvivalTwoTeams)]
    [InlineData(GameType.SurvivalThreeTeams)]
    public void Survival_uses_effective_hits_and_full_minutes_for_final_ranks(GameType mode)
    {
        var host = new HostGun(string.Empty);
        using var protocol = host.Protocol;
        protocol.PacketSending += (_, args) => args.Cancel = true;
        var game = new GameDefinition
        {
            GameType = mode, GameId = new RandomId(0x37), GameTimeMinutes = 2,
            Tags = 10, CountdownTimeSeconds = 0, UseLtarEnhancedProtocol = true,
        };
        host.SetPlannedTeamPreferences([1, 2, 3]);
        host.StartServer(game);
        JoinEnhanced(host, 0x31);
        JoinEnhanced(host, 0x32);
        if (game.TeamCount == 3) JoinEnhanced(host, 0x33);
        Assert.True(host.StartCountdown());
        host.Update();
        host.EndGame();

        var shooter = host.Players.First();
        var target = host.Players.Skip(1).First();
        Assert.True(host.ProcessPacket(PacketOf(PacketType.TagSummary, game.GameId,
            shooter.TeamPlayerId.Packed23, 0, 0x02, 0x09, 0, 0, 0)));
        Assert.True(host.ProcessPacket(TagSummary(game.GameId, target.TeamPlayerId, 0x02, tagsTaken: 0x03)));
        Assert.True(host.ProcessPacket(TeamReport(PacketType.TeamOneTagReport,
            game.GameId, target.TeamPlayerId, 0x01, 0x8A)));
        if (game.TeamCount == 3)
            Assert.True(host.ProcessPacket(TagSummary(game.GameId, host.Players.Last().TeamPlayerId, 0)));

        host.Update();
        Assert.Equal(HostGun.HostingStates.GameOver, host.HostingState);
        Assert.Equal(3, host.CountEffectiveHitsScored(shooter));
        Assert.Equal(23, shooter.Score);
        Assert.Equal(1, shooter.Rank);
        if (game.IsTeamGame)
            Assert.Equal((1 << 10) + 23 * 4, shooter.Team.Score);
    }

    [Theory]
    [InlineData(GameType.CustomLazerTagTwoTeams)]
    [InlineData(GameType.OneOnOne)]
    public void Noncanonical_count_is_limited_to_the_targets_reported_hits_taken(GameType mode)
    {
        var (host, game) = StartTwoTeamDebrief(mode, [1, 2], 0x41, 0x42);
        using var protocol = host.Protocol;
        var shooter = host.Players.Player(new TeamPlayerId(1, 1))!;
        var target = host.Players.Player(new TeamPlayerId(2, 1))!;

        Assert.True(host.ProcessPacket(TagSummary(game.GameId, shooter.TeamPlayerId, 0x00)));
        Assert.True(host.ProcessPacket(TagSummary(game.GameId, target.TeamPlayerId, 0x02, tagsTaken: 0x04)));
        Assert.True(host.ProcessPacket(TeamReport(PacketType.TeamOneTagReport,
            game.GameId, target.TeamPlayerId, 0x01, 0x8A)));
        host.Update();

        Assert.Equal(HostGun.HostingStates.GameOver, host.HostingState);
        Assert.Equal(4, host.CountEffectiveHitsScored(shooter));
        Assert.Equal(4, shooter.Score);
        Assert.Equal(4, target.TagsTaken);
    }

    [Theory]
    [InlineData(0x02, 0x8A, 2, 3)]
    [InlineData(0x8A, 0x02, 3, 2)]
    [InlineData(0x8A, 0x8A, 5, 0)]
    public void Noncanonical_count_only_gets_hits_the_target_has_not_already_attributed(
        ushort firstCount, ushort secondCount, int expectedFirst, int expectedSecond)
    {
        var (host, game) = StartTwoTeamDebrief(GameType.CustomLazerTagTwoTeams, [1, 1, 2], 0x41, 0x42, 0x43);
        using var protocol = host.Protocol;
        var first = host.Players.Player(new TeamPlayerId(1, 1))!;
        var second = host.Players.Player(new TeamPlayerId(1, 2))!;
        var target = host.Players.Player(new TeamPlayerId(2, 1))!;

        Assert.True(host.ProcessPacket(TagSummary(game.GameId, first.TeamPlayerId, 0x00)));
        Assert.True(host.ProcessPacket(TagSummary(game.GameId, second.TeamPlayerId, 0x00)));
        Assert.True(host.ProcessPacket(TagSummary(game.GameId, target.TeamPlayerId, 0x02, tagsTaken: 0x05)));
        Assert.True(host.ProcessPacket(TeamReport(PacketType.TeamOneTagReport,
            game.GameId, target.TeamPlayerId, 0x03, firstCount, secondCount)));
        host.Update();

        Assert.Equal(HostGun.HostingStates.GameOver, host.HostingState);
        Assert.Equal(expectedFirst, host.CountEffectiveHitsScored(first));
        Assert.Equal(expectedSecond, host.CountEffectiveHitsScored(second));
        Assert.True(host.CountEffectiveHitsScored(first) + host.CountEffectiveHitsScored(second) <= target.TagsTaken);
    }

    [Fact]
    public void Canonical_counts_are_scored_as_reported()
    {
        var (host, game) = StartTwoTeamDebrief(GameType.CustomLazerTagTwoTeams, [1, 1, 2], 0x41, 0x42, 0x43);
        using var protocol = host.Protocol;
        var first = host.Players.Player(new TeamPlayerId(1, 1))!;
        var second = host.Players.Player(new TeamPlayerId(1, 2))!;
        var target = host.Players.Player(new TeamPlayerId(2, 1))!;

        Assert.True(host.ProcessPacket(TagSummary(game.GameId, first.TeamPlayerId, 0x00)));
        Assert.True(host.ProcessPacket(TagSummary(game.GameId, second.TeamPlayerId, 0x00)));
        Assert.True(host.ProcessPacket(TagSummary(game.GameId, target.TeamPlayerId, 0x02, tagsTaken: 0x09)));
        Assert.True(host.ProcessPacket(TeamReport(PacketType.TeamOneTagReport,
            game.GameId, target.TeamPlayerId, 0x03, 0x04, 0x05)));
        host.Update();

        Assert.Equal(4, host.CountEffectiveHitsScored(first));
        Assert.Equal(5, host.CountEffectiveHitsScored(second));
    }

    private static (HostGun Host, GameDefinition Game) StartTwoTeamDebrief(
        GameType mode, int?[] teams, params byte[] taggerIds)
    {
        var host = new HostGun(string.Empty);
        host.Protocol.PacketSending += (_, args) => args.Cancel = true;
        var game = new GameDefinition
        {
            GameType = mode, GameId = new RandomId(0x37), GameTimeMinutes = 1,
            Tags = 10, CountdownTimeSeconds = 0, UseLtarEnhancedProtocol = true,
        };
        host.SetPlannedTeamPreferences(teams);
        host.StartServer(game);
        foreach (var taggerId in taggerIds) JoinEnhanced(host, taggerId);
        Assert.True(host.StartCountdown());
        host.Update();
        host.EndGame();
        return (host, game);
    }

    private static void JoinEnhanced(HostGun host, byte taggerId)
    {
        byte gameId = host.GameDefinition.GameId;
        Assert.True(host.ProcessPacket(PacketOf(PacketType.RequestJoinGameEnhanced, gameId, taggerId)));
        Assert.True(host.ProcessPacket(PacketOf(PacketType.AcknowledgePlayerAssignmentEnhanced, gameId, taggerId)));
    }


    private static Packet TagSummary(RandomId gameId, TeamPlayerId player, ushort reportFlags,
        ushort tagsTaken = 0) =>
        PacketOf(PacketType.TagSummary, gameId, player.Packed23, tagsTaken, 0, 0, 0, 0, reportFlags);

    private static Packet TeamReport(PacketType type, RandomId gameId, TeamPlayerId player,
        params ushort[] reportData)
    {
        var data = new List<ushort> { gameId, player.Packed23 };
        data.AddRange(reportData);
        return PacketOf(type, data.ToArray());
    }

    private static Packet PacketOf(PacketType type, params ushort[] data)
    {
        var packet = new Packet(type);
        foreach (ushort value in data)
            packet.Data.Add(new Signature(SignatureType.Data, value));
        packet.PopulateChecksum();
        return packet;
    }

    private static void MakeDebriefRequestDue(HostGun host)
    {
        FieldInfo? field = typeof(HostGun).GetField("_nextAnnouncement",
            BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        field.SetValue(host, DateTime.MinValue);
    }
}
