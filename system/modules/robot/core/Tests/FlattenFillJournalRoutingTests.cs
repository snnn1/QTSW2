// Post-deploy regression: robot flatten tags decode to pseudo intent "FLATTEN"; journal must
// accept RecordExitFill(..., "FLATTEN") so broker flatten fills close the trade (see ProcessBrokerFlattenFill).
// NT adapter: ProcessExecutionUpdateContinuation early-exits to ProcessBrokerFlattenFill for OrderType FLATTEN / QTSW2:FLATTEN: tags.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test FLATTEN_FILL_JOURNAL

using System;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class FlattenFillJournalRoutingTests
{
    public static (bool Pass, string? Error) RunFlattenFillJournalRoutingTests()
    {
        if (!string.Equals(RobotOrderIds.DecodeIntentId($"{RobotOrderIds.Prefix}FLATTEN:REQ1"),
                "FLATTEN", StringComparison.Ordinal))
            return (false, "DecodeIntentId must return FLATTEN for QTSW2:FLATTEN:... (pseudo-intent; not in IntentMap)");

        var root = Path.Combine(Path.GetTempPath(), "FlattenFillJournalRoutingTests_" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        try
        {
            var log = new RobotLogger(root);
            var journal = new ExecutionJournal(root, log);
            var utc = DateTimeOffset.UtcNow;
            const string tradingDate = "2026-03-26";
            const string stream = "T1";
            var intentId = "intent_flatten_close_" + Guid.NewGuid().ToString("N")[..6];

            journal.RecordSubmission(intentId, tradingDate, stream, "MES", "ENTRY_STOP", "b1", utc);
            journal.RecordEntryFill(intentId, tradingDate, stream, 4500m, 3, utc, 5m, "Long", "MES", "ES",
                brokerOrderInstrumentKey: "MES");

            if (journal.IsIntentCompleted(intentId, tradingDate, stream))
                return (false, "Trade must not be completed before flatten exit fill");

            journal.RecordExitFill(intentId, tradingDate, stream, 4501m, 3, "FLATTEN", utc);

            if (!journal.IsIntentCompleted(intentId, tradingDate, stream))
                return (false, "RecordExitFill(FLATTEN) must set TradeCompleted when qty matches");

            var openByInstrument = journal.GetOpenJournalEntriesByInstrument();
            if (openByInstrument.Count > 0)
                return (false, "No open journal buckets expected after FLATTEN exit");

            var mesOpenQty = journal.GetOpenJournalQuantitySumForInstrument("MES", "ES");
            if (mesOpenQty != 0)
                return (false, $"Open journal qty for MES should be 0, got {mesOpenQty}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }
}
