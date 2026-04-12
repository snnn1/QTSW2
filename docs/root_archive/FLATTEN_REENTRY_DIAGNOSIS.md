# Flatten → Immediate Opposite Re-Entry Diagnosis

## A) VERDICT

**Re-entry is caused by active entry stop orders remaining after position closure because cancellation logic has gaps.**

**Root Cause**: When a position closes (via protective stop/target fill OR manual flatten), the opposite entry stop order remains active. If price is at/through the opposite breakout level, it fills immediately, creating an unwanted re-entry.

**Evidence**:
- `CancelIntentOrders()` only cancels orders for a **specific intentId** (line 3265-3347)
- `FlattenIntentReal()` only cancels entry stops if it can find the intentId in `_intentMap` (line 3484-3550)
- Manual flatten bypasses robot code entirely → no cancellation happens
- `CheckAndCancelEntryStopsOnPositionFlat()` exists but may have timing issues

**File References**:
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:3265-3347` - `CancelIntentOrdersReal()`
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:3352-3577` - `FlattenIntentReal()`
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:3583-3665` - `CheckAndCancelEntryStopsOnPositionFlat()`
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:2046-2119` - Exit fill handler (cancels opposite entry)

---

## B) FLATTEN PATHWAYS ANALYSIS

### Pathway 1: `Flatten()` → `FlattenIntentReal()`

**Callers**:
- `FailClosed()` (line 644) - Fail-closed scenarios
- `TriggerQuantityEmergency()` (line 1388) - Quantity violations
- `HandleExecutionUpdateReal()` (line 1547) - Untracked fills
- `HandleEntryFill()` (line 1956) - Intent not found after entry fill
- `CheckUnprotectedPositions()` (line 1337) - Unprotected position timeout

**Behavior**:
- Calls `FlattenIntentReal()` (line 1207)
- **Cancels entry stops**: YES, but only if `intentId` found in `_intentMap` (line 3484-3550)
- **Cancels protective orders**: NO (left to OCO)
- **Intent ID used**: The `intentId` parameter passed to `Flatten()`

**Gap**: If `intentId` not in `_intentMap`, no cancellation happens.

---

### Pathway 2: `FlattenWithRetry()` → `Flatten()`

**Callers**:
- `FailClosed()` (line 644)
- `TriggerQuantityEmergency()` (line 1388)

**Behavior**:
- Retries `Flatten()` up to 3 times (line 1078-1104)
- Same cancellation behavior as Pathway 1

---

### Pathway 3: `FlattenIntent()` → `FlattenWithRetry()`

**Callers**:
- `RobotEngine.FlattenIntent()` (line 3624-3632)
- `HandleSlotExpiry()` (line 5721, 5734) - Slot expiry

**Behavior**:
- Calls `FlattenWithRetry()` (line 1556)
- Same cancellation behavior as Pathway 1

---

### Pathway 4: Manual Flatten (NinjaTrader UI)

**Callers**:
- User clicks "Flatten" in NinjaTrader UI
- NinjaTrader calls `account.Flatten()` directly
- **Bypasses robot code entirely**

**Behavior**:
- Robot code never executes
- No cancellation happens
- **Gap**: Manual flatten not detected until next execution update

**Mitigation**: `CheckAndCancelEntryStopsOnPositionFlat()` called after execution updates (line 1928, 2038)

---

### Pathway 5: Protective Stop/Target Fill

**Callers**:
- `HandleExecutionUpdateReal()` when `orderTypeForContext == "STOP" || "TARGET"` (line 2046)

**Behavior**:
- **Cancels opposite entry stop**: YES (line 2087)
- Only cancels if opposite entry hasn't filled (line 2084)
- Uses `CancelIntentOrders()` which only cancels entry orders (not protective)

**Gap**: If opposite intent not found in `_intentMap`, cancellation fails silently.

---

## C) ENTRY ORDER SUBMISSION & TAGGING

### Entry Order Tag Format

**Entry Orders**:
- Tag: `QTSW2:{intentId}` (no suffix)
- Created by: `SubmitEntryOrderReal()` (line 171)
- Tag set: `RobotOrderIds.EncodeTag(intentId)` (line 515, 531)
- Example: `QTSW2:ES1_S2_20260205_0930_LONG`

