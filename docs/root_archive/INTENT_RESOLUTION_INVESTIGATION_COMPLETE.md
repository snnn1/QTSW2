# Intent Resolution Investigation - Complete Analysis

**Date**: February 4, 2026

## Summary

After investigating the code, I found that **"UNKNOWN" intent_id is NOT actually set in the code**. The issue is more subtle - fills are being **ignored** when intent resolution fails, but the position still accumulates in NinjaTrader.

---

## How Intent Resolution Works

### Flow in `HandleExecutionUpdateReal()` (line 1494-1804)

1. **Extract Intent ID from Order Tag** (line 1494-1495)
   ```csharp
   var encodedTag = GetOrderTag(order);
   var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
   ```

2. **Check if Intent ID is Empty** (line 1504)
   ```csharp
   if (string.IsNullOrEmpty(intentId))
   {
       // Logs EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL
       // Flattens position with intent_id = "UNKNOWN_UNTrackED_FILL"
       return; // Fill is ignored
   }
   ```

3. **Check if Order is in Tracking Map** (line 1586)
   ```csharp
   if (!_orderMap.TryGetValue(intentId, out var orderInfo))
   {
       // Retries if order is Initialized (race condition)
       // Otherwise logs EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL
       // Flattens position
       return; // Fill is ignored
   }
   ```

4. **Resolve Intent Context** (line 1798)
   ```csharp
   if (!ResolveIntentContextOrFailClosed(intentId, encodedTag, orderInfo.OrderType, 
       orderInfo.Instrument, fillPrice, fillQuantity, utcNow, out context))
   {
       // Logs orphan fill
       // Flattens position
       return; // Fill is ignored
   }
   ```

5. **Record Fill** (line 1810)
   ```csharp
   _executionJournal.RecordEntryFill(
       context.IntentId,  // ✅ Valid intent ID
       context.TradingDate,
       context.Stream,
       ...
   );
   ```

---

## Where "UNKNOWN" Appears

### 1. **Instrument Name** (when instrument can't be resolved)
- Line 1506: `var instrument = order.Instrument?.MasterInstrument?.Name ?? "UNKNOWN";`
- This is just for logging - not the intent_id

### 2. **Intent ID Placeholder** (when flattening untracked fills)
- Line 1528: `Flatten("UNKNOWN_UNTrackED_FILL", instrument, utcNow);`
- This is a placeholder intent_id for flattening - not logged as actual intent_id

### 3. **Stream Name** (when stream can't be resolved)
- Line 1445: `stream = stream ?? "UNKNOWN"`
- This is just for logging - not the intent_id

---

## The Real Problem

### Issue: Fills Are Ignored But Position Accumulates

**When intent resolution fails**:
1. ✅ Fill is logged with error
2. ✅ Position is flattened (fail-closed)
3. ❌ **BUT**: If flatten fails or is delayed, position accumulates in NinjaTrader
4. ❌ **AND**: If fill happens BEFORE flatten completes, position grows

### Root Causes

#### 1. **Order Tag Missing/Invalid** (Line 1504)
- Order tag not set correctly during submission
- `GetOrderTag(order)` returns null or invalid tag
- `DecodeIntentId()` returns empty string
- Fill is ignored, but position still fills in NinjaTrader

**Fix Status**: ✅ **Already Fixed** - Untracked fills now flatten immediately (fail-closed)

#### 2. **Order Not in Tracking Map** (Line 1586)
- Order was rejected before being added to `_orderMap`
- But order still fills in NinjaTrader
- Fill arrives but order not in tracking map
- Fill is ignored, but position still accumulates

**Fix Status**: ✅ **Already Fixed** - Retries for race conditions, flattens if still not found

#### 3. **Intent Not in Intent Map** (Line 1270)
- Intent was never registered or was removed
- Fill can't be linked to intent
- Protective orders can't be submitted

**Fix Status**: ✅ **Already Fixed** - Orphan fills logged and position flattened

---

## Why "UNKNOWN" Appears in Logs

The "UNKNOWN" intent_id mentioned in previous analysis documents likely comes from:

1. **Log Analysis Scripts** - May be setting "UNKNOWN" when intent_id is empty/missing
2. **Execution Journal** - May be storing empty string as "UNKNOWN" in analysis
3. **Watchdog/Analysis Tools** - May be displaying empty intent_id as "UNKNOWN"

