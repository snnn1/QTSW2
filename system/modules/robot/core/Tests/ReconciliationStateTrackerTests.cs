// Reconciliation debounce + single-writer coordinator unit tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RECONCILIATION_STATE_TRACKER

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ReconciliationStateTrackerTests
{
    public static (bool Pass, string? Error) RunReconciliationStateTrackerTests()
    {
        var debounce = TimeSpan.FromSeconds(45);
        var t0 = new DateTimeOffset(2026, 3, 23, 12, 0, 0, TimeSpan.Zero);
        const string acct = "SIM";
        const string inst = "MNQ";
        var eng1 = "eng:owner";
        var eng2 = "eng:secondary";

        // Case 1 — first mismatch → full path
        var tracker = new ReconciliationStateTracker();
        var g1 = tracker.EvaluateRunnerMismatch(acct, inst, 2, 0, t0, eng1, debounce);
        if (g1.Outcome != ReconciliationMismatchGateOutcome.EmitFullAndInvokeCallback)
            return (false, $"Case1: expected Full, got {g1.Outcome}");
        if (tracker.Metrics.ReconciliationMismatchTotal != 1)
            return (false, $"Case1: mismatch_total expected 1, got {tracker.Metrics.ReconciliationMismatchTotal}");

        tracker.NotifyMismatchHandlingDispatched(acct, inst, 2, 0, t0.AddSeconds(1));

        // Case 2 — same qty within debounce → still open (no duplicate full)
        var g2 = tracker.EvaluateRunnerMismatch(acct, inst, 2, 0, t0.AddSeconds(10), eng1, debounce);
        if (g2.Outcome != ReconciliationMismatchGateOutcome.EmitStillOpenInfoOnly)
            return (false, $"Case2: expected StillOpen, got {g2.Outcome}");
        if (tracker.Metrics.ReconciliationDebouncedTotal < 1)
            return (false, $"Case2: expected debounced counter incremented");

        // Qty change → full again
        var g2b = tracker.EvaluateRunnerMismatch(acct, inst, 3, 0, t0.AddSeconds(15), eng1, debounce);
        if (g2b.Outcome != ReconciliationMismatchGateOutcome.EmitFullAndInvokeCallback)
            return (false, $"Case2b: qty change should bypass debounce, got {g2b.Outcome}");
        tracker.NotifyMismatchHandlingDispatched(acct, inst, 3, 0, t0.AddSeconds(16));

        // Case 3 — multi-instance: only owner; secondary skips
        var tMulti = new ReconciliationStateTracker();
        var m1 = tMulti.EvaluateRunnerMismatch(acct, "MES", 1, 0, t0, eng1, debounce);
        if (m1.Outcome != ReconciliationMismatchGateOutcome.EmitFullAndInvokeCallback)
            return (false, $"Case3a: expected Full for first instance");
        var m2 = tMulti.EvaluateRunnerMismatch(acct, "MES", 1, 0, t0.AddSeconds(2), eng2, debounce);
        if (m2.Outcome != ReconciliationMismatchGateOutcome.SecondaryInstanceSkip)
            return (false, $"Case3b: expected SecondarySkip, got {m2.Outcome}");
        if (tMulti.Metrics.ReconciliationSecondarySkippedTotal != 1)
            return (false, $"Case3: secondary_skipped expected 1");

        // Case 4 — resolution clears tracker
        var tRes = new ReconciliationStateTracker();
        tRes.EvaluateRunnerMismatch(acct, "YM", 1, 0, t0, eng1, debounce);
        tRes.NotifyMismatchHandlingDispatched(acct, "YM", 1, 0, t0.AddSeconds(1));
        if (!tRes.TryMarkResolved(acct, "YM", 1, 1, t0.AddSeconds(2), out var prevOwner, out var prevState))
            return (false, "Case4: TryMarkResolved should return true when qtys match");
        if (prevOwner != eng1)
            return (false, $"Case4: expected owner {eng1}, got {prevOwner}");
        if (tRes.Metrics.ReconciliationResolvedTotal != 1)
            return (false, "Case4: resolved_total expected 1");
        var gFresh = tRes.EvaluateRunnerMismatch(acct, "YM", 1, 0, t0.AddSeconds(5), eng1, debounce);
        if (gFresh.Outcome != ReconciliationMismatchGateOutcome.EmitFullAndInvokeCallback)
            return (false, "Case4b: after resolve, new mismatch should be full episode again");

        // Case 5 — RECOVERY_ESCALATED suppression helper (reconciliation reason only)
        var tEsc = new ReconciliationStateTracker();
        tEsc.EvaluateRunnerMismatch(acct, "NQ", 2, 0, t0, eng1, debounce);
        tEsc.NotifyMismatchHandlingDispatched(acct, "NQ", 2, 0, t0);
        if (!tEsc.ShouldEmitRecoveryEscalatedLog(acct, "NQ", "RECONCILIATION_QTY_MISMATCH"))
            return (false, "Case5a: first reconciliation escalation log should be allowed");
        if (tEsc.ShouldEmitRecoveryEscalatedLog(acct, "NQ", "RECONCILIATION_QTY_MISMATCH"))
            return (false, "Case5b: second reconciliation escalation log should be suppressed");
        if (!tEsc.ShouldEmitRecoveryEscalatedLog(acct, "NQ", "OTHER_REASON"))
            return (false, "Case5c: non-reconciliation reasons should always allow escalation log");

        return (true, null);
    }
}
