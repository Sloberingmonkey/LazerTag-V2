using System;
using System.Collections.Generic;

namespace LazerTagHostLibrary.StateMachine
{
    public sealed class HostStateChangedEventArgs : EventArgs
    {
        public HostSessionState PreviousState { get; }
        public HostSessionState NewState { get; }
        public string Reason { get; }

        public HostStateChangedEventArgs(HostSessionState previousState, HostSessionState newState, string reason)
        {
            PreviousState = previousState;
            NewState = newState;
            Reason = reason ?? string.Empty;
        }
    }

    public sealed class HostStateMachineEvent
    {
        public HostEventType Type { get; }
        public object Payload { get; }
        public DateTime TimestampUtc { get; }

        public HostStateMachineEvent(HostEventType type, object payload = null)
        {
            Type = type;
            Payload = payload;
            TimestampUtc = DateTime.UtcNow;
        }
    }

    /// <summary>
    /// A conservative, explicit state machine for the host lifecycle.
    /// This does not replace HostGun by itself; it is intended to become the single place
    /// that answers "what state are we in, what is accepted here, and where can we go next?"
    /// </summary>
    public sealed class HostStateMachine
    {
        private readonly Dictionary<HostSessionState, HostStatePolicy> _policies;
        private DateTime? _stateDeadlineUtc;
        private int _retryCount;
        private readonly MatchClock _clock;

        public HostSessionState CurrentState { get; private set; }

        public event EventHandler<HostStateChangedEventArgs> StateChanged;

        public HostStateMachine(Dictionary<HostSessionState, HostStatePolicy> policies = null) : this(policies, MatchClock.Start()) { }

        internal HostStateMachine(Dictionary<HostSessionState, HostStatePolicy> policies, MatchClock clock)
        {
            _clock = clock;
            _policies = policies ?? HostStatePolicies.CreateDefault();
            CurrentState = HostSessionState.Idle;
            EnterState(HostSessionState.Idle, "initial");
        }

        public HostStatePolicy PolicyFor(HostSessionState state)
        {
            return _policies.TryGetValue(state, out var policy) ? policy : new HostStatePolicy();
        }

        public bool IsPacketAccepted(HostPacketKind packetKind)
        {
            return PolicyFor(CurrentState).AcceptedPackets.Contains(packetKind);
        }

        public bool IsEventAccepted(HostEventType eventType)
        {
            return PolicyFor(CurrentState).AcceptedEvents.Contains(eventType);
        }

        public bool HasTimedOut(DateTime utcNow)
        {
            return _stateDeadlineUtc.HasValue && utcNow >= _stateDeadlineUtc.Value;
        }

        public bool CanRetry => _retryCount < PolicyFor(CurrentState).MaxRetries;

        public bool Handle(HostStateMachineEvent evt, out string note)
        {
            note = null;

            if (!IsEventAccepted(evt.Type))
            {
                note = $"Event {evt.Type} ignored in {CurrentState}.";
                return false;
            }

            switch (CurrentState)
            {
                case HostSessionState.Idle:
                    return HandleIdle(evt, out note);

                case HostSessionState.AddingPlayers:
                    return HandleAddingPlayers(evt, out note);

                case HostSessionState.AwaitingAssignmentAck:
                    return HandleAwaitingAssignmentAck(evt, out note);

                case HostSessionState.Countdown:
                    return HandleCountdown(evt, out note);

                case HostSessionState.ResendCountdown:
                    return HandleResendCountdown(evt, out note);

                case HostSessionState.InGame:
                    return HandleInGame(evt, out note);

                case HostSessionState.AwaitingDebrief:
                    return HandleAwaitingDebrief(evt, out note);

                case HostSessionState.Recovery:
                    return HandleRecovery(evt, out note);

                case HostSessionState.GameOver:
                    return HandleGameOver(evt, out note);

                case HostSessionState.Faulted:
                    return HandleFaulted(evt, out note);

                default:
                    note = $"Unhandled state {CurrentState}.";
                    return false;
            }
        }

        public bool OnTick(DateTime utcNow, out string note)
        {
            note = null;
            if (!HasTimedOut(utcNow))
            {
                return false;
            }

            if (!Handle(new HostStateMachineEvent(HostEventType.TimeoutExpired), out note))
                return false;

            // A retry may stay in the same state. Its deadline starts at this tick.
            SetTimeoutDeadline(utcNow);
            return true;
        }

