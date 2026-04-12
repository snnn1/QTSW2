// Adoption-on-restart deferral decision logic (extracted for unit testing).
// Used by InstrumentExecutionAuthority.ScanAndAdoptExistingOrders when candidates empty but broker has QTSW2 orders.
// Real-world scenario: restart → broker has orders → journals unavailable → defer → journals appear → adopt.

using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Result of adoption deferral decision.
/// </summary>
public enum AdoptionDeferralAction
{
    /// <summary>Proceed to adoption (candidates available) or no-op (no broker orders).</summary>
    Proceed,
    /// <summary>Defer UNOWNED; retry within grace window.</summary>
    Defer,
    /// <summary>Grace expired; proceed to UNOWNED (fail-closed).</summary>
    GraceExpiredUnowned,
    /// <summary>Candidates empty, no broker orders; nothing to adopt.</summary>
    CandidatesEmptyNoBrokerOrders
}

/// <summary>
/// Pure decision logic for adoption-on-restart deferral.
/// Testable without NinjaTrader; IEA uses this for ScanAndAdoptExistingOrders.
/// </summary>
public static class AdoptionDeferralDecision
{
    /// <summary>
    /// Evaluate adoption deferral decision.
    /// </summary>
    /// <param name="candidateCount">Count from GetAdoptionCandidateIntentIds.</param>
    /// <param name="qtsw2WorkingCount">Broker working orders with QTSW2 tag.</param>
    /// <param name="elapsedSinceFirstScanMs">Wall-clock milliseconds since first scan in this adoption episode (see IEA _firstAdoptionScanUtc).</param>
    /// <param name="graceSeconds">Grace window (default 60). Only wall-clock time gates UNOWNED — not scan count.</param>
    /// <remarks>
    /// Previous versions also capped deferral by scan count. NinjaTrader can emit many execution updates per second,
    /// each triggering a scan, which exhausted the cap in &lt;1s while elapsed wall time was still ~0 — false UNOWNED
    /// and flatten (see docs/robot/incidents/2026-03-19_ES1_ES2_ADOPTION_AND_SUBMISSION_INVESTIGATION.md).
    /// </remarks>
    public static AdoptionDeferralAction Evaluate(
        int candidateCount,
        int qtsw2WorkingCount,
        long elapsedSinceFirstScanMs,
        int graceSeconds = 60)
    {
        if (candidateCount > 0)
            return AdoptionDeferralAction.Proceed;

        if (candidateCount == 0 && qtsw2WorkingCount == 0)
            return AdoptionDeferralAction.CandidatesEmptyNoBrokerOrders;

        // candidateCount == 0 && qtsw2WorkingCount > 0 — defer on wall clock only
        if (elapsedSinceFirstScanMs < graceSeconds * 1000L)
            return AdoptionDeferralAction.Defer;
        return AdoptionDeferralAction.GraceExpiredUnowned;
    }

    /// <summary>
    /// Simulate a sequence of scans and return the action sequence.
    /// For delayed journal visibility test: empty × N then candidates.
    /// </summary>
    /// <param name="candidateCountPerScan">Candidate count for each scan (1-based index).</param>
    /// <param name="qtsw2WorkingCount">Broker QTSW2 orders (constant).</param>
    /// <param name="msBetweenScans">Simulated wall-clock ms added after each Defer (models time between retries).</param>
    public static AdoptionDeferralAction[] SimulateSequence(
        int[] candidateCountPerScan,
        int qtsw2WorkingCount,
        int msBetweenScans = 5000,
        int graceSeconds = 60)
    {
        var results = new AdoptionDeferralAction[candidateCountPerScan.Length];
        long elapsedMs = 0;
        for (int i = 0; i < candidateCountPerScan.Length; i++)
        {
            var action = Evaluate(candidateCountPerScan[i], qtsw2WorkingCount, elapsedMs, graceSeconds);
            results[i] = action;
            if (action == AdoptionDeferralAction.Defer)
                elapsedMs += msBetweenScans;
        }
        return results;
    }
}
