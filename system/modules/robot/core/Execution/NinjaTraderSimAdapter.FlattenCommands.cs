using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using QTSW2.Robot.Contracts;
using QTSW2.Robot.Core.Diagnostics;

namespace QTSW2.Robot.Core.Execution;

public sealed partial class NinjaTraderSimAdapter
{
    /// <summary>
    /// REAL RISK FIX: Flatten with retry logic (3 retries, short delay).
    /// Flatten is the last line of defense - if it fails due to transient issues,
    /// we must retry before giving up.
    /// Gap 7: When IEA enabled, route through queue for serialization.
    /// </summary>
    private FlattenResult FlattenWithRetry(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            return _iea.EnqueueFlattenAndWait(() => FlattenWithRetryCore(intentId, instrument, utcNow), 10000);
        }
        return FlattenWithRetryCore(intentId, instrument, utcNow);
    }

    private FlattenResult FlattenWithRetryCore(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        const int MAX_RETRIES = 3;
        const int RETRY_DELAY_MS = 200; // Short delay between retries
        
        FlattenResult? lastResult = null;
        
        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0)
            {
                // Short delay before retry
                System.Threading.Thread.Sleep(RETRY_DELAY_MS);
            }
            
            lastResult = Flatten(intentId, instrument, utcNow);
            
            if (lastResult.Success)
            {
                if (attempt > 0)
                {
                    // Log successful retry
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_RETRY_SUCCEEDED",
                        new
                        {
                            intent_id = intentId,
                            instrument = instrument,
                            attempt = attempt + 1,
                            total_attempts = MAX_RETRIES,
                            note = "Flatten succeeded on retry"
                        }));
                }
                return lastResult;
            }
            
            // Log retry attempt
            if (attempt < MAX_RETRIES - 1)
            {
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_RETRY_ATTEMPT",
                    new
                    {
                        intent_id = intentId,
                        instrument = instrument,
                        attempt = attempt + 1,
                        total_attempts = MAX_RETRIES,
                        error = lastResult.ErrorMessage,
                        note = "Flatten failed, retrying..."
                    }));
            }
        }
        
        // All retries failed - log POSITION_FLATTEN_FAIL_CLOSED and stand down
        var finalError = $"Flatten failed after {MAX_RETRIES} attempts: {lastResult?.ErrorMessage ?? "Unknown error"}";
        
        // Log POSITION_FLATTEN_FAIL_CLOSED at ERROR level (CRITICAL event)
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "POSITION_FLATTEN_FAIL_CLOSED", new
        {
            intent_id = intentId,
            instrument = instrument,
            retry_count = MAX_RETRIES,
            final_error = finalError,
            last_error_message = lastResult?.ErrorMessage ?? "Unknown error",
            note = "All flatten retries exhausted - position may still be open. Manual intervention required."
        }));
        
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAILED_ALL_RETRIES",
            new
            {
                intent_id = intentId,
                instrument = instrument,
                total_attempts = MAX_RETRIES,
                final_error = finalError,
                note = "CRITICAL: Flatten failed after all retries - manual intervention required"
            }));
        
        // Scream loudly: Send emergency notification
        var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
        if (notificationService != null)
        {
            var title = $"EMERGENCY: Flatten Failed After All Retries - {instrument}";
            var message = $"CRITICAL: Flatten failed after {MAX_RETRIES} attempts. Error: {finalError}. Intent: {intentId}. Manual intervention required immediately.";
            notificationService.EnqueueNotification($"FLATTEN_FAILED:{intentId}", title, message, priority: 2); // Emergency priority
        }
        
        // Stand down stream if callback available
        if (IntentMap.TryGetValue(intentId, out var intent))
        {
            _standDownStreamCallback?.Invoke(intent.Stream, utcNow, $"FLATTEN_FAILED_ALL_RETRIES: {finalError}");
        }
        
        return lastResult ?? FlattenResult.FailureResult(finalError, utcNow);
    }

    public FlattenResult Flatten(
        string intentId,
        string instrument,
        DateTimeOffset utcNow)
    {
        if (_isPlaybackStallNtCallBlockedCallback != null && _isPlaybackStallNtCallBlockedCallback())
        {
            const string error = "NT_CALL_BLOCKED:PLAYBACK_STALL_QUIESCENCE";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
            {
                error,
                reason = "PLAYBACK_STALL_QUIESCENCE",
                note = "Playback stall quiescence is armed; blocking NT-touching flatten path."
            }));
            return FlattenResult.FailureResult(error, utcNow);
        }

        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_ATTEMPT", new
        {
            account = "SIM"
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return FlattenResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
{
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED"
        }));
        return FlattenResult.FailureResult(error, utcNow);
}
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET"
            }));
            return FlattenResult.FailureResult(error, utcNow);
        }

        try
        {
            if (_useInstrumentExecutionAuthority && _iea != null)
            {
                // P2.6.6: never call RequestFlatten directly from adapter â€” single funnel via NtFlattenInstrumentCommand.
                var cmd = new NtFlattenInstrumentCommand($"FLATTEN:{intentId}:{utcNow:yyyyMMddHHmmssfff}", intentId, instrument, "FLATTEN_DELEGATED", utcNow,
                    DestructiveActionSource.MANUAL, DestructiveTriggerReason.MANUAL, allowAccountWideCancelFallback: false);
                EnqueueNtActionInternal(cmd);
                return FlattenResult.FailureResult("Enqueued for strategy thread", utcNow);
            }
            return FlattenIntentReal(intentId, instrument, utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
            {
                error = ex.Message,
                account = "SIM",
                exception_type = ex.GetType().Name
            }));
            
            return FlattenResult.FailureResult($"Flatten failed: {ex.Message}", utcNow);
        }
    }

    public int GetCurrentPosition(string instrument)
    {
        if (_isPlaybackStallNtCallBlockedCallback != null && _isPlaybackStallNtCallBlockedCallback())
        {
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new
                {
                    reason = "PLAYBACK_STALL_QUIESCENCE",
                    operation = "GetCurrentPosition",
                    instrument,
                    note = "Playback stall quiescence is armed; returning flat without touching NT account state."
                }));
            return 0;
        }

