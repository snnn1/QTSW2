// Unit tests for UnmatchedPositionPolicyEvaluator.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test UNMATCHED_POSITION_POLICY
//
// Verifies: Must OWNERSHIP_PROVEN (one candidate, exact match, continuity evidence), Must FLATTEN (no candidate,
// multiple candidates, qty mismatch, instrument mismatch, stale session, no continuity evidence).

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class UnmatchedPositionPolicyTests
{
    public static (bool Pass, string? Error) RunUnmatchedPositionPolicyTests()
    {
        var (p1, e1) = TestMustFlattenNoCandidate();
        if (!p1) return (false, e1);

        var (p2, e2) = TestMustFlattenMultipleCandidates();
        if (!p2) return (false, e2);

        var (p3, e3) = TestMustFlattenQtyMismatch();
        if (!p3) return (false, e3);

        var (p4, e4) = TestMustFlattenTradingDateMismatch();
        if (!p4) return (false, e4);

        var (p5, e5) = TestMustFlattenNoContinuityEvidence();
        if (!p5) return (false, e5);

        var (p6, e6) = TestMustAdoptOneCandidateExactMatchWithContinuity();
        if (!p6) return (false, e6);

        var (p7, e7) = TestMustFlattenInstrumentMismatch();
        if (!p7) return (false, e7);

        var (p8, e8) = TestMustFlattenPartialAmbiguousBrokerExposure();
        if (!p8) return (false, e8);

        var (p9, e9) = TestRepeatedEvaluationDoesNotOscillate();
        if (!p9) return (false, e9);

        var (p10, e10) = TestNoDuplicateRegistryJournalStateOnRepeatedAdoptPath();
        if (!p10) return (false, e10);

        return (true, null);
    }

    private static (bool Pass, string? Error) TestMustFlattenNoCandidate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);
            // No journal files - no candidates
            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);

            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 2 };
            var snapshot = new AccountSnapshot { Positions = new List<PositionSnapshot> { position }, WorkingOrders = new List<WorkingOrderSnapshot>() };
            var result = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);

            if (result.Decision != UnmatchedPositionPolicyDecision.FLATTEN)
                return (false, $"Expected FLATTEN (no candidate), got {result.Decision}");
            if (result.Reason != "NO_CANDIDATE")
                return (false, $"Expected reason NO_CANDIDATE, got {result.Reason}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    private static (bool Pass, string? Error) TestMustFlattenMultipleCandidates()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var intent1 = "a1b2c3d4e5f67890";
            var intent2 = "b2c3d4e5f6789012";
            foreach (var (intentId, stream) in new[] { (intent1, "YM1"), (intent2, "YM2") })
            {
                var entry = new ExecutionJournalEntry
                {
                    IntentId = intentId,
                    TradingDate = "2026-03-18",
                    Stream = stream,
                    Instrument = "MYM",
                    EntrySubmitted = true,
                    EntryFilled = true,
                    TradeCompleted = false,
                    EntryFilledQuantityTotal = 1,
                    ExitFilledQuantityTotal = 0
                };
                File.WriteAllText(Path.Combine(journalDir, $"2026-03-18_{stream}_{intentId}.json"), JsonUtil.Serialize(entry));
            }

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 1 };
            var snapshot = new AccountSnapshot { Positions = new List<PositionSnapshot> { position }, WorkingOrders = new List<WorkingOrderSnapshot>() };
            var result = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);

            if (result.Decision != UnmatchedPositionPolicyDecision.FLATTEN)
                return (false, $"Expected FLATTEN (multiple candidates), got {result.Decision}");
            if (result.Reason != "MULTIPLE_CANDIDATES")
                return (false, $"Expected MULTIPLE_CANDIDATES, got {result.Reason}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    private static (bool Pass, string? Error) TestMustFlattenQtyMismatch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var intentId = "c3d4e5f678901234";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-18",
                Stream = "YM1",
                Instrument = "MYM",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = false,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 0
            };
            File.WriteAllText(Path.Combine(journalDir, $"2026-03-18_YM1_{intentId}.json"), JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 1 }; // broker has 1, journal has 2
            var snapshot = new AccountSnapshot { Positions = new List<PositionSnapshot> { position }, WorkingOrders = new List<WorkingOrderSnapshot>() };
            var result = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);

            if (result.Decision != UnmatchedPositionPolicyDecision.FLATTEN)
                return (false, $"Expected FLATTEN (qty mismatch), got {result.Decision}");
            if (!result.Reason.StartsWith("QTY_MISMATCH"))
                return (false, $"Expected QTY_MISMATCH, got {result.Reason}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    private static (bool Pass, string? Error) TestMustFlattenTradingDateMismatch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var intentId = "d4e5f67890123456";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-17", // stale
                Stream = "YM1",
                Instrument = "MYM",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = false,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 0
            };
            File.WriteAllText(Path.Combine(journalDir, $"2026-03-17_YM1_{intentId}.json"), JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 1 };
            var snapshot = new AccountSnapshot { Positions = new List<PositionSnapshot> { position }, WorkingOrders = new List<WorkingOrderSnapshot>() };
            var result = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);

            if (result.Decision != UnmatchedPositionPolicyDecision.FLATTEN)
                return (false, $"Expected FLATTEN (trading date mismatch), got {result.Decision}");
            if (!result.Reason.StartsWith("TRADING_DATE_MISMATCH"))
                return (false, $"Expected TRADING_DATE_MISMATCH, got {result.Reason}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    private static (bool Pass, string? Error) TestMustFlattenNoContinuityEvidence()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var intentId = "e5f6789012345678";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-18",
                Stream = "YM1",
                Instrument = "MYM",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = false,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 0
            };
            File.WriteAllText(Path.Combine(journalDir, $"2026-03-18_YM1_{intentId}.json"), JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 1 };
            // No working orders with QTSW2 tag for this intent - continuity A/B fail
            // C: EntryFilled && journalOpenQty==brokerQty && !TradeCompleted - that should pass!
            // So we need a case where C fails. C requires EntryFilled. We have that. So C passes.
            // To fail continuity: we need EntryFilled=false? But then we wouldn't have journalOpenQty. Actually
            // journalOpenQty = EntryFilledQuantityTotal - ExitFilledQuantityTotal. So even without EntryFilled
            // we could have qty. But TryGetAdoptionCandidateEntry requires EntrySubmitted && !TradeCompleted.
            // So we have a candidate. For continuity C: EntryFilled && journalOpenQty==brokerQty && !TradeCompleted.
            // We have all three. So C passes. So we'd ADOPT. To test NO_CONTINUITY_EVIDENCE we need:
            // - No working orders (A/B fail)
            // - C fail: EntryFilled=false? But then journalOpenQty could still be from EntryFilledQuantityTotal.
            // Actually if EntryFilled=false, we might have EntryFilledQuantityTotal=0. So journalOpenQty=0.
            // brokerQty=1. So we'd fail qty match earlier. So we need a scenario where all hard gates pass
            // but no continuity. For C to fail: we need !EntryFilled OR journalOpenQty != brokerQty.
            // If !EntryFilled, we might have EntryFilledQuantityTotal from a partial? Actually adoption
            // candidates require EntrySubmitted && !TradeCompleted. They can have EntryFilled=false (unfilled
            // entry stop). So for an unfilled entry stop, EntryFilledQuantityTotal might be 0. So journalOpenQty=0.
            // brokerQty=1. Qty mismatch - we'd fail earlier. So for NO_CONTINUITY we need: one candidate,
            // qty match, trading date match, but no working orders AND C fails. C fails when !EntryFilled.
            // So we need EntryFilled=false, EntryFilledQuantityTotal=1? That's inconsistent - EntryFilled
            // usually means we have a fill. Let me check - for adoption candidates we have EntrySubmitted && !TradeCompleted.
            // So we could have EntrySubmitted=true, EntryFilled=false (unfilled entry stop), EntryFilledQuantityTotal=0.
            // Then journalOpenQty=0. brokerQty=1. Qty mismatch. So we can't have both qty match and C fail
            // with EntryFilled=false. For C to fail with EntryFilled=true: we'd need journalOpenQty != brokerQty,
            // but that would fail the qty gate. So actually when all hard gates pass, C (journal continuity)
            // will always pass because we have EntryFilled, journalOpenQty==brokerQty, !TradeCompleted.
            // So the only way to get NO_CONTINUITY is when we have no working orders (A/B fail) AND C fails.
            // For NO_CONTINUITY: one candidate, qty match, but no working orders (A/B fail) and C fail.
            // C fails when !EntryFilled. Use inconsistent state: EntryFilled=false, EntryFilledQuantityTotal=1
            // so journalOpenQty=1, brokerQty=1 (qty match). C requires EntryFilled - false, so C fails.
            var entryNoContinuity = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-18",
                Stream = "YM1",
                Instrument = "MYM",
                EntrySubmitted = true,
                EntryFilled = false,
                TradeCompleted = false,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 0
            };
            File.WriteAllText(Path.Combine(journalDir, $"2026-03-18_YM1_{intentId}.json"), JsonUtil.Serialize(entryNoContinuity));

            var log2 = new RobotLogger(Path.Combine(tempRoot, "log2"));
            var journal2 = new ExecutionJournal(tempRoot, log2);
            var position2 = new PositionSnapshot { Instrument = "MYM", Quantity = 1 };
            var snapshot2 = new AccountSnapshot { Positions = new List<PositionSnapshot> { position2 }, WorkingOrders = new List<WorkingOrderSnapshot>() };
            var result2 = UnmatchedPositionPolicyEvaluator.Evaluate(position2, snapshot2, journal2, "2026-03-18", "run1", log2);

            if (result2.Decision != UnmatchedPositionPolicyDecision.FLATTEN)
                return (false, $"Expected FLATTEN (no continuity), got {result2.Decision}");
            if (result2.Reason != "NO_CONTINUITY_EVIDENCE")
                return (false, $"Expected NO_CONTINUITY_EVIDENCE, got {result2.Reason}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    private static (bool Pass, string? Error) TestMustAdoptOneCandidateExactMatchWithContinuity()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var intentId = "f678901234567890";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-18",
                Stream = "YM1",
                Instrument = "MYM",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = false,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 0
            };
            File.WriteAllText(Path.Combine(journalDir, $"2026-03-18_YM1_{intentId}.json"), JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 1 };
            // Continuity C: EntryFilled && journalOpenQty==brokerQty && !TradeCompleted - all true
            var snapshot = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { position },
                WorkingOrders = new List<WorkingOrderSnapshot>() // No working orders; C provides continuity
            };
            var result = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);

            if (result.Decision != UnmatchedPositionPolicyDecision.OWNERSHIP_PROVEN)
                return (false, $"Expected OWNERSHIP_PROVEN, got {result.Decision}");
            if (result.CandidateIntentId != intentId)
                return (false, $"Expected candidate {intentId}, got {result.CandidateIntentId}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    /// <summary>Broker has MYM position; journal has only NQ candidates → NO_CANDIDATE (instrument mismatch).</summary>
    private static (bool Pass, string? Error) TestMustFlattenInstrumentMismatch()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var intentId = "instMismatch12345678";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-18",
                Stream = "NQ1",
                Instrument = "MNQ",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = false,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 0
            };
            File.WriteAllText(Path.Combine(journalDir, $"2026-03-18_NQ1_{intentId}.json"), JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 1 }; // Broker has MYM, journal has MNQ only
            var snapshot = new AccountSnapshot { Positions = new List<PositionSnapshot> { position }, WorkingOrders = new List<WorkingOrderSnapshot>() };
            var result = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);

            if (result.Decision != UnmatchedPositionPolicyDecision.FLATTEN)
                return (false, $"Expected FLATTEN (instrument mismatch), got {result.Decision}");
            if (result.Reason != "NO_CANDIDATE")
                return (false, $"Expected NO_CANDIDATE, got {result.Reason}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    /// <summary>Broker has 2 contracts; journal has 2 intents each with 1 → MULTIPLE_CANDIDATES (partial ambiguous).</summary>
    private static (bool Pass, string? Error) TestMustFlattenPartialAmbiguousBrokerExposure()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            foreach (var (intentId, stream) in new[] { ("partialA1234567890", "YM1"), ("partialB2345678901", "YM2") })
            {
                var entry = new ExecutionJournalEntry
                {
                    IntentId = intentId,
                    TradingDate = "2026-03-18",
                    Stream = stream,
                    Instrument = "MYM",
                    EntrySubmitted = true,
                    EntryFilled = true,
                    TradeCompleted = false,
                    EntryFilledQuantityTotal = 1,
                    ExitFilledQuantityTotal = 0
                };
                File.WriteAllText(Path.Combine(journalDir, $"2026-03-18_{stream}_{intentId}.json"), JsonUtil.Serialize(entry));
            }

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 2 }; // Broker 2, cannot attribute to single intent
            var snapshot = new AccountSnapshot { Positions = new List<PositionSnapshot> { position }, WorkingOrders = new List<WorkingOrderSnapshot>() };
            var result = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);

            if (result.Decision != UnmatchedPositionPolicyDecision.FLATTEN)
                return (false, $"Expected FLATTEN (partial ambiguous), got {result.Decision}");
            if (result.Reason != "MULTIPLE_CANDIDATES")
                return (false, $"Expected MULTIPLE_CANDIDATES, got {result.Reason}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    /// <summary>Repeated evaluation on same inputs returns same result (no oscillation).</summary>
    private static (bool Pass, string? Error) TestRepeatedEvaluationDoesNotOscillate()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var intentId = "oscillate123456789";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-18",
                Stream = "YM1",
                Instrument = "MYM",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = false,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 0
            };
            File.WriteAllText(Path.Combine(journalDir, $"2026-03-18_YM1_{intentId}.json"), JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 1 };
            var snapshot = new AccountSnapshot { Positions = new List<PositionSnapshot> { position }, WorkingOrders = new List<WorkingOrderSnapshot>() };

            var r1 = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);
            var r2 = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);

            if (r1.Decision != r2.Decision)
                return (false, $"Oscillation: first={r1.Decision}, second={r2.Decision}");
            if (r1.Reason != r2.Reason)
                return (false, $"Reason changed: first={r1.Reason}, second={r2.Reason}");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }

    /// <summary>Evaluator is read-only; repeated adopt path returns same result without modifying journal/registry.</summary>
    private static (bool Pass, string? Error) TestNoDuplicateRegistryJournalStateOnRepeatedAdoptPath()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"UnmatchedPolicy_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
            Directory.CreateDirectory(journalDir);

            var intentId = "dupCheck123456789";
            var entry = new ExecutionJournalEntry
            {
                IntentId = intentId,
                TradingDate = "2026-03-18",
                Stream = "YM1",
                Instrument = "MYM",
                EntrySubmitted = true,
                EntryFilled = true,
                TradeCompleted = false,
                EntryFilledQuantityTotal = 1,
                ExitFilledQuantityTotal = 0
            };
            var journalPath = Path.Combine(journalDir, $"2026-03-18_YM1_{intentId}.json");
            File.WriteAllText(journalPath, JsonUtil.Serialize(entry));

            var log = new RobotLogger(Path.Combine(tempRoot, "log"));
            var journal = new ExecutionJournal(tempRoot, log);
            var position = new PositionSnapshot { Instrument = "MYM", Quantity = 1 };
            var snapshot = new AccountSnapshot { Positions = new List<PositionSnapshot> { position }, WorkingOrders = new List<WorkingOrderSnapshot>() };

            for (int i = 0; i < 3; i++)
            {
                var result = UnmatchedPositionPolicyEvaluator.Evaluate(position, snapshot, journal, "2026-03-18", "run1", log);
                if (result.Decision != UnmatchedPositionPolicyDecision.OWNERSHIP_PROVEN)
                    return (false, $"Iteration {i}: expected OWNERSHIP_PROVEN, got {result.Decision}");
            }

            var fileContentAfter = File.ReadAllText(journalPath);
            if (!fileContentAfter.Contains(intentId))
                return (false, "Journal entry corrupted or missing after repeated evaluation");
            // Evaluator is read-only; journal must be unchanged
            var parsed = JsonUtil.Deserialize<ExecutionJournalEntry>(fileContentAfter);
            if (parsed?.IntentId != intentId || parsed.EntryFilledQuantityTotal != 1)
                return (false, "Journal entry was modified by evaluator (should be read-only)");
            return (true, null);
        }
        finally { try { Directory.Delete(tempRoot, true); } catch { } }
    }
}
