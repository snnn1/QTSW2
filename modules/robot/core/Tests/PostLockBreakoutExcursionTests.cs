// Post-lock breakout excursion (bar path) and journal persistence.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test POST_LOCK_EXCURSION

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class PostLockBreakoutExcursionTests
{
    public static (bool Pass, string? Error) RunPostLockBreakoutExcursionTests()
    {
        var (p1, e1) = TestCleanBarPath_AllowsRecoverySubmit();
        if (!p1) return (false, e1);
        var (p2, e2) = TestBarTouchThenInsideRange_StillNoTrade();
        if (!p2) return (false, e2);
        var (p3, e3) = TestRestartHydration_PersistsNoTrade();
        if (!p3) return (false, e3);
        return (true, null);
    }

    /// <summary>Inside post-slot bars then recovery → submissions occur.</summary>
    private static (bool Pass, string? Error) TestCleanBarPath_AllowsRecoverySubmit()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PostLockClean_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = BreakoutValidityGateTests.CreateStreamWithPendingResubmit(tempRoot);
            if (sm == null) return (true, null);

            adapter.TestBid = 4495m;
            adapter.TestAsk = 4498m;
            sm.OnBar(sm.SlotTimeUtc.AddMinutes(1), 4496m, 4499m, 4495.5m, 4498m, DateTimeOffset.UtcNow, true);
            if (sm.Committed)
                return (false, "Clean path: unexpected early commit");

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
            var executed = sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);
            if (!executed)
                return (false, "Clean path: expected recovery to run submissions");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    /// <summary>First post-slot bar touches long; strict expiry — committed; reversal bar does not re-open setup.</summary>
    private static (bool Pass, string? Error) TestBarTouchThenInsideRange_StillNoTrade()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PostLockRev_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = BreakoutValidityGateTests.CreateStreamWithPendingResubmit(tempRoot);
            if (sm == null) return (true, null);

            adapter.TestBid = 4495m;
            adapter.TestAsk = 4496m;
            var t1 = sm.SlotTimeUtc.AddMinutes(1);
            sm.OnBar(t1, 4500m, 4501m, 4499m, 4500m, DateTimeOffset.UtcNow, true);
            if (!sm.Committed || sm.CommitReason != "NO_TRADE_BREAKOUT_ALREADY_OCCURRED")
                return (false, "Reversal test: expected commit on first touch");

            var t2 = sm.SlotTimeUtc.AddMinutes(2);
            sm.OnBar(t2, 4496m, 4499m, 4495.5m, 4497m, DateTimeOffset.UtcNow, true);

            var journals = new JournalStore(tempRoot);
            var j = journals.TryLoad("2026-06-03", "ES1");
            if (j == null || !j.PostLockLongBreakoutTouched)
                return (false, "Reversal test: journal should retain long touch flag");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    /// <summary>Journal hydrated with post-lock flags, RANGE_LOCKED restore — Arm completes NO_TRADE.</summary>
    private static (bool Pass, string? Error) TestRestartHydration_PersistsNoTrade()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"PostLockRestart_{Guid.NewGuid():N}");
        try
        {
            var tradingDateStr = "2026-06-03";
            var streamId = "ES1";
            var configsRobot = Path.Combine(tempRoot, "configs", "robot");
            Directory.CreateDirectory(configsRobot);
            File.WriteAllText(Path.Combine(configsRobot, "kill_switch.json"),
                System.Text.Json.JsonSerializer.Serialize(new { Enabled = false, Message = "post-lock test" }));

            var rangesDir = Path.Combine(tempRoot, "logs", "robot");
            Directory.CreateDirectory(rangesDir);
            var hydrationFile = Path.Combine(rangesDir, $"hydration_{tradingDateStr}.jsonl");
            var hydrationJson = "{\"event_type\":\"RANGE_LOCKED\",\"trading_day\":\"" + tradingDateStr + "\",\"stream_id\":\"" + streamId + "\",\"canonical_instrument\":\"ES\",\"execution_instrument\":\"ES\",\"session\":\"S1\",\"slot_time_chicago\":\"07:30\",\"timestamp_utc\":\"2026-06-03T12:30:00.0000000+00:00\",\"timestamp_chicago\":\"2026-06-03T07:30:00-05:00\",\"state\":\"RANGE_LOCKED\",\"data\":{\"range_high\":4500.25,\"range_low\":4495.00,\"freeze_close\":4500.00,\"breakout_long\":4501.00,\"breakout_short\":4494.00}}";
            File.AppendAllText(hydrationFile, hydrationJson + Environment.NewLine);

            var journals = new JournalStore(tempRoot);
            var journal = new StreamJournal
            {
                TradingDate = tradingDateStr,
                Stream = streamId,
                Committed = false,
                LastState = "RANGE_LOCKED",
                LastUpdateUtc = DateTimeOffset.UtcNow.ToString("o"),
                StopBracketsSubmittedAtLock = false,
                PostLockLongBreakoutTouched = true,
                PostLockShortBreakoutTouched = false,
                EntryDetected = false,
                SlotStatus = SlotStatus.ACTIVE,
                SlotInstanceKey = $"{streamId}_07:30_{tradingDateStr}",
                RecoveryAction = "ResubmitClean",
                RecoveryActionReason = "missing",
                RecoveryActionIssuedUtc = DateTimeOffset.UtcNow.ToString("o")
            };
            journals.Save(journal);

            var spec = OrderReconciliationRecoveryTests.LoadMinimalSpecForTests();
            var time = new TimeService(spec.timezone);
            var log = new RobotLogger(tempRoot);
            var adapter = new BreakoutValidityGateTests.MockPriceAdapter(log);
            var killSwitch = new KillSwitch(tempRoot, log);
            var riskGate = new RiskGate(spec, time, log, killSwitch, guard: null);
            var directive = new TimetableStream { stream = streamId, instrument = "ES", session = "S1", slot_time = "07:30", enabled = true, decision_time = "07:30" };

            var tradingDate = new DateOnly(2026, 6, 3);
            var sm = new StreamStateMachine(
                time, spec, log, journals, tradingDate, "hash", directive,
                ExecutionMode.DRYRUN, 1, 2, tempRoot,
                executionAdapter: adapter,
                riskGate: riskGate,
                executionJournal: new ExecutionJournal(tempRoot, log));

            if (sm.State != StreamState.DONE && sm.State != StreamState.RANGE_LOCKED)
                return (false, $"Restart: unexpected initial state {sm.State}");

            sm.Arm(DateTimeOffset.UtcNow);

            if (!sm.Committed || sm.CommitReason != "NO_TRADE_BREAKOUT_ALREADY_OCCURRED")
                return (false, $"Restart: expected commit NO_TRADE_BREAKOUT_ALREADY_OCCURRED, got committed={sm.Committed} reason={sm.CommitReason}");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }
}
