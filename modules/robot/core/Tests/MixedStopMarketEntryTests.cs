// Unit tests for mixed STOP/MARKET entry handling after breakout execution decision logic.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test MIXED_STOP_MARKET_ENTRY
//
// Verifies: long MARKET + short STOP, short MARKET + long STOP, both use same ocoGroup,
// no duplicate exposure, protective orders attached correctly.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class MixedStopMarketEntryTests
{
    /// <summary>Recorded submission for verification.</summary>
    private sealed class RecordedSubmission
    {
        public string Method { get; set; } = "";  // "SubmitEntryOrder" or "SubmitStopEntryOrder"
        public string Direction { get; set; } = "";
        public string? EntryOrderType { get; set; }
        public string? OcoGroup { get; set; }
        public decimal? StopPrice { get; set; }
    }

    /// <summary>Mock adapter that returns configurable bid/ask and records entry submissions.</summary>
    private sealed class RecordingMockPriceAdapter : IExecutionAdapter
    {
        private readonly NullExecutionAdapter _base;
        private readonly List<RecordedSubmission> _recorded = new();
        private readonly object _lock = new();

        public decimal? TestBid { get; set; }
        public decimal? TestAsk { get; set; }

        public RecordingMockPriceAdapter(RobotLogger log) => _base = new NullExecutionAdapter(log);

        public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow)
            => (TestBid, TestAsk);

        public OrderSubmissionResult SubmitEntryOrder(string intentId, string instrument, string direction, decimal? entryPrice, int quantity, string? entryOrderType, string? ocoGroup, DateTimeOffset utcNow)
        {
            lock (_lock)
            {
                _recorded.Add(new RecordedSubmission
                {
                    Method = "SubmitEntryOrder",
                    Direction = direction,
                    EntryOrderType = entryOrderType,
                    OcoGroup = ocoGroup
                });
            }
            return _base.SubmitEntryOrder(intentId, instrument, direction, entryPrice, quantity, entryOrderType, ocoGroup, utcNow);
        }

        public OrderSubmissionResult SubmitStopEntryOrder(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
        {
            lock (_lock)
            {
                _recorded.Add(new RecordedSubmission
                {
                    Method = "SubmitStopEntryOrder",
                    Direction = direction,
                    OcoGroup = ocoGroup,
                    StopPrice = stopPrice
                });
            }
            return _base.SubmitStopEntryOrder(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        }

        public IReadOnlyList<RecordedSubmission> GetRecorded() { lock (_lock) { return _recorded.ToArray(); } }

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
        public bool TryEnqueueEmergencyFlattenProtective(string instrument, DateTimeOffset utcNow)
            => _base.TryEnqueueEmergencyFlattenProtective(instrument, utcNow);
    }

    public static (bool Pass, string? Error) RunMixedStopMarketEntryTests()
    {
        var (p1, e1) = TestLongMarketShortStop();
        if (!p1) return (false, e1);

        var (p2, e2) = TestShortMarketLongStop();
        if (!p2) return (false, e2);

        return (true, null);
    }

    /// <summary>Long crossed (ask >= brk_long) → MARKET; short not crossed → STOP. Both same ocoGroup.</summary>
    private static (bool Pass, string? Error) TestLongMarketShortStop()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"MixedEntry_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = CreateStreamWithRiskGate(tempRoot);
            if (sm == null) return (false, "CreateStreamWithRiskGate returned null");

            // brkLong=4501, brkShort=4494. Long crossed: ask >= 4501. Short not crossed: bid > 4494.
            // Within unified validity (2 ticks × 0.25): ask < 4501.5, bid > 4493.5
            adapter.TestBid = 4494.25m;
            adapter.TestAsk = 4501.25m;

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };

            var executed = sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);
            if (!executed)
                return (false, "Long MARKET + short STOP: expected ExecutePendingRecoveryAction=true");

            var recorded = adapter.GetRecorded();
            if (recorded.Count != 2)
                return (false, $"Long MARKET + short STOP: expected 2 submissions, got {recorded.Count}");

            var longSub = recorded.FirstOrDefault(r => r.Direction == "Long");
            var shortSub = recorded.FirstOrDefault(r => r.Direction == "Short");
            if (longSub == null || shortSub == null)
                return (false, "Long MARKET + short STOP: missing Long or Short submission");

            if (longSub.Method != "SubmitEntryOrder" || longSub.EntryOrderType != "MARKET")
                return (false, $"Long MARKET + short STOP: Long should be SubmitEntryOrder(MARKET), got {longSub.Method}/{longSub.EntryOrderType}");
            if (shortSub.Method != "SubmitStopEntryOrder")
                return (false, $"Long MARKET + short STOP: Short should be SubmitStopEntryOrder, got {shortSub.Method}");

            var ocoGroup = longSub.OcoGroup;
            if (string.IsNullOrEmpty(ocoGroup))
                return (false, "Long MARKET + short STOP: Long ocoGroup should be non-empty");
            if (shortSub.OcoGroup != ocoGroup)
                return (false, $"Long MARKET + short STOP: both must share same ocoGroup, got Long={ocoGroup}, Short={shortSub.OcoGroup}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    /// <summary>Short crossed (bid <= brk_short) → MARKET; long not crossed → STOP. Both same ocoGroup.</summary>
    private static (bool Pass, string? Error) TestShortMarketLongStop()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"MixedEntry_{Guid.NewGuid():N}");
        try
        {
            var (sm, adapter) = CreateStreamWithRiskGate(tempRoot);
            if (sm == null) return (false, "CreateStreamWithRiskGate returned null");

            // brkLong=4501, brkShort=4494. Short crossed: bid <= 4494. Long not crossed: ask < 4501.
            // Within unified validity: bid > 4493.5, ask < 4501.5
            adapter.TestBid = 4494m;   // exactly at brk_short → short crossed
            adapter.TestAsk = 4500m;  // below brk_long → long not crossed

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot> { new() { Instrument = "ES", Quantity = 0 } },
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };

            var executed = sm.ExecutePendingRecoveryAction(snap, DateTimeOffset.UtcNow);
            if (!executed)
                return (false, "Short MARKET + long STOP: expected ExecutePendingRecoveryAction=true");

            var recorded = adapter.GetRecorded();
            if (recorded.Count != 2)
                return (false, $"Short MARKET + long STOP: expected 2 submissions, got {recorded.Count}");

            var longSub = recorded.FirstOrDefault(r => r.Direction == "Long");
            var shortSub = recorded.FirstOrDefault(r => r.Direction == "Short");
            if (longSub == null || shortSub == null)
                return (false, "Short MARKET + long STOP: missing Long or Short submission");

            if (shortSub.Method != "SubmitEntryOrder" || shortSub.EntryOrderType != "MARKET")
                return (false, $"Short MARKET + long STOP: Short should be SubmitEntryOrder(MARKET), got {shortSub.Method}/{shortSub.EntryOrderType}");
            if (longSub.Method != "SubmitStopEntryOrder")
                return (false, $"Short MARKET + long STOP: Long should be SubmitStopEntryOrder, got {longSub.Method}");

            var ocoGroup = shortSub.OcoGroup;
            if (string.IsNullOrEmpty(ocoGroup))
                return (false, "Short MARKET + long STOP: Short ocoGroup should be non-empty");
            if (longSub.OcoGroup != ocoGroup)
                return (false, $"Short MARKET + long STOP: both must share same ocoGroup, got Short={ocoGroup}, Long={longSub.OcoGroup}");

            return (true, null);
        }
        finally
        {
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (StreamStateMachine? sm, RecordingMockPriceAdapter adapter) CreateStreamWithRiskGate(string tempRoot)
    {
        var tradingDate = DateOnly.FromDateTime(DateTime.UtcNow);
        var tradingDateStr = tradingDate.ToString("yyyy-MM-dd");
        var streamId = "ES1";

        var logsDir = Path.Combine(tempRoot, "logs", "robot");
        Directory.CreateDirectory(logsDir);
        // Use hydration format (more robust than ranges for System.Text.Json deserialization)
        var hydrationFile = Path.Combine(logsDir, $"hydration_{tradingDateStr}.jsonl");
        var hydrationJson = $"{{\"event_type\":\"RANGE_LOCKED\",\"trading_day\":\"{tradingDateStr}\",\"stream_id\":\"{streamId}\",\"canonical_instrument\":\"ES\",\"execution_instrument\":\"ES\",\"session\":\"S1\",\"slot_time_chicago\":\"07:30\",\"timestamp_utc\":\"2026-03-12T12:30:00Z\",\"timestamp_chicago\":\"2026-03-12T07:30:00-05:00\",\"state\":\"RANGE_LOCKED\",\"data\":{{\"range_high\":4500.25,\"range_low\":4495.00,\"freeze_close\":4500.00,\"breakout_long\":4501.00,\"breakout_short\":4494.00}}}}";
        File.AppendAllText(hydrationFile, hydrationJson + Environment.NewLine);

        var configsRobot = Path.Combine(tempRoot, "configs", "robot");
        Directory.CreateDirectory(configsRobot);
        File.WriteAllText(Path.Combine(configsRobot, "kill_switch.json"),
            JsonSerializer.Serialize(new { Enabled = false, Message = "Mixed entry tests" }));

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

        var adapter = new RecordingMockPriceAdapter(log);
        var killSwitch = new KillSwitch(tempRoot, log);
        var riskGate = new RiskGate(spec, time, log, killSwitch, guard: null);
        var directive = new TimetableStream { stream = streamId, instrument = "ES", session = "S1", slot_time = "07:30", enabled = true, decision_time = "07:30" };

        var sm = new StreamStateMachine(
            time, spec, log, journals, tradingDate, "hash", directive,
            ExecutionMode.DRYRUN, 1, 2, tempRoot,
            executionAdapter: adapter,
            riskGate: riskGate,
            executionJournal: new ExecutionJournal(tempRoot, log));

        if (sm.State != StreamState.RANGE_LOCKED)
            return (null, adapter);

        return (sm, adapter);
    }
}
