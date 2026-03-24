using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Lightweight identity for recovery adoption scans — compared across runs to detect provably unchanged state.
/// All fields are captured at scan decision time (before heavy iteration).
/// </summary>
internal readonly struct AdoptionScanRecoveryFingerprint : IEquatable<AdoptionScanRecoveryFingerprint>
{
    public string ExecutionInstrumentKey { get; }
    public int AccountOrdersTotal { get; }
    public int CandidateIntentCount { get; }
    public int SameInstrumentQtsw2WorkingCount { get; }
    public bool DeferredState { get; }
    /// <summary>Working or accepted orders on the execution instrument (broker view).</summary>
    public int BrokerWorkingExecutionInstrumentCount { get; }
    /// <summary>IEA owned+adopted working count at fingerprint time.</summary>
    public int IeaRegistryWorkingCount { get; }

    public AdoptionScanRecoveryFingerprint(
        string executionInstrumentKey,
        int accountOrdersTotal,
        int candidateIntentCount,
        int sameInstrumentQtsw2WorkingCount,
        bool deferredState,
        int brokerWorkingExecutionInstrumentCount,
        int ieaRegistryWorkingCount)
    {
        ExecutionInstrumentKey = executionInstrumentKey ?? "";
        AccountOrdersTotal = accountOrdersTotal;
        CandidateIntentCount = candidateIntentCount;
        SameInstrumentQtsw2WorkingCount = sameInstrumentQtsw2WorkingCount;
        DeferredState = deferredState;
        BrokerWorkingExecutionInstrumentCount = brokerWorkingExecutionInstrumentCount;
        IeaRegistryWorkingCount = ieaRegistryWorkingCount;
    }

    public bool Equals(AdoptionScanRecoveryFingerprint other) =>
        AccountOrdersTotal == other.AccountOrdersTotal &&
        CandidateIntentCount == other.CandidateIntentCount &&
        SameInstrumentQtsw2WorkingCount == other.SameInstrumentQtsw2WorkingCount &&
        DeferredState == other.DeferredState &&
        BrokerWorkingExecutionInstrumentCount == other.BrokerWorkingExecutionInstrumentCount &&
        IeaRegistryWorkingCount == other.IeaRegistryWorkingCount &&
        string.Equals(ExecutionInstrumentKey, other.ExecutionInstrumentKey, StringComparison.OrdinalIgnoreCase);

    public override bool Equals(object? obj) => obj is AdoptionScanRecoveryFingerprint o && Equals(o);

    public override int GetHashCode()
    {
        unchecked
        {
            var h = StringComparer.OrdinalIgnoreCase.GetHashCode(ExecutionInstrumentKey);
            h = (h * 397) ^ AccountOrdersTotal;
            h = (h * 397) ^ CandidateIntentCount;
            h = (h * 397) ^ SameInstrumentQtsw2WorkingCount;
            h = (h * 397) ^ DeferredState.GetHashCode();
            h = (h * 397) ^ BrokerWorkingExecutionInstrumentCount;
            h = (h * 397) ^ IeaRegistryWorkingCount;
            return h;
        }
    }

    public static bool operator ==(AdoptionScanRecoveryFingerprint a, AdoptionScanRecoveryFingerprint b) => a.Equals(b);

    public static bool operator !=(AdoptionScanRecoveryFingerprint a, AdoptionScanRecoveryFingerprint b) => !a.Equals(b);

    /// <summary>Number of fingerprint components that differ (0 = full equality, 7 = all differ). Instrument key compared case-insensitively.</summary>
    public static int CountFieldMismatches(in AdoptionScanRecoveryFingerprint a, in AdoptionScanRecoveryFingerprint b)
    {
        var n = 0;
        if (!string.Equals(a.ExecutionInstrumentKey, b.ExecutionInstrumentKey, StringComparison.OrdinalIgnoreCase))
            n++;
        if (a.AccountOrdersTotal != b.AccountOrdersTotal) n++;
        if (a.CandidateIntentCount != b.CandidateIntentCount) n++;
        if (a.SameInstrumentQtsw2WorkingCount != b.SameInstrumentQtsw2WorkingCount) n++;
        if (a.DeferredState != b.DeferredState) n++;
        if (a.BrokerWorkingExecutionInstrumentCount != b.BrokerWorkingExecutionInstrumentCount) n++;
        if (a.IeaRegistryWorkingCount != b.IeaRegistryWorkingCount) n++;
        return n;
    }
}
