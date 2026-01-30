# Tick and Heartbeat Events Cleanup Plan

**Date**: 2026-01-30  
**Purpose**: Remove deprecated, optional, and unused tick/heartbeat events  
**Status**: Ready to execute

---

## Overview

**Total Events to Remove**: 8 events  
**Files to Modify**: 6 files  
**Risk Level**: Low (all events are optional/deprecated)

---

## Cleanup Summary

### ✅ KEEP (5 events - Critical/Important)
1. **ENGINE_TICK_CALLSITE** - Primary watchdog liveness (37K+ events)
2. **ENGINE_TICK_STALL_DETECTED** - Failure detection
3. **ENGINE_TICK_STALL_RECOVERED** - Recovery tracking
4. **HEARTBEAT (stream-level)** - Stream health monitoring
5. **SUSPENDED_STREAM_HEARTBEAT** - Suspended stream monitoring

### ❌ REMOVE (8 events)

#### Phase 1: Optional Diagnostic Events (3 events)
1. **TICK_CALLED_FROM_ONMARKETDATA** - Diagnostic for fix verification (no longer needed)
2. **ENGINE_TICK_HEARTBEAT** - Bar-driven heartbeat (diagnostic only)
3. **ENGINE_BAR_HEARTBEAT** - Bar ingress diagnostic (diagnostic only)

#### Phase 2: Deprecated/Unused Events (5 events)
4. **ENGINE_HEARTBEAT** - Deprecated (replaced by ENGINE_TICK_CALLSITE)
5. **ENGINE_TICK_HEARTBEAT_AUDIT** - Not implemented
6. **TICK_METHOD_ENTERED** - Optional diagnostic (not critical)
7. **TICK_CALLED** - Optional diagnostic (not critical)
8. **TICK_TRACE** - Optional diagnostic (not critical)

---

## Files to Modify

### 1. `modules/robot/core/RobotEventTypes.cs`
- Remove event type definitions
- Remove from `_allEvents` HashSet

### 2. `modules/robot/core/RobotEngine.cs`
- Remove `ENGINE_TICK_HEARTBEAT` logging code
- Remove `ENGINE_BAR_HEARTBEAT` logging code
- Remove related tracking dictionaries

### 3. `modules/robot/core/StreamStateMachine.cs`
- Remove `TICK_METHOD_ENTERED` logging code
- Remove `TICK_CALLED` logging code
- Remove `TICK_TRACE` logging code
- Remove related rate-limiting variables

### 4. `modules/robot/ninjatrader/RobotSimStrategy.cs`
- Remove `TICK_CALLED_FROM_ONMARKETDATA` logging code
- Remove related rate-limiting dictionary

### 5. `modules/robot/ninjatrader/HeartbeatAddOn.cs` (if exists)
- Remove entire file (deprecated ENGINE_HEARTBEAT)

### 6. `modules/robot/ninjatrader/HeartbeatStrategy.cs` (if exists)
- Remove entire file (deprecated ENGINE_HEARTBEAT)

---

## Detailed Cleanup Steps

### Phase 1: Remove Optional Diagnostic Events

#### Step 1.1: Remove TICK_CALLED_FROM_ONMARKETDATA

**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

**Remove**:
- Rate-limiting dictionary: `_lastOnMarketDataTickLogUtc` (if exists)
- Rate-limiting constant: `ON_MARKET_DATA_TICK_RATE_LIMIT_MINUTES` (if exists)
- Logging code block (lines ~1291-1305)

**Location**: Around line 1297 in `OnMarketData()` method

---

#### Step 1.2: Remove ENGINE_TICK_HEARTBEAT

**File**: `modules/robot/core/RobotEngine.cs`

**Remove**:
- Tracking dictionaries (lines ~92-95):
  - `_lastBarDrivenHeartbeatPerInstrument`
  - `_barsSinceLastHeartbeatPerInstrument`
- Rate-limiting constant: `BAR_DRIVEN_HEARTBEAT_RATE_LIMIT_SECONDS`
- Method: `EmitBarDrivenHeartbeatIfNeeded()` (around line 1766)
- All calls to `EmitBarDrivenHeartbeatIfNeeded()`
- Logging code that emits `ENGINE_TICK_HEARTBEAT` event

---

#### Step 1.3: Remove ENGINE_BAR_HEARTBEAT

**File**: `modules/robot/core/RobotEngine.cs`

**Remove**:
- Tracking dictionary: `_lastBarHeartbeatPerInstrument` (line ~89)
- Rate-limiting property: `BAR_HEARTBEAT_RATE_LIMIT_MINUTES` (line ~90)
- Logging code that emits `ENGINE_BAR_HEARTBEAT` event (around line 1345)

---

### Phase 2: Remove Deprecated/Unused Events

