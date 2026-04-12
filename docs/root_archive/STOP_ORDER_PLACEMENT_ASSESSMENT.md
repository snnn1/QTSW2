# Stop Order Placement Assessment

## Summary
**YES, stop orders WILL be placed** - but only if specific conditions are met. The code has multiple safeguards and failure points that could prevent placement.

---

## Entry Stop Orders (Breakout Orders) - Placed at RANGE_LOCKED

### Call Chain
1. `HandleRangeBuildingState()` → transitions to `RANGE_LOCKED` (line 2373)
2. `CheckImmediateEntryAtLock()` → checks if price already at breakout (line 2448)
3. `SubmitStopEntryBracketsAtLock()` → places stop orders (line 2456)

### Conditions Required (ALL must pass):

#### Level 1: Entry Point Guard (line 2454)
- ✅ `!_entryDetected` - Entry not already detected
- ✅ `utcNow < MarketCloseUtc` - Before market close cutoff

**⚠️ CRITICAL:** If `CheckImmediateEntryAtLock()` detects price is already at/through breakout level, it sets `_entryDetected = true`, which **prevents stop orders from being placed**. This is **by design** - immediate entry takes precedence over stop orders.

#### Level 2: SubmitStopEntryBracketsAtLock Preconditions (lines 3182-3189)
- ✅ `!_stopBracketsSubmittedAtLock` - Not already submitted (idempotency)
- ✅ `!_journal.Committed && State != StreamState.DONE` - Stream still active
- ✅ `!_rangeInvalidated` - Range is valid (gap tolerance violations disabled, but flag still checked)
- ✅ `_executionAdapter != null` - Execution adapter initialized
- ✅ `_executionJournal != null` - Execution journal initialized
- ✅ `_riskGate != null` - Risk gate initialized
- ✅ `_brkLongRounded.HasValue && _brkShortRounded.HasValue` - Breakout levels computed
- ✅ `RangeHigh.HasValue && RangeLow.HasValue` - Range values exist

#### Level 3: Risk Gate Check (lines 3193-3211)
- ✅ `CheckGates()` returns `allowed = true`
  - Kill switch NOT enabled
  - Timetable validated
  - Stream armed (not committed/done)
  - Recovery guard allows execution (if present)

#### Level 4: Order Submission (lines 3295-3296)
- ✅ `SubmitStopEntryOrder()` succeeds for BOTH long and short orders
  - Order creation succeeds (3 fallback attempts for StopMarket orders)
  - Order submission to NinjaTrader succeeds (`account.Submit()`)
  - Order not rejected by broker

### Potential Failure Points:

1. **Immediate Entry Detected** (line 2448)
   - If `freezeClose >= brkLong` OR `freezeClose <= brkShort` at lock time
   - **Result:** `_entryDetected = true` → stop orders skipped (by design)

2. **Risk Gate Blocks** (line 3204)
   - Kill switch enabled
   - Timetable not validated
   - Stream committed/done
   - Recovery guard blocking
   - **Result:** Logs `STOP_BRACKETS_SUBMIT_ATTEMPT` but returns early, no orders placed

3. **Order Creation Fails** (lines 500-574)
   - All 3 CreateOrder signature attempts fail
   - **Result:** Logs `ORDER_CREATE_FAIL`, returns `FailureResult`, stop orders NOT placed

4. **Order Submission Fails** (lines 774-798)
   - `account.Submit()` throws exception
   - Order rejected by NinjaTrader (`OrderState.Rejected`)
   - **Result:** Logs `ORDER_SUBMITTED` with rejection, stop orders NOT placed

5. **Partial Failure** (lines 3310-3346)
   - One order succeeds, one fails
   - **Result:** Logs `STOP_BRACKETS_SUBMIT_FAILED`, flag NOT set, may retry on next Tick()

### Success Indicators:
- ✅ Log event: `STOP_BRACKETS_SUBMITTED` (line 3318)
- ✅ `_stopBracketsSubmittedAtLock = true` (line 3312)
- ✅ Journal persisted: `StopBracketsSubmittedAtLock = true` (line 3315)
- ✅ Both orders logged: `ORDER_CREATED_STOPMARKET` (line 582)
- ✅ Both orders submitted: `ORDER_SUBMITTED` (line 846)

