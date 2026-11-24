using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using LazerTagHostLibrary;
using LazerTagHostLibrary.StateMachine;
using Xunit;

namespace LazerTagWebServer.Tests;

public sealed class ReliabilityRegressionTests
{
    [Fact]
    public async Task Disconnect_cancels_a_relay_connect_attempt_in_progress()
    {
        // A relay that accepts the socket but never answers HELLO keeps Connect retrying
        // (five 2-second handshakes plus delays). Disconnect must not wait all that out.
        using var silentRelay = new TcpListener(IPAddress.Loopback, 0);
        silentRelay.Start();
        int port = ((IPEndPoint)silentRelay.LocalEndpoint).Port;
        var accepted = new List<TcpClient>();
        try
        {
            var serial = new LazerTagSerial();
            var connecting = Task.Run(() => serial.Connect($"127.0.0.1:{port}"));
            accepted.Add(await silentRelay.AcceptTcpClientAsync().WaitAsync(TimeSpan.FromSeconds(10)));

            var timer = Stopwatch.StartNew();
            serial.Disconnect();
            timer.Stop();
            Assert.False(await connecting.WaitAsync(TimeSpan.FromSeconds(2)));
            Assert.True(timer.ElapsedMilliseconds < 1500, $"Disconnect took {timer.ElapsedMilliseconds} ms.");
            Assert.False(serial.IsConnected);
            serial.Dispose();
        }
        finally
        {
            foreach (var client in accepted) client.Dispose();
            silentRelay.Stop();
        }
    }

    [Fact]
    public void Countdown_play_and_reconnect_snapshots_follow_elapsed_time()
    {
        TimeSpan elapsed = TimeSpan.Zero;
        var clock = new MatchClock(new DateTime(2000, 1, 1), () => elapsed);
        using var fixture = new HostFixture(GameType.CustomLazerTagTwoTeams, [1, 2], clock: clock);
        var host = fixture.Host;
        byte game = host.GameDefinition.GameId;
        foreach (byte player in new byte[] { 1, 2 })
        {
            Assert.True(host.ProcessPacket(PacketOf(PacketType.RequestJoinGameEnhanced, game, player)));
            Assert.True(host.ProcessPacket(PacketOf(PacketType.AcknowledgePlayerAssignmentEnhanced, game, player)));
        }
        Assert.True(host.StartCountdown());
        elapsed += TimeSpan.FromSeconds(12);
        host.Update();
        Assert.Equal(18000, host.PhaseRemainingMilliseconds);
        elapsed += TimeSpan.FromSeconds(18);
        host.Update();
        Assert.Equal(HostGun.HostingStates.Playing, host.HostingState);
        elapsed += TimeSpan.FromMinutes(4);
        Assert.Equal(360000, host.PhaseRemainingMilliseconds);
        elapsed += TimeSpan.FromMinutes(6);
        host.Update();
        Assert.Equal(HostGun.HostingStates.Summary, host.HostingState);
    }

    [Fact]
    public async Task Relay_replies_match_their_packet_and_disconnection_rejects_remaining_writes()
    {
        var queue = new RelayOutbox();
        var first = new TaskCompletionSource<bool>();
        var second = new TaskCompletionSource<bool>();
        var third = new TaskCompletionSource<bool>();
        foreach (var receipt in new[] { first, second, third })
        {
            Assert.True(queue.TryQueue([1], 3, 2000, receipt));
            Assert.True(queue.TryTake(out var write, default));
            Assert.True(queue.BeginWrite(write, true));
        }
        queue.Reply(true);
        queue.Reply(false);
        Assert.False(first.Task.IsCompleted);
        queue.Reply(true);
        Assert.False(await first.Task);
        for (int i = 0; i < 3; i++) queue.Reply(true);
        Assert.True(await second.Task);
        queue.Close();
        Assert.False(await third.Task);
        Assert.False(queue.TryQueue([1], 1, 1000));
    }

    [Fact]
    public async Task Relay_queue_bounds_backlog_and_discards_expired_commands()
    {
        var queue = new RelayOutbox();
        var stale = new TaskCompletionSource<bool>();
        Assert.True(queue.TryQueue([1], 1, 0, stale));
        for (int i = 1; i < 64; i++) Assert.True(queue.TryQueue([1], 1, 2000));
        Assert.False(queue.TryQueue([1], 1, 2000));
        Assert.True(queue.TryTake(out var expired, default));
        Assert.False(queue.BeginWrite(expired, true));
        Assert.False(await stale.Task);
        queue.Close();
    }

