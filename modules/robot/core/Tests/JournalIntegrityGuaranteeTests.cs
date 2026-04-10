// Journal integrity guarantee layer — authoritative parity + bounded escalation.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test JOURNAL_INTEGRITY_GUARANTEE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class JournalIntegrityGuaranteeTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var prevHard = FeatureFlags.EnableHardFailClosedJournalIntegrity;
        FeatureFlags.EnableHardFailClosedJournalIntegrity = false;
        try
        {
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            var e = Case_ParityOk_Classification();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_InsufficientData_NullSnapshot();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_UnknownOrder_UntaggedWorking();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_PositionMismatch();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_WorkingMismatch_NoIea();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_OrphanEscalation_BoundedAttempts();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredIntent_BrokerOpenEmptyJournal_ReachesParity();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredIntent_TwoContracts();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredIntent_SecondPassNoDuplicateRow();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredIntent_MixedNonRecoveredPlusDeficit();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredIntent_StructuralOpenQtyMatchesBroker();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredIntent_HedgedBrokerConflict_FailClosed();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredSupersededBy_RealStrategyMatchesBroker();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredSupersededBy_NoClose_OnlyRecovered();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_RecoveredSupersededBy_NoClose_PartialNonRecovered();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_ParityPending_A01_HappyPath();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_ParityPending_A02_CatchUpParityOk();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_ParityPending_D_WrongSignRejected();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_ParityPending_F_StaleAppliedKeyRejected();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_ParityPending_G_TwoFillsAggregated();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_ParityPending_H_ClearLedgerNoExplain();
            if (e != null) return (false, e);
            JournalIntegrityGuarantee.ResetAttemptsForTests();
            e = Case_ParityPending_EnsureIntegrityNoRepair();
            if (e != null) return (false, e);
            return (true, null);
        }
        finally
        {
            FeatureFlags.EnableHardFailClosedJournalIntegrity = prevHard;
        }
    }

    private static string? Case_ParityOk_Classification()
    {
        var utc = DateTimeOffset.UtcNow;
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.PARITY_OK)
            return "parity_ok: flat/flat should be PARITY_OK";
        return null;
    }

    private static string? Case_InsufficientData_NullSnapshot()
    {
        using var tmp = NewTempJournal();
        var r = JournalParityChecker.CheckJournalParity("ES", null, tmp.Journal, new TestRegistryView(true, -1), "ES", DateTimeOffset.UtcNow);
        if (r.Status != JournalParityStatus.INSUFFICIENT_DATA)
            return "expected INSUFFICIENT_DATA for null snapshot";
        return null;
    }

    private static string? Case_UnknownOrder_UntaggedWorking()
    {
        var utc = DateTimeOffset.UtcNow;
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>
            {
                new()
                {
                    Instrument = "ES",
                    Quantity = 1,
                    Tag = "external",
                    OcoGroup = ""
                }
            }
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(true, 0);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.UNKNOWN_ORDER_PRESENT || r.OrphanOrdersDetected < 1)
            return "untagged working should be UNKNOWN_ORDER_PRESENT with orphan count";
        return null;
    }

    private static string? Case_PositionMismatch()
    {
        var utc = DateTimeOffset.UtcNow;
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 5 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.POSITION_MISMATCH || r.UnexplainedPositionQty <= 0)
            return "broker qty with empty journal should be POSITION_MISMATCH";
        return null;
    }

    private static string? Case_WorkingMismatch_NoIea()
    {
        var utc = DateTimeOffset.UtcNow;
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>
            {
                new()
                {
                    Instrument = "ES",
                    Quantity = 1,
                    Tag = "QTSW2:i1",
                    OcoGroup = ""
                }
            }
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.WORKING_ORDER_MISMATCH)
            return "broker working with zero iea explainability (IEA off) should be WORKING_ORDER_MISMATCH";
        return null;
    }

    private static string? Case_OrphanEscalation_BoundedAttempts()
    {
        JournalIntegrityGuarantee.ResetAttemptsForTests();
        var utc = DateTimeOffset.UtcNow;
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>
            {
                new() { Instrument = "ES", Quantity = 1, Tag = "ext", OcoGroup = "" }
            }
        };
        using var tmp = NewTempJournal();
        string? lastEvt = null;
        void Log(string e, string _, object? __) => lastEvt = e;

        for (var i = 0; i < JournalIntegrityGuarantee.MaxIntegrityRepairAttemptsBeforeEscalation - 1; i++)
        {
            var res = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, new TestRegistryView(true, 0),
                "ES", "ES", Array.Empty<string>(), null, null, utc, Log, allowReconstruction: true);
            if (res.EscalationEmitted)
                return "orphan: should not escalate before max attempts";
        }

        var final = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, new TestRegistryView(true, 0),
            "ES", "ES", Array.Empty<string>(), null, null, utc, Log, allowReconstruction: true);
        if (!final.EscalationEmitted || lastEvt != "RECONCILIATION_JOURNAL_INTEGRITY_FAILED")
            return "expected RECONCILIATION_JOURNAL_INTEGRITY_FAILED on bounded orphan attempts";
        return null;
    }

    private static string? Case_RecoveredIntent_BrokerOpenEmptyJournal_ReachesParity()
    {
        var utc = DateTimeOffset.Parse("2099-01-15T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        var res = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, reg,
            "ES", "ES", Array.Empty<string>(), null, null, utc,
            (_, _, _) => { }, allowReconstruction: true, tradingDateForJournal: "2099-01-15");
        if (res.Outcome != JournalIntegrityPhaseOutcome.Repaired || res.PostRepairCheck == null || !res.PostRepairCheck.IsOk)
            return "recovered_empty_journal: expected Repaired + PARITY_OK post-check";
        if (res.RecoveredIntentWrites < 1)
            return "recovered_empty_journal: expected at least one recovered intent disk write";
        return null;
    }

    private static string? Case_RecoveredIntent_TwoContracts()
    {
        var utc = DateTimeOffset.Parse("2099-01-15T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 2 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        var res = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, reg,
            "ES", "ES", Array.Empty<string>(), null, null, utc,
            (_, _, _) => { }, allowReconstruction: true, tradingDateForJournal: "2099-01-15");
        if (!res.PostRepairCheck!.IsOk || res.PostRepairCheck.JournalOpenQty != 2)
            return "recovered_two: journal open qty should match broker (2)";
        return null;
    }

    private static string? Case_RecoveredIntent_SecondPassNoDuplicateRow()
    {
        var utc = DateTimeOffset.Parse("2099-01-15T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        _ = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, reg,
            "ES", "ES", Array.Empty<string>(), null, null, utc,
            (_, _, _) => { }, allowReconstruction: true, tradingDateForJournal: "2099-01-15");
        var res2 = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, reg,
            "ES", "ES", Array.Empty<string>(), null, null, utc,
            (_, _, _) => { }, allowReconstruction: true, tradingDateForJournal: "2099-01-15");
        if (res2.Outcome != JournalIntegrityPhaseOutcome.Ok || !res2.InitialCheck.IsOk)
            return "recovered_idempotent: second ensure should hit PARITY_OK immediately";
        var open = tmp.Journal.GetOpenJournalEntriesByInstrument();
        if (!open.TryGetValue("ES", out var rows)) return "recovered_idempotent: expected ES bucket";
        var recoveredRows = rows.Count(t => string.Equals(t.Stream, ExecutionJournal.RecoveredIntentStream, StringComparison.OrdinalIgnoreCase));
        if (recoveredRows != 1)
            return "recovered_idempotent: expected exactly one recovered stream row";
        return null;
    }

    private static string? Case_RecoveredIntent_MixedNonRecoveredPlusDeficit()
    {
        var utc = DateTimeOffset.Parse("2099-01-15T12:00:00Z");
        var td = "2099-01-15";
        using var tmp = NewTempJournal();
        WriteMinimalNonRecoveredOpenRow(tmp.ProjectRoot, td, "TEST", "seed-mixed-1", "ES", 1, "Long");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 3 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        var reg = new TestRegistryView(false, 0);
        var res = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, reg,
            "ES", "ES", Array.Empty<string>(), null, null, utc,
            (_, _, _) => { }, allowReconstruction: true, tradingDateForJournal: td);
        if (!res.PostRepairCheck!.IsOk || res.PostRepairCheck.JournalOpenQty != 3)
            return "recovered_mixed: expected PARITY_OK with journal 3 matching broker 3";
        return null;
    }

    private static string? Case_RecoveredIntent_StructuralOpenQtyMatchesBroker()
    {
        var utc = DateTimeOffset.Parse("2099-01-15T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "MES", Quantity = -4 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        _ = JournalIntegrityGuarantee.EnsureJournalIntegrity("MES", snap, tmp.Journal, reg,
            "MES", "MES", Array.Empty<string>(), null, null, utc,
            (_, _, _) => { }, allowReconstruction: true, tradingDateForJournal: "2099-01-15");
        var (sum, _) = tmp.Journal.GetOpenJournalStructuralStateForInstrument("MES", "MES");
        if (sum != 4)
            return "recovered_structural: open qty sum should reflect abs exposure (4) for flatten-style callers";
        return null;
    }

    private static string? Case_RecoveredIntent_HedgedBrokerConflict_FailClosed()
    {
        var utc = DateTimeOffset.Parse("2099-01-15T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>
            {
                new() { Instrument = "ES", Quantity = 1 },
                new() { Instrument = "ES", Quantity = -1 }
            },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var logEvts = new List<string>();
        void Log(string e, string _, object? __) => logEvts.Add(e);
        var reg = new TestRegistryView(false, 0);
        var res = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, reg,
            "ES", "ES", Array.Empty<string>(), null, null, utc, Log, allowReconstruction: true, tradingDateForJournal: "2099-01-15");
        if (res.Outcome != JournalIntegrityPhaseOutcome.EscalatedUnrecoverable ||
            !logEvts.Contains("RECONCILIATION_RECOVERED_INTENT_FAILED"))
            return "hedged_conflict: expected fail-closed with RECONCILIATION_RECOVERED_INTENT_FAILED";
        return null;
    }

    private static string? Case_RecoveredSupersededBy_RealStrategyMatchesBroker()
    {
        var utc = DateTimeOffset.Parse("2099-01-16T12:00:00Z");
        var td = "2099-01-16";
        using var tmp = NewTempJournal();
        WriteMinimalRecoveredOpenRow(tmp.ProjectRoot, td, "RECOVERED-ES", "ES", 2, 1, "Long");
        WriteMinimalNonRecoveredOpenRow(tmp.ProjectRoot, td, "TEST", "seed-real-es", "ES", 2, "Long");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 2 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        var reg = new TestRegistryView(false, 0);
        var res = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, reg,
            "ES", "ES", Array.Empty<string>(), null, null, utc,
            (_, _, _) => { }, allowReconstruction: true, tradingDateForJournal: td);
        if (!res.InitialCheck.IsOk || res.Outcome != JournalIntegrityPhaseOutcome.Ok)
            return "superseded_match: expected PARITY_OK after closing redundant recovered row";
        var (sum, _) = tmp.Journal.GetOpenJournalStructuralStateForInstrument("ES", "ES");
        if (sum != 2)
            return "superseded_match: structural open sum should be 2 (strategy only)";
        var jdir = Path.Combine(tmp.ProjectRoot, "data", "execution_journals");
        var recPath = Path.Combine(jdir, $"{td}_{ExecutionJournal.RecoveredIntentStream}_RECOVERED-ES.json");
        if (!File.Exists(recPath)) return "superseded_match: expected recovered journal file";
        var json = File.ReadAllText(recPath);
        if (!json.Contains("\"TradeCompleted\":true", StringComparison.OrdinalIgnoreCase) ||
            !json.Contains(CompletionReasons.RECONCILIATION_RECOVERED_SUPERSEDED_BY_REAL, StringComparison.Ordinal))
            return "superseded_match: recovered row should be completed with RECONCILIATION_RECOVERED_SUPERSEDED_BY_REAL";
        return null;
    }

    private static string? Case_RecoveredSupersededBy_NoClose_OnlyRecovered()
    {
        var utc = DateTimeOffset.Parse("2099-01-16T13:00:00Z");
        var td = "2099-01-17";
        using var tmp = NewTempJournal();
        WriteMinimalRecoveredOpenRow(tmp.ProjectRoot, td, "RECOVERED-ES", "ES", 2, 0, "Long");
        var n = tmp.Journal.CloseRecoveredRowsSupersededByRealExposure("ES", "ES", 2, utc);
        if (n != 0)
            return "superseded_only_recovered: should not close when non-recovered open sum is 0 and broker is 2";
        var json = File.ReadAllText(Path.Combine(tmp.ProjectRoot, "data", "execution_journals",
            $"{td}_{ExecutionJournal.RecoveredIntentStream}_RECOVERED-ES.json"));
        if (json.Contains("\"TradeCompleted\":true", StringComparison.OrdinalIgnoreCase))
            return "superseded_only_recovered: recovered row must remain open";
        return null;
    }

    private static string? Case_ParityPending_A01_HappyPath()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-02-01T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        JournalParityPendingLedger.TryRecordTrustedFill("ES", "k1", 1, "intent-a", utc);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.PARITY_PENDING_ALIGNMENT)
            return "pending_a01: broker 1 journal 0 + trusted pending +1 should be PARITY_PENDING_ALIGNMENT";
        return null;
    }

    private static string? Case_ParityPending_A02_CatchUpParityOk()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-02-02T12:00:00Z");
        var td = "2099-02-02";
        using var tmp = NewTempJournal();
        WriteMinimalNonRecoveredOpenRow(tmp.ProjectRoot, td, "TEST", "seed-pend-a2", "ES", 1, "Long");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        var reg = new TestRegistryView(false, 0);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.PARITY_OK)
            return "pending_a02: broker 1 journal 1 should be PARITY_OK";
        return null;
    }

    private static string? Case_ParityPending_D_WrongSignRejected()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-02-03T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        JournalParityPendingLedger.TryRecordTrustedFill("ES", "k1", -1, "intent-d", utc);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.POSITION_MISMATCH)
            return "pending_d: wrong-sign pending must not classify as PENDING";
        return null;
    }

    private static string? Case_ParityPending_F_StaleAppliedKeyRejected()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-02-04T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        JournalParityPendingLedger.TryRecordTrustedFill("ES", "kStale", 1, "intent-f", utc);
        tmp.Journal.RegisterParityPendingFillPersisted("kStale");
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.POSITION_MISMATCH)
            return "pending_f: I5 stale applied key must not recycle pending explanation";
        return null;
    }

    private static string? Case_ParityPending_G_TwoFillsAggregated()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-02-05T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 2 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        JournalParityPendingLedger.TryRecordTrustedFill("ES", "k1", 1, "i1", utc);
        JournalParityPendingLedger.TryRecordTrustedFill("ES", "k2", 1, "i2", utc);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.PARITY_PENDING_ALIGNMENT)
            return "pending_g: two +1 pending should explain broker +2 journal 0";
        return null;
    }

    private static string? Case_ParityPending_H_ClearLedgerNoExplain()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-02-06T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        var r = JournalParityChecker.CheckJournalParity("ES", snap, tmp.Journal, reg, "ES", utc);
        if (r.Status != JournalParityStatus.POSITION_MISMATCH)
            return "pending_h: empty ledger cannot pending-align";
        return null;
    }

    private static string? Case_ParityPending_EnsureIntegrityNoRepair()
    {
        JournalParityPendingLedger.Clear();
        var utc = DateTimeOffset.Parse("2099-02-07T12:00:00Z");
        var snap = new AccountSnapshot
        {
            Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 1 } },
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
        using var tmp = NewTempJournal();
        var reg = new TestRegistryView(false, 0);
        JournalParityPendingLedger.TryRecordTrustedFill("ES", "kE", 1, "intent-e", utc);
        var res = JournalIntegrityGuarantee.EnsureJournalIntegrity("ES", snap, tmp.Journal, reg,
            "ES", "ES", Array.Empty<string>(), null, null, utc,
            (_, _, _) => { }, allowReconstruction: true, tradingDateForJournal: "2099-02-07");
        if (res.Outcome != JournalIntegrityPhaseOutcome.Ok || res.InitialCheck.Status != JournalParityStatus.PARITY_PENDING_ALIGNMENT)
            return "pending_ensure: PARITY_PENDING_ALIGNMENT must short-circuit integrity repair (Outcome Ok)";
        return null;
    }

    private static string? Case_RecoveredSupersededBy_NoClose_PartialNonRecovered()
    {
        var utc = DateTimeOffset.Parse("2099-01-16T14:00:00Z");
        var td = "2099-01-18";
        using var tmp = NewTempJournal();
        WriteMinimalRecoveredOpenRow(tmp.ProjectRoot, td, "RECOVERED-ES", "ES", 2, 0, "Long");
        WriteMinimalNonRecoveredOpenRow(tmp.ProjectRoot, td, "TEST", "seed-partial", "ES", 1, "Long");
        var n = tmp.Journal.CloseRecoveredRowsSupersededByRealExposure("ES", "ES", 2, utc);
        if (n != 0)
            return "superseded_partial: non-recovered 1 != broker 2, no close";
        return null;
    }

    private static void WriteMinimalRecoveredOpenRow(string projectRoot, string tradingDate, string intentId, string instrument,
        int entryFilledQty, int exitFilledQty, string direction)
    {
        var jdir = Path.Combine(projectRoot, "data", "execution_journals");
        Directory.CreateDirectory(jdir);
        var stream = ExecutionJournal.RecoveredIntentStream;
        var path = Path.Combine(jdir, $"{tradingDate}_{stream}_{intentId}.json");
        var json =
            $"{{\"IntentId\":\"{intentId}\",\"TradingDate\":\"{tradingDate}\",\"Stream\":\"{stream}\",\"Instrument\":\"{instrument}\"," +
            "\"IntentType\":\"RECOVERED\",\"IsRecovered\":true," +
            "\"EntryFilled\":true,\"EntryFilledQuantityTotal\":" + entryFilledQty +
            ",\"ExitFilledQuantityTotal\":" + exitFilledQty +
            ",\"TradeCompleted\":false,\"Direction\":\"" + direction +
            "\",\"EntryFilledAtUtc\":\"2099-01-16T12:00:00Z\"}";
        File.WriteAllText(path, json);
    }

    private static void WriteMinimalNonRecoveredOpenRow(string projectRoot, string tradingDate, string stream, string intentId,
        string instrument, int entryFilledQty, string direction)
    {
        var jdir = Path.Combine(projectRoot, "data", "execution_journals");
        Directory.CreateDirectory(jdir);
        var path = Path.Combine(jdir, $"{tradingDate}_{stream}_{intentId}.json");
        var json =
            $"{{\"IntentId\":\"{intentId}\",\"TradingDate\":\"{tradingDate}\",\"Stream\":\"{stream}\",\"Instrument\":\"{instrument}\"," +
            "\"EntryFilled\":true,\"EntryFilledQuantityTotal\":" + entryFilledQty +
            ",\"ExitFilledQuantityTotal\":0,\"TradeCompleted\":false,\"Direction\":\"" + direction +
            "\",\"EntryFilledAtUtc\":\"2099-01-15T12:00:00Z\"}";
        File.WriteAllText(path, json);
    }

    private sealed class TestRegistryView : IJournalParityRegistryView
    {
        public TestRegistryView(bool useIea, int ieaOwned) =>
            (UseInstrumentExecutionAuthority, IeaOwnedPlusAdoptedWorking) = (useIea, ieaOwned);

        public bool UseInstrumentExecutionAuthority { get; }
        public int IeaOwnedPlusAdoptedWorking { get; }
    }

    private sealed class TempJournal : IDisposable
    {
        public TempJournal(ExecutionJournal j, string projectRoot)
        {
            Journal = j;
            ProjectRoot = projectRoot;
            _dir = projectRoot;
        }

        public ExecutionJournal Journal { get; }
        /// <summary>Temp project root (contains data/execution_journals).</summary>
        public string ProjectRoot { get; }
        private readonly string _dir;

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(_dir))
                    Directory.Delete(_dir, true);
            }
            catch
            {
                // test cleanup best-effort
            }
        }
    }

    private static TempJournal NewTempJournal()
    {
        var dir = Path.Combine(Path.GetTempPath(), "qtsw2_jig_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = new RobotLogger(dir, Path.Combine(dir, "logs", "robot"));
        return new TempJournal(new ExecutionJournal(dir, log), dir);
    }
}
