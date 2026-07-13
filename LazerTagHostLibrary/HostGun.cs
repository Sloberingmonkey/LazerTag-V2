using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using LazerTagHostLibrary.StateMachine;


namespace LazerTagHostLibrary
{
    public class HostGun
    {
	    private class JoinState
	    {
		    public UInt16 GameId;
		    public Player Player;
		    public DateTime AssignPlayerSendTime;
		    public bool Failed;
		    public int AssignPlayerFailSendCount;
		    public DateTime LastAssignPlayerFailSendTime;
	    };

        public enum HostingStates
		{
            Idle,
            Adding,
			AcknowledgePlayerAssignment,
            Countdown,
			ResendCountdown,
			Playing,
            Summary,
            GameOver,
        };

		public enum ZoneType
		{
			// Invalid = 0x0,		// 00
			// Reserved = 0x1,		// 01
			ContestedZone = 0x2,	// 10
			TeamZone = 0x3			// 11
		}

		private readonly TeamCollection _teams = new TeamCollection();
		public TeamCollection Teams
	    {
			get { return _teams; }
	    }

		private readonly PlayerCollection _players = new PlayerCollection();
		public PlayerCollection Players
	    {
		    get { return _players; }
	    }

	    public int TeamCount
	    {
			get { return _gameDefinition.TeamCount; }
	    }

		public LazerTagProtocol Protocol { get; set; }

        private const int GameAnnouncementFrequencyMilliseconds = 1500;
		private const int AcknowledgePlayerAssignmentTimeoutSeconds = 2;
		private const int AssignPlayerFailedSendCount = 6;
		private const int AssignPlayerFailedFrequencyMilliseconds = 500;
        private const int MinimumPlayerCount = 2;
        // The Wi-Fi relay queues a complete IR request before the tagger can return a
        // summary plus as many as three detailed reports. Six seconds prevents the
        // next player's request from overlapping a worst-case response on the air.
        private const int RequestTagReportFrequencySeconds = 6;
        private const int GameOverAnnouncementFrequencySeconds = 3;
	    private const int ZoneBeaconFrequencyMilliseconds = 500;
        
        private GameDefinition _gameDefinition;
	    public GameDefinition GameDefinition
	    {
		    get { return _gameDefinition; }
	    }

	    public HostingStates HostingState { get; private set; }


		private readonly HostStateMachine _explicitStateMachine = new HostStateMachine();
		private HostSessionState _lastExplicitPacketCheckState = HostSessionState.Idle;

        private readonly Dictionary<UInt16, JoinState> _joinStates = new Dictionary<ushort, JoinState>();

        private DateTime _stateChangeTimeout;
        private DateTime _nextAnnouncement;
	    private int _debriefPlayerSequence;
		private int _rankReportTeamNumber;
		private Player _currentDebriefPlayer;

	    private DateTime _resendCountdownPlayingStateChangeTimeout;

		// Per-slot HP overrides set before hosting starts (index = join order, 0-based).
		// The announcement broadcast during Adding state uses this to send each
		// upcoming player's HP before they join, so their tagger picks it up.
		private readonly List<int?> _plannedTeamPreferences = new List<int?>();

		// Per-slot HP overrides set before hosting starts (index = join order, 0-based).
		// The announcement broadcast during Adding state uses this to send each
		// upcoming player's HP before they join, so their tagger picks it up.
		private readonly List<int?> _plannedTagsOverrides = new List<int?>();

		// Per-slot shields overrides (seconds, 0xFF = unlimited).
		private readonly List<int?> _plannedShieldsOverrides = new List<int?>();

		// Per-slot reloads overrides (number of reload clips, 0xFF = unlimited).
		private readonly List<int?> _plannedReloadsOverrides = new List<int?>();

		// Per-slot automatic reload overrides.
		private readonly List<bool?> _plannedAutoReloadOverrides = new List<bool?>();

		// Per-slot full-auto overrides.
		private readonly List<bool?> _plannedFullAutoOverrides = new List<bool?>();

		// Per-slot starting ammo overrides (rounds per mag, 0 = unlimited).
		private readonly List<int?> _plannedStartingAmmoOverrides = new List<int?>();

		// Per-slot sleep timeout overrides (minutes, 0 = disabled).
		private readonly List<int?> _plannedSleepTimeoutOverrides = new List<int?>();

		public HostGun(string portName)
		{
			HostingState = HostingStates.Idle;
			Protocol = new LazerTagProtocol(this, portName);
			ForceExplicitState(HostSessionState.Idle, "constructor");
		}

		public HostSessionState ExplicitHostingState
		{
			get { return _explicitStateMachine.CurrentState; }
		}

		public void SetGameStartCountdownTime(int countdownTimeSeconds)
		{
            _gameDefinition.CountdownTimeSeconds = countdownTimeSeconds;
        }

	    private bool AssignTeamAndPlayer(int requestedTeam, int preferredTeam, Player newPlayer)
	    {
		    int assignedTeamNumber = 0;
		    var assignedPlayerNumber = 0;

		    if (_players.Count >= TeamPlayerId.MaximumPlayerNumber)
		    {
			    Log.Add(Log.Severity.Information, "Cannot add player. The game is full.");
			    return false;
		    }

		    if (GameDefinition.IsTeamGame)
		    {
			    var isTagMasterGame = (
					GameDefinition.GameType == GameType.TagMasterHideAndSeek ||
					GameDefinition.GameType == GameType.TagMasterHuntThePrey ||
					GameDefinition.GameType == GameType.HuntTheTagMasterTwoTeams);

			    // Count the players on each team and find the smallest team
			    var teamPlayerCounts = new int[_gameDefinition.TeamCount];
			    var smallestTeamNumber = 0;
			    var smallestTeamPlayerCount = 8;
			    for (var teamNumber = 1; teamNumber <= _gameDefinition.TeamCount; teamNumber++)
			    {
				    var teamPlayerCount = 0;
				    foreach (var player in _players)
				    {
					    if (player.TeamPlayerId.TeamNumber == teamNumber) teamPlayerCount++;
				    }

					// In Tag Master games, team 1 is full once it has a player
					if (isTagMasterGame && teamNumber == 1 && teamPlayerCount > 0) teamPlayerCount = 8;

				    if (teamPlayerCount < smallestTeamPlayerCount)
				    {
					    smallestTeamNumber = teamNumber;
					    smallestTeamPlayerCount = teamPlayerCount;
				    }
				    teamPlayerCounts[teamNumber - 1] = teamPlayerCount;
			    }

			    if (smallestTeamNumber == 0)
			    {
					Log.Add(Log.Severity.Information, "All teams are full.");
					return false;
			    }

			    if (preferredTeam > 0 &&
			        preferredTeam <= _gameDefinition.TeamCount &&
			        teamPlayerCounts[preferredTeam - 1] < 8)
			    {
				    assignedTeamNumber = preferredTeam;
			    }
			    else if (requestedTeam > 0 &&
			             requestedTeam <= _gameDefinition.TeamCount &&
			             teamPlayerCounts[requestedTeam - 1] < 8)
			    {
				    assignedTeamNumber = requestedTeam;
			    }
			    else
			    {
				    assignedTeamNumber = smallestTeamNumber;
			    }

			    for (var playerNumber = 1; playerNumber <= 8; playerNumber++)
			    {
				    if (_players.Player(new TeamPlayerId(assignedTeamNumber, playerNumber)) == null)
				    {
					    assignedPlayerNumber = playerNumber;
					    break;
				    }
			    }
		    }
		    else
		    {
			    // Assign player to the first open player number
			    for (var playerNumber = 1; playerNumber <= 24; playerNumber++)
			    {
					var teamPlayerId = new TeamPlayerId(playerNumber);
					if (_players.Player(teamPlayerId) != null) continue;
					assignedTeamNumber = teamPlayerId.TeamNumber;
					assignedPlayerNumber = playerNumber;
					break;
			    }
		    }

			if (assignedTeamNumber == 0 || assignedPlayerNumber == 0)
		    {
			    Log.Add(Log.Severity.Warning, "Unable to assign a player number.");
			    return false;
		    }

		    if (_gameDefinition.IsTeamGame)
		    {
				newPlayer.TeamPlayerId = new TeamPlayerId(assignedTeamNumber, assignedPlayerNumber);
		    }
		    else
		    {
				newPlayer.TeamPlayerId = new TeamPlayerId(assignedPlayerNumber);
			}

			newPlayer.Team = Teams.Team(assignedTeamNumber);
			Teams.Team(assignedTeamNumber).Players.Add(newPlayer);

		    return true;
	    }

	    // mask = bitmask of reserved/unknown bits; warns if any of those bits are set in the signature.
	    private static void AssertUnknownBits(string name, Signature signature, byte mask)
        {
	        if (((byte) (signature.Data & mask)) == 0) return;
		    Log.Add(Log.Severity.Warning, "Unknown bits set: \"{0}\", data: 0x{2:X2}, mask: 0x{3:X2}, unknown: 0x{1:X2}",
			    name, (byte) (signature.Data & mask), (byte) signature.Data, mask);
        }
        
        /// <summary>
        /// Returns the number of effective tags landed by a player. LTAR detailed tag
        /// reports can contain repeated IR frames for the same accepted hit, so the raw
        /// per-target counters must be capped at the target's available hit points.
        /// </summary>
        public int CountEffectiveHitsScored(Player player)
        {
            if (player == null) return 0;

            var hitsScored = 0;
            foreach (var target in _players)
            {
                if (ReferenceEquals(player, target) || target.Dropped) continue;
                if (_gameDefinition.IsTeamGame &&
                    player.TeamPlayerId.TeamNumber == target.TeamPlayerId.TeamNumber) continue;

                var targetIndex = target.TeamPlayerId.PlayerNumber - 1;
                if (targetIndex < 0 || targetIndex >= player.TaggedPlayerCounts.Length) continue;

                var rawHits = Math.Max(0, player.TaggedPlayerCounts[targetIndex]);
                var targetHp = target.TagsOverride.HasValue
                    ? target.TagsOverride.Value
                    : _gameDefinition.Tags;

                hitsScored += Math.Min(rawHits, Math.Max(0, targetHp));
            }

            return hitsScored;
        }

