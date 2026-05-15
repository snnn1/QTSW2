// Unit tests for adoption recovery fix (unfilled entry stops, reconciliation recovery).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test ADOPTION_RECOVERY
//
// Verifies: GetAdoptionCandidateIntentIdsForInstrument includes EntrySubmitted (unfilled),
// adoption candidate discovery separate from BE monitoring, fail-closed when no adoptable evidence.

using System;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class AdoptionRecoveryTests
{
    public static (bool Pass, string? Error) RunAdoptionRecoveryTests()
    {
        // 1. GetAdoptionCandidateIntentIdsForInstrument returns EntrySubmitted (unfilled) entries
        var (p1, e1) = TestAdoptionCandidatesIncludeUnfilled();
        if (!p1) return (false, e1);

        // 2. GetAdoptionCandidateIntentIdsForInstrument excludes TradeCompleted (journal-level)
        var (p2a, e2a) = TestAdoptionCandidatesExcludeTradeCompleted();
        if (!p2a) return (false, e2a);

        // 3. Bootstrap classifier/decider: no slot journal → FLATTEN
        var (p2, e2) = TestAdoptionCandidatesExcludeCompleted();
        if (!p2) return (false, e2);

        // 4. Broker working with no adoptable evidence: MismatchClassification yields ORDER_REGISTRY_MISSING
        var (p3, e3) = TestNoAdoptableEvidenceFailClosed();
        if (!p3) return (false, e3);

        var (p3b, e3b) = TestLocalWorkingAheadClassifiesAsConvergence();
        if (!p3b) return (false, e3b);

        // 5. Bootstrap LIVE_ORDERS_PRESENT_NO_POSITION + SlotJournalShowsEntryStopsExpected -> ADOPT
        var (p4, e4) = TestBootstrapAdoptEntryStops();
        if (!p4) return (false, e4);

        // 6. Mixed restart: entry filled + protective working — both journal-backed, adoption candidate includes intent
        var (p5, e5) = TestMixedRestartCandidateCoverage();
        if (!p5) return (false, e5);

        var (p6, e6) = TestAdoptedEntryRestoresMissingQuantityPolicy();
        if (!p6) return (false, e6);

        var (p7, e7) = TestAdoptedEntryPreservesExistingQuantityPolicy();
        if (!p7) return (false, e7);

        return (true, null);
    }

    private static (bool Pass, string? Error) TestAdoptionCandidatesIncludeUnfilled()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"AdoptionTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
            Directory.CreateDirectory(journalDir);

            var intentId = "a1b2c3d4e5f67890";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-17",
                Stream = "NQ2",
                Instrument = "MNQ",
                EntrySubmitted = true,
                EntryFilled = false,
                TradeCompleted = false
            };
            var path = Path.Combine(journalDir, $"2026-03-17_NQ2_{intentId}.json");
            File.WriteAllText(path, JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var ids = journal.GetAdoptionCandidateIntentIdsForInstrument("MNQ", null);
            if (ids.Count == 0)
                return (false, "Unfilled entry stop: expected 1 adoption candidate, got 0");
            if (!ids.Contains(intentId))
                return (false, $"Unfilled: expected intentId {intentId} in candidates, got [{string.Join(",", ids)}]");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestAdoptionCandidatesExcludeTradeCompleted()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"AdoptionTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
            Directory.CreateDirectory(journalDir);

            var intentId = "b2c3d4e5f6789012";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-17",
                Stream = "NQ2",
                Instrument = "MNQ",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = true
            };
            var path = Path.Combine(journalDir, $"2026-03-17_NQ2_{intentId}.json");
            File.WriteAllText(path, JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var ids = journal.GetAdoptionCandidateIntentIdsForInstrument("MNQ", null);
            if (ids.Contains(intentId))
                return (false, $"TradeCompleted: intentId {intentId} should be excluded from adoption candidates, got [{string.Join(",", ids)}]");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestAdoptionCandidatesExcludeCompleted()
    {
        var snap = new BootstrapSnapshot
        {
            Instrument = "MNQ",
            BrokerPositionQty = 0,
            BrokerWorkingOrderCount = 2,
            JournalQty = 0,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 },
            SlotJournalShowsEntryStopsExpected = false
        };
        var c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.LIVE_ORDERS_PRESENT_NO_POSITION)
            return (false, $"No slot journal: expected LIVE_ORDERS_PRESENT_NO_POSITION, got {c}");
        var d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.FLATTEN_THEN_RECONSTRUCT)
            return (false, $"No slot journal: expected FLATTEN (fail-closed path), got {d}");
        return (true, null);
    }

    private static (bool Pass, string? Error) TestNoAdoptableEvidenceFailClosed()
    {
        var mismatchType = MismatchClassification.Classify(
            brokerQtyAbs: 0,
            grossJournalQty: 0,
            netBrokerQty: 0,
            netJournalQty: 0,
            opposingMultiIntentOpen: false,
            brokerWorkingOrderCount: 2,
            localWorkingOrderCount: 0);
        if (mismatchType != MismatchType.ORDER_REGISTRY_MISSING)
            return (false, $"No adoptable evidence: expected ORDER_REGISTRY_MISSING, got {mismatchType}");
        return (true, null);
    }

    private static (bool Pass, string? Error) TestLocalWorkingAheadClassifiesAsConvergence()
    {
        var mismatchType = MismatchClassification.Classify(
            brokerQtyAbs: 2,
            grossJournalQty: 2,
            netBrokerQty: -2,
            netJournalQty: -2,
            opposingMultiIntentOpen: false,
            brokerWorkingOrderCount: 2,
            localWorkingOrderCount: 4);
        if (mismatchType != MismatchType.WORKING_ORDER_COUNT_CONVERGENCE)
            return (false, $"Local working ahead: expected WORKING_ORDER_COUNT_CONVERGENCE, got {mismatchType}");
        return (true, null);
    }

    private static (bool Pass, string? Error) TestBootstrapAdoptEntryStops()
    {
        var snap = new BootstrapSnapshot
        {
            Instrument = "MNQ",
            BrokerPositionQty = 0,
            BrokerWorkingOrderCount = 2,
            JournalQty = 0,
            UnownedLiveOrderCount = 0,
            RegistrySnapshot = new RecoveryRegistrySnapshot { UnownedLiveCount = 0 },
            SlotJournalShowsEntryStopsExpected = true
        };
        var c = BootstrapClassifier.Classify(snap);
        if (c != BootstrapClassification.LIVE_ORDERS_PRESENT_NO_POSITION)
            return (false, $"Entry stops + slot journal: expected LIVE_ORDERS_PRESENT_NO_POSITION, got {c}");
        var d = BootstrapDecider.Decide(c, snap);
        if (d != BootstrapDecision.ADOPT)
            return (false, $"Entry stops + slot journal: expected ADOPT, got {d}");
        return (true, null);
    }

    /// <summary>Mixed restart: one entry stop + one protective at restart. Both journal-backed (EntrySubmitted, !TradeCompleted).
    /// GetAdoptionCandidateIntentIdsForInstrument returns the intent so both can be adopted.</summary>
    private static (bool Pass, string? Error) TestMixedRestartCandidateCoverage()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"AdoptionTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
            Directory.CreateDirectory(journalDir);

            var intentId = "c3d4e5f6789012345";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-17",
                Stream = "NQ2",
                Instrument = "MNQ",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = false
            };
            var path = Path.Combine(journalDir, $"2026-03-17_NQ2_{intentId}.json");
            File.WriteAllText(path, JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var ids = journal.GetAdoptionCandidateIntentIdsForInstrument("MNQ", null);
            if (ids.Count == 0)
                return (false, "Mixed restart: expected 1 adoption candidate (entry+protective share intent), got 0");
            if (!ids.Contains(intentId))
                return (false, $"Mixed restart: expected intentId {intentId} in candidates, got [{string.Join(",", ids)}]");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestAdoptedEntryRestoresMissingQuantityPolicy()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"AdoptionPolicyTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var iea = new InstrumentExecutionAuthority("SIM101", "MYM", log: log);
            var now = DateTimeOffset.Parse("2026-05-12T15:00:00Z");
            var intentId = "adoptpolicy000001";
            var orderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = "MYM",
                OrderId = "broker-entry-1",
                OrderType = "ENTRY",
                Quantity = 2,
                State = "WORKING",
                IsEntryOrder = true
            };

            iea.RegisterAdoptedOrder(orderInfo.OrderId, intentId, "MYM", OrderRole.ENTRY, "RESTART_ADOPTION_ENTRY", orderInfo, now);

            if (orderInfo.ExpectedQuantity != 2)
                return (false, $"Adopted entry: expected OrderInfo.ExpectedQuantity=2, got {orderInfo.ExpectedQuantity}");
            if (orderInfo.MaxQuantity != 2)
                return (false, $"Adopted entry: expected OrderInfo.MaxQuantity=2, got {orderInfo.MaxQuantity}");
            if (!string.Equals(orderInfo.PolicySource, "RESTART_ADOPTION_ENTRY", StringComparison.Ordinal))
                return (false, $"Adopted entry: expected PolicySource=RESTART_ADOPTION_ENTRY, got {orderInfo.PolicySource}");
            if (!iea.TryGetIntentPolicy(intentId, out var policy) || policy == null)
                return (false, "Adopted entry: expected IEA intent policy to be restored");
            if (policy.ExpectedQuantity != 2 || policy.MaxQuantity != 2)
                return (false, $"Adopted entry: expected restored policy 2/2, got {policy.ExpectedQuantity}/{policy.MaxQuantity}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestAdoptedEntryPreservesExistingQuantityPolicy()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"AdoptionPolicyTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var iea = new InstrumentExecutionAuthority("SIM101", "MYM", log: log);
            var now = DateTimeOffset.Parse("2026-05-12T15:00:00Z");
            var intentId = "adoptpolicy000002";
            iea.RegisterIntentPolicy(intentId, expectedQty: 3, maxQty: 4, canonical: "YM", execution: "MYM", policySource: "EXECUTION_POLICY_FILE");
            var orderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = "MYM",
                OrderId = "broker-entry-2",
                OrderType = "ENTRY",
                Quantity = 2,
                State = "WORKING",
                IsEntryOrder = true
            };

            iea.RegisterAdoptedOrder(orderInfo.OrderId, intentId, "MYM", OrderRole.ENTRY, "RESTART_ADOPTION_ENTRY", orderInfo, now);

            if (orderInfo.ExpectedQuantity != 3 || orderInfo.MaxQuantity != 4)
                return (false, $"Existing policy: expected OrderInfo policy 3/4, got {orderInfo.ExpectedQuantity}/{orderInfo.MaxQuantity}");
            if (!string.Equals(orderInfo.PolicySource, "EXECUTION_POLICY_FILE", StringComparison.Ordinal))
                return (false, $"Existing policy: expected PolicySource=EXECUTION_POLICY_FILE, got {orderInfo.PolicySource}");
            if (!string.Equals(orderInfo.CanonicalInstrument, "YM", StringComparison.Ordinal))
                return (false, $"Existing policy: expected CanonicalInstrument=YM, got {orderInfo.CanonicalInstrument}");
            if (!iea.TryGetIntentPolicy(intentId, out var policy) || policy == null)
                return (false, "Existing policy: expected IEA intent policy");
            if (policy.ExpectedQuantity != 3 || policy.MaxQuantity != 4)
                return (false, $"Existing policy: expected stored policy 3/4, got {policy.ExpectedQuantity}/{policy.MaxQuantity}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, true); } catch { }
        }
    }
}
