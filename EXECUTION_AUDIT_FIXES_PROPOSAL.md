# Execution Audit Fixes Proposal

**Date**: February 4, 2026  
**Based On**: `EXECUTION_AUDIT_COMPREHENSIVE.md`

---

## Proposed Fixes

### Fix 1: Prevent Multiple Entry Orders for Same Intent (HIGH PRIORITY)

**Issue**: Multiple entry orders can be submitted for the same intent, causing unexpected fill accumulation.

**Location**: `NinjaTraderSimAdapter.NT.cs` - `SubmitEntryOrderReal()` method

**Proposed Change**: Add check at the beginning of `SubmitEntryOrderReal()` to prevent duplicate entry orders.

**Code Change**:
```csharp
// Add after line 179 (after method signature, before NT context checks)
// CRITICAL FIX: Prevent multiple entry orders for same intent
if (_orderMap.TryGetValue(intentId, out var existingOrder))
{
    // Check if existing order is an entry order and still active
    if (existingOrder.IsEntryOrder && 
        (existingOrder.State == "SUBMITTED" || 
         existingOrder.State == "ACCEPTED" || 
         existingOrder.State == "WORKING"))
    {
        var error = $"Entry order already exists for intent {intentId}. " +
                   $"Existing order state: {existingOrder.State}, " +
                   $"Broker Order ID: {existingOrder.OrderId}";
        
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_ORDER_DUPLICATE_BLOCKED", new
        {
            intent_id = intentId,
            existing_order_id = existingOrder.OrderId,
            existing_order_state = existingOrder.State,
            error = error,
            note = "Multiple entry orders prevented - only one entry order allowed per intent"
        }));
        
        return OrderSubmissionResult.FailureResult(error, utcNow);
    }
    
    // If existing order is filled, allow new entry (shouldn't happen but handle gracefully)
    if (existingOrder.State == "FILLED")
    {
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ENTRY_ORDER_ALREADY_FILLED", new
        {
            intent_id = intentId,
            existing_order_id = existingOrder.OrderId,
            warning = "Entry order already filled - new entry order submission blocked",
            note = "This should not happen - entry orders should only be submitted once per intent"
        }));
        
        return OrderSubmissionResult.FailureResult("Entry order already filled for this intent", utcNow);
    }
}
```

**Impact**: Prevents duplicate entry orders, reducing risk of unexpected position accumulation.

**Risk**: LOW - Fail-closed behavior, only blocks invalid submissions.

---

### Fix 2: Make Tag Verification Failure Fatal (MEDIUM PRIORITY)

**Issue**: Tag verification failure is logged but not fatal, which may cause fills to be untracked.

**Location**: `NinjaTraderSimAdapter.NT.cs` lines 653-667

**Proposed Change**: Make tag verification failure fatal (fail-closed) with retry logic.

**Code Change**:
```csharp
// Replace lines 653-667 with:
// CRITICAL: Verify tag was set correctly - fail-closed if verification fails
var verifyTag = GetOrderTag(order);
if (verifyTag != encodedTag)
{
    var error = $"Order tag verification failed - tag may not be set correctly. " +
               $"Expected: {encodedTag}, Actual: {verifyTag ?? "NULL"}";
    
    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_FAILED_CRITICAL",
        new
        {
            intent_id = intentId,
            expected_tag = encodedTag,
            actual_tag = verifyTag ?? "NULL",
            broker_order_id = order.OrderId,
            error = error,
            action = "RETRYING_ORDER_CREATION",
            note = "CRITICAL: Tag verification failed - retrying order creation with explicit tag setting"
        }));
    
    // CRITICAL FIX: Retry order creation with explicit tag setting
    // Try setting tag again before giving up
    try
    {
        SetOrderTag(order, encodedTag);
        verifyTag = GetOrderTag(order);
        
        if (verifyTag != encodedTag)
        {
            // Still failed after retry - fail-closed
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_FAILED_FATAL",
                new
                {
                    intent_id = intentId,
                    expected_tag = encodedTag,
                    actual_tag = verifyTag ?? "NULL",
                    broker_order_id = order.OrderId,
                    error = "Tag verification failed after retry - order creation aborted",
                    action = "ORDER_CREATION_ABORTED",
                    note = "CRITICAL: Cannot guarantee fill tracking - aborting order creation (fail-closed)"
                }));
            
            // Remove from order map if already added
            _orderMap.TryRemove(intentId, out _);
            
            return OrderSubmissionResult.FailureResult(
                $"Order tag verification failed after retry: {error}. Order creation aborted (fail-closed).", 
                utcNow);
        }
        else
        {
            // Retry succeeded - log success
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_RETRY_SUCCEEDED",
                new
                {
                    intent_id = intentId,
                    tag = encodedTag,
                    broker_order_id = order.OrderId,
                    note = "Tag verification retry succeeded - order creation continuing"
                }));
        }
    }
    catch (Exception ex)
    {
        // Retry failed with exception - fail-closed
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "ORDER_TAG_SET_RETRY_EXCEPTION",
            new
            {
                intent_id = intentId,
                expected_tag = encodedTag,
                error = ex.Message,
                exception_type = ex.GetType().Name,
                action = "ORDER_CREATION_ABORTED",
                note = "CRITICAL: Tag retry threw exception - aborting order creation (fail-closed)"
            }));
        
        // Remove from order map if already added
        _orderMap.TryRemove(intentId, out _);
        
        return OrderSubmissionResult.FailureResult(
            $"Order tag verification retry failed: {ex.Message}. Order creation aborted (fail-closed).", 
            utcNow);
    }
}
```

