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
    /// <param name="deferredCount">Number of deferred scans so far (1-based).</param>
    /// <param name="elapsedSinceFirstScanMs">Milliseconds since first deferred scan.</param>
    /// <param name="graceSeconds">Grace window (default 20).</param>
    /// <param name="maxDeferredScans">Max retries before grace expiry (default 10).</param>
    public static AdoptionDeferralAction Evaluate(
        int candidateCount,
        int qtsw2WorkingCount,
        int deferredCount,
        long elapsedSinceFirstScanMs,
        int graceSeconds = 20,
        int maxDeferredScans = 10)
    {
        if (candidateCount > 0)
            return AdoptionDeferralAction.Proceed;

        if (candidateCount == 0 && qtsw2WorkingCount == 0)
            return AdoptionDeferralAction.CandidatesEmptyNoBrokerOrders;

        // candidateCount == 0 && qtsw2WorkingCount > 0
        var withinGrace = elapsedSinceFirstScanMs < graceSeconds * 1000L;
        var underRetryLimit = deferredCount <= maxDeferredScans;
        if (withinGrace && underRetryLimit)
            return AdoptionDeferralAction.Defer;
        return AdoptionDeferralAction.GraceExpiredUnowned;
    }

    /// <summary>
    /// Simulate a sequence of scans and return the action sequence.
    /// For delayed journal visibility test: empty × N then candidates.
    /// </summary>
    /// <param name="candidateCountPerScan">Candidate count for each scan (1-based index).</param>
    /// <param name="qtsw2WorkingCount">Broker QTSW2 orders (constant).</param>
    /// <param name="msBetweenScans">Milliseconds between scans.</param>
    public static AdoptionDeferralAction[] SimulateSequence(
        int[] candidateCountPerScan,
        int qtsw2WorkingCount,
        int msBetweenScans = 5000)
    {
        var results = new AdoptionDeferralAction[candidateCountPerScan.Length];
        long elapsedMs = 0;
        int deferredCount = 0;
        for (int i = 0; i < candidateCountPerScan.Length; i++)
        {
            var action = Evaluate(candidateCountPerScan[i], qtsw2WorkingCount, deferredCount + 1, elapsedMs);
            results[i] = action;
            if (action == AdoptionDeferralAction.Defer)
            {
                deferredCount++;
                elapsedMs += msBetweenScans;
            }
        }
        return results;
    }
}
