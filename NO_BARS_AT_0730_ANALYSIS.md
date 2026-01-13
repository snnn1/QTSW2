# Why No Bars at 07:30 Slot Time?

## The Problem

Logs show `NO_DATA_NO_TRADE_INCIDENT` events when slot_time (07:30 Chicago) is reached with zero bars in the buffer.

## Root Cause Analysis

### 1. **Range Building Window**
- **S1 Session**: `range_start_time = "02:00"` (Chicago)
- **Slot Time**: `"07:30"` (Chicago)  
- **Window**: 02:00 to 07:30 CT (5.5 hours)

### 2. **How Bars Are Collected**

Bars are only received when:
- NinjaTrader calls `OnBarUpdate()` → `RobotEngine.OnBar()` → `StreamStateMachine.OnBar()`
- Bars are buffered **only if** they fall within the range window: `barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime`

### 3. **Why No Bars Are Received**

**The logs show NO `BAR_RECEIVED_DIAGNOSTIC` events**, which means `OnBar()` was **never called** during the range building window.

**Possible reasons:**

1. **Market/Data Feed Not Connected**
   - NinjaTrader isn't receiving data from the broker/data provider
   - Connection status is disconnected
   - Data feed subscription expired or not configured

2. **Market Hours**
   - While ES/NQ trade 23 hours/day, if NinjaTrader isn't configured for overnight data or the connection isn't active, no bars will arrive
   - The range window (02:00-07:30 CT) is during overnight/pre-market hours

3. **Strategy Not Receiving Bars**
   - Strategy may not be properly subscribed to the instrument
   - Bars may be filtered out before reaching `OnBarUpdate()`
   - Historical data may not be loaded/enabled

### 4. **What Happens When Slot Time Reaches with No Bars**

**Code Flow** (`StreamStateMachine.cs` line 416-491):

```csharp
if (utcNow >= SlotTimeUtc && !_rangeComputed)
{
    // Check bar buffer
    int finalBarCount = _barBuffer.Count;
    
    if (finalBarCount == 0)
    {
        // Attempt historical hydration (if _barProvider exists)
        if (_barProvider != null && !_hydrationAttempted)
        {
            TryHydrateFromHistory(utcNow);
            // Re-check count after hydration
        }
        
        // If still zero bars
        if (finalBarCount == 0)
        {
            // Log NO_DATA_NO_TRADE_INCIDENT
            // Persist incident record
            // Send emergency Pushover alert
            // Commit stream as NO_TRADE_RANGE_DATA_MISSING
        }
    }
}
```

**Key Issue**: Historical hydration only works if `_barProvider != null`, which is:
- ✅ **Available in DRYRUN mode** (uses `SnapshotParquetBarProvider`)
- ❌ **NOT available in SIM/LIVE mode** (`_barProvider` is null)

### 5. **Why Historical Hydration Doesn't Help**

In SIM/LIVE mode:
- `_barProvider` is `null` (only DRYRUN has historical bar provider)
- `TryHydrateFromHistory()` is never called
- Stream immediately commits as `NO_TRADE_RANGE_DATA_MISSING`

## The Solution

### Option 1: Ensure Data Feed is Connected (Recommended)
- Verify NinjaTrader is connected to data provider
- Check connection status in NinjaTrader
- Ensure instrument is subscribed and receiving data
- Verify overnight/24-hour data is enabled

### Option 2: Enable Historical Bar Provider for SIM Mode
- Modify `RobotEngine` to provide `SnapshotParquetBarProvider` in SIM mode
- This would allow historical hydration when real-time bars aren't available
- **Trade-off**: SIM mode would use historical data instead of live data

### Option 3: Start Strategy Earlier
- Start the strategy before `range_start_time` (02:00 CT)
- This ensures the strategy is running and can receive bars as they arrive
- Currently, if strategy starts after range_start but before slot_time, it may miss bars

## Log Evidence

From `robot_ES.jsonl`:
```json
{
  "event": "NO_DATA_NO_TRADE_INCIDENT",
  "slot_time_chicago": "07:30",
  "slot_time_utc": "2026-01-13T13:30:00.0000000+00:00",
  "state": "RANGE_BUILDING"
}
```

**Missing logs**:
- ❌ No `BAR_RECEIVED_DIAGNOSTIC` events
- ❌ No `RANGE_FIRST_BAR_ACCEPTED` events
- ❌ No `BAR_FILTERED_OUT` events

This confirms: **`OnBar()` was never called** → **NinjaTrader isn't receiving/reporting bars**.

## Recommendations

1. **Check NinjaTrader Connection Status**
   - Verify data feed is connected
   - Check instrument subscription
   - Verify overnight data is enabled

2. **Enable Diagnostic Logging**
   - Set `enable_diagnostic_logs: true` in `configs/robot/logging.json`
   - This will log `BAR_RECEIVED_DIAGNOSTIC` events to see if bars are being received but filtered out

3. **Monitor Bar Reception**
   - Add logging in `RobotEngine.OnBar()` to confirm bars are arriving
   - Check if bars are being filtered before reaching streams

4. **Consider Historical Hydration for SIM Mode**
   - If real-time data isn't reliable, enable historical bar provider for SIM mode
   - This would allow range computation from historical data when real-time bars aren't available
