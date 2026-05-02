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
    /// PHASE 2: Handle entry fill and submit protective orders with retry and failure recovery.
    /// </summary>
    private void HandleEntryFill(string intentId, Intent intent, decimal fillPrice, int fillQuantity, int totalFilledQuantity, DateTimeOffset utcNow)
    {
        // CRITICAL FIX: totalFilledQuantity is the cumulative filled quantity (passed from caller)
        // For incremental fills, protective orders must cover the ENTIRE position, not just the new fill
        // This prevents position accumulation bugs where protective orders only cover the delta
        
        // Record entry fill time for watchdog tracking
        if (OrderMap.TryGetValue(intentId, out var entryOrderInfo))
        {
            entryOrderInfo.EntryFillTime = utcNow;
            entryOrderInfo.ProtectiveStopAcknowledged = false;
            entryOrderInfo.ProtectiveTargetAcknowledged = false;
        }
        
        // CRITICAL: Validate intent has all required fields for protective orders
        // REAL RISK FIX: Treat missing intent fields the same as protective submission failure
        // If we cannot prove the position is protected, flatten immediately (fail-closed)
        var missingFields = new List<string>();
        if (intent.Direction == null) missingFields.Add("Direction");
        if (intent.StopPrice == null) missingFields.Add("StopPrice");
        if (intent.TargetPrice == null) missingFields.Add("TargetPrice");
        
        if (missingFields.Count > 0)
        {
            var failureReason = $"Intent incomplete - missing fields: {string.Join(", ", missingFields)}";
            
            // Log critical error
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "INTENT_INCOMPLETE_UNPROTECTED_POSITION",
                new 
                { 
                    error = failureReason,
                    intent_id = intentId,
                    missing_fields = missingFields,
                    direction = intent.Direction,
                    stop_price = intent.StopPrice,
                    target_price = intent.TargetPrice,
                    fill_price = fillPrice,
                    fill_quantity = fillQuantity,
                    total_filled_quantity = totalFilledQuantity,
                    stream = intent.Stream,
                    instrument = intent.Instrument,
                    action = "FLATTEN_IMMEDIATELY",
                    note = "Entry order filled but intent incomplete - position unprotected. Flattening immediately (fail-closed behavior)."
                }));
            
            // SIMPLIFICATION: Use centralized fail-closed pattern
            FailClosed(
                intentId,
                intent,
                failureReason,
                "INTENT_INCOMPLETE_FLATTENED",
                $"INTENT_INCOMPLETE:{intentId}",
                $"CRITICAL: Intent Incomplete - Unprotected Position - {intent.Instrument}",
                $"Entry filled but intent incomplete (missing: {string.Join(", ", missingFields)}). Position flattened. Stream: {intent.Stream}, Intent: {intentId}.",
                null, // stopResult
                null, // targetResult
                new { missing_fields = missingFields }, // additionalData
                utcNow);
            
            return;
        }
        
        // Stage 1: Entry fill during recovery â€” queue protective submission (three-stage safety model)
        if (_isExecutionAllowedCallback != null && !_isExecutionAllowedCallback())
        {
            if (Volatile.Read(ref _sessionMismatchBlocked) != 0)
                return;
            QueueProtectiveForRecovery(intentId, intent, totalFilledQuantity, utcNow);
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_QUEUED_RECOVERY",
                new
                {
                    intent_id = intentId,
                    fill_quantity = fillQuantity,
                    fill_price = fillPrice,
                    total_filled_quantity = totalFilledQuantity,
                    timeout_seconds = RECOVERY_PROTECTIVE_TIMEOUT_SECONDS,
                    note = "Protective orders queued for submission after recovery; fail-safe flatten if not placed within timeout"
                }));
            return;
        }
        
        // Validate exit orders before submission
        // CRITICAL FIX: Use totalFilledQuantity (cumulative) for validation
        // Protective orders must cover the ENTIRE position, not just the new fill
        if (_coordinator != null)
        {
            if (!_coordinator.CanSubmitExit(intentId, totalFilledQuantity))
            {
                var error = "Exit validation failed - cannot submit protective orders";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "EXECUTION_ERROR",
                    new { 
                        error = error, 
                        intent_id = intentId, 
                        fill_quantity = fillQuantity,
                        total_filled_quantity = totalFilledQuantity
                    }));
                return;
            }
        }
        
        // PHASE 2: Submit protective orders with retry
        // CRITICAL FIX: Generate unique OCO group for each retry attempt
        // NinjaTrader does not allow reusing OCO IDs once they've been used (even if rejected)
        // Both stop and target must use the same OCO group to be paired, so we retry both together
        const int MAX_RETRIES = 3;
        const int RETRY_DELAY_MS = 100;

        SetProtectionState(intentId, ProtectionState.Executing);
        OrderSubmissionResult? stopResult = null;
        OrderSubmissionResult? targetResult = null;
        
        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0)
            {
                System.Threading.Thread.Sleep(RETRY_DELAY_MS);
            }
            
            // Validate again before each retry
            // CRITICAL FIX: Use totalFilledQuantity (cumulative) for validation and protective orders
            // Protective orders must cover the ENTIRE position, not just the new fill
            if (_coordinator != null && !_coordinator.CanSubmitExit(intentId, totalFilledQuantity))
            {
                var error = "Exit validation failed during retry";
                stopResult = OrderSubmissionResult.FailureResult(error, utcNow);
                targetResult = OrderSubmissionResult.FailureResult(error, utcNow);
                break;
            }
            
            // SIMPLIFICATION: Use centralized OCO group generation
            // Generate unique OCO group for this attempt (append attempt number and timestamp)
            // Both stop and target must use the same OCO group to be paired
            var protectiveOcoGroup = GenerateProtectiveOcoGroup(intentId, attempt, utcNow);
            
            // Submit stop order
            // CRITICAL FIX: Use totalFilledQuantity (cumulative) for protective orders
            // This ensures protective orders cover the ENTIRE position for incremental fills
            stopResult = SubmitProtectiveStop(
                intentId,
                intent.Instrument,
                intent.Direction,
                intent.StopPrice.Value,
                totalFilledQuantity,  // CRITICAL: Use total, not delta
                protectiveOcoGroup,
                utcNow);
            
            // Only submit target if stop succeeded (they must be OCO paired)
            if (stopResult.Success)
            {
                targetResult = SubmitTargetOrder(
                    intentId,
                    intent.Instrument,
                    intent.Direction,
                    intent.TargetPrice.Value,
                    totalFilledQuantity,  // CRITICAL: Use total, not delta
                    protectiveOcoGroup,
                    utcNow);
                
                // If both succeeded, we're done
                if (targetResult.Success)
                {
                    SetProtectionState(intentId, ProtectionState.Submitted);
                    break;
                }
                
                // If target failed but stop succeeded, we need to cancel stop and retry both
                // Cancel the stop order before retrying (OCO pairing broken)
                if (stopResult.BrokerOrderId != null)
                {
                    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_RETRY_CANCEL_STOP",
                        new
                        {
                            attempt = attempt + 1,
                            reason = "Target submission failed - canceling stop to retry both as OCO pair",
                            stop_broker_order_id = stopResult.BrokerOrderId
                        }));
                    // Note: Stop will be canceled by NinjaTrader when we submit new orders, or we could explicitly cancel
                    // For now, continue to next retry attempt with new OCO group
                }
            }
            // If stop failed, continue to next retry attempt
        }
        
        // PHASE 2: If either protective leg failed after retries, flatten position and stand down stream
        if (!stopResult.Success || !targetResult.Success)
        {
            PruneIntentState(intentId, "protective_orders_failed");
            var failedLegs = new List<string>();
            if (!stopResult.Success) failedLegs.Add($"STOP: {stopResult.ErrorMessage}");
            if (!targetResult.Success) failedLegs.Add($"TARGET: {targetResult.ErrorMessage}");
            
            var failureReason = $"Protective orders failed after {MAX_RETRIES} retries: {string.Join(", ", failedLegs)}";
            
            // SIMPLIFICATION: Use centralized fail-closed pattern
            FailClosed(
                intentId,
                intent,
                failureReason,
                "PROTECTIVE_ORDERS_FAILED_FLATTENED",
                $"PROTECTIVE_FAILURE:{intentId}",
                $"CRITICAL: Protective Order Failure - {intent.Instrument}",
                $"Entry filled but protective orders failed. Position flattened. Stream: {intent.Stream}, Intent: {intentId}. Failures: {failureReason}",
                stopResult,
                targetResult,
                new { retry_count = MAX_RETRIES }, // additionalData
                utcNow);
            
            return;
        }
        
        // Log protective orders submitted successfully
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_SUBMITTED",
            new
            {
                stop_order_id = stopResult.BrokerOrderId,
                target_order_id = targetResult.BrokerOrderId,
                stop_price = intent.StopPrice,
                target_price = intent.TargetPrice,
                fill_quantity = fillQuantity,  // Delta for this fill
                total_filled_quantity = totalFilledQuantity,  // Cumulative total for protective orders
                note = "Protective orders submitted for total filled quantity (covers entire position for incremental fills)"
            }));

        _onMismatchExecutionTrigger?.Invoke(intent.Instrument!.Trim(), utcNow, new MismatchExecutionTriggerDetails
        {
            IntentId = intentId,
            EntryToProtectivesTransition = true
        });

        // Check for unprotected positions after protective order submission
        CheckUnprotectedPositions(utcNow);
        
        // CRITICAL FIX: Check all instruments for flat positions and cancel entry stops
        // This detects manual position closures that bypass robot code
        // Called on every execution update to catch manual flattens quickly
        CheckAllInstrumentsForFlatPositions(utcNow);

        // Proof log: unambiguous, includes encoded envelope and decoded identity
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVES_PLACED", new
        {
            intent_id = intentId,
            encoded_entry_tag = RobotOrderIds.EncodeTag(intentId),
            stop_tag = RobotOrderIds.EncodeStopTag(intentId),
            target_tag = RobotOrderIds.EncodeTargetTag(intentId),
            order_type = "ENTRY_OR_ENTRY_STOP",
            stop_price = intent.StopPrice,
            target_price = intent.TargetPrice,
            fill_quantity = fillQuantity,  // Delta for this fill
            protected_quantity = totalFilledQuantity,  // Cumulative total protected
            note = "Protective stop + target successfully placed/ensured for total filled quantity (covers entire position for incremental fills)"
        }));
    }
    
    /// <summary>
    /// SIMPLIFICATION: Generate unique OCO group for protective orders.
    /// NinjaTrader does not allow reusing OCO IDs once they've been used (even if rejected),
    /// so we generate a unique group per retry attempt.
    /// </summary>
    private string GenerateProtectiveOcoGroup(string intentId, int attempt, DateTimeOffset utcNow)
    {
        return $"QTSW2:{intentId}_PROTECTIVE_A{attempt}_{utcNow:HHmmssfff}";
    }
    
    /// <summary>
    /// SIMPLIFICATION: Fail-closed pattern - flatten position, stand down stream, alert, and persist incident.
    /// Centralizes the fail-closed behavior used in multiple places (intent incomplete, recovery blocked, protective failure, unprotected timeout).
    /// </summary>
    private void FailClosed(
        string intentId,
        Intent intent,
        string failureReason,
        string eventType,
        string notificationKey,
        string notificationTitle,
        string notificationMessage,
        OrderSubmissionResult? stopResult,
        OrderSubmissionResult? targetResult,
        object? additionalData,
        DateTimeOffset utcNow)
    {
        // Notify coordinator of protective failure
        _coordinator?.OnProtectiveFailure(intentId, intent.Stream, utcNow);

        // P2.6.6: Any fail-closed flatten uses NtFlattenInstrumentCommand â†’ policy (FAIL_CLOSED trigger), not Flattenâ†’RequestFlatten.
        if (_ntActionQueue != null)
        {
            var cid = $"FAILCLOSED:{intentId}:{utcNow:yyyyMMddHHmmssfff}";
            _ntActionQueue.EnqueueNtAction(new NtCancelOrdersCommand(cid, intentId, intent.Instrument, false, failureReason, utcNow), out _);
            EnqueueNtActionInternal(new NtFlattenInstrumentCommand(cid + ":F", intentId, intent.Instrument ?? "", failureReason, utcNow,
                DestructiveActionSource.FAIL_CLOSED, DestructiveTriggerReason.FAIL_CLOSED, allowAccountWideCancelFallback: false));
            _standDownStreamCallback?.Invoke(intent.Stream, utcNow, $"{eventType}: {failureReason}");
            PersistProtectiveFailureIncident(intentId, intent, stopResult, targetResult, FlattenResult.FailureResult("Enqueued for strategy thread", utcNow), utcNow);
            var notificationSvc = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
            if (notificationSvc != null)
                notificationSvc.EnqueueNotification(notificationKey, notificationTitle, notificationMessage, priority: 2);
            var logDataIea = new Dictionary<string, object>
            {
                { "intent_id", intentId }, { "stream", intent.Stream }, { "instrument", intent.Instrument },
                { "failure_reason", failureReason }, { "note", "Fail-closed: NT actions enqueued for strategy thread" }
            };
            if (_iea != null) { logDataIea["iea_instance_id"] = _iea.InstanceId; logDataIea["execution_instrument_key"] = _iea.ExecutionInstrumentKey; }
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, eventType, logDataIea));
            return;
        }

        // P2.6.6: no Flattenâ†’RequestFlatten chain; direct path still hits policy in FlattenIntentReal (FAIL_CLOSED).
        var flattenResult = FlattenIntentReal(intentId, intent.Instrument ?? "", utcNow,
            destructiveSourceOverride: DestructiveActionSource.FAIL_CLOSED,
            explicitTriggerOverride: DestructiveTriggerReason.FAIL_CLOSED);
        
        // Stand down stream
        _standDownStreamCallback?.Invoke(intent.Stream, utcNow, $"{eventType}: {failureReason}");
        
        // Persist incident record
        PersistProtectiveFailureIncident(intentId, intent, stopResult, targetResult, flattenResult, utcNow);
        
        // Raise high-priority alert
        var notificationSvc2 = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
        if (notificationSvc2 != null)
        {
            notificationSvc2.EnqueueNotification(notificationKey, notificationTitle, notificationMessage, priority: 2); // Emergency priority
        }
        
        // Log final flattened event
        var logDataFlatten = new Dictionary<string, object>
        {
            { "intent_id", intentId },
            { "stream", intent.Stream },
            { "instrument", intent.Instrument },
            { "flatten_success", flattenResult.Success },
            { "flatten_error", flattenResult.ErrorMessage },
            { "failure_reason", failureReason },
            { "note", "Position flattened and stream stood down (fail-closed behavior)" }
        };
        
        if (stopResult != null)
        {
            logDataFlatten["stop_success"] = stopResult.Success;
            logDataFlatten["stop_error"] = stopResult.ErrorMessage;
        }
        if (targetResult != null)
        {
            logDataFlatten["target_success"] = targetResult.Success;
            logDataFlatten["target_error"] = targetResult.ErrorMessage;
        }
        if (additionalData != null)
        {
            // Merge additional data into log
            var props = additionalData.GetType().GetProperties();
            foreach (var prop in props)
            {
                logDataFlatten[prop.Name] = prop.GetValue(additionalData);
            }
        }

        // Gap 3: Ensure CRITICAL logs include IEA context when IEA enabled.
        if (_useInstrumentExecutionAuthority && _iea != null)
        {
            logDataFlatten["iea_instance_id"] = _iea.InstanceId;
            logDataFlatten["execution_instrument_key"] = _iea.ExecutionInstrumentKey;
        }
        
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, eventType, logDataFlatten));
    }
    
    /// <summary>
    /// PHASE 2: Persist protective order failure incident record.
    /// </summary>
    private void PersistProtectiveFailureIncident(
        string intentId,
        Intent intent,
        OrderSubmissionResult? stopResult,
        OrderSubmissionResult? targetResult,
        FlattenResult flattenResult,
        DateTimeOffset utcNow)
    {
        try
        {
            var incidentDir = System.IO.Path.Combine(_stateRoot, "data", "execution_incidents");
            System.IO.Directory.CreateDirectory(incidentDir);
            
            var incidentPath = System.IO.Path.Combine(incidentDir, $"protective_failure_{intentId}_{utcNow:yyyyMMddHHmmss}.json");
            
            var incident = new
            {
                incident_type = "PROTECTIVE_ORDER_FAILURE",
                timestamp_utc = utcNow.ToString("o"),
                intent_id = intentId,
                trading_date = intent.TradingDate,
                stream = intent.Stream,
                instrument = intent.Instrument,
                session = intent.Session,
                direction = intent.Direction,
                entry_price = intent.EntryPrice,
                stop_price = intent.StopPrice,
                target_price = intent.TargetPrice,
                stop_result = stopResult != null ? new { success = stopResult.Success, error = stopResult.ErrorMessage, broker_order_id = stopResult.BrokerOrderId } : null,
                target_result = targetResult != null ? new { success = targetResult.Success, error = targetResult.ErrorMessage, broker_order_id = targetResult.BrokerOrderId } : null,
                flatten_result = new { success = flattenResult.Success, error = flattenResult.ErrorMessage },
                action_taken = "POSITION_FLATTENED_STREAM_STOOD_DOWN"
            };
            
            var json = JsonUtil.Serialize(incident);
            System.IO.File.WriteAllText(incidentPath, json);
        }
        catch (Exception ex)
        {
            // Log error but don't fail execution
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "INCIDENT_PERSIST_ERROR",
                new { error = ex.Message, exception_type = ex.GetType().Name }));
        }
    }
    
    /// <summary>
    /// Register intent for fill callback handling.
    /// Called by StreamStateMachine or test inject.
    /// </summary>
    public void RegisterIntent(Intent intent)
    {
        if (!Intent.TryValidateRegistrationPrerequisites(intent, out var registrationFailureReason))
        {
            var utcNow = DateTimeOffset.UtcNow;
            var intentIdForLog = intent != null ? intent.ComputeIntentId() : "";
            var instrumentForLog = intent?.Instrument ?? "";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentIdForLog, instrumentForLog, "INTENT_REGISTRATION_REJECTED",
                new
                {
                    failure_reason = registrationFailureReason,
                    trading_date = intent?.TradingDate,
                    stream = intent?.Stream,
                    session = intent?.Session,
                    slot_time_chicago = intent?.SlotTimeChicago,
                    direction = intent?.Direction,
                    trigger_reason = intent?.TriggerReason,
                    note = "Intent rejected at registration boundary; not added to IntentMap."
                }));
            throw new InvalidOperationException($"Intent registration rejected: {registrationFailureReason}");
        }

        // Option A (Stronger): Fail-closed when execution differs from canonical but ExecutionInstrument is null.
        // Prevents journal/reconciliation mismatch (e.g. RTY stored when M2K expected).
        var execContext = _iea?.ExecutionInstrumentKey ?? _ieaEngineExecutionInstrument ?? "";
        var execRoot = string.IsNullOrEmpty(execContext) ? "" : execContext.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? execContext;
        var canonical = (intent.Instrument ?? "").Trim();
        var executionNull = string.IsNullOrWhiteSpace(intent.ExecutionInstrument);
        var executionDiffersFromCanonical = !string.IsNullOrEmpty(execRoot) && !string.IsNullOrEmpty(canonical) &&
            !string.Equals(execRoot, canonical, StringComparison.OrdinalIgnoreCase);
        if (executionDiffersFromCanonical && executionNull)
        {
            var utcNow = DateTimeOffset.UtcNow;
            _log.Write(RobotEvents.ExecutionBase(utcNow, intent.TradingDate ?? "", "INTENT_EXECUTION_INSTRUMENT_MISSING", "CRITICAL",
                new
                {
                    intent_id = intent.ComputeIntentId(),
                    stream = intent.Stream,
                    canonical_instrument = canonical,
                    execution_instrument_context = execRoot,
                    note = "Execution differs from canonical but intent.ExecutionInstrument is null. Would cause journal/reconciliation mismatch. Fail-closed."
                }));
            _standDownStreamCallback?.Invoke(intent.Stream ?? "", utcNow, $"INTENT_EXECUTION_INSTRUMENT_MISSING:{intent.ComputeIntentId()}");
            throw new InvalidOperationException($"Intent registration rejected: ExecutionInstrument is null but execution context ({execRoot}) differs from canonical ({canonical}). Journal would store wrong instrument for reconciliation.");
        }

        var intentId = intent.ComputeIntentId();
        IntentMap[intentId] = intent;
        
        // Log intent registration for debugging
        _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, intentId, intent.Instrument, "INTENT_REGISTERED",
            new
            {
                intent_id = intentId,
                stream = intent.Stream,
                instrument = intent.Instrument,
                direction = intent.Direction,
                entry_price = intent.EntryPrice,
                stop_price = intent.StopPrice,
                target_price = intent.TargetPrice,
                be_trigger = intent.BeTrigger,  // CRITICAL: Log BE trigger to verify it's set
                has_direction = intent.Direction != null,
                has_stop_price = intent.StopPrice != null,
                has_target_price = intent.TargetPrice != null,
                has_be_trigger = intent.BeTrigger != null,  // CRITICAL: Log whether BE trigger is set
                note = "Intent registered - required for protective order placement on fill. BE trigger must be set for break-even detection."
            }));
    }
    
    /// <summary>
    /// Register intent policy expectation for quantity invariant tracking.
    /// Called by StreamStateMachine after intent creation, before order submission.
    /// 
    /// VERIFICATION CHECKLIST:
    /// 1. Check for INTENT_EXECUTION_EXPECTATION_DECLARED event after intent creation
    ///    - Should show policy_base_size and policy_max_size matching policy file
    /// 2. Check for INTENT_POLICY_REGISTERED event in adapter
    ///    - Should match INTENT_EXECUTION_EXPECTATION_DECLARED values
    /// 3. Check for ENTRY_SUBMIT_PRECHECK before every order submission
    ///    - Should show allowed=true for valid submissions
    ///    - Should show allowed=false and reason for blocked submissions
    /// 4. Check for ORDER_CREATED_VERIFICATION after CreateOrder()
    ///    - Should show verified=true for correct orders
    ///    - Should show verified=false and QUANTITY_MISMATCH_EMERGENCY for mismatches
    /// 5. Check for INTENT_FILL_UPDATE on every fill
    ///    - Should show cumulative_filled_qty increasing
    ///    - Should show overfill=false for normal fills
    ///    - Should show overfill=true and INTENT_OVERFILL_EMERGENCY for overfills
    /// 6. Verify end-to-end: policy_base_size â†’ expected_quantity â†’ order_quantity â†’ cumulative_filled_qty
    ///    - All should match for successful execution
    /// </summary>
    public void RegisterIntentPolicy(
        string intentId, 
        int expectedQty, 
        int maxQty, 
        string canonical, 
        string execution, 
        string policySource = "EXECUTION_POLICY_FILE")
    {
        IntentPolicy[intentId] = new IntentPolicyExpectation
        {
            ExpectedQuantity = expectedQty,
            MaxQuantity = maxQty,
            PolicySource = policySource,
            CanonicalInstrument = canonical,
            ExecutionInstrument = execution
        };
        
        // Emit log event
        _log.Write(RobotEvents.ExecutionBase(DateTimeOffset.UtcNow, intentId, execution, 
            "INTENT_POLICY_REGISTERED", new
        {
            intent_id = intentId,
            canonical_instrument = canonical,
            execution_instrument = execution,
            expected_qty = expectedQty,
            max_qty = maxQty,
            source = policySource
        }));
    }

    /// <summary>
    /// STEP 4: Protective Orders (ON FILL ONLY)
    /// Called when entry is fully filled.
    /// </summary>
    public OrderSubmissionResult SubmitProtectiveStop(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        if (!TryIntentIdConsistencyGuard(intentId, instrument, "protective", utcNow, out var intentIdFailProt))
            return intentIdFailProt!;
        if (!TrySessionIdentityGate(intentId, instrument, "protective", utcNow, null, out var sessionFailProt))
            return sessionFailProt!;

        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "PROTECTIVE_STOP",
            direction,
            stop_price = stopPrice,
            quantity,
            account = "SIM"
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
{
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED",
            order_type = "PROTECTIVE_STOP"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
}
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET",
                order_type = "PROTECTIVE_STOP"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        if (!TryExecutionSafetyGateForOrderSubmit(intentId, instrument, "SUBMIT_PROTECTIVE_STOP", utcNow, out var safetyFailProt))
        {
            ReleaseMarketReentryExecutionLatchIfProtectionFailed(intentId, instrument, utcNow, "REENTRY_PROTECTIVE_STOP_DENIED");
            return safetyFailProt!;
        }

        try
        {
            var result = SubmitProtectiveStopReal(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
            if (!result.Success)
                ReleaseMarketReentryExecutionLatchIfProtectionFailed(intentId, instrument, utcNow, "REENTRY_PROTECTIVE_STOP_FAILED");
            return result;
        }
        catch (Exception ex)
        {
            ReleaseMarketReentryExecutionLatchIfProtectionFailed(intentId, instrument, utcNow, "REENTRY_PROTECTIVE_STOP_EXCEPTION");
            // Journal: STOP_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (IntentMap.TryGetValue(intentId, out var stopIntent))
            {
                tradingDate = stopIntent.TradingDate;
                stream = stopIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"STOP_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                order_type = "PROTECTIVE_STOP",
                account = "SIM"
            }));
            
            return OrderSubmissionResult.FailureResult($"Stop order submission failed: {ex.Message}", utcNow);
        }
    }

    public OrderSubmissionResult SubmitTargetOrder(
        string intentId,
        string instrument,
        string direction,
        decimal targetPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        if (!TryIntentIdConsistencyGuard(intentId, instrument, "target", utcNow, out var intentIdFailTgt))
            return intentIdFailTgt!;
        if (!TrySessionIdentityGate(intentId, instrument, "target", utcNow, null, out var sessionFailTgt))
            return sessionFailTgt!;

        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "TARGET",
            direction,
            target_price = targetPrice,
            quantity,
            account = "SIM"
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
{
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED",
            order_type = "TARGET"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
}
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET",
                order_type = "TARGET"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        if (!TryExecutionSafetyGateForOrderSubmit(intentId, instrument, "SUBMIT_TARGET", utcNow, out var safetyFailTgt2))
        {
            ReleaseMarketReentryExecutionLatchIfProtectionFailed(intentId, instrument, utcNow, "REENTRY_TARGET_DENIED");
            return safetyFailTgt2!;
        }

        try
        {
            var result = SubmitTargetOrderReal(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);
            if (!result.Success)
                ReleaseMarketReentryExecutionLatchIfProtectionFailed(intentId, instrument, utcNow, "REENTRY_TARGET_FAILED");
            return result;
        }
        catch (Exception ex)
        {
            ReleaseMarketReentryExecutionLatchIfProtectionFailed(intentId, instrument, utcNow, "REENTRY_TARGET_EXCEPTION");
            // Journal: TARGET_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (IntentMap.TryGetValue(intentId, out var targetIntent))
            {
                tradingDate = targetIntent.TradingDate;
                stream = targetIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"TARGET_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "TARGET", rejectedPrice: targetPrice, rejectedQuantity: quantity);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                order_type = "TARGET",
                account = "SIM"
            }));
            
            return OrderSubmissionResult.FailureResult($"Target order submission failed: {ex.Message}", utcNow);
        }
    }

}