#if !NINJATRADER
{
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
}
#endif

        if (!_ntContextSet)
        {
            var error = "NT context is not ready yet. " +
                       "SetNTContext() must be called by RobotSimStrategy before broker position can be read.";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_CONTEXT_NOT_READY", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", operation = "GetCurrentPosition", instrument, note = error }));
            throw new InvalidOperationException(error);
        }

        return GetCurrentPositionReal(instrument);
    }
    
    public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
    {
        if (_isPlaybackStallNtCallBlockedCallback != null && _isPlaybackStallNtCallBlockedCallback())
        {
            return new AccountSnapshot
            {
                Positions = new List<PositionSnapshot>(),
                WorkingOrders = new List<WorkingOrderSnapshot>(),
                CapturedAtUtc = utcNow
            };
        }

#if !NINJATRADER
{
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
}
#endif

        if (!_ntContextSet)
        {
            var error = "NT context is not ready yet. " +
                       "SetNTContext() must be called by RobotSimStrategy before broker account snapshot can be read.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_CONTEXT_NOT_READY", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", operation = "GetAccountSnapshot", note = error }));
            throw new InvalidOperationException(error);
        }

        var sw = System.Diagnostics.Stopwatch.StartNew();
        var snap = GetAccountSnapshotReal(utcNow);
        sw.Stop();
        SnapshotMetricsCollector.GetOrCreate(_log).RecordCall(
            DateTimeOffset.UtcNow, sw.ElapsedMilliseconds,
            snap?.Positions?.Count ?? 0, snap?.WorkingOrders?.Count ?? 0);
        return snap;
    }

    /// <summary>
    /// Releasable / protective-relevant intent union: BE monitoring actives plus adoption candidates (same instrument scope).
    /// </summary>
    public IReadOnlyCollection<string> GetActiveIntentIdsForProtectiveAudit(string instrument)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!string.IsNullOrEmpty(instrument))
        {
            foreach (var id in GetOpenIntentIdsForInstrument(instrument))
                set.Add(id);
            foreach (var id in GetAdoptionCandidateIntentIds(instrument))
                set.Add(id);
        }
        return set.Count == 0 ? Array.Empty<string>() : set.ToList();
    }

    /// <inheritdoc cref="IExecutionAdapter.TryRepairTaggedBrokerWithoutJournal"/>
    public bool TryRepairTaggedBrokerWithoutJournal(string instrument, int accountQtyAbs, int journalOpenQtySum, DateTimeOffset utcNow, out string resultCode, out string? detail)
    {
#if !NINJATRADER
        resultCode = "NOT_NINJATRADER_BUILD";
        detail = null;
        return false;
#else
        return TryRepairTaggedBrokerWithoutJournalCore(instrument, accountQtyAbs, journalOpenQtySum, utcNow, out resultCode, out detail);
#endif
    }

    public (decimal? Bid, decimal? Ask) GetCurrentMarketPrice(string instrument, DateTimeOffset utcNow)
    {
#if !NINJATRADER
{
        return (null, null);
}
#endif
        if (!_ntContextSet)
            return (null, null);
        return GetCurrentMarketPriceReal(instrument, utcNow);
    }

    private bool TryExecutionSafetyCancelAuthority(string? instrument, string? intentId, string submitPath, DateTimeOffset utcNow)
    {
        var inst = instrument?.Trim() ?? "";
        var path = string.IsNullOrWhiteSpace(submitPath) ? "CANCEL_SUBMIT" : submitPath.Trim();
        ExecutionPreflightAuthoritySample? preflight = null;
        if (!string.IsNullOrWhiteSpace(inst))
            preflight = SampleExecutionPreflightAuthority(inst, path);

        var frame = ExecutionAuthorityFrameBuilder.Build(new ExecutionAuthorityFrameBuilderInput
        {
            FrameId = ExecutionAuthorityFrame.CreateFrameId(utcNow),
            Source = "adapter_cancel_authority",
            Account = GetLedgerAccountName(),
            Instrument = inst,
            CanonicalInstrument = string.IsNullOrWhiteSpace(inst) ? "" : ResolveCanonicalInstrumentForExecutionSafety(inst, intentId),
            ExecutionInstrumentKey = _iea?.ExecutionInstrumentKey,
            IntentId = intentId ?? "",
            SubmitPath = path,
            ExecutionMode = _authorityExecutionMode.ToString(),
            DecisionUtc = utcNow,
            FrameCreatedUtc = DateTimeOffset.UtcNow,
            PreflightAuthoritySampled = preflight.HasValue,
            PreflightGlobalKillSwitchActive = preflight?.GlobalKillSwitchActive ?? false,
            PreflightMismatchExecutionBlocked = preflight?.MismatchExecutionBlocked ?? false,
            PreflightMismatchExecutionBlockedForSubmit = preflight?.MismatchExecutionBlockedForSubmit,
            PreflightInstrumentFrozenOrEpaBlocked = preflight?.InstrumentFrozenOrEpaBlocked ?? false,
            PreflightInstrumentFrozenOrEpaBlockReason = preflight?.InstrumentFrozenOrEpaBlockReason,
            IsPlayback = _authorityIsPlayback,
            IsMultiDayScenario = _authorityIsMultiDayScenario,
            PlaybackScenarioId = _authorityPlaybackScenarioId
        });
        EmitAuthorityFrameEvaluated(frame, utcNow);

        var decision = UnifiedExecutionAuthority.EvaluateAction(new ExecutionAuthorityActionEvaluationRequest
        {
            Action = ExecutionAuthorityAction.CancelSubmit,
            Source = "NinjaTraderSimAdapter.CancelAuthority",
            Instrument = inst,
            IntentId = intentId ?? "",
            UtcNow = utcNow,
            SnapshotSufficient = true,
            AuthorityFrame = frame
        });
        if (decision.Allowed)
            return true;

        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ORDER_CANCEL_BLOCKED", state: "ENGINE",
            new
            {
                reason = decision.DenyReason ?? "CANCEL_AUTHORITY_DENIED",
                authority_gate = decision.GateName,
                authority_frame_id = decision.AuthorityFrame?.FrameId,
                instrument = inst,
                intent_id = intentId ?? "",
                submit_path = path,
                note = decision.Detail
            }));
        return false;
    }
    
    public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        if (_isPlaybackStallNtCallBlockedCallback != null && _isPlaybackStallNtCallBlockedCallback())
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ORDER_CANCEL_BLOCKED", state: "ENGINE",
                new
                {
                    reason = "PLAYBACK_STALL_QUIESCENCE",
                    note = "Playback stall quiescence is armed; blocking NT-touching cancel path."
                }));
            return;
        }

