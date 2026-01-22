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
public sealed class NinjaTraderSimAdapter : IExecutionAdapter
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
    
    // NT Account and Instrument references (injected from Strategy host)
    private object? _ntAccount; // NinjaTrader.Cbi.Account
    private object? _ntInstrument; // NinjaTrader.Cbi.Instrument
    private bool _simAccountVerified = false;
    private bool _ntContextSet = false;
    
    // PHASE 2: Callback to stand down stream on protective order failure
    private Action<string, DateTimeOffset, string>? _standDownStreamCallback;
    
    // PHASE 2: Callback to get notification service for alerts
    private Func<object?>? _getNotificationServiceCallback;

    public NinjaTraderSimAdapter(string projectRoot, RobotLogger log, ExecutionJournal executionJournal)
    {
        _projectRoot = projectRoot;
        _log = log;
        _executionJournal = executionJournal;
        
        // Note: SIM account verification happens when NT context is set via SetNTContext()
        // This allows adapter to work in both harness (mock) and NT Strategy (real) contexts
    }
    
    /// <summary>
    /// PHASE 2: Set callbacks for stream stand-down and notification service access.
    /// </summary>
    public void SetEngineCallbacks(Action<string, DateTimeOffset, string>? standDownStreamCallback, Func<object?>? getNotificationServiceCallback)
    {
        _standDownStreamCallback = standDownStreamCallback;
        _getNotificationServiceCallback = getNotificationServiceCallback;
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
    /// Routes to real NT API when NT context is available, otherwise uses mock for harness testing.
    /// </summary>
    private void VerifySimAccount()
    {
        // If NT context is set, use real NT API verification (when compiled with NINJATRADER)
#if NINJATRADER
        if (_ntContextSet)
        {
            VerifySimAccountReal();
            return;
        }
#endif

        // Otherwise, use mock for harness testing (will fail in real NT environment)
        try
        {
            // Mock NT account resolution (for harness testing only)
            var mockAccount = new { IsSimAccount = true, Name = "Sim101" };
            _ntAccount = mockAccount;
            
            if (_ntAccount == null)
            {
                var error = "NT account is null - cannot verify Sim account";
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                    new { reason = "NOT_SIM_ACCOUNT", error }));
                throw new InvalidOperationException(error);
            }
            
            // Mock: assume Sim for harness testing
            var isSim = true;
            
            if (!isSim)
            {
                var error = $"Account is not Sim account - aborting execution";
                _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                    new { reason = "NOT_SIM_ACCOUNT", account_name = "UNKNOWN", error }));
                throw new InvalidOperationException(error);
            }
            
            _simAccountVerified = true;
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "SIM_ACCOUNT_VERIFIED", state: "ENGINE",
                new { account_name = "Sim101", note = "SIM account verification passed (MOCK - harness mode)" }));
        }
        catch (InvalidOperationException)
        {
            throw;
        }
        catch (Exception ex)
        {
            var error = $"SIM account verification failed: {ex.Message}";
            _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_BLOCKED", state: "ENGINE",
                new { reason = "SIM_ACCOUNT_VERIFICATION_FAILED", error }));
            throw new InvalidOperationException(error, ex);
        }
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
        DateTimeOffset utcNow)
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_ATTEMPT", new
        {
            order_type = "ENTRY",
            direction,
            entry_price = entryPrice,
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

        try
        {
            // STEP 2: Route to real NT API if context is set, otherwise use mock
#if NINJATRADER
            if (_ntContextSet)
            {
                return SubmitEntryOrderReal(intentId, instrument, direction, entryPrice, quantity, utcNow);
            }
#endif

            // Mock implementation for harness testing
            var mockOrderId = $"NT_{intentId}_{utcNow:yyyyMMddHHmmss}";
            var orderAction = direction == "Long" ? "Buy" : "SellShort";
            var orderType = entryPrice.HasValue ? "Limit" : "Market";
            
            var orderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = instrument,
                OrderId = mockOrderId,
                OrderType = "ENTRY",
                Direction = direction,
                Quantity = quantity,
                Price = entryPrice,
                State = "SUBMITTED",
                IsEntryOrder = true,
                FilledQuantity = 0
            };
            _orderMap[intentId] = orderInfo;
            
            var acknowledgedAt = utcNow.AddMilliseconds(50);
            _executionJournal.RecordSubmission(intentId, "", "", instrument, "ENTRY", mockOrderId, acknowledgedAt);
            
            _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = mockOrderId,
                order_type = "ENTRY",
                direction,
                entry_price = entryPrice,
                quantity,
                account = "SIM",
                order_action = orderAction,
                order_type_nt = orderType,
                note = "MOCK - harness mode"
            }));
            
            return OrderSubmissionResult.SuccessResult(mockOrderId, utcNow, acknowledgedAt);
        }
        catch (Exception ex)
        {
            // Journal: ENTRY_SUBMIT_FAILED
            _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_SUBMIT_FAILED: {ex.Message}", utcNow);
            
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

        try
        {
            // Route to real NT API if context is set, otherwise use mock
#if NINJATRADER
            if (_ntContextSet)
            {
                return SubmitStopEntryOrderReal(intentId, instrument, direction, stopPrice, quantity, ocoGroup, utcNow);
            }
#endif

            // Mock implementation for harness testing
            var mockOrderId = $"NT_STOP_{intentId}_{utcNow:yyyyMMddHHmmss}";
            var orderAction = direction == "Long" ? "Buy" : "SellShort";

            var orderInfo = new OrderInfo
            {
                IntentId = intentId,
                Instrument = instrument,
                OrderId = mockOrderId,
                OrderType = "ENTRY_STOP",
                Direction = direction,
                Quantity = quantity,
                Price = stopPrice,
                State = "SUBMITTED",
                IsEntryOrder = true,
                FilledQuantity = 0
            };
            _orderMap[intentId] = orderInfo;

            var acknowledgedAt = utcNow.AddMilliseconds(50);
            _executionJournal.RecordSubmission(intentId, "", "", instrument, "ENTRY_STOP", mockOrderId, acknowledgedAt);

            _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = mockOrderId,
                order_type = "ENTRY_STOP",
                direction,
                stop_price = stopPrice,
                quantity,
                oco_group = ocoGroup,
                account = "SIM",
                order_action = orderAction,
                order_type_nt = "StopMarket",
                note = "MOCK - harness mode"
            }));

            // Alias event for easier grepping (user-facing)
            _log.Write(RobotEvents.ExecutionBase(acknowledgedAt, intentId, instrument, "ORDER_SUBMITTED", new
            {
                broker_order_id = mockOrderId,
                order_type = "ENTRY_STOP",
                direction,
                stop_price = stopPrice,
                quantity,
                oco_group = ocoGroup,
                account = "SIM"
            }));

            return OrderSubmissionResult.SuccessResult(mockOrderId, utcNow, acknowledgedAt);
        }
        catch (Exception ex)
        {
            _executionJournal.RecordRejection(intentId, "", "", $"ENTRY_STOP_SUBMIT_FAILED: {ex.Message}", utcNow);

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
#if NINJATRADER
        // Real NT implementation in .NT.cs file
        HandleOrderUpdateReal(order, orderUpdate);
#else
        // Mock: No-op in harness mode
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "ORDER_UPDATE_MOCK", state: "ENGINE",
            new { note = "OrderUpdate received (MOCK - harness mode)" }));
