using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Per-evaluation snapshot for release-readiness suppression (pre–full-eval path).
/// </summary>
public readonly struct ReleaseReadinessSuppressionProbe
{
    public int BrokerPositionQty { get; init; }
    public int BrokerWorkingCount { get; init; }
    public int JournalOpenQty { get; init; }
    public int PendingCandidateCount { get; init; }
    public int IeaTrustedWorkingCount { get; init; }
    public bool UseIea { get; init; }
    /// <summary>64-bit deterministic surrogate; not cryptographic. Mixed with set cardinality internally.</summary>
    public long BlockingAdoptionIntentSetHash { get; init; }

    public long RegistryMismatchTrustedIntentSetHash { get; init; }
    public long JournalOpenIntentSetHash { get; init; }
}

/// <summary>
/// Skips redundant release-readiness work when material + structural inputs are stable.
/// Does not alter evaluator semantics — only when full evaluation runs.
/// </summary>
public sealed class ReleaseReconciliationRedundancySuppression
{
    private long _executionActivityGeneration;

    public void NotifyExecutionActivity() => Interlocked.Increment(ref _executionActivityGeneration);

    public long ExecutionActivityGeneration => Volatile.Read(ref _executionActivityGeneration);

    private sealed class InstrumentReleaseCache
    {
        public string Fingerprint = "";
        public string StructuralSuppressionKey = "";
        public long ActivityGenAtFullEval;
        public DateTimeOffset LastFullEvalUtc;
        public DateTimeOffset NextFullEvalNotBeforeUtc;
        public int ConsecutiveUnchangedStreak;
        public int LastBrokerPositionQty;
        public int LastBrokerWorkingCount;
        public StateConsistencyReleaseReadinessResult? CachedResult;
        public string LastAuditFingerprint = "";
        public DateTimeOffset LastAuditEmitUtc = DateTimeOffset.MinValue;
        public string LastBlockingAuditFingerprint = "";
        public DateTimeOffset LastBlockingAuditEmitUtc = DateTimeOffset.MinValue;
    }

    private readonly ConcurrentDictionary<string, InstrumentReleaseCache> _releaseByInstrument =
        new(StringComparer.OrdinalIgnoreCase);

    private string? _periodicPassSignature;
    private long _periodicPassActivityGen = long.MinValue;
    private DateTimeOffset _periodicLastFullPassUtc = DateTimeOffset.MinValue;
    private int _periodicSkipStreak;

    private const int ReleaseEvalBaseBackoffMs = 400;
    private const int ReleaseEvalMaxBackoffMs = 30_000;
    private const int ReleaseEvalMandatoryRefreshMs = 45_000;
    private const int ReleaseEvalBackoffCapPendingMs = 2_000;
    private const int ReleaseEvalBackoffCapPositionMismatchMs = 1_000;

    private const int PeriodicPassBaseBackoffMs = 10_000;
    private const int PeriodicPassMaxBackoffMs = 90_000;
    private const int PeriodicPassMandatoryRefreshMs = 120_000;

    private static readonly TimeSpan ReadinessAuditMinRepeat = TimeSpan.FromSeconds(30);

    private const ulong Fnv1a64Offset = 14695981039346656037UL;
    private const ulong Fnv1a64Prime = 1099511628211UL;

    /// <summary>
    /// Deterministic 64-bit FNV-1a: sort (ordinal ignore case), hash UTF-16 chars uppercased, boundaries between ids,
    /// then mix list cardinality (reduces collision vs raw 32-bit aggregate).
    /// </summary>
    public static long ComputeStableOrderedStringSetHash(IReadOnlyCollection<string>? ids)
    {
        if (ids == null || ids.Count == 0) return 0;
        var arr = new string[ids.Count];
        var i = 0;
        foreach (var id in ids)
        {
            if (!string.IsNullOrEmpty(id))
                arr[i++] = id;
        }
        if (i == 0) return 0;
        if (i < arr.Length)
            Array.Resize(ref arr, i);
        Array.Sort(arr, StringComparer.OrdinalIgnoreCase);
        return HashOrderedStringArray64(arr);
    }

    /// <summary>Caller must sort with <see cref="StringComparer.OrdinalIgnoreCase"/> first.</summary>
    public static long ComputeStableOrderedStringSetHashFromSortedList(IReadOnlyList<string> sortedIds) =>
        sortedIds == null || sortedIds.Count == 0 ? 0L : HashOrderedStringArray64(sortedIds);

