# DATA STALLED Explanation

## What is "DATA STALLED"?

The watchdog monitors data feed health by tracking when bars are received from NinjaTrader. If bars stop arriving for more than **120 seconds** while the market is open, it flags this as a **DATA STALL**.

## How Data Stall Detection Works

### Threshold
- **DATA_STALL_THRESHOLD_SECONDS = 120 seconds** (2 minutes)
- If no bars received for > 120 seconds AND market is open → **STALLED**
- If market is closed → **ACCEPTABLE_SILENCE** (not a problem)

### Detection Logic
1. **Per Execution Instrument**: Each contract (e.g., "MES 03-26", "MNQ 03-26") is tracked separately
2. **Bar Events**: Robot sends `ONBARUPDATE_CALLED` events (rate-limited to every 60 seconds)
3. **Stall Detection**: If last bar was > 120 seconds ago → flagged as stalled

## Why You Might See "DATA STALLED"

### Common Causes:

1. **Market is Closed**
   - If market closed recently, bars stop arriving
   - This is **normal** and should show "ACCEPTABLE_SILENCE" not "STALLED"
   - Check: Is it after 4:00 PM Chicago time?

2. **NinjaTrader Data Feed Disconnected**
   - Data provider connection lost
   - NinjaTrader not connected to broker/data feed
   - **Action**: Check NinjaTrader connection status

3. **Robot Not Running**
   - Robot strategy not active in NinjaTrader
   - **Action**: Check if strategy is running in NinjaTrader

4. **No Active Streams**
   - No streams expecting bars (all completed or not started)
   - **Action**: Check if streams are in bar-dependent states (ARMED, RANGE_BUILDING, etc.)

5. **Stale State**
   - Watchdog UI showing old state
   - **Action**: Refresh the watchdog page

## Current Status Check

Based on diagnostic:
- **Market Open**: `None` (unknown)
- **Worst Last Bar Age**: `None` (no bars tracked)
- **Data Stall Detected**: 0 instruments
- **Engine Activity**: Active (25,002 tick events in last 5 minutes)

**This suggests:**
- Robot is running and processing ticks
- But watchdog state may be stale or not initialized
- No actual stalls detected in backend state

## How to Fix

### If Market is Closed:
- **Normal behavior** - wait for market to open
- Should show "ACCEPTABLE_SILENCE" not "STALLED"

### If Market is Open:
1. **Check NinjaTrader Connection**:
   - Open NinjaTrader
   - Check connection status (green = connected)
   - Verify data feed is active

2. **Check Robot Strategy**:
   - Is strategy running?
   - Check for errors in NinjaTrader output window

3. **Check Robot Logs**:
   - Look for `ONBARUPDATE_CALLED` events
   - Look for connection errors
   - Check if bars are being received

4. **Restart Watchdog** (if needed):
   - Restart watchdog backend
   - Refresh watchdog UI

5. **Restart NinjaTrader** (if needed):
   - Sometimes data feed needs reconnection
   - Restart NinjaTrader to reconnect

## Diagnostic Commands

```bash
# Check data stall status
python check_data_stall.py

# Check recent bar events
python check_latest_logs.py

# Check watchdog state
python -c "import json; print(json.dumps(json.load(open('automation/logs/orchestrator_state.json')), indent=2))"
```

## Summary

"DATA STALLED" means bars haven't been received for > 2 minutes while market is open. Check:
1. Is market actually open?
2. Is NinjaTrader connected?
3. Is robot strategy running?
4. Are bars being received (check logs)?

If all checks pass but still showing stalled, it may be a stale UI state - refresh the watchdog page.
