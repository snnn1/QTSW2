# Friday Stop Order Issue - Risk Assessment (CORRECTED)

## Executive Summary

**Critical Finding**: Protective stop orders did NOT check the **Recovery Guard enforcement boundary**, despite documentation stating they should be blocked during disconnect recovery. This creates a vulnerability where protective orders may fail silently if attempted during recovery states.

**Key Correction**: This is NOT a "RiskGate issue" - it's a **Recovery Guard enforcement boundary** issue. The requirement is:

> **Any path that can create or maintain exposure must be constrained by the recovery guard, except emergency flatten.**

RiskGate is just where enforcement was expected to happen, but the real requirement is recovery guard enforcement at the boundary.

## Issue Analysis

### 1. Current Implementation

**Protective Order Submission Path:**
```
HandleEntryFill() 
  -> SubmitProtectiveStop() 
    -> SubmitProtectiveStopReal() 
      -> NT API (account.CreateOrder + account.Submit)
```

**Key Finding**: No Recovery Guard check (`IsExecutionAllowed()`) in the protective order path.

### 2. Documentation vs Reality

**Documentation (RiskGate.cs lines 13-16):**
> "During DISCONNECT_FAIL_CLOSED and RECONNECTED_RECOVERY_PENDING:
> - Protective orders go through CheckGates() → blocked"

**Reality**: Protective orders bypassed Recovery Guard entirely. They only checked:
- SIM account verification
- NT context set
- Coordinator validation (_coordinator.CanSubmitExit)
- Intent completeness

### 3. Friday Scenario Analysis

**Most Likely Scenario:**
1. Entry order filled successfully
2. Disconnect occurred (or was in progress)
3. Recovery state = DISCONNECT_FAIL_CLOSED or RECONNECTED_RECOVERY_PENDING
4. HandleEntryFill() called (entry fill callback received)
5. SubmitProtectiveStop() attempted (no recovery guard check)
6. NT API unavailable or broker rejecting orders during disconnect
7. Orders failed silently or with errors
8. Position left unprotected

**Alternative Scenarios:**
- Recovery state stuck in RECONNECTED_RECOVERY_PENDING (broker sync never completes)
- Market close timing (orders rejected near close)
- NT API exceptions during recovery

## Answers to Critical Questions

### Q1: Does emergency flatten bypass the recovery guard?

**Answer**: ✅ **YES** - Emergency flatten bypasses both RiskGate AND recovery guard.

**Evidence**:
- `Flatten()` calls `FlattenIntentReal()` directly on adapter
- No RiskGate.CheckGates() call
- No recovery guard check
- Documentation explicitly states: "Flatten operations call adapter's Flatten() directly → permitted (bypasses RiskGate)"

**Status**: ✅ **CORRECT** - Flatten must be permitted even when guard blocks.

### Q2: Do you have a hard guarantee that "protective order submission failure" triggers flatten?

**Answer**: ✅ **YES** - Flatten is guaranteed (not best-effort) if protective orders fail.

**Evidence**:
- `HandleEntryFill()` line 548: `if (!stopResult.Success || !targetResult.Success)` → guaranteed flatten path
- Line 560: `FlattenWithRetry()` called unconditionally
- Line 563: Stream stand-down called unconditionally
- Line 566: Incident persistence called unconditionally
- Line 574: Emergency notification sent unconditionally

**Status**: ✅ **HARD GUARANTEE** - Same hardness as "Intent incomplete → flatten".

### Q3: Do you have an explicit "protectives confirmed live" signal?

**Answer**: ⚠️ **PARTIAL** - Order acknowledgment tracked, but rejection does NOT trigger flatten.

**Evidence**:
- OrderState.Accepted sets `ProtectiveStopAcknowledged = true` and `ProtectiveTargetAcknowledged = true` (line 841-846)
- OrderState.Rejected logs `ORDER_REJECTED` but does NOT trigger flatten
- Order rejection is logged but position remains unprotected

**Status**: ⚠️ **GAP IDENTIFIED** - Order rejection should trigger same fail-closed pathway as submission failure.

**Risk**: If protective order is submitted successfully but broker rejects it later, position remains unprotected.

