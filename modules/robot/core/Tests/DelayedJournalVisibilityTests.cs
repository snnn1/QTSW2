// Delayed journal visibility tests (adoption-on-restart hardening).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test DELAYED_JOURNAL_VISIBILITY
//
// Tests the exact real-world failure we fixed:
//   restart → broker has orders → journals unavailable → system defers → journals appear → system adopts (before grace expiry)
//
// Also verifies fail-closed: empty → empty → empty → (grace expires) → UNOWNED.

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class DelayedJournalVisibilityTests
{
    public static (bool Pass, string? Error) RunDelayedJournalVisibilityTests()
    {
        var (p1, e1) = TestDeferThenSuccess();
        if (!p1) return (false, e1);

        var (p2, e2) = TestGraceExpiryFailClosed();
        if (!p2) return (false, e2);

        var (p3, e3) = TestNoUnownedBeforeSuccess();
        if (!p3) return (false, e3);

        return (true, null);
    }

    /// <summary>
    /// Required sequence: DEFER (≥1) then PROCEED (success).
    /// Journals unavailable for first 3 scans, then appear on scan 4.
    /// </summary>
    private static (bool Pass, string? Error) TestDeferThenSuccess()
    {
        // call 1-3 → empty, call 4 → valid candidates (1)
        var sequence = new[] { 0, 0, 0, 1 };
        var results = AdoptionDeferralDecision.SimulateSequence(sequence, qtsw2WorkingCount: 1, msBetweenScans: 5000);

        if (results[0] != AdoptionDeferralAction.Defer)
            return (false, $"Scan 1: expected Defer, got {results[0]}");
        if (results[1] != AdoptionDeferralAction.Defer)
            return (false, $"Scan 2: expected Defer, got {results[1]}");
        if (results[2] != AdoptionDeferralAction.Defer)
            return (false, $"Scan 3: expected Defer, got {results[2]}");
        if (results[3] != AdoptionDeferralAction.Proceed)
            return (false, $"Scan 4: expected Proceed (adoption success), got {results[3]}");

        return (true, null);
    }

    /// <summary>
    /// Edge case: empty → empty → empty → (grace expires) → GRACE_EXPIRED_UNOWNED.
    /// Confirms fail-closed still works when journals never appear.
    /// </summary>
    private static (bool Pass, string? Error) TestGraceExpiryFailClosed()
    {
        // 5 scans at 5s each = 25s > 20s grace. All empty.
        var sequence = new[] { 0, 0, 0, 0, 0 };
        var results = AdoptionDeferralDecision.SimulateSequence(sequence, qtsw2WorkingCount: 1, msBetweenScans: 5000);

        if (results[0] != AdoptionDeferralAction.Defer)
            return (false, $"Scan 1: expected Defer, got {results[0]}");
        if (results[1] != AdoptionDeferralAction.Defer)
            return (false, $"Scan 2: expected Defer, got {results[1]}");
        if (results[2] != AdoptionDeferralAction.Defer)
            return (false, $"Scan 3: expected Defer, got {results[2]}");
        // Scan 4: elapsed = 15s, still within 20s grace
        if (results[3] != AdoptionDeferralAction.Defer)
            return (false, $"Scan 4: expected Defer (within grace), got {results[3]}");
        // Scan 5: elapsed = 20s, grace expired
        if (results[4] != AdoptionDeferralAction.GraceExpiredUnowned)
            return (false, $"Scan 5: expected GraceExpiredUnowned (fail-closed), got {results[4]}");

        return (true, null);
    }

    /// <summary>
    /// Required guarantee: NO UNOWNED (GraceExpiredUnowned) before PROCEED (success).
    /// In success path, we must never see GraceExpiredUnowned.
    /// </summary>
    private static (bool Pass, string? Error) TestNoUnownedBeforeSuccess()
    {
        var sequence = new[] { 0, 0, 0, 1 };
        var results = AdoptionDeferralDecision.SimulateSequence(sequence, qtsw2WorkingCount: 1, msBetweenScans: 5000);

        foreach (var r in results)
        {
            if (r == AdoptionDeferralAction.GraceExpiredUnowned)
                return (false, "Success path must never emit GraceExpiredUnowned (no UNOWNED before adoption)");
        }
        return (true, null);
    }
}