    [Fact]
    public void Each_assignment_retry_gets_a_full_timeout_interval()
    {
        var machine = new HostStateMachine();
        Assert.True(machine.Handle(new HostStateMachineEvent(HostEventType.StartHosting), out _));
        Assert.True(machine.Handle(new HostStateMachineEvent(HostEventType.PlayerAssignmentSent), out _));
        var policy = machine.PolicyFor(HostSessionState.AwaitingAssignmentAck);
        var interval = policy.Timeout!.Value;
        var deadline = DateTime.UtcNow.Add(interval);
        for (int retry = 0; retry < policy.MaxRetries; retry++)
        {
            Assert.True(machine.OnTick(deadline, out _));
            Assert.Equal(HostSessionState.AwaitingAssignmentAck, machine.CurrentState);
            Assert.False(machine.OnTick(deadline.Add(interval).AddTicks(-1), out _));
            deadline = deadline.Add(interval);
        }
        Assert.True(machine.OnTick(deadline, out _));
        Assert.Equal(HostSessionState.Recovery, machine.CurrentState);
    }

    [Fact]
    public void Repeated_join_from_confirmed_tagger_reuses_existing_roster_slot()
    {
        using var fixture = new HostFixture(GameType.CustomLazerTagTwoTeams, [1]);
        var host = fixture.Host;
        byte gameId = host.GameDefinition.GameId;
        const byte taggerId = 0x31;

        Assert.True(host.ProcessPacket(PacketOf(PacketType.RequestJoinGameEnhanced, gameId, taggerId)));
        Assert.True(host.ProcessPacket(PacketOf(PacketType.AcknowledgePlayerAssignmentEnhanced, gameId, taggerId)));

        var original = Assert.Single(host.Players);
        Assert.True(original.Confirmed);

        Assert.True(host.ProcessPacket(PacketOf(PacketType.RequestJoinGameEnhanced, gameId, taggerId)));
        Assert.True(host.ProcessPacket(PacketOf(PacketType.AcknowledgePlayerAssignmentEnhanced, gameId, taggerId)));

        var afterRepeat = Assert.Single(host.Players);
        Assert.Same(original, afterRepeat);
        Assert.True(afterRepeat.Confirmed);
        Assert.Single(host.Teams.Team(1)!.Players);
    }

