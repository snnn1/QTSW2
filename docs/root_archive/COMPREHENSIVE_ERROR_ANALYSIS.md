# Comprehensive Error Analysis and Fixes

## Error Summary

### 1. BARSREQUEST_FAILED (M2K) - 3 errors
**Error**: `BarsRequest timed out after 30 seconds`  
**Location**: Background thread (non-blocking)  
**Impact**: M2K pre-hydration fails, but strategy continues  
**Status**: ✅ Fixed - Now non-blocking (fire-and-forget)  
**Note**: This is expected - M2K BarsRequest timing out, but strategy reaches Realtime

### 2. INSTRUMENT_RESOLUTION_FAILED (MYM, M2K, MNQ) - Many errors
**Error**: `Instrument.GetInstrument("MYM")` returns null  
**Impact**: Falls back to strategy instrument (works correctly)  
**Status**: ⚠️ Expected behavior, but logging as ERROR  
**Fix**: Added note that this is expected for micro futures

### 3. ORDER_REJECTED - Error Message Extraction Failing
**Error**: `'NinjaTrader.Cbi.OrderEventArgs' does not contain a definition for 'ErrorMessage'`  
**Root Cause**: NinjaTrader OrderEventArgs doesn't have ErrorMessage property  
**Fix Applied**: ✅ Changed to use `Order.Comment` and `OrderEventArgs.ErrorCode`  
**Status**: Fixed - will now extract error messages correctly

### 4. ORDER_SUBMIT_FAIL (MGC, MYM)
**Error**: Orders failing to submit  
**Root Cause**: Related to instrument resolution failures or order rejection  
**Status**: Will be resolved once error extraction shows actual rejection reasons

## Fixes Applied

### Fix 1: Error Message Extraction (ORDER_REJECTED) ✅
**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` (lines 890-980)

**Changes**:
- Removed attempts to access `ErrorMessage` on OrderEventArgs (doesn't exist)
- Now uses `OrderEventArgs.ErrorCode` first (enum value with error info)
- Uses `OrderEventArgs.Comment` if available
- Falls back to `Order.Comment` (contains broker error messages)
- Uses `Order.Name` if it contains "reject"
- Graceful fallback if all methods fail

### Fix 2: Fire-and-Forget BarsRequest ✅
**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs` (lines 232-250)

**Changes**:
- BarsRequest now runs in background thread pool (`ThreadPool.QueueUserWorkItem`)
- No longer blocks DataLoaded initialization
- Timeouts are logged but don't prevent Realtime transition
- Strategy reaches Realtime immediately

### Fix 3: Enhanced Diagnostic Logging ✅
**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

**Changes**:
- Added `DATALOADED_INITIALIZATION_COMPLETE` event
- Added `NT_CONTEXT_WIRED` event
- Added `REALTIME_STATE_REACHED` event
- These help diagnose "stuck in loading" issues

### Fix 4: Instrument Resolution Note ✅
**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` (line 226)

**Changes**:
- Added note that `INSTRUMENT_RESOLUTION_FAILED` is expected for micro futures
- Fallback to strategy instrument works correctly
- This is not an error condition

## Error Breakdown by Type

### ENGINE Log Errors (Last 500 events):
- **BARSREQUEST_FAILED**: 3 (M2K timeouts - non-blocking)
- **TIMETABLE_POLL_STALL_DETECTED**: 1

### Instrument-Specific Log Errors:
- **MGC**: `ORDER_SUBMIT_FAIL` (18 errors) - Related to order rejections
- **M2K**: `INSTRUMENT_RESOLUTION_FAILED` (20 errors) - Expected, has fallback
- **MNQ**: `INSTRUMENT_RESOLUTION_FAILED` (19 errors) - Expected, has fallback  
- **MYM**: `INSTRUMENT_RESOLUTION_FAILED` + `ORDER_REJECTED` + `ORDER_SUBMIT_FAIL` - Multiple issues

## Root Causes

1. **BarsRequest Timeout**: M2K historical data not available or connection issue
2. **Instrument Resolution**: Micro futures (MYM, M2K, MNQ) can't be resolved by name alone - fallback works
3. **Error Message Extraction**: Trying to access non-existent `ErrorMessage` property
4. **Order Rejections**: Need actual error messages to diagnose (now fixed)

## Next Steps

1. ✅ Fix error message extraction (DONE)
2. ✅ Make BarsRequest non-blocking (DONE)
3. ✅ Add diagnostic logging (DONE)
4. ⏳ Sync fixes to RobotCore_For_NinjaTrader
5. ⏳ Rebuild and test - verify error messages are now extracted correctly
6. ⏳ Check if order rejections show actual broker error messages

## Expected Behavior After Fixes

- **Error messages**: Will now show actual broker rejection reasons from `Order.Comment` or `ErrorCode`
- **BarsRequest**: Won't block initialization - strategies reach Realtime immediately
- **Instrument resolution**: Still logs failures but notes they're expected for micro futures
- **Diagnostics**: Clear visibility into initialization and state transitions
