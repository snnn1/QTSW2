// Focused checks for YM/MYM reconciliation aliasing (no full IEA registry — run via harness).
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ReconciliationIeaLookupTests
{
    /// <summary>
    /// MYM vs YM must canonical-match for ReconciliationIeaLookup step 1 / 4 to align engine hint with broker snapshot.
    /// </summary>
    public static (bool Pass, string? Error) RunCanonicalMarketAliasTests()
    {
        if (!ExecutionInstrumentResolver.IsSameInstrument("MYM", "YM"))
            return (false, "IsSameInstrument(MYM, YM) expected true");
        if (!ExecutionInstrumentResolver.IsSameInstrument("MNQ", "NQ"))
            return (false, "IsSameInstrument(MNQ, NQ) expected true");
        if (ExecutionInstrumentResolver.IsSameInstrument("MNQ", "ES"))
            return (false, "IsSameInstrument(MNQ, ES) expected false");
        return (true, null);
    }

    /// <summary>
    /// Selection must track broker working count, not "max local count" (stale tracker can inflate).
    /// </summary>
    public static (bool Pass, string? Error) RunCountSelectionTowardsBrokerTests()
    {
        // broker=2: prefer 2 over 3 (stale high)
        if (!ReconciliationIeaLookup.IsBetterCountTowardsBroker(2, 3, 2))
            return (false, "broker=2: should prefer count 2 over 3");
        if (ReconciliationIeaLookup.IsBetterCountTowardsBroker(3, 2, 2))
            return (false, "broker=2: should not prefer 3 over 2");
        // broker=2: prefer 2 over 0
        if (!ReconciliationIeaLookup.IsBetterCountTowardsBroker(2, 0, 2))
            return (false, "broker=2: should prefer 2 over 0");
        // tie distance: prefer fewer local orders
        if (!ReconciliationIeaLookup.IsBetterCountTowardsBroker(1, 3, 2))
            return (false, "tie distance |n-2|: prefer lower count 1 over 3");
        return (true, null);
    }
}
