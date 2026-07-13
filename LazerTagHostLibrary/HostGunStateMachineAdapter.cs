using System;

namespace LazerTagHostLibrary.StateMachine
{
    /// <summary>
    /// Small adapter between the existing HostGun.HostingStates enum and the new explicit state machine.
    /// This does not change HostGun behavior by itself; it gives you a low-risk place to start wiring
    /// the new state machine into existing code.
    /// </summary>
    public static class HostGunStateMachineAdapter
    {
        public static HostSessionState ToExplicitState(HostGun.HostingStates state)
        {
            switch (state)
            {
                case HostGun.HostingStates.Idle:
                    return HostSessionState.Idle;
                case HostGun.HostingStates.Adding:
                    return HostSessionState.AddingPlayers;
                case HostGun.HostingStates.AcknowledgePlayerAssignment:
                    return HostSessionState.AwaitingAssignmentAck;
                case HostGun.HostingStates.Countdown:
                    return HostSessionState.Countdown;
                case HostGun.HostingStates.ResendCountdown:
                    return HostSessionState.ResendCountdown;
                case HostGun.HostingStates.Playing:
                    return HostSessionState.InGame;
                case HostGun.HostingStates.Summary:
                    return HostSessionState.AwaitingDebrief;
                case HostGun.HostingStates.GameOver:
                    return HostSessionState.GameOver;
                default:
                    return HostSessionState.Faulted;
            }
        }

        public static HostGun.HostingStates? ToHostGunState(HostSessionState state)
        {
            switch (state)
            {
                case HostSessionState.Idle:
                    return HostGun.HostingStates.Idle;
                case HostSessionState.AddingPlayers:
                    return HostGun.HostingStates.Adding;
                case HostSessionState.AwaitingAssignmentAck:
                    return HostGun.HostingStates.AcknowledgePlayerAssignment;
                case HostSessionState.Countdown:
                    return HostGun.HostingStates.Countdown;
                case HostSessionState.ResendCountdown:
                    return HostGun.HostingStates.ResendCountdown;
                case HostSessionState.InGame:
                    return HostGun.HostingStates.Playing;
                case HostSessionState.AwaitingDebrief:
                    return HostGun.HostingStates.Summary;
                case HostSessionState.GameOver:
                    return HostGun.HostingStates.GameOver;
                default:
                    return null;
            }
        }

        public static HostPacketKind PacketKindFrom(PacketType packetType)
        {
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
                case PacketType.AnnounceGameEnhanced:
                    return HostPacketKind.AnnounceGame;

                case PacketType.AssignPlayer:
                case PacketType.AssignPlayerEnhanced:
                    return HostPacketKind.AssignPlayer;

                case PacketType.AcknowledgePlayerAssignment:
                case PacketType.AcknowledgePlayerAssignmentEnhanced:
                case PacketType.AssignPlayerSuccessEnhanced:
                    return HostPacketKind.AssignPlayerSuccess;

                case PacketType.AnnounceCountdown:
                    return HostPacketKind.Countdown;

                case PacketType.AnnounceCountdownEnhanced:
                    return HostPacketKind.CountdownEnhanced;

                case PacketType.SingleTagReport:
                case PacketType.TagSummary:
                    return HostPacketKind.TagReport;

                case PacketType.TeamOneTagReport:
                case PacketType.TeamTwoTagReport:
                case PacketType.TeamThreeTagReport:
                    return HostPacketKind.TeamTagReport;

                case PacketType.RankReport:
                    return HostPacketKind.RankReport;

                default:
                    return HostPacketKind.Unknown;
            }
        }
    }
}