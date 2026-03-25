using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Resolves <see cref="InstrumentExecutionAuthority"/> for broker snapshot instrument strings when
/// <see cref="ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(string?, object?, string?)"/> with a null
/// engine hint can point at the wrong registry key (e.g. YM vs MYM micro).
///
/// <para>
/// Step 4 uses <see cref="ExecutionInstrumentResolver.IsSameInstrument"/> only for the explicit micro/root pairs
/// (MES/ES, MNQ/NQ, MYM/YM, etc. in <see cref="ExecutionInstrumentResolver"/>). It does not equate unrelated roots.
/// Disambiguation between multiple matching IEAs uses distance to <paramref name="brokerWorking"/>, not “max count”
/// (max count would bias toward a stale tracker with inflated rows).
/// </para>
/// </summary>
public static class ReconciliationIeaLookup
{
    /// <summary>
    /// Find the IEA whose registry should be compared to broker working orders for <paramref name="brokerInstrument"/>.
    /// Lookup order: engine execution instrument hint (when same canonical market as broker string) →
    /// TryGet by broker-root key → exact ExecutionInstrumentKey match → IsSameInstrument scan, choosing the IEA whose
    /// <see cref="InstrumentExecutionAuthority.GetOwnedPlusAdoptedWorkingCount"/> is closest to <paramref name="brokerWorking"/>.
    /// </summary>
    public static bool TryResolve(
        string account,
        string brokerInstrument,
        int brokerWorking,
        Func<string?> getExecutionInstrumentHint,
        out InstrumentExecutionAuthority? iea)
    {
        iea = null;
        brokerInstrument = brokerInstrument?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(account) || string.IsNullOrEmpty(brokerInstrument))
            return false;

        var hint = getExecutionInstrumentHint?.Invoke()?.Trim();
        var hintNorm = string.IsNullOrWhiteSpace(hint) ? "" : hint.ToUpperInvariant();

        // 1) Prefer engine execution instrument when it matches broker snapshot (same canonical market: YM/MYM, NQ/MNQ, etc.)
        if (!string.IsNullOrEmpty(hintNorm) &&
            ExecutionInstrumentResolver.IsSameInstrument(hintNorm, brokerInstrument) &&
            InstrumentExecutionAuthorityRegistry.TryGet(account, hintNorm, out var hintIea))
        {
            if (brokerWorking == 0 || hintIea!.GetOwnedPlusAdoptedWorkingCount() > 0)
            {
                iea = hintIea;
                return true;
            }
            iea = hintIea;
        }

        // 2) Broker-root key from snapshot string (no engine hint)
        var keyRoot = ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(account, brokerInstrument, null);
        if (!string.IsNullOrEmpty(keyRoot) && keyRoot != "UNKNOWN" &&
            InstrumentExecutionAuthorityRegistry.TryGet(account, keyRoot, out var direct))
        {
            if (iea == null || IsBetterReconciliationCandidate(direct, iea, brokerWorking))
                iea = direct;
            if (brokerWorking == 0 || iea!.GetOwnedPlusAdoptedWorkingCount() > 0)
                return true;
        }

        // 3) Exact ExecutionInstrumentKey match to brokerInstrument (e.g. snapshot root equals IEA key)
        foreach (var x in InstrumentExecutionAuthorityRegistry.GetAllForAccount(account))
        {
            if (string.Equals(x.ExecutionInstrumentKey, brokerInstrument, StringComparison.OrdinalIgnoreCase))
            {
                iea = x;
                return true;
            }
        }

        // 4) Canonical market match — pick IEA whose local count is closest to broker working (not “max count”: stale trackers can inflate)
        foreach (var x in InstrumentExecutionAuthorityRegistry.GetAllForAccount(account))
        {
            if (!ExecutionInstrumentResolver.IsSameInstrument(x.ExecutionInstrumentKey, brokerInstrument))
                continue;
            if (iea == null || IsBetterReconciliationCandidate(x, iea, brokerWorking))
                iea = x;
        }

        return iea != null;
    }

    /// <summary>
    /// Prefer <paramref name="candidate"/> over <paramref name="current"/> when its owned+adopted working count
    /// is closer to <paramref name="brokerWorking"/> (broker truth). On tie distance, prefer fewer local orders
    /// (less likely stale accumulation). Avoids selecting an IEA solely because it has more registry rows.
    /// </summary>
    internal static bool IsBetterReconciliationCandidate(InstrumentExecutionAuthority candidate, InstrumentExecutionAuthority current, int brokerWorking)
    {
        var cc = candidate.GetOwnedPlusAdoptedWorkingCount();
        var cur = current.GetOwnedPlusAdoptedWorkingCount();
        return IsBetterCountTowardsBroker(cc, cur, brokerWorking);
    }

    /// <summary>
    /// Pure selection rule for tests and documentation — compares two local working counts to broker snapshot.
    /// </summary>
    internal static bool IsBetterCountTowardsBroker(int candidateCount, int currentCount, int brokerWorking)
    {
        if (brokerWorking <= 0)
            return candidateCount < currentCount;

        var distC = Math.Abs(candidateCount - brokerWorking);
        var distCur = Math.Abs(currentCount - brokerWorking);
        if (distC < distCur) return true;
        if (distC > distCur) return false;
        return candidateCount < currentCount;
    }
}