    private static long HashOrderedStringArray64(IReadOnlyList<string> sorted)
    {
        var h = Fnv1a64Offset;
        for (var j = 0; j < sorted.Count; j++)
        {
            var s = sorted[j];
            if (!string.IsNullOrEmpty(s))
            {
                foreach (var c in s)
                {
                    h ^= char.ToUpperInvariant(c);
                    h *= Fnv1a64Prime;
                }
            }

            h ^= 0x7DUL; // boundary between sorted ids (disambiguates concatenation ambiguity)
            h *= Fnv1a64Prime;
        }

        h ^= (ulong)(uint)sorted.Count;
        h *= Fnv1a64Prime;
        return unchecked((long)h);
    }

    /// <summary>64-bit FNV-1a of fingerprint string + length mix (DEBUG / dedupe; not cryptographic).</summary>
    public static long ComputeFingerprintHash64(string? materialFingerprint)
    {
        if (string.IsNullOrEmpty(materialFingerprint)) return 0;
        var h = Fnv1a64Offset;
        foreach (var c in materialFingerprint)
        {
            h ^= c;
            h *= Fnv1a64Prime;
        }

        h ^= (ulong)(uint)materialFingerprint.Length;
        h *= Fnv1a64Prime;
        return unchecked((long)h);
    }

    public static string BuildStructuralSuppressionKey(in ReleaseReadinessSuppressionProbe p) =>
        string.Join("|",
            p.BrokerPositionQty,
            p.BrokerWorkingCount,
            p.JournalOpenQty,
            p.PendingCandidateCount,
            p.IeaTrustedWorkingCount,
            p.UseIea ? 1 : 0,
            p.BlockingAdoptionIntentSetHash,
            p.RegistryMismatchTrustedIntentSetHash,
            p.JournalOpenIntentSetHash);

    public static string BuildStructuralSuppressionKeyFromInput(StateConsistencyReleaseEvaluationInput i) =>
        string.Join("|",
            i.BrokerPositionQty,
            i.BrokerWorkingCount,
            i.JournalOpenQty,
            i.PendingAdoptionCandidateCount,
            i.IeaOwnedPlusAdoptedWorking,
            i.UseInstrumentExecutionAuthority ? 1 : 0,
            i.BlockingAdoptionIntentSetHash,
            i.RegistryMismatchTrustedIntentSetHash,
            i.JournalOpenIntentSetHash);

    public static string BuildReleaseMaterialFingerprint(StateConsistencyReleaseEvaluationInput input,
        StateConsistencyReleaseReadinessResult result)
    {
        var contra = result.Contradictions is { Count: > 0 }
            ? string.Join(";", result.Contradictions.OrderBy(s => s, StringComparer.Ordinal))
            : "";
        return string.Join("|",
            input.BrokerPositionQty,
            input.BrokerWorkingCount,
            input.JournalOpenQty,
            input.PendingAdoptionCandidateCount,
            input.IeaOwnedPlusAdoptedWorking,
            input.BlockingAdoptionIntentSetHash,
            input.RegistryMismatchTrustedIntentSetHash,
            input.JournalOpenIntentSetHash,
            result.ReleaseReady ? 1 : 0,
            contra);
    }

    private static int ComputeBackoffMs(int streak, int basis, int cap)
    {
        var exp = Math.Min(Math.Max(streak, 0), 8);
        var mult = 1 << exp;
        var ms = basis * mult;
        if (ms < 0) ms = cap;
        return Math.Min(ms, cap);
    }

    /// <summary>Try return cached readiness; <paramref name="reason"/> is for DEBUG diagnostics only.</summary>
    public bool TryGetCachedReleaseReadiness(
        string instrument,
        in ReleaseReadinessSuppressionProbe probe,
        long activityGeneration,
        DateTimeOffset utcNow,
        out StateConsistencyReleaseReadinessResult? cached,
        out string reason)
    {
        cached = null;
        reason = "cache_miss";
        var inst = instrument.Trim();
        if (string.IsNullOrEmpty(inst)) return false;

        if (probe.PendingCandidateCount > 0)
        {
            reason = "forced_eval_non_idle_pending";
            return false;
        }

        if (Math.Abs(probe.BrokerPositionQty - probe.JournalOpenQty) > 0)
        {
            reason = "forced_eval_position_qty_mismatch";
            return false;
        }

        if (probe.UseIea && probe.BrokerWorkingCount > 0 && probe.IeaTrustedWorkingCount == 0)
        {
            reason = "forced_eval_broker_working_without_iea_trust";
            return false;
        }

        var probeKey = BuildStructuralSuppressionKey(probe);

        if (!_releaseByInstrument.TryGetValue(inst, out var c) || c.CachedResult == null)
        {
            reason = "cache_miss";
            return false;
        }

        if (activityGeneration != c.ActivityGenAtFullEval)
        {
            reason = "no_activity_match_failed";
            return false;
        }

        if (probe.BrokerPositionQty != c.LastBrokerPositionQty || probe.BrokerWorkingCount != c.LastBrokerWorkingCount)
        {
            reason = "broker_snapshot_changed";
            return false;
        }

        if (!string.Equals(probeKey, c.StructuralSuppressionKey, StringComparison.Ordinal))
        {
            reason = "structural_fingerprint_mismatch";
            return false;
        }

        if ((utcNow - c.LastFullEvalUtc).TotalMilliseconds >= ReleaseEvalMandatoryRefreshMs)
        {
            reason = "forced_eval_mandatory_refresh";
            return false;
        }

        if (utcNow < c.NextFullEvalNotBeforeUtc)
        {
            cached = CloneReadiness(c.CachedResult);
            reason = "suppressed_redundant_eval";
            return true;
        }

        reason = "backoff_elapsed";
        return false;
    }

