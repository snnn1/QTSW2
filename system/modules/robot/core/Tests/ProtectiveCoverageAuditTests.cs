// Unit tests for Gap 3: Protective Coverage Audit.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test PROTECTIVE_AUDIT

using System;
using System.Collections.Generic;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ProtectiveCoverageAuditTests
{
    public static (bool Pass, string? Error) RunProtectiveAuditTests()
    {
        var utcNow = DateTimeOffset.UtcNow;

        // 1. Long position, valid stop and target -> PROTECTIVE_OK
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>
            {
                new() { Instrument = "MNQ", Quantity = 1, AveragePrice = 21000m }
            },
            WorkingOrders = new List<WorkingOrderSnapshot>
            {
                new() { Instrument = "MNQ", Tag = "QTSW2:intent1:STOP", StopPrice = 20950m, Quantity = 1 },
                new() { Instrument = "MNQ", Tag = "QTSW2:intent1:TARGET", Price = 21050m, Quantity = 1 }
            }
        };
        var r = ProtectiveCoverageAudit.Audit("MNQ", snap, null, false, false, false, utcNow);
        if (r.Status != ProtectiveAuditStatus.PROTECTIVE_OK)
            return (false, $"Valid stop+target: expected PROTECTIVE_OK, got {r.Status}");

        // 2. Long position, no stop -> PROTECTIVE_MISSING_STOP
        snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "MNQ", Quantity = 1, AveragePrice = 21000m } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        r = ProtectiveCoverageAudit.Audit("MNQ", snap, null, false, false, false, utcNow);
        if (r.Status != ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP)
            return (false, $"No stop: expected PROTECTIVE_MISSING_STOP, got {r.Status}");

        // 3. Long position, stop qty less than broker qty -> PROTECTIVE_STOP_QTY_MISMATCH
        snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "MNQ", Quantity = 2, AveragePrice = 21000m } },
            WorkingOrders = new List<WorkingOrderSnapshot>
            {
                new() { Instrument = "MNQ", Tag = "QTSW2:intent1:STOP", StopPrice = 20950m, Quantity = 1 },
                new() { Instrument = "MNQ", Tag = "QTSW2:intent1:TARGET", Price = 21050m, Quantity = 1 }
            }
        };
        r = ProtectiveCoverageAudit.Audit("MNQ", snap, null, false, false, false, utcNow);
        if (r.Status != ProtectiveAuditStatus.PROTECTIVE_STOP_QTY_MISMATCH)
            return (false, $"Stop qty mismatch: expected PROTECTIVE_STOP_QTY_MISMATCH, got {r.Status}");

        // 4. Long position, target missing but stop present -> PROTECTIVE_MISSING_TARGET
        snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "MNQ", Quantity = 1, AveragePrice = 21000m } },
            WorkingOrders = new List<WorkingOrderSnapshot>
            {
                new() { Instrument = "MNQ", Tag = "QTSW2:intent1:STOP", StopPrice = 20950m, Quantity = 1 }
            }
        };
        r = ProtectiveCoverageAudit.Audit("MNQ", snap, null, false, false, false, utcNow);
        if (r.Status != ProtectiveAuditStatus.PROTECTIVE_MISSING_TARGET)
            return (false, $"Missing target: expected PROTECTIVE_MISSING_TARGET, got {r.Status}");

        // 5. Flat position -> PROTECTIVE_OK (no audit needed)
        snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        r = ProtectiveCoverageAudit.Audit("MNQ", snap, null, false, false, false, utcNow);
        if (r.Status != ProtectiveAuditStatus.PROTECTIVE_OK)
            return (false, $"Flat: expected PROTECTIVE_OK, got {r.Status}");

        // 6. Flatten in progress -> PROTECTIVE_FLATTEN_IN_PROGRESS
        snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "MNQ", Quantity = 1, AveragePrice = 21000m } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        r = ProtectiveCoverageAudit.Audit("MNQ", snap, null, flattenInProgress: true, false, false, utcNow);
        if (r.Status != ProtectiveAuditStatus.PROTECTIVE_FLATTEN_IN_PROGRESS)
            return (false, $"Flatten in progress: expected PROTECTIVE_FLATTEN_IN_PROGRESS, got {r.Status}");

        // 7. IsCritical
        if (!ProtectiveCoverageAudit.IsCritical(ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP))
            return (false, "IsCritical(PROTECTIVE_MISSING_STOP) should be true");
        if (ProtectiveCoverageAudit.IsCritical(ProtectiveAuditStatus.PROTECTIVE_OK))
            return (false, "IsCritical(PROTECTIVE_OK) should be false");

        // Phase 3: Coordinator recovery state and block tests
        var phase3Result = RunProtectiveCoordinatorPhase3Tests();
        if (!phase3Result.Pass)
            return phase3Result;

        // Phase 4: Bounded corrective workflow tests
        var phase4Result = RunProtectiveCoordinatorPhase4Tests();
        if (!phase4Result.Pass)
            return phase4Result;

        // Phase 5: Emergency flatten escalation tests
        var phase5Result = RunProtectiveCoordinatorPhase5Tests();
        if (!phase5Result.Pass)
            return phase5Result;

        return (true, null);
    }

    /// <summary>Phase 3: Per-instrument recovery state, block on critical, 2-pass clear.</summary>
    private static (bool Pass, string? Error) RunProtectiveCoordinatorPhase3Tests()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var emptySnap = new AccountSnapshot { Positions = new List<PositionSnapshot>(), WorkingOrders = new List<WorkingOrderSnapshot>() };

        var coordinator = new ProtectiveCoverageCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            isInstrumentBlocked: _ => false,
            log: null);

        // 1. First critical failure -> DETECTED + blocked
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        var state = coordinator.GetStateForTest("ES");
        if (state == null)
            return (false, "State should exist after critical failure");
        if (state.RecoveryState != ProtectiveRecoveryState.DETECTED)
            return (false, $"First critical: expected DETECTED, got {state.RecoveryState}");
        if (!state.Blocked)
            return (false, "First critical: expected Blocked=true");
        if (!coordinator.IsInstrumentBlockedByProtective("ES"))
            return (false, "IsInstrumentBlockedByProtective should be true");

        // 2. Repeated critical audits do not spam transition events (idempotent - no new PROTECTIVE_RECOVERY_STARTED / PROTECTIVE_INSTRUMENT_BLOCKED)
        // We can't easily count log events without a mock logger; verify state stays DETECTED+blocked
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow.AddSeconds(1)
        });
        state = coordinator.GetStateForTest("ES");
        if (state?.RecoveryState != ProtectiveRecoveryState.DETECTED || !state.Blocked)
            return (false, "Repeated critical: should remain DETECTED and blocked");

        // 3. Non-critical target-only failures do not block
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "NQ",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_TARGET,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        if (coordinator.IsInstrumentBlockedByProtective("NQ"))
            return (false, "PROTECTIVE_MISSING_TARGET should NOT block instrument");
        var nqState = coordinator.GetStateForTest("NQ");
        if (nqState != null && nqState.Blocked)
            return (false, "NQ should not be in blocked state after missing target only");

        // 4. Two consecutive clean audits clear block
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_OK,
            BrokerPositionQty = 0,
            BrokerDirection = "",
            AuditUtc = utcNow.AddSeconds(2)
        });
        state = coordinator.GetStateForTest("ES");
        if (state?.ConsecutiveCleanPassCount != 1)
            return (false, $"After 1st clean: expected ConsecutiveCleanPassCount=1, got {state?.ConsecutiveCleanPassCount}");
        if (state?.Blocked != true)
            return (false, "After 1st clean: should still be blocked (need 2 passes)");

        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_OK,
            BrokerPositionQty = 0,
            BrokerDirection = "",
            AuditUtc = utcNow.AddSeconds(3)
        });
        state = coordinator.GetStateForTest("ES");
        if (state?.RecoveryState != ProtectiveRecoveryState.NONE)
            return (false, $"After 2nd clean: expected NONE, got {state?.RecoveryState}");
        if (state?.Blocked != false)
            return (false, "After 2nd clean: should be unblocked");
        if (coordinator.IsInstrumentBlockedByProtective("ES"))
            return (false, "IsInstrumentBlockedByProtective should be false after 2 clean passes");

        // 5. Blocked instrument refuses new entries (via RiskGate when wired)
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "RTY",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        if (!coordinator.IsInstrumentBlockedByProtective("RTY"))
            return (false, "RTY should be blocked after critical failure");
        var reason = coordinator.GetBlockReason("RTY");
        if (string.IsNullOrEmpty(reason) || !reason.Contains("PROTECTIVE"))
            return (false, $"Block reason should indicate protective: got '{reason}'");

        return (true, null);
    }

    /// <summary>Phase 4: Bounded corrective workflow — one attempt, no resubmit, timeout escalation.</summary>
    private static (bool Pass, string? Error) RunProtectiveCoordinatorPhase4Tests()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var emptySnap = new AccountSnapshot { Positions = new List<PositionSnapshot>(), WorkingOrders = new List<WorkingOrderSnapshot>() };

        var submitCount = 0;
        var coordinator = new ProtectiveCoverageCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            isInstrumentBlocked: _ => false,
            log: null,
            submitCorrective: req =>
            {
                submitCount++;
                return new ProtectiveCorrectiveResult { Submitted = true, IntentId = "test-intent" };
            });

        // 1. Missing stop triggers one corrective submission only
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        if (submitCount != 1)
            return (false, $"Missing stop: expected 1 corrective call, got {submitCount}");
        var state = coordinator.GetStateForTest("ES");
        if (state?.RecoveryState != ProtectiveRecoveryState.AWAITING_CONFIRMATION)
            return (false, $"After corrective: expected AWAITING_CONFIRMATION, got {state?.RecoveryState}");
        if (state?.AttemptCount != 1)
            return (false, $"After corrective: expected AttemptCount=1, got {state?.AttemptCount}");

        // 2. Repeated critical audits during AWAITING_CONFIRMATION do not resubmit
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow.AddSeconds(1)
        });
        if (submitCount != 1)
            return (false, $"Repeated critical during AWAITING: expected 1 total corrective call, got {submitCount}");

        // 3. Clean audit after corrective action clears state
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_OK,
            BrokerPositionQty = 0,
            BrokerDirection = "",
            AuditUtc = utcNow.AddSeconds(2)
        });
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_OK,
            BrokerPositionQty = 0,
            BrokerDirection = "",
            AuditUtc = utcNow.AddSeconds(3)
        });
        state = coordinator.GetStateForTest("ES");
        if (state?.RecoveryState != ProtectiveRecoveryState.NONE)
            return (false, $"After 2 clean: expected NONE, got {state?.RecoveryState}");
        if (state?.Blocked != false)
            return (false, "After 2 clean: should be unblocked");

        // 4. Corrective timeout transitions to ESCALATE_TO_FLATTEN
        submitCount = 0;
        var coord2 = new ProtectiveCoverageCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            isInstrumentBlocked: _ => false,
            log: null,
            submitCorrective: _ => new ProtectiveCorrectiveResult { Submitted = true, IntentId = "test" });
        coord2.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "NQ",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        var afterTimeout = utcNow.AddMilliseconds(ProtectiveAuditPolicy.PROTECTIVE_AWAITING_CONFIRMATION_MS + 100);
        coord2.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "NQ",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = afterTimeout
        });
        state = coord2.GetStateForTest("NQ");
        if (state?.RecoveryState != ProtectiveRecoveryState.ESCALATE_TO_FLATTEN)
            return (false, $"After timeout: expected ESCALATE_TO_FLATTEN, got {state?.RecoveryState}");

        // 5. Non-critical target-only issues never enter corrective submission
        submitCount = 0;
        var coord3 = new ProtectiveCoverageCoordinator(
            getSnapshot: () => emptySnap,
            getActiveInstruments: () => Array.Empty<string>(),
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            isInstrumentBlocked: _ => false,
            log: null,
            submitCorrective: _ => { submitCount++; return new ProtectiveCorrectiveResult { Submitted = true }; });
        coord3.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "RTY",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_TARGET,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        if (submitCount != 0)
            return (false, $"PROTECTIVE_MISSING_TARGET: should NOT call corrective, got {submitCount} calls");

        return (true, null);
    }

    /// <summary>Phase 5: Emergency flatten escalation — corrective timeout, NO_SAFE_STOP_PRICE, single-trigger, flat→LOCKED_FAIL_CLOSED.</summary>
    private static (bool Pass, string? Error) RunProtectiveCoordinatorPhase5Tests()
    {
        var utcNow = DateTimeOffset.UtcNow;
        var criticalSnap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1, AveragePrice = 21000m } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        var flatSnap = new AccountSnapshot { Positions = new List<PositionSnapshot>(), WorkingOrders = new List<WorkingOrderSnapshot>() };

        // 1. Corrective timeout triggers one emergency flatten only
        var flattenCount = 0;
        var coordinator = new ProtectiveCoverageCoordinator(
            getSnapshot: () => criticalSnap,
            getActiveInstruments: () => new[] { "ES" },
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            isInstrumentBlocked: _ => false,
            log: null,
            submitCorrective: _ => new ProtectiveCorrectiveResult { Submitted = true, IntentId = "test" },
            emergencyFlatten: (inst, now) => { flattenCount++; return FlattenResult.SuccessResult(now); });
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        var afterTimeout = utcNow.AddMilliseconds(ProtectiveAuditPolicy.PROTECTIVE_AWAITING_CONFIRMATION_MS + 100);
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = afterTimeout
        });
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = afterTimeout.AddMilliseconds(100)
        });
        if (flattenCount != 1)
            return (false, $"Corrective timeout: expected 1 emergency flatten call, got {flattenCount}");
        var state = coordinator.GetStateForTest("ES");
        if (state?.RecoveryState != ProtectiveRecoveryState.FLATTEN_IN_PROGRESS)
            return (false, $"After timeout+flatten: expected FLATTEN_IN_PROGRESS, got {state?.RecoveryState}");

        // 2. Repeated audits in FLATTEN_IN_PROGRESS do not retrigger flatten
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = afterTimeout.AddSeconds(1)
        });
        coordinator.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = afterTimeout.AddSeconds(2)
        });
        if (flattenCount != 1)
            return (false, $"FLATTEN_IN_PROGRESS repeated: expected 1 total flatten call, got {flattenCount}");

        // 3. Flat broker position transitions to LOCKED_FAIL_CLOSED
        var coord2 = new ProtectiveCoverageCoordinator(
            getSnapshot: () => flatSnap,
            getActiveInstruments: () => new[] { "ES" },
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            isInstrumentBlocked: _ => false,
            log: null,
            submitCorrective: _ => new ProtectiveCorrectiveResult { Submitted = true },
            emergencyFlatten: (inst, now) => FlattenResult.SuccessResult(now));
        coord2.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        var afterTimeout2 = utcNow.AddMilliseconds(ProtectiveAuditPolicy.PROTECTIVE_AWAITING_CONFIRMATION_MS + 100);
        coord2.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = afterTimeout2
        });
        coord2.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = afterTimeout2.AddMilliseconds(100)
        });
        coord2.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_OK,
            BrokerPositionQty = 0,
            BrokerDirection = "",
            AuditUtc = afterTimeout2.AddSeconds(1)
        });
        state = coord2.GetStateForTest("ES");
        if (state?.RecoveryState != ProtectiveRecoveryState.LOCKED_FAIL_CLOSED)
            return (false, $"Flat broker: expected LOCKED_FAIL_CLOSED, got {state?.RecoveryState}");

        // 4. Instrument remains blocked after protective-triggered flatten
        if (!coord2.IsInstrumentBlockedByProtective("ES"))
            return (false, "Instrument should remain blocked in LOCKED_FAIL_CLOSED");

        // 5. NO_SAFE_STOP_PRICE escalates to flatten immediately
        var flattenCount2 = 0;
        var coord3 = new ProtectiveCoverageCoordinator(
            getSnapshot: () => criticalSnap,
            getActiveInstruments: () => new[] { "ES" },
            isFlattenInProgress: _ => false,
            isRecoveryInProgress: _ => false,
            isInstrumentBlocked: _ => false,
            log: null,
            submitCorrective: _ => new ProtectiveCorrectiveResult { Submitted = false, FailureReason = "NO_SAFE_STOP_PRICE" },
            emergencyFlatten: (inst, now) => { flattenCount2++; return FlattenResult.SuccessResult(now); });
        coord3.ProcessResultForTest(new ProtectiveAuditResult
        {
            Instrument = "ES",
            Status = ProtectiveAuditStatus.PROTECTIVE_MISSING_STOP,
            BrokerPositionQty = 1,
            BrokerDirection = "Long",
            AuditUtc = utcNow
        });
        if (flattenCount2 != 1)
            return (false, $"NO_SAFE_STOP_PRICE: expected 1 emergency flatten call, got {flattenCount2}");
        state = coord3.GetStateForTest("ES");
        if (state?.RecoveryState != ProtectiveRecoveryState.FLATTEN_IN_PROGRESS)
            return (false, $"NO_SAFE_STOP_PRICE: expected FLATTEN_IN_PROGRESS, got {state?.RecoveryState}");
        if (!coord3.IsInstrumentBlockedByProtective("ES"))
            return (false, "Instrument should be blocked after NO_SAFE_STOP_PRICE escalate");

        return (true, null);
    }
}