**Protective Stop Orders**:
- Tag: `QTSW2:{intentId}:STOP`
- Created by: `SubmitProtectiveStopReal()` (line 2134)
- Tag set: `RobotOrderIds.EncodeStopTag(intentId)` (line 2188, 2438)
- Example: `QTSW2:ES1_S2_20260205_0930_LONG:STOP`

**Protective Target Orders**:
- Tag: `QTSW2:{intentId}:TARGET`
- Created by: `SubmitTargetOrderReal()` (line 2613)
- Tag set: `RobotOrderIds.EncodeTargetTag(intentId)` (line 2613)
- Example: `QTSW2:ES1_S2_20260205_0930_LONG:TARGET`

**Verification**: All entry orders correctly use `EncodeTag()` without `:STOP`/:TARGET suffix. No entry orders are incorrectly tagged as protective.

---

## D) ORDER TYPE CLASSIFICATION

### Fill Classification Logic

**Location**: `HandleExecutionUpdateReal()` (line 1501-1518)

**Process**:
1. Extract tag from order: `GetOrderTag(order)` (line 1494)
2. Decode intentId: `RobotOrderIds.DecodeIntentId(encodedTag)` (line 1495)
3. Determine order type from tag suffix:
   - If tag ends with `:STOP` → `orderTypeFromTag = "STOP"`, `isProtectiveOrder = true`
   - If tag ends with `:TARGET` → `orderTypeFromTag = "TARGET"`, `isProtectiveOrder = true`
   - Otherwise → Entry order (no suffix)

**Entry vs Protective Decision** (line 1873):
```csharp
bool isEntryFill = !isProtectiveOrder && orderInfo.IsEntryOrder == true;
```

**Gap**: If `_orderMap` lookup fails and tag doesn't have `:STOP`/:TARGET suffix, fill is treated as entry even if it's actually a protective order that wasn't added to `_orderMap`.

---

## E) RE-ENTRY LOGIC ANALYSIS

### Stream Re-Arming After Exit

**Search Results**: No automatic re-arming after exit fills.

**Re-Entry Logic**:
- `CheckMarketOpenReentry()` (line 5888-5967) - Only for `ExecutionInterruptedByClose` (forced flatten at market close)
- Requires `_journal.ExecutionInterruptedByClose == true` (line 925)
- Only submits MARKET entry, not stop orders
- **Does NOT resubmit entry stop brackets**

**Conclusion**: Streams do NOT automatically resubmit entry stop orders after exit. Re-entry is caused by **existing entry stop orders that were never cancelled**.

---

## F) CANCELLATION BEHAVIOR

### `CancelIntentOrdersReal()` Behavior

**Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:3265-3347`

**Process**:
1. Iterates through `account.Orders` (line 3286)
2. Filters: `OrderState == Working || Accepted` (line 3288)
3. Decodes intentId from tag: `RobotOrderIds.DecodeIntentId(tag)` (line 3294)
4. Matches intentId AND ensures tag doesn't end with `:STOP` or `:TARGET` (line 3299-3301)
5. Cancels matching orders (line 3310)

**What It Cancels**:
- ✅ Entry orders: `QTSW2:{intentId}` (no suffix)
- ❌ Protective orders: `QTSW2:{intentId}:STOP` or `:TARGET` (explicitly excluded)

**Gap**: Only cancels orders for the **specific intentId** passed as parameter. Does NOT cancel orders for other intents in the same stream.

---

### `FlattenIntentReal()` Cancellation Logic

**Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:3484-3550`

**Process**:
1. Finds flattened intent in `_intentMap` (line 3484)
2. Extracts stream and tradingDate (line 3487-3488)
3. Searches `_intentMap` for all entry intents matching stream/tradingDate (line 3494-3502)
4. Filters: Only unfilled entries (line 3504-3509)
5. Cancels each unfilled entry intent (line 3519-3548)

**What It Cancels**:
- ✅ Both long and short entry stop orders for the stream
- ✅ Only unfilled entries (checks execution journal)

**Gap**: Only works if `intentId` found in `_intentMap`. If intent was removed or never added, cancellation fails silently.

---

### `CheckAndCancelEntryStopsOnPositionFlat()` Behavior

**Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:3583-3665`

**Process**:
1. Checks if position is flat (line 3607)
2. If flat, finds all entry intents for instrument (line 3613-3633)
3. Filters: Only unfilled entries (line 3635-3642)
4. Cancels each unfilled entry intent (line 3645-3662)

**Call Sites**:
- After entry fill (line 1928)
- After exit fill (line 2038)

**Gap**: Only checks position **after** execution update. If manual flatten happens between execution updates, check is delayed.

---

## G) ROOT CAUSE IDENTIFICATION

### Primary Cause: Incomplete Cancellation Coverage

**Scenario 1: Manual Flatten**
- User clicks "Flatten" → NinjaTrader flattens position
- Robot code never executes
- Entry stop orders remain active
- `CheckAndCancelEntryStopsOnPositionFlat()` only runs on next execution update
- **Race condition**: Entry stop can fill before next execution update

**Scenario 2: Protective Fill with Missing Intent**
- Protective stop/target fills
- Opposite entry cancellation logic runs (line 2046-2119)
- If opposite intent not in `_intentMap`, cancellation fails silently
- Entry stop remains active → re-entry

**Scenario 3: Flatten with Missing Intent**
- `Flatten()` called but `intentId` not in `_intentMap`
- `FlattenIntentReal()` cancellation logic skipped (line 3484 check fails)
- Entry stops remain active → re-entry

---

## H) FIXES (Prioritized by Safety)

### Fix 1: Defensive Cancellation in `CheckAndCancelEntryStopsOnPositionFlat()` (HIGH PRIORITY)

**Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:3583-3665`

**Issue**: Only checks position after execution updates. Manual flatten may not trigger execution update immediately.

**Fix**: Add periodic position check OR enhance to check all instruments, not just the one from execution update.

**Implementation**:
```csharp
// After line 2038, also check ALL instruments with robot orders
// Iterate through _intentMap, group by instrument, check each position
```

**Safety**: Fail-closed - cancels orders defensively.

---

### Fix 2: Enhance `CancelIntentOrdersReal()` to Cancel by Stream (MEDIUM PRIORITY)

**Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs:3265-3347`

**Issue**: Only cancels orders for specific `intentId`. Doesn't cancel opposite entry for same stream.

**Fix**: Add optional parameter to cancel all entry orders for a stream:
```csharp
private bool CancelIntentOrdersReal(string intentId, DateTimeOffset utcNow, bool cancelOppositeEntry = false)
{
    // ... existing logic ...
    
    if (cancelOppositeEntry && _intentMap.TryGetValue(intentId, out var intent))
    {
        // Find opposite entry intent and cancel it
        // (similar to logic in line 2046-2119)
    }
}
```

**Safety**: Fail-closed - cancels orders defensively.

---

### Fix 3: Add Position Monitoring Thread/Timer (LOW PRIORITY)

**Issue**: Position flat detection only happens on execution updates.

**Fix**: Add periodic position check (every 1-2 seconds) that cancels entry stops when position goes flat.

**Safety**: Fail-closed but adds complexity.

---

## I) VERIFICATION CHECKLIST

### Log Event Types to Search

**1. Position Closure Events**:
```bash
grep -E "EXECUTION_EXIT_FILL|FLATTEN_ATTEMPT|FLATTEN_INTENT_SUCCESS" robot_logs.jsonl | jq 'select(.event_type == "EXECUTION_EXIT_FILL" or .event_type == "FLATTEN_INTENT_SUCCESS")'
```

**2. Entry Stop Cancellation Events**:
```bash
grep -E "OPPOSITE_ENTRY_CANCELLED_ON_EXIT_FILL|ENTRY_STOP_CANCELLED_ON_MANUAL_FLATTEN|ENTRY_STOP_CANCELLED_ON_POSITION_FLAT|CANCEL_INTENT_ORDERS_SUCCESS" robot_logs.jsonl | jq 'select(.event_type | contains("CANCELLED") or contains("CANCEL"))'
```

**3. Entry Order Fills (Re-Entry)**:
```bash
grep -E "EXECUTION_ENTRY_FILL|EXECUTION_FILLED" robot_logs.jsonl | jq 'select(.order_type == null or .order_type == "") | select(.tag | test("^QTSW2:[^:]+$"))'
```

**4. Working Entry Orders After Exit**:
```bash
grep -E "ORDER_SUBMIT_SUCCESS|STOP_BRACKETS_SUBMIT" robot_logs.jsonl | jq 'select(.order_type == "ENTRY" or .trigger_reason | contains("ENTRY_STOP_BRACKET"))'
```

### Verification Steps

**Step 1: Find Re-Entry Incident**
```bash
# Find protective fill or flatten event
grep -E "EXECUTION_EXIT_FILL.*exit_order_type.*(STOP|TARGET)|FLATTEN_INTENT_SUCCESS" robot_logs.jsonl | jq -r '.timestamp, .intent_id, .instrument, .exit_order_type // .event_type'

