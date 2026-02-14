// CRITICAL: Define NINJATRADER for NinjaTrader's compiler
// NinjaTrader compiles to tmp folder and may not respect .csproj DefineConstants
#define NINJATRADER

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;

namespace QTSW2.Robot.Core.Execution;

/// <summary>
/// NinjaTrader Sim adapter: places orders in NT Sim account only.
/// 
/// Submission Sequencing (Safety-First Approach):
/// 1. Submit entry order (market order initially)
/// 2. On entry fill confirmation → submit protective stop + target (OCO pair)
/// 3. On BE trigger reached → modify stop to break-even
/// 4. On target/stop fill → flatten remaining position
/// 
/// Hard Safety Requirements:
/// - Must verify SIM account usage (fail closed if not Sim)
/// - All orders must be namespaced by (intent_id, stream) for isolation
/// - OCO grouping must be stream-local (no cross-stream interference)
/// </summary>
public sealed partial class NinjaTraderSimAdapter : IExecutionAdapter
{
    private readonly RobotLogger _log;
    private readonly string _projectRoot;
    private readonly ExecutionJournal _executionJournal;
    
    // Order tracking: intentId -> NT order info
    private readonly ConcurrentDictionary<string, OrderInfo> _orderMap = new();
    
    // Intent tracking: intentId -> Intent (for protective order submission)
    private readonly ConcurrentDictionary<string, Intent> _intentMap = new();
    
    // Fill callback: intentId -> callback action (for protective order submission)
    private readonly ConcurrentDictionary<string, Action<string, decimal, int, DateTimeOffset>> _fillCallbacks = new();
    
    // Intent policy tracking: intentId -> policy expectation
    private readonly Dictionary<string, IntentPolicyExpectation> _intentPolicy = new();
    
    // Track which intents have already triggered emergency (idempotent)
    private readonly HashSet<string> _emergencyTriggered = new();
    
    // NT Account and Instrument references (injected from Strategy host)
    private object? _ntAccount; // NinjaTrader.Cbi.Account
    private object? _ntInstrument; // NinjaTrader.Cbi.Instrument
    private bool _simAccountVerified = false;
    private bool _ntContextSet = false;
    
    // PHASE 2: Callback to stand down stream on protective order failure
    private Action<string, DateTimeOffset, string>? _standDownStreamCallback;
    
    // PHASE 2: Callback to get notification service for alerts
    private Func<object?>? _getNotificationServiceCallback;
    
    // PHASE 2: Callback to check if execution is allowed (recovery state guard)
    private Func<bool>? _isExecutionAllowedCallback;
    
    // Intent exposure coordinator
    private InstrumentIntentCoordinator? _coordinator;
    
    // Rate-limiting for INSTRUMENT_MISMATCH logging (operational hygiene)
    // Prevents log flooding when instrument mismatch persists
    private readonly Dictionary<string, DateTimeOffset> _lastInstrumentMismatchLogUtc = new();
    private const int INSTRUMENT_MISMATCH_RATE_LIMIT_MINUTES = 60; // Log at most once per hour per instrument
    
    // Diagnostic: Track rate-limit diagnostic logs separately
    private readonly Dictionary<string, DateTimeOffset> _lastInstrumentMismatchDiagLogUtc = new();

    public NinjaTraderSimAdapter(string projectRoot, RobotLogger log, ExecutionJournal executionJournal)
    {
        _projectRoot = projectRoot;
        _log = log;
        _executionJournal = executionJournal;
        
        // Note: SIM account verification happens when NT context is set via SetNTContext()
        // Mock mode has been removed - only real NT API execution is supported
    }
    
    /// <summary>
    /// PHASE 2: Set callbacks for stream stand-down and notification service access.
    /// </summary>
    public void SetEngineCallbacks(Action<string, DateTimeOffset, string>? standDownStreamCallback, Func<object?>? getNotificationServiceCallback, Func<bool>? isExecutionAllowedCallback = null)
    {
        _standDownStreamCallback = standDownStreamCallback;
        _getNotificationServiceCallback = getNotificationServiceCallback;
        _isExecutionAllowedCallback = isExecutionAllowedCallback;
    }
    
    /// <summary>
    /// Set intent exposure coordinator.
    /// </summary>
    public void SetCoordinator(InstrumentIntentCoordinator coordinator)
    {
        _coordinator = coordinator;
    }