#if !NINJATRADER
{
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
}
#endif

        if (!_ntContextSet)
        {
            var error = "NT context is not ready yet. " +
                       "SetNTContext() must be called by RobotSimStrategy before broker working orders can be cancelled.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_CONTEXT_NOT_READY", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", operation = "CancelRobotOwnedWorkingOrders", note = error }));
            throw new InvalidOperationException(error);
        }

        if (!TryExecutionSafetyCancelAuthority(null, null, "CANCEL_ROBOT_ORDERS", utcNow))
            return;

        CancelRobotOwnedWorkingOrdersReal(snap, utcNow, instrumentRootForScope: null, explicitBrokerOrderIds: null, allowAccountWideCancelFallback: false, correlationId: null);
    }

    public void CancelOrders(IEnumerable<string> orderIds, DateTimeOffset utcNow)
    {
        if (_isPlaybackStallNtCallBlockedCallback != null && _isPlaybackStallNtCallBlockedCallback())
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ORDER_CANCEL_BLOCKED", state: "ENGINE",
                new
                {
                    reason = "PLAYBACK_STALL_QUIESCENCE",
                    note = "Playback stall quiescence is armed; blocking NT-touching cancel path."
                }));
            return;
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#else
        var ids = orderIds?.ToList() ?? new List<string>();
        if (!TryExecutionSafetyCancelAuthority(null, null, "CANCEL_ORDERS", utcNow))
            return;

        if (!IsStrategyThreadContext())
        {
            if (_ntActionQueue != null)
            {
                var cid = $"CANCEL_ORDERS:{utcNow:yyyyMMddHHmmssfff}:{ids.Count}";
                _ntActionQueue.EnqueueNtAction(
                    new NtDeferredAction(cid, null, null, "PUBLIC_CANCEL_ORDERS", () => CancelOrdersReal(ids, utcNow)),
                    out _);
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ENTRY_ORDER_SET_CANCEL_ENQUEUED", state: "ENGINE",
                    new
                    {
                        order_count = ids.Count,
                        order_ids = ids,
                        correlation_id = cid,
                        note = "CancelOrders requested off strategy thread; queued onto strategy thread before touching NT account/orders."
                    }));
                return;
            }

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "ORDER_CANCEL_BLOCKED", state: "ENGINE",
                new
                {
                    reason = "NT_ACTION_QUEUE_UNAVAILABLE",
                    order_count = ids.Count,
                    note = "CancelOrders requested off strategy thread but NT action queue is unavailable."
                }));
            return;
        }

        CancelOrdersReal(ids, utcNow);