    [Fact]
    public void Dropping_lobby_player_removes_player_from_team_roster_too()
    {
        using var fixture = new HostFixture(GameType.CustomLazerTagTwoTeams, [1]);
        var host = fixture.Host;
        byte gameId = host.GameDefinition.GameId;
        const byte taggerId = 0x32;

        Assert.True(host.ProcessPacket(PacketOf(PacketType.RequestJoinGameEnhanced, gameId, taggerId)));
        Assert.True(host.ProcessPacket(PacketOf(PacketType.AcknowledgePlayerAssignmentEnhanced, gameId, taggerId)));

        var player = Assert.Single(host.Players);
        Assert.Single(host.Teams.Team(1)!.Players);

        host.WithStateLock(() => host.DropPlayer(player.TeamPlayerId));

        Assert.Empty(host.Players);
        Assert.Empty(host.Teams.Team(1)!.Players);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Full_planned_lobby_rejects_only_new_taggers_and_preserves_countdown(bool enhanced)
    {
        using var fixture = new HostFixture(GameType.CustomLazerTagTwoTeams, [1, 2], enhanced, 2);
        var host = fixture.Host;
        var sent = new List<Packet>();
        host.Protocol.PacketSending += (_, args) => sent.Add(args.Packet);
        byte gameId = host.GameDefinition.GameId;
        bool Join(byte id) => host.ProcessPacket(enhanced
            ? PacketOf(PacketType.RequestJoinGameEnhanced, gameId, id)
            : PacketOf(PacketType.RequestJoinGame, gameId, id, 0));
        bool Confirm(byte id) => host.ProcessPacket(PacketOf(enhanced
            ? PacketType.AcknowledgePlayerAssignmentEnhanced : PacketType.AcknowledgePlayerAssignment, gameId, id));

        Assert.True(Join(1));
        Assert.True(Confirm(1));
        Assert.True(Join(2));
        sent.Clear();
        Assert.False(Join(3)); // The last planned slot is reserved even before it acknowledges.
        var refusal = Assert.Single(sent);
        Assert.Equal(enhanced ? PacketType.AssignPlayerFailedEnhanced : PacketType.AssignPlayerFailed, refusal.Type);
        Assert.Equal(new ushort[] { gameId, 3 }, refusal.Data.Select(value => value.Data));
        Assert.True(refusal.ChecksumValid);
        Assert.False(Confirm(3));
        Assert.True(Confirm(2));
        Assert.True(Join(1));
        Assert.True(Confirm(1));
        Assert.Equal(2, host.Players.Count);
        Assert.All(host.Teams, team => Assert.Single(team.Players));

        host.DropPlayer(host.Players.Last().TeamPlayerId);
        Assert.True(Join(3));
        Assert.True(Confirm(3));
        Assert.True(host.StartCountdown());
        Assert.Equal(HostGun.HostingStates.Countdown, host.HostingState);
    }

    [Fact]
    public async Task Handshake_delivers_coalesced_lines_immediately_and_retains_partial_line()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(6));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        using var serial = new LazerTagSerial();
        var received = System.Threading.Channels.Channel.CreateUnbounded<string>();
        serial.DataReceived += (_, args) => received.Writer.TryWrite(args.Data);
        var connecting = Task.Run(() => serial.Connect(listener.LocalEndpoint.ToString()!));
        using var client = await listener.AcceptTcpClientAsync(timeout.Token);
        var stream = client.GetStream();
        using var reader = new StreamReader(stream, Encoding.ASCII, leaveOpen: true);
        Assert.Equal("HELLO 1", await reader.ReadLineAsync(timeout.Token));
        await stream.WriteAsync("PONG\r\nRE"u8.ToArray(), timeout.Token);
        await stream.WriteAsync("ADY 1\r\nRCV 81 9 0\r\nRCV 22 8 0\r\nRCV 3"u8.ToArray(), timeout.Token);
        Assert.True(await connecting);
        Assert.Equal("RCV 81 9 0", await received.Reader.ReadAsync(timeout.Token));
        Assert.Equal("RCV 22 8 0", await received.Reader.ReadAsync(timeout.Token));
        await stream.WriteAsync("3 8 0\r\nPONG\r\nRCV 144 9 0\r\n"u8.ToArray(), timeout.Token);
        Assert.Equal("RCV 33 8 0", await received.Reader.ReadAsync(timeout.Token));
        Assert.Equal("RCV 144 9 0", await received.Reader.ReadAsync(timeout.Token));
        Assert.False(received.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Remote_tcp_close_raises_io_error_and_clears_connection_state()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;

        var relayTask = Task.Run(async () =>
        {
            using TcpClient client = await listener.AcceptTcpClientAsync();
            using NetworkStream stream = client.GetStream();
            var buffer = new byte[128];
            int used = 0;
            while (used < buffer.Length)
            {
                int n = await stream.ReadAsync(buffer.AsMemory(used));
                if (n <= 0) break;
                used += n;
                if (Encoding.ASCII.GetString(buffer, 0, used).Contains('\n')) break;
            }

            byte[] ready = Encoding.ASCII.GetBytes("READY 1\r\n");
            await stream.WriteAsync(ready);
            await stream.FlushAsync();
            await Task.Delay(100);
            // Dispose closes the socket from the relay side.
        });

        using var serial = new LazerTagSerial();
        var error = new TaskCompletionSource<LazerTagSerial.IoErrorEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        serial.IoError += (_, args) => error.TrySetResult(args);

        Assert.True(serial.Connect($"127.0.0.1:{port}"));
        Assert.True(serial.IsConnected);

        await error.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(SpinWait.SpinUntil(() => !serial.IsConnected, TimeSpan.FromSeconds(2)));
        await relayTask;
    }

    [Fact]
    public void Send_while_disconnected_returns_quickly_instead_of_retrying_inline()
    {
        using var serial = new LazerTagSerial();
        var portField = typeof(LazerTagSerial).GetField("_portName", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("_portName field not found");
        portField.SetValue(serial, "127.0.0.1:1");

        var stopwatch = Stopwatch.StartNew();
        serial.Enqueue([0x01, 0x02, 0x03]);
        stopwatch.Stop();

        Assert.True(stopwatch.Elapsed < TimeSpan.FromMilliseconds(250),
            $"Enqueue blocked for {stopwatch.Elapsed.TotalMilliseconds:F0} ms");
    }

    private static Packet PacketOf(PacketType type, params ushort[] data)
    {
        var packet = new Packet(type);
        foreach (ushort value in data)
            packet.Data.Add(new Signature(SignatureType.Data, value));
        packet.PopulateChecksum();
        return packet;
    }

    private sealed class HostFixture : IDisposable
    {
        public HostGun Host { get; }

        public HostFixture(GameType gameType, int?[] teamPreferences, bool enhanced = true, int? plannedCount = null, MatchClock? clock = null)
        {
            Host = new HostGun(string.Empty, null, clock ?? MatchClock.Start());
            Host.Protocol.PacketSending += (_, args) => args.Cancel = true;
            Host.SetPlannedTeamPreferences(teamPreferences);
            Host.StartServer(new GameDefinition
            {
                GameType = gameType,
                PlannedPlayerCount = plannedCount,
                GameId = new RandomId(0x5A),
                GameTimeMinutes = 10,
                Tags = 10,
                Reloads = 0xFF,
                Mega = 0,
                CountdownTimeSeconds = 30,
                UseLtarEnhancedProtocol = enhanced,
            });
        }

        public void Dispose() => Host.Protocol.Dispose();
    }
}
