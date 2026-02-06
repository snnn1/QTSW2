# Manual Flatten Re-Entry Fix

## Issue
When a position is manually cancelled/flattened, it immediately gets back in the opposite direction.

## Root Cause
1. User manually cancels/flattens a position
2. `Flatten()` is called, which closes the position
3. **BUT**: Entry stop orders are NOT cancelled
4. The opposite entry stop order is still active
5. If price is at/through the opposite breakout level, it fills immediately
6. **Result**: Re-entry in opposite direction

## Solution
When `Flatten()` is called (manual cancellation), cancel **BOTH** entry stop orders (long and short) for that stream to prevent re-entry.

### Implementation
Added logic in `FlattenIntentReal()` to:
1. Find the stream and trading date from the flattened intent
2. Search `_intentMap` for both entry intents (long and short) for that stream
3. Check if each entry has filled (only cancel unfilled entries)
4. Cancel all unfilled entry stop orders

### Code Changes
**Location**: `FlattenIntentReal()` in `NinjaTraderSimAdapter.NT.cs` (after position flatten succeeds)

**Logic**:
```csharp
// Find both entry intents (long and short) for this stream
var entryIntentIds = new List<string>();
foreach (var kvp in _intentMap)
{
    var otherIntent = kvp.Value;
    if (otherIntent.Stream == stream &&
        otherIntent.TradingDate == tradingDate &&
        otherIntent.TriggerReason != null &&
        (otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_LONG") ||
         otherIntent.TriggerReason.Contains("ENTRY_STOP_BRACKET_SHORT")))
    {
        // Only cancel unfilled entries
        var entryFilled = false;
        if (_executionJournal != null)
        {
            var journalEntry = _executionJournal.GetEntry(kvp.Key, tradingDate, stream);
            entryFilled = journalEntry != null && (journalEntry.EntryFilled || journalEntry.EntryFilledQuantityTotal > 0);
        }
        
        if (!entryFilled)
        {
            entryIntentIds.Add(kvp.Key);
        }
    }
}

// Cancel all unfilled entry stop orders
foreach (var entryIntentId in entryIntentIds)
{
    CancelIntentOrders(entryIntentId, utcNow);
}
```

## Safety Checks
1. **Only cancel unfilled entries**: Checks execution journal to ensure entry hasn't filled
2. **Only cancel entry orders**: `CancelIntentOrders()` filters out protective orders (STOP/TARGET suffixes)
3. **Error handling**: Wrapped in try-catch to prevent flatten failure if cancellation fails

## Expected Behavior After Fix
1. User manually cancels position → `Flatten()` called
2. Position closed ✅
3. **BOTH entry stop orders cancelled** ✅ (NEW)
4. No re-entry ✅

## Files Changed
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`
- `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs`

## Testing
After deploying DLL:
1. Manually cancel a position
2. Verify both entry stop orders are cancelled (check logs for `ENTRY_STOP_CANCELLED_ON_MANUAL_FLATTEN`)
3. Verify no re-entry occurs