#endif
    }

    /// <summary>
    /// Check all instruments for flat positions and cancel entry stops.
    /// Called on every execution update to detect manual position closures.
    /// </summary>
    private void CheckAllInstrumentsForFlatPositions(DateTimeOffset utcNow)
    {
        OnCheckAllInstrumentsForFlatPositions(utcNow);
    }

    partial void OnCheckAllInstrumentsForFlatPositions(DateTimeOffset utcNow);
    
    /// <summary>
    /// Check for unprotected positions and flatten if protectives not acknowledged within timeout.
    /// </summary>
    private void CheckUnprotectedPositions(DateTimeOffset utcNow)
    {
        const double UNPROTECTED_POSITION_TIMEOUT_SECONDS = 10.0;
        
        foreach (var kvp in OrderMap)
        {
            var orderInfo = kvp.Value;
            
            // Only check entry orders that are filled
            if (!orderInfo.IsEntryOrder || orderInfo.State != "FILLED" || !orderInfo.EntryFillTime.HasValue)
                continue;
            
            var elapsed = (utcNow - orderInfo.EntryFillTime.Value).TotalSeconds;
            
            // Check if timeout exceeded and protectives not acknowledged
            if (elapsed > UNPROTECTED_POSITION_TIMEOUT_SECONDS)
            {
                if (!orderInfo.ProtectiveStopAcknowledged || !orderInfo.ProtectiveTargetAcknowledged)
                {
                    // Flatten position and stand down stream
                    var intentId = orderInfo.IntentId;
                    var instrument = orderInfo.Instrument;
                    
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "UNPROTECTED_POSITION_TIMEOUT",
                        new
                        {
                            intent_id = intentId,
                            instrument = instrument,
                            elapsed_seconds = elapsed,
                            protective_stop_acknowledged = orderInfo.ProtectiveStopAcknowledged,
                            protective_target_acknowledged = orderInfo.ProtectiveTargetAcknowledged,
                            timeout_seconds = UNPROTECTED_POSITION_TIMEOUT_SECONDS,
                            note = "Position unprotected beyond timeout - flattening and standing down stream"
                        }));
                    
                    // Get intent to find stream
                    if (IntentMap.TryGetValue(intentId, out var intent))
                    {
                        // SIMPLIFICATION: Use centralized fail-closed pattern
                        var failureReason = $"Unprotected position timeout ({UNPROTECTED_POSITION_TIMEOUT_SECONDS}s) - protective orders not acknowledged";
                        FailClosed(
                            intentId,
                            intent,
                            failureReason,
                            "UNPROTECTED_POSITION_TIMEOUT_FLATTENED",
                            $"UNPROTECTED_TIMEOUT:{intentId}",
                            $"CRITICAL: Unprotected Position Timeout - {instrument}",
                            $"Entry filled but protective orders not acknowledged within {UNPROTECTED_POSITION_TIMEOUT_SECONDS} seconds. Position flattened. Stream: {intent.Stream}, Intent: {intentId}",
                            null, // stopResult
                            null, // targetResult
                            new 
                            { 
                                elapsed_seconds = elapsed,
                                protective_stop_acknowledged = orderInfo.ProtectiveStopAcknowledged,
                                protective_target_acknowledged = orderInfo.ProtectiveTargetAcknowledged,
                                timeout_seconds = UNPROTECTED_POSITION_TIMEOUT_SECONDS
                            }, // additionalData
                            utcNow);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Trigger quantity emergency handler (idempotent).
    /// Cancels orders, flattens intent, and stands down stream.
    /// </summary>
    private void TriggerQuantityEmergency(string intentId, string emergencyType, DateTimeOffset utcNow, Dictionary<string, object> details)
    {
        // Idempotent: only trigger once per intent unless reason changes
        if (_emergencyTriggered.Contains(intentId))
        {
            // Already triggered - log but don't repeat actions
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", 
                $"{emergencyType}_REPEAT", new
            {
                intent_id = intentId,
                note = "Emergency already triggered for this intent"
            }));
            return;
        }
        
        _emergencyTriggered.Add(intentId);
        
        // Cancel all remaining intent orders
        CancelIntentOrders(intentId, utcNow);
        
        // Flatten intent exposure with retry logic
        if (IntentMap.TryGetValue(intentId, out var intent))
        {
            FlattenWithRetry(intentId, intent.Instrument, utcNow);
        }
        
        // Emit emergency log
        var emergencyPayload = new Dictionary<string, object>(details)
        {
            { "intent_id", intentId },
            { "action_taken", new[] { "CANCEL_ORDERS", "FLATTEN_INTENT" } }
        };
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, "", emergencyType, emergencyPayload));
        
        // Emit notification (highest priority)
        var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
        if (notificationService != null)
        {
            var title = $"EMERGENCY: {emergencyType} - {intentId}";
            var reason = details.TryGetValue("reason", out var reasonObj) ? reasonObj?.ToString() ?? "Unknown" : "Unknown";
            var message = $"Quantity invariant violated: {reason}. Orders cancelled, position flattened.";
            notificationService.EnqueueNotification($"{emergencyType}:{intentId}", title, message, priority: 2); // Emergency priority
        }
        
        // Stand down stream/intent (via callback)
        if (IntentMap.TryGetValue(intentId, out var intentForCallback))
        {
            _standDownStreamCallback?.Invoke(intentForCallback.Stream, utcNow, emergencyType);
        }
    }
    
    /// <summary>
    /// Get active intents that need break-even monitoring.
    /// Returns intents with filled entries that haven't had BE triggered yet.
    /// 
    /// CRITICAL FIX: Check execution journal instead of OrderMap because protective orders
    /// overwrite entry orders in OrderMap (entry order is replaced with stop/target orders).
    /// Execution journal is the source of truth for entry fill status.
    /// </summary>
    /// <param name="executionInstrument">Optional. When provided, only return intents for this execution instrument (e.g. MES, MYM).
    /// CRITICAL: Each strategy instance receives ticks for ONE instrument only. Without this filter, we'd compare MES tick price
    /// against YM/GC/NG intents (wrong price scales) causing false triggers or missed triggers.</param>
    public List<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> GetActiveIntentsForBEMonitoring()
    {
        return GetActiveIntentsForBEMonitoring(null);
    }

    public List<(string intentId, Intent intent, decimal beTriggerPrice, decimal entryPrice, decimal? actualFillPrice, string direction)> GetActiveIntentsForBEMonitoring(string? executionInstrument)
    {
        var activeIntents = new List<(string, Intent, decimal, decimal, decimal?, string)>();
        
        // CRITICAL FIX: Iterate over IntentMap instead of OrderMap
        // OrderMap gets overwritten by protective orders (stop/target), so entry orders are lost
        // Execution journal is the authoritative source for entry fill status
        foreach (var kvp in IntentMap)
        {
            var intentId = kvp.Key;
            var intent = kvp.Value;
            
            // CRITICAL FIX: Filter by execution instrument - each strategy only gets ticks for its chart.
            // Use IsSameInstrument for alias resolution (M2Kâ†”RTY, MESâ†”ES, MNQâ†”NQ) so BE does not return 0
            // when a real live position exists for the monitored execution instrument.
            if (!string.IsNullOrEmpty(executionInstrument) &&
                !ExecutionInstrumentResolver.IsSameInstrument(intent.ExecutionInstrument, executionInstrument))
                continue;
            
            // Check if intent has required fields for BE monitoring
            if (intent.BeTrigger == null || intent.EntryPrice == null || intent.Direction == null)
                continue;
            
            // Check if entry has been filled using execution journal (source of truth)
            // Execution journal tracks entry fills regardless of OrderMap state
            var tradingDate = intent.TradingDate ?? "";
            var stream = intent.Stream ?? "";
            
            ExecutionJournalEntry? journalEntry = null;
            try
            {
                journalEntry = _executionJournal.GetEntry(intentId, tradingDate, stream);
            }
            catch
            {
                // If journal lookup fails, skip this intent
                continue;
            }
            
            // Only check intents with filled entries
            if (journalEntry == null || !journalEntry.EntryFilled || journalEntry.EntryFilledQuantityTotal <= 0)
                continue;
            
            // ZOMBIE_STOP FIX: Never place or modify BE for completed trades - prevents zombie stop orders
            // that can fill after target/stop hit, creating extra positions not in journal (QTY_MISMATCH)
            if (journalEntry.TradeCompleted)
                continue;
            
            // TERMINAL_INTENT HARDENING: Only include intents with remaining open quantity
            if (journalEntry.ExitFilledQuantityTotal >= journalEntry.EntryFilledQuantityTotal)
                continue;
            
            // Check if BE has already been triggered (idempotency check)
            if (_executionJournal.IsBEModified(intentId, tradingDate, stream))
                continue;

            // Skip if pending modify (waiting for confirmation)
            if (HasPendingBEForIntent(intentId))
                continue;
            
            // Get actual fill price from execution journal for logging/debugging purposes
            // NOTE: BE stop uses breakout level (entryPrice), not actual fill price
            // The breakout level is the strategic entry point, slippage shouldn't affect BE stop placement
            decimal? actualFillPrice = journalEntry.FillPrice;
            
            // entryPrice is the breakout level (brkLong/brkShort for stop orders, limit price for limit orders)
            activeIntents.Add((intentId, intent, intent.BeTrigger.Value, intent.EntryPrice.Value, actualFillPrice, intent.Direction));
        }
        
        return activeIntents;
    }

    public IReadOnlyCollection<string> GetOpenIntentIdsForInstrument(string instrument)
    {
        var openIntentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(instrument))
            return openIntentIds;

        foreach (var kvp in IntentMap)
        {
            var intentId = kvp.Key;
            var intent = kvp.Value;
            if (!ExecutionInstrumentResolver.IsSameInstrument(intent.ExecutionInstrument, instrument))
                continue;

            var tradingDate = intent.TradingDate ?? "";
            var stream = intent.Stream ?? "";
            ExecutionJournalEntry? journalEntry;
            try
            {
                journalEntry = _executionJournal.GetEntry(intentId, tradingDate, stream);
            }
            catch
            {
                continue;
            }

            if (journalEntry == null || !journalEntry.EntryFilled || journalEntry.EntryFilledQuantityTotal <= 0)
                continue;
            if (journalEntry.TradeCompleted)
                continue;
            if (journalEntry.ExitFilledQuantityTotal >= journalEntry.EntryFilledQuantityTotal)
                continue;

            openIntentIds.Add(intentId);
        }

        return openIntentIds;
    }

    /// <summary>
    /// Get intent IDs that are adoption candidates for restart recovery.
    /// Uses execution journal EntrySubmitted (includes unfilled entry stops) â€” separate from BE monitoring.
    /// </summary>
    public IReadOnlyCollection<string> GetAdoptionCandidateIntentIds(string? executionInstrument)
    {
        if (string.IsNullOrEmpty(executionInstrument)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var execVariant = executionInstrument.StartsWith("M", StringComparison.OrdinalIgnoreCase) && executionInstrument.Length > 1 ? executionInstrument : "M" + executionInstrument;
        var canonical = DeriveCanonicalFromExecutionInstrument(executionInstrument);
        var ids = _executionJournal.GetAdoptionCandidateIntentIdsForInstrument(executionInstrument, canonical);
        var idsVariant = _executionJournal.GetAdoptionCandidateIntentIdsForInstrument(execVariant, canonical);
        foreach (var id in idsVariant) ids.Add(id);
        return ids;
    }

    public int ReopenBrokerFlatCompletedJournalsForCarryover(
        string? executionInstrument,
        IReadOnlyDictionary<string, int> workingIntentOpenQtyByIntent,
        DateTimeOffset utcNow,
        string triggerSource)
    {
        if (string.IsNullOrWhiteSpace(executionInstrument) ||
            workingIntentOpenQtyByIntent == null ||
            workingIntentOpenQtyByIntent.Count == 0)
            return 0;

        var canonical = DeriveCanonicalFromExecutionInstrument(executionInstrument);
        return _executionJournal.ReopenBrokerFlatCompletedJournalRowsForCarryover(
            executionInstrument,
            canonical,
            workingIntentOpenQtyByIntent,
            utcNow,
            triggerSource);
    }

    /// <summary>
    /// Get journal visibility diagnostics for adoption deferral logging.
    /// </summary>
    public (string JournalDir, int FileCount, bool DirectoryExists) GetJournalDiagnostics(string? executionInstrument)
    {
        var (exists, count, dir) = _executionJournal.GetJournalDiagnostics();
        return (dir, count, exists);
    }

    private static string DeriveCanonicalFromExecutionInstrument(string execInst)
    {
        if (string.IsNullOrEmpty(execInst)) return "";
        var u = execInst.ToUpperInvariant();
        if (u.StartsWith("MES")) return "ES";
        if (u.StartsWith("MNQ")) return "NQ";
        if (u.StartsWith("MYM")) return "YM";
        if (u.StartsWith("MGC")) return "GC";
        if (u.StartsWith("MCL")) return "CL";
        if (u.StartsWith("MNG")) return "NG";
        if (u.StartsWith("M2K")) return "RTY";
        return execInst.Length >= 2 ? execInst.Substring(0, 2).ToUpperInvariant() : execInst;
    }

    /// <summary>
    /// Get count of open journal entries for an instrument (for BE_ACCOUNT_EXPOSURE_WITHOUT_INTENT).
    /// </summary>
    public int GetOpenJournalCountForInstrument(string executionInstrument, string? canonicalInstrument = null)
    {
        return _executionJournal.GetOpenJournalCountForInstrument(executionInstrument, canonicalInstrument);
    }
    
    /// <summary>
    /// Cancel orders for a specific intent only.
    /// </summary>
    public bool CancelIntentOrders(string intentId, DateTimeOffset utcNow)
    {
        if (!_simAccountVerified)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_BLOCKED", state: "ENGINE",
                new { intent_id = intentId, reason = "SIM_ACCOUNT_NOT_VERIFIED" }));
            return false;
        }
        