## Risk Assessment

### High Risk Factors

1. **Protective Orders Bypassed Recovery Guard** ⚠️ CRITICAL
   - Risk: Orders attempted during disconnect when broker unavailable
   - Impact: Unprotected positions
   - Likelihood: Medium (depends on disconnect timing)
   - **Status**: ✅ **FIXED** - Recovery guard check now implemented

2. **Order Rejection Does Not Trigger Flatten** ⚠️ HIGH
   - Risk: Protective order submitted but broker rejects it
   - Impact: Position unprotected, no automatic flatten
   - Likelihood: Low-Medium (broker rejections can occur)
   - **Status**: ⚠️ **NOT FIXED** - Needs implementation

### Medium Risk Factors

1. **Retry Logic for "Connected but Flaky"** ⚠️ MEDIUM
   - Current: 3 retries with 100ms delay
   - Risk: Transient NT API failures not recovered
   - Impact: Orders fail permanently
   - Likelihood: Low-Medium
   - **Note**: This is for "connected but flaky" scenarios, NOT recovery scenarios
   - **Status**: ⚠️ **COULD BE IMPROVED** - Exponential backoff recommended

2. **Recovery State May Not Clear** ⚠️ MEDIUM
   - Risk: RECONNECTED_RECOVERY_PENDING never transitions to RECOVERY_COMPLETE
   - Impact: Protective orders blocked indefinitely (if fix applied)
   - Likelihood: Low (broker sync should complete)
   - **Note**: If queueing is added, MUST have explicit timeout
   - **Status**: ⚠️ **MONITORING NEEDED**

### Low Risk Factors

1. **Intent Incomplete** ✅ LOW
   - Current: Checked and handled (flattens position)
   - Risk: Minimal (validation catches this)

2. **SIM Account Not Verified** ✅ LOW
   - Current: Checked before orders
   - Risk: Minimal (verified at startup)

## Recurrence Risk

### Probability: **LOW** (after fix)

**Why it's LOW now:**
1. ✅ Recovery guard check implemented
2. ✅ Protective orders blocked during recovery
3. ✅ Position flattened immediately (fail-closed)

**Remaining Risks:**
1. ⚠️ Order rejection after submission (not handled)
2. ⚠️ Recovery state stuck (monitoring needed)
3. ⚠️ Transient NT API failures (retry could be improved)

## Implemented Fixes

### ✅ Fix 1: Add Recovery Guard Check to Protective Orders (IMPLEMENTED)

**Status**: ✅ **IMPLEMENTED**

**Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.cs`

**Change**: Added recovery guard check in `HandleEntryFill()` before submitting protective orders:

- Added `_isExecutionAllowedCallback` field to adapter
- Updated `SetEngineCallbacks()` to accept recovery state check callback
- Added recovery state check in `HandleEntryFill()` before protective order submission
- If blocked during recovery, position is flattened immediately (fail-closed behavior)
- High-priority alert sent when protective orders blocked

**Behavior**:
- If recovery state blocks execution, protective orders are NOT attempted
- Position is flattened immediately (fail-closed)
- Stream is stood down
- Alert is sent

**Note**: Current implementation flattens immediately. Future enhancement: Bounded grace window (5-10s wait for recovery to clear, then flatten).

## Additional Recommended Fixes

### ✅ Fix 2: Handle Order Rejection → Flatten (IMPLEMENTED)

**Status**: ✅ **IMPLEMENTED**

**Priority**: **CRITICAL** - Order rejection leaves position unprotected

**Location**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - `HandleOrderUpdateReal()`

**Change**: When protective order (STOP or TARGET) is rejected:
- Check if order is protective (OrderType == "STOP" or "TARGET")
- If protective order rejected, trigger same fail-closed pathway:
  - Call `OnProtectiveFailure()`
  - Call `FlattenWithRetry()`
  - Stand down stream
  - Persist incident
  - Send emergency notification
  - Log `PROTECTIVE_ORDER_REJECTED_FLATTENED` event

**Rationale**: Order submission success != order acceptance. Broker can reject orders after submission, leaving position unprotected.

**Status**: ✅ **IMPLEMENTED** - Protective order rejection now triggers immediate flatten (fail-closed behavior).

### Fix 3: Enhanced Retry with Exponential Backoff (MEDIUM PRIORITY)

**Purpose**: Handle "connected but flaky" scenarios (transient NT API failures)

**Current**: 3 retries, 100ms delay

**Proposed**: 
- 5 retries
- Exponential backoff: 100ms, 200ms, 400ms, 800ms, 1600ms
- Check recovery state between retries (if recovery occurs, abort retries)

**Note**: This is for "connected but flaky", NOT recovery scenarios. Recovery guard handles "system-level disconnection".

### Fix 4: Recovery State Monitoring with Timeout (MEDIUM PRIORITY)

**Purpose**: Ensure recovery state clears properly, especially if queueing is added

**Implementation**:
- Timeout for RECONNECTED_RECOVERY_PENDING (e.g., 5 minutes)
- Auto-transition to RECONNECTED_RECOVERY_PENDING → RECOVERY_COMPLETE if timeout exceeded
- Log warning if recovery takes too long
- **CRITICAL**: If queueing is added, MUST have explicit timeout to prevent indefinite exposure

### Fix 5: Bounded Protective Grace Window (OPTIONAL - Near-term)

**Purpose**: Get 80% of queueing benefit with 20% of complexity

**Implementation**:
- If recovery guard blocks protective orders, wait up to N seconds (5-10s max) for recovery to clear
- If recovery clears within N seconds, submit protective orders
- If not, flatten immediately

**Trade-off**: Reduces "panic exits" during brief disconnects, but adds complexity. Staying with immediate flatten is cleaner fail-closed posture.

## Implementation Priority

1. **Fix 2 (Order Rejection Handling)**: CRITICAL - Prevents unprotected positions from broker rejections
2. **Fix 1 (Recovery Guard Check)**: ✅ COMPLETE - Prevents recurrence
3. **Fix 4 (Recovery Monitoring)**: MEDIUM - Prevents stuck states (required if queueing added)
4. **Fix 3 (Enhanced Retry)**: MEDIUM - Improves resilience for "connected but flaky"
5. **Fix 5 (Grace Window)**: OPTIONAL - Only if fewer "panic exits" desired

## Testing Recommendations

1. **Disconnect During Entry Fill Test**:
   - Simulate disconnect during entry fill
   - Verify protective orders are blocked/queued
   - Verify recovery completion triggers submission (if grace window implemented)

2. **Order Rejection Test**:
   - Simulate broker rejection of protective order
   - Verify flatten is triggered
   - Verify all fail-closed pathways execute

3. **Recovery State Stuck Test**:
   - Simulate recovery state that never completes
   - Verify timeout mechanism (if implemented)
   - Verify protective orders queued/flattened appropriately

4. **NT API Failure Test**:
   - Simulate NT API exceptions during protective order submission
   - Verify retry logic
   - Verify fail-closed behavior

## Invariant Log Events (Required)

**Must confirm these events exist and are emitted:**

1. ✅ `PROTECTIVE_ORDERS_SUBMITTED` - Emitted when both stop and target submitted successfully (line 598)
2. ✅ `PROTECTIVE_ORDERS_FAILED_FLATTENED` - Emitted when protective orders fail (line 577)
3. ✅ `PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED` - Emitted when blocked by recovery guard (line 453)
4. ✅ `PROTECTIVE_ORDER_REJECTED_FLATTENED` - **IMPLEMENTED** - Emitted when broker rejects protective order

**Every protective failure path must end in:**
- ✅ Flatten attempt with retry
- ✅ Stream stand down
- ✅ Incident persistence
- ✅ Emergency notification

## Conclusion

The Friday issue was caused by protective orders being attempted during a disconnect/recovery state when the NT API was unavailable. The code did NOT check recovery state before submitting protective orders, creating a vulnerability.

**✅ Fix Status**: **Fix 1 has been IMPLEMENTED** - Recovery guard check now blocks protective orders during disconnect recovery.

**Recurrence Risk**: 
- **Before Fix**: **MEDIUM-HIGH** - Issue would recur on any disconnect during active trading
- **After Fix**: **LOW** - Protective orders blocked during recovery, position flattened (fail-closed)

**Remaining Gaps**:
- ✅ Order rejection after submission now triggers flatten (Fix 2 implemented)
- ⚠️ Recovery state monitoring needed (Fix 4 recommended, especially if queueing added)
- ⚠️ Retry logic could be improved for "connected but flaky" (Fix 3 optional)

**Current Behavior**:
- If entry fills during recovery state, protective orders are blocked
- Position is flattened immediately (fail-closed behavior)
- High-priority alert is sent
- Stream is stood down

**Status**: ✅ **Both critical fixes implemented**:
1. ✅ Recovery guard check blocks protective orders during disconnect
2. ✅ Order rejection triggers immediate flatten (fail-closed)

**Next Optional Actions**: 
- Consider Fix 4 (Recovery monitoring) if queueing is added
- Consider Fix 3 (Enhanced retry) for "connected but flaky" scenarios
- Consider Fix 5 (Grace window) only if fewer "panic exits" desired

---

## Implementation Summary

### ✅ Fixes Implemented

1. **Recovery Guard Enforcement** (Fix 1)
   - Added `_isExecutionAllowedCallback` to adapter
   - Recovery guard check in `HandleEntryFill()` before protective order submission
   - If blocked, position flattened immediately (fail-closed)
   - Event: `PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED`

2. **Order Rejection Handling** (Fix 2)
   - Protective order rejection detection in `HandleOrderUpdateReal()`
   - Triggers same fail-closed pathway as submission failure
   - Event: `PROTECTIVE_ORDER_REJECTED_FLATTENED`

### Key Corrections Applied

1. **Terminology**: Changed from "RiskGate issue" to "Recovery Guard enforcement boundary" issue
   - Requirement: Any path creating/maintaining exposure must be constrained by recovery guard (except emergency flatten)

2. **Retry Logic Classification**: 
   - Retry/backoff = "connected but flaky" (transient NT API failures)
   - Recovery guard = "system-level disconnection" (broker unavailable)
   - Different classes of issues

3. **Recovery Monitoring**: 
   - If queueing is added, MUST have explicit timeout for RECONNECTED_RECOVERY_PENDING
   - Prevents indefinite exposure risk

### Answers to Critical Questions

**Q1: Does emergency flatten bypass recovery guard?**
- ✅ YES - Flatten bypasses both RiskGate and recovery guard
- ✅ CORRECT - Flatten must be permitted even when guard blocks

**Q2: Hard guarantee for protective failure → flatten?**
- ✅ YES - Flatten is guaranteed (not best-effort)
- ✅ Same hardness as "Intent incomplete → flatten"

**Q3: Explicit "protectives confirmed live" signal?**
- ✅ PARTIAL - Order acknowledgment tracked (`ProtectiveStopAcknowledged`, `ProtectiveTargetAcknowledged`)
- ✅ FIXED - Order rejection now triggers flatten (was missing, now implemented)

### Invariant Log Events (Confirmed)

1. ✅ `PROTECTIVE_ORDERS_SUBMITTED` - Both stop and target submitted successfully
2. ✅ `PROTECTIVE_ORDERS_FAILED_FLATTENED` - Submission failure after retries
3. ✅ `PROTECTIVE_ORDERS_BLOCKED_RECOVERY_FLATTENED` - Blocked by recovery guard
4. ✅ `PROTECTIVE_ORDER_REJECTED_FLATTENED` - Broker rejection (newly implemented)

**Every protective failure path ends in:**
- ✅ Flatten attempt with retry
- ✅ Stream stand down
- ✅ Incident persistence
- ✅ Emergency notification

### Final Risk Assessment

**Recurrence Risk**: **LOW** (after fixes)

**Why LOW**:
1. ✅ Recovery guard blocks protective orders during disconnect
2. ✅ Order rejection triggers immediate flatten
3. ✅ All failure paths end in fail-closed behavior
4. ✅ Invariant log events confirmed

**Remaining Optional Improvements**:
- Recovery state monitoring (if queueing added)
- Enhanced retry for "connected but flaky"
- Bounded grace window (only if fewer "panic exits" desired)