---

## Protective Stop-Loss Orders - Placed After Entry Fill

### Call Chain
1. Entry order fills → `OnExecutionUpdate()` callback (NinjaTrader)
2. `HandleEntryFill()` → submits protective orders (line 330)
3. `SubmitProtectiveStop()` → places stop-loss order (line 379)
4. `SubmitTargetOrder()` → places target order (line 408)

### Conditions Required:

#### Level 1: Entry Fill Detection
- ✅ Entry order fills (long OR short)
- ✅ Fill callback received from NinjaTrader
- ✅ Intent registered in adapter

#### Level 2: Protective Order Submission (lines 350-418)
- ✅ `_coordinator.CanSubmitExit()` returns true (for each retry attempt)
- ✅ `SubmitProtectiveStop()` succeeds (3 retries, 100ms delay)
- ✅ `SubmitTargetOrder()` succeeds (3 retries, 100ms delay)

### Failure Handling (lines 421-467):
- ❌ **If either protective order fails after 3 retries:**
  - Position is **flattened immediately** (`Flatten()`)
  - Stream is **stood down** (`_standDownStreamCallback()`)
  - High-priority notification sent
  - Logs `PROTECTIVE_ORDERS_FAILED_FLATTENED`

### Success Indicators:
- ✅ Log event: `PROTECTIVE_ORDERS_SUBMITTED` (line 471)
- ✅ Log event: `PROTECTIVES_PLACED` (line 485)
- ✅ Both orders logged: `ORDER_CREATED_STOPMARKET` (for stop) and `ORDER_CREATED_LIMIT` (for target)

---

## Code Verification Summary

### ✅ What WILL Work:
1. **Stop entry orders WILL be placed** at RANGE_LOCKED if:
   - No immediate entry detected
   - All preconditions met
   - Risk gates pass
   - Order creation/submission succeeds

2. **Protective stop orders WILL be placed** after entry fill if:
   - Entry order fills successfully
   - Exit coordinator allows submission
   - Order creation/submission succeeds (with retries)

### ⚠️ What Might Prevent Placement:

1. **Immediate Entry Scenario:**
   - If price is already at/through breakout at lock time
   - Stop orders are **intentionally skipped** (immediate entry takes precedence)

2. **Risk Gate Blocks:**
   - Kill switch enabled
   - Stream committed/done
   - Recovery guard blocking

3. **Order Creation Failures:**
   - NinjaTrader API incompatibility (all 3 signature attempts fail)
   - Missing required parameters

4. **Order Submission Failures:**
   - NinjaTrader rejects order
   - Account connection issues
   - Insufficient margin (unlikely in SIM)

5. **Partial Failures:**
   - One order succeeds, one fails
   - System logs failure but doesn't retry automatically

---

## Recommendations for Verification:

1. **Check logs for NG1 at 07:30:**
   - Look for `STOP_BRACKETS_SUBMIT_ATTEMPT` event
   - Check if `STOP_BRACKETS_SUBMITTED` or `STOP_BRACKETS_SUBMIT_FAILED` follows
   - Verify `ORDER_CREATED_STOPMARKET` events for both long and short

2. **Monitor risk gates:**
   - Check for `STOP_BRACKETS_SUBMIT_ATTEMPT` → if missing, risk gate likely blocked
   - Look for `LogBlocked` events from risk gate

3. **Verify immediate entry logic:**
   - If `IMMEDIATE_AT_LOCK` entry detected, stop orders are skipped (expected behavior)
   - Check `freezeClose` vs `brkLong`/`brkShort` at lock time

4. **Check order submission:**
   - Look for `ORDER_SUBMITTED` events with `order_state = "Working"`
   - If `order_state = "Rejected"`, check rejection reason

---

## Conclusion

The code **WILL place stop orders** if all conditions are met. The implementation includes:
- ✅ Proper idempotency checks
- ✅ Risk gate validation
- ✅ Multiple fallback attempts for order creation
- ✅ Comprehensive error logging
- ✅ Retry logic for protective orders

However, there are **legitimate scenarios** where stop orders won't be placed:
- Immediate entry detected (by design)
- Risk gates blocking execution
- Order creation/submission failures

**All failure paths are logged**, making it easy to diagnose why orders weren't placed.