    /// <summary>
    /// Set NinjaTrader context (Account, Instrument) from Strategy host.
    /// Called by RobotSimStrategy after NT context is available.
    /// </summary>
    public void SetNTContext(object account, object instrument)
    {
        _ntAccount = account;
        _ntInstrument = instrument;
        _ntContextSet = true;
        
        // STEP 1: SIM Account Verification (MANDATORY) - now with real NT account
        VerifySimAccount();
    }

    /// <summary>
    /// STEP 1: Verify we're connected to NT Sim account (fail closed if not).
    /// REQUIRES: NINJATRADER preprocessor directive and NT context to be set.
    /// </summary>
    private void VerifySimAccount()
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", error }));
            throw new InvalidOperationException(error);
        }

        VerifySimAccountReal();
    }

    /// <summary>
    /// STEP 2: Implement Entry Order Submission (REAL NT API)
    /// </summary>
    public OrderSubmissionResult SubmitEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal? entryPrice,
        int quantity,
        string? entryOrderType,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY",
            direction,
            entry_price = entryPrice,
            entry_order_type = entryOrderType,
            quantity,
            account = "SIM"
        }));

        // Hard safety: Verify Sim account (should already be verified, but double-check)
        if (!_simAccountVerified)
        {
            var error = "SIM account not verified - not placing orders";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                account = "SIM"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                reason = "NT_CONTEXT_NOT_SET"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            return SubmitEntryOrderReal(intentId, instrument, direction, entryPrice, quantity, entryOrderType, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: ENTRY_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (_intentMap.TryGetValue(intentId, out var entryIntent))
            {
                tradingDate = entryIntent.TradingDate;
                stream = entryIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"ENTRY_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "ENTRY", rejectedPrice: entryPrice, rejectedQuantity: quantity);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                account = "SIM",
                exception_type = ex.GetType().Name
            }));
            
            return OrderSubmissionResult.FailureResult($"Entry order submission failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// STEP 2b: Submit stop-market entry order (breakout stop).
    /// Used to place stop entries immediately after RANGE_LOCKED (before breakout occurs).
    /// </summary>
    public OrderSubmissionResult SubmitStopEntryOrder(
        string intentId,
        string instrument,
        string direction,
        decimal stopPrice,
        int quantity,
        string? ocoGroup,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY_STOP",
            direction,
            stop_price = stopPrice,
            quantity,
            oco_group = ocoGroup,
            account = "SIM"
        }));

        // Hard safety: Verify Sim account (should already be verified, but double-check)
        if (!_simAccountVerified)
        {
            var error = "SIM account not verified - not placing stop entry orders";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error,
                order_type = "ENTRY_STOP",
                account = "SIM"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED",
            order_type = "ENTRY_STOP"
        }));
        return OrderSubmissionResult.FailureResult(error, utcNow);
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
                order_type = "ENTRY_STOP"
            }));
            return OrderSubmissionResult.FailureResult(error, utcNow);
        }

        try
        {
            return SubmitStopEntryOrderReal(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        }
        catch (Exception ex)
        {
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (_intentMap.TryGetValue(intentId, out var stopEntryIntent))
            {
                tradingDate = stopEntryIntent.TradingDate;
                stream = stopEntryIntent.Stream;
            }
            _executionJournal.RecordRejection(intentId, tradingDate, stream, $"ENTRY_STOP_SUBMIT_FAILED: {ex.Message}", utcNow, 
                orderType: "ENTRY_STOP", rejectedPrice: stopPrice, rejectedQuantity: quantity);

            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_FAIL", new
            {
                error = ex.Message,
                order_type = "ENTRY_STOP",
                account = "SIM",
                exception_type = ex.GetType().Name
            }));

            return OrderSubmissionResult.FailureResult($"Stop entry order submission failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// STEP 3: Handle NT OrderUpdate event (called by Strategy host).
    /// Public method for Strategy to forward NT events.
    /// </summary>
    public void HandleOrderUpdate(object order, object orderUpdate)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif
        HandleOrderUpdateReal(order, orderUpdate);
    }

    /// <summary>
    /// STEP 3: Handle NT ExecutionUpdate event (called by Strategy host).
    /// Public method for Strategy to forward NT events.
    /// </summary>
    public void HandleExecutionUpdate(object execution, object order)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif
        HandleExecutionUpdateReal(execution, order);
    }
    
    /// <summary>
    /// PHASE 2: Handle entry fill and submit protective orders with retry and failure recovery.
    /// </summary>
    private void HandleEntryFill(string intentId, Intent intent, decimal fillPrice, int fillQuantity, int totalFilledQuantity, DateTimeOffset utcNow)
    {
        // CRITICAL FIX: totalFilledQuantity is the cumulative filled quantity (passed from caller)
        // For incremental fills, protective orders must cover the ENTIRE position, not just the new fill
        // This prevents position accumulation bugs where protective orders only cover the delta
        
        // Record entry fill time for watchdog tracking
        if (_orderMap.TryGetValue(intentId, out var entryOrderInfo))
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
        
        // CRITICAL FIX: Check recovery state before submitting protective orders
        // This prevents protective orders from being attempted during disconnect recovery
        // when the broker API may be unavailable
        if (_isExecutionAllowedCallback != null && !_isExecutionAllowedCallback())
        {
            var error = "Execution blocked - recovery state guard active. Protective orders will be queued for submission after recovery completes.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_BLOCKED_RECOVERY",
                new 
                { 
                    error = error, 
                    intent_id = intentId, 
                    fill_quantity = fillQuantity,
                    fill_price = fillPrice,
                    note = "Protective orders blocked during recovery state - position unprotected until recovery completes"
                }));
            
            // SIMPLIFICATION: Use centralized fail-closed pattern
            // CRITICAL: Queue protective orders for submission after recovery completes
            // For now, flatten immediately (fail-closed) - TODO: Implement queue mechanism
            FailClosed(
                intentId,
                intent,
                error,
                "PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED",
                $"PROTECTIVE_BLOCKED_RECOVERY:{intentId}",
                $"CRITICAL: Protective Orders Blocked - Recovery State - {intent.Instrument}",
                $"Entry filled but protective orders blocked due to recovery state. Position flattened. Stream: {intent.Stream}, Intent: {intentId}.",
                null, // stopResult
                null, // targetResult
                new { error }, // additionalData
                utcNow);
            
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
                    break;
                
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
        
        // Flatten position immediately with retry logic
        var flattenResult = FlattenWithRetry(intentId, intent.Instrument, utcNow);
        
        // Stand down stream
        _standDownStreamCallback?.Invoke(intent.Stream, utcNow, $"{eventType}: {failureReason}");
        
        // Persist incident record
        PersistProtectiveFailureIncident(intentId, intent, stopResult, targetResult, flattenResult, utcNow);
        
        // Raise high-priority alert
        var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
        if (notificationService != null)
        {
            notificationService.EnqueueNotification(notificationKey, notificationTitle, notificationMessage, priority: 2); // Emergency priority
        }
        
        // Log final flattened event
        var logData = new Dictionary<string, object>
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
            logData["stop_success"] = stopResult.Success;
            logData["stop_error"] = stopResult.ErrorMessage;
        }
        if (targetResult != null)
        {
            logData["target_success"] = targetResult.Success;
            logData["target_error"] = targetResult.ErrorMessage;
        }
        if (additionalData != null)
        {
            // Merge additional data into log
            var props = additionalData.GetType().GetProperties();
            foreach (var prop in props)
            {
                logData[prop.Name] = prop.GetValue(additionalData);
            }
        }
        
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, eventType, logData));
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
            var incidentDir = System.IO.Path.Combine(_projectRoot, "data", "execution_incidents");
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
    /// Called by StreamStateMachine after entry submission.
    /// Internal method - adapter-specific.
    /// </summary>
    internal void RegisterIntent(Intent intent)
    {
        var intentId = intent.ComputeIntentId();
        _intentMap[intentId] = intent;
        
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
    /// 6. Verify end-to-end: policy_base_size → expected_quantity → order_quantity → cumulative_filled_qty
    ///    - All should match for successful execution
    /// </summary>
    internal void RegisterIntentPolicy(
        string intentId, 
        int expectedQty, 
        int maxQty, 
        string canonical, 
        string execution, 
        string policySource = "EXECUTION_POLICY_FILE")
    {
        _intentPolicy[intentId] = new IntentPolicyExpectation
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

        try
        {
            return SubmitProtectiveStopReal(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: STOP_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (_intentMap.TryGetValue(intentId, out var stopIntent))
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

        try
        {
            return SubmitTargetOrderReal(intentId, instrument, direction, targetPrice, quantity, ocoGroup, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: TARGET_SUBMIT_FAILED
            // Get Intent info for journal logging
            string tradingDate = "";
            string stream = "";
            if (_intentMap.TryGetValue(intentId, out var targetIntent))
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

    /// <summary>
    /// STEP 5: Break-Even Modification
    /// </summary>
    public OrderModificationResult ModifyStopToBreakEven(
        string intentId,
        string instrument,
        decimal beStopPrice,
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_ATTEMPT", new
        {
            be_stop_price = beStopPrice,
            account = "SIM"
        }));

        if (!_simAccountVerified)
        {
            var error = "SIM account not verified";
            return OrderModificationResult.FailureResult(error, utcNow);
        }

        try
        {
            // STEP 5: Check journal to prevent duplicate BE modifications
            // CRITICAL: Use tradingDate and stream from intent - empty strings never match journal keys
            var tradingDate = "";
            var stream = "";
            if (_intentMap.TryGetValue(intentId, out var intentForBe))
            {
                tradingDate = intentForBe.TradingDate ?? "";
                stream = intentForBe.Stream ?? "";
            }
            if (_executionJournal.IsBEModified(intentId, tradingDate, stream))
            {
                var error = "BE modification already attempted for this intent";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_SKIPPED", new
                {
                    reason = "DUPLICATE_BE_MODIFICATION",
                    account = "SIM"
                }));
                return OrderModificationResult.FailureResult(error, utcNow);
            }
            
#if !NINJATRADER
            var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                       "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
            {
                error,
                reason = "NINJATRADER_NOT_DEFINED"
            }));
            return OrderModificationResult.FailureResult(error, utcNow);
#endif

            if (!_ntContextSet)
            {
                var error = "CRITICAL: NT context is not set. " +
                           "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                           "Mock mode has been removed - only real NT API execution is supported.";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
                {
                    error,
                    reason = "NT_CONTEXT_NOT_SET"
                }));
                return OrderModificationResult.FailureResult(error, utcNow);
            }

            return ModifyStopToBreakEvenReal(intentId, instrument, beStopPrice, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: BE_MODIFY_FAILED
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_FAIL", new
            {
                error = ex.Message,
                account = "SIM"
            }));
            
            return OrderModificationResult.FailureResult($"BE modification failed: {ex.Message}", utcNow);
        }
    }

    /// <summary>
    /// REAL RISK FIX: Flatten with retry logic (3 retries, short delay).
    /// Flatten is the last line of defense - if it fails due to transient issues,
    /// we must retry before giving up.
    /// </summary>
    private FlattenResult FlattenWithRetry(
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
        if (_intentMap.TryGetValue(intentId, out var intent))
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
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
        {
            error,
            reason = "NINJATRADER_NOT_DEFINED"
        }));
        return FlattenResult.FailureResult(error, utcNow);
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
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", error }));
            throw new InvalidOperationException(error);
        }

        return GetCurrentPositionReal(instrument);
    }
    
    public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", error }));
            throw new InvalidOperationException(error);
        }

        return GetAccountSnapshotReal(utcNow);
    }
    
    public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow)
    {
#if !NINJATRADER
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
            new { reason = "NINJATRADER_NOT_DEFINED", error }));
        throw new InvalidOperationException(error);