**Impact**: Ensures all orders have correct tags, preventing untracked fills.

**Risk**: LOW - Fail-closed behavior, only blocks orders that can't be tracked.

---

### Fix 3: Increase Race Condition Retry Delay (LOW PRIORITY)

**Issue**: 50ms retry delay may be too short for some threading scenarios.

**Location**: `NinjaTraderSimAdapter.NT.cs` line 1488

**Proposed Change**: Increase retry delay from 50ms to 100ms.

**Code Change**:
```csharp
// Change line 1488 from:
const int RETRY_DELAY_MS = 50;

// To:
const int RETRY_DELAY_MS = 100; // Increased from 50ms to improve race condition resolution
```

**Impact**: Improves race condition resolution reliability.

**Risk**: VERY LOW - Only affects retry timing, no functional change.

---

### Fix 4: Recovery State Protective Order Queue (MEDIUM PRIORITY - DESIGN DISCUSSION)

**Issue**: Recovery state blocks protective orders and flattens position, which may be too aggressive.

**Location**: `NinjaTraderSimAdapter.cs` lines 446-489

**Current Behavior**: If recovery state is active, protective orders are blocked and position is flattened immediately.

**Proposed Change**: Queue protective orders for submission after recovery completes instead of immediate flatten.

**Design Considerations**:
1. **Queue Structure**: Need a queue to store pending protective orders
2. **Recovery Completion Detection**: Need callback when recovery state clears
3. **Timeout**: If recovery doesn't complete within timeout, flatten (fail-closed)
4. **State Tracking**: Track which intents have queued protective orders

