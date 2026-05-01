using System;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class MismatchEscalationCoordinator
{
    internal void ProcessObservationForTest(MismatchObservation obs) => ProcessMismatchPresent(obs);

    internal void ProcessCleanPassForTest(string instrument, DateTimeOffset utcNow) =>
        ProcessMismatchSignalAbsent(instrument, utcNow);

    internal MismatchInstrumentState? GetStateForTest(string instrument) =>
        _stateByInstrument.TryGetValue(instrument, out var s) ? s : null;

    /// <summary>For P1.5 tests: advance gate for one instrument (same as audit tail).</summary>
    internal void AdvanceStateConsistencyGateForTest(string instrument, AccountSnapshot snapshot, DateTimeOffset utcNow,
        MismatchObservation? observation = null) =>
        AdvanceStateConsistencyGate(instrument.Trim(), snapshot, utcNow, observation);

    internal void SetStableWindowForTest(int ms)
    {
        foreach (var kv in _stateByInstrument)
            kv.Value.StableWindowMsApplied = ms;
    }

    internal void SetForcedConvergenceStallForTest(string instrument, bool succeeded, ulong postAlignmentFingerprint)
    {
        if (string.IsNullOrWhiteSpace(instrument)) return;
        if (!_stateByInstrument.TryGetValue(instrument.Trim(), out var s)) return;
        s.ForcedConvergenceSucceeded = succeeded;
        s.PostForcedConvergenceFingerprint = postAlignmentFingerprint;
    }
}