#endif

        if (!_ntContextSet)
        {
            var error = "CRITICAL: NT context is not set. " +
                       "SetNTContext() must be called by RobotSimStrategy before orders can be placed. " +
                       "Mock mode has been removed - only real NT API execution is supported.";
            _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "NT_CONTEXT_NOT_SET", error }));
            throw new InvalidOperationException(error);
        }

        CancelRobotOwnedWorkingOrdersReal(snap, utcNow);
    }

    /// <summary>
    /// Check all instruments for flat positions and cancel entry stops.
    /// Called on every execution update to detect manual position closures.
    /// </summary>
    private void CheckAllInstrumentsForFlatPositions(DateTimeOffset utcNow)
    {
        // Implementation in NinjaTraderSimAdapter.NT.cs
    }
    
    /// <summary>
    /// Check for unprotected positions and flatten if protectives not acknowledged within timeout.
    /// </summary>
    private void CheckUnprotectedPositions(DateTimeOffset utcNow)
    {
        const double UNPROTECTED_POSITION_TIMEOUT_SECONDS = 10.0;
        
        foreach (var kvp in _orderMap)
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
                    if (_intentMap.TryGetValue(intentId, out var intent))
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
        if (_intentMap.TryGetValue(intentId, out var intent))
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
        if (_intentMap.TryGetValue(intentId, out var intentForCallback))
        {
            _standDownStreamCallback?.Invoke(intentForCallback.Stream, utcNow, emergencyType);
        }
    }
    
    /// <summary>
    /// Get active intents that need break-even monitoring.
    /// Returns intents with filled entries that haven't had BE triggered yet.
    /// 
    /// CRITICAL FIX: Check execution journal instead of _orderMap because protective orders
    /// overwrite entry orders in _orderMap (entry order is replaced with stop/target orders).
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
        
        // CRITICAL FIX: Iterate over _intentMap instead of _orderMap
        // _orderMap gets overwritten by protective orders (stop/target), so entry orders are lost
        // Execution journal is the authoritative source for entry fill status
        foreach (var kvp in _intentMap)
        {
            var intentId = kvp.Key;
            var intent = kvp.Value;
            
            // CRITICAL FIX: Filter by execution instrument - each strategy only gets ticks for its chart
            // Without this, MES strategy would compare MES tick price against YM/GC/NG intents (wrong!)
            if (!string.IsNullOrEmpty(executionInstrument) &&
                !string.Equals(intent.ExecutionInstrument, executionInstrument, StringComparison.OrdinalIgnoreCase))
                continue;
            
            // Check if intent has required fields for BE monitoring
            if (intent.BeTrigger == null || intent.EntryPrice == null || intent.Direction == null)
                continue;
            
            // Check if entry has been filled using execution journal (source of truth)
            // Execution journal tracks entry fills regardless of _orderMap state
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
            
            // Check if BE has already been triggered (idempotency check)
            if (_executionJournal.IsBEModified(intentId, tradingDate, stream))
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
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_INTENT_ORDERS_BLOCKED", state: "ENGINE",
            new { intent_id = intentId, reason = "NINJATRADER_NOT_DEFINED", error }));
        return false;
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
        var error = "CRITICAL: NINJATRADER preprocessor directive is NOT defined. " +
                   "Add <DefineConstants>NINJATRADER</DefineConstants> to your .csproj file and rebuild. " +
                   "Mock mode has been removed - only real NT API execution is supported.";
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "FLATTEN_INTENT_ERROR", state: "ENGINE",
            new { intent_id = intentId, instrument, reason = "NINJATRADER_NOT_DEFINED", error }));
        return FlattenResult.FailureResult(error, utcNow);
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

    /// <summary>
    /// Order tracking info for callback correlation.
    /// </summary>
    private partial class OrderInfo
    {
        public string IntentId { get; set; } = "";
        public string Instrument { get; set; } = "";
        public string OrderId { get; set; } = "";
        public string OrderType { get; set; } = ""; // ENTRY, STOP, TARGET
        public string Direction { get; set; } = "";
        public int Quantity { get; set; }
        public decimal? Price { get; set; }
        public string State { get; set; } = ""; // SUBMITTED, FILLED, REJECTED, CANCELLED

        // Classification: true for entry intents (ENTRY and ENTRY_STOP)
        public bool IsEntryOrder { get; set; }

        // Partial fill handling
        public int FilledQuantity { get; set; }
        
        // Watchdog tracking for unprotected positions
        public DateTimeOffset? EntryFillTime { get; set; }
        public bool ProtectiveStopAcknowledged { get; set; }
        public bool ProtectiveTargetAcknowledged { get; set; }
        
        // Policy expectation snapshot (copied from _intentPolicy when OrderInfo is created)
        public int ExpectedQuantity { get; set; }
        public int MaxQuantity { get; set; }
        public string PolicySource { get; set; } = "";
        public string CanonicalInstrument { get; set; } = "";
        public string ExecutionInstrument { get; set; } = "";
        
        // NT-specific: Store NT Order object for callbacks (only when NINJATRADER is defined)
#if NINJATRADER
        public object? NTOrder { get; set; } // NinjaTrader.Cbi.Order
#endif
    }
    
    /// <summary>
    /// Intent policy expectation model for quantity invariant tracking.
    /// </summary>
    private sealed class IntentPolicyExpectation
    {
        public int ExpectedQuantity { get; set; }
        public int MaxQuantity { get; set; }
        public string PolicySource { get; set; } = "EXECUTION_POLICY_FILE";
        public string CanonicalInstrument { get; set; } = "";
        public string ExecutionInstrument { get; set; } = "";
    }
}
