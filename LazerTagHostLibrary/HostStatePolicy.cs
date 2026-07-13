using System;
using System.Collections.Generic;

namespace LazerTagHostLibrary.StateMachine
{
    public sealed class HostStatePolicy
    {
        public TimeSpan? Timeout { get; set; }
        public int MaxRetries { get; set; }
        public bool AcceptDuplicatePackets { get; set; }
        public HashSet<HostPacketKind> AcceptedPackets { get; } = new HashSet<HostPacketKind>();
        public HashSet<HostEventType> AcceptedEvents { get; } = new HashSet<HostEventType>();
    }

    public static class HostStatePolicies
    {
        public static Dictionary<HostSessionState, HostStatePolicy> CreateDefault()
        {
            return new Dictionary<HostSessionState, HostStatePolicy>
            {
                [HostSessionState.Idle] = new HostStatePolicy
                {
                    Timeout = null,
                    MaxRetries = 0,
                    AcceptDuplicatePackets = false,
                    AcceptedEvents = { HostEventType.StartHosting, HostEventType.Reset }
                },
                [HostSessionState.AddingPlayers] = new HostStatePolicy
                {
                    Timeout = null,
                    MaxRetries = 0,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents =
                    {
                        HostEventType.PlayerJoinObserved,
                        HostEventType.PlayerAssignmentSent,
                        HostEventType.CountdownStarted,
                        HostEventType.StopHosting,
                        HostEventType.FaultRaised
                    },
                    AcceptedPackets =
                    {
                        HostPacketKind.AnnounceGame,
                        HostPacketKind.AssignPlayer,
                        HostPacketKind.AssignPlayerSuccess
                    }
                },
                [HostSessionState.AwaitingAssignmentAck] = new HostStatePolicy
                {
                    Timeout = TimeSpan.FromSeconds(2),
                    MaxRetries = 3,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents =
                    {
                        HostEventType.AssignmentAcknowledged,
                        HostEventType.TimeoutExpired,
                        HostEventType.RetryRequested,
                        HostEventType.StopHosting,
                        HostEventType.FaultRaised
                    },
                    AcceptedPackets = { HostPacketKind.AssignPlayerSuccess }
                },
                [HostSessionState.Countdown] = new HostStatePolicy
                {
                    Timeout = null,
                    MaxRetries = 0,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents =
                    {
                        HostEventType.CountdownExpired,
                        HostEventType.StopHosting,
                        HostEventType.TransportLost,
                        HostEventType.FaultRaised
                    },
                    AcceptedPackets =
                    {
                        HostPacketKind.Countdown,
                        HostPacketKind.CountdownEnhanced
                    }
                },
                [HostSessionState.ResendCountdown] = new HostStatePolicy
                {
                    Timeout = null,
                    MaxRetries = 1,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents =
                    {
                        HostEventType.ResendCountdownExpired,
                        HostEventType.StopHosting,
                        HostEventType.FaultRaised
                    },
                    AcceptedPackets =
                    {
                        HostPacketKind.Countdown,
                        HostPacketKind.CountdownEnhanced
                    }
                },
                [HostSessionState.InGame] = new HostStatePolicy
                {
                    Timeout = null,
                    MaxRetries = 0,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents =
                    {
                        HostEventType.GameExpired,
                        HostEventType.DebriefRequested,
                        HostEventType.StopHosting,
                        HostEventType.TransportLost,
                        HostEventType.FaultRaised
                    },
                    AcceptedPackets = { HostPacketKind.ZoneBeacon }
                },
                [HostSessionState.AwaitingDebrief] = new HostStatePolicy
                {
                    Timeout = TimeSpan.FromSeconds(3),
                    MaxRetries = 4,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents =
                    {
                        HostEventType.DebriefSummaryReceived,
                        HostEventType.DebriefComplete,
                        HostEventType.TimeoutExpired,
                        HostEventType.RetryRequested,
                        HostEventType.StopHosting,
                        HostEventType.TransportLost,
                        HostEventType.FaultRaised
                    },
                    AcceptedPackets =
                    {
                        HostPacketKind.TagReport,
                        HostPacketKind.TeamTagReport
                    }
                },
                [HostSessionState.Recovery] = new HostStatePolicy
                {
                    Timeout = TimeSpan.FromSeconds(3),
                    MaxRetries = 3,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents =
                    {
                        HostEventType.TransportRestored,
                        HostEventType.TimeoutExpired,
                        HostEventType.StopHosting,
                        HostEventType.FaultRaised
                    }
                },
                [HostSessionState.GameOver] = new HostStatePolicy
                {
                    Timeout = null,
                    MaxRetries = 0,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents = { HostEventType.Reset, HostEventType.StopHosting },
                    AcceptedPackets = { HostPacketKind.RankReport }
                },
                [HostSessionState.Faulted] = new HostStatePolicy
                {
                    Timeout = null,
                    MaxRetries = 0,
                    AcceptDuplicatePackets = true,
                    AcceptedEvents = { HostEventType.Reset, HostEventType.StopHosting }
                }
            };
        }
    }
}