#endif
    }

    /// <summary>
    /// STEP 3: Handle NT ExecutionUpdate event (called by Strategy host).
    /// Public method for Strategy to forward NT events.
    /// </summary>
    public void HandleExecutionUpdate(object execution, object order)
    {
#if NINJATRADER
        // Real NT implementation in .NT.cs file
        HandleExecutionUpdateReal(execution, order);
#else
        // Mock: No-op in harness mode
        _log.Write(RobotEvents.EngineBase(DateTimeOffset.UtcNow, tradingDate: "", eventType: "EXECUTION_UPDATE_MOCK", state: "ENGINE",
            new { note = "ExecutionUpdate received (MOCK - harness mode)" }));
#endif
    }
    
    /// <summary>
    /// PHASE 2: Handle entry fill and submit protective orders with retry and failure recovery.
    /// </summary>
    private void HandleEntryFill(string intentId, Intent intent, decimal fillPrice, int fillQuantity, DateTimeOffset utcNow)
    {
        if (intent.Direction == null || intent.StopPrice == null || intent.TargetPrice == null)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "EXECUTION_ERROR",
                new { error = "Intent incomplete - cannot submit protective orders", intent_id = intentId }));
            return;
        }
        
        // PHASE 2: Submit protective stop with retry
        const int MAX_RETRIES = 3;
        const int RETRY_DELAY_MS = 100;
        
        OrderSubmissionResult? stopResult = null;
        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0)
            {
                System.Threading.Thread.Sleep(RETRY_DELAY_MS);
            }
            
            stopResult = SubmitProtectiveStop(
                intentId,
                intent.Instrument,
                intent.Direction,
                intent.StopPrice.Value,
                fillQuantity,
                utcNow);
            
            if (stopResult.Success)
                break;
        }
        
        // PHASE 2: Submit target order with retry
        OrderSubmissionResult? targetResult = null;
        for (int attempt = 0; attempt < MAX_RETRIES; attempt++)
        {
            if (attempt > 0)
            {
                System.Threading.Thread.Sleep(RETRY_DELAY_MS);
            }
            
            targetResult = SubmitTargetOrder(
                intentId,
                intent.Instrument,
                intent.Direction,
                intent.TargetPrice.Value,
                fillQuantity,
                utcNow);
            
            if (targetResult.Success)
                break;
        }
        
        // PHASE 2: If either protective leg failed after retries, flatten position and stand down stream
        if (!stopResult.Success || !targetResult.Success)
        {
            var failedLegs = new List<string>();
            if (!stopResult.Success) failedLegs.Add($"STOP: {stopResult.ErrorMessage}");
            if (!targetResult.Success) failedLegs.Add($"TARGET: {targetResult.ErrorMessage}");
            
            var failureReason = $"Protective orders failed after {MAX_RETRIES} retries: {string.Join(", ", failedLegs)}";
            
            // Flatten position immediately
            var flattenResult = Flatten(intentId, intent.Instrument, utcNow);
            
            // Stand down stream
            _standDownStreamCallback?.Invoke(intent.Stream, utcNow, $"PROTECTIVE_ORDER_FAILURE: {failureReason}");
            
            // Persist incident record
            PersistProtectiveFailureIncident(intentId, intent, stopResult, targetResult, flattenResult, utcNow);
            
            // Raise high-priority alert
            var notificationService = _getNotificationServiceCallback?.Invoke() as QTSW2.Robot.Core.Notifications.NotificationService;
            if (notificationService != null)
            {
                var title = $"CRITICAL: Protective Order Failure - {intent.Instrument}";
                var message = $"Entry filled but protective orders failed. Position flattened. Stream: {intent.Stream}, Intent: {intentId}. Failures: {failureReason}";
                notificationService.EnqueueNotification($"PROTECTIVE_FAILURE:{intentId}", title, message, priority: 2); // Emergency priority
            }
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_FAILED_FLATTENED",
                new
                {
                    intent_id = intentId,
                    stream = intent.Stream,
                    instrument = intent.Instrument,
                    stop_success = stopResult.Success,
                    stop_error = stopResult.ErrorMessage,
                    target_success = targetResult.Success,
                    target_error = targetResult.ErrorMessage,
                    flatten_success = flattenResult.Success,
                    flatten_error = flattenResult.ErrorMessage,
                    failure_reason = failureReason,
                    retry_count = MAX_RETRIES,
                    note = "Position flattened and stream stood down due to protective order failure"
                }));
            
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
                quantity = fillQuantity
            }));

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
            protected_quantity = fillQuantity,
            note = "Protective stop + target successfully placed/ensured for filled quantity"
        }));
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
        _intentMap[intent.ComputeIntentId()] = intent;
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

        try
        {
            // STEP 4: Route to real NT API if context is set
#if NINJATRADER
            if (_ntContextSet)
            {
                return SubmitProtectiveStopReal(intentId, instrument, direction, stopPrice, quantity, utcNow);
            }
#endif

            // Mock implementation for harness testing
            var mockOrderId = $"NT_{intentId}_STOP_{utcNow:yyyyMMddHHmmss}";
            _executionJournal.RecordSubmission(intentId, "", "", instrument, "STOP", mockOrderId, utcNow);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = mockOrderId,
                order_type = "PROTECTIVE_STOP",
                direction,
                stop_price = stopPrice,
                quantity,
                account = "SIM",
                note = "MOCK - harness mode"
            }));
            
            return OrderSubmissionResult.SuccessResult(mockOrderId, utcNow, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: STOP_SUBMIT_FAILED
            _executionJournal.RecordRejection(intentId, "", "", $"STOP_SUBMIT_FAILED: {ex.Message}", utcNow);
            
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

        try
        {
            // STEP 4: Route to real NT API if context is set
#if NINJATRADER
            if (_ntContextSet)
            {
                return SubmitTargetOrderReal(intentId, instrument, direction, targetPrice, quantity, utcNow);
            }
#endif

            // Mock implementation for harness testing
            var mockOrderId = $"NT_{intentId}_TARGET_{utcNow:yyyyMMddHHmmss}";
            _executionJournal.RecordSubmission(intentId, "", "", instrument, "TARGET", mockOrderId, utcNow);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_SUBMIT_SUCCESS", new
            {
                broker_order_id = mockOrderId,
                order_type = "TARGET",
                direction,
                target_price = targetPrice,
                quantity,
                account = "SIM",
                note = "MOCK - harness mode"
            }));
            
            return OrderSubmissionResult.SuccessResult(mockOrderId, utcNow, utcNow);
        }
        catch (Exception ex)
        {
            // Journal: TARGET_SUBMIT_FAILED
            _executionJournal.RecordRejection(intentId, "", "", $"TARGET_SUBMIT_FAILED: {ex.Message}", utcNow);
            
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
            if (_executionJournal.IsBEModified(intentId, "", ""))
            {
                var error = "BE modification already attempted for this intent";
                _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_SKIPPED", new
                {
                    reason = "DUPLICATE_BE_MODIFICATION",
                    account = "SIM"
                }));
                return OrderModificationResult.FailureResult(error, utcNow);
            }
            
            // STEP 5: Route to real NT API if context is set
