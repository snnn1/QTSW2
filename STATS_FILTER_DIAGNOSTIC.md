# Statistics Filter Toggle Diagnostic Guide

## Problem
Statistics panel shows only unfiltered stats regardless of toggle state.

## Expected Behavior

**Toggle OFF (Gray)**: Shows stats for **allowed trades only** (excludes filtered executed trades)
- `include_filtered_executed = False`
- Uses: `executed_selected = executed_all[executed_all["final_allowed"] == True]`
- Should show **fewer** trades and **lower** profit (if filtered trades were profitable)

**Toggle ON (Green)**: Shows stats for **all executed trades** (includes filtered executed trades)
- `include_filtered_executed = True`
- Uses: `executed_selected = executed_all` (all executed trades)
- Should show **more** trades and potentially **higher** profit

## Diagnostic Steps

### 1. Check Browser Console Logs

When you toggle, you should see:
```
[Toggle] Button clicked: true -> false
[Master Stats] Toggle changed: true -> false, clearing old stats and refetching...
[Master Stats] Cleared backendStatsFull, will refetch with includeFilteredExecuted=false
[API] getMatrixData called with includeFilteredExecuted=false (type: boolean)
[API] Query params: include_filtered_executed=false
[Master Stats] Refetching with includeFilteredExecuted=false
[Master Stats] Successfully refetched stats with includeFilteredExecuted=false
[Master Stats] Sample counts: {total: X, allowed: Y, filtered: Z}
```

### 2. Check Backend Logs

Look for these log lines:
```
GET /api/matrix/data called with contract_multiplier=1.0, include_filtered_executed=False (parsed as bool), stream_include=None
[API] Calculating stats with include_filtered_executed=False (type: bool)
[Statistics] include_filtered_executed=False (type: bool)
[Statistics] executed_all count: X, allowed: Y, filtered: Z
[Statistics] Using ONLY allowed executed trades (include_filtered_executed=False): Y trades (filtered out Z trades)
[API] Stats sample counts: total=X, allowed=Y, filtered=Z
```

### 3. Verify Sample Counts

Check the `sample_counts` in the stats response:
- `executed_trades_total`: All executed trades (should be constant)
- `executed_trades_allowed`: Allowed trades only (should change with toggle)
- `executed_trades_filtered`: Filtered trades (should change with toggle)

**When toggle is OFF**: `executed_trades_allowed` should equal the number of trades used in stats
**When toggle is ON**: `executed_trades_total` should equal the number of trades used in stats

### 4. Quick Test

1. Note current Total Trades and Total Profit
2. Toggle OFF (gray) - should see **fewer** trades and potentially **lower** profit
3. Toggle ON (green) - should see **more** trades and potentially **higher** profit

If numbers don't change, the toggle isn't working.

## Fixes Applied

1. **Clear backendStatsFull on toggle change** - Prevents stale stats from showing
2. **Explicit boolean parsing in API** - Ensures FastAPI correctly parses query param
3. **Enhanced logging** - Added diagnostic logs at every step

## Common Issues

### Issue 1: FastAPI Boolean Parsing
**Symptom**: Backend receives string "false" instead of boolean False
**Fix**: Added explicit boolean parsing in API endpoint

### Issue 2: Stale Stats Cache
**Symptom**: Old stats remain visible after toggle
**Fix**: Clear `backendStatsFull` before refetching

### Issue 3: Refetch Not Triggered
**Symptom**: No console logs when toggling
**Check**: Verify `masterData.length > 0` and `refetchMasterStats` exists

## Next Steps

1. **Check browser console** - Look for the log messages above
2. **Check backend logs** - Verify parameter is received correctly
3. **Compare sample counts** - Verify they change with toggle
4. **Report findings** - Share console/backend logs if issue persists
