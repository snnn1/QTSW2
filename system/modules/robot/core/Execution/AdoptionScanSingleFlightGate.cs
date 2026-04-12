using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Explicit single-flight state for expensive adoption/recovery scans (one IEA instance).
/// Idle → Queued (off-worker reservation) → Running (worker) → Idle.
/// On-worker inline path: Idle → Running → Idle (never Queued).
/// </summary>
internal enum AdoptionScanGateState
{
    Idle = 0,
    Queued = 1,
    Running = 2
}

internal enum AdoptionScanEnqueueAttemptOutcome
{
    ReservedQueued = 0,
    AlreadyRunning = 1,
    AlreadyQueued = 2
}

/// <summary>
/// Thread-safe coordinator so at most one adoption scan is reserved/running at a time per IEA.
/// </summary>
internal sealed class AdoptionScanSingleFlightGate
{
    private readonly object _lock = new();
    private AdoptionScanGateState _state;

    internal AdoptionScanGateState State
    {
        get
        {
            lock (_lock)
                return _state;
        }
    }

    /// <summary>Off-worker: reserve Queued, or report why not.</summary>
    internal AdoptionScanEnqueueAttemptOutcome TryReserveQueuedSlot()
    {
        lock (_lock)
        {
            if (_state == AdoptionScanGateState.Running)
                return AdoptionScanEnqueueAttemptOutcome.AlreadyRunning;
            if (_state == AdoptionScanGateState.Queued)
                return AdoptionScanEnqueueAttemptOutcome.AlreadyQueued;
            _state = AdoptionScanGateState.Queued;
            return AdoptionScanEnqueueAttemptOutcome.ReservedQueued;
        }
    }

    /// <summary>Worker: transition Queued → Running. False if state is wrong (anomaly).</summary>
    internal bool TryBeginQueuedRun()
    {
        lock (_lock)
        {
            if (_state != AdoptionScanGateState.Queued)
                return false;
            _state = AdoptionScanGateState.Running;
            return true;
        }
    }

    /// <summary>On-worker inline scan: Idle → Running. False if not idle.</summary>
    internal bool TryBeginInlineRun()
    {
        lock (_lock)
        {
            if (_state != AdoptionScanGateState.Idle)
                return false;
            _state = AdoptionScanGateState.Running;
            return true;
        }
    }

    internal void EndRun()
    {
        lock (_lock)
        {
            _state = AdoptionScanGateState.Idle;
        }
    }

    /// <summary>If enqueue failed after <see cref="TryReserveQueuedSlot"/> succeeded, return to Idle.</summary>
    internal void AbortQueuedReservation()
    {
        lock (_lock)
        {
            if (_state == AdoptionScanGateState.Queued)
                _state = AdoptionScanGateState.Idle;
        }
    }
}