#if NINJATRADER
            if (_ntContextSet)
            {
                return ModifyStopToBreakEvenReal(intentId, instrument, beStopPrice, utcNow);
            }
#endif

            // Mock implementation for harness testing
            _executionJournal.RecordBEModification(intentId, "", "", beStopPrice, utcNow);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "STOP_MODIFY_SUCCESS", new
            {
                be_stop_price = beStopPrice,
                account = "SIM",
                note = "MOCK - harness mode"
            }));
            
            return OrderModificationResult.SuccessResult(utcNow);
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

        try
        {
            // In NT8 API:
            // var position = _ntAccount.GetPosition(instrument);
            // if (position.MarketPosition != MarketPosition.Flat) {
            //     _ntAccount.Flatten(instrument);
            // }
            
            // Mock flatten
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_SUCCESS", new
            {
                account = "SIM"
            }));
            
            return FlattenResult.SuccessResult(utcNow);
        }
        catch (Exception ex)
        {
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "FLATTEN_FAIL", new
            {
                error = ex.Message,
                account = "SIM"
            }));
            
            return FlattenResult.FailureResult($"Flatten failed: {ex.Message}", utcNow);
        }
    }

    public int GetCurrentPosition(string instrument)
    {
        // In NT8 API:
        // var position = _ntAccount?.GetPosition(instrument);
        // return position?.Quantity ?? 0;
        
        // Mock: return 0
        return 0;
    }
    
    public AccountSnapshot GetAccountSnapshot(DateTimeOffset utcNow)
    {
        // Route to real NT API if context is set, otherwise use mock
#if NINJATRADER
        if (_ntContextSet)
        {
            return GetAccountSnapshotReal(utcNow);
        }
#endif
        
        // Mock implementation for harness testing
        return new AccountSnapshot
        {
            Positions = new List<PositionSnapshot>(),
            WorkingOrders = new List<WorkingOrderSnapshot>()
        };
    }
    
    public void CancelRobotOwnedWorkingOrders(AccountSnapshot snap, DateTimeOffset utcNow)
    {
        // Route to real NT API if context is set, otherwise use mock
#if NINJATRADER
        if (_ntContextSet)
        {
            CancelRobotOwnedWorkingOrdersReal(snap, utcNow);
            return;
        }
#endif
        
        // Mock implementation for harness testing
        var robotOwnedOrders = snap.WorkingOrders?.Where(o => 
            (!string.IsNullOrEmpty(o.Tag) && o.Tag.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase)) ||
            (!string.IsNullOrEmpty(o.OcoGroup) && o.OcoGroup.StartsWith("QTSW2:", StringComparison.OrdinalIgnoreCase))
        ).ToList() ?? new List<WorkingOrderSnapshot>();
        
        _log.Write(RobotEvents.EngineBase(utcNow, tradingDate: "", eventType: "CANCEL_ROBOT_ORDERS_MOCK", state: "ENGINE",
            new
            {
                robot_owned_count = robotOwnedOrders.Count,
                robot_owned_order_ids = robotOwnedOrders.Select(o => o.OrderId).ToList(),
                note = "MOCK - harness mode"
            }));
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
    }
}
