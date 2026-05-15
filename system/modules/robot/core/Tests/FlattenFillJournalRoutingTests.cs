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

        var flattenRequestId = "FLATTEN:MES:20260511205500123:L0";
        if (!string.Equals(
                RobotOrderIds.DecodeFlattenRequestId($"{RobotOrderIds.Prefix}FLATTEN:{flattenRequestId}"),
                flattenRequestId,
                StringComparison.Ordinal))
        {
            return (false, "DecodeFlattenRequestId must recover the full IEA flatten request id from QTSW2:FLATTEN tags");
        }

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

            var allocationPolicyResult = RunSessionForcedFlattenAllocationPolicyTest(journal, utc, tradingDate);
            if (!allocationPolicyResult.Pass)
                return allocationPolicyResult;

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { /* ignore */ }
        }
    }

    private static (bool Pass, string? Error) RunSessionForcedFlattenAllocationPolicyTest(
        ExecutionJournal journal,
        DateTimeOffset utc,
        string tradingDate)
    {
        var ng1Intent = "intent_ng1_flatten_" + Guid.NewGuid().ToString("N")[..6];
        var ng2Intent = "intent_ng2_flatten_" + Guid.NewGuid().ToString("N")[..6];

        journal.RecordSubmission(ng1Intent, tradingDate, "NG1", "MNG", "ENTRY_STOP", "b-ng1", utc);
        journal.RecordEntryFill(ng1Intent, tradingDate, "NG1", 2.650m, 2, utc, 0.01m, "Short", "MNG", "NG",
            brokerOrderInstrumentKey: "MNG");
        journal.RecordSubmission(ng2Intent, tradingDate, "NG2", "MNG", "ENTRY_STOP", "b-ng2", utc);
        journal.RecordEntryFill(ng2Intent, tradingDate, "NG2", 2.650m, 2, utc, 0.01m, "Short", "MNG", "NG",
            brokerOrderInstrumentKey: "MNG");

        var openRows = journal.GetOpenJournalEntriesByInstrument()
            .Where(kvp => ExecutionInstrumentResolver.IsSameInstrument(kvp.Key, "MNG"))
            .SelectMany(kvp => kvp.Value)
            .ToList();
        var openQty = openRows.Sum(row => ExecutionJournal.GetEntryRemainingOpenQuantity(row.Entry));

        if (openRows.Count != 2 || openQty != 4)
            return (false, $"Expected two MNG open journal rows totaling 4, got rows={openRows.Count} qty={openQty}");

        if (!FlattenFillAllocationPolicy.ShouldPreferOpenJournalAllocationForRegistryLink(
                "SESSION_FORCED_FLATTEN", 4, 2, openRows.Count, openQty))
        {
            return (false, "Session forced flatten must prefer open journal allocation when one broker flatten closes two stream intents");
        }

        if (FlattenFillAllocationPolicy.ShouldPreferOpenJournalAllocationForRegistryLink(
                "MANUAL_FLATTEN", 4, 2, openRows.Count, openQty))
        {
            return (false, "Non-session flatten registry link should not be widened to every open journal row");
        }

        if (FlattenFillAllocationPolicy.ShouldPreferOpenJournalAllocationForRegistryLink(
                "SESSION_FORCED_FLATTEN", 2, 2, 1, 2))
        {
            return (false, "Single-row session flatten should keep the exact registry intent link");
        }

        return (true, null);
    }
}
