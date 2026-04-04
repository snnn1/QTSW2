// Proof tests for tagged broker-without-journal recovery and lifecycle submit ordering.
// Built into Robot.Core; run via harness: --test TAGGED_BROKER_JOURNAL_REPAIR
// Or reference static methods from a test host.

using System;
using System.IO;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class JournalRepairProofTests
{
    /// <summary>Test 2/3/4 partial: recovery upsert produces open journal qty for instrument.</summary>
    public static (bool Pass, string? Error) RunTaggedRecoveryJournalUpsertProof()
    {
        var g = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "qtsw2_journal_repair_proof_" + g.Substring(0, Math.Min(8, g.Length)));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new QTSW2.Robot.Core.RobotLogger(dir);
            var journal = new ExecutionJournal(dir, log);
            var utc = DateTimeOffset.UtcNow;
            journal.UpsertTaggedBrokerExposureRecoveryJournal(
                "intent_proof_1",
                "2026-03-30",
                "S1",
                "MNG",
                "broker-oid-1",
                2,
                "Long",
                100.5m,
                95m,
                110m,
                utc,
                "proof");

            var sum = journal.GetOpenJournalQuantitySumForInstrument("MNG", null);
            if (sum != 2)
                return (false, $"Expected open journal sum 2, got {sum}");
            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Test 1: SUBMIT_ENTRY precedes ENTRY_ACCEPTED in validator (FSM legality).</summary>
    public static (bool Pass, string? Error) RunLifecycleStreamSubmitLegalityProof()
    {
        var cur = IntentLifecycleState.CREATED;
        var (v1, s1) = IntentLifecycleValidator.TryGetTransition(cur, IntentLifecycleTransition.SUBMIT_ENTRY);
        if (!v1 || s1 != IntentLifecycleState.ENTRY_SUBMITTED)
            return (false, "CREATED -> SUBMIT_ENTRY should reach ENTRY_SUBMITTED");
        cur = s1;
        var (v2, s2) = IntentLifecycleValidator.TryGetTransition(cur, IntentLifecycleTransition.ENTRY_ACCEPTED);
        if (!v2 || s2 != IntentLifecycleState.ENTRY_WORKING)
            return (false, "ENTRY_SUBMITTED -> ENTRY_ACCEPTED should reach ENTRY_WORKING");
        var (vBad, _) = IntentLifecycleValidator.TryGetTransition(IntentLifecycleState.CREATED, IntentLifecycleTransition.ENTRY_ACCEPTED);
        if (vBad)
            return (false, "CREATED -> ENTRY_ACCEPTED must remain invalid");
        return (true, null);
    }

    /// <summary>Test 5: untracked path remains distinct (no intent id in upsert API).</summary>
    /// <summary>
    /// Hardening: fill recorded first, then late submission observation — canonical submit must not be stamped from the later observation.
    /// </summary>
    public static (bool Pass, string? Error) RunLateSubmitObservedAfterFillDoesNotSetCanonicalSubmitUtc()
    {
        var g = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "qtsw2_journal_submit_order_" + g.Substring(0, Math.Min(8, g.Length)));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new RobotLogger(dir);
            var journal = new ExecutionJournal(dir, log);
            var tFill = new DateTimeOffset(2026, 4, 1, 10, 0, 0, TimeSpan.Zero);
            var tSubObs = new DateTimeOffset(2026, 4, 1, 11, 0, 0, TimeSpan.Zero);
            const string intentId = "intent_order_proof";
            const string td = "2026-04-01";
            const string stream = "S1";
            journal.RecordEntryFill(intentId, td, stream, 100m, 1, tFill, 5m, "Long", "MES", "MES", null);
            journal.RecordSubmission(intentId, td, stream, "MES", "ENTRY", "oid1", tSubObs);

            var jpath = Path.Combine(dir, "data", "execution_journals", $"{td}_{stream}_{intentId}.json");
            if (!File.Exists(jpath))
                return (false, "journal json missing");
            var entry = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(jpath));
            if (entry == null)
                return (false, "deserialize failed");
            if (!string.IsNullOrEmpty(entry.EntrySubmittedAtUtc))
                return (false, $"EntrySubmittedAtUtc should remain unset; got {entry.EntrySubmittedAtUtc}");
            if (!entry.IsReconstructedSubmission)
                return (false, "IsReconstructedSubmission expected true");
            if (string.IsNullOrEmpty(entry.EntrySubmittedObservedAtUtc))
                return (false, "EntrySubmittedObservedAtUtc should capture late observation");
            if (string.IsNullOrEmpty(entry.EntryFilledAtUtc) || entry.EntryFilledAtUtc != tFill.ToString("o"))
                return (false, $"EntryFilledAtUtc should be first fill canonical; got {entry.EntryFilledAtUtc}");
            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { /* best-effort */ }
        }
    }

    public static (bool Pass, string? Error) RunUntrackedRecoveryDistinctFromTaggedProof()
    {
        var g = Guid.NewGuid().ToString("N");
        var dir = Path.Combine(Path.GetTempPath(), "qtsw2_untracked_proof_" + g.Substring(0, Math.Min(8, g.Length)));
        Directory.CreateDirectory(dir);
        try
        {
            var log = new QTSW2.Robot.Core.RobotLogger(dir);
            var journal = new ExecutionJournal(dir, log);
            var utc = DateTimeOffset.UtcNow;
            journal.UpsertUntrackedFillRecoveryJournal("MES", "u1", 1, 4000m, utc, "x");
            journal.UpsertTaggedBrokerExposureRecoveryJournal(
                "tagged_real_id",
                "2026-03-30",
                "S9",
                "MES",
                "b2",
                1,
                "Short",
                3999m,
                null,
                null,
                utc,
                "y");
            var untracked = journal.GetOpenJournalQuantitySumForInstrument("MES", null);
            if (untracked < 1)
                return (false, "Untracked + tagged recovery should yield positive open qty");
            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(dir))
                    Directory.Delete(dir, true);
            }
            catch { /* best-effort */ }
        }
    }

    public static (bool Pass, string? Error) RunAll()
    {
        var a = RunLifecycleStreamSubmitLegalityProof();
        if (!a.Pass) return (false, $"lifecycle: {a.Error}");
        var b = RunTaggedRecoveryJournalUpsertProof();
        if (!b.Pass) return (false, $"tagged_upsert: {b.Error}");
        var c = RunUntrackedRecoveryDistinctFromTaggedProof();
        if (!c.Pass) return (false, $"untracked_distinct: {c.Error}");
        var d = RunLateSubmitObservedAfterFillDoesNotSetCanonicalSubmitUtc();
        if (!d.Pass) return (false, $"late_submit_order: {d.Error}");
        return (true, null);
    }
}
