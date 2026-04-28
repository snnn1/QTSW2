// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test REENTRY_MARKET_CLOSE_COMMIT
//
// Verifies market-close completion checks use the current lifecycle intent, not any completed
// historical trade in the same stream.

using System;
using System.IO;
using System.Reflection;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class ReentryMarketCloseCommitTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var first = Case_OriginalCompletedButReentryOpen_DoesNotCommit();
        if (!first.Pass) return first;

        var forcedFlattenReentry = Case_ForcedFlattenOriginalCompleteWithActiveReentry_DoesNotCommit();
        if (!forcedFlattenReentry.Pass) return forcedFlattenReentry;

        var preEntryBrackets = Case_ForcedFlattenPreEntryLiveBrackets_CancelsBeforeNoTradeCommit();
        if (!preEntryBrackets.Pass) return preEntryBrackets;

        var preEntryStreamJournalResidue = Case_ForcedFlattenPreEntryStreamJournalResidue_CancelsBeforeNoTradeCommit();
        if (!preEntryStreamJournalResidue.Pass) return preEntryStreamJournalResidue;

        var second = Case_ReentryCompleted_CommitsAtMarketClose();
        if (!second.Pass) return second;

        var third = Case_ReentryFilledButUncompleted_DoesNotCommitNoTrade();
        if (!third.Pass) return third;

        var fourth = Case_LateSessionCloseConfirm_AtMarketOpenEnqueuesReentry();
        if (!fourth.Pass) return fourth;

        var fifth = Case_LateSessionCloseConfirm_WallClockAfterSlotDefersUntilMarketOpenTick();
        if (!fifth.Pass) return fifth;

        var sixth = Case_LateSessionCloseReentryBlockBypass_PredicateOnlyAllowsFlatQuietInterruptedShape();
        if (!sixth.Pass) return sixth;

        return (true, null);
    }

    private static (bool Pass, string? Error) Case_ForcedFlattenOriginalCompleteWithActiveReentry_DoesNotCommit()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ForcedFlattenReentryGate_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(tempRoot));

            var spec = LoadSpec();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);

            const string tradingDate = "2026-04-13";
            const string stream = "NG2";
            const string instrument = "MNG";
            const string originalIntent = "orig-ng2";
            const string reentryIntent = "reentry-ng2";
            var utc = DateTimeOffset.Parse("2026-04-14T22:01:00Z");

            SeedCompletedTrade(executionJournal, tradingDate, stream, instrument, originalIntent, utc.AddMinutes(-90));
            SeedOpenTrade(executionJournal, tradingDate, stream, instrument, reentryIntent, utc.AddMinutes(-2));

            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate,
                Stream = stream,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = false,
                OriginalIntentId = originalIntent,
                ReentryIntentId = reentryIntent,
                ReentrySubmitted = true,
                ReentryFilled = true,
                ProtectionSubmitted = true,
                ProtectionAccepted = true,
                SlotInstanceKey = "2026-04-13_NG2_11:00",
                LastState = "RANGE_LOCKED",
                NextSlotTimeUtc = DateTimeOffset.Parse("2026-04-14T16:00:00Z")
            });

            var sm = CreateStreamStateMachine(tempRoot, time, spec, log, journals, executionJournal, tradingDate, stream, instrument, new CapturingExecutionAdapter(log));
            sm.HandleForcedFlatten(utc);

            var reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.Committed == true)
                return (false, $"HandleForcedFlatten should not commit original-complete stream while reentry is open, got {reloaded.CommitReason ?? "null"}");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Case_ForcedFlattenPreEntryLiveBrackets_CancelsBeforeNoTradeCommit()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ForcedFlattenPreEntryBrackets_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(tempRoot));

            var spec = LoadSpec();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);

            const string tradingDate = "2026-04-13";
            const string stream = "RTY2";
            const string instrument = "M2K";
            var utc = DateTimeOffset.Parse("2026-04-13T20:55:00Z");

            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate,
                Stream = stream,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                StopBracketsSubmittedAtLock = true,
                SlotInstanceKey = "2026-04-13_RTY2_11:00",
                LastState = "RANGE_LOCKED",
                NextSlotTimeUtc = DateTimeOffset.Parse("2026-04-14T16:00:00Z")
            });

            var adapter = new CapturingExecutionAdapter(log);
            var sm = CreateStreamStateMachine(tempRoot, time, spec, log, journals, executionJournal, tradingDate, stream, instrument, adapter);
            SetPrivate(sm, "_brkLongRounded", 2726.1m);
            SetPrivate(sm, "_brkShortRounded", 2696.1m);
            SetPrivate(sm, "_stopBracketsSubmittedAtLock", true);

            var longIntentId = InvokeComputeIntentId(sm, "Long", 2726.1m, "ENTRY_STOP_BRACKET_LONG");
            var shortIntentId = InvokeComputeIntentId(sm, "Short", 2696.1m, "ENTRY_STOP_BRACKET_SHORT");
            adapter.Snapshot.WorkingOrders.Add(new WorkingOrderSnapshot
            {
                OrderId = "long-working",
                Instrument = instrument,
                Tag = RobotOrderIds.EncodeTag(longIntentId) + ":ENTRY",
                StopPrice = 2726.1m,
                Quantity = 2
            });
            adapter.Snapshot.WorkingOrders.Add(new WorkingOrderSnapshot
            {
                OrderId = "short-working",
                Instrument = instrument,
                Tag = RobotOrderIds.EncodeTag(shortIntentId) + ":ENTRY",
                StopPrice = 2696.1m,
                Quantity = 2
            });

            sm.HandleForcedFlatten(utc);

            var reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.Committed == true)
                return (false, "Pre-entry forced flatten must not commit while bracket orders are still working");
            if (!adapter.CancelledOrderIds.Contains("long-working") || !adapter.CancelledOrderIds.Contains("short-working"))
                return (false, "Expected pre-entry forced flatten to request cancellation of both live bracket orders");

            adapter.Snapshot.WorkingOrders.Clear();
            sm.HandleForcedFlatten(utc.AddSeconds(6));

            reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.CommitReason != "NO_TRADE_FORCED_FLATTEN_PRE_ENTRY")
                return (false, $"Expected no-trade commit after bracket orders disappeared, got {reloaded?.CommitReason ?? "null"}");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Case_ForcedFlattenPreEntryStreamJournalResidue_CancelsBeforeNoTradeCommit()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ForcedFlattenPreEntryStreamJournal_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(tempRoot));

            var spec = LoadSpec();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);

            const string tradingDate = "2026-04-14";
            const string stream = "RTY2";
            const string instrument = "M2K";
            const string liveLongIntent = "actual-rty2-long";
            const string liveShortIntent = "actual-rty2-short";
            var utc = DateTimeOffset.Parse("2026-04-14T20:55:00Z");

            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate,
                Stream = stream,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                StopBracketsSubmittedAtLock = true,
                SlotInstanceKey = "2026-04-14_RTY2_11:00",
                LastState = "RANGE_LOCKED",
                NextSlotTimeUtc = DateTimeOffset.Parse("2026-04-15T16:00:00Z")
            });

            executionJournal.RecordSubmission(liveLongIntent, tradingDate, stream, instrument, "ENTRY_STOP", "long-broker", utc.AddHours(-5));
            executionJournal.RecordSubmission(liveShortIntent, tradingDate, stream, instrument, "ENTRY_STOP", "short-broker", utc.AddHours(-5));

            var adapter = new CapturingExecutionAdapter(log);
            adapter.Snapshot.WorkingOrders.Add(new WorkingOrderSnapshot
            {
                OrderId = "unmatched-live-long",
                Instrument = instrument,
                Tag = RobotOrderIds.EncodeTag("different-long") + ":ENTRY",
                StopPrice = 2726.1m,
                Quantity = 2
            });
            adapter.Snapshot.WorkingOrders.Add(new WorkingOrderSnapshot
            {
                OrderId = "unmatched-live-short",
                Instrument = instrument,
                Tag = RobotOrderIds.EncodeTag("different-short") + ":ENTRY",
                StopPrice = 2693.9m,
                Quantity = 2
            });

            var sm = CreateStreamStateMachine(tempRoot, time, spec, log, journals, executionJournal, tradingDate, stream, instrument, adapter);
            SetPrivate(sm, "_brkLongRounded", 2726.1m);
            SetPrivate(sm, "_brkShortRounded", 2693.9m);
            SetPrivate(sm, "_stopBracketsSubmittedAtLock", true);

            sm.HandleForcedFlatten(utc);

            var reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.Committed == true)
                return (false, "Pre-entry forced flatten must not commit while same-instrument working orders and pending stream journals remain");
            if (!adapter.CancelledOrderIds.Contains("long-broker") || !adapter.CancelledOrderIds.Contains("short-broker"))
                return (false, "Expected pre-entry forced flatten to cancel broker ids from pending stream journals when exact recomputed intent match is unavailable");

            adapter.Snapshot.WorkingOrders.Clear();
            sm.HandleForcedFlatten(utc.AddSeconds(6));

            reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.CommitReason != "NO_TRADE_FORCED_FLATTEN_PRE_ENTRY")
                return (false, $"Expected no-trade commit after journal residue terminalized, got {reloaded?.CommitReason ?? "null"}");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Case_OriginalCompletedButReentryOpen_DoesNotCommit()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ReentryCloseGate_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(tempRoot));

            var spec = LoadSpec();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);

            const string tradingDate = "2026-04-13";
            const string stream = "NG2";
            const string instrument = "MNG";
            const string originalIntent = "orig-ng2";
            const string reentryIntent = "reentry-ng2";
            var utc = DateTimeOffset.Parse("2026-04-23T22:01:00Z");

            SeedCompletedTrade(executionJournal, tradingDate, stream, instrument, originalIntent, utc.AddMinutes(-10));
            SeedOpenTrade(executionJournal, tradingDate, stream, instrument, reentryIntent, utc.AddMinutes(-2));

            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate,
                Stream = stream,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = false,
                OriginalIntentId = originalIntent,
                ReentryIntentId = reentryIntent,
                ReentrySubmitted = true,
                ReentryFilled = true,
                ProtectionSubmitted = true,
                ProtectionAccepted = true,
                SlotInstanceKey = "2026-04-13_NG2_11:00",
                LastState = "RANGE_LOCKED"
            });

            var sm = CreateStreamStateMachine(tempRoot, time, spec, log, journals, executionJournal, tradingDate, stream, instrument);
            var result = InvokeTryCommitCompletedTradeAtMarketClose(sm, utc);
            if (result)
                return (false, "Expected market-close commit to stay false when only original intent is completed");

            var reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.Committed == true)
                return (false, "Stream journal should remain uncommitted while reentry intent is still open");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Case_ReentryCompleted_CommitsAtMarketClose()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ReentryCloseCommit_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(tempRoot));

            var spec = LoadSpec();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);

            const string tradingDate = "2026-04-13";
            const string stream = "NG2";
            const string instrument = "MNG";
            const string originalIntent = "orig-ng2";
            const string reentryIntent = "reentry-ng2";
            var utc = DateTimeOffset.Parse("2026-04-23T22:01:00Z");

            SeedCompletedTrade(executionJournal, tradingDate, stream, instrument, originalIntent, utc.AddMinutes(-10));
            SeedCompletedTrade(executionJournal, tradingDate, stream, instrument, reentryIntent, utc.AddMinutes(-2));

            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate,
                Stream = stream,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = false,
                OriginalIntentId = originalIntent,
                ReentryIntentId = reentryIntent,
                ReentrySubmitted = true,
                ReentryFilled = true,
                ProtectionSubmitted = true,
                ProtectionAccepted = true,
                SlotInstanceKey = "2026-04-13_NG2_11:00",
                LastState = "RANGE_LOCKED"
            });

            var sm = CreateStreamStateMachine(tempRoot, time, spec, log, journals, executionJournal, tradingDate, stream, instrument);
            var result = InvokeTryCommitCompletedTradeAtMarketClose(sm, utc);
            if (!result)
                return (false, "Expected market-close commit when reentry intent itself is completed");

            var reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.CommitReason != "REENTRY_TRADE_COMPLETED_MARKET_CLOSE")
                return (false, $"Expected REENTRY_TRADE_COMPLETED_MARKET_CLOSE, got {reloaded?.CommitReason ?? "null"}");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Case_ReentryFilledButUncompleted_DoesNotCommitNoTrade()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "ReentryNoTradeGate_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(tempRoot));

            var spec = LoadSpec();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);

            const string tradingDate = "2026-04-13";
            const string stream = "NG2";
            const string instrument = "MNG";
            const string originalIntent = "orig-ng2";
            const string reentryIntent = "reentry-ng2";
            var utc = DateTimeOffset.Parse("2026-04-23T22:01:00Z");

            SeedCompletedTrade(executionJournal, tradingDate, stream, instrument, originalIntent, utc.AddMinutes(-10));
            SeedOpenTrade(executionJournal, tradingDate, stream, instrument, reentryIntent, utc.AddMinutes(-2));

            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate,
                Stream = stream,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = false,
                OriginalIntentId = originalIntent,
                ReentryIntentId = reentryIntent,
                ReentrySubmitted = true,
                ReentryFilled = true,
                ProtectionSubmitted = true,
                ProtectionAccepted = true,
                EntryDetected = false,
                SlotInstanceKey = "2026-04-13_NG2_11:00",
                LastState = "RANGE_LOCKED"
            });

            var sm = CreateStreamStateMachine(tempRoot, time, spec, log, journals, executionJournal, tradingDate, stream, instrument);
            InvokeHandleRangeLockedState(sm, utc);

            var reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.Committed == true)
                return (false, $"Expected stream to remain unresolved at market close, got commit reason {reloaded.CommitReason ?? "null"}");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static void SeedOpenTrade(ExecutionJournal executionJournal, string tradingDate, string stream, string instrument, string intentId, DateTimeOffset utcNow)
    {
        executionJournal.RecordSubmission(intentId, tradingDate, stream, instrument, "ENTRY", intentId + "-entry", utcNow);
        executionJournal.RecordEntryFill(intentId, tradingDate, stream, 2.619m, 2, utcNow, 1m, "Short", instrument, instrument);
    }

    private static void SeedCompletedTrade(ExecutionJournal executionJournal, string tradingDate, string stream, string instrument, string intentId, DateTimeOffset utcNow)
    {
        executionJournal.RecordSubmission(intentId, tradingDate, stream, instrument, "ENTRY", intentId + "-entry", utcNow);
        executionJournal.RecordEntryFill(intentId, tradingDate, stream, 2.635m, 2, utcNow, 1m, "Short", instrument, instrument);
        executionJournal.RecordExitFill(intentId, tradingDate, stream, 2.600m, 2, "FLATTEN", utcNow.AddSeconds(1));
    }

    private static (bool Pass, string? Error) Case_LateSessionCloseConfirm_AtMarketOpenEnqueuesReentry()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "LateCloseReentry_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(tempRoot));

            var spec = LoadSpec();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);

            const string tradingDate = "2026-04-13";
            const string stream = "NG1";
            const string instrument = "MNG";
            const string originalIntent = "orig-ng1";
            var forcedFlattenUtc = DateTimeOffset.Parse("2026-04-13T20:55:00Z");
            var reopenUtc = DateTimeOffset.Parse("2026-04-13T22:00:01Z");

            SeedCompletedTrade(executionJournal, tradingDate, stream, instrument, originalIntent, forcedFlattenUtc);

            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate,
                Stream = stream,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = true,
                ForcedFlattenTimestamp = forcedFlattenUtc,
                OriginalIntentId = originalIntent,
                ReentrySubmitted = false,
                ReentryFilled = false,
                ProtectionSubmitted = false,
                ProtectionAccepted = false,
                SlotInstanceKey = "2026-04-13_NG1_07:30",
                LastState = "RANGE_LOCKED",
                NextSlotTimeUtc = DateTimeOffset.Parse("2026-04-14T12:30:00Z")
            });

            var capturingAdapter = new CapturingExecutionAdapter(log);
            var sm = CreateStreamStateMachine(tempRoot, time, spec, log, journals, executionJournal, tradingDate, stream, instrument, capturingAdapter);
            sm.HandleLateSessionCloseFlattenConfirmed(reopenUtc);

            if (!capturingAdapter.TryGetLastCommand<SubmitMarketReentryCommand>(out var cmd) || cmd == null)
                return (false, "Expected late session-close confirmation at market open to enqueue market reentry");

            var reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.ReentrySubmitPending != true)
                return (false, "Expected ReentrySubmitPending=true after late session-close reentry enqueue");
            if (string.IsNullOrWhiteSpace(reloaded?.ReentryIntentId))
                return (false, "Expected ReentryIntentId to be created after late session-close reentry enqueue");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Case_LateSessionCloseConfirm_WallClockAfterSlotDefersUntilMarketOpenTick()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "LateCloseExpiredReentry_" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(Path.Combine(tempRoot, "logs", "robot", "journal"));
            Directory.CreateDirectory(RobotRunArtifactPaths.StateExecutionJournals(tempRoot));

            var spec = LoadSpec();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var journals = new JournalStore(tempRoot);
            var executionJournal = new ExecutionJournal(tempRoot, log);

            const string tradingDate = "2026-04-13";
            const string stream = "NG2";
            const string instrument = "MNG";
            const string originalIntent = "orig-ng2";
            var forcedFlattenUtc = DateTimeOffset.Parse("2026-04-13T20:55:00Z");
            var reopenUtc = DateTimeOffset.Parse("2026-04-13T22:00:01Z");
            var wallClockUtc = DateTimeOffset.Parse("2026-04-24T15:17:26Z");

            SeedCompletedTrade(executionJournal, tradingDate, stream, instrument, originalIntent, forcedFlattenUtc);

            journals.Save(new StreamJournal
            {
                TradingDate = tradingDate,
                Stream = stream,
                Committed = false,
                SlotStatus = SlotStatus.ACTIVE,
                ExecutionInterruptedByClose = true,
                ForcedFlattenTimestamp = forcedFlattenUtc,
                OriginalIntentId = originalIntent,
                ReentrySubmitted = false,
                ReentryFilled = false,
                ProtectionSubmitted = false,
                ProtectionAccepted = false,
                SlotInstanceKey = "2026-04-13_NG2_11:00",
                LastState = "RANGE_LOCKED",
                NextSlotTimeUtc = DateTimeOffset.Parse("2026-04-14T16:00:00Z")
            });

            var capturingAdapter = new CapturingExecutionAdapter(log);
            var sm = CreateStreamStateMachine(tempRoot, time, spec, log, journals, executionJournal, tradingDate, stream, instrument, capturingAdapter);
            sm.HandleLateSessionCloseFlattenConfirmed(wallClockUtc);

            if (capturingAdapter.CommandCount != 0)
                return (false, "Wall-clock late confirmation after the historical slot must not enqueue reentry immediately");

            var reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.ReentrySubmitPending == true)
                return (false, "Wall-clock late confirmation before logical reopen should leave ReentrySubmitPending=false");
            if (!string.IsNullOrWhiteSpace(reloaded?.ReentryIntentId))
                return (false, "Wall-clock late confirmation before logical reopen should not create ReentryIntentId");

            sm.Tick(reopenUtc);

            if (!capturingAdapter.TryGetLastCommand<SubmitMarketReentryCommand>(out var cmd) || cmd == null)
                return (false, "Expected market-open tick to enqueue deferred late session-close reentry");

            reloaded = journals.TryLoad(tradingDate, stream);
            if (reloaded?.ReentrySubmitPending != true)
                return (false, "Expected ReentrySubmitPending=true after market-open tick");
            if (string.IsNullOrWhiteSpace(reloaded?.ReentryIntentId))
                return (false, "Expected ReentryIntentId to be created after market-open tick");

            return (true, null);
        }
        finally
        {
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true); } catch { }
        }
    }

    private static (bool Pass, string? Error) Case_LateSessionCloseReentryBlockBypass_PredicateOnlyAllowsFlatQuietInterruptedShape()
    {
        if (!RobotEngine.ShouldBypassInterruptedLateSessionCloseReentryBlock(
                hasInterruptedSessionCloseStream: true,
                hasSupervisoryBlock: false,
                brokerPositionQty: 0,
                brokerWorkingCount: 0))
        {
            return (false, "Expected interrupted late-close stream with broker-flat quiet state to bypass stale reentry block");
        }

        if (RobotEngine.ShouldBypassInterruptedLateSessionCloseReentryBlock(
                hasInterruptedSessionCloseStream: false,
                hasSupervisoryBlock: false,
                brokerPositionQty: 0,
                brokerWorkingCount: 0))
        {
            return (false, "Expected no bypass when there is no interrupted late-close stream");
        }

        if (RobotEngine.ShouldBypassInterruptedLateSessionCloseReentryBlock(
                hasInterruptedSessionCloseStream: true,
                hasSupervisoryBlock: true,
                brokerPositionQty: 0,
                brokerWorkingCount: 0))
        {
            return (false, "Expected supervisory blocks to remain fail-closed");
        }

        if (RobotEngine.ShouldBypassInterruptedLateSessionCloseReentryBlock(
                hasInterruptedSessionCloseStream: true,
                hasSupervisoryBlock: false,
                brokerPositionQty: 2,
                brokerWorkingCount: 0))
        {
            return (false, "Expected no bypass when broker position is still open");
        }

        if (RobotEngine.ShouldBypassInterruptedLateSessionCloseReentryBlock(
                hasInterruptedSessionCloseStream: true,
                hasSupervisoryBlock: false,
                brokerPositionQty: 0,
                brokerWorkingCount: 1))
        {
            return (false, "Expected no bypass when broker working orders remain");
        }

        return (true, null);
    }

    private static StreamStateMachine CreateStreamStateMachine(
        string tempRoot,
        TimeService time,
        ParitySpec spec,
        RobotLogger log,
        JournalStore journals,
        ExecutionJournal executionJournal,
        string tradingDate,
        string stream,
        string instrument,
        IExecutionAdapter? executionAdapter = null)
    {
        var directive = new TimetableStream
        {
            stream = stream,
            instrument = instrument,
            session = "S2",
            slot_time = "11:00",
            enabled = true,
            decision_time = "11:00"
        };

        return new StreamStateMachine(
            time,
            spec,
            log,
            journals,
            DateOnly.Parse(tradingDate),
            "test_hash",
            directive,
            ExecutionMode.DRYRUN,
            1,
            2,
            tempRoot,
            tempRoot,
            executionAdapter: executionAdapter ?? new NullExecutionAdapter(log),
            executionJournal: executionJournal);
    }

    private static bool InvokeTryCommitCompletedTradeAtMarketClose(StreamStateMachine sm, DateTimeOffset utcNow)
    {
        var method = typeof(StreamStateMachine).GetMethod("TryCommitCompletedTradeAtMarketClose", BindingFlags.Instance | BindingFlags.NonPublic);
        return method != null && (bool)(method.Invoke(sm, new object[] { utcNow }) ?? false);
    }

    private static void InvokeHandleRangeLockedState(StreamStateMachine sm, DateTimeOffset utcNow)
    {
        var method = typeof(StreamStateMachine).GetMethod("HandleRangeLockedState", BindingFlags.Instance | BindingFlags.NonPublic);
        method?.Invoke(sm, new object[] { utcNow });
    }

    private static string InvokeComputeIntentId(StreamStateMachine sm, string direction, decimal entryPrice, string triggerReason)
    {
        var slotProp = typeof(StreamStateMachine).GetProperty("SlotTimeUtc", BindingFlags.Instance | BindingFlags.Public);
        var slotTimeUtc = (DateTimeOffset)(slotProp?.GetValue(sm) ?? DateTimeOffset.MinValue);
        var method = typeof(StreamStateMachine).GetMethod("ComputeIntentId", BindingFlags.Instance | BindingFlags.NonPublic);
        return (string)(method?.Invoke(sm, new object[] { direction, entryPrice, slotTimeUtc, triggerReason }) ?? "");
    }

    private static void SetPrivate<T>(StreamStateMachine sm, string name, T value)
    {
        var field = typeof(StreamStateMachine).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic);
        field?.SetValue(sm, value);
    }

    private static ParitySpec LoadSpec()
    {
        var root = ProjectRootResolver.ResolveProjectRoot();
        var configPath = Path.Combine(root, "configs", "analyzer_robot_parity.json");
        return ParitySpec.LoadFromFile(configPath);
    }
}