        private void PrintScoreReport()
        {
            foreach (var player in _players)
            {
	            var effectiveHp      = player.TagsOverride.HasValue    ? player.TagsOverride.Value    : _gameDefinition.Tags;
	            var effectiveShields = player.ShieldsOverride.HasValue  ? player.ShieldsOverride.Value  : _gameDefinition.Shields;
	            var effectiveReloads = player.ReloadsOverride.HasValue  ? player.ReloadsOverride.Value  : _gameDefinition.Reloads;
	            var hpNote      = string.Format("HP: {0}{1}",      effectiveHp,      player.TagsOverride.HasValue    ? " (individual)" : " (global)");
	            var shieldsNote = string.Format("Shields: {0}s{1}", effectiveShields, player.ShieldsOverride.HasValue  ? " (individual)" : " (global)");
	            var reloadsNote = string.Format("Reloads: {0}{1}", effectiveReloads == 0xff ? "unlimited" : effectiveReloads.ToString(),
	                                            player.ReloadsOverride.HasValue ? " (individual)" : " (global)");
	            Log.Add(Log.Severity.Information, "{0} ({1}) - {2}, {3}, {4}", player.NameAndNumber, player.TaggerId, hpNote, shieldsNote, reloadsNote);

				var hitsScored = CountEffectiveHitsScored(player);
				var rawHitFrames = player.TaggedPlayerCounts.Sum();
				if (rawHitFrames != hitsScored)
				{
					Log.Add(Log.Severity.Debug,
						"Normalized {0} raw tag-report frames to {1} effective hits for {2}.",
						rawHitFrames, hitsScored, player.NameAndNumber);
				}

				var rankLine = _gameDefinition.IsTeamGame
					? string.Format("\tRank: {0} (Team Rank: {1})", player.Rank, player.Team.Rank)
					: string.Format("\tRank: {0}", player.Rank);

				Log.Add(Log.Severity.Information, "{0} - Score: {1}, Hits Scored: {2}, Hits Taken: {3}",
					rankLine, player.Score, hitsScored, player.TagsTaken);
            }
        }


		private void SyncExplicitStateFromHostingState(string reason)
		{
			var explicitState = HostGunStateMachineAdapter.ToExplicitState(HostingState);

			if (_explicitStateMachine.CurrentState == explicitState)
				return;

			ForceExplicitState(explicitState, reason);
		}