        private bool HandleIdle(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.StartHosting:
                    TransitionTo(HostSessionState.AddingPlayers, "hosting started");
                    note = "Entered AddingPlayers.";
                    return true;
                case HostEventType.Reset:
                    TransitionTo(HostSessionState.Idle, "reset");
                    note = "Reset to Idle.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleAddingPlayers(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.PlayerJoinObserved:
                    note = "Player join observed.";
                    return true;
                case HostEventType.PlayerAssignmentSent:
                    TransitionTo(HostSessionState.AwaitingAssignmentAck, "assignment sent");
                    note = "Awaiting assignment acknowledgement.";
                    return true;
                case HostEventType.CountdownStarted:
                    TransitionTo(HostSessionState.Countdown, "countdown started");
                    note = "Countdown started.";
                    return true;
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.Idle, "stopped while adding players");
                    note = "Stopped hosting.";
                    return true;
                case HostEventType.FaultRaised:
                    TransitionTo(HostSessionState.Faulted, "fault while adding players");
                    note = "Faulted.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleAwaitingAssignmentAck(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.AssignmentAcknowledged:
                    TransitionTo(HostSessionState.AddingPlayers, "assignment acknowledged");
                    note = "Assignment acknowledged.";
                    return true;
                case HostEventType.TimeoutExpired:
                case HostEventType.RetryRequested:
                    if (CanRetry)
                    {
                        _retryCount++;
                        note = $"Retry assignment ack wait {_retryCount}/{PolicyFor(CurrentState).MaxRetries}.";
                        return true;
                    }
                    TransitionTo(HostSessionState.Recovery, "assignment ack retries exhausted");
                    note = "Moved to Recovery after assignment ack timeout.";
                    return true;
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.Idle, "stopped while awaiting ack");
                    note = "Stopped hosting.";
                    return true;
                case HostEventType.FaultRaised:
                    TransitionTo(HostSessionState.Faulted, "fault while awaiting ack");
                    note = "Faulted.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleCountdown(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.CountdownExpired:
                    TransitionTo(HostSessionState.InGame, "countdown expired");
                    note = "Entered InGame.";
                    return true;
                case HostEventType.TransportLost:
                    TransitionTo(HostSessionState.Recovery, "transport lost during countdown");
                    note = "Moved to Recovery.";
                    return true;
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.Idle, "stopped during countdown");
                    note = "Stopped hosting.";
                    return true;
                case HostEventType.FaultRaised:
                    TransitionTo(HostSessionState.Faulted, "fault during countdown");
                    note = "Faulted.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleResendCountdown(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.ResendCountdownExpired:
                    TransitionTo(HostSessionState.AwaitingDebrief, "resend countdown expired");
                    note = "Moved to AwaitingDebrief.";
                    return true;
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.Idle, "stopped during resend countdown");
                    note = "Stopped hosting.";
                    return true;
                case HostEventType.FaultRaised:
                    TransitionTo(HostSessionState.Faulted, "fault during resend countdown");
                    note = "Faulted.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleInGame(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.GameExpired:
                case HostEventType.DebriefRequested:
                    TransitionTo(HostSessionState.AwaitingDebrief, "game ended");
                    note = "Moved to AwaitingDebrief.";
                    return true;
                case HostEventType.TransportLost:
                    TransitionTo(HostSessionState.Recovery, "transport lost during game");
                    note = "Moved to Recovery.";
                    return true;
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.GameOver, "stopped during game");
                    note = "Forced GameOver.";
                    return true;
                case HostEventType.FaultRaised:
                    TransitionTo(HostSessionState.Faulted, "fault during game");
                    note = "Faulted.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleAwaitingDebrief(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.DebriefSummaryReceived:
                    note = "Debrief summary received.";
                    return true;
                case HostEventType.DebriefComplete:
                    TransitionTo(HostSessionState.GameOver, "all debrief complete");
                    note = "Entered GameOver.";
                    return true;
                case HostEventType.TimeoutExpired:
                case HostEventType.RetryRequested:
                    if (CanRetry)
                    {
                        _retryCount++;
                        note = $"Retry debrief {_retryCount}/{PolicyFor(CurrentState).MaxRetries}.";
                        return true;
                    }
                    TransitionTo(HostSessionState.Recovery, "debrief retries exhausted");
                    note = "Moved to Recovery after debrief timeout.";
                    return true;
                case HostEventType.TransportLost:
                    TransitionTo(HostSessionState.Recovery, "transport lost during debrief");
                    note = "Moved to Recovery.";
                    return true;
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.GameOver, "stopped during debrief");
                    note = "Forced GameOver.";
                    return true;
                case HostEventType.FaultRaised:
                    TransitionTo(HostSessionState.Faulted, "fault during debrief");
                    note = "Faulted.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleRecovery(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.TransportRestored:
                    TransitionTo(HostSessionState.AwaitingDebrief, "transport restored");
                    note = "Recovered to AwaitingDebrief.";
                    return true;
                case HostEventType.TimeoutExpired:
                    if (CanRetry)
                    {
                        _retryCount++;
                        note = $"Recovery retry {_retryCount}/{PolicyFor(CurrentState).MaxRetries}.";
                        return true;
                    }
                    TransitionTo(HostSessionState.Faulted, "recovery retries exhausted");
                    note = "Faulted after recovery failure.";
                    return true;
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.GameOver, "stopped during recovery");
                    note = "Forced GameOver.";
                    return true;
                case HostEventType.FaultRaised:
                    TransitionTo(HostSessionState.Faulted, "fault during recovery");
                    note = "Faulted.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleGameOver(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.Reset:
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.Idle, "reset from game over");
                    note = "Returned to Idle.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private bool HandleFaulted(HostStateMachineEvent evt, out string note)
        {
            switch (evt.Type)
            {
                case HostEventType.Reset:
                case HostEventType.StopHosting:
                    TransitionTo(HostSessionState.Idle, "reset from faulted");
                    note = "Returned to Idle.";
                    return true;
                default:
                    note = null;
                    return false;
            }
        }

        private void EnterState(HostSessionState state, string reason)
        {
            CurrentState = state;
            _retryCount = 0;
            SetTimeoutDeadline(_clock.Now);
        }

        private void SetTimeoutDeadline(DateTime utcNow)
        {
            var timeout = PolicyFor(CurrentState).Timeout;
            _stateDeadlineUtc = timeout.HasValue ? utcNow.Add(timeout.Value) : (DateTime?)null;
        }

        private void TransitionTo(HostSessionState newState, string reason)
        {
            var previous = CurrentState;
            EnterState(newState, reason);
            StateChanged?.Invoke(this, new HostStateChangedEventArgs(previous, newState, reason));
        }
    }
}