**The code itself doesn't set `intent_id = "UNKNOWN"`** - it either:
- Has a valid intent_id (from order tag)
- Has empty/null intent_id (which triggers fail-closed flattening)

---

## Current Protection Mechanisms

### ✅ **Fail-Closed Behavior** (All Failure Paths)

1. **Missing Tag** (Line 1504-1581)
   - Logs `EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL`
   - Flattens position immediately
   - Alerts if flatten fails

2. **Order Not in Map** (Line 1586-1695)
   - Retries for race conditions (3 retries, 100ms delay)
   - Logs `EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL` if still not found
   - Flattens position immediately
   - Alerts if flatten fails

3. **Intent Not in Map** (Line 1270-1353)
   - Logs orphan fill
   - Flattens position immediately
   - Alerts if flatten fails

### ✅ **Race Condition Handling** (Line 1594-1626)

- If order state is "Initialized", waits and retries (up to 3 times, 100ms delay)
- Handles threading visibility issues
- Resolves most race conditions automatically

---

## What Still Needs Investigation

### 1. **Why Are Fills Still Accumulating?**

If fail-closed flattening is working, why are positions still accumulating?

**Possible Causes**:
- Flatten is failing silently
- Flatten is delayed (position grows before flatten completes)
- Multiple fills arrive simultaneously before flatten completes
- Flatten is not being called (code path not reached)

**Investigation Needed**:
- Check logs for `UNTrackED_FILL_FLATTENED` events
- Check logs for `UNKNOWN_ORDER_FILL_FLATTENED` events
- Check logs for `ORPHAN_FILL_CRITICAL` events
- Verify flatten is actually being called and succeeding

### 2. **Order Tag Setting**

Are order tags being set correctly during submission?

**Check**:
- Line 515: `RobotOrderIds.EncodeTag(intentId)` - Entry orders
- Line 531: `SetOrderTag(order, RobotOrderIds.EncodeTag(intentId))` - Entry orders
- Line 690: `RobotOrderIds.EncodeTag(intentId)` - Entry orders
- Verify tags are being set BEFORE order submission

### 3. **Intent Registration**

Are intents being registered before order submission?

**Check**:
- `RegisterIntent()` is called before `SubmitEntryOrder()`
- Intent is in `_intentMap` when fill arrives
- Intent is not removed prematurely

---

## Recommendations

### Immediate Actions

1. **Monitor Logs** - After restarting NinjaTrader, check for:
   - `EXECUTION_UPDATE_UNTrackED_FILL_CRITICAL` events
   - `EXECUTION_UPDATE_UNKNOWN_ORDER_CRITICAL` events
   - `ORPHAN_FILL_CRITICAL` events
   - `UNTrackED_FILL_FLATTENED` / `UNKNOWN_ORDER_FILL_FLATTENED` events

2. **Verify Flatten Success** - Check if flatten operations are succeeding:
   - Look for `flatten_success: true` in logs
   - Check for `flatten_error` messages
   - Verify positions are actually being flattened

3. **Check Order Tags** - Verify order tags are being set:
   - Look for `ORDER_CREATED_STOPMARKET` / `ORDER_CREATED_LIMIT` events
   - Verify `order_name` field contains valid tag format
   - Check that tags match expected pattern: `QTSW2:{intentId}`

### If Issues Persist

1. **Add More Logging** - Add detailed logging around:
   - Order tag setting
   - Intent registration
   - Fill processing
   - Flatten operations

2. **Investigate Flatten Failures** - If flatten is failing:
   - Check NinjaTrader API errors
   - Verify account connection
   - Check for position conflicts

3. **Race Condition Analysis** - If race conditions persist:
   - Increase retry delay
   - Add more retry attempts
   - Add synchronization mechanisms

---

## Conclusion

**The code has extensive fail-closed protection** - all failure paths flatten positions immediately. However, if positions are still accumulating, it suggests:

1. **Flatten is failing** - Need to investigate why
2. **Fills are arriving faster than flatten can complete** - May need faster flatten or better synchronization
3. **Order tags are not being set** - Need to verify tag setting during submission
4. **Intents are not being registered** - Need to verify intent registration flow

**Next Step**: Monitor logs after restart to see which failure path is being triggered and why flatten might not be working.
