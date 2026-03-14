// Unit tests for Gap 4: Persistent Mismatch Escalation.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test MISMATCH_ESCALATION

using System;
using System.Collections.Generic;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class MismatchEscalationTests
{
    public static (bool Pass, string? Error) RunMismatchEscalationTests()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var emptySnap = new AccountSnapshot { Positions = new List<PositionSnapshot>(), WorkingOrders = new List<WorkingOrderSnapshot>() };

        var coordinator = new MismatchEscalationCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: () => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null);

        // 1. First mismatch creates DETECTED but does not block immediately
        coordinator.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            BrokerQty = 1,
            LocalQty = 0,
            ObservedUtc = utcNow
        });
        var state = coordinator.GetStateForTest("ES");
        if (state == null)
            return (false, "State should exist after first mismatch");
        if (state.EscalationState != MismatchEscalationState.DETECTED)
            return (false, $"First mismatch: expected DETECTED, got {state.EscalationState}");
        if (state.Blocked)
            return (false, "First mismatch: should NOT block immediately (below persistent threshold)");

        // 2. Mismatch persisting beyond persistent threshold transitions to PERSISTENT_MISMATCH and blocks instrument
        var t1 = utcNow.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS + 100);
        coordinator.ProcessObservationForTest(new MismatchObservation
        {
            Instrument = "ES",
            MismatchType = MismatchType.BROKER_AHEAD,
            Present = true,
            BrokerQty = 1,
            LocalQty = 0,
            ObservedUtc = t1
        });
        state = coordinator.GetStateForTest("ES");
        if (state?.EscalationState != MismatchEscalationState.PERSISTENT_MISMATCH)
            return (false, $"After persistent threshold: expected PERSISTENT_MISMATCH, got {state?.EscalationState}");
        if (!state.Blocked)
            return (false, "After persistent threshold: should block instrument");
        if (!coordinator.IsInstrumentBlockedByMismatch("ES"))
            return (false, "IsInstrumentBlockedByMismatch should be true");

        // 3. Mismatch persisting beyond fail-closed threshold transitions to FAIL_CLOSED
        var coord2 = new MismatchEscalationCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: () => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null);
        coord2.ProcessObservationForTest(new MismatchObservation { Instrument = "NQ", MismatchType = MismatchType.JOURNAL_AHEAD, Present = true, BrokerQty = 0, LocalQty = 1, ObservedUtc = utcNow });
        var t2 = utcNow.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_FAIL_CLOSED_THRESHOLD_MS + 100);
        coord2.ProcessObservationForTest(new MismatchObservation { Instrument = "NQ", MismatchType = MismatchType.JOURNAL_AHEAD, Present = true, BrokerQty = 0, LocalQty = 1, ObservedUtc = utcNow.AddMilliseconds(MismatchEscalationPolicy.MISMATCH_PERSISTENT_THRESHOLD_MS + 100) });
        coord2.ProcessObservationForTest(new MismatchObservation { Instrument = "NQ", MismatchType = MismatchType.JOURNAL_AHEAD, Present = true, BrokerQty = 0, LocalQty = 1, ObservedUtc = t2 });
        state = coord2.GetStateForTest("NQ");
        if (state?.EscalationState != MismatchEscalationState.FAIL_CLOSED)
            return (false, $"After fail-closed threshold: expected FAIL_CLOSED, got {state?.EscalationState}");

        // 4. Two consecutive clean passes clear DETECTED or PERSISTENT_MISMATCH state if not fail-closed
        var coord3 = new MismatchEscalationCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: () => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null);
        coord3.ProcessObservationForTest(new MismatchObservation { Instrument = "RTY", MismatchType = MismatchType.POSITION_QTY_MISMATCH, Present = true, BrokerQty = 2, LocalQty = 1, ObservedUtc = utcNow });
        coord3.ProcessCleanPassForTest("RTY", utcNow.AddSeconds(1));
        coord3.ProcessCleanPassForTest("RTY", utcNow.AddSeconds(2));
        state = coord3.GetStateForTest("RTY");
        if (state?.EscalationState != MismatchEscalationState.NONE)
            return (false, $"After 2 clean passes: expected NONE, got {state?.EscalationState}");
        if (coord3.IsInstrumentBlockedByMismatch("RTY"))
            return (false, "Should not be blocked after clear");

        // 5. FAIL_CLOSED remains blocked and does not auto-clear
        coord2.ProcessCleanPassForTest("NQ", t2.AddSeconds(1));
        coord2.ProcessCleanPassForTest("NQ", t2.AddSeconds(2));
        state = coord2.GetStateForTest("NQ");
        if (state?.EscalationState != MismatchEscalationState.FAIL_CLOSED)
            return (false, $"FAIL_CLOSED: should remain FAIL_CLOSED after clean passes, got {state?.EscalationState}");
        if (!coord2.IsInstrumentBlockedByMismatch("NQ"))
            return (false, "FAIL_CLOSED: instrument should remain blocked");

        // 6. Repeated identical mismatch audits do not spam transition events (state stays DETECTED, no duplicate PERSISTENT)
        var coord4 = new MismatchEscalationCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: () => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null);
        for (int i = 0; i < 5; i++)
            coord4.ProcessObservationForTest(new MismatchObservation { Instrument = "YM", MismatchType = MismatchType.BROKER_AHEAD, Present = true, BrokerQty = 1, LocalQty = 0, ObservedUtc = utcNow.AddSeconds(i) });
        state = coord4.GetStateForTest("YM");
        if (state?.EscalationState != MismatchEscalationState.DETECTED)
            return (false, $"Repeated audits before threshold: should stay DETECTED, got {state?.EscalationState}");

        // 7. Broker-ahead classification
        if (state?.MismatchType != MismatchType.BROKER_AHEAD)
            return (false, $"Broker-ahead: expected MismatchType BROKER_AHEAD, got {state?.MismatchType}");

        // 8. Position qty mismatch classification
        var coord5 = new MismatchEscalationCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            getMismatchObservations: () => Array.Empty<MismatchObservation>(),
            isInstrumentBlocked: _ => false,
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            log: null);
        coord5.ProcessObservationForTest(new MismatchObservation { Instrument = "CL", MismatchType = MismatchType.POSITION_QTY_MISMATCH, Present = true, BrokerQty = 3, LocalQty = 1, ObservedUtc = utcNow });
        state = coord5.GetStateForTest("CL");
        if (state?.MismatchType != MismatchType.POSITION_QTY_MISMATCH)
            return (false, $"Position qty mismatch: expected POSITION_QTY_MISMATCH, got {state?.MismatchType}");

        // 9. Entry gating: coordinator blocks; RiskGate integration tested via IsInstrumentBlockedByMismatch
        if (!coordinator.IsInstrumentBlockedByMismatch("ES"))
            return (false, "Entry gating: mismatch-blocked instrument should be blocked");

        // 10. Block reason returned is persistent mismatch reason
        var reason = coordinator.GetBlockReason("ES");
        if (string.IsNullOrEmpty(reason) || !reason.Contains("PERSISTENT") && !reason.Contains("RECONCILIATION"))
            return (false, $"Block reason: expected persistent mismatch reason, got '{reason}'");

        return (true, null);
    }
}