    public void RecordReleaseFullEvaluation(
        string instrument,
        StateConsistencyReleaseEvaluationInput input,
        StateConsistencyReleaseReadinessResult result,
        DateTimeOffset utcNow,
        long activityGenerationAtStart)
    {
        var inst = (input.Instrument ?? instrument).Trim();
        if (string.IsNullOrEmpty(inst)) return;

        var fp = BuildReleaseMaterialFingerprint(input, result);
        var structuralKey = BuildStructuralSuppressionKeyFromInput(input);
        var c = _releaseByInstrument.GetOrAdd(inst, _ => new InstrumentReleaseCache());

        var unchanged = fp == c.Fingerprint && activityGenerationAtStart == c.ActivityGenAtFullEval;
        if (unchanged && !string.IsNullOrEmpty(c.Fingerprint))
            c.ConsecutiveUnchangedStreak++;
        else
            c.ConsecutiveUnchangedStreak = 0;

        c.Fingerprint = fp;
        c.StructuralSuppressionKey = structuralKey;
        c.ActivityGenAtFullEval = activityGenerationAtStart;
        c.LastFullEvalUtc = utcNow;
        c.LastBrokerPositionQty = input.BrokerPositionQty;
        c.LastBrokerWorkingCount = input.BrokerWorkingCount;
        c.CachedResult = CloneReadiness(result);

        var skipMs = ComputeBackoffMs(c.ConsecutiveUnchangedStreak, ReleaseEvalBaseBackoffMs, ReleaseEvalMaxBackoffMs);
        if (input.PendingAdoptionCandidateCount > 0)
            skipMs = Math.Min(skipMs, ReleaseEvalBackoffCapPendingMs);
        if (Math.Abs(input.BrokerPositionQty - input.JournalOpenQty) > 0)
            skipMs = Math.Min(skipMs, ReleaseEvalBackoffCapPositionMismatchMs);

        c.NextFullEvalNotBeforeUtc = utcNow.AddMilliseconds(skipMs);
    }

    public bool ShouldSuppressIdenticalReadinessAudit(string instrument, string materialFingerprint, DateTimeOffset utcNow)
    {
        var inst = instrument.Trim();
        if (string.IsNullOrEmpty(inst)) return false;
        var c = _releaseByInstrument.GetOrAdd(inst, _ => new InstrumentReleaseCache());
        return c.LastAuditFingerprint == materialFingerprint &&
               c.LastAuditEmitUtc != DateTimeOffset.MinValue &&
               (utcNow - c.LastAuditEmitUtc) < ReadinessAuditMinRepeat;
    }

    public void MarkReadinessAuditEmitted(string instrument, string materialFingerprint, DateTimeOffset utcNow)
    {
        var inst = instrument.Trim();
        if (string.IsNullOrEmpty(inst)) return;
        var c = _releaseByInstrument.GetOrAdd(inst, _ => new InstrumentReleaseCache());
        c.LastAuditFingerprint = materialFingerprint;
        c.LastAuditEmitUtc = utcNow;
    }

    public static string BuildBlockingCandidateAuditFingerprint(StateConsistencyReleaseEvaluationInput input)
    {
        var inst = input.Instrument?.Trim() ?? "";
        return string.Join("|", inst, input.BrokerPositionQty, input.BrokerWorkingCount, input.JournalOpenQty,
            input.PendingAdoptionCandidateCount, input.IeaOwnedPlusAdoptedWorking,
            input.BlockingAdoptionIntentSetHash, input.RegistryMismatchTrustedIntentSetHash, input.JournalOpenIntentSetHash);
    }

    public bool ShouldSuppressBlockingCandidateAudit(string instrument, string fingerprint, DateTimeOffset utcNow)
    {
        var inst = instrument.Trim();
        if (string.IsNullOrEmpty(inst)) return false;
        var c = _releaseByInstrument.GetOrAdd(inst, _ => new InstrumentReleaseCache());
        return c.LastBlockingAuditFingerprint == fingerprint &&
               c.LastBlockingAuditEmitUtc != DateTimeOffset.MinValue &&
               (utcNow - c.LastBlockingAuditEmitUtc) < ReadinessAuditMinRepeat;
    }

