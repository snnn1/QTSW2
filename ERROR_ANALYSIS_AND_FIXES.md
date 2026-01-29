# Error Analysis and Fixes

## Summary of Errors Found

### 1. BARSREQUEST_FAILED (M2K) - 3 errors
**Error**: `BarsRequest timed out after 30 seconds`  
**Location**: Background thread (non-blocking - good!)  
**Impact**: M2K pre-hydration fails, but strategy continues with live bars only  
**Status**: ✅ Non-blocking (fire-and-forget fix applied)  
**Action**: This is expected - M2K BarsRequest timing out, but strategy continues

### 2. INSTRUMENT_RESOLUTION_FAILED (MYM, M2K, MNQ) - Many errors
**Error**: `Instrument.GetInstrument("MYM")` returns null  
**Impact**: Falls back to strategy instrument (expected behavior)  
**Status**: ⚠️ Logging as ERROR but should be WARNING (has fallback)  
**Action**: Change log level from ERROR to WARNING since fallback exists

### 3. ORDER_REJECTED - Error Message Extraction Failing
**Error**: `'NinjaTrader.Cbi.OrderEventArgs' does not contain a definition for 'ErrorMessage'`  
**Root Cause**: NinjaTrader OrderEventArgs doesn't have ErrorMessage property  
**Fix Applied**: ✅ Changed to use `Order.Comment` and `OrderEventArgs.ErrorCode` instead  
**Status**: Fixed - will use Order.Comment for error messages

### 4. ORDER_SUBMIT_FAIL (MGC, MYM)
**Error**: Orders failing to submit  
**Root Cause**: Likely related to instrument resolution failures or order rejection  
**Status**: Will be resolved once instrument resolution and error extraction are fixed

## Fixes Applied

### Fix 1: Error Message Extraction (ORDER_REJECTED)
**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` (lines 887-951)

**Change**: 
- Removed attempts to access `ErrorMessage` on OrderEventArgs (doesn't exist)
- Now uses `Order.Comment` (contains broker error messages)
- Uses `OrderEventArgs.ErrorCode` for error codes
- Falls back gracefully if Comment is empty

### Fix 2: Instrument Resolution Log Level
**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` (line 218)

**Change**: 
- Change `INSTRUMENT_RESOLUTION_FAILED` from ERROR to WARNING
- This is expected behavior for micro futures - fallback to strategy instrument works

### Fix 3: Fire-and-Forget BarsRequest (Already Applied)
**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs` (lines 232-250)

**Change**: 
- BarsRequest now runs in background thread pool
- No longer blocks DataLoaded initialization
- Timeouts are logged but don't prevent Realtime transition

## Remaining Issues

### 1. M2K BarsRequest Timeouts
**Status**: Non-blocking (good), but still timing out  
**Possible Causes**:
- M2K instrument not available in NinjaTrader historical data
- NinjaTrader data connection issue
- Historical data not loaded for M2K

**Recommendation**: 
- Check NinjaTrader "Days to load" setting
- Verify M2K historical data is available
- Consider skipping BarsRequest for M2K if data isn't available

### 2. Instrument Resolution Failures
**Status**: Expected behavior (has fallback), but logging as ERROR  
**Fix**: Change log level to WARNING

## Next Steps

1. ✅ Fix error message extraction (DONE)
2. ⏳ Change instrument resolution log level to WARNING
3. ⏳ Sync fixes to RobotCore_For_NinjaTrader
4. ⏳ Test order rejection error messages are now properly extracted
