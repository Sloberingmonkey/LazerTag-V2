using System.Reflection;
using LazerTagHostLibrary;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed class DebriefRegressionTests
{
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
            // canonical BCD, but the original host accepted it and the effective
            // scoring path caps the decoded raw frames at the target's 10 HP.
            Assert.True(host.ProcessPacket(TagSummary(game.GameId, new TeamPlayerId(2, 1), 0x02)));
            Assert.True(host.ProcessPacket(TeamReport(PacketType.TeamOneTagReport,
                game.GameId, new TeamPlayerId(2, 1), 0x01, 0x8A)));

            var secondPlayer = host.Players.Player(new TeamPlayerId(2, 1));
            var firstPlayer = host.Players.Player(new TeamPlayerId(1, 1));
            Assert.NotNull(secondPlayer);
            Assert.NotNull(firstPlayer);
            Assert.True(secondPlayer!.AllTagReportsReceived());
            Assert.Equal(10, host.CountEffectiveHitsScored(firstPlayer!));

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

    private static void JoinEnhanced(HostGun host, byte taggerId)
    {
        byte gameId = host.GameDefinition.GameId;
        Assert.True(host.ProcessPacket(PacketOf(PacketType.RequestJoinGameEnhanced, gameId, taggerId)));
        Assert.True(host.ProcessPacket(PacketOf(PacketType.AcknowledgePlayerAssignmentEnhanced, gameId, taggerId)));
    }


    private static Packet TagSummary(RandomId gameId, TeamPlayerId player, ushort reportFlags) =>
        PacketOf(PacketType.TagSummary, gameId, player.Packed23, 0, 0, 0, 0, 0, reportFlags);

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
