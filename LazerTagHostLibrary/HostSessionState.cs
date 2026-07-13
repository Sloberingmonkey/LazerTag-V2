using System;

namespace LazerTagHostLibrary.StateMachine
{
    /// <summary>
    /// Higher-level host session states. These map closely to the existing HostGun.HostingStates,
    /// but make the intent of each phase more explicit.
    /// </summary>
    public enum HostSessionState
    {
        Idle,
        AddingPlayers,
        AwaitingAssignmentAck,
        Countdown,
        ResendCountdown,
        InGame,
        AwaitingDebrief,
        Recovery,
        GameOver,
        Faulted
    }

    public enum HostEventType
    {
        StartHosting,
        StopHosting,
        PlayerJoinObserved,
        PlayerAssignmentSent,
        AssignmentAcknowledged,
        CountdownStarted,
        CountdownExpired,
        ResendCountdownStarted,
        ResendCountdownExpired,
        GameStarted,
        GameExpired,
        DebriefRequested,
        DebriefSummaryReceived,
        DebriefComplete,
        TransportLost,
        TransportRestored,
        TimeoutExpired,
        RetryRequested,
        FaultRaised,
        Reset
    }

    public enum HostPacketKind
    {
        Unknown,
        AnnounceGame,
        AssignPlayer,
        AssignPlayerSuccess,
        Countdown,
        CountdownEnhanced,
        TagReport,
        TeamTagReport,
        RankReport,
        ZoneBeacon
    }
}
