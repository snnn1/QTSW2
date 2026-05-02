// Journal reopen normalization + coordinator rehydration (reconnect class).
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test JOURNAL_REOPEN_EXPOSURE_REHYDRATE

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class JournalReopenAndExposureRehydrateTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var a = TaggedReopenClearsTerminalExitState();
        if (!a.Pass) return (false, $"reopen_norm: {a.Error}");
        var b = PartialExitNotWiped();
        if (!b.Pass) return (false, $"partial: {b.Error}");
        var c = TaggedRecoveryHydratesMultiplierFromSiblingAndClosesOnFlatten();
        if (!c.Pass) return (false, $"multiplier_hydrate: {c.Error}");
        var d = RehydrateAllowsExitWithoutNoExposure();
        if (!d.Pass) return (false, $"rehydrate: {d.Error}");
        return (true, null);
    }

    /// <summary>Completed row with exit == entry: reopen must clear exit side so row is not numerically flat.</summary>
    private static (bool Pass, string? Error) TaggedReopenClearsTerminalExitState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ReopenNorm_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
            Directory.CreateDirectory(journalDir);
            var path = Path.Combine(journalDir, "2026-04-01_TEST_INJECT_intentx.json");
            var seed = new ExecutionJournalEntry
            {
                IntentId = "intentx",
                TradingDate = "2026-04-01",
                Stream = "TEST_INJECT",
                Instrument = "MES",
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 2,
                TradeCompleted = true,
                CompletionReason = "STOP",
                Direction = "Long",
                CompletedAtUtc = "2026-04-01T00:52:51+00:00",
                ExitOrderType = "STOP",
                ExitFilledAtUtc = "2026-04-01T00:52:51+00:00"
            };
            File.WriteAllText(path, JsonUtil.Serialize(seed));

            var log = new RobotLogger(tempRoot);
            var journal = new ExecutionJournal(tempRoot, log);
            var utc = DateTimeOffset.Parse("2026-04-01T12:00:00+00:00");
            journal.UpsertTaggedBrokerExposureRecoveryJournal(
                "intentx", "2026-04-01", "TEST_INJECT", "MES", "bid", 1, "Long", null, null, null, utc, null);

            var after = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(path));
            if (after == null) return (false, "deserialized null");
            if (after.ExitFilledQuantityTotal != 0)
                return (false, $"expected ExitFilledQuantityTotal 0, got {after.ExitFilledQuantityTotal}");
            if (after.TradeCompleted)
                return (false, "TradeCompleted should be false");
            if (after.ExitOrderType != null)
                return (false, "ExitOrderType should be cleared");
            if (after.CompletionReason != null)
                return (false, "CompletionReason should be cleared");
            var open = after.EntryFilledQuantityTotal - after.ExitFilledQuantityTotal;
            if (open <= 0) return (false, "row should have positive open journal qty");
            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch { /* best-effort */ }
        }
    }

    /// <summary>Partial exit (exit &lt; entry), not completed: reopen must preserve exit cumulatives.</summary>
    private static (bool Pass, string? Error) PartialExitNotWiped()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ReopenPartial_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
            Directory.CreateDirectory(journalDir);
            var path = Path.Combine(journalDir, "2026-04-01_TEST_INJECT_intentp.json");
            var seed = new ExecutionJournalEntry
            {
                IntentId = "intentp",
                TradingDate = "2026-04-01",
                Stream = "TEST_INJECT",
                Instrument = "MES",
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                ExitFilledQuantityTotal = 1,
                TradeCompleted = false,
                Direction = "Long"
            };
            File.WriteAllText(path, JsonUtil.Serialize(seed));

            var log = new RobotLogger(tempRoot);
            var journal = new ExecutionJournal(tempRoot, log);
            var utc = DateTimeOffset.Parse("2026-04-01T12:00:00+00:00");
            journal.UpsertTaggedBrokerExposureRecoveryJournal(
                "intentp", "2026-04-01", "TEST_INJECT", "MES", "bid", 1, "Long", null, null, null, utc, null);

            var after = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(path));
            if (after == null) return (false, "deserialized null");
            if (after.ExitFilledQuantityTotal != 1)
                return (false, $"partial exit should be preserved, got exit {after.ExitFilledQuantityTotal}");
            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch { /* best-effort */ }
        }
    }

    private static (bool Pass, string? Error) TaggedRecoveryHydratesMultiplierFromSiblingAndClosesOnFlatten()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"TaggedRecoveryMultiplier_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = RobotRunArtifactPaths.StateExecutionJournals(tempRoot);
            Directory.CreateDirectory(journalDir);

            const string tradingDate = "2026-04-14";
            const string stream = "GC1";
            const string siblingIntent = "265cb3d9c03e8d4f";
            const string recoveredIntent = "f27b9be8a8e29ac9";

            var siblingPath = Path.Combine(journalDir, $"{tradingDate}_{stream}_{siblingIntent}.json");
            var sibling = new ExecutionJournalEntry
            {
                IntentId = siblingIntent,
                TradingDate = tradingDate,
                Stream = stream,
                Instrument = "MGC",
                EntrySubmitted = true,
                EntryFilled = true,
                EntryFilledQuantityTotal = 2,
                EntryAvgFillPrice = 4820.4m,
                EntryFillNotional = 9640.8m,
                ContractMultiplier = 10m,
                Direction = "Short",
                TradeCompleted = true,
                CompletionReason = "TARGET"
            };
            File.WriteAllText(siblingPath, JsonUtil.Serialize(sibling));

            var stoodDown = false;
            var log = new RobotLogger(tempRoot);
            var journal = new ExecutionJournal(tempRoot, log);
            journal.SetJournalCorruptionCallback((_, _, _, _) => stoodDown = true);
            var utc = DateTimeOffset.Parse("2026-04-14T12:38:55.900+00:00");

            journal.UpsertTaggedBrokerExposureRecoveryJournal(
                recoveredIntent,
                tradingDate,
                stream,
                "MGC",
                "target-order",
                1,
                "Short",
                4825.7m,
                null,
                null,
                utc,
                "test");

            var recoveredPath = Path.Combine(journalDir, $"{tradingDate}_{stream}_{recoveredIntent}.json");
            var afterUpsert = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(recoveredPath));
            if (afterUpsert == null) return (false, "recovered row missing after upsert");
            if (afterUpsert.ContractMultiplier != 10m)
                return (false, $"expected hydrated ContractMultiplier 10, got {afterUpsert.ContractMultiplier}");
            if (afterUpsert.EntryFillNotional != 4825.7m)
                return (false, $"expected EntryFillNotional 4825.7, got {afterUpsert.EntryFillNotional}");

            journal.RecordExitFill(
                recoveredIntent,
                tradingDate,
                stream,
                4812.3m,
                1,
                "FLATTEN",
                utc.AddSeconds(1));

            var afterExit = JsonUtil.Deserialize<ExecutionJournalEntry>(File.ReadAllText(recoveredPath));
            if (afterExit == null) return (false, "recovered row missing after exit");
            if (stoodDown)
                return (false, "journal corruption callback should not fire when sibling multiplier evidence exists");
            if (!afterExit.TradeCompleted)
                return (false, "recovered row should complete after matching flatten exit");
            if (afterExit.ExitFilledQuantityTotal != 1)
                return (false, $"expected exit qty 1, got {afterExit.ExitFilledQuantityTotal}");
            if (afterExit.RealizedPnLGross != 134.0m)
                return (false, $"expected gross PnL 134.0, got {afterExit.RealizedPnLGross}");

            return (true, null);
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempRoot))
                    Directory.Delete(tempRoot, true);
            }
            catch { /* best-effort */ }
        }
    }

    private static (bool Pass, string? Error) RehydrateAllowsExitWithoutNoExposure()
    {
        var log = new RobotLogger(Path.Combine(Path.GetTempPath(), "rehydrate_coord_log"));
        var coord = new InstrumentIntentCoordinator(
            log,
            () => new AccountSnapshot { Positions = new List<PositionSnapshot>() },
            standDownStreamCallback: null,
            flattenIntentCallback: null,
            cancelIntentOrdersCallback: null);
        var utc = DateTimeOffset.UtcNow;
        if (!coord.TryRehydrateOpenExposureFromJournal("i1", "TEST_INJECT", "ES", "Long", 2, 1, utc, "journal_recovery"))
            return (false, "TryRehydrateOpenExposureFromJournal returned false");
        coord.OnExitFill("i1", 1, utc);
        var exp = coord.GetExposure("i1");
        if (exp == null || exp.ExitFilledQty != 2)
            return (false, "exit fill should apply to rehydrated exposure");
        return (true, null);
    }
}