**Proposed Implementation**:
```csharp
// Add to NinjaTraderSimAdapter.cs class fields:
private readonly Dictionary<string, QueuedProtectiveOrder> _queuedProtectiveOrders = new();
private const double PROTECTIVE_ORDER_QUEUE_TIMEOUT_SECONDS = 30.0; // 30 second timeout

private class QueuedProtectiveOrder
{
    public string IntentId { get; set; } = "";
    public Intent Intent { get; set; } = null!;
    public decimal FillPrice { get; set; }
    public int FillQuantity { get; set; }
    public int TotalFilledQuantity { get; set; }
    public DateTimeOffset QueueTime { get; set; }
}

// Modify HandleEntryFill() recovery state check (lines 446-489):
if (_isExecutionAllowedCallback != null && !_isExecutionAllowedCallback())
{
    var error = "Execution blocked - recovery state guard active. Protective orders will be queued for submission after recovery completes.";
    _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, intent.Instrument, "PROTECTIVE_ORDERS_QUEUED_RECOVERY",
        new 
        { 
            error = error, 
            intent_id = intentId, 
            fill_quantity = fillQuantity,
            fill_price = fillPrice,
            queue_timeout_seconds = PROTECTIVE_ORDER_QUEUE_TIMEOUT_SECONDS,
            note = "Protective orders queued during recovery state - will submit after recovery completes or timeout"
        }));
    
    // CRITICAL FIX: Queue protective orders instead of immediate flatten
    _queuedProtectiveOrders[intentId] = new QueuedProtectiveOrder
    {
        IntentId = intentId,
        Intent = intent,
        FillPrice = fillPrice,
        FillQuantity = fillQuantity,
        TotalFilledQuantity = totalFilledQuantity,
        QueueTime = utcNow
    };
    
    // Start timeout check (would need periodic timer or callback)
    // For now, check on next recovery state change
    
    return; // Don't flatten - queue instead
}

// Add method to process queued protective orders:
private void ProcessQueuedProtectiveOrders(DateTimeOffset utcNow)
{
    var expiredIntents = new List<string>();
    
    foreach (var kvp in _queuedProtectiveOrders)
    {
        var intentId = kvp.Key;
        var queued = kvp.Value;
        var elapsed = (utcNow - queued.QueueTime).TotalSeconds;
        
        if (elapsed > PROTECTIVE_ORDER_QUEUE_TIMEOUT_SECONDS)
        {
            // Timeout exceeded - flatten (fail-closed)
            expiredIntents.Add(intentId);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, queued.Intent.Instrument, "PROTECTIVE_ORDERS_QUEUE_TIMEOUT",
                new
                {
                    intent_id = intentId,
                    elapsed_seconds = elapsed,
                    timeout_seconds = PROTECTIVE_ORDER_QUEUE_TIMEOUT_SECONDS,
                    action = "FLATTENING_POSITION",
                    note = "Protective order queue timeout exceeded - flattening position (fail-closed)"
                }));
            
            _coordinator?.OnProtectiveFailure(intentId, queued.Intent.Stream, utcNow);
            var flattenResult = FlattenWithRetry(intentId, queued.Intent.Instrument, utcNow);
            _standDownStreamCallback?.Invoke(queued.Intent.Stream, utcNow, $"PROTECTIVE_ORDERS_QUEUE_TIMEOUT:{intentId}");
        }
        else if (_isExecutionAllowedCallback != null && _isExecutionAllowedCallback())
        {
            // Recovery completed - submit protective orders
            expiredIntents.Add(intentId);
            
            _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, queued.Intent.Instrument, "PROTECTIVE_ORDERS_QUEUE_PROCESSING",
                new
                {
                    intent_id = intentId,
                    elapsed_seconds = elapsed,
                    note = "Recovery completed - submitting queued protective orders"
                }));
            
            // Submit protective orders (reuse existing HandleEntryFill logic)
            HandleEntryFill(intentId, queued.Intent, queued.FillPrice, queued.FillQuantity, queued.TotalFilledQuantity, utcNow);
        }
    }
    
    // Remove processed/expired intents from queue
    foreach (var intentId in expiredIntents)
    {
        _queuedProtectiveOrders.Remove(intentId);
    }
}

// Add callback registration point (would need to be called when recovery state changes):
public void OnRecoveryStateChanged(DateTimeOffset utcNow)
{
    ProcessQueuedProtectiveOrders(utcNow);
}
```

**Impact**: Reduces false positives from recovery state blocking, improves system resilience.

**Risk**: MEDIUM - More complex implementation, requires recovery state change detection.

**Recommendation**: **DEFER** - This requires design discussion and recovery state change detection mechanism. Current fail-closed behavior is safer for now.

---

## Implementation Priority

### Immediate (High Priority)
1. ✅ **Fix 1: Prevent Multiple Entry Orders** - Straightforward, prevents real issue
2. ✅ **Fix 2: Make Tag Verification Fatal** - Prevents untracked fills

### Soon (Low Priority)
3. ✅ **Fix 3: Increase Retry Delay** - Simple change, improves reliability

### Defer (Requires Design Discussion)
4. ⚠️ **Fix 4: Recovery State Queue** - Complex, requires recovery state change detection

---

## Testing Plan

### Fix 1 Testing
1. Submit entry order for intent
2. Attempt to submit second entry order for same intent
3. Verify second submission is blocked
4. Verify log shows `ENTRY_ORDER_DUPLICATE_BLOCKED`

### Fix 2 Testing
1. Create order with tag
2. Simulate tag verification failure
3. Verify retry logic executes
4. Verify order creation aborted if retry fails
5. Verify order creation succeeds if retry succeeds

### Fix 3 Testing
1. Trigger race condition scenario
2. Verify retry logic executes with 100ms delay
3. Verify race condition resolves

---

## Files to Modify

1. `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
   - Fix 1: Add duplicate entry order check
   - Fix 2: Make tag verification fatal with retry
   - Fix 3: Increase retry delay

2. `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`
   - Same changes as above

---

## Risk Assessment

**Overall Risk**: LOW
- All fixes follow fail-closed behavior
- No changes to core execution logic
- Only adds validation and improves error handling

**Backward Compatibility**: ✅ FULLY COMPATIBLE
- All changes are additive (new checks)
- No breaking changes to existing behavior
- Fail-closed behavior preserved

---

**Ready for Implementation**: Fixes 1, 2, and 3 are ready. Fix 4 requires design discussion.
