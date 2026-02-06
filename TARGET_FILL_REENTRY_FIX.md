# Target Fill Re-Entry Fix

## Issue
When a protective TARGET (limit) order fills in a real stream, the opposite entry stop order was NOT cancelled, allowing immediate re-entry.

## Root Cause
The code only cancelled opposite entry stop orders when a protective **STOP** filled, but NOT when a protective **TARGET** filled:

```csharp
if (orderTypeForContext == "STOP" && _intentMap.TryGetValue(intentId, out var filledIntent))
```

When a target fills:
1. Position closes (profit taken)
2. Opposite entry stop order remains active ❌
3. If price is at/through opposite breakout level, opposite entry stop fills immediately
4. **Result**: Re-entry in opposite direction

## Solution
Cancel opposite entry stop orders when **BOTH** stop and target orders fill.

### Code Changes

**Before**:
```csharp
if (orderTypeForContext == "STOP" && _intentMap.TryGetValue(intentId, out var filledIntent))
```

**After**:
```csharp
if ((orderTypeForContext == "STOP" || orderTypeForContext == "TARGET") && _intentMap.TryGetValue(intentId, out var filledIntent))
```

**Updated Logging**:
- Changed event name from `OPPOSITE_ENTRY_CANCELLED_ON_STOP_FILL` to `OPPOSITE_ENTRY_CANCELLED_ON_EXIT_FILL`
- Added `exit_order_type` field to indicate whether it was STOP or TARGET
- Updated note to say "protective {stop|target} filled"

## Expected Behavior After Fix

**When Protective STOP Fills**:
1. Stop loss fills → Position closes
2. Opposite entry stop order cancelled ✅
3. No re-entry ✅

**When Protective TARGET Fills**:
1. Target (limit) fills → Position closes (profit taken)
2. Opposite entry stop order cancelled ✅ (NEW)
3. No re-entry ✅

## Files Changed
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`

## Testing
After deploying DLL and restarting NinjaTrader:
1. Enter position
2. Wait for protective target to fill
3. Check logs for `OPPOSITE_ENTRY_CANCELLED_ON_EXIT_FILL` with `exit_order_type = "TARGET"`
4. Verify no re-entry occurs

## Related Fixes
- **Manual Flatten Re-Entry Fix (V2)**: Detects position flat state and cancels entry stops
- **Protective Stop Fill Re-Entry Fix**: Cancels opposite entry when stop fills
- **This Fix**: Cancels opposite entry when target fills (completes the protection)
