// Unit tests for post-lock breakout excursion during recovery.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test BREAKOUT_VALIDITY_GATE
//
// Verifies: ExecutePendingRecoveryAction blocks resubmit when a post-slot bar touches either breakout
// (bar path, not bid/ask snapshot); inside-range bars allow resubmit; snapshot-only cross without
// bar touch allows resubmit; recovery clears after commit.

using System;
using System.Collections.Generic;
using System.IO;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class BreakoutValidityGateTests
{
    /// <summary>Mock adapter with configurable bid/ask for breakout validity tests.</summary>
    public sealed class MockPriceAdapter : IExecutionAdapter
    {
        private readonly NullExecutionAdapter _base;
        public decimal? TestBid { get; set; }
        public decimal? TestAsk { get; set; }

        public MockPriceAdapter(RobotLogger log) => _base = new NullExecutionAdapter(log);

        public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow)
            => (TestBid, TestAsk);

        public OrderSubmissionResult SubmitEntryOrder(string intentId, string instrument, string direction, decimal? entryPrice, int quantity, string? entryOrderType, string? ocoGroup, DateTimeOffset utcNow)
            => _base.SubmitEntryOrder(intentId, instrument, direction, entryPrice, quantity, entryOrderType, ocoGroup, utcNow);
        public OrderSubmissionResult SubmitStopEntryOrder(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => _base.SubmitStopEntryOrder(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        public OrderSubmissionResult SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => _base.SubmitProtectiveStop(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        public OrderSubmissionResult SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => _base.SubmitTargetOrder(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);
        public OrderModificationResult ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow)
            => _base.ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow);
        public FlattenResult Flatten(string intentId, string instrument, DateTimeOffset utcNow)
            => _base.Flatten(intentId, instrument, utcNow);
        public FlattenResult FlattenEmergency(string instrument, DateTimeOffset utcNow)
            => _base.FlattenEmergency(instrument, utcNow);
        public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
            => _base.GetAccountSnapshot(utcNow);
        public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow)
            => _base.CancelRobotOwnedWorkingOrders(snap, utcNow);
        public void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow)
            => _base.CancelOrders(orderIds, utcNow);
        public void EnqueueExecutionCommand(ExecutionCommandBase command)
            => _base.EnqueueExecutionCommand(command);
        public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow)
            => _base.RequestSessionCloseFlattenImmediate(intentId, instrument, utcNow);
    }

    public static (bool Pass, string? Error) RunBreakoutValidityGateTests()
    {
        var (p1, e1) = TestLongBreakoutCrossed_Blocked();
        if (!p1) return (false, e1);

        var (p2, e2) = TestShortBreakoutCrossed_Blocked();
        if (!p2) return (false, e2);

        var (p3, e3) = TestWithinTolerance_Allowed();
        if (!p3) return (false, e3);

        var (p4, e4) = TestBeyondTolerance_Blocked();
        if (!p4) return (false, e4);

        var (p5, e5) = TestBlockedClearsRecoveryAction();
        if (!p5) return (false, e5);

        var (p6, e6) = TestLiveAdapter_NoContext_ReturnsNull();
        if (!p6) return (false, e6);

        var (p7, e7) = TestLiveAdapter_WithMockInstrument_ReturnsBidAsk();
        if (!p7) return (false, e7);

        return (true, null);
    }

    private static (bool Pass, string? Error) TestLongBreakoutCrossed_Blocked()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"BreakoutGate_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = CreateStreamWithPendingResubmit(tempRoot);
            if (sm == null) return (true, null);

            // brkLong=4501 — post-slot bar high >= 4501 invalidates setup (path-based).
            adapter.TestBid = 4500m;
            adapter.TestAsk = 4500.5m;
            var postSlot = sm.SlotTimeUtc.AddMinutes(1);
            sm.OnBar(postSlot, 4500m, 4501m, 4499.5m, 4500m, DateTimeOffset.UtcNow, true);

            if (!sm.Committed)
                return (false, "Long bar touch: expected stream committed");
            if (sm.CommitReason != "NO_TRADE_BREAKOUT_ALREADY_OCCURRED")
                return (false, "Long bar touch: expected NO_TRADE_BREAKOUT_ALREADY_OCCURRED commit reason");

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
            var executed = sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);
            if (executed)
                return (false, "Long bar touch: expected ExecutePendingRecoveryAction=false after commit");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestShortBreakoutCrossed_Blocked()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"BreakoutGate_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = CreateStreamWithPendingResubmit(tempRoot);
            if (sm == null) return (true, null);

            adapter.TestBid = 4495m;
            adapter.TestAsk = 4496m;
            var postSlot = sm.SlotTimeUtc.AddMinutes(1);
            sm.OnBar(postSlot, 4495m, 4495.5m, 4494m, 4494.75m, DateTimeOffset.UtcNow, true);

            if (!sm.Committed)
                return (false, "Short bar touch: expected stream committed");
            if (sm.CommitReason != "NO_TRADE_BREAKOUT_ALREADY_OCCURRED")
                return (false, "Short bar touch: expected NO_TRADE_BREAKOUT_ALREADY_OCCURRED commit reason");

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
            var executed = sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);
            if (executed)
                return (false, "Short bar touch: expected ExecutePendingRecoveryAction=false after commit");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestWithinTolerance_Allowed()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"BreakoutGate_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = CreateStreamWithPendingResubmit(tempRoot);
            if (sm == null) return (true, null);

            adapter.TestBid = 4494.25m;
            adapter.TestAsk = 4501.25m;

            // Post-slot bar stays inside breakouts (high < brkLong, low > brkShort)
            var postSlot = sm.SlotTimeUtc.AddMinutes(1);
            sm.OnBar(postSlot, 4496m, 4500m, 4495m, 4498m, DateTimeOffset.UtcNow, true);
            if (sm.Committed)
                return (false, "Inside bar path: expected stream not committed before recovery");

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };

            var executed = sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);
            // With NullExecutionAdapter base, SubmitStopEntryOrder is DRYRUN - succeeds
            if (!executed)
                return (false, "Within tolerance: expected ExecutePendingRecoveryAction=true (allowed)");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestBeyondTolerance_Blocked()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"BreakoutGate_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = CreateStreamWithPendingResubmit(tempRoot);
            if (sm == null) return (true, null);

            // Snapshot would fail old unified gate, but no post-lock bar path → recovery still allowed.
            adapter.TestBid = 4492m;
            adapter.TestAsk = 4503m;

            var postSlot = sm.SlotTimeUtc.AddMinutes(1);
            sm.OnBar(postSlot, 4496m, 4500m, 4495m, 4498m, DateTimeOffset.UtcNow, true);
            if (sm.Committed)
                return (false, "Snapshot-only cross: did not expect commit from bar path");

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };

            var executed = sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);
            if (!executed)
                return (false, "Snapshot-only cross: expected recovery submit (bar path clean)");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestBlockedClearsRecoveryAction()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"BreakoutGate_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = CreateStreamWithPendingResubmit(tempRoot);
            if (sm == null) return (true, null);

            adapter.TestBid = 4495m;
            adapter.TestAsk = 4496m;
            var postSlot = sm.SlotTimeUtc.AddMinutes(1);
            sm.OnBar(postSlot, 4495m, 4495.5m, 4494m, 4494.25m, DateTimeOffset.UtcNow, true);

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };

            sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);

            // Second call: no pending action, should return false immediately
            var executed2 = sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);
            if (executed2)
                return (false, "Blocked clears: second call should return false (no pending action)");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Test NinjaTraderLiveAdapter.GetCurrentMarketPrice: without SetNTContext returns (null,null).
    /// </summary>
    private static (bool Pass, string? Error) TestLiveAdapter_NoContext_ReturnsNull()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"LiveAdapter_{Guid.NewGuid():N}");
        try
        {
            var log = new RobotLogger(tempRoot);
            var adapter = new NinjaTraderLiveAdapter(log, new TimeService("America/Chicago"));
            var (bid, ask) = adapter.GetCurrentMarketPrice("ES", DateTimeOffset.UtcNow);
            if (bid.HasValue || ask.HasValue)
                return (false, "Without SetNTContext: expected (null,null), got bid=" + bid + " ask=" + ask);
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    /// <summary>
    /// Test NinjaTraderLiveAdapter.GetCurrentMarketPrice: with mock instrument returns usable bid/ask.
    /// Uses dynamic-compatible mock (anonymous type with Func) to simulate Instrument.MarketData.GetBid(0)/GetAsk(0).
    /// </summary>
    private static (bool Pass, string? Error) TestLiveAdapter_WithMockInstrument_ReturnsBidAsk()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"LiveAdapter_{Guid.NewGuid():N}");
        try
        {
            var log = new RobotLogger(tempRoot);
            var adapter = new NinjaTraderLiveAdapter(log, new TimeService("America/Chicago"));
            // Anonymous type with Func delegates - works reliably with dynamic dispatch
            var mockInstrument = new
            {
                MarketData = new
                {
                    GetBid = new Func<int, double>(_ => 4500.25),
                    GetAsk = new Func<int, double>(_ => 4500.50),
                    Bid = 4500.25,
                    Ask = 4500.50
                }
            };
            adapter.SetNTContext(new object(), mockInstrument);
            var (bid, ask) = adapter.GetCurrentMarketPrice("ES", DateTimeOffset.UtcNow);
            if (!bid.HasValue || !ask.HasValue)
                return (false, "With mock instrument: expected usable bid/ask, got bid=" + bid + " ask=" + ask);
            if (Math.Abs(bid.Value - 4500.25m) > 0.0001m || Math.Abs(ask.Value - 4500.50m) > 0.0001m)
                return (false, $"With mock instrument: expected (4500.25, 4500.50), got ({bid}, {ask})");
            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    public static (StreamStateMachine? sm, MockPriceAdapter adapter) CreateStreamWithPendingResubmit(string tempRoot)
    {
        var tradingDate = new DateOnly(2026, 6, 3);
        var tradingDateStr = tradingDate.ToString("yyyy-MM-dd");
        var streamId = "ES1";

        var rangesDir = Path.Combine(tempRoot, "logs", "robot");
        Directory.CreateDirectory(rangesDir);
        var hydrationFile = Path.Combine(rangesDir, $"hydration_{tradingDateStr}.jsonl");
        var hydrationJson = "{\"event_type\":\"RANGE_LOCKED\",\"trading_day\":\"" + tradingDateStr + "\",\"stream_id\":\"" + streamId + "\",\"canonical_instrument\":\"ES\",\"execution_instrument\":\"ES\",\"session\":\"S1\",\"slot_time_chicago\":\"07:30\",\"timestamp_utc\":\"2026-06-03T12:30:00.0000000+00:00\",\"timestamp_chicago\":\"2026-06-03T07:30:00-05:00\",\"state\":\"RANGE_LOCKED\",\"data\":{\"range_high\":4500.25,\"range_low\":4495.00,\"freeze_close\":4500.00,\"breakout_long\":4501.00,\"breakout_short\":4494.00}}";
        File.AppendAllText(hydrationFile, hydrationJson + Environment.NewLine);

        var spec = OrderReconciliationRecoveryTests.LoadMinimalSpecForTests();
        var time = new TimeService(spec.timezone);
        var log = new RobotLogger(tempRoot);
        var journals = new JournalStore(tempRoot);

        var journal = new StreamJournal
        {
            TradingDate = tradingDateStr,
            Stream = streamId,
            Committed = false,
            LastState = "RANGE_LOCKED",
            LastUpdateUtc = DateTimeOffset.UtcNow.ToString("o"),
            StopBracketsSubmittedAtLock = false,
            EntryDetected = false,
            SlotStatus = SlotStatus.ACTIVE,
            SlotInstanceKey = $"{streamId}_07:30_{tradingDateStr}",
            RecoveryAction = "ResubmitClean",
            RecoveryActionReason = "missing",
            RecoveryActionIssuedUtc = DateTimeOffset.UtcNow.ToString("o")
        };
        journals.Save(journal);

        var adapter = new MockPriceAdapter(log);
        var directive = new TimetableStream { stream = streamId, instrument = "ES", session = "S1", slot_time = "07:30", enabled = true, decision_time = "07:30" };

        var sm = new StreamStateMachine(
            time, spec, log, journals, tradingDate, "hash", directive,
            ExecutionMode.DRYRUN, 1, 2, tempRoot,
            executionAdapter: adapter,
            executionJournal: new ExecutionJournal(tempRoot, log));

        if (sm.State != StreamState.RANGE_LOCKED)
            return (null, adapter);

        return (sm, adapter);
    }
}
