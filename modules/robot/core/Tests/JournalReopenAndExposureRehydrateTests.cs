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
        var c = RehydrateAllowsExitWithoutNoExposure();
        if (!c.Pass) return (false, $"rehydrate: {c.Error}");
        return (true, null);
    }

    /// <summary>Completed row with exit == entry: reopen must clear exit side so row is not numerically flat.</summary>
    private static (bool Pass, string? Error) TaggedReopenClearsTerminalExitState()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"ReopenNorm_{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(tempRoot);
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
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
            var journalDir = Path.Combine(tempRoot, "data", "execution_journals");
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
