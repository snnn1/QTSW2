# Order Preservation on Strategy Restart

When the NinjaTrader strategy restarts (e.g. connection loss, NinjaTrader restart), orders can be cancelled. This document describes how to keep orders alive and what the robot does to recover.

## Root Cause

1. **NinjaTrader platform behavior**: When a strategy is disabled or restarts, NinjaTrader may cancel all strategy-generated orders based on the "Cancel entry/exit orders when strategy is disabled" settings.
2. **Connection loss**: Brief connection loss can trigger NinjaTrader to restart the strategy (depending on `ConnectionLossHandling`).

## Mitigations Implemented

### 1. ConnectionLossHandling = KeepRunning (Code)

The strategy sets `ConnectionLossHandling = ConnectionLossHandling.KeepRunning` in `State.SetDefaults`. This tells NinjaTrader to **keep the strategy running** during brief connection loss instead of restarting it. When the strategy doesn't restart, orders are not cancelled.

**Files**: `NT_STRATEGIES/RobotSimStrategy.cs`, `RobotCore_For_NinjaTrader/Strategies/RobotSimStrategy.cs`

### 2. Stop Bracket Resubmit on RANGE_LOCKED Restore (Code)

When the strategy restarts and restores a stream from `RANGE_LOCKED` (from hydration log), we now force `_stopBracketsSubmittedAtLock = false` so the robot **resubmits** the stop-entry brackets at breakout levels. Broker orders are lost on process restart; this ensures they are replaced.

**Files**: `modules/robot/core/StreamStateMachine.cs`, `RobotCore_For_NinjaTrader/StreamStateMachine.cs`

### 3. NinjaTrader UI Settings (Manual)

The "Cancel entry orders when strategy is disabled" and "Cancel exit orders when strategy is disabled" options **cannot be set in code**—they are configured in the NinjaTrader UI when enabling the strategy.

**To preserve orders on manual disable/restart:**

1. In NinjaTrader, open **Control Center** → **Strategies** tab
2. Right-click the strategy → **Properties** (or enable the strategy and configure before starting)
3. Under **On connection loss** or strategy properties, ensure:
   - **Cancel entry orders when a strategy is disabled** = **unchecked** (if you want entries to survive)
   - **Cancel exit orders when a strategy is disabled** = **unchecked** (if you want protectives to survive)

**Note**: These may be global options under **Tools** → **Options** → **Strategies** → **NinjaScript** depending on NinjaTrader version. Check both strategy-level and global settings.

## What Does NOT Cause Restarts

- **Timetable file changes**: The robot polls `timetable_current.json` every 5 seconds. When the hash changes (e.g. Matrix app writes a new `as_of` timestamp), the robot runs `ApplyTimetable` but does **not** restart streams or cancel orders. Timetable updates only apply slot-time changes to existing streams.
- **Matrix Timetable App**: The Matrix app is a separate web application. It writes the timetable file but has no direct control over NinjaTrader or orders.

## What Does Cause Restarts

- **NinjaTrader connection loss** (data feed, order feed) — with default `ConnectionLossHandling`, NinjaTrader may restart the strategy
- **NinjaTrader process restart** — user or system restarts NinjaTrader
- **Strategy disable/re-enable** — user manually stops and restarts the strategy

## Summary

| Mitigation | Effect |
|------------|--------|
| `ConnectionLossHandling.KeepRunning` | Reduces restarts from brief connection loss |
| Stop bracket resubmit on RANGE_LOCKED restore | Replaces lost entry orders after restart |
| NinjaTrader cancel-on-disable = unchecked | Keeps orders on broker when strategy is disabled (UI setting) |
