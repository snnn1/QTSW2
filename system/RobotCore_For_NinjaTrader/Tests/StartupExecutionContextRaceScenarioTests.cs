// Startup race: BarsRequest / recovery must not reach broker stop-entry submission before NT context + SIM verify
// (same contract as DATALOADED_WIRE_DONE + execution-context readiness). Proves StreamStateMachine gates real submission.
// Run: dotnet run --project RobotCore_For_NinjaTrader/SiblingProtectiveCancelQueue.Test/SiblingProtectiveCancelQueue.Test.csproj -- STARTUP_EXEC_CONTEXT_RACE

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Execution;

namespace QTSW2.Robot.Core.Tests;

public static class StartupExecutionContextRaceScenarioTests
{
    /// <summary>
    /// Simulates "strategy queued recovery / worker tick" before DataLoaded wiring: <see cref="IExecutionAdapter.IsExecutionContextReady"/> false.
    /// Then flips readiness (post-DATALOADED_WIRE_DONE) and proves stop-entry brackets actually submit.
    /// </summary>
    public static bool RunAll(Action<string>? log = null)
    {
        if (!RunSanityBracketSubmitWhenContextReady(log))
            return false;

        var tmp = Path.Combine(Path.GetTempPath(), "startup_race_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try
        {
            var (sm, gated) = CreateRangeLockedStreamPendingResubmit(tmp);
            if (sm == null || gated == null)
            {
                log?.Invoke("FAIL: could not construct RANGE_LOCKED stream with ResubmitClean recovery");
                return false;
            }

            gated.SimulatedDataLoadedWireDone = false;
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
            // Must be strictly before MarketCloseUtc (ExecutePendingRecoveryAction eligibility) and consistent across both attempts.
            var utc = sm.MarketCloseUtc.AddHours(-2);

            sm.ExecutePendingRecoveryAction(snap, utc);
            if (gated.TotalBracketPathSubmits != 0)
            {
                log?.Invoke($"FAIL: before readiness, expected 0 broker entry submits (STOP or MARKET), got {gated.TotalBracketPathSubmits}");
                return false;
            }

            if (!LogsUnderRootContain(tmp, "EXECUTION_CONTEXT_NOT_READY"))
            {
                log?.Invoke("FAIL: expected STOP_BRACKETS early return with reason EXECUTION_CONTEXT_NOT_READY in stream logs");
                return false;
            }

            gated.SimulatedDataLoadedWireDone = true;
            sm.ExecutePendingRecoveryAction(snap, utc);
            if (gated.TotalBracketPathSubmits != 2)
            {
                log?.Invoke($"FAIL: after readiness, expected 2 broker entry submits (long+short, STOP and/or MARKET per gate), got {gated.TotalBracketPathSubmits} (stop={gated.StopEntrySubmitCount}, market={gated.MarketEntrySubmitCount})");
                return false;
            }

            log?.Invoke("PASS: stream cannot effect stop-entry brackets until execution context ready; submits after wire");
            return true;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
    }

    /// <summary>
    /// Control: same stream + recovery, but readiness true from the start — proves setup can reach paired entry submission.
    /// </summary>
    private static bool RunSanityBracketSubmitWhenContextReady(Action<string>? log = null)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "startup_race_sanity_" + Guid.NewGuid().ToString("N").Substring(0, 8));
        Directory.CreateDirectory(tmp);
        try
        {
            var (sm, gated) = CreateRangeLockedStreamPendingResubmit(tmp);
            if (sm == null || gated == null)
            {
                log?.Invoke("SANITY FAIL: could not construct stream");
                return false;
            }

            gated.SimulatedDataLoadedWireDone = true;
            var snap = new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>()
            };
            var utc = sm.MarketCloseUtc.AddHours(-2);
            sm.ExecutePendingRecoveryAction(snap, utc);
            if (gated.TotalBracketPathSubmits != 2)
            {
                log?.Invoke($"SANITY FAIL: expected 2 entry submits when ready from start, got {gated.TotalBracketPathSubmits} (stop={gated.StopEntrySubmitCount}, mkt={gated.MarketEntrySubmitCount})");
                return false;
            }

            log?.Invoke("SANITY OK: bracket path reaches adapter when execution context ready");
            return true;
        }
        finally
        {
            try { Directory.Delete(tmp, recursive: true); } catch { /* best-effort */ }
        }
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
                var text = File.ReadAllText(f);
                if (text.IndexOf(substring, StringComparison.Ordinal) >= 0)
                    return true;
            }
            catch
            {
                // ignore
            }
        }
        return false;
    }

    private static (StreamStateMachine? sm, GatedExecutionContextAdapter? adapter) CreateRangeLockedStreamPendingResubmit(string tempRoot)
    {
        // Align with MixedStopMarketEntryTests: today's date avoids fixed-past-day vs MarketCloseUtc edge cases.
        var tradingDate = DateOnly.FromDateTime(DateTime.UtcNow);
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
            JsonSerializer.Serialize(new { Enabled = false, Message = "Startup race scenario" }));

        var spec = LoadMinimalParitySpec();
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

        var reloaded = journals.TryLoad(tradingDateStr, streamId);
        if (reloaded == null || !string.Equals(reloaded.RecoveryAction, "ResubmitClean", StringComparison.OrdinalIgnoreCase))
        {
            return (null, null);
        }

        var gated = new GatedExecutionContextAdapter(log);
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
            time, spec, log, journals, tradingDate, "hash", directive,
            ExecutionMode.DRYRUN, 1, 2, tempRoot, tempRoot,
            executionAdapter: gated,
            riskGate: riskGate,
            executionJournal: new ExecutionJournal(tempRoot, log));

        if (sm.State != StreamState.RANGE_LOCKED)
            return (null, null);

        return (sm, gated);
    }

    private static ParitySpec LoadMinimalParitySpec()
    {
        var inst = new ParityInstrument { tick_size = 0.25m, base_target = 4.0m };
        var instruments = new Dictionary<string, ParityInstrument> { ["ES"] = inst };
        // slot_end_times must list the stream's slot_time_chicago ("07:30") or RiskGate blocks (SLOT_TIME_NOT_ALLOWED).
        var sessions = new Dictionary<string, ParitySession>
        {
            ["S1"] = new ParitySession { range_start_time = "07:00", slot_end_times = new List<string> { "07:30", "16:00" } },
            ["S2"] = new ParitySession { range_start_time = "07:00", slot_end_times = new List<string> { "07:30", "16:00" } }
        };
        return new ParitySpec
        {
            spec_name = "analyzer_robot_parity",
            spec_revision = "test",
            timezone = "America/Chicago",
            instruments = instruments,
            sessions = sessions,
            entry_cutoff = new EntryCutoff { type = "MARKET_CLOSE", market_close_time = "16:00" },
            breakout = new BreakoutSpec { offset_ticks = 1, tick_rounding = new TickRounding { method = "utility_round_to_tick", definition = "test" } },
            timetable_validation = new TimetableValidation()
        };
    }

    /// <summary>
    /// Delegates to <see cref="NullExecutionAdapter"/> but gates <see cref="IExecutionAdapter.IsExecutionContextReady"/>
    /// to simulate pre- / post- DataLoaded wiring (DATALOADED_WIRE_DONE).
    /// </summary>
    private sealed class GatedExecutionContextAdapter : IExecutionAdapter
    {
        private readonly NullExecutionAdapter _inner;

        public GatedExecutionContextAdapter(RobotLogger log) => _inner = new NullExecutionAdapter(log);

        /// <summary>When true, matches adapter readiness after WireNTContextToAdapter + SIM verify.</summary>
        public bool SimulatedDataLoadedWireDone { get; set; }

        public int StopEntrySubmitCount { get; private set; }
        public int MarketEntrySubmitCount { get; private set; }

        /// <summary>Long+short entry attempts at RANGE_LOCKED (SubmitStopEntryOrder and/or SubmitEntryOrder for MARKET path).</summary>
        public int TotalBracketPathSubmits => StopEntrySubmitCount + MarketEntrySubmitCount;

        public bool IsExecutionContextReady => SimulatedDataLoadedWireDone;

        public OrderSubmissionResult SubmitStopEntryOrder(
            string intentId,
            string instrument,
            string direction,
            decimal stopPrice,
            int quantity,
            string? ocoGroup,
            DateTimeOffset utcNow)
        {
            StopEntrySubmitCount++;
            return _inner.SubmitStopEntryOrder(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        }

        public OrderSubmissionResult SubmitEntryOrder(
            string intentId, string instrument, string direction, decimal? entryPrice, int quantity, string? entryOrderType, string? ocoGroup,
            DateTimeOffset utcNow)
        {
            MarketEntrySubmitCount++;
            return _inner.SubmitEntryOrder(intentId, instrument, direction, entryPrice, quantity, entryOrderType, ocoGroup, utcNow);
        }

        public OrderSubmissionResult SubmitProtectiveStop(
            string intentId, string instrument, string direction, decimal stopPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) =>
            _inner.SubmitProtectiveStop(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);

        public OrderSubmissionResult SubmitTargetOrder(
            string intentId, string instrument, string direction, decimal targetPrice, int quantity, string? ocoGroup, DateTimeOffset utcNow) =>
            _inner.SubmitTargetOrder(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);

        public OrderModificationResult ModifyStopToBreakEven(string intentId, string instrument, decimal beStopPrice, DateTimeOffset utcNow) =>
            _inner.ModifyStopToBreakEven(intentId, instrument, beStopPrice, utcNow);

        public FlattenResult Flatten(string intentId, string instrument, DateTimeOffset utcNow) =>
            _inner.Flatten(intentId, instrument, utcNow);

        public FlattenResult FlattenEmergency(string instrument, DateTimeOffset utcNow) => _inner.FlattenEmergency(instrument, utcNow);

        public bool TryEnqueueEmergencyFlattenProtective(string instrument, DateTimeOffset utcNow) =>
            _inner.TryEnqueueEmergencyFlattenProtective(instrument, utcNow);

        public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow) => _inner.GetAccountSnapshot(utcNow);

        public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow) =>
            _inner.GetCurrentMarketPrice(instrument, utcNow);

        public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow) =>
            _inner.CancelRobotOwnedWorkingOrders(snap, utcNow);

        public void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow) => _inner.CancelOrders(orderIds, utcNow);

        public void EvaluateBreakEven(decimal tickPrice, DateTimeOffset? tickTimeFromEvent, string executionInstrument) =>
            _inner.EvaluateBreakEven(tickPrice, tickTimeFromEvent, executionInstrument);

        public void ProcessPendingUnresolvedExecutions() => _inner.ProcessPendingUnresolvedExecutions();

        public void RequestRecoveryForInstrument(string instrument, string reason, object context, DateTimeOffset utcNow) =>
            _inner.RequestRecoveryForInstrument(instrument, reason, context, utcNow);

        public void RequestSupervisoryActionForInstrument(string instrument, SupervisoryTriggerReason reason, SupervisorySeverity severity, object? context,
            DateTimeOffset utcNow) =>
            _inner.RequestSupervisoryActionForInstrument(instrument, reason, severity, context, utcNow);

        public void EnqueueExecutionCommand(ExecutionCommandBase command) => _inner.EnqueueExecutionCommand(command);

        public IReadOnlyCollection<string> GetActiveIntentIdsForProtectiveAudit(string instrument) =>
            _inner.GetActiveIntentIdsForProtectiveAudit(instrument);

        public bool TryRepairTaggedBrokerWithoutJournal(string instrument, int accountQtyAbs, int journalOpenQtySum, DateTimeOffset utcNow,
            out string resultCode, out string? detail) =>
            _inner.TryRepairTaggedBrokerWithoutJournal(instrument, accountQtyAbs, journalOpenQtySum, utcNow, out resultCode, out detail);

        public void TryRetryDeferredAdoptionScan() => _inner.TryRetryDeferredAdoptionScan();

        public void PrepareOrderRegistryForMismatchAssembly(string instrument, AccountSnapshot snap, DateTimeOffset utcNow) =>
            _inner.PrepareOrderRegistryForMismatchAssembly(instrument, snap, utcNow);

        public FlattenResult? RequestSessionCloseFlattenImmediate(string intentId, string instrument, DateTimeOffset utcNow) =>
            _inner.RequestSessionCloseFlattenImmediate(intentId, instrument, utcNow);

        public bool TryTriggerHardFlatten(string instrument, string reason, DateTimeOffset utcNow) =>
            _inner.TryTriggerHardFlatten(instrument, reason, utcNow);
    }
}