# Find subsequent entry fill within 5 seconds
grep -E "EXECUTION_ENTRY_FILL|EXECUTION_FILLED" robot_logs.jsonl | jq 'select(.timestamp > "<previous_timestamp>" and .timestamp < "<previous_timestamp + 5s")'
```

**Step 2: Check for Cancellation Events**
```bash
# Between exit/flatten and re-entry, check for cancellation
grep -E "OPPOSITE_ENTRY_CANCELLED|ENTRY_STOP_CANCELLED|CANCEL_INTENT_ORDERS_SUCCESS" robot_logs.jsonl | jq 'select(.timestamp > "<exit_timestamp>" and .timestamp < "<reentry_timestamp>")'
```

**Step 3: Verify Entry Order Tags**
```bash
# Check re-entry order tag format
grep -E "EXECUTION_ENTRY_FILL|EXECUTION_FILLED" robot_logs.jsonl | jq 'select(.timestamp == "<reentry_timestamp>") | {tag: .tag, intent_id: .intent_id, order_type: .order_type}'
```

**Expected**: Tag should be `QTSW2:{intentId}` (no `:STOP`/:TARGET suffix), indicating it's an entry order.

**Step 4: Check Intent Map State**
```bash
# Check if opposite intent was in _intentMap at time of exit
grep -E "OPPOSITE_ENTRY_ALREADY_FILLED_SKIP_CANCEL|OPPOSITE_ENTRY_CANCELLED_ON_EXIT_FILL" robot_logs.jsonl | jq 'select(.timestamp == "<exit_timestamp>")'
```

**Expected**: Should see `OPPOSITE_ENTRY_CANCELLED_ON_EXIT_FILL` if cancellation succeeded, or `OPPOSITE_ENTRY_ALREADY_FILLED_SKIP_CANCEL` if opposite entry already filled.

---

## J) MINIMAL REPRODUCTION SEQUENCE

**Timeline** (from user's logs):
```
19:54:27.973 - Entry fill: Buy Market, tag=QTSW2:{intentId}
19:54:31.314 - Manual flatten: Buy to cover Market (no tag - manual)
19:54:34.000 - Position shows Long Quantity=2 (re-entry occurred)
```

**What Happened**:
1. Entry filled at 19:54:27
2. User manually flattened at 19:54:31 (bypassed robot code)
3. Entry stop orders remained active (not cancelled)
4. Opposite entry stop filled immediately (price at/through breakout level)
5. Re-entry occurred

**Missing Events**:
- No `ENTRY_STOP_CANCELLED_ON_MANUAL_FLATTEN` event (flatten didn't call robot code)
- No `ENTRY_STOP_CANCELLED_ON_POSITION_FLAT` event (check only runs on execution updates)
- No `OPPOSITE_ENTRY_CANCELLED_ON_EXIT_FILL` event (no exit fill - manual flatten)

---

## K) SUMMARY

**Root Cause**: Entry stop orders remain active after position closure due to:
1. Manual flatten bypasses robot code
2. Cancellation logic requires intentId in `_intentMap`
3. Position flat check only runs on execution updates

**Primary Fix**: Enhance `CheckAndCancelEntryStopsOnPositionFlat()` to check ALL instruments periodically, not just on execution updates.

**Secondary Fix**: Add defensive cancellation in `CancelIntentOrdersReal()` to cancel opposite entry when cancelling an intent.

**Verification**: Search logs for cancellation events between exit/flatten and re-entry. If missing, fix is not working.
