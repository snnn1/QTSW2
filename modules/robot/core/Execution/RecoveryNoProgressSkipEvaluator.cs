using System;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Clause-level view of why a recovery no-progress skip did or did not apply (instrumentation / audits).
/// </summary>
internal readonly struct RecoveryNoProgressSkipDiagnosticSnapshot
{
    public bool HasLastCompletedRecoveryScan { get; }
    public bool LastCompletedAdoptedDeltaZero { get; }
    public bool FingerprintEqual { get; }
    public bool CooldownPositive { get; }
    public bool LastCompletedUtcValid { get; }
    public bool WithinCooldown { get; }
    public bool LastIeaMutationLteLastCompleted { get; }
    public int FingerprintFieldMismatchCount { get; }
    /// <summary>First failing clause in the same order as <see cref="ShouldSkipRecoveryNoProgress"/> (skip blocked at this reason).</summary>
    public string SkipBlockedReason { get; }
    public double? SecondsSinceLastCompleted { get; }

    public RecoveryNoProgressSkipDiagnosticSnapshot(
        bool hasLastCompletedRecoveryScan,
        bool lastCompletedAdoptedDeltaZero,
        bool fingerprintEqual,
        bool cooldownPositive,
        bool lastCompletedUtcValid,
        bool withinCooldown,
        bool lastIeaMutationLteLastCompleted,
        int fingerprintFieldMismatchCount,
        string skipBlockedReason,
        double? secondsSinceLastCompleted)
    {
        HasLastCompletedRecoveryScan = hasLastCompletedRecoveryScan;
        LastCompletedAdoptedDeltaZero = lastCompletedAdoptedDeltaZero;
        FingerprintEqual = fingerprintEqual;
        CooldownPositive = cooldownPositive;
        LastCompletedUtcValid = lastCompletedUtcValid;
        WithinCooldown = withinCooldown;
        LastIeaMutationLteLastCompleted = lastIeaMutationLteLastCompleted;
        FingerprintFieldMismatchCount = fingerprintFieldMismatchCount;
        SkipBlockedReason = skipBlockedReason ?? "other";
        SecondsSinceLastCompleted = secondsSinceLastCompleted;
    }
}

/// <summary>
/// Pure logic for recovery adoption no-progress short-circuit. Fail-safe: any uncertainty → do not skip.
/// </summary>
internal static class RecoveryNoProgressSkipEvaluator
{
    public const string ReasonNoLastCompleted = "no_last_completed";
    public const string ReasonLastAdoptedDeltaNonzero = "last_adopted_delta_nonzero";
    public const string ReasonFingerprintChanged = "fingerprint_changed";
    public const string ReasonCooldownExpired = "cooldown_expired";
    public const string ReasonMutationAfterLastCompleted = "mutation_after_last_completed";
    public const string ReasonNonRecoverySource = "non_recovery_source";
    public const string ReasonOther = "other";

    /// <summary>
    /// True if a recovery adoption scan can be skipped because state matches last completed no-progress scan within cooldown.
    /// </summary>
    public static bool ShouldSkipRecoveryNoProgress(
        bool hasLastCompletedRecoveryScan,
        in AdoptionScanRecoveryFingerprint current,
        in AdoptionScanRecoveryFingerprint lastCompleted,
        int lastCompletedAdoptedDelta,
        DateTimeOffset lastCompletedUtc,
        DateTimeOffset utcNow,
        double cooldownSeconds,
        DateTimeOffset lastIeaMutationUtc)
    {
        if (!hasLastCompletedRecoveryScan)
            return false;
        if (lastCompletedAdoptedDelta > 0)
            return false;
        if (current != lastCompleted)
            return false;
        if (cooldownSeconds <= 0 || lastCompletedUtc == DateTimeOffset.MinValue)
            return false;
        if ((utcNow - lastCompletedUtc).TotalSeconds >= cooldownSeconds)
            return false;
        // Any IEA mutation after the last completed scan may have changed registry truth — re-run.
        if (lastIeaMutationUtc > lastCompletedUtc)
            return false;
        return true;
    }

