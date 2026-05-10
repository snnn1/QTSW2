// NinjaTrader-specific implementation using real NT APIs
// This file is compiled only when NINJATRADER is defined (inside NT Strategy context)

// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

#if NINJATRADER
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Microsoft.CSharp.RuntimeBinder;
using NinjaTrader.Cbi;
using NinjaTrader.NinjaScript;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// Real NinjaTrader API implementation for SIM adapter.
/// This partial class provides real NT API calls when running inside NinjaTrader.
/// </summary>
public sealed partial class NinjaTraderSimAdapter
{
    /// <summary>
    /// P0: Process broker flatten fill through canonical exit path.
    /// Emits EXECUTION_FILLED per intent, RecordExitFill, OnExitFill.
    /// If no intents can be mapped, emits EXECUTION_FILL_UNMAPPED (CRITICAL).
    /// </summary>
    private void EnqueueBrokerFlattenFillPostFill(
        object execution,
        string instrument,
        decimal fillPrice,
        int fillQuantity,
        DateTimeOffset utcNow,
        object orderId,
        Order? order,
        OrderRegistryEntry? flattenRegistryEntry,
        bool runFlatCheck,
        ExecutionUpdateSnapshot? snapshot = null)
    {
        var brokerOrderId = orderId?.ToString() ?? "";
        if (!TryMarkFlattenExecutionPostFill(
                execution,
                snapshot,
                instrument,
                fillPrice,
                fillQuantity,
                brokerOrderId,
                out var flattenDedupKey,
                out var flattenExecutionId))
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrument, "FLATTEN_EXECUTION_DUPLICATE_SKIPPED", new
            {
                broker_order_id = brokerOrderId,
                execution_id = flattenExecutionId,
                fill_quantity = fillQuantity,
                fill_price = fillPrice,
                dedup_key = flattenDedupKey,
                stage = "PRE_FLATTEN_POSTFILL_ENQUEUE",
                note = "Duplicate robot-owned flatten execution skipped before journal/ownership/coordinator post-fill work."
            }));
            return;
        }

        if (!_useInstrumentExecutionAuthority || _iea == null)
        {
            ProcessBrokerFlattenFill(execution, instrument, fillPrice, fillQuantity, utcNow, orderId, order, flattenRegistryEntry, snapshot: snapshot);
            if (runFlatCheck)
                CheckAllInstrumentsForFlatPositions(utcNow);
            return;
        }

        var followUpEnqueueUtc = DateTimeOffset.UtcNow;
        _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrument, "EXECUTION_UPDATE_FLATTEN_FOLLOWUP_ENQUEUED", new
        {
            instrument,
            broker_order_id = brokerOrderId,
            fill_quantity = fillQuantity,
            run_flat_check = runFlatCheck,
            queued_via = "IEA_WORKER",
            note = "Robot-owned flatten fill acknowledged on ExecutionUpdate; journal/ownership/coordinator work is deferred to bounded post-fill stages."
        }));

        void LogFlattenStage(string stageName, string workKind, DateTimeOffset stageStartUtc, long totalMs, int actionCount, DateTimeOffset stageEnqueueUtc)
        {
            _log.Write(RobotEvents.ExecutionBase(stageStartUtc, "FLATTEN", instrument, "EXECUTION_UPDATE_POSTFILL_STAGE_TIMING", new
            {
                intent_id = "FLATTEN",
                fill_quantity = fillQuantity,
                broker_order_id = brokerOrderId,
                stage = stageName,
                work_kind = workKind,
                action_count = actionCount,
                queue_delay_ms = Math.Max(0L, (long)(stageStartUtc - followUpEnqueueUtc).TotalMilliseconds),
                stage_queue_delay_ms = Math.Max(0L, (long)(stageStartUtc - stageEnqueueUtc).TotalMilliseconds),
                total_ms = totalMs,
                note = "Deferred flatten post-fill stage timing; forced-flatten ExecutionUpdate is kept bounded."
            }));
        }

        void EnqueueFlattenStage(string stageName, string workKind, Action action, int actionCount)
        {
            var stageEnqueueUtc = DateTimeOffset.UtcNow;
            _iea.EnqueueRecoveryEssential(() =>
            {
                var stageStartUtc = DateTimeOffset.UtcNow;
                var sw = Stopwatch.StartNew();
                action();
                LogFlattenStage(stageName, workKind, stageStartUtc, sw.ElapsedMilliseconds, actionCount, stageEnqueueUtc);
            }, workKind);
        }

        var lateConfirmCandidates = new List<FlattenLateConfirmCandidate>();

        EnqueueFlattenStage(
            "flatten_fill_mapping_journal",
            "ExecutionUpdatePostFillFlattenMapping",
            () => ProcessBrokerFlattenFill(
                execution,
                instrument,
                fillPrice,
                fillQuantity,
                utcNow,
                orderId,
                order,
                flattenRegistryEntry,
                lateConfirmCandidates,
                snapshot),
            1);

        EnqueueFlattenStage(
            "late_session_close_flatten_confirm_enqueue",
            "ExecutionUpdatePostFillFlattenLateConfirm",
            () =>
            {
                if (lateConfirmCandidates.Count == 0)
                    return;

                var registryOriginalIntentId = flattenRegistryEntry?.FlattenOriginalIntentId?.Trim() ?? "";
                var completionCandidate = lateConfirmCandidates.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(registryOriginalIntentId) &&
                    string.Equals(c.IntentId, registryOriginalIntentId, StringComparison.OrdinalIgnoreCase));
                if (completionCandidate == null || string.IsNullOrWhiteSpace(completionCandidate.IntentId))
                    completionCandidate = lateConfirmCandidates[0];

                EnqueueLateSessionCloseFlattenCompleteCheck(
                    completionCandidate.FlattenRegistryEntry,
                    completionCandidate.IntentId,
                    completionCandidate.Instrument,
                    completionCandidate.ExecutionInstrumentKey,
                    completionCandidate.TradingDate,
                    completionCandidate.Stream,
                    completionCandidate.UtcNow,
                    completionCandidate.AccountName,
                    completionCandidate.BrokerOrderId,
                    completionCandidate.AllowAllocatedIntent);
            },
            1);

        if (runFlatCheck)
        {
            EnqueueFlattenStage(
                "flat_check_enqueue",
                "ExecutionUpdatePostFillFlattenFlatCheckEnqueue",
                () =>
                {
                    var flatCheckEnqueueUtc = DateTimeOffset.UtcNow;
                    EnqueueStrategyThreadDeferredAction(
                        $"FLATTEN_FLAT_CHECK:{brokerOrderId}:{flatCheckEnqueueUtc:yyyyMMddHHmmssfff}",
                        "FLATTEN",
                        instrument,
                        "EXECUTION_UPDATE_POSTFILL_FLAT_CHECK",
                        flatCheckEnqueueUtc,
                        () =>
                        {
                            var flatCheckStartUtc = DateTimeOffset.UtcNow;
                            var flatCheckSw = Stopwatch.StartNew();
                            CheckAllInstrumentsForFlatPositions(flatCheckStartUtc);
                            LogFlattenStage("flat_check", "ExecutionUpdatePostFillFlattenFlatCheck", flatCheckStartUtc, flatCheckSw.ElapsedMilliseconds, 1, flatCheckEnqueueUtc);
                        });
                },
                1);
        }
    }

    private void EnqueueBrokerFlattenFillPostFill(
        ExecutionUpdateSnapshot snapshot,
        string instrument,
        decimal fillPrice,
        int fillQuantity,
        DateTimeOffset utcNow,
        object orderId,
        OrderRegistryEntry? flattenRegistryEntry,
        bool runFlatCheck)
    {
        EnqueueBrokerFlattenFillPostFill(snapshot, instrument, fillPrice, fillQuantity, utcNow, orderId, null,
            flattenRegistryEntry, runFlatCheck, snapshot);
    }

    private bool TryMarkFlattenExecutionPostFill(
        object execution,
        ExecutionUpdateSnapshot? snapshot,
        string instrument,
        decimal fillPrice,
        int fillQuantity,
        string brokerOrderId,
        out string dedupKey,
        out string executionId)
    {
        executionId = snapshot?.ExecutionId ?? "";
        if (string.IsNullOrWhiteSpace(executionId))
        {
            try
            {
                dynamic dynExecution = execution;
                executionId = dynExecution.ExecutionId as string ?? "";
            }
            catch
            {
                executionId = "";
            }
        }

        var accountName = snapshot?.AccountName ?? _iea?.AccountName ?? "";
        var executionInstrumentKey = snapshot?.ExecutionInstrumentKey ?? _iea?.ExecutionInstrumentKey ?? instrument ?? "";
        var executionTimeTicks = snapshot?.ExecutionTimeTicks ?? 0L;
        if (executionTimeTicks == 0)
            executionTimeTicks = SafeReadExecutionTimeTicks(execution);

        var instKey = (executionInstrumentKey.Length > 0 ? executionInstrumentKey : instrument ?? "").Trim();
        var orderKey = brokerOrderId?.Trim() ?? "";
        var accountKey = accountName.Trim();

        dedupKey = !string.IsNullOrWhiteSpace(executionId)
            ? $"{accountKey}|{instKey}|{orderKey}|exec:{executionId.Trim()}|qty:{fillQuantity}"
            : $"{accountKey}|{instKey}|{orderKey}|ticks:{executionTimeTicks}|qty:{fillQuantity}|price:{fillPrice}";

        if (string.IsNullOrWhiteSpace(dedupKey))
            return true;

        return _flattenExecutionPostFillProcessed.TryAdd(dedupKey, 0);
    }

    private sealed class FlattenLateConfirmCandidate
    {
        public OrderRegistryEntry? FlattenRegistryEntry { get; init; }
        public string IntentId { get; init; } = "";
        public string Instrument { get; init; } = "";
        public string? ExecutionInstrumentKey { get; init; }
        public string TradingDate { get; init; } = "";
        public string Stream { get; init; } = "";
        public DateTimeOffset UtcNow { get; init; }
        public string AccountName { get; init; } = "";
        public string BrokerOrderId { get; init; } = "";
        public bool AllowAllocatedIntent { get; init; }
    }

    private void ProcessBrokerFlattenFill(
        object execution,
        string instrument,
        decimal fillPrice,
        int fillQuantity,
        DateTimeOffset utcNow,
        object orderId,
        Order? order,
        OrderRegistryEntry? flattenRegistryEntry = null,
        IList<FlattenLateConfirmCandidate>? lateConfirmCandidates = null,
        ExecutionUpdateSnapshot? snapshot = null)
    {
        var flattenTimingSw = Stopwatch.StartNew();
        var allocationTimingSw = Stopwatch.StartNew();
        long allocationMs = 0;
        long ownershipRestoreMs = 0;
        long journalMs = 0;
        long ownershipMs = 0;
        long lifecycleMs = 0;
        long coordinatorMs = 0;
        long executionLogMs = 0;
        long lateConfirmEnqueueMs = 0;
        var emittedTiming = false;

        void EmitFlattenFillTiming(string outcome)
        {
            if (emittedTiming)
                return;
            emittedTiming = true;
            _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrument, "EXECUTION_UPDATE_POSTFILL_STAGE_TIMING", new
            {
                intent_id = "FLATTEN",
                fill_quantity = fillQuantity,
                broker_order_id = orderId?.ToString() ?? "",
                stage = "flatten_fill_breakdown",
                work_kind = "ExecutionUpdatePostFillFlatten",
                action_count = 1,
                total_ms = flattenTimingSw.ElapsedMilliseconds,
                allocation_ms = allocationMs,
                ownership_restore_ms = ownershipRestoreMs,
                journal_ms = journalMs,
                ownership_ms = ownershipMs,
                lifecycle_ms = lifecycleMs,
                coordinator_ms = coordinatorMs,
                execution_log_ms = executionLogMs,
                late_confirm_enqueue_ms = lateConfirmEnqueueMs,
                outcome,
                note = "Breakdown of robot-owned flatten fill processing after ExecutionUpdate was bounded."
            }));
        }

        try
        {
        var accountName = !string.IsNullOrWhiteSpace(snapshot?.AccountName)
            ? snapshot!.AccountName
            : (_iea?.AccountName ?? (order != null ? ExecutionUpdateRouter.GetAccountNameFromOrder(order) : ""));
        var execInstKey = !string.IsNullOrWhiteSpace(snapshot?.ExecutionInstrumentKey)
            ? snapshot!.ExecutionInstrumentKey
            : (_iea?.ExecutionInstrumentKey ?? (order != null ? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order.Instrument) : ""));
        var brokerOrderId = orderId?.ToString() ?? "";
        var orderIdInternal = brokerOrderId;
        var executionSeq = GetNextExecutionSequence(execInstKey, accountName);
        string? brokerExecId = snapshot?.ExecutionId;
        if (string.IsNullOrWhiteSpace(brokerExecId))
        {
            try { dynamic d = execution; brokerExecId = d.ExecutionId as string; } catch { }
        }
        var fillGroupId = ComputeFillGroupId(brokerExecId, orderIdInternal, brokerOrderId, utcNow.ToString("o"), fillPrice, fillQuantity);

        List<(string IntentId, int Qty, string TradingDate, string Stream, ExecutionJournalEntry? Entry)> allocated;
        var allocatedFromFlattenRegistry = false;
        var allocatedFromOpenJournal = false;

        if (TryAllocateFlattenFillFromRegistryLink(flattenRegistryEntry, instrument, execInstKey, fillQuantity,
                out allocated, out var registryAllocationSource, out var registryUsedOpenJournal))
        {
            allocatedFromFlattenRegistry = true;
            allocatedFromOpenJournal = registryUsedOpenJournal;
            _log.Write(RobotEvents.ExecutionBase(utcNow, flattenRegistryEntry?.FlattenOriginalIntentId ?? "FLATTEN", instrument, "FLATTEN_FILL_ALLOCATED_FROM_REGISTRY_LINK",
                new
                {
                    broker_order_id = brokerOrderId,
                    instrument,
                    execution_instrument_key = execInstKey,
                    fill_quantity = fillQuantity,
                    allocation_count = allocated.Count,
                    original_intent_id = flattenRegistryEntry?.FlattenOriginalIntentId,
                    flatten_request_id = flattenRegistryEntry?.FlattenRequestId,
                    flatten_reason = flattenRegistryEntry?.FlattenReason,
                    flatten_leg_index = flattenRegistryEntry?.FlattenLegIndex,
                    allocation_source = registryAllocationSource,
                    note = registryUsedOpenJournal
                        ? "Robot-owned session flatten fill mapped from the registry link to all open journal intents on the instrument."
                        : "Robot-owned flatten fill mapped from the flatten order registry link to its original intent."
                }));
        }
        else
        {
            // Try instrument and execInstKey - exposure.Instrument may be MES while order reports ES
            var exposures = _coordinator?.GetActiveExposuresForInstrument(instrument) ?? new List<IntentExposure>();
            if (exposures.Count == 0 && !string.IsNullOrEmpty(execInstKey))
                exposures = _coordinator?.GetActiveExposuresForInstrument(execInstKey) ?? new List<IntentExposure>();
            var matchingExposures = exposures.Where(e => ExecutionInstrumentResolver.IsSameInstrument(e.Instrument, instrument)).ToList();

            if (matchingExposures.Count == 0)
            {
                allocated = AllocateFlattenFillToOpenJournalEntries(instrument, execInstKey, fillQuantity);
                allocatedFromOpenJournal = allocated.Count > 0;
                if (!allocatedFromOpenJournal)
                {
                    EmitUnmappedFill(instrument, "NO_ACTIVE_EXPOSURES", fillPrice, fillQuantity, utcNow, orderId, order);
                    LogCriticalWithIeaContext(utcNow, "", instrument, "EXECUTION_FILL_UNMAPPED",
                        new
                        {
                            error = "Broker flatten fill cannot be mapped to any intent",
                            broker_order_id = brokerOrderId,
                            instrument = instrument,
                            account = accountName,
                            execution_instrument_key = execInstKey,
                            fill_price = fillPrice,
                            fill_quantity = fillQuantity,
                            timestamp_utc = utcNow.ToString("o"),
                            note = "No registry link, active exposures, or open journal rows for instrument - PnL gap"
                        });
                    allocationMs = allocationTimingSw.ElapsedMilliseconds;
                    EmitFlattenFillTiming("unmapped_no_active_exposures");
                    return;
                }

                _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrument, "FLATTEN_FILL_ALLOCATED_FROM_OPEN_JOURNAL",
                    new
                    {
                        broker_order_id = brokerOrderId,
                        instrument,
                        execution_instrument_key = execInstKey,
                        fill_quantity = fillQuantity,
                        allocation_count = allocated.Count,
                        note = "Coordinator exposure was unavailable; open execution journal rows were used to map the robot-owned flatten fill."
                    }));
            }
            else
            {
                var totalRemaining = matchingExposures.Sum(e => e.RemainingExposure);
                if (totalRemaining <= 0)
                {
                    allocated = AllocateFlattenFillToOpenJournalEntries(instrument, execInstKey, fillQuantity);
                    allocatedFromOpenJournal = allocated.Count > 0;
                    if (!allocatedFromOpenJournal)
                    {
                        EmitUnmappedFill(instrument, "ZERO_REMAINING_EXPOSURE", fillPrice, fillQuantity, utcNow, orderId, order);
                        LogCriticalWithIeaContext(utcNow, "", instrument, "EXECUTION_FILL_UNMAPPED",
                            new
                            {
                                error = "Broker flatten fill but exposures have zero remaining",
                                broker_order_id = brokerOrderId,
                                instrument = instrument,
                                account = accountName,
                                execution_instrument_key = execInstKey,
                                fill_price = fillPrice,
                                fill_quantity = fillQuantity,
                                note = "Coordinator state inconsistent and no registry/open journal fallback was available"
                            });
                        allocationMs = allocationTimingSw.ElapsedMilliseconds;
                        EmitFlattenFillTiming("unmapped_zero_remaining_exposure");
                        return;
                    }

                    _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", instrument, "FLATTEN_FILL_ALLOCATED_FROM_OPEN_JOURNAL",
                        new
                        {
                            broker_order_id = brokerOrderId,
                            instrument,
                            execution_instrument_key = execInstKey,
                            fill_quantity = fillQuantity,
                            allocation_count = allocated.Count,
                            note = "Coordinator exposure had zero remaining; open execution journal rows were used to map the robot-owned flatten fill."
                        }));
                }
                else
                {
                    allocated = fillQuantity >= totalRemaining
                        ? matchingExposures.Select(e => (e.IntentId, e.RemainingExposure, "", "", (ExecutionJournalEntry?)null)).ToList()
                        : AllocateFlattenFillToExposures(matchingExposures, fillQuantity)
                            .Select(e => (e.IntentId, e.Qty, "", "", (ExecutionJournalEntry?)null)).ToList();
                }
            }
        }
        allocationMs = allocationTimingSw.ElapsedMilliseconds;

        if (allocatedFromOpenJournal)
        {
            var restoreSw = Stopwatch.StartNew();
            TryRestoreOwnershipLedgerForFlattenOpenJournalAllocation(instrument, execInstKey, utcNow);
            ownershipRestoreMs += restoreSw.ElapsedMilliseconds;
        }

        var lateSessionCloseFlattenCompletionCandidates =
            new List<FlattenLateConfirmCandidate>();

        foreach (var (intentId, allocQty, journalTradingDate, journalStream, journalEntry) in allocated)
        {
            if (allocQty <= 0) continue;
            if (!IntentMap.TryGetValue(intentId, out var intent))
            {
                if (journalEntry != null)
                {
                    intent = CreateIntentFromJournalEntry(journalTradingDate, journalStream, intentId, journalEntry);
                    if (intent != null)
                        RegisterIntent(intent);
                }

                if (intent == null)
                {
                    EmitUnmappedFill(instrument, "INTENT_NOT_FOUND", fillPrice, allocQty, utcNow, orderId, order);
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_FILL_UNMAPPED",
                        new { broker_order_id = brokerOrderId, intent_id = intentId, alloc_qty = allocQty, error = "Intent not in IntentMap" }));
                    continue;
                }
            }

            var tradingDate = intent.TradingDate ?? journalTradingDate ?? "";
            if (string.IsNullOrWhiteSpace(tradingDate))
            {
                EmitUnmappedFill(instrument, "TRADING_DATE_NULL", fillPrice, allocQty, utcNow, orderId, order);
                LogCriticalWithIeaContext(utcNow, intentId, instrument, "EXECUTION_FILL_BLOCKED_TRADING_DATE_NULL",
                    new { intent_id = intentId, fill_price = fillPrice, fill_quantity = allocQty, order_type = "FLATTEN", note = "Broker flatten: trading_date null" });
                continue;
            }

            var stream = intent.Stream ?? journalStream ?? "";
            var direction = intent.Direction ?? "";
            var side = direction.Equals("Long", StringComparison.OrdinalIgnoreCase) ? "SELL" : "BUY";
            var sessionClass = DeriveSessionClass(stream);

            var journalSw = Stopwatch.StartNew();
            _executionJournal.RecordExitFill(intentId, tradingDate, stream, fillPrice, allocQty, "FLATTEN", utcNow);
            journalMs += journalSw.ElapsedMilliseconds;
            if (FeatureFlags.CanonicalOwnershipLedgerEnabled && _ownershipLedger != null)
            {
                var ownershipSw = Stopwatch.StartNew();
                var result = _ownershipLedger.RecordMappedExitFill(
                    GetLedgerAccountName(), instrument.Trim(), intentId, allocQty, utcNow, executionSeq);
                ownershipMs += ownershipSw.ElapsedMilliseconds;
                if (!result.Success)
                {
                    LogCriticalWithIeaContext(utcNow, intentId, instrument, "OWNERSHIP_FLATTEN_EXIT_CLOSE_FAILED",
                        new
                        {
                            intent_id = intentId,
                            instrument = instrument,
                            account = GetLedgerAccountName(),
                            fill_quantity = allocQty,
                            error = result.ErrorReason ?? "",
                            note = "Broker flatten fill closed legacy journal exposure but ownership ledger close failed"
                    });
                }
            }
            var lifecycleSw = Stopwatch.StartNew();
            _iea?.TryTransitionIntentLifecycle(intentId, IntentLifecycleTransition.INTENT_COMPLETED, null, utcNow);
            lifecycleMs += lifecycleSw.ElapsedMilliseconds;
            var coordinatorSw = Stopwatch.StartNew();
            _coordinator?.OnExitFill(intentId, allocQty, utcNow);
            coordinatorMs += coordinatorSw.ElapsedMilliseconds;

            var executionLogSw = Stopwatch.StartNew();
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "EXECUTION_FILLED",
                new
                {
                    execution_sequence = executionSeq,
                    fill_group_id = fillGroupId,
                    order_id = orderIdInternal,
                    broker_order_id = brokerOrderId,
                    intent_id = intentId,
                    instrument = instrument,
                    execution_instrument_key = execInstKey,
                    side = side,
                    order_type = "FLATTEN",
                    position_effect = "CLOSE",
                    fill_price = fillPrice,
                    fill_quantity = allocQty,
                    filled_total = allocQty,
                    remaining_qty = 0,
                    timestamp_utc = utcNow.ToString("o"),
                    trading_date = tradingDate,
                    account = accountName,
                    stream_key = stream,
                    session_class = sessionClass,
                    source = "robot",
                    mapped = true,
                    mapped_from_flatten_registry = allocatedFromFlattenRegistry,
                    mapped_from_open_journal = allocatedFromOpenJournal
                }));
            executionLogMs += executionLogSw.ElapsedMilliseconds;

            lateSessionCloseFlattenCompletionCandidates.Add(new FlattenLateConfirmCandidate
            {
                FlattenRegistryEntry = flattenRegistryEntry,
                IntentId = intentId,
                Instrument = instrument,
                ExecutionInstrumentKey = execInstKey,
                TradingDate = tradingDate,
                Stream = stream,
                UtcNow = utcNow,
                AccountName = accountName,
                BrokerOrderId = brokerOrderId,
                AllowAllocatedIntent =
                    allocatedFromOpenJournal ||
                    !string.Equals(
                        flattenRegistryEntry?.FlattenOriginalIntentId?.Trim() ?? "",
                        intentId,
                        StringComparison.OrdinalIgnoreCase)
            });
        }

        if (lateSessionCloseFlattenCompletionCandidates.Count > 0)
        {
            var registryOriginalIntentId = flattenRegistryEntry?.FlattenOriginalIntentId?.Trim() ?? "";
            var completionCandidate =
                lateSessionCloseFlattenCompletionCandidates.FirstOrDefault(c =>
                    !string.IsNullOrWhiteSpace(registryOriginalIntentId) &&
                    string.Equals(c.IntentId, registryOriginalIntentId, StringComparison.OrdinalIgnoreCase));
            if (completionCandidate == null || string.IsNullOrWhiteSpace(completionCandidate.IntentId))
                completionCandidate = lateSessionCloseFlattenCompletionCandidates[0];

            var lateConfirmSw = Stopwatch.StartNew();
            if (lateConfirmCandidates != null)
            {
                lateConfirmCandidates.Add(completionCandidate);
            }
            else
            {
                EnqueueLateSessionCloseFlattenCompleteCheck(
                    completionCandidate.FlattenRegistryEntry,
                    completionCandidate.IntentId,
                    completionCandidate.Instrument,
                    completionCandidate.ExecutionInstrumentKey,
                    completionCandidate.TradingDate,
                    completionCandidate.Stream,
                    completionCandidate.UtcNow,
                    completionCandidate.AccountName,
                    completionCandidate.BrokerOrderId,
                    completionCandidate.AllowAllocatedIntent);
            }
            lateConfirmEnqueueMs += lateConfirmSw.ElapsedMilliseconds;
        }
        EmitFlattenFillTiming(allocated.Count > 0 ? "mapped" : "no_allocations");
        }
        catch
        {
            if (allocationMs == 0)
                allocationMs = allocationTimingSw.ElapsedMilliseconds;
            EmitFlattenFillTiming("exception");
            throw;
        }
    }

    private void TryRestoreOwnershipLedgerForFlattenOpenJournalAllocation(
        string instrument,
        string? executionInstrumentKey,
        DateTimeOffset utcNow)
    {
        if (!FeatureFlags.CanonicalOwnershipLedgerEnabled || _ownershipLedger == null)
            return;

        var ledgerInstrument = !string.IsNullOrWhiteSpace(instrument)
            ? instrument.Trim()
            : (executionInstrumentKey ?? "").Trim();
        if (string.IsNullOrWhiteSpace(ledgerInstrument))
            return;

        try
        {
            var openByInstrument = _executionJournal.GetOpenJournalEntriesByInstrument();
            var rows = new List<JournalRestoreRow>();
            long seq = 0;

            foreach (var kvp in openByInstrument)
            {
                var journalInstrument = kvp.Key?.Trim() ?? "";
                if (string.IsNullOrWhiteSpace(journalInstrument))
                    continue;
                if (!ExecutionInstrumentResolver.IsSameInstrument(journalInstrument, instrument) &&
                    !ExecutionInstrumentResolver.IsSameInstrument(journalInstrument, executionInstrumentKey))
                    continue;

                foreach (var row in kvp.Value
                             .OrderBy(r => r.TradingDate, StringComparer.Ordinal)
                             .ThenBy(r => r.Stream, StringComparer.Ordinal)
                             .ThenBy(r => r.IntentId, StringComparer.Ordinal))
                {
                    var entry = row.Entry;
                    if (entry == null) continue;
                    var remaining = entry.EntryFilledQuantityTotal - entry.ExitFilledQuantityTotal;
                    if (remaining <= 0) continue;

                    rows.Add(new JournalRestoreRow
                    {
                        IntentId = row.IntentId,
                        Stream = row.Stream,
                        Direction = entry.Direction,
                        EntryFilledQty = entry.EntryFilledQuantityTotal,
                        ExitFilledQty = entry.ExitFilledQuantityTotal,
                        ExecutionSequence = seq++,
                        IsOrphan = string.Equals(entry.IntentType, ExecutionJournal.IntentTypeRecovered, StringComparison.OrdinalIgnoreCase)
                    });
                }
            }

            if (rows.Count == 0)
                return;

            _ownershipLedger.RestoreFromJournal(GetLedgerAccountName(), ledgerInstrument, rows, utcNow);
            _log.Write(RobotEvents.ExecutionBase(utcNow, "FLATTEN", ledgerInstrument, "OWNERSHIP_FLATTEN_LEDGER_RESTORED_FROM_OPEN_JOURNAL",
                new
                {
                    instrument,
                    execution_instrument_key = executionInstrumentKey,
                    ledger_instrument = ledgerInstrument,
                    row_count = rows.Count,
                    note = "Session flatten allocation used open journal rows; ownership ledger restored from the same frame before applying flatten exit fills."
                }));
        }
        catch (Exception ex)
        {
            LogCriticalWithIeaContext(utcNow, "", ledgerInstrument, "OWNERSHIP_FLATTEN_LEDGER_RESTORE_FAILED",
                new
                {
                    instrument,
                    execution_instrument_key = executionInstrumentKey,
                    ledger_instrument = ledgerInstrument,
                    error = ex.Message,
                    note = "Could not restore ownership ledger before open-journal flatten allocation."
                });
        }
    }

    private void EnqueueLateSessionCloseFlattenCompleteCheck(
        OrderRegistryEntry? flattenRegistryEntry,
        string intentId,
        string instrument,
        string? executionInstrumentKey,
        string tradingDate,
        string stream,
        DateTimeOffset utcNow,
        string accountName,
        string brokerOrderId,
        bool allowAllocatedIntent)
    {
        if (flattenRegistryEntry == null ||
            !string.Equals(flattenRegistryEntry.FlattenReason?.Trim(), "SESSION_FORCED_FLATTEN", StringComparison.OrdinalIgnoreCase))
        {
            TryNotifyLateSessionCloseFlattenComplete(
                flattenRegistryEntry,
                intentId,
                instrument,
                executionInstrumentKey,
                tradingDate,
                stream,
                utcNow,
                accountName,
                brokerOrderId,
                allowAllocatedIntent);
            return;
        }

        var enqueuedAt = DateTimeOffset.UtcNow;
        EnqueueStrategyThreadDeferredAction(
            $"SESSION_CLOSE_FLATTEN_LATE_CONFIRM:{intentId}:{brokerOrderId}:{enqueuedAt:yyyyMMddHHmmssfff}",
            intentId,
            executionInstrumentKey ?? instrument,
            "SESSION_CLOSE_FLATTEN_LATE_CONFIRM",
            enqueuedAt,
            () =>
            {
                var stageStartUtc = DateTimeOffset.UtcNow;
                var sw = Stopwatch.StartNew();
                TryNotifyLateSessionCloseFlattenComplete(
                    flattenRegistryEntry,
                    intentId,
                    instrument,
                    executionInstrumentKey,
                    tradingDate,
                    stream,
                    stageStartUtc,
                    accountName,
                    brokerOrderId,
                    allowAllocatedIntent);
                _log.Write(RobotEvents.ExecutionBase(stageStartUtc, intentId, instrument, "EXECUTION_UPDATE_POSTFILL_STAGE_TIMING", new
                {
                    intent_id = intentId,
                    broker_order_id = brokerOrderId,
                    fill_quantity = 0,
                    stage = "late_session_close_flatten_confirm",
                    work_kind = "ExecutionUpdatePostFillFlattenConfirm",
                    action_count = 1,
                    queue_delay_ms = Math.Max(0L, (long)(stageStartUtc - enqueuedAt).TotalMilliseconds),
                    total_ms = sw.ElapsedMilliseconds,
                    note = "Late broker-flat confirmation runs on the strategy thread because it reads NT account position state."
                }));
            });
    }

    private void TryNotifyLateSessionCloseFlattenComplete(
        OrderRegistryEntry? flattenRegistryEntry,
        string intentId,
        string instrument,
        string? executionInstrumentKey,
        string tradingDate,
        string stream,
        DateTimeOffset utcNow,
        string accountName,
        string brokerOrderId,
        bool allowAllocatedIntent)
    {
        if (flattenRegistryEntry == null)
            return;

        if (!string.Equals(flattenRegistryEntry.FlattenReason?.Trim(), "SESSION_FORCED_FLATTEN", StringComparison.OrdinalIgnoreCase))
            return;

        var registryOriginalIntentId = flattenRegistryEntry.FlattenOriginalIntentId?.Trim() ?? "";
        var completionIntentId = intentId?.Trim() ?? "";
        if (string.IsNullOrWhiteSpace(completionIntentId) ||
            (!allowAllocatedIntent &&
             (string.IsNullOrWhiteSpace(registryOriginalIntentId) ||
              !string.Equals(registryOriginalIntentId, completionIntentId, StringComparison.OrdinalIgnoreCase))) ||
            string.IsNullOrWhiteSpace(tradingDate) ||
            string.IsNullOrWhiteSpace(stream))
        {
            return;
        }

        var entry = _executionJournal.GetEntry(completionIntentId, tradingDate, stream);
        var remainingOpenQty = entry != null ? ExecutionJournal.GetEntryRemainingOpenQuantity(entry) : int.MaxValue;
        if (remainingOpenQty > 0)
            return;

        var exposureInstrument = !string.IsNullOrWhiteSpace(executionInstrumentKey)
            ? executionInstrumentKey.Trim()
            : instrument.Trim();

        BrokerCanonicalExposure exposure;
        try
        {
            exposure = ((IIEAOrderExecutor)this).GetBrokerCanonicalExposure(exposureInstrument);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, completionIntentId, instrument, "SESSION_CLOSE_FLATTEN_LATE_BROKER_FLAT_CHECK_FAILED",
                new
                {
                    broker_order_id = brokerOrderId,
                    instrument,
                    execution_instrument_key = exposureInstrument,
                    account = accountName,
                    error = ex.Message,
                    exception_type = ex.GetType().Name
                }));
            return;
        }

        if (!FlattenCompletionAuthority.IsOfficialFlattenComplete(exposure))
            return;

        var dedupeKey = $"{completionIntentId}|{exposureInstrument}|{flattenRegistryEntry.FlattenRequestId ?? ""}";
        if (!_lateSessionCloseFlattenConfirmed.TryAdd(dedupeKey, 0))
            return;

        var retiredVerifiers = RetirePendingFlattenVerificationsForLateSessionClose(exposureInstrument, completionIntentId, exposure, utcNow);
        var fallbackCorrelationId = flattenRegistryEntry.FlattenRequestId?.Trim();
        if (!string.IsNullOrWhiteSpace(fallbackCorrelationId))
            TryAppendLateSessionCloseFlattenConfirmedKeyEvent(utcNow, exposureInstrument, fallbackCorrelationId, null, completionIntentId);

        _log.Write(RobotEvents.ExecutionBase(utcNow, completionIntentId, instrument, "SESSION_CLOSE_FLATTEN_LATE_BROKER_FLAT_CONFIRMED",
            new
            {
                broker_order_id = brokerOrderId,
                instrument,
                execution_instrument_key = exposureInstrument,
                account = accountName,
                trading_date = tradingDate,
                stream,
                original_intent_id = completionIntentId,
                registry_original_intent_id = registryOriginalIntentId,
                flatten_request_id = flattenRegistryEntry.FlattenRequestId,
                flatten_reason = flattenRegistryEntry.FlattenReason,
                journal_remaining_open_qty = remainingOpenQty,
                broker_reconciliation_abs_total = exposure.ReconciliationAbsQuantityTotal,
                broker_legs = BrokerPositionResolver.ToDiagnosticRows(exposure),
                retired_pending_flatten_verifiers = retiredVerifiers.Select(x => new { instrument = x.InstrumentKey, correlation_id = x.CorrelationId, episode_id = x.EpisodeId }).ToArray(),
                note = "Timed-out session-close flatten later completed; broker-flat proof will clear the reentry latch."
            }));

        _iea?.ReleaseFlattenLatch(exposureInstrument, InstrumentExecutionAuthority.FlattenLatchState.Resolved, utcNow);

        try
        {
            _onSessionCloseFlattenConfirmedLateCallback?.Invoke(completionIntentId, exposureInstrument, utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, completionIntentId, instrument, "SESSION_CLOSE_FLATTEN_LATE_CALLBACK_FAILED",
                new
                {
                    broker_order_id = brokerOrderId,
                    instrument,
                    execution_instrument_key = exposureInstrument,
                    account = accountName,
                    error = ex.Message,
                    exception_type = ex.GetType().Name
                }));
        }
    }

    private OrderRegistryEntry? TryResolveFlattenRegistryEntry(object? orderId)
    {
        var brokerOrderId = orderId?.ToString() ?? "";
        if (!_useInstrumentExecutionAuthority || _iea == null || string.IsNullOrEmpty(brokerOrderId))
            return null;

        if (!_iea.TryResolveByBrokerOrderId(brokerOrderId, out var entry) || entry == null)
            return null;

        return entry.OrderRole == OrderRole.FLATTEN || entry.OrderRole == OrderRole.RECOVERY_FLATTEN
            ? entry
            : null;
    }

    private bool TryAllocateFlattenFillFromRegistryLink(
        OrderRegistryEntry? flattenRegistryEntry,
        string instrument,
        string? executionInstrumentKey,
        int fillQuantity,
        out List<(string IntentId, int Qty, string TradingDate, string Stream, ExecutionJournalEntry? Entry)> allocated,
        out string allocationSource,
        out bool allocatedFromOpenJournal)
    {
        allocated = new List<(string IntentId, int Qty, string TradingDate, string Stream, ExecutionJournalEntry? Entry)>();
        allocationSource = "";
        allocatedFromOpenJournal = false;
        if (flattenRegistryEntry == null || fillQuantity <= 0)
            return false;

        var originalIntentId = flattenRegistryEntry.FlattenOriginalIntentId?.Trim() ?? "";
        var registryInstrument = flattenRegistryEntry.Instrument?.Trim() ?? "";
        var executionInstrument = !string.IsNullOrWhiteSpace(executionInstrumentKey)
            ? executionInstrumentKey.Trim()
            : (!string.IsNullOrWhiteSpace(registryInstrument) ? registryInstrument : instrument.Trim());
        var canonical = DeriveCanonicalFromExecutionInstrument(executionInstrument);
        var openJournalAllocation = AllocateFlattenFillToOpenJournalEntries(instrument, executionInstrumentKey, fillQuantity);

        var located = _executionJournal.TryGetAdoptionCandidateEntry(originalIntentId, executionInstrument, canonical)
            ?? _executionJournal.TryGetAdoptionCandidateEntry(originalIntentId, instrument, canonical)
            ?? (!string.IsNullOrWhiteSpace(registryInstrument)
                ? _executionJournal.TryGetAdoptionCandidateEntry(originalIntentId, registryInstrument, canonical)
                : null);

        var originalRemaining = located.HasValue
            ? ExecutionJournal.GetEntryRemainingOpenQuantity(located.Value.Entry)
            : 0;
        var openJournalQty = openJournalAllocation.Sum(x => x.Qty);

        if (FlattenFillAllocationPolicy.ShouldPreferOpenJournalAllocationForRegistryLink(
                flattenRegistryEntry.FlattenReason,
                fillQuantity,
                originalRemaining,
                openJournalAllocation.Count,
                openJournalQty))
        {
            allocated = openJournalAllocation;
            allocationSource = "registry_session_forced_flatten_open_journal";
            allocatedFromOpenJournal = true;
            return true;
        }

        if (string.IsNullOrEmpty(originalIntentId) ||
            originalIntentId.Equals("NT_FLATTEN", StringComparison.OrdinalIgnoreCase) ||
            originalIntentId.Equals("EMERGENCY_BLOCK", StringComparison.OrdinalIgnoreCase) ||
            originalIntentId.Equals("UNKNOWN_UNTrackED_FILL", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!located.HasValue)
            return false;

        var remaining = originalRemaining;
        if (remaining <= 0)
            return false;

        var qty = Math.Min(fillQuantity, remaining);
        if (qty <= 0)
            return false;

        allocated.Add((originalIntentId, qty, located.Value.TradingDate, located.Value.Stream, located.Value.Entry));
        allocationSource = "registry_original_intent";
        return true;
    }

    private List<(string IntentId, int Qty, string TradingDate, string Stream, ExecutionJournalEntry? Entry)> AllocateFlattenFillToOpenJournalEntries(
        string instrument,
        string? executionInstrumentKey,
        int fillQuantity)
    {
        var result = new List<(string IntentId, int Qty, string TradingDate, string Stream, ExecutionJournalEntry? Entry)>();
        if (fillQuantity <= 0) return result;

        var openByInstrument = _executionJournal.GetOpenJournalEntriesByInstrument();
        var candidates = new List<(string TradingDate, string Stream, string IntentId, ExecutionJournalEntry Entry, int Remaining)>();
        foreach (var kvp in openByInstrument)
        {
            var journalInstrument = kvp.Key?.Trim() ?? "";
            if (string.IsNullOrEmpty(journalInstrument))
                continue;
            if (!ExecutionInstrumentResolver.IsSameInstrument(journalInstrument, instrument) &&
                !ExecutionInstrumentResolver.IsSameInstrument(journalInstrument, executionInstrumentKey))
                continue;

            foreach (var row in kvp.Value)
            {
                var remaining = ExecutionJournal.GetEntryRemainingOpenQuantity(row.Entry);
                if (remaining <= 0)
                    continue;
                candidates.Add((row.TradingDate, row.Stream, row.IntentId, row.Entry, remaining));
            }
        }

        var remainingFill = fillQuantity;
        foreach (var candidate in candidates
                     .OrderBy(c => c.TradingDate, StringComparer.Ordinal)
                     .ThenBy(c => c.Stream, StringComparer.Ordinal)
                     .ThenBy(c => c.IntentId, StringComparer.Ordinal))
        {
            if (remainingFill <= 0)
                break;

            var qty = Math.Min(remainingFill, candidate.Remaining);
            if (qty <= 0)
                continue;

            result.Add((candidate.IntentId, qty, candidate.TradingDate, candidate.Stream, candidate.Entry));
            remainingFill -= qty;
        }

        return result;
    }

    private static List<(string IntentId, int Qty)> AllocateFlattenFillToExposures(List<IntentExposure> exposures, int fillQuantity)
    {
        var total = exposures.Sum(e => e.RemainingExposure);
        if (total <= 0) return new List<(string, int)>();
        var result = new List<(string, int)>();
        var remaining = fillQuantity;
        foreach (var e in exposures.OrderBy(x => x.IntentId))
        {
            if (remaining <= 0) break;
            var pct = (double)e.RemainingExposure / total;
            var alloc = (int)Math.Round(fillQuantity * pct);
            if (alloc > remaining) alloc = remaining;
            if (alloc > e.RemainingExposure) alloc = e.RemainingExposure;
            if (alloc > 0)
            {
                result.Add((e.IntentId, alloc));
                remaining -= alloc;
            }
        }
        if (remaining > 0 && result.Count > 0)
        {
            var last = result[result.Count - 1];
            result[result.Count - 1] = (last.Item1, last.Item2 + remaining);
        }
        return result;
    }

    private static string DeriveSessionClass(string stream)
    {
        if (string.IsNullOrEmpty(stream)) return "";
        var last = stream[stream.Length - 1];
        return char.IsDigit(last) ? "S" + last : "";
    }

    /// <summary>
    /// Canonical unmapped fill: emit EXECUTION_FILLED(mapped=false) before fail-closed.
    /// Removes accounting hole; unmapped fills become actionable incidents.
    /// unmappedReason: NO_ACTIVE_EXPOSURES, ZERO_REMAINING_EXPOSURE, UNTrackED_TAG, UNKNOWN_ORDER_AFTER_GRACE, INTENT_NOT_FOUND, TRADING_DATE_NULL, OTHER
    /// </summary>
    private void EmitUnmappedFill(
        string instrument,
        string unmappedReason,
        decimal fillPrice,
        int fillQuantity,
        DateTimeOffset utcNow,
        object? orderId,
        Order? order,
        string? ntOrderName = null,
        string? tag = null,
        string? ocoId = null)
    {
        // Race mitigation: latch kill-switch before any logging / secondary work so no submit can slip past.
        ExecutionSafetyGate.ApplyUnmappedExecutionKillSwitch(instrument, unmappedReason, utcNow);
        QuantExecutionControlStore.NotifyUnmappedExecution(instrument, utcNow, unmappedReason);
        var instKey = (instrument ?? "").Trim();
        if (!string.IsNullOrEmpty(instKey))
        {
            _blockInstrumentCallback?.Invoke(instKey, utcNow, "UNMAPPED_FILL_HARD_STOP");
            _log.Write(RobotEvents.ExecutionBase(utcNow, "", instKey, "CRITICAL_UNMAPPED_FILL_DETECTED",
                new
                {
                    instrument = instKey,
                    instrument_state = "LOCKED",
                    unmapped_reason = unmappedReason,
                    trading_disabled = true,
                    note = "Hard stop: all entries, exits, flatten, and recovery blocked until UnlockInstrument after operator review"
                }));
        }

        var execInstKey = _iea?.ExecutionInstrumentKey ?? (order != null ? ExecutionUpdateRouter.GetExecutionInstrumentKeyFromOrder(order.Instrument) : "");
        var accountName = _iea?.AccountName ?? (order != null ? ExecutionUpdateRouter.GetAccountNameFromOrder(order) : "");
        var brokerOrderId = orderId?.ToString() ?? "";
        var orderIdInternal = brokerOrderId;
        var seq = GetNextExecutionSequence(execInstKey, accountName);
        var fillGroupId = ComputeFillGroupId(null, orderIdInternal, brokerOrderId, utcNow.ToString("o"), fillPrice, fillQuantity);
        var side = "UNKNOWN";
        if (order != null)
        {
            try
            {
                dynamic d = order;
                var action = d.OrderAction;
                if (action != null && action.ToString()?.Contains("Buy") == true) side = "BUY";
                else if (action != null && action.ToString()?.Contains("Sell") == true) side = "SELL";
            }
            catch { }
        }

        _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "EXECUTION_FILLED",
            new
            {
                execution_sequence = seq,
                fill_group_id = fillGroupId,
                order_id = orderIdInternal,
                broker_order_id = brokerOrderId,
                execution_instrument_key = execInstKey,
                side = side,
                order_type = "UNMAPPED",
                fill_price = fillPrice,
                fill_quantity = fillQuantity,
                trading_date = "",
                account = accountName,
                source = "robot",
                mapped = false,
                unmapped_reason = unmappedReason,
                nt_order_name = ntOrderName,
                tag = tag,
                oco_id = ocoId
            }));

        // Phase 2: Emit EXECUTION_GHOST_FILL_DETECTED for institutional anomaly monitoring
        _log.Write(RobotEvents.ExecutionBase(utcNow, "", instrument, "EXECUTION_GHOST_FILL_DETECTED",
            new
            {
                account = accountName,
                instrument = instrument,
                broker_order_id = brokerOrderId,
                order_id = orderIdInternal,
                quantity = fillQuantity,
                price = fillPrice,
                side = side,
                mapped = false,
                reason = unmappedReason,
                stream_key = (string?)null,
                intent_id = (string?)null
            }));
        TryEmitCriticalUnsafeStateDetected(instrument, "unsafe_locked_kill_switch", utcNow);
    }

}

#endif
