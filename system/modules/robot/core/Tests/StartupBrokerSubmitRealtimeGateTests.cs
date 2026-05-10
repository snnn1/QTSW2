// Startup broker-submit gate tests.
// Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test STARTUP_BROKER_SUBMIT_GATE

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class StartupBrokerSubmitRealtimeGateTests
{
    public static (bool Pass, string? Error) RunAll()
    {
        var beforeSlot = TestPendingRecoveryDefersBeforeSlotTime();
        if (!beforeSlot.Pass)
            return beforeSlot;

        return TestRealtimeGateBlocksThenAllowsAtOrAfterSlot();
    }

    private static (bool Pass, string? Error) TestRealtimeGateBlocksThenAllowsAtOrAfterSlot()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"StartupBrokerGate_{Guid.NewGuid():N}");
        var previousScenario = Environment.GetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName, null);
            Directory.CreateDirectory(tempRoot);
            var engine = new RobotEngine(
                tempRoot,
                TimeSpan.FromSeconds(60),
                ExecutionMode.SIM,
                customLogDir: null,
                customTimetablePath: null,
                instrument: "ES",
                masterInstrumentName: "ES",
                ignoreExistingStreamJournals: false,
                playbackAccountDetected: false,
                useAsyncLogging: false);

            var adapter = new RecordingAdapter(new RobotLogger(tempRoot));
            var sm = CreateRangeLockedStreamPendingResubmit(tempRoot, engine, adapter);
            if (sm == null)
                return (false, "failed to construct RANGE_LOCKED stream");

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
            var utc = sm.MarketCloseUtc.AddHours(-2);

            sm.ExecutePendingRecoveryAction(snap, utc);
            if (adapter.TotalEntrySubmits != 0)
                return (false, $"expected 0 entry submits before Realtime gate, got {adapter.TotalEntrySubmits}");

            if (!LogsUnderRootContain(tempRoot, "BROKER_SUBMIT_NOT_REALTIME"))
                return (false, "expected BROKER_SUBMIT_NOT_REALTIME early-return evidence");

            engine.LogEngineEvent(utc, "REALTIME_STATE_REACHED", new Dictionary<string, object>
            {
                ["engine_ready"] = true,
                ["init_failed"] = false
            });
            if (!LogsUnderRootContain(tempRoot, "BROKER_ORDER_SUBMISSION_ALLOWED"))
                return (false, "expected REALTIME_STATE_REACHED to open broker-submit gate");

            sm.ExecutePendingRecoveryAction(snap, utc);
            if (adapter.TotalEntrySubmits != 2)
                return (false, $"expected paired entry submits after Realtime gate, got {adapter.TotalEntrySubmits}");

            return (true, null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName, previousScenario);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static (bool Pass, string? Error) TestPendingRecoveryDefersBeforeSlotTime()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), $"StartupBrokerGateBeforeSlot_{Guid.NewGuid():N}");
        var previousScenario = Environment.GetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName);
        try
        {
            Environment.SetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName, null);
            Directory.CreateDirectory(tempRoot);
            var engine = new RobotEngine(
                tempRoot,
                TimeSpan.FromSeconds(60),
                ExecutionMode.SIM,
                customLogDir: null,
                customTimetablePath: null,
                instrument: "ES",
                masterInstrumentName: "ES",
                ignoreExistingStreamJournals: false,
                playbackAccountDetected: false,
                useAsyncLogging: false);

            var adapter = new RecordingAdapter(new RobotLogger(tempRoot));
            var sm = CreateRangeLockedStreamPendingResubmit(tempRoot, engine, adapter);
            if (sm == null)
                return (false, "before-slot: failed to construct RANGE_LOCKED stream");

            var beforeSlotUtc = sm.SlotTimeUtc.AddMinutes(-30);
            engine.LogEngineEvent(beforeSlotUtc, "REALTIME_STATE_REACHED", new Dictionary<string, object>
            {
                ["engine_ready"] = true,
                ["init_failed"] = false
            });

            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };

            sm.ExecutePendingRecoveryAction(snap, beforeSlotUtc);
            if (adapter.TotalEntrySubmits != 0)
                return (false, $"before-slot: expected 0 entry submits, got {adapter.TotalEntrySubmits}");

            if (!LogsUnderRootContain(tempRoot, "ENTRY_ORDER_ACTION_DEFERRED_BEFORE_SLOT_TIME"))
                return (false, "before-slot: expected deferred-before-slot evidence");

            var afterSlotUtc = sm.SlotTimeUtc.AddMinutes(1);
            sm.ExecutePendingRecoveryAction(snap, afterSlotUtc);
            if (adapter.TotalEntrySubmits != 2)
                return (false, $"after-slot: expected paired entry submits, got {adapter.TotalEntrySubmits}");

            return (true, null);
        }
        finally
        {
            Environment.SetEnvironmentVariable(PlaybackScenarioManifest.EnvVarName, previousScenario);
            try { Directory.Delete(tempRoot, recursive: true); } catch { }
        }
    }

    private static StreamStateMachine? CreateRangeLockedStreamPendingResubmit(
        string tempRoot,
        RobotEngine engine,
        RecordingAdapter adapter)
    {
        var tradingDate = new DateOnly(2026, 6, 3);
        var tradingDateStr = tradingDate.ToString("yyyy-MM-dd");
        const string streamId = "ES1";

        var logsDir = Path.Combine(tempRoot, "logs", "robot");
        Directory.CreateDirectory(logsDir);
        var hydrationFile = Path.Combine(logsDir, $"hydration_{tradingDateStr}.jsonl");
        var hydrationJson =
            $"{{\"event_type\":\"RANGE_LOCKED\",\"trading_day\":\"{tradingDateStr}\",\"stream_id\":\"{streamId}\",\"canonical_instrument\":\"ES\",\"execution_instrument\":\"ES\",\"session\":\"S1\",\"slot_time_chicago\":\"07:30\",\"timestamp_utc\":\"{tradingDateStr}T12:30:00Z\",\"timestamp_chicago\":\"{tradingDateStr}T07:30:00-05:00\",\"state\":\"RANGE_LOCKED\",\"data\":{{\"range_high\":4500.25,\"range_low\":4495.00,\"freeze_close\":4500.00,\"breakout_long\":4501.00,\"breakout_short\":4494.00}}}}";
        File.AppendAllText(hydrationFile, hydrationJson + Environment.NewLine);

        var configsRobot = Path.Combine(tempRoot, "configs", "robot");
        Directory.CreateDirectory(configsRobot);
        File.WriteAllText(Path.Combine(configsRobot, "kill_switch.json"),
            JsonSerializer.Serialize(new { Enabled = false, Message = "Startup broker submit gate test" }));

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
            RecoveryActionReason = "startup_gate_test",
            RecoveryActionIssuedUtc = DateTimeOffset.UtcNow.ToString("o")
        };
        journals.Save(journal);

        var killSwitch = new KillSwitch(tempRoot, log);
        var riskGate = new RiskGate(spec, time, log, killSwitch, guard: null);
        var directive = new TimetableStream
        {
            stream = streamId,
            instrument = "ES",
            session = "S1",
            slot_time = "07:30",
            enabled = true,
            decision_time = "07:30"
        };

        var sm = new StreamStateMachine(
            time,
            spec,
            log,
            journals,
            tradingDate,
            "hash",
            directive,
            ExecutionMode.SIM,
            1,
            2,
            tempRoot,
            tempRoot,
            ignoreExistingStreamJournals: false,
            executionAdapter: adapter,
            riskGate: riskGate,
            executionJournal: new ExecutionJournal(tempRoot, log),
            engine: engine);

        return sm.State == StreamState.RANGE_LOCKED ? sm : null;
    }

    private static bool LogsUnderRootContain(string projectRoot, string substring)
    {
        var logsRobot = Path.Combine(projectRoot, "logs", "robot");
        if (!Directory.Exists(logsRobot))
            return false;
        foreach (var f in Directory.GetFiles(logsRobot, "*.jsonl", SearchOption.AllDirectories))
        {
            try
            {
                if (File.ReadAllText(f).IndexOf(substring, StringComparison.Ordinal) >= 0)
                    return true;
            }
            catch
            {
                // best-effort test helper
            }
        }
        return false;
    }

    private sealed class RecordingAdapter : IExecutionAdapter
    {
        private readonly NullExecutionAdapter _inner;

        public RecordingAdapter(RobotLogger log) => _inner = new NullExecutionAdapter(log);

        public int TotalEntrySubmits { get; private set; }

        public bool IsExecutionContextReady => true;

        public OrderSubmissionResult SubmitEntryOrder(string intentId, string instrument, string direction, decimal? entryPrice, int quantity, string? entryOrderType, string? ocoGroup, DateTimeOffset utcNow)
        {
            TotalEntrySubmits++;
            return _inner.SubmitEntryOrder(intentId, instrument, direction, entryPrice, quantity, entryOrderType, ocoGroup, utcNow);
        }

        public OrderSubmissionResult SubmitStopEntryOrder(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
        {
            TotalEntrySubmits++;
            return _inner.SubmitStopEntryOrder(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        }

        public OrderSubmissionResult SubmitProtectiveStop(string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => _inner.SubmitProtectiveStop(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);

        public OrderSubmissionResult SubmitTargetOrder(string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow)
            => _inner.SubmitTargetOrder(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);

        public OrderModificationResult ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow)
            => _inner.ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow);

        public FlattenResult Flatten(string intentId, string instrument, DateTimeOffset utcNow)
            => _inner.Flatten(intentId, instrument, utcNow);

        public FlattenResult FlattenEmergency(string instrument, DateTimeOffset utcNow)
            => _inner.FlattenEmergency(instrument, utcNow);

        public bool TryEnqueueEmergencyFlattenProtective(string instrument, DateTimeOffset utcNow)
            => _inner.TryEnqueueEmergencyFlattenProtective(instrument, utcNow);

        public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
            => _inner.GetAccountSnapshot(utcNow);

        public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow)
            => _inner.GetCurrentMarketPrice(instrument, utcNow);

        public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow)
            => _inner.CancelRobotOwnedWorkingOrders(snap, utcNow);

        public void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow)
            => _inner.CancelOrders(orderIds, utcNow);

        public void EnqueueExecutionCommand(ExecutionCommandBase command)
            => _inner.EnqueueExecutionCommand(command);

        public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow)
            => _inner.RequestSessionCloseFlattenImmediate(intentId, instrument, utcNow);
    }
}