		private void ForceExplicitState(HostSessionState targetState, string reason)
		{
			if (_explicitStateMachine.CurrentState == targetState)
				return;

			string ignored;
			var guard = 0;

			while (_explicitStateMachine.CurrentState != targetState && guard < 20)
			{
				guard++;

				switch (_explicitStateMachine.CurrentState)
				{
					case HostSessionState.Idle:
						if (targetState != HostSessionState.Idle)
						{
							_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StartHosting), out ignored);
						}
						else
						{
							return;
						}
						break;

					case HostSessionState.AddingPlayers:
						switch (targetState)
						{
							case HostSessionState.AwaitingAssignmentAck:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.PlayerAssignmentSent), out ignored);
								break;
							case HostSessionState.Countdown:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.CountdownStarted), out ignored);
								break;
							case HostSessionState.Idle:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StopHosting), out ignored);
								break;
							default:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.FaultRaised, reason), out ignored);
								break;
						}
						break;

					case HostSessionState.AwaitingAssignmentAck:
						switch (targetState)
						{
							case HostSessionState.AddingPlayers:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.AssignmentAcknowledged), out ignored);
								break;
							case HostSessionState.Idle:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StopHosting), out ignored);
								break;
							case HostSessionState.Countdown:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.AssignmentAcknowledged), out ignored);
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.CountdownStarted), out ignored);
								break;
							default:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.FaultRaised, reason), out ignored);
								break;
						}
						break;

					case HostSessionState.Countdown:
						switch (targetState)
						{
							case HostSessionState.InGame:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.CountdownExpired), out ignored);
								break;
							case HostSessionState.Idle:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StopHosting), out ignored);
								break;
							default:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.FaultRaised, reason), out ignored);
								break;
						}
						break;

					case HostSessionState.ResendCountdown:
						switch (targetState)
						{
							case HostSessionState.InGame:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.ResendCountdownExpired), out ignored);
								break;
							case HostSessionState.Idle:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StopHosting), out ignored);
								break;
							default:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.FaultRaised, reason), out ignored);
								break;
						}
						break;

					case HostSessionState.InGame:
						switch (targetState)
						{
							case HostSessionState.ResendCountdown:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.TransportLost), out ignored);
								break;
							case HostSessionState.AwaitingDebrief:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.GameExpired), out ignored);
								break;
							case HostSessionState.Idle:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StopHosting), out ignored);
								break;
							default:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.FaultRaised, reason), out ignored);
								break;
						}
						break;

					case HostSessionState.AwaitingDebrief:
						switch (targetState)
						{
							case HostSessionState.GameOver:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.DebriefComplete), out ignored);
								break;
							case HostSessionState.Idle:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StopHosting), out ignored);
								break;
							default:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.FaultRaised, reason), out ignored);
								break;
						}
						break;

					case HostSessionState.Recovery:
						switch (targetState)
						{
							case HostSessionState.InGame:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.TransportRestored), out ignored);
								break;
							case HostSessionState.Idle:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StopHosting), out ignored);
								break;
							default:
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.FaultRaised, reason), out ignored);
								break;
						}
						break;

					case HostSessionState.GameOver:
					case HostSessionState.Faulted:
						if (targetState == HostSessionState.Idle)
						{
							_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.Reset), out ignored);
						}
						else
						{
							_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.Reset), out ignored);
							if (targetState != HostSessionState.Idle)
								_explicitStateMachine.Handle(new HostStateMachineEvent(HostEventType.StartHosting), out ignored);
						}
						break;
				}
			}

			if (_explicitStateMachine.CurrentState != targetState)
			{
				Log.Add(Log.Severity.Debug,
					"Explicit state machine sync ended at {0} instead of {1} ({2}).",
					_explicitStateMachine.CurrentState, targetState, reason);
			}
		}

		private void NotifyExplicitEvent(HostEventType eventType, object payload = null)
		{
			string note;
			if (_explicitStateMachine.Handle(new HostStateMachineEvent(eventType, payload), out note))
			{
				if (!string.IsNullOrWhiteSpace(note))
				{
					Log.Add(Log.Severity.Debug, "Explicit state event {0}: {1}", eventType, note);
				}
			}
		}

		private void CheckPacketAgainstExplicitState(PacketType packetType)
		{
			var packetKind = HostGunStateMachineAdapter.PacketKindFrom(packetType);
			if (packetKind == HostPacketKind.Unknown)
				return;

			var explicitState = HostGunStateMachineAdapter.ToExplicitState(HostingState);
			if (_lastExplicitPacketCheckState != explicitState)
			{
				_lastExplicitPacketCheckState = explicitState;
			}

			if (!_explicitStateMachine.IsPacketAccepted(packetKind))
			{
				Log.Add(Log.Severity.Debug,
					"Explicit state machine: packet kind {0} is not expected in {1} (packet 0x{2:X2}).",
					packetKind, _explicitStateMachine.CurrentState, (byte)packetType);
			}
		}

	    public bool ProcessPacket(Packet packet)
	    {
		    var packetType = (PacketType) packet.PacketTypeSignature.Data;
			CheckPacketAgainstExplicitState(packetType);

		    // These packet types are ones we transmit - if we receive them it is
		    // always self-echo via the IR relay picking up its own transmission.
		    // Silently ignore them regardless of state to keep the log clean.
		    switch (packetType)
		    {
			    case PacketType.AnnounceGameCustomLazerTag:
			    case PacketType.AnnounceGameCustomLazerTagTwoTeams:
			    case PacketType.AnnounceGameCustomLazerTagThreeTeams:
			    case PacketType.AnnounceGameHideAndSeek:
			    case PacketType.AnnounceGameHuntThePrey:
			    case PacketType.AnnounceGameKingsTwoTeams:
			    case PacketType.AnnounceGameKingsThreeTeams:
			    case PacketType.AnnounceGameOwnTheZone:
			    case PacketType.AnnounceGameOwnTheZoneTwoTeams:
			    case PacketType.AnnounceGameOwnTheZoneThreeTeams:
			    case PacketType.AnnounceGameSpecial:
			    case PacketType.AnnounceGameEnhanced:       // 0x81
			    case PacketType.AssignPlayerEnhanced:       // 0x83
			    case PacketType.AssignPlayerSuccessEnhanced:// 0x87
			    case PacketType.AnnounceCountdownEnhanced:  // 0x88
			    case PacketType.AssignPlayer:
			    case PacketType.AssignPlayerFailed:
			    case PacketType.AssignPlayerFailedEnhanced:
			    case PacketType.AnnounceCountdown:
			    case PacketType.RequestTagReport:            // 0x31
			    case PacketType.RankReport:                 // 0x32
				    Log.Add(Log.Severity.Debug, "Ignored self-echo packet 0x{0:X2} in state {1}.",
					    (byte)packetType, HostingState);
				    return true;
		    }

		    switch (packetType)
		    {
			    case PacketType.SingleTagReport:
				    if (packet.Data.Count != 4) break;
				    var isReply = ((packet.Data[1].Data & 0x80) & (packet.Data[2].Data & 0x80)) != 0;
				    var teamPlayerId1 = TeamPlayerId.FromPacked34(packet.Data[1].Data);
				    var teamPlayerId2 = TeamPlayerId.FromPacked34(packet.Data[2].Data);
				    var tagsReceived = packet.Data[3].Data;
				    var replyText = isReply ? "replied to tag count request from" : "requested tag count from";
				    Log.Add(Log.Severity.Information, "Player {0} {1} player {2}. Player {0} received {3} tags from player {2}.",
					    teamPlayerId1, replyText, teamPlayerId2, tagsReceived);
				    return true;
			    case PacketType.TextMessage:
				    var message = new StringBuilder();
				    var i = 0;
				    while (i < packet.Data.Count &&
							packet.Data[i].Data >= 0x20 &&
							packet.Data[i].Data <= 0x7e &&
							packet.Data[i].BitCount == 8)
				    {
					    message.Append(Convert.ToChar(packet.Data[i].Data));
					    i++;
				    }
				    Log.Add(Log.Severity.Information, "Received Text Message: {0}", message);
				    return true;
			    case PacketType.SpecialAttack:
				    var type = "Unknown Type";
				    if (packet.Data.Count >= 5)
				    {
					    switch (packet.Data[2].Data)
					    {
						    case 0x77:
							    type = "EM Peacemaker";
							    break;
						    case 0xb1:
							    type = "Talus Airstrike";
							    break;
					    }
				    }
				    Log.Add(Log.Severity.Information, "Special Attack: {0} - {1}", type, packet);
				    if (packet.Data.Count >= 5)
				    {
					    AssertUnknownBits("Special Attack Flags 1", packet.Data[1], 0xff);
					    AssertUnknownBits("Special Attack Flags 2", packet.Data[2], 0xff);
					    AssertUnknownBits("Special Attack Flags 3", packet.Data[3], 0xff);
					    AssertUnknownBits("Special Attack Flags 4", packet.Data[4], 0xff);
				    }
				    return true;
		    }

		    switch (HostingState)
		    {
			    case HostingStates.Idle:
				    return true;
			    case HostingStates.Adding:
			    case HostingStates.AcknowledgePlayerAssignment:
				    if (packet.Data.Count < 2) return false;

				    var gameId = packet.Data[0].Data;
				    var taggerId = packet.Data[1].Data;

				    switch (packet.Type)
				    {
					    case PacketType.RequestJoinGame:
							if (packet.Data.Count < 3) return false;
							var requestedTeam = (UInt16) (packet.Data[2].Data & 0x03);
							return ProcessRequestJoinGame(gameId, taggerId, requestedTeam);
						case PacketType.RequestJoinGameEnhanced:
							return ProcessRequestJoinGame(gameId, taggerId, 0);
						case PacketType.AcknowledgePlayerAssignment:
						case PacketType.AcknowledgePlayerAssignmentEnhanced:
							return ProcessAcknowledgePlayerAssignment(gameId, taggerId);
					    default:
						    Log.Add(Log.Severity.Debug, "Unexpected packet in {0} state: {1}", HostingState, packet);
						    return false;
				    }
			    case HostingStates.Summary:
				    switch (packet.Type)
				    {
					    case PacketType.TagSummary:
						    return ProcessTagSummary(packet);
					    case PacketType.TeamOneTagReport:
					    case PacketType.TeamTwoTagReport:
					    case PacketType.TeamThreeTagReport:
						    return ProcessTeamTagReport(packet);
					    default:
						    Log.Add(Log.Severity.Debug, "Unhandled summary packet 0x{0:X2}: {1}",
							    (byte)packet.Type, packet);
						    return false;
				    }
		    }

		    Log.Add(Log.Severity.Debug, "Unhandled packet 0x{0:X2} in state {1}: {2}",
			    (byte)packet.Type, HostingState, packet);
		    return false;
	    }

	    private bool ProcessRequestJoinGame(UInt16 gameId, UInt16 taggerId, UInt16 requestedTeam)
	    {
			
			if (gameId != GameDefinition.GameId)
			{
				Log.Add(Log.Severity.Warning, "Wrong game ID.");
				return false;
			}

			Player player = null;

			foreach (var checkPlayer in Players)
			{
				if (checkPlayer.TaggerId == taggerId && !checkPlayer.Confirmed)
				{
					player = checkPlayer;
					break;
				}
			}

            if (player == null)
            {
                player = new Player(this, (byte)taggerId);

                var joinIndex = Players.Count;
                var preferredTeam = 0;
                if (joinIndex >= 0 && joinIndex < _plannedTeamPreferences.Count && _plannedTeamPreferences[joinIndex].HasValue)
                    preferredTeam = _plannedTeamPreferences[joinIndex].Value;

                if (!AssignTeamAndPlayer(requestedTeam, preferredTeam, player)) return false;

                Players.Add(player);
            }

			var joinState = new JoinState
			{
				GameId = gameId,
				Player = player,
				AssignPlayerSendTime = DateTime.Now
			};
			_joinStates.Remove(taggerId);
			_joinStates.Add(taggerId, joinState);

			Log.Add(Log.Severity.Information, "Assigning tagger 0x{0:X2} to player {1} for game 0x{2:X2}.", taggerId,
				   player.TeamPlayerId.ToString(_gameDefinition.IsTeamGame), gameId);

		    SendPlayerAssignment(player.TeamPlayerId);

			ChangeState(HostingStates.AcknowledgePlayerAssignment);

			return true;
		}

		private bool ProcessAcknowledgePlayerAssignment(UInt16 gameId, UInt16 taggerId)
		{
			
			if (gameId != _gameDefinition.GameId)
			{
				Log.Add(Log.Severity.Warning, "Wrong game ID.");
				return false;
			}

			JoinState joinState;
			if (!_joinStates.TryGetValue(taggerId, out joinState))
			{
				Log.Add(Log.Severity.Warning, "Unable to find player to confirm");
				return false;
			}

			var player = joinState.Player;
			player.Confirmed = true;
			_joinStates.Remove(taggerId);

			Log.Add(Log.Severity.Information, "Confirmed player {0} for game 0x{1:X2}.",
			                   player.TeamPlayerId.ToString(_gameDefinition.IsTeamGame), gameId);

			if (_gameDefinition.UseLtarEnhancedProtocol)
			{
				SendLtarRelease(player, "assignment");
			}

			if (_joinStates.Count < 1) ChangeState(HostingStates.Adding);

			OnPlayerListChanged(new PlayerListChangedEventArgs(Players));

			return true;
		}


		private void SendLtarRelease(Player player, string reason)
		{
			if (!_gameDefinition.UseLtarEnhancedProtocol) return;
			if (player == null) return;

			var packet = PacketPacker.AssignPlayerSuccessEnhanced(_gameDefinition.GameId, player.TeamPlayerId);
			Protocol.TransmitPacket(packet);
			Log.Add(Log.Severity.Information, "Sent LTAR release for {0} ({1}).", player.NameAndNumber, reason);
		}

		private void SendLtarReleaseToAllConfirmedPlayers(string reason)
		{
			if (!_gameDefinition.UseLtarEnhancedProtocol) return;
			foreach (var player in Players)
			{
				if (!player.Confirmed || player.Dropped) continue;
				SendLtarRelease(player, reason);
			}
		}

		private void SendLtarDebriefReleaseIfReady(Player player)
		{
			if (!_gameDefinition.UseLtarEnhancedProtocol) return;
			if (player == null) return;
			if (player.LtarDebriefReleased) return;
			if (!player.AllTagReportsReceived()) return;

			SendLtarRelease(player, "debrief");
			player.LtarDebriefReleased = true;
		}

		private void SendGameEndSignal()
		{
			// Per the wiki, CountdownEnhanced is ignored by taggers unless sent at 1-second
			// intervals with a decrementing value - so a rapid burst of T-0 packets is always
			// ignored. Debrief is triggered correctly by the AssignPlayerSuccess (0x87) release
			// sent before each RequestTagReport. This method is intentionally a no-op and kept
			// only so ChangeState(Summary) compiles; it can be removed in a future cleanup.
			Log.Add(Log.Severity.Debug, "SendGameEndSignal: no-op (CountdownEnhanced burst is ignored by taggers per protocol spec).");
		}

		internal PacketType? GetExpectedDebriefPacketTypeForRecovery()
		{
			if (HostingState != HostingStates.Summary) return null;
			if (_currentDebriefPlayer == null) return null;

			if (!_currentDebriefPlayer.TagSummaryReceived)
			{
				return PacketType.TagSummary;
			}

			for (var teamNumber = 1; teamNumber <= 3; teamNumber++)
			{
				if (!_currentDebriefPlayer.TeamTagReportsExpected[teamNumber - 1]) continue;
				if (_currentDebriefPlayer.TeamTagReportsReceived[teamNumber - 1]) continue;

				return (PacketType)((int)PacketType.TeamOneTagReport + (teamNumber - 1));
			}

			return null;
		}

		internal static TeamPlayerId DecodeDebriefPlayerAddress(UInt16 packedAddress, bool useEnhancedAddressing)
		{
			// Tag Summary and Team Tag Report follow Request Tag Report: standard
			// hosting uses a 4:4 team/player address, while LTAR enhanced hosting uses
			// the compact 2:3 address used by the enhanced assignment packets.
			return useEnhancedAddressing
				? TeamPlayerId.FromPacked23(packedAddress)
				: TeamPlayerId.FromPacked44(packedAddress);
		}

		private bool ProcessTagSummary(Packet packet)
		{
			if (packet.Type != PacketType.TagSummary)
			{
				Log.Add(Log.Severity.Debug, "Unexpected packet in ProcessTagSummary: {0}", packet);
				return false;
			}

			if (packet.Data.Count != 8)
			{
				return false;
			}

			var gameId = packet.Data[0].Data;
			if (gameId != _gameDefinition.GameId)
			{
				Log.Add(Log.Severity.Warning, "Wrong game ID.");
				return false;
			}

			var teamPlayerId = DecodeDebriefPlayerAddress(packet.Data[1].Data,
				_gameDefinition.UseLtarEnhancedProtocol);
			var tagsTaken = packet.Data[2].Data; // Hex Coded Decimal
			var surviveTimeMinutes = packet.Data[3].Data;
			var surviveTimeSeconds = packet.Data[4].Data;
			var zoneTimeMinutes = packet.Data[5].Data;
			var zoneTimeSeconds = packet.Data[6].Data;

			var teamTagReports = packet.Data[7];
			AssertUnknownBits("teamTagReports", teamTagReports, 0xf1);

			var player = _players.Player(teamPlayerId);
			if (player == null)
			{
				Log.Add(Log.Severity.Warning, "Unable to find player for score report.");
				return false;
			}

			player.SurviveTime = new TimeSpan(0, 0, BinaryCodedDecimal.ToDecimal(surviveTimeMinutes), BinaryCodedDecimal.ToDecimal(surviveTimeSeconds));
			player.Survived = player.SurviveTime.TotalMinutes >= GameDefinition.GameTimeMinutes;
			player.TagsTaken = BinaryCodedDecimal.ToDecimal(tagsTaken);
			player.TeamTagReportsExpected[0] = (teamTagReports.Data & 0x2) != 0;
			player.TeamTagReportsExpected[1] = (teamTagReports.Data & 0x4) != 0;
			player.TeamTagReportsExpected[2] = (teamTagReports.Data & 0x8) != 0;
			player.ZoneTime = new TimeSpan(0, 0, BinaryCodedDecimal.ToDecimal(zoneTimeMinutes), BinaryCodedDecimal.ToDecimal(zoneTimeSeconds));

			player.TagSummaryReceived = true;

			Log.Add(Log.Severity.Information, "Received tag summary from {0}.", player.NameAndNumber);
			SendLtarDebriefReleaseIfReady(player);
			NotifyExplicitEvent(HostEventType.DebriefSummaryReceived, player.TeamPlayerId);

			OnPlayerListChanged(new PlayerListChangedEventArgs(Players));

			return true;
		}

		internal static bool TryDecodeTeamTagCount(UInt16 encodedValue, out int tagCount)
		{
			tagCount = 0;
			if (encodedValue > 0xff) return false;

			// Most taggers send canonical packed BCD here, but real LTAR hardware has
			// also been observed returning stable non-canonical bytes such as 0x8A.
			// The original working host accepted every byte and decoded it with the same
			// nibble arithmetic. Rejecting a non-decimal nibble leaves that player in an
			// endless debrief loop. Preserve the original compatibility behavior; the
			// score path later caps repeated/raw frames at the target's available HP.
			tagCount = BinaryCodedDecimal.ToDecimal((byte)encodedValue);
			return true;
		}

		internal static bool IsCanonicalTeamTagCountBcd(UInt16 encodedValue)
		{
			if (encodedValue > 0xff) return false;

			var encodedByte = (byte)encodedValue;
			return (encodedByte & 0x0f) <= 9 && ((encodedByte >> 4) & 0x0f) <= 9;
		}

		private bool ProcessTeamTagReport(Packet packet)
		{
			if (packet.Data.Count < 3) return false;

			var gameId = packet.Data[0].Data;
			if (gameId != _gameDefinition.GameId)
			{
				Log.Add(Log.Severity.Warning, "Wrong game ID.");
				return false;
			}

			// what team do the scores relate to hits from
			var taggedByTeamNumber = (int)(packet.PacketTypeSignature.Data - PacketType.TeamOneTagReport + 1);
			if (taggedByTeamNumber < 1 || taggedByTeamNumber > 3)
			{
				Log.Add(Log.Severity.Warning, "Invalid team number {0} in tag report.", taggedByTeamNumber);
				return false;
			}

			var teamPlayerId = DecodeDebriefPlayerAddress(packet.Data[1].Data,
				_gameDefinition.UseLtarEnhancedProtocol);

			var player = Players.Player(teamPlayerId);
			if (player == null) return false;

			if (player.TagSummaryReceived && !player.TeamTagReportsExpected[taggedByTeamNumber - 1])
			{
				Log.Add(Log.Severity.Warning, "A tag report from player {0} for team {1} was not expected.",
					player.TeamPlayerId, taggedByTeamNumber);
			}

			if (player.TeamTagReportsReceived[taggedByTeamNumber - 1])
			{
				Log.Add(Log.Severity.Warning, "A tag report from player {0} for team {1} was already received. Discarding.",
					player.TeamPlayerId, taggedByTeamNumber);
				return false;
			}

			var scoreBitmask = packet.Data[2].Data;
			var expectedPayloadLength = 3;
			for (var bit = 0; bit < 8; bit++)
			{
				if (((scoreBitmask >> bit) & 0x1) != 0) expectedPayloadLength++;
			}

			if (packet.Data.Count != expectedPayloadLength)
			{
				Log.Add(Log.Severity.Warning,
					"Invalid tag report length for player {0}, team {1}: expected {2}, received {3}.",
					player.TeamPlayerId, taggedByTeamNumber, expectedPayloadLength, packet.Data.Count);
				return false;
			}

			// Decode the complete payload before changing any score or receipt state.
			var decodedTagCounts = new int[8];
			var packetIndex = 3;
			for (var taggedByTeamPlayerNumber = 1; taggedByTeamPlayerNumber <= 8; taggedByTeamPlayerNumber++)
			{
				var hasTags = ((scoreBitmask >> (taggedByTeamPlayerNumber - 1)) & 0x1) != 0;
				if (!hasTags) continue;

				int tagsTaken;
				var encodedTagCount = packet.Data[packetIndex].Data;
				if (!TryDecodeTeamTagCount(encodedTagCount, out tagsTaken))
				{
					Log.Add(Log.Severity.Warning,
						"Out-of-range tag count 0x{0:X} for player {1}, team {2}, team player {3}.",
						encodedTagCount, player.TeamPlayerId, taggedByTeamNumber, taggedByTeamPlayerNumber);
					return false;
				}

				if (!IsCanonicalTeamTagCountBcd(encodedTagCount))
				{
					Log.Add(Log.Severity.Warning,
						"Accepted non-canonical tag count 0x{0:X} for player {1}, team {2}, team player {3} using legacy-compatible decoding ({4}).",
						encodedTagCount, player.TeamPlayerId, taggedByTeamNumber, taggedByTeamPlayerNumber, tagsTaken);
				}

				decodedTagCounts[taggedByTeamPlayerNumber - 1] = tagsTaken;
				packetIndex++;
			}

			for (var taggedByTeamPlayerNumber = 1; taggedByTeamPlayerNumber <= 8; taggedByTeamPlayerNumber++)
			{
				var hasTags = ((scoreBitmask >> (taggedByTeamPlayerNumber - 1)) & 0x1) != 0;
				if (!hasTags) continue;

				var taggedByTeamPlayerId = new TeamPlayerId(taggedByTeamNumber, taggedByTeamPlayerNumber);
				var tagsTaken = decodedTagCounts[taggedByTeamPlayerNumber - 1];
				player.TaggedByPlayerCounts[taggedByTeamPlayerId.PlayerNumber - 1] = tagsTaken;

				var taggedByPlayer = Players.Player(taggedByTeamPlayerId);
				if (taggedByPlayer != null)
				{
					taggedByPlayer.TaggedPlayerCounts[player.TeamPlayerId.PlayerNumber - 1] = tagsTaken;
				}
			}

			player.TeamTagReportsReceived[taggedByTeamNumber - 1] = true;

			SendLtarDebriefReleaseIfReady(player);
			NotifyExplicitEvent(HostEventType.DebriefSummaryReceived, player.TeamPlayerId);
			OnPlayerListChanged(new PlayerListChangedEventArgs(Players));

			return true;
		}

        private void CalculateScores()
        {
	        RemoveDroppedPlayerTags();

            switch (_gameDefinition.GameType)
			{
				case GameType.OwnTheZone:
				case GameType.OwnTheZoneTwoTeams:
				case GameType.OwnTheZoneThreeTeams:
					CalculateScoresOwnTheZone();
					break;
				case GameType.CustomLazerTag:
				case GameType.Respawn:
					CalculateScoresSoloGames();
					break;
				case GameType.CustomLazerTagTwoTeams:
				case GameType.CustomLazerTagThreeTeams:
				case GameType.HuntThePrey:
				case GameType.HideAndSeek:
				case GameType.RespawnTwoTeams:
				case GameType.RespawnThreeTeams:
				case GameType.TagMasterHideAndSeek:
				case GameType.TagMasterHuntThePrey:
					CalculateScoresTeamGames();
					break;
				case GameType.KingsTwoTeams:
				case GameType.KingsThreeTeams:
					CalculateScoresKings();
					break;
				case GameType.OneOnOne:
					CalculateScoresOneOnOne();
					break;
				case GameType.Survival:
					CalculateScoresSurvivalSolo();
					break;
				case GameType.SurvivalTwoTeams:
				case GameType.SurvivalThreeTeams:
					CalculateScoresSurvivalTeams();
					break;
				case GameType.HuntTheTagMasterTwoTeams:
					CalculateScoresHuntTheTagMasterTwoTeams();
					break;
				default:
					Log.Add(Log.Severity.Warning, "Unable to score game type {0}.", _gameDefinition.GameType);
					break;
			}
        }

		private void RemoveDroppedPlayerTags()
	    {
		    foreach (var droppedPlayer in Players.Where(droppedPlayer => droppedPlayer.Dropped))
		    {
			    droppedPlayer.TagsTaken = 0;

			    foreach (var otherPlayer in Players)
			    {
				    droppedPlayer.TaggedPlayerCounts[otherPlayer.TeamPlayerId.PlayerNumber - 1] = 0;
				    droppedPlayer.TaggedByPlayerCounts[otherPlayer.TeamPlayerId.PlayerNumber - 1] = 0;

				    otherPlayer.TagsTaken -= otherPlayer.TaggedByPlayerCounts[droppedPlayer.TeamPlayerId.PlayerNumber - 1];
				    otherPlayer.TaggedPlayerCounts[droppedPlayer.TeamPlayerId.PlayerNumber - 1] = 0;
				    otherPlayer.TaggedByPlayerCounts[droppedPlayer.TeamPlayerId.PlayerNumber - 1] = 0;
			    }
		    }
	    }

	    private void CalculateScoresOwnTheZone()
		{
			foreach (var player in _players)
			{
				var playerZoneTimeSeconds = Convert.ToInt32(player.ZoneTime.TotalSeconds);
				player.Score = playerZoneTimeSeconds;
				Teams.Team(player.TeamPlayerId.TeamNumber).Score += playerZoneTimeSeconds;
			}

			Teams.CalculateRanks();
			Players.CalculateRanks();
		}

		private void CalculateScoresSoloGames()
		{
			foreach (var player in _players)
			{
				player.Score = CountEffectiveHitsScored(player);
			}

			Teams.CalculateRanks();
			Players.CalculateRanks();
		}

		private void CalculateScoresTeamGames()
	    {
			var teamCount = GameDefinition.TeamCount;
			var teamSurvivedPlayerCounts      = new int[teamCount];
			var teamSurvivedPlayerScoreTotals = new int[teamCount];
			var teamAllPlayerScoreTotals      = new int[teamCount];

		    foreach (var player in _players)
		    {
				var hitsScored = CountEffectiveHitsScored(player);
			    player.Score = hitsScored;
			    teamAllPlayerScoreTotals[player.TeamPlayerId.TeamNumber - 1] += hitsScored;

			    if (player.Survived)
			    {
				    teamSurvivedPlayerCounts[player.TeamPlayerId.TeamNumber - 1]++;
				    teamSurvivedPlayerScoreTotals[player.TeamPlayerId.TeamNumber - 1] += player.Score;
			    }
		    }

		    // Primary: survivors (weighted heavily so a surviving team always beats a non-surviving one).
		    // Fallback when no team has survivors: rank by total hits so the result is meaningful.
		    bool anyTeamHasSurvivors = false;
		    for (var t = 0; t < teamCount; t++)
		        if (teamSurvivedPlayerCounts[t] > 0) { anyTeamHasSurvivors = true; break; }

			for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
			{
				int teamScore;
				if (anyTeamHasSurvivors)
					teamScore = (teamSurvivedPlayerCounts[teamNumber - 1] << 10) +
					            (teamSurvivedPlayerScoreTotals[teamNumber - 1] << 2);
				else
					teamScore = teamAllPlayerScoreTotals[teamNumber - 1];

				Teams.Team(teamNumber).Score = teamScore;
				Log.Add(Log.Severity.Information, "Team {0} had {1} surviving players.", teamNumber, teamSurvivedPlayerCounts[teamNumber - 1]);
				Log.Add(Log.Severity.Information, "The total score of the surviving players was {0}.", teamSurvivedPlayerScoreTotals[teamNumber - 1]);
				Log.Add(Log.Severity.Information, "Team {0}'s final score was {1}.", teamNumber, teamScore);
		    }

		    Teams.CalculateRanks();
		    Players.CalculateRanks();
	    }

		private void CalculateScoresKings()
		{
			// Note: Kings scoring formula - verify against tagger firmware if results look off.

			var teamCount = GameDefinition.TeamCount;
			var teamKingSurvived = new bool[teamCount];
			var teamKingTagsTaken = new int[teamCount];

			// Calculate player scores
			foreach (var player in _players)
			{
				if (player.TeamPlayerId.TeamPlayerNumber == 1) // player is a king
				{
					var teamNumber = player.TeamPlayerId.TeamNumber - 1;
					teamKingSurvived[teamNumber] = player.Survived;
					teamKingTagsTaken[teamNumber] = player.TagsTaken;
					Log.Add(Log.Severity.Information, "Team {0}'s king took {1} tags and {2}.", teamNumber, player.TagsTaken, player.Survived ? "survived" : "did not survive");
				}

				for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
				{
					var teamKing = Players.Player(new TeamPlayerId(teamNumber, 1));
					if (teamKing == null)
					{
						Log.Add(Log.Severity.Warning, "Could not find the king for team {0}.", teamNumber);
						return;
					}

					if (player.TeamPlayerId.TeamNumber == teamNumber)
					{
						// Players lose 4 point for each tag they land on their own team's king
						player.Score -= 4*player.TaggedPlayerCounts[teamKing.TeamPlayerId.PlayerNumber - 1];
					}
					else
					{
						// Players receive 1 point for each tag they land on other teams' kings
						player.Score += player.TaggedPlayerCounts[teamKing.TeamPlayerId.PlayerNumber - 1];
					}
				}
			}

			// Calculate team scores
			for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
			{
				var teamScore = ((teamKingSurvived[teamNumber - 1] ? 1 : 0) << 10) - (teamKingTagsTaken[teamNumber - 1] << 2);
				Teams.Team(teamNumber).Score = teamScore;
			}

			Teams.CalculateRanks();
			Players.CalculateRanks();
		}

		private void CalculateScoresOneOnOne()
		{
			var teamCount = GameDefinition.TeamCount;
			var teamSurvivedPlayerCounts = new int[teamCount];
			var teamScoreTotals = new int[teamCount];

			foreach (var player in _players)
			{
				var hitsScored = 0;
				for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
					for (var playerNumber = 1; playerNumber <= 8; playerNumber++)
						if (player.TeamPlayerId.TeamNumber != teamNumber)
							hitsScored += player.TaggedPlayerCounts[new TeamPlayerId(teamNumber, playerNumber).PlayerNumber - 1];

				player.Score = hitsScored;
				teamScoreTotals[player.TeamPlayerId.TeamNumber - 1] += player.Score;
				if (player.Survived) teamSurvivedPlayerCounts[player.TeamPlayerId.TeamNumber - 1]++;
			}

			for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
			{
				var teamScore = teamScoreTotals[teamNumber - 1];
				Teams.Team(teamNumber).Score = teamScore;
				Log.Add(Log.Severity.Information, "Team {0} had {1} surviving players.", teamNumber, teamSurvivedPlayerCounts[teamNumber - 1]);
				Log.Add(Log.Severity.Information, "The total score of the team was {0}.", teamScore);
		    }

		    Teams.CalculateRanks();
		    Players.CalculateRanks();
		}

		private void CalculateScoresSurvivalSolo()
		{
			foreach (var player in _players)
			{
				var hitsScored = 0;
				for (var playerNumber = 1; playerNumber <= TeamPlayerId.MaximumPlayerNumber; playerNumber++)
					hitsScored += player.TaggedPlayerCounts[playerNumber - 1];

				// 1 point per hit scored + 10 points per full minute survived
				player.Score = hitsScored + 10 * (int)player.SurviveTime.TotalMinutes;
			}

			Teams.CalculateRanks();
			Players.CalculateRanks();
		}

		private void CalculateScoresSurvivalTeams()
		{
			var teamCount = GameDefinition.TeamCount;
			var teamSurvivedPlayerCounts = new int[teamCount];
			var teamSurvivedPlayerScoreTotals = new int[teamCount];

			foreach (var player in _players)
			{
				var hitsScored = 0;
				for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
					for (var playerNumber = 1; playerNumber <= 8; playerNumber++)
						if (player.TeamPlayerId.TeamNumber != teamNumber)
							hitsScored += player.TaggedPlayerCounts[new TeamPlayerId(teamNumber, playerNumber).PlayerNumber - 1];

				// 1 point per enemy hit scored + 10 points per full minute survived
				player.Score = hitsScored + 10 * (int)player.SurviveTime.TotalMinutes;

				if (player.Survived)
				{
					teamSurvivedPlayerCounts[player.TeamPlayerId.TeamNumber - 1]++;
					teamSurvivedPlayerScoreTotals[player.TeamPlayerId.TeamNumber - 1] += player.Score;
				}
			}

			for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
			{
				var teamScore = (teamSurvivedPlayerCounts[teamNumber - 1] << 10) + (teamSurvivedPlayerScoreTotals[teamNumber - 1] << 2);
				Teams.Team(teamNumber).Score = teamScore;
				Log.Add(Log.Severity.Information, "Team {0} had {1} surviving players.", teamNumber, teamSurvivedPlayerCounts[teamNumber - 1]);
				Log.Add(Log.Severity.Information, "The total score of the surviving players was {0}.", teamSurvivedPlayerScoreTotals[teamNumber - 1]);
				Log.Add(Log.Severity.Information, "Team {0}'s final score was {1}.", teamNumber, teamScore);
			}

			Teams.CalculateRanks();
			Players.CalculateRanks();
		}

		private void CalculateScoresHuntTheTagMasterTwoTeams()
		{
			var teamCount = GameDefinition.TeamCount;
			var teamSurvivedPlayerCounts = new int[teamCount];
			var teamScoreTotals = new int[teamCount];

			// Calculate player scores - 1 point per hit scored on relevant opponents, hits taken ignored
			foreach (var player in _players)
			{
				var score = 0;

				for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
				{
					for (var teamPlayerNumber = 1; teamPlayerNumber <= 8; teamPlayerNumber++)
					{
						var playerNumber = new TeamPlayerId(teamNumber, teamPlayerNumber).PlayerNumber;

						if (player.TeamPlayerId.TeamNumber == 1) // Tag Master scores hits on anyone
						{
							if (teamNumber != 1)
								score += player.TaggedPlayerCounts[playerNumber - 1];
						}
						else // Hunters score hits on the Tag Master only
						{
							if (teamNumber == 1)
								score += player.TaggedPlayerCounts[playerNumber - 1];
						}
					}
				}

				player.Score = score;
				teamScoreTotals[player.TeamPlayerId.TeamNumber - 1] += score;

				if (player.Survived) teamSurvivedPlayerCounts[player.TeamPlayerId.TeamNumber - 1]++;
			}

			// Calculate team scores
			for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
			{
				var teamScore = teamScoreTotals[teamNumber - 1];
				Teams.Team(teamNumber).Score = teamScore;
				Log.Add(Log.Severity.Information, "Team {0} had {1} surviving players.", teamNumber, teamSurvivedPlayerCounts[teamNumber - 1]);
				Log.Add(Log.Severity.Information, "The total score of the team was {0}.", teamScore);
			}

			Teams.CalculateRanks();
			Players.CalculateRanks();

			// Adjust the ranks based on whether or not the Tag Master survived
			if (Players.Player(new TeamPlayerId(1, 1)).Survived) // The Tag Master survived
			{
				Teams.Team(1).Rank = 1;

				if (Teams.Team(2).Rank < Teams.Team(3).Rank)
				{
					Teams.Team(2).Rank = 2;
					Teams.Team(3).Rank = 3;
				}
				else if (Teams.Team(2).Rank == Teams.Team(3).Rank)
				{
					Teams.Team(2).Rank = 2;
					Teams.Team(3).Rank = 2;
				}
				else
				{
					Teams.Team(2).Rank = 3;
					Teams.Team(3).Rank = 2;
				}
			}
			else // the Tag Master did not survive
			{
				Teams.Team(1).Rank = 3;

				if (Teams.Team(2).Rank < Teams.Team(3).Rank)
				{
					Teams.Team(2).Rank = 1;
					Teams.Team(3).Rank = 2;
				}
				else if (Teams.Team(2).Rank == Teams.Team(3).Rank)
				{
					Teams.Team(2).Rank = 1;
					Teams.Team(3).Rank = 1;
				}
				else
				{
					Teams.Team(2).Rank = 2;
					Teams.Team(3).Rank = 1;
				}
			}
		}

        private bool ChangeState(HostingStates state)
        {
	        var previousState = HostingState;

	        switch (state)
	        {
		        case HostingStates.Idle:
			        _players.Clear();
			        break;
		        case HostingStates.Countdown:
					if (previousState != HostingStates.Adding &&
					    previousState != HostingStates.AcknowledgePlayerAssignment) return false;
			        Log.Add(Log.Severity.Information, "Starting countdown");
			        _stateChangeTimeout = DateTime.Now.AddSeconds(_gameDefinition.CountdownTimeSeconds);
			        SendLtarReleaseToAllConfirmedPlayers("countdown-start");
			        break;
		        case HostingStates.ResendCountdown:
					if (previousState != HostingStates.Playing) return false;
					Log.Add(Log.Severity.Information, "Resending countdown");
			        _resendCountdownPlayingStateChangeTimeout = _stateChangeTimeout;
			        _stateChangeTimeout = DateTime.Now.AddSeconds(_gameDefinition.ResendCountdownTimeSeconds);
			        break;
		        case HostingStates.Adding:
					Log.Add(Log.Severity.Information, "Adding players");

					if (previousState != HostingStates.AcknowledgePlayerAssignment)
			        {
				        Teams.Clear();

						var teamCount = _gameDefinition.IsTeamGame ? _gameDefinition.TeamCount : 3;
						for (var teamNumber = 1; teamNumber <= teamCount; teamNumber++)
						{
							Teams.Add(new Team(teamNumber));
						}

				        _joinStates.Clear();
			        }
			        break;
		        case HostingStates.AcknowledgePlayerAssignment:
			        Log.Add(Log.Severity.Debug, "Waiting for AcknowledgePlayerAssignment packet.");
			        _stateChangeTimeout = DateTime.Now.AddSeconds(AcknowledgePlayerAssignmentTimeoutSeconds);
			        break;
		        case HostingStates.Playing:
					switch (previousState)
			        {
				        case HostingStates.Countdown:
					        Log.Add(Log.Severity.Information, "Starting Game");
					        _stateChangeTimeout = DateTime.Now.AddMinutes(_gameDefinition.GameTimeMinutes);
					        break;
				        case HostingStates.ResendCountdown:
					        Log.Add(Log.Severity.Information, "Continuing Game");
					        _stateChangeTimeout = _resendCountdownPlayingStateChangeTimeout;
					        break;
			        }
			        break;
			case HostingStates.Summary:
				Log.Add(Log.Severity.Information, "Debriefing");
				// Debrief is driven by RequestTagReport. AssignPlayerSuccess (0x87) is
				// an assignment-stage release, not a debrief preamble. The former release
				// burst followed by another directed release made roster player 2 receive
				// two back-to-back releases before the first report request.
				_debriefPlayerSequence = -1;
				_currentDebriefPlayer = null;
				break;
		        case HostingStates.GameOver:
			        Log.Add(Log.Severity.Information, "Debriefing completed");
			        _rankReportTeamNumber = 1;
			        break;
		        default:
			        return false;
	        }

	        HostingState = state;
			_nextAnnouncement = DateTime.Now;

			SyncExplicitStateFromHostingState("ChangeState");

	        OnHostingStateChanged(new HostingStateChangedEventArgs(previousState, state));
		
            return true;
        }

		#region Public Interface

        /// <summary>
        /// Sets per-slot preferred team assignments. Index 0 = first player to join, etc.
        /// Null entries use normal auto-assignment.
        /// </summary>
        public void SetPlannedTeamPreferences(IEnumerable<int?> preferences)
        {
            _plannedTeamPreferences.Clear();
            _plannedTeamPreferences.AddRange(preferences);
        }

		/// <summary>
		/// Sets per-slot HP overrides to use during the lobby (Adding state).
		/// Call this before StartServer. Index 0 = first player to join, etc.
		/// While in Adding state, the announcement broadcast uses the HP for the
		/// next expected joiner, so their tagger picks up the right value before
		/// completing the handshake. Null entries use the global Tags value.
		/// </summary>
		public void SetPlannedTagsOverrides(IEnumerable<int?> overrides)
		{
			_plannedTagsOverrides.Clear();
			_plannedTagsOverrides.AddRange(overrides);
		}

		/// <summary>
		/// Sets per-slot shields overrides (seconds). Call before StartServer.
		/// Null entries use the global Shields value.
		/// </summary>
		public void SetPlannedShieldsOverrides(IEnumerable<int?> overrides)
		{
			_plannedShieldsOverrides.Clear();
			_plannedShieldsOverrides.AddRange(overrides);
		}

		/// <summary>
		/// Sets per-slot reloads overrides (number of reload clips). Call before StartServer.
		/// Null entries use the global Reloads value.
		/// </summary>
		public void SetPlannedReloadsOverrides(IEnumerable<int?> overrides)
		{
			_plannedReloadsOverrides.Clear();
			_plannedReloadsOverrides.AddRange(overrides);
		}

		public void SetPlannedAutoReloadOverrides(IEnumerable<bool?> overrides)
		{
			_plannedAutoReloadOverrides.Clear();
			_plannedAutoReloadOverrides.AddRange(overrides);
		}

		public void SetPlannedFullAutoOverrides(IEnumerable<bool?> overrides)
		{
			_plannedFullAutoOverrides.Clear();
			_plannedFullAutoOverrides.AddRange(overrides);
		}

		public void SetPlannedStartingAmmoOverrides(IEnumerable<int?> overrides)
		{
			_plannedStartingAmmoOverrides.Clear();
			_plannedStartingAmmoOverrides.AddRange(overrides);
		}

		public void SetPlannedSleepTimeoutOverrides(IEnumerable<int?> overrides)
		{
			_plannedSleepTimeoutOverrides.Clear();
			_plannedSleepTimeoutOverrides.AddRange(overrides);
		}

		public void StartServer(GameDefinition gameDefinition)
		{
            if (HostingState != HostingStates.Idle) return;

			_gameDefinition = gameDefinition;
			// GameId is assigned by the caller (MainForm.BuildGameDefinition) - do not overwrite it here.

            ChangeState(HostingStates.Adding);
        }

        public void EndGame()
        {
            switch (HostingState)
            {
                case HostingStates.Adding:
                case HostingStates.AcknowledgePlayerAssignment:
                case HostingStates.Countdown:
                    Log.Add(Log.Severity.Information, "Ending host session before game start.");
                    ChangeState(HostingStates.Idle);
                    _stateChangeTimeout = new DateTime(0);
                    break;

                case HostingStates.Playing:
                case HostingStates.ResendCountdown:
                    Log.Add(Log.Severity.Information, "Ending live game and moving to summary.");
                    ChangeState(HostingStates.Summary);
                    break;

                case HostingStates.Summary:
                    Log.Add(Log.Severity.Information, "Forcing summary complete and moving to game over.");
                    foreach (var player in Players)
                    {
                        if (!player.AllTagReportsReceived())
                            DropPlayer(player.TeamPlayerId);
                    }
                    ChangeState(HostingStates.GameOver);
                    break;

                case HostingStates.GameOver:
                    Log.Add(Log.Severity.Information, "Game is already in GameOver.");
                    break;

                case HostingStates.Idle:
                default:
                    Log.Add(Log.Severity.Information, "EndGame pressed while already idle.");
                    break;
            }
        }

        public void DelayGame(int seconds)
		{
			_stateChangeTimeout = _stateChangeTimeout.AddSeconds(seconds);
        }

	    public void Next()
		{
            switch (HostingState)
			{
				case HostingStates.Adding:
					ChangeState(HostingStates.Countdown);
					break;
				case HostingStates.Playing:
					ChangeState(HostingStates.Summary);
					break;
				case HostingStates.Summary:
					foreach (var player in Players)
					{
						if (!player.AllTagReportsReceived()) DropPlayer(player.TeamPlayerId);
					}
					ChangeState(HostingStates.GameOver);
					break;
				case HostingStates.Idle:
				case HostingStates.AcknowledgePlayerAssignment:
				case HostingStates.Countdown:
				case HostingStates.ResendCountdown:
				case HostingStates.GameOver:
				default:
					Log.Add(Log.Severity.Warning, "Next cannot be used while in the {0} hosting state.", HostingState);
					break;
            }
        }

        public bool StartCountdown()
		{
            return ChangeState(HostingStates.Countdown);
        }

		public void ResendCountdown()
		{
			ChangeState(HostingStates.ResendCountdown);
		}

        public string GetGameStateText()
        {
            switch (HostingState)
			{
				case HostingStates.Adding:
				case HostingStates.AcknowledgePlayerAssignment:
					return "Adding Players";
				case HostingStates.Countdown:
					return "Countdown to Game Start";
				case HostingStates.ResendCountdown:
					return "Resending Countdown";
				case HostingStates.Playing:
					return "Game in Progress";
				case HostingStates.Summary:
					return "Debriefing Players";
				case HostingStates.GameOver:
					return "Game Over";
				default:
					return "Not In a Game";
            }
        }

	    public string GetCountdown()
	    {
			string countdown;
		    var timeRemaining = (_stateChangeTimeout - DateTime.Now).ToString(@"m\:ss");
		    switch (HostingState)
		    {
			    case HostingStates.Adding:
			    case HostingStates.AcknowledgePlayerAssignment:
				    var needed = (MinimumPlayerCount - _players.Count);
				    if (needed > 0)
				    {
					    countdown = "Waiting for " + needed + " more players";
				    }
				    else
				    {
					    countdown = "Ready to start countdown";
				    }
				    break;
			    case HostingStates.Countdown:
				    countdown = string.Format("{0} until game start", timeRemaining);
				    break;
				case HostingStates.ResendCountdown:
					countdown = string.Format("{0}", timeRemaining);
					break;
				case HostingStates.Playing:
				    countdown = string.Format("{0} until game end", timeRemaining);
				    break;
			    case HostingStates.Summary:
				    countdown = "Waiting for all players to check in";
				    break;
			    case HostingStates.GameOver:
				    countdown = "All players may now receive scores";
				    break;
			    default:
				    countdown = "Waiting";
				    break;
		    }
		    return countdown;
        }

        /// <summary>Returns just the mm:ss time remaining for the clock display.</summary>
        public string GetTimeRemaining()
        {
            var remaining = _stateChangeTimeout - DateTime.Now;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            switch (HostingState)
            {
                case HostingStates.Countdown:
                case HostingStates.ResendCountdown:
                case HostingStates.Playing:
                    return remaining.ToString(@"m\:ss");
                default:
                    return "";
            }
        }

        public bool SetPlayerName(TeamPlayerId teamPlayerId, string name)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null)
			{
                Log.Add(Log.Severity.Warning, "Player not found.");
                return false;
            }

            player.Name = name;

            return true;
        }

        /// <summary>
        /// Sets a per-player HP override recorded on the Player object after they join.
        /// The actual HP the tagger uses is determined by the AnnounceGame broadcast it
        /// heard before joining, controlled via SetPlannedTagsOverrides() before StartServer.
        /// This value is used for score reporting purposes.
        /// </summary>
        public bool SetPlayerTagsOverride(TeamPlayerId teamPlayerId, int? tagsOverride)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null) { Log.Add(Log.Severity.Warning, "Player not found."); return false; }
            player.TagsOverride = tagsOverride;
            Log.Add(Log.Severity.Information, "Set HP override for {0} to {1}.", player.NameAndNumber,
                tagsOverride.HasValue ? tagsOverride.Value.ToString() : "global default");
            return true;
        }

        /// <summary>Sets a per-player shields override (seconds). Null = use global.</summary>
        public bool SetPlayerShieldsOverride(TeamPlayerId teamPlayerId, int? shieldsOverride)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null) { Log.Add(Log.Severity.Warning, "Player not found."); return false; }
            player.ShieldsOverride = shieldsOverride;
            Log.Add(Log.Severity.Information, "Set shields override for {0} to {1}.", player.NameAndNumber,
                shieldsOverride.HasValue ? shieldsOverride.Value.ToString() : "global default");
            return true;
        }

        /// <summary>Sets a per-player reloads override (number of clips). Null = use global.</summary>
        public bool SetPlayerReloadsOverride(TeamPlayerId teamPlayerId, int? reloadsOverride)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null) { Log.Add(Log.Severity.Warning, "Player not found."); return false; }
            player.ReloadsOverride = reloadsOverride;
            Log.Add(Log.Severity.Information, "Set reloads override for {0} to {1}.", player.NameAndNumber,
                reloadsOverride.HasValue ? reloadsOverride.Value.ToString() : "global default");
            return true;
        }

        public bool SetPlayerAutoReloadOverride(TeamPlayerId teamPlayerId, bool? autoReload)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null) { Log.Add(Log.Severity.Warning, "Player not found."); return false; }
            player.AutoReloadOverride = autoReload;
            return true;
        }

        public bool SetPlayerFullAutoOverride(TeamPlayerId teamPlayerId, bool? fullAuto)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null) { Log.Add(Log.Severity.Warning, "Player not found."); return false; }
            player.FullAutoOverride = fullAuto;
            return true;
        }

        public bool SetPlayerStartingAmmoOverride(TeamPlayerId teamPlayerId, int? startingAmmo)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null) { Log.Add(Log.Severity.Warning, "Player not found."); return false; }
            player.StartingAmmoOverride = startingAmmo;
            return true;
        }

        public bool SetPlayerSleepTimeoutOverride(TeamPlayerId teamPlayerId, int? sleepTimeout)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null) { Log.Add(Log.Severity.Warning, "Player not found."); return false; }
            player.SleepTimeoutOverride = sleepTimeout;
            return true;
        }


        public bool MovePlayerToTeam(TeamPlayerId teamPlayerId, int requestedTeamNumber)
        {
            if (!_gameDefinition.IsTeamGame) return false;
            if (requestedTeamNumber < 1 || requestedTeamNumber > _gameDefinition.TeamCount) return false;

            // Only allow manual team changes before the game starts.
            if (HostingState != HostingStates.Adding && HostingState != HostingStates.AcknowledgePlayerAssignment)
                return false;

            var player = Players.Player(teamPlayerId);
            if (player == null)
            {
                Log.Add(Log.Severity.Warning, "Player not found.");
                return false;
            }

            var assignedPlayerNumber = 0;
            for (var playerNumber = 1; playerNumber <= 8; playerNumber++)
            {
                var existing = _players.Player(new TeamPlayerId(requestedTeamNumber, playerNumber));
                if (existing == null || ReferenceEquals(existing, player))
                {
                    assignedPlayerNumber = playerNumber;
                    break;
                }
            }

            if (assignedPlayerNumber == 0)
            {
                Log.Add(Log.Severity.Information, "Cannot move player. Team {0} is full.", requestedTeamNumber);
                return false;
            }

            var oldTeamPlayerId = player.TeamPlayerId;
            var newTeamPlayerId = new TeamPlayerId(requestedTeamNumber, assignedPlayerNumber);

            if (oldTeamPlayerId == newTeamPlayerId) return true;

            if (player.Team != null)
            {
                player.Team.Players.Remove(player);
            }

            player.TeamPlayerId = newTeamPlayerId;
            player.Team = Teams.Team(requestedTeamNumber);
            if (player.Team != null && !player.Team.Players.Contains(player))
            {
                player.Team.Players.Add(player);
            }

            SendPlayerAssignment(player.TeamPlayerId);

            Log.Add(Log.Severity.Information,
                "Moved tagger 0x{0:X2} from {1} to {2}.",
                (byte)player.TaggerId,
                oldTeamPlayerId.ToString(_gameDefinition.IsTeamGame),
                newTeamPlayerId.ToString(_gameDefinition.IsTeamGame));

            OnPlayerListChanged(new PlayerListChangedEventArgs(Players));
            return true;
        }

        public void DropPlayer(TeamPlayerId teamPlayerId)
        {
			var player = Players.Player(teamPlayerId);
            if (player == null)
			{
                Log.Add(Log.Severity.Warning, "Player not found.");
                return;
            }

			var changed = false;
			switch (HostingState)
			{
				case HostingStates.Adding:
				case HostingStates.AcknowledgePlayerAssignment:
				case HostingStates.Countdown:
					_players.Remove(player.TeamPlayerId);
					changed = true;
					break;
				case HostingStates.Playing:
				case HostingStates.ResendCountdown:
				case HostingStates.Summary:
					if (player.AllTagReportsReceived()) return;
					player.Dropped = true;
					player.SurviveTime = new TimeSpan();
					player.Survived = false;
					changed = true;
					break;
				case HostingStates.Idle:
				case HostingStates.GameOver:
				default:
					Log.Add(Log.Severity.Warning, "Players cannot be dropped while in the {0} hosting state.", HostingState);
					break;
			}

	        if (changed) OnPlayerListChanged(new PlayerListChangedEventArgs(Players));
        }

        public void Update()
        {
			string explicitTimeoutNote;
			if (_explicitStateMachine.OnTick(DateTime.UtcNow, out explicitTimeoutNote))
			{
				if (!string.IsNullOrWhiteSpace(explicitTimeoutNote))
				{
					Log.Add(Log.Severity.Debug, "Explicit state machine timeout: {0}", explicitTimeoutNote);
				}
			}

			switch (HostingState)
			{
				case HostingStates.Idle:
					break;
				case HostingStates.Adding:
					CheckAssignPlayerFailed();

					if (DateTime.Now >= _nextAnnouncement)
					{
						// If the next expected joiner has per-player overrides,
						// temporarily broadcast with their values so their tagger picks them up.
						var nextJoinIndex = _players.Count;
						int?  nextPlayerHp         = (nextJoinIndex < _plannedTagsOverrides.Count)           ? _plannedTagsOverrides[nextJoinIndex]           : null;
						int?  nextPlayerShields     = (nextJoinIndex < _plannedShieldsOverrides.Count)       ? _plannedShieldsOverrides[nextJoinIndex]       : null;
						int?  nextPlayerReloads     = (nextJoinIndex < _plannedReloadsOverrides.Count)       ? _plannedReloadsOverrides[nextJoinIndex]       : null;
						bool? nextPlayerAutoReload  = (nextJoinIndex < _plannedAutoReloadOverrides.Count)    ? _plannedAutoReloadOverrides[nextJoinIndex]    : null;
						bool? nextPlayerFullAuto    = (nextJoinIndex < _plannedFullAutoOverrides.Count)      ? _plannedFullAutoOverrides[nextJoinIndex]      : null;
						int?  nextPlayerAmmo        = (nextJoinIndex < _plannedStartingAmmoOverrides.Count)  ? _plannedStartingAmmoOverrides[nextJoinIndex]  : null;
						int?  nextPlayerSleep       = (nextJoinIndex < _plannedSleepTimeoutOverrides.Count)  ? _plannedSleepTimeoutOverrides[nextJoinIndex]  : null;

						var savedTags        = _gameDefinition.Tags;
						var savedShields     = _gameDefinition.Shields;
						var savedReloads     = _gameDefinition.Reloads;
						var savedAutoReload  = _gameDefinition.AutomaticReload;
						var savedFullAuto    = _gameDefinition.AllowFullAuto;
						var savedAmmo        = _gameDefinition.StartingAmmo;
						var savedSleep       = _gameDefinition.SleepTimeoutMinutes;

						try
						{
							if (nextPlayerHp.HasValue)         _gameDefinition.Tags                = nextPlayerHp.Value;
							if (nextPlayerShields.HasValue)    _gameDefinition.Shields             = nextPlayerShields.Value;
							if (nextPlayerReloads.HasValue)    _gameDefinition.Reloads             = nextPlayerReloads.Value;
							if (nextPlayerAutoReload.HasValue) _gameDefinition.AutomaticReload     = nextPlayerAutoReload.Value;
							if (nextPlayerFullAuto.HasValue)   _gameDefinition.AllowFullAuto       = nextPlayerFullAuto.Value;
							if (nextPlayerAmmo.HasValue)       _gameDefinition.StartingAmmo        = nextPlayerAmmo.Value;
							if (nextPlayerSleep.HasValue)      _gameDefinition.SleepTimeoutMinutes = nextPlayerSleep.Value;

							var packet = _gameDefinition.UseLtarEnhancedProtocol
								? PacketPacker.AnnounceGameEnhanced(GameDefinition)
								: PacketPacker.AnnounceGame(GameDefinition);
							Protocol.TransmitPacket(packet);
						}
						finally
						{
							// Never leave a temporary per-player override in the shared game
							// definition if packet construction or transmission throws.
							_gameDefinition.Tags                = savedTags;
							_gameDefinition.Shields             = savedShields;
							_gameDefinition.Reloads             = savedReloads;
							_gameDefinition.AutomaticReload     = savedAutoReload;
							_gameDefinition.AllowFullAuto       = savedFullAuto;
							_gameDefinition.StartingAmmo        = savedAmmo;
							_gameDefinition.SleepTimeoutMinutes = savedSleep;
						}

						_nextAnnouncement = DateTime.Now.AddMilliseconds(GameAnnouncementFrequencyMilliseconds);
					}
					break;
				case HostingStates.AcknowledgePlayerAssignment:
					if (DateTime.Now >= _stateChangeTimeout) ChangeState(HostingStates.Adding);
					break;
				case HostingStates.Countdown:
				case HostingStates.ResendCountdown:
					// Note: ResendCountdown behaviour in zone games is not fully tested.
					if (HostingState == HostingStates.ResendCountdown && DateTime.Now >= _resendCountdownPlayingStateChangeTimeout)
					{
						ChangeState(HostingStates.Summary);
					}
					else if (DateTime.Now >= _stateChangeTimeout)
					{
						ChangeState(HostingStates.Playing);
					}
					else if (DateTime.Now >= _nextAnnouncement)
					{
						var remainingSeconds = (byte)((_stateChangeTimeout - DateTime.Now).TotalSeconds);

						// There does not appear to be a reason to tell the gun the number of players
						// ahead of time.  It only prevents those players from joining midgame.  The
						// score report is bitmasked and only reports non-zero scores.
						const int playerCountTeam1 = 8;
						const int playerCountTeam2 = 8;
						const int playerCountTeam3 = 8;

						var team1Mask = GetCountdownMaskForTeam(1);
						var team2Mask = GetCountdownMaskForTeam(2);
						var team3Mask = GetCountdownMaskForTeam(3);

						var packet = _gameDefinition.UseLtarEnhancedProtocol
							? PacketPacker.CountdownEnhanced(GameDefinition.GameId, remainingSeconds, team1Mask, team2Mask, team3Mask)
							: PacketPacker.Countdown(GameDefinition.GameId, remainingSeconds, playerCountTeam1, playerCountTeam2,
						                                    playerCountTeam3);
						Protocol.TransmitPacket(packet);

						Log.Add(Log.Severity.Information, "T-{0}", remainingSeconds);

						_nextAnnouncement = DateTime.Now.AddSeconds(1);
					}
					break;
				case HostingStates.Playing:
					if (DateTime.Now >= _stateChangeTimeout)
					{
						ChangeState(HostingStates.Summary);
					}
					else if (DateTime.Now >= _nextAnnouncement)
					{
						switch (_gameDefinition.GameType)
						{
							case GameType.OwnTheZone:
							case GameType.OwnTheZoneTwoTeams:
							case GameType.OwnTheZoneThreeTeams:
								Protocol.TransmitSignature(PacketPacker.ZoneBeacon(0, ZoneType.ContestedZone));
								_nextAnnouncement = DateTime.Now.AddMilliseconds(ZoneBeaconFrequencyMilliseconds);
								break;
							case GameType.Respawn: // RESP
							case GameType.RespawnTwoTeams: // 2TRS
							case GameType.RespawnThreeTeams: // 3TRS
								Protocol.TransmitSignature(PacketPacker.ZoneBeacon(0, ZoneType.TeamZone));
								_nextAnnouncement = DateTime.Now.AddMilliseconds(ZoneBeaconFrequencyMilliseconds);
								break;
						}
					}
					break;
				case HostingStates.Summary:
					if (DateTime.Now >= _nextAnnouncement)
					{
						var undebriefed = new List<Player>();
						foreach (var player in _players)
						{
							if (!player.AllTagReportsReceived()) undebriefed.Add(player);
						}

						Player nextDebriefPlayer = null;
						if (undebriefed.Count > 0)
						{
							_debriefPlayerSequence = _debriefPlayerSequence < Int32.MaxValue ? _debriefPlayerSequence + 1 : 0;
							nextDebriefPlayer = undebriefed[_debriefPlayerSequence%undebriefed.Count];
						}

						_currentDebriefPlayer = nextDebriefPlayer;

						if (nextDebriefPlayer == null)
						{
							Log.Add(Log.Severity.Information, "All players debriefed");

							CalculateScores();
							PrintScoreReport();

							ChangeState(HostingStates.GameOver);
							break;
						}

						var reportGroupCount = _gameDefinition.IsTeamGame ? _gameDefinition.TeamCount : 3;
						var packet = PacketPacker.RequestTagReport(GameDefinition.GameId,
							nextDebriefPlayer.TeamPlayerId, reportGroupCount,
							_gameDefinition.UseLtarEnhancedProtocol);
						Protocol.TransmitPacket(packet);
						Log.Add(Log.Severity.Information, "Requested tag report from {0}.",
							nextDebriefPlayer.NameAndNumber);

						_nextAnnouncement = DateTime.Now.AddSeconds(RequestTagReportFrequencySeconds);
					}
					break;
				case HostingStates.GameOver:
					if (DateTime.Now >= _nextAnnouncement)
					{
						_nextAnnouncement = DateTime.Now.AddSeconds(GameOverAnnouncementFrequencySeconds);

						var team = Teams.Team(_rankReportTeamNumber);

						var maxTeamNumber = GameDefinition.IsTeamGame ? _gameDefinition.TeamCount : 3;
						_rankReportTeamNumber = GetNextRankReportTeamNumber(_rankReportTeamNumber, maxTeamNumber, Teams);

						if (team == null) break;

					    var teamRank = GameDefinition.IsTeamGame ? team.Rank : 0;

						var playerRanks = new int[8];
						foreach (var player in team.Players)
						{
							playerRanks[player.TeamPlayerId.TeamPlayerNumber - 1] = player.Rank;
						}

						var packet = PacketPacker.RankReport(GameDefinition.GameId, team.Number, teamRank, playerRanks);
						Protocol.TransmitPacket(packet);
					}
					break;
			}
		}

		private static int GetNextRankReportTeamNumber(int currentTeamNumber, int maxTeamNumber, TeamCollection teams)
		{
			var nextTeamNumber = currentTeamNumber + 1;
			var checkedTeamCount = 0;
			Team team = null;

			while (true)
			{
				if (nextTeamNumber > maxTeamNumber) nextTeamNumber = 1;
				team = teams.Team(nextTeamNumber);
				checkedTeamCount++;

				if (team != null && team.Players != null && team.Players.Count > 0) break;

				if (checkedTeamCount >= maxTeamNumber)
				{
					nextTeamNumber = 0;
					break;
				}
			}

			return nextTeamNumber;
		}

	    private void CheckAssignPlayerFailed()
	    {
		    var removeKeys = new List<UInt16>();

		    foreach (var taggerId in _joinStates.Keys)
		    {
				var joinState = _joinStates[taggerId];

			    if (!joinState.Failed)
			    {
					if (DateTime.Now < joinState.AssignPlayerSendTime.AddSeconds(AcknowledgePlayerAssignmentTimeoutSeconds)) continue;

					Log.Add(Log.Severity.Warning, "Timed out after {0} seconds waiting for AcknowledgePlayerAssignment from tagger 0x{1:X2} for game {2}.", AcknowledgePlayerAssignmentTimeoutSeconds, taggerId, GameDefinition.GameId);

					joinState.Failed = true;
				    joinState.AssignPlayerFailSendCount = 0;
					joinState.LastAssignPlayerFailSendTime = new DateTime(0);
				}

			    if (!joinState.Failed) continue;

			    if (DateTime.Now < joinState.LastAssignPlayerFailSendTime.AddMilliseconds(AssignPlayerFailedFrequencyMilliseconds)) continue;

			    var packet = _gameDefinition.UseLtarEnhancedProtocol ? PacketPacker.AssignPlayerFailedEnhanced(joinState.GameId, taggerId) : PacketPacker.AssignPlayerFailed(joinState.GameId, taggerId);
				Protocol.TransmitPacket(packet);

				joinState.AssignPlayerFailSendCount++;
			    joinState.LastAssignPlayerFailSendTime = DateTime.Now;

				if (joinState.AssignPlayerFailSendCount >= AssignPlayerFailedSendCount) removeKeys.Add(taggerId);
		    }

		    foreach (var key in removeKeys)
		    {
			    var player = _joinStates[key].Player;
				Players.Remove(player.TeamPlayerId);
				_joinStates.Remove(key);
		    }

		    if (removeKeys.Count > 0)
		    {
				OnPlayerListChanged(new PlayerListChangedEventArgs(Players));
			}
		}

		private void SendPlayerAssignment(TeamPlayerId teamPlayerId)
		{
			if (HostingState != HostingStates.Adding && HostingState != HostingStates.AcknowledgePlayerAssignment) return;

			var gameId = _gameDefinition.GameId;
			var player = Players.Player(teamPlayerId);
			var taggerId = player.TaggerId;

			var packet = _gameDefinition.UseLtarEnhancedProtocol ? PacketPacker.AssignPlayerEnhanced(gameId, taggerId, teamPlayerId) : PacketPacker.AssignPlayer(gameId, taggerId, teamPlayerId);
			Protocol.TransmitPacket(packet);
		}
		

		private int GetCountdownMaskForTeam(int teamNumber)
		{
			var mask = 0;
			foreach (var player in Players)
			{
				if (player.TeamPlayerId.TeamNumber != teamNumber) continue;
				var bit = player.TeamPlayerId.TeamPlayerNumber - 1;
				if (bit < 0 || bit > 7) continue;
				mask |= (1 << bit);
			}
			return mask;
		}

		#region Events
	    public class PlayerListChangedEventArgs : EventArgs
	    {
		    public PlayerListChangedEventArgs(IEnumerable<Player> players)
		    {
			    Players = players;
		    }

		    public IEnumerable<Player> Players { get; set; }
	    }

		public event EventHandler<PlayerListChangedEventArgs> PlayerListChanged;

	    protected virtual void OnPlayerListChanged(PlayerListChangedEventArgs e)
	    {
		    PlayerListChanged?.Invoke(this, e);
	    }

	    public class HostingStateChangedEventArgs : EventArgs
	    {
		    public HostingStateChangedEventArgs(HostingStates previousState, HostingStates state)
		    {
			    PreviousState = previousState;
			    State = state;
		    }

		    public HostingStates PreviousState { get; set; }
		    public HostingStates State { get; set; }
	    }

	    public event EventHandler<HostingStateChangedEventArgs> HostingStateChanged;

	    protected virtual void OnHostingStateChanged(HostingStateChangedEventArgs e)
	    {
		    HostingStateChanged?.Invoke(this, e);
	    }

	    #endregion

	    #endregion
    }
}
