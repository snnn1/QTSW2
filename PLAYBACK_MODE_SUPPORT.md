# Playback Mode Support

**Date:** 2026-01-16

## Overview

The robot now fully supports NinjaTrader's playback mode (historical data replay) when running in SIM execution mode. This allows you to test the strategy against historical data with simulated order execution.

## What Changed

### 1. Playback Detection in Strategy

**File:** `modules/robot/ninjatrader/RobotSimStrategy.cs`

- Added detection for playback mode using `State == State.Historical` or `Bars.IsInPlayback`
- Logs when playback mode is detected
- Sets environment to `SIM_PLAYBACK` when in playback mode (for logging clarity)
- Starts tick timer in historical state to ensure time-based state transitions work correctly

**Key Changes:**
```csharp
// Detect playback mode (NinjaTrader historical replay)
bool isPlaybackMode = State == State.Historical || (Bars != null && Bars.IsInPlayback);
if (isPlaybackMode)
{
    Log($"Playback mode detected - strategy will work with historical data replay", LogLevel.Information);
}

// Set environment appropriately
var environment = _simAccountVerified ? (isPlaybackMode ? "SIM_PLAYBACK" : "SIM") : "UNKNOWN";
```

### 2. Historical State Handling

**File:** `modules/robot/ninjatrader/RobotSimStrategy.cs`

- Added `State.Historical` handler to start tick timer
- Ensures time-based state transitions work correctly in playback mode
- Logs when historical mode is detected

**Key Changes:**
```csharp
else if (State == State.Historical)
{
    // Historical/playback mode: Start timer for time-based state transitions
    StartTickTimer();
    Log("Historical/playback mode: Timer started for time-based state transitions", LogLevel.Information);
}
```

### 3. Playback Support Indicator

**File:** `modules/robot/core/RobotEngine.cs`

- Added `supports_playback` field to startup banner
- Indicates that SIM and DRYRUN modes support playback

**Key Changes:**
```csharp
["supports_playback"] = _executionMode == ExecutionMode.SIM || _executionMode == ExecutionMode.DRYRUN,
```

## How It Works

### SIM Mode in Playback

1. **Detection**: Strategy detects playback mode when `State == State.Historical` or `Bars.IsInPlayback` is true
2. **Environment**: Environment is set to `SIM_PLAYBACK` for logging clarity
3. **Execution**: `NinjaTraderSimAdapter` works normally - it uses SIM account which is available in playback mode
4. **Orders**: Orders are placed in SIM account (simulated execution)
5. **Time Management**: Tick timer ensures time-based state transitions work correctly

### Key Points

- **SIM mode works in playback**: The `NinjaTraderSimAdapter` already supports playback because it uses SIM account
- **No code changes needed in adapter**: The adapter doesn't need to know about playback - it just uses SIM account
- **Time-based transitions**: Tick timer ensures state transitions work correctly even when bars arrive from historical data
- **Logging**: Environment is set to `SIM_PLAYBACK` to make it clear in logs that playback mode is active

## Usage

### In NinjaTrader

1. **Enable Playback Mode**: In NinjaTrader, enable playback mode (Market Replay or Historical Data)
2. **Select Strategy**: Use `RobotSimStrategy` (not `RobotSkeletonStrategy`)
3. **Select SIM Account**: Ensure a SIM account is selected
4. **Run**: Strategy will detect playback mode and work correctly

### Expected Behavior

- Strategy logs: `"Playback mode detected - strategy will work with historical data replay"`
- Environment in logs: `SIM_PLAYBACK`
- Orders: Placed in SIM account (simulated)
- State transitions: Work correctly based on bar timestamps
- Time management: Tick timer ensures time-based transitions work

## Verification

### Logs to Check

1. **Startup Banner**: Should show `"supports_playback": true` for SIM mode
2. **Environment**: Should show `SIM_PLAYBACK` when in playback mode
3. **Playback Detection**: Should log `"Playback mode detected"` message

### Testing

1. Run strategy in NinjaTrader playback mode
2. Verify orders are placed in SIM account
3. Verify state transitions work correctly
4. Verify logs show `SIM_PLAYBACK` environment

## Technical Details

### Why SIM Mode Works in Playback

- NinjaTrader's SIM account is available in both live and playback modes
- The `NinjaTraderSimAdapter` uses `Account.IsSimAccount` verification
- Orders are placed using NinjaTrader's standard order API, which works in playback
- No special handling needed - SIM account is the same in both modes

### Time Management

- In playback mode, bars arrive from historical data
- Tick timer ensures `Engine.Tick()` is called regularly
- This allows time-based state transitions to work correctly
- State machine uses bar timestamps for range windows, not wall clock time

## Limitations

- **LIVE mode**: Does not support playback (by design - LIVE is for real trading only)
- **DRYRUN mode**: Supports playback but doesn't place orders (logs only)
- **SIM mode**: Full support for playback with simulated order execution

## Related Files

- `modules/robot/ninjatrader/RobotSimStrategy.cs` - Strategy host with playback detection
- `modules/robot/core/RobotEngine.cs` - Engine with playback support indicator
- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs` - SIM adapter (no changes needed)
- `RobotCore_For_NinjaTrader/` - Synced versions of all files

---

*Playback mode support added: 2026-01-16*
