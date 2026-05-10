// Focused probe: shared execution instrument, long then opposite short, structural_multi_intent_policy = auto_offset.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test STRUCTURAL_AUTO_OFFSET
//
// Verifies deterministic classification, journal net/gross/opposing flags, policy runtime (gate recovery hook),
// and documents expected broker snapshot shapes (abs sum vs signed net).

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class StructuralMultiIntentAutoOffsetTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var e = VerifyMismatchClassificationMatchesAuditScenario();
        if (e != null) return (false, e);

        e = VerifyBrokerNetFlatGrossOpenClassifiesAsHedged();
        if (e != null) return (false, e);

        e = VerifyExplainedHedgedNetFlatGrossOpenPolicy();
        if (e != null) return (false, e);

        e = VerifyJournalGrossNetOppositeFromDisk();
        if (e != null) return (false, e);

        e = VerifyExecutionPolicyLoadsAutoOffset();
        if (e != null) return (false, e);

        e = VerifyExecutionPolicyLegacySingleOwnerTokenFallsBackToAllow();
        if (e != null) return (false, e);

        e = VerifyOwnershipLedgerAllowsSameSideMultiStreamEntries();
        if (e != null) return (false, e);

        e = VerifyPolicyRuntimeInvokesGateRecoveryOnlyForAutoOffset();
        if (e != null) return (false, e);

        e = VerifyBrokerSnapshotPositionAggregation();
        if (e != null) return (false, e);

        e = VerifyReconciliationClassifierSumsDuplicateBrokerRows();
        if (e != null) return (false, e);

        return (true, null);
    }

    /// <summary>MNG-style: broker reports abs sum 2 while journal gross 4, nets both 0 → STRUCTURAL_MULTI_INTENT.</summary>
    private static string? VerifyMismatchClassificationMatchesAuditScenario()
    {
        var mt = MismatchClassification.Classify(
            brokerQtyAbs: 2,
            grossJournalQty: 4,
            netBrokerQty: 0,
            netJournalQty: 0,
            opposingMultiIntentOpen: true,
            brokerWorkingOrderCount: 0,
            localWorkingOrderCount: 0);
        if (mt != MismatchType.STRUCTURAL_MULTI_INTENT)
            return $"Audit scenario: expected STRUCTURAL_MULTI_INTENT, got {mt}";
        return null;
    }

    /// <summary>Broker-net flat with valid opposing gross streams is hedged gross-open, not stale JOURNAL_AHEAD.</summary>
    private static string? VerifyBrokerNetFlatGrossOpenClassifiesAsHedged()
    {
        var mt = MismatchClassification.Classify(
            brokerQtyAbs: 0,
            grossJournalQty: 4,
            netBrokerQty: 0,
            netJournalQty: 0,
            opposingMultiIntentOpen: true,
            brokerWorkingOrderCount: 4,
            localWorkingOrderCount: 4);
        if (mt != MismatchType.HEDGED_NET_FLAT_GROSS_OPEN)
            return $"Broker-net-flat gross-open: expected HEDGED_NET_FLAT_GROSS_OPEN, got {mt}";
        return null;
    }

    private static string? VerifyExplainedHedgedNetFlatGrossOpenPolicy()
    {
        if (!MismatchClassification.IsExplainedHedgedNetFlatGrossOpen(
                brokerQtyAbs: 0,
                grossJournalQty: 4,
                netBrokerQty: 0,
                netJournalQty: 0,
                opposingMultiIntentOpen: true,
                brokerWorkingOrderCount: 4,
                localWorkingOrderCount: 4))
            return "Explained hedged net-flat gross-open should be recognized when working orders match";

        if (MismatchClassification.IsExplainedHedgedNetFlatGrossOpen(0, 4, 0, 0, true, 0, 0))
            return "Unprotected hedged net-flat gross-open must not be treated as explained";

        if (MismatchClassification.IsExplainedHedgedNetFlatGrossOpen(0, 4, 0, 0, true, 4, 2))
            return "Working-order mismatch must not be treated as explained hedged gross exposure";

        if (MismatchClassification.IsExplainedHedgedNetFlatGrossOpen(0, 4, 0, 0, false, 4, 4))
            return "Single-direction broker-net-flat gross-open must not be treated as explained hedged exposure";

        return null;
    }

    private static string? VerifyJournalGrossNetOppositeFromDisk()
    {
        var root = Path.Combine(Path.GetTempPath(), "QTSW2_StructuralAutoOffset_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(root);
            Directory.CreateDirectory(journalDir);
            var td = "2026-04-07";
            var longId = "9073e0ab46e8f498";
            var shortId = "5fb77505f5b29740";
            WriteOpenJournal(journalDir, td, "NG1", longId, "MNG", "Long", entryQty: 2);
            WriteOpenJournal(journalDir, td, "NG2", shortId, "MNG", "Short", entryQty: 2);

            var journal = new ExecutionJournal(root, new RobotLogger(root));
            var map = journal.GetOpenJournalEntriesByInstrument();
            var gross = journal.GetOpenJournalQuantitySumForInstrumentFromMap(map, "MNG", "MNG");
            var net = journal.GetOpenJournalSignedNetForInstrumentFromMap(map, "MNG", "MNG");
            var opp = journal.HasOpposingDirectionOpenIntentsFromMap(map, "MNG", "MNG");
            if (gross != 4) return $"Journal gross expected 4 (2+2 long/short open), got {gross}";
            if (net != 0) return $"Journal net expected 0 (offsetting), got {net}";
            if (!opp) return "Expected opposing open intents on MNG";
            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static void WriteOpenJournal(string journalRoot, string tradingDate, string stream, string intentId,
        string instrument, string direction, int entryQty)
    {
        var path = Path.Combine(journalRoot, $"{tradingDate}_{stream}_{intentId}.json");
        var entry = new ExecutionJournalEntry
        {
            IntentId = intentId,
            TradingDate = tradingDate,
            Stream = stream,
            Instrument = instrument,
            Direction = direction,
            EntryFilled = true,
            EntryFilledQuantityTotal = entryQty,
            ExitFilledQuantityTotal = 0,
            TradeCompleted = false
        };
        File.WriteAllText(path, JsonUtil.Serialize(entry));
    }

    private static string? VerifyExecutionPolicyLoadsAutoOffset()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        var src = Path.Combine(root, "configs", "execution_policy.json");
        if (!File.Exists(src)) return $"Missing {src}";
        var tmp = Path.Combine(Path.GetTempPath(), "QTSW2_exec_pol_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var txt = File.ReadAllText(src);
            var patched = txt.Replace("\"use_instrument_execution_authority\"",
                "\"structural_multi_intent_policy\": \"auto_offset\",\n  \"use_instrument_execution_authority\"",
                StringComparison.Ordinal);
            File.WriteAllText(tmp, patched);
            var pol = ExecutionPolicy.LoadFromFile(tmp);
            if (pol.StructuralMultiIntentPolicy != StructuralMultiIntentPolicy.AutoOffsetRequest)
                return $"ExecutionPolicy: expected AutoOffsetRequest, got {pol.StructuralMultiIntentPolicy}";
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static string? VerifyExecutionPolicyLegacySingleOwnerTokenFallsBackToAllow()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        var src = Path.Combine(root, "configs", "execution_policy.json");
        if (!File.Exists(src)) return $"Missing {src}";
        var tmp = Path.Combine(Path.GetTempPath(), "QTSW2_exec_pol_" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var txt = File.ReadAllText(src);
            var patched = txt.Replace("\"use_instrument_execution_authority\"",
                "\"structural_multi_intent_policy\": \"single_owner\",\n  \"use_instrument_execution_authority\"",
                StringComparison.Ordinal);
            File.WriteAllText(tmp, patched);
            var pol = ExecutionPolicy.LoadFromFile(tmp);
            if (pol.StructuralMultiIntentPolicy != StructuralMultiIntentPolicy.Allow)
                return $"ExecutionPolicy: legacy single_owner token should fall back to Allow, got {pol.StructuralMultiIntentPolicy}";
            return null;
        }
        finally
        {
            try { File.Delete(tmp); } catch { /* ignore */ }
        }
    }

    private static string? VerifyOwnershipLedgerAllowsSameSideMultiStreamEntries()
    {
        var root = Path.Combine(Path.GetTempPath(), "QTSW2_owner_multi_stream_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var emitted = new List<OwnershipEvent>();
            var ledger = new InstrumentOwnershipLedger(new RobotLogger(root), onClassAEvent: e => emitted.Add(e));
            var utc = DateTimeOffset.Parse("2026-04-13T14:30:00Z");

            var r1 = ledger.RecordMappedEntryFill("Playback101", "MCL", "intent-a", "MCL1", SlotDirection.Short, 2, utc, 1);
            var r2 = ledger.RecordMappedEntryFill("Playback101", "MCL", "intent-b", "MCL2", SlotDirection.Short, 2, utc.AddMilliseconds(1), 2);

            if (!r1.Success || !r2.Success)
                return $"Ledger multi-stream: expected both writes to succeed, got r1={r1.Success}, r2={r2.Success}";
            if (emitted.Any(e => e.Kind == OwnershipEventKind.OwnershipConflictRejected))
                return "Ledger multi-stream: unexpected OwnershipConflictRejected for same-side independent streams";

            var snap = ledger.GetOwnershipSnapshot("Playback101", "MCL");
            if (snap.ActiveSlotCount != 2)
                return $"Ledger multi-stream: expected 2 active slots, got {snap.ActiveSlotCount}";
            if (snap.LedgerSignedNetQty != -4)
                return $"Ledger multi-stream: expected signed net -4, got {snap.LedgerSignedNetQty}";
            if (!snap.MultiIntentAllowed)
                return "Ledger multi-stream: snapshot should report MultiIntentAllowed=true";
            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }

    private static string? VerifyPolicyRuntimeInvokesGateRecoveryOnlyForAutoOffset()
    {
        var utc = DateTimeOffset.Parse("2026-04-07T15:16:00Z");
        var inst = "MNG";
        int standDown = 0, gate = 0;
        var k1 = StructuralMultiIntentPolicyRuntime.Invoke(StructuralMultiIntentPolicy.Allow, inst, utc,
            (_, _, _) => standDown++, (_, _) => gate++);
        if (k1 != StructuralMultiIntentPolicyActionKind.AllowObservation || standDown != 0 || gate != 0)
            return "Allow: expected no stand-down and no gate recovery";

        standDown = gate = 0;
        var k2 = StructuralMultiIntentPolicyRuntime.Invoke(StructuralMultiIntentPolicy.BlockNewEntries, inst, utc,
            (_, _, _) => standDown++, (_, _) => gate++);
        if (k2 != StructuralMultiIntentPolicyActionKind.BlockNewEntries || standDown != 1 || gate != 0)
            return "Block: expected stand-down only";

        standDown = gate = 0;
        var k3 = StructuralMultiIntentPolicyRuntime.Invoke(StructuralMultiIntentPolicy.AutoOffsetRequest, inst, utc,
            (_, _, _) => standDown++, (_, _) => gate++);
        if (k3 != StructuralMultiIntentPolicyActionKind.GateRecoveryRequested || standDown != 0 || gate != 1)
            return "AutoOffset: expected gate recovery only";
        return null;
    }

    /// <summary>
    /// Document broker snapshot aggregation (same rules as RobotEngine AssembleMismatchObservations):
    /// multiple rows sum abs for BrokerQty; signed quantities sum for NetBrokerQty.
    /// </summary>
    private static string? VerifyBrokerSnapshotPositionAggregation()
    {
        var rows = new List<PositionSnapshot>
        {
            new() { Instrument = "MNG", Quantity = 2 },
            new() { Instrument = "MNG", Quantity = -2 }
        };
        var absSum = 0;
        var net = 0;
        foreach (var p in rows)
        {
            absSum += Math.Abs(p.Quantity);
            net += p.Quantity;
        }

        if (absSum != 4 || net != 0)
            return $"Two-row offset broker model: expected absSum=4 net=0, got abs={absSum} net={net}";

        // Single-row net (e.g. venue nets to one line) — abs tracks magnitude only
        var oneRow = new List<PositionSnapshot> { new() { Instrument = "MNG", Quantity = 2 } };
        absSum = oneRow.Sum(p => Math.Abs(p.Quantity));
        net = oneRow.Sum(p => p.Quantity);
        if (absSum != 2 || net != 2)
            return $"Single-row +2: expected abs=2 net=2, got abs={absSum} net={net}";

        return null;
    }

    private static string? VerifyReconciliationClassifierSumsDuplicateBrokerRows()
    {
        var root = Path.Combine(Path.GetTempPath(), "QTSW2_recon_classifier_rows_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(root);
            var log = new RobotLogger(root);
            var ledger = new InstrumentOwnershipLedger(log);
            var account = "Playback101";
            var utc = DateTimeOffset.Parse("2026-04-27T22:53:57Z");
            ledger.RecordMappedEntryFill(account, "MNG", "intent-long", "NG1", SlotDirection.Long, 2, utc, 1);
            ledger.RecordMappedEntryFill(account, "MNG", "intent-short", "NG2", SlotDirection.Short, 2, utc.AddMilliseconds(1), 2);

            var classifier = new ReconciliationClassifier(ledger, log);
            var broker = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>
                {
                    new() { Instrument = "MNG", Quantity = 2 },
                    new() { Instrument = "MNG", Quantity = -2 }
                },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };

            var verdict = classifier.Classify(broker, account, new[] { "MNG" }, utc).Single();
            if (verdict.BrokerQty != 0)
                return $"ReconciliationClassifier: expected summed broker net 0, got {verdict.BrokerQty}";
            if (verdict.BrokerSignedQty != 0)
                return $"ReconciliationClassifier: expected signed broker net 0, got {verdict.BrokerSignedQty}";
            if (verdict.LedgerQty != 0)
                return $"ReconciliationClassifier: expected ledger signed net 0, got {verdict.LedgerQty}";
            if (verdict.JournalOpenQty != 4)
                return $"ReconciliationClassifier: expected ledger gross open 4, got {verdict.JournalOpenQty}";
            if (verdict.ActiveSlotCount != 2)
                return $"ReconciliationClassifier: expected 2 active slots, got {verdict.ActiveSlotCount}";

            return null;
        }
        finally
        {
            try { Directory.Delete(root, true); } catch { /* ignore */ }
        }
    }
}
