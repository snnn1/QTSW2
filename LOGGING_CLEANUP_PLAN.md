# Logging Cleanup Plan

## Problem Analysis

From log analysis, found **311,962 total log entries** with:
- **RANGE_COMPUTE_START**: 128,846 occurrences (41.3%) - logged every tick when range isn't computed
- **RANGE_COMPUTE_FAILED**: 128,827 occurrences (41.3%) - logged every tick on failure
- These two events account for **82.6% of all logs** and create excessive noise

## Strategy: Keep Critical Logs, Reduce Noise

### KEEP (Critical for errors/strategy):
- ✅ All ERROR/WARN level events
- ✅ State transitions (PRE_HYDRATION_COMPLETE, ARMED, RANGE_BUILDING → RANGE_LOCKED)
- ✅ Range computation SUCCESS (RANGE_COMPUTE_COMPLETE, RANGE_LOCKED)
- ✅ Range invalidation (RANGE_INVALIDATED)
- ✅ Pre-hydration errors (RANGE_HYDRATION_ERROR)
- ✅ Execution errors (order submission failures, rejections)
- ✅ Kill switch events
- ✅ Data feed stalls (DATA_FEED_STALL)
- ✅ Invariant violations
- ✅ Trading day rollover (keep but verify frequency)

### REDUCE/OPTIMIZE:
- 🔧 **RANGE_COMPUTE_START**: Log only ONCE per stream per slot (add flag)
- 🔧 **RANGE_COMPUTE_FAILED**: Rate-limit to once per minute max
- 🔧 **UPDATE_APPLIED**: Review frequency (file poller might be too frequent)
- 🔧 **ARMED_STATE_DIAGNOSTIC**: Already rate-limited, verify it's appropriate

## Implementation Steps

### 1. Fix RANGE_COMPUTE_START spam
**File**: `modules/robot/core/StreamStateMachine.cs`

**Changes**:
- Add field: `private bool _rangeComputeStartLogged = false;`
- Only log `RANGE_COMPUTE_START` if `!_rangeComputeStartLogged`
- Set flag to `true` after logging
- Reset flag in `UpdateTradingDate()` or when entering RANGE_BUILDING state

**Location**: Around line 632-647

### 2. Rate-limit RANGE_COMPUTE_FAILED
**File**: `modules/robot/core/StreamStateMachine.cs`

**Changes**:
- Add field: `private DateTimeOffset? _lastRangeComputeFailedLogUtc = null;`
- Only log if `!_lastRangeComputeFailedLogUtc.HasValue || (utcNow - _lastRangeComputeFailedLogUtc.Value).TotalMinutes >= 1`
- Update timestamp after logging
- Reset on successful computation

**Location**: Around line 651-663

### 3. Review UPDATE_APPLIED frequency
**File**: `modules/robot/core/StreamStateMachine.cs`

**Changes**:
- Check if file poller is triggering too often
- Consider rate-limiting or reducing verbosity
- May need to check FilePoller implementation

**Location**: Around line 214

### 4. Sync changes to NinjaTrader
- Run `sync_robotcore_to_ninjatrader.ps1` after changes

## Expected Impact

- **Reduce log volume by ~80%** (from 311k to ~60k entries)
- **Keep all error detection** - no loss of critical information
- **Easier debugging** - less noise, easier to find real issues
- **Better performance** - less I/O overhead

## Files to Modify

- `modules/robot/core/StreamStateMachine.cs` - Main logging cleanup
- `RobotCore_For_NinjaTrader/StreamStateMachine.cs` - Will be synced automatically

## Verification

After changes:
1. Run robot for a short period
2. Check log file size reduction
3. Verify critical errors still logged
4. Confirm RANGE_COMPUTE_START only appears once per stream per slot
5. Confirm RANGE_COMPUTE_FAILED is rate-limited