    public void MarkBlockingCandidateAuditEmitted(string instrument, string fingerprint, DateTimeOffset utcNow)
    {
        var inst = instrument.Trim();
        if (string.IsNullOrEmpty(inst)) return;
        var c = _releaseByInstrument.GetOrAdd(inst, _ => new InstrumentReleaseCache());
        c.LastBlockingAuditFingerprint = fingerprint;
        c.LastBlockingAuditEmitUtc = utcNow;
    }

    public static StateConsistencyReleaseReadinessResult CloneReadiness(StateConsistencyReleaseReadinessResult r)
    {
        return new StateConsistencyReleaseReadinessResult
        {
            Instrument = r.Instrument,
            IsExplainable = r.IsExplainable,
            ReleaseReady = r.ReleaseReady,
            BrokerPositionExplainable = r.BrokerPositionExplainable,
            BrokerWorkingExplainable = r.BrokerWorkingExplainable,
            LocalStateCoherent = r.LocalStateCoherent,
            PendingAdoptionExists = r.PendingAdoptionExists,
            UnexplainedBrokerPositionQty = r.UnexplainedBrokerPositionQty,
            UnexplainedBrokerWorkingCount = r.UnexplainedBrokerWorkingCount,
            Contradictions = r.Contradictions != null ? new List<string>(r.Contradictions) : new List<string>(),
            Summary = r.Summary ?? "",
            SnapshotSufficient = r.SnapshotSufficient,
            DiagnosticBrokerPositionQty = r.DiagnosticBrokerPositionQty,
            DiagnosticJournalOpenQty = r.DiagnosticJournalOpenQty,
            DiagnosticBrokerWorkingCount = r.DiagnosticBrokerWorkingCount,
            DiagnosticIeaOwnedPlusAdoptedWorking = r.DiagnosticIeaOwnedPlusAdoptedWorking,
            DiagnosticPendingAdoptionCandidateCount = r.DiagnosticPendingAdoptionCandidateCount
        };
    }

    public static string BuildPeriodicPassSignature(
        Dictionary<string, int> accountQtyByInstrument,
        Dictionary<string, List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry)>> openByInstrument)
    {
        var sb = new StringBuilder(256);
        foreach (var k in accountQtyByInstrument.Keys.OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(k).Append('=').Append(accountQtyByInstrument[k]).Append(';');
        }

        sb.Append("openinst:").Append(openByInstrument.Count).Append(';');
        foreach (var kv in openByInstrument.OrderBy(x => x.Key, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(kv.Key).Append(':').Append(kv.Value.Count).Append(':');
            foreach (var e in kv.Value.OrderBy(x => x.IntentId, StringComparer.OrdinalIgnoreCase))
                sb.Append(e.IntentId).Append(',');
            sb.Append(';');
        }

        return sb.ToString();
    }

    public bool TrySkipRedundantPeriodicPass(string signature, long activityGeneration, DateTimeOffset utcNow)
    {
        if (_periodicPassSignature == null || signature != _periodicPassSignature || activityGeneration != _periodicPassActivityGen)
        {
            var signatureOrGenChanged = _periodicPassSignature != null &&
                                      (signature != _periodicPassSignature || activityGeneration != _periodicPassActivityGen);
            _periodicPassSignature = signature;
            _periodicPassActivityGen = activityGeneration;
            _periodicSkipStreak = 0;
            if (signatureOrGenChanged)
                _periodicLastFullPassUtc = DateTimeOffset.MinValue;
            return false;
        }

        if (_periodicLastFullPassUtc == DateTimeOffset.MinValue)
            return false;

        var sinceMs = (utcNow - _periodicLastFullPassUtc).TotalMilliseconds;
        if (sinceMs >= PeriodicPassMandatoryRefreshMs)
            return false;

        var backoffMs = ComputeBackoffMs(_periodicSkipStreak, PeriodicPassBaseBackoffMs, PeriodicPassMaxBackoffMs);
        if (sinceMs >= backoffMs)
            return false;

        _periodicSkipStreak = Math.Min(_periodicSkipStreak + 1, 32);
        return true;
    }

    public void NotifyPeriodicReconciliationPassCompleted(string signature, long activityGeneration, DateTimeOffset utcNow)
    {
        _periodicPassSignature = signature;
        _periodicPassActivityGen = activityGeneration;
        _periodicLastFullPassUtc = utcNow;
        _periodicSkipStreak = 0;
    }
}
