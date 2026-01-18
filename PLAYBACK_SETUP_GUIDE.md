# NinjaTrader Playback Mode Setup Guide

**Date:** 2026-01-16

## Issue: Can't Enable Strategy on Playback Data

If you can't check the "Enable" checkbox for the strategy in playback mode, follow these steps:

## Step-by-Step Setup

### 1. Close All Connections
- Go to **Connections** menu in Control Center
- Close any active connections (Live, Sim, etc.)
- You can only have one connection active at a time

### 2. Download Market Replay Data (if needed)
- Go to **Tools → Historical Data → Market Replay**
- Download the data for the dates you want to test
- Or use **Tools → Historical Data → Tick** for historical tick data

### 3. Connect to Playback Connection
- Go to **Connections** menu
- Select **Playback Connection**
- Wait for connection to establish

### 4. Open Chart with Historical Data
- Open a chart for your instrument (e.g., ES, NQ)
- **IMPORTANT**: Make sure the chart has historical bars loaded
- The chart should show bars BEFORE your playback start time
- If chart is blank, load historical data first:
  - Right-click chart → **Data Series**
  - Set **Days to load** to include your playback date range
  - Click **OK**

### 5. Add Strategy with Playback101 Account
- Right-click chart → **Strategies** → **RobotSimStrategy**
- **CRITICAL**: Set **Account** to **Playback101** (not Sim101!)
- Set other parameters (Instrument, Quantity, etc.)
- Click **OK**

### 6. Enable Strategy
- In the Strategies window, you should now be able to check the **Enable** checkbox
- If checkbox is still disabled:
  - Remove the strategy
  - Make sure you're connected to Playback Connection
  - Make sure Account is set to Playback101
  - Re-add the strategy

## What Changed in the Code

### 1. Strategy Now Accepts Playback101 Account
**File:** `modules/robot/ninjatrader/RobotSimStrategy.cs`

- Strategy now accepts both Sim accounts AND Playback101
- Logs when Playback101 is detected
- Added `IsInstantiatedOnEachOptimizationIteration = true` to allow historical processing

### 2. Adapter Accepts Playback101 Account
**File:** `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`

- Adapter now accepts Playback101 account (not just Sim accounts)
- Logs `PLAYBACK_ACCOUNT_DETECTED` event when Playback101 is used
- Still enforces safety: only Sim accounts or Playback101 allowed

## Expected Behavior

### When Using Playback101:
- Log: `"Playback101 account verified: Playback101 - strategy will run in playback mode"`
- Log: `"PLAYBACK_ACCOUNT_DETECTED"` event
- Environment: `SIM_PLAYBACK` in logs
- Orders: Placed in Playback101 account (simulated execution)

### When Using Sim Account:
- Log: `"SIM account verified: Sim101"`
- Log: `"SIM_ACCOUNT_VERIFIED"` event
- Environment: `SIM` in logs
- Orders: Placed in Sim account

## Troubleshooting

### Checkbox Still Disabled?

1. **Verify Connection**: Make sure Playback Connection is active
   - Check Connections menu - should show "Playback" as connected

2. **Verify Account**: Strategy must use Playback101 account
   - In Strategy parameters, Account dropdown should show "Playback101"
   - If Playback101 doesn't appear, you're not connected to Playback Connection

3. **Chart Has Data**: Chart must have historical bars loaded
   - Chart should show bars before playback start time
   - If blank, load historical data first

4. **Remove and Re-add**: Sometimes strategy needs to be removed and re-added
   - Remove strategy from chart
   - Make sure Playback Connection is active
   - Re-add strategy with Playback101 account

### Strategy Won't Start?

- Check logs for `EXECUTION_BLOCKED` events
- Verify account name is exactly "Playback101" (case-sensitive)
- Make sure you're connected to Playback Connection, not Sim connection

## Key Points

- **Playback101 is required** for playback mode (not Sim101)
- **Playback Connection must be active** before adding strategy
- **Chart must have historical data** loaded before playback starts
- **Strategy must be added AFTER** connecting to Playback Connection

## Files Modified

- `modules/robot/ninjatrader/RobotSimStrategy.cs` - Accepts Playback101 account
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs` - Accepts Playback101 account
- All files synced to `RobotCore_For_NinjaTrader/`

---

*Playback101 support added: 2026-01-16*
