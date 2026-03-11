# HeartbeatAddOn Setup Guide

## Status: ❌ NOT RUNNING
No `ENGINE_HEARTBEAT` events found in logs. The AddOn needs to be installed and enabled.

## Installation Steps

### 1. Copy AddOn File
Copy `modules/robot/ninjatrader/HeartbeatAddOn.cs` to:
```
C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\AddOns\HeartbeatAddOn.cs
```

**Important**: AddOns go in the `AddOns` folder, NOT the `Strategies` folder!

### 2. Ensure Robot.Core.dll Reference
The AddOn needs `Robot.Core.dll` reference. Check if it's already added:
- In NinjaTrader: Tools → References
- Look for `Robot.Core` reference
- If missing, add it: Browse to `C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll`

### 3. Compile in NinjaTrader
1. Open NinjaTrader 8
2. Tools → Compile
3. Check for compilation errors
4. If errors, verify:
   - `Robot.Core.dll` is referenced
   - Namespace is correct: `NinjaTrader.NinjaScript`
   - All using statements are correct

### 4. Enable the AddOn
1. In NinjaTrader: Tools → AddOns
2. Find "HeartbeatAddOn" in the list
3. Check the box to enable it
4. Click OK

### 5. Restart NinjaTrader
The AddOn starts automatically when NinjaTrader starts (no chart needed).

## Verification

After enabling and restarting, run:
```bash
python check_heartbeat_addon.py
```

You should see:
- `[OK] Found X ENGINE_HEARTBEAT events`
- Recent heartbeats showing every ~5 seconds
- Instance ID, AddOn State, Connection State

## Expected Behavior

Once enabled, the AddOn will:
- Start automatically when NinjaTrader opens
- Emit `ENGINE_HEARTBEAT` events every 5 seconds
- Log to `logs/robot/robot_ENGINE.jsonl`
- Continue running even when:
  - No charts are open
  - Market is closed
  - No instruments are loaded

## Troubleshooting

### No Heartbeat Events After Setup

1. **Check AddOn is Enabled**:
   - Tools → AddOns → Verify "HeartbeatAddOn" is checked

2. **Check Compilation**:
   - Tools → Compile → Look for errors
   - Common issues:
     - Missing `Robot.Core.dll` reference
     - Namespace mismatch
     - Missing using statements

3. **Check Logger Initialization**:
   - Look for warnings in NinjaTrader output window
   - Check if `ProjectRootResolver.ResolveProjectRoot()` succeeds
   - Verify log directory is writable

4. **Check AddOn State**:
   - AddOn must reach `State.Active` to start timer
   - Check NinjaTrader output window for errors

5. **Manual Test**:
   - Add a `Print()` statement in `TimerCallback()` to verify timer is firing
   - Check NinjaTrader output window for prints

## File Locations

- **Source**: `modules/robot/ninjatrader/HeartbeatAddOn.cs`
- **Target**: `C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\AddOns\HeartbeatAddOn.cs`
- **DLL Reference**: `C:\Users\jakej\OneDrive\Documents\NinjaTrader 8\bin\Custom\Robot.Core.dll`
- **Log File**: `logs/robot/robot_ENGINE.jsonl`

## Next Steps

1. Copy `HeartbeatAddOn.cs` to AddOns folder
2. Compile in NinjaTrader
3. Enable in Tools → AddOns
4. Restart NinjaTrader
5. Run `python check_heartbeat_addon.py` to verify