    /// <summary>
    /// Diagnostics for logging when skip was not taken. <paramref name="scanIsRecovery"/> false → <see cref="ReasonNonRecoverySource"/>.
    /// </summary>
    public static RecoveryNoProgressSkipDiagnosticSnapshot BuildDiagnosticSnapshot(
        bool scanIsRecovery,
        bool hasLastCompletedRecoveryScan,
        in AdoptionScanRecoveryFingerprint current,
        in AdoptionScanRecoveryFingerprint lastCompleted,
        int lastCompletedAdoptedDelta,
        DateTimeOffset lastCompletedUtc,
        DateTimeOffset utcNow,
        double cooldownSeconds,
        DateTimeOffset lastIeaMutationUtc)
    {
        if (!scanIsRecovery)
        {
            return new RecoveryNoProgressSkipDiagnosticSnapshot(
                false, false, false, cooldownSeconds > 0, lastCompletedUtc != DateTimeOffset.MinValue,
                false, false, AdoptionScanRecoveryFingerprint.CountFieldMismatches(in current, in lastCompleted),
                ReasonNonRecoverySource, SecondsSinceLast(lastCompletedUtc, utcNow));
        }

        var mismatchCount = AdoptionScanRecoveryFingerprint.CountFieldMismatches(in current, in lastCompleted);
        var fingerprintEqual = mismatchCount == 0;
        var lastDeltaZero = lastCompletedAdoptedDelta == 0;
        var cooldownPositive = cooldownSeconds > 0;
        var lastUtcValid = lastCompletedUtc != DateTimeOffset.MinValue;
        var withinCooldown = cooldownPositive && lastUtcValid &&
                             (utcNow - lastCompletedUtc).TotalSeconds < cooldownSeconds;
        var mutationOk = lastIeaMutationUtc <= lastCompletedUtc;

        var reason = ResolveSkipBlockedReason(
            hasLastCompletedRecoveryScan,
            lastDeltaZero,
            fingerprintEqual,
            cooldownPositive,
            lastUtcValid,
            withinCooldown,
            mutationOk);

        return new RecoveryNoProgressSkipDiagnosticSnapshot(
            hasLastCompletedRecoveryScan,
            lastDeltaZero,
            fingerprintEqual,
            cooldownPositive,
            lastUtcValid,
            withinCooldown,
            mutationOk,
            mismatchCount,
            reason,
            SecondsSinceLast(lastCompletedUtc, utcNow));
    }

    private static double? SecondsSinceLast(DateTimeOffset lastCompletedUtc, DateTimeOffset utcNow)
    {
        if (lastCompletedUtc == DateTimeOffset.MinValue) return null;
        return (utcNow - lastCompletedUtc).TotalSeconds;
    }

    /// <summary>First clause that prevents skip (mirrors <see cref="ShouldSkipRecoveryNoProgress"/> order).</summary>
    public static string ResolveSkipBlockedReason(
        bool hasLastCompletedRecoveryScan,
        bool lastCompletedAdoptedDeltaZero,
        bool fingerprintEqual,
        bool cooldownPositive,
        bool lastCompletedUtcValid,
        bool withinCooldown,
        bool lastIeaMutationLteLastCompleted)
    {
        if (!hasLastCompletedRecoveryScan)
            return ReasonNoLastCompleted;
        if (!lastCompletedAdoptedDeltaZero)
            return ReasonLastAdoptedDeltaNonzero;
        if (!fingerprintEqual)
            return ReasonFingerprintChanged;
        if (!cooldownPositive || !lastCompletedUtcValid)
            return ReasonOther;
        if (!withinCooldown)
            return ReasonCooldownExpired;
        if (!lastIeaMutationLteLastCompleted)
            return ReasonMutationAfterLastCompleted;
        return ReasonOther;
    }
}