#### Step 2.1: Remove ENGINE_HEARTBEAT

**Files**:
- `modules/robot/ninjatrader/HeartbeatAddOn.cs` - Delete entire file
- `modules/robot/ninjatrader/HeartbeatStrategy.cs` - Delete entire file

**Note**: These files exist only to emit deprecated ENGINE_HEARTBEAT events.

---

#### Step 2.2: Remove TICK_METHOD_ENTERED, TICK_CALLED, TICK_TRACE

**File**: `modules/robot/core/StreamStateMachine.cs`

**Remove**:
- Rate-limiting variables (lines ~176-177):
  - `_lastTickTraceUtc`
  - `_lastTickCalledUtc`
- Logging code for `TICK_METHOD_ENTERED` (around line 782)
- Logging code for `TICK_CALLED` (around line 857)
- Logging code for `TICK_TRACE` (around line 906)
- Error handling for `TICK_METHOD_ENTERED_ERROR` (around line 826)

---

#### Step 2.3: Remove ENGINE_TICK_HEARTBEAT_AUDIT

**File**: `modules/robot/core/RobotEventTypes.cs`

**Remove**: Event type definition (if any logging code exists, remove it)

---

### Phase 3: Update Event Type Registry

#### Step 3.1: Remove from RobotEventTypes.cs

**File**: `modules/robot/core/RobotEventTypes.cs`

**Remove from `_eventLevels` dictionary** (lines ~73-76, ~328-331):
- `ENGINE_HEARTBEAT`
- `ENGINE_TICK_HEARTBEAT`
- `ENGINE_TICK_HEARTBEAT_AUDIT`
- `ENGINE_BAR_HEARTBEAT`
- `TICK_METHOD_ENTERED`
- `TICK_METHOD_ENTERED_ERROR`
- `TICK_CALLED`
- `TICK_TRACE`

**Remove from `_allEvents` HashSet** (lines ~358-359, ~486):
- Same events as above

---

### Phase 4: Sync to RobotCore_For_NinjaTrader

#### Step 4.1: Copy Modified Files

After completing all changes in `modules/robot/`, copy to `RobotCore_For_NinjaTrader/`:
- `RobotEventTypes.cs`
- `RobotEngine.cs`
- `StreamStateMachine.cs`
- `RobotSimStrategy.cs`

**Note**: HeartbeatAddOn.cs and HeartbeatStrategy.cs should be deleted from both locations if they exist.

---

## Verification Steps

### 1. Compilation Check
- Build `RobotCore_For_NinjaTrader` project
- Verify no compilation errors
- Verify no references to removed events

### 2. Logging Check
- Run robot and verify:
  - ✅ `ENGINE_TICK_CALLSITE` still logging (critical)
  - ✅ `ENGINE_TICK_STALL_DETECTED` still logging (critical)
  - ✅ `ENGINE_TICK_STALL_RECOVERED` still logging (critical)
  - ✅ `HEARTBEAT` (stream-level) still logging (important)
  - ❌ Removed events NOT appearing in logs

### 3. Watchdog Check
- Verify watchdog still receives `ENGINE_TICK_CALLSITE` events
- Verify no watchdog errors related to missing events

---

## Risk Assessment

### Low Risk ✅
- All removed events are optional/deprecated
- Critical events (`ENGINE_TICK_CALLSITE`, stall detection) remain intact
- No functional dependencies on removed events

### Rollback Plan
- Git commit before cleanup
- Can revert if issues arise
- All removed code is in version control

---

## Execution Order

1. ✅ **Phase 1**: Remove optional diagnostic events (3 events)
2. ✅ **Phase 2**: Remove deprecated/unused events (5 events)
3. ✅ **Phase 3**: Update event type registry
4. ✅ **Phase 4**: Sync to RobotCore_For_NinjaTrader
5. ✅ **Verification**: Build, test, verify logs

---

## Notes

- **ENGINE_TICK_HEARTBEAT_AUDIT**: Not found in code, may already be removed
- **HeartbeatAddOn.cs / HeartbeatStrategy.cs**: May not exist if already removed
- **TICK_METHOD_ENTERED_ERROR**: Keep error handling if it's used for other purposes, but remove the event logging

---

## Estimated Time

- **Phase 1**: 15 minutes
- **Phase 2**: 20 minutes
- **Phase 3**: 5 minutes
- **Phase 4**: 5 minutes
- **Verification**: 10 minutes

**Total**: ~55 minutes

---

## Post-Cleanup Benefits

1. **Reduced Log Noise**: Fewer unnecessary diagnostic events
2. **Cleaner Codebase**: Removed deprecated/unused code
3. **Easier Maintenance**: Less code to maintain
4. **Better Performance**: Slightly reduced logging overhead
5. **Clearer Intent**: Only critical/important events remain
