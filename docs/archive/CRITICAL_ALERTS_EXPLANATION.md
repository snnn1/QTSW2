# Critical Alerts Explanation

## Status Badges (Not Alerts)

The badges you're seeing are **status indicators**, not alerts:
- ✅ **ENGINE ALIVE** (green) = Engine is running and healthy
- ✅ **MARKET OPEN** (green) = Market is currently open
- ❌ **BROKER DISCONNECTED** (red) = Connection issue
- ❌ **DATA STALLED** (red) = Data flow issue
- ❌ **IDENTITY VIOLATION** (red) = Identity check issue

## Current Issues

### 1. BROKER DISCONNECTED

**Status**: `connection_status: ConnectionLost`  
**Problem**: Connection events exist (`CONNECTION_RECOVERED` at 22:21:37) but state isn't updating.

**Root Cause**: 
- Events are in the feed but `last_connection_event_utc: None`
- State manager may not be processing connection events correctly
- Or events are being processed but timestamp isn't being stored

**Fix Needed**: 
- Check if `CONNECTION_RECOVERED` events are being processed
- Verify `update_connection_status()` is being called with correct timestamp
- May need to rebuild connection status from recent events

### 2. DATA STALLED

**Status**: `worst_last_bar_age_seconds: 737.8` (12+ minutes) but `data_stall_detected count: 0`  
**Problem**: Data stall detection only runs for instruments that are **expecting bars**, but `bars_expected_count: 0`.

**Root Cause**:
- No streams are in bar-dependent states (ARMED, RANGE_BUILDING, etc.)
- So `instruments_with_bars_expected` is empty
- Data stall detection loop doesn't run (line 761 in state_manager.py)
- But bars ARE old (737 seconds), indicating a real stall

**Fix Needed**:
- Data stall detection should check ALL instruments that have received bars, not just those expecting bars
- Or show "DATA STALLED" badge when `worst_last_bar_age_seconds > threshold` regardless of `bars_expected_count`

### 3. IDENTITY VIOLATION

**Status**: `last_identity_invariants_pass: False` with `violations: []`  
**Problem**: If there are no violations, the check should pass.

**Root Cause**:
- Robot engine is logging `IDENTITY_INVARIANTS_STATUS` with `pass: false` but empty violations list
- This is likely a bug in the robot engine logging logic

**Fix Needed**:
- Check robot engine code that logs `IDENTITY_INVARIANTS_STATUS`
- If violations list is empty, `pass` should be `true`

## Immediate Actions

1. **Restart watchdog backend** - May fix connection status if events aren't being processed
2. **Check robot logs** - Verify identity invariants are being logged correctly
3. **Fix data stall detection** - Should check all instruments, not just those expecting bars

## Code Changes Needed

### Fix 1: Data Stall Detection
Update `compute_engine_activity_state()` to check data stalls for ALL instruments that have received bars, not just those expecting bars.

### Fix 2: Connection Status
Ensure `CONNECTION_RECOVERED` events update `last_connection_event_utc` correctly.

### Fix 3: Identity Invariants
Fix robot engine to log `pass: true` when violations list is empty.
