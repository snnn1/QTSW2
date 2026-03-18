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

        // 5. Bootstrap LIVE_ORDERS_PRESENT_NO_POSITION + SlotJournalShowsEntryStopsExpected -> ADOPT
        var (p4, e4) = TestBootstrapAdoptEntryStops();
        if (!p4) return (false, e4);

        // 6. Mixed restart: entry filled + protective working — both journal-backed, adoption candidate includes intent
        var (p5, e5) = TestMixedRestartCandidateCoverage();
        if (!p5) return (false, e5);

        return (true, null);
    }

    private static (bool Pass, string? Error) TestAdoptionCandidatesIncludeUnfilled()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"AdoptionTest_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
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
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
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
            brokerQty: 0,
            localQty: 0,
            brokerWorkingOrderCount: 2,
            localWorkingOrderCount: 0);
        if (mismatchType != MismatchType.ORDER_REGISTRY_MISSING)
            return (false, $"No adoptable evidence: expected ORDER_REGISTRY_MISSING, got {mismatchType}");
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
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
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
}