#if !NINJATRADER
{
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_BLOCKED", state: "ENGINE",
            new { intent_id = intentId, reason = "NINJATRADER_NOT_DEFINED", error }));
        return false;
}
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_BLOCKED", state: "ENGINE",
                new { intent_id = intentId, reason = "NT_CONTEXT_NOT_SET", error }));
            return false;
        }

        var cancelInstrument = "";
        if (!string.IsNullOrWhiteSpace(intentId) &&
            IntentMap.TryGetValue(intentId, out var cancelIntent))
        {
            cancelInstrument = string.IsNullOrWhiteSpace(cancelIntent.ExecutionInstrument)
                ? cancelIntent.Instrument ?? ""
                : cancelIntent.ExecutionInstrument ?? "";
        }
        if (!TryExecutionSafetyCancelAuthority(cancelInstrument, intentId, "CANCEL_INTENT_ORDERS", utcNow))
            return false;

#if NINJATRADER
        if (!IsStrategyThreadContext())
        {
            if (_ntActionQueue != null)
            {
                var cid = $"CANCEL_INTENT:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
                _ntActionQueue.EnqueueNtAction(
                    new NtDeferredAction(cid, intentId, null, "PUBLIC_CANCEL_INTENT_ORDERS", () => CancelIntentOrdersReal(intentId, utcNow)),
                    out _);
                _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_ENQUEUED", state: "ENGINE",
                    new
                    {
                        intent_id = intentId,
                        correlation_id = cid,
                        note = "CancelIntentOrders requested off strategy thread; queued onto strategy thread before touching NT account/orders."
                    }));
                return true;
            }

            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_BLOCKED", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    reason = "NT_ACTION_QUEUE_UNAVAILABLE",
                    note = "CancelIntentOrders requested off strategy thread but NT action queue is unavailable."
                }));
            return false;
        }
#endif

        try
        {
            return CancelIntentOrdersReal(intentId, utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_ERROR", state: "ENGINE",
                new
                {
                    intent_id = intentId,
                    error = ex.Message,
                    exception_type = ex.GetType().Name
                }));
            return false;
        }
    }
    
    /// <summary>
    /// Flatten exposure for a specific intent only.
    /// </summary>
    public FlattenResult FlattenIntent(string intentId, string instrument, DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_ATTEMPT", state: "ENGINE",
            new
            {
                intent_id = intentId,
                instrument = instrument
            }));
        
        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return FlattenResult.FailureResult(error, utcNow);
        }
        
#if !NINJATRADER
{
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_ERROR", state: "ENGINE",
            new { intent_id = intentId, instrument, reason = "NINJATRADER_NOT_DEFINED", error }));
        return FlattenResult.FailureResult(error, utcNow);
}
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_ERROR", state: "ENGINE",
                new { intent_id = intentId, instrument, reason = "NT_CONTEXT_NOT_SET", error }));
            return FlattenResult.FailureResult(error, utcNow);
        }

        // Use retry logic for FlattenIntent as well
        return FlattenWithRetry(intentId, instrument, utcNow);
    }

}
