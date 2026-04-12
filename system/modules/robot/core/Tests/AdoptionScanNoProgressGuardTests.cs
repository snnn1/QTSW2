// Recovery adoption no-progress skip evaluator (see RecoveryNoProgressSkipEvaluator.cs, InstrumentExecutionAuthority.NT.cs RunGatedAdoptionScanBody).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test IEA_ADOPTION_NO_PROGRESS

using System;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class AdoptionScanNoProgressGuardTests
{
    private static AdoptionScanRecoveryFingerprint Fp(
        string key = "ES 03-26",
        int accountTotal = 10,
        int candidates = 2,
        int qtsw2Same = 1,
        bool deferred = false,
        int brokerWorking = 3,
        int ieaReg = 2) =>
        new(key, accountTotal, candidates, qtsw2Same, deferred, brokerWorking, ieaReg);

    public static (bool Pass, string? Error) RunAdoptionScanNoProgressGuardTests()
    {
        var t0 = new DateTimeOffset(2026, 3, 24, 12, 0, 0, TimeSpan.Zero);
        var lastDone = t0;
        var current = Fp();
        var last = Fp();

        // First scan — no prior completed recovery snapshot
        if (RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                false, in current, in last, 0, lastDone, t0.AddSeconds(5), 20, t0))
            return (false, "Must not skip when hasLastCompletedRecoveryScan is false");

        // Same fingerprint, last adopted 0, within cooldown, no mutation after last scan
        if (!RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                true, in current, in last, 0, lastDone, t0.AddSeconds(5), 20, lastDone))
            return (false, "Expected skip when fingerprint matches, delta 0, cooldown, mutation not after last");

        // last adopted > 0 — must re-run (progress last time or registry may still need work)
        if (RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                true, in current, in last, 1, lastDone, t0.AddSeconds(5), 20, lastDone))
            return (false, "Must not skip when lastCompletedAdoptedDelta > 0");

        // Fingerprint changed
        var changed = Fp(brokerWorking: 99);
        if (RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                true, in changed, in last, 0, lastDone, t0.AddSeconds(5), 20, lastDone))
            return (false, "Must not skip when current fingerprint != last");

        // Cooldown expired
        if (RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                true, in current, in last, 0, lastDone, t0.AddSeconds(25), 20, lastDone))
            return (false, "Must not skip when elapsed >= cooldownSeconds");

        // IEA mutation after last completed scan
        if (RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                true, in current, in last, 0, lastDone, t0.AddSeconds(5), 20, t0.AddSeconds(10)))
            return (false, "Must not skip when lastIeaMutationUtc > lastCompletedUtc");

        // Cooldown disabled / invalid last time
        if (RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                true, in current, in last, 0, lastDone, t0.AddSeconds(5), 0, lastDone))
            return (false, "Must not skip when cooldownSeconds <= 0");

        if (RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                true, in current, in last, 0, DateTimeOffset.MinValue, t0.AddSeconds(5), 20, lastDone))
            return (false, "Must not skip when lastCompletedUtc is MinValue");

        // Instrument key case-insensitive equality for fingerprint
        var upper = Fp("ES 03-26");
        var lower = Fp("es 03-26");
        if (!RecoveryNoProgressSkipEvaluator.ShouldSkipRecoveryNoProgress(
                true, in upper, in lower, 0, lastDone, t0.AddSeconds(3), 20, lastDone))
            return (false, "Fingerprint keys should compare ordinal-ignore-case");

        // Diagnostic snapshot: exact fingerprint + last delta 0 + mutation after completed → mutation_after_last_completed
        var cur = Fp();
        var prev = Fp();
        var lastCompletedUtc = t0;
        var mutAfter = t0.AddSeconds(1);
        var snap = RecoveryNoProgressSkipEvaluator.BuildDiagnosticSnapshot(
            true, true, in cur, in prev, 0, lastCompletedUtc, t0.AddSeconds(5), 20, mutAfter);
        if (snap.SkipBlockedReason != RecoveryNoProgressSkipEvaluator.ReasonMutationAfterLastCompleted)
            return (false, "Expected mutation_after_last_completed when LastMutationUtc > lastCompletedUtc");
        if (snap.LastIeaMutationLteLastCompleted)
            return (false, "Snapshot should show mutation not lte last completed");

        var snapCd = RecoveryNoProgressSkipEvaluator.BuildDiagnosticSnapshot(
            true, true, in cur, in prev, 0, lastCompletedUtc, utcNow: t0.AddSeconds(60), cooldownSeconds: 20, lastIeaMutationUtc: lastCompletedUtc);
        if (snapCd.SkipBlockedReason != RecoveryNoProgressSkipEvaluator.ReasonCooldownExpired)
            return (false, "Expected cooldown_expired when elapsed >= cooldownSeconds with matching fingerprint");
        if (snapCd.WithinCooldown)
            return (false, "WithinCooldown should be false when expired");

        // Non-recovery scans are not passed to this evaluator at the call site (documented invariant).
        return (true, null);
    }
}
