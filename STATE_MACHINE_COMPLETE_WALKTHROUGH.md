# Complete State Machine Walkthrough

## State Enum

```csharp
public enum StreamState
{
    PRE_HYDRATION,    // Loading historical data from CSV files
    ARMED,            // Waiting for range start time
    RANGE_BUILDING,   // Actively building range from live bars
    RANGE_LOCKED,     // Range is locked, ready for trading
    DONE              // Slot is complete
}
```

---

## Complete Flow for 09:00 Slot (S1) on 2026-01-14

### Timeline Overview

| Chicago Time | UTC Time | State | What's Happening |
|--------------|----------|-------|------------------|
| 00:00 (midnight) | 06:00 | PRE_HYDRATION | Robot starts, loads historical bars |
| 02:00 | 08:00 | ARMED → RANGE_BUILDING | Range building starts |
| 02:00-09:00 | 08:00-15:00 | RANGE_BUILDING | Collecting bars, updating range |
| 09:00 | 15:00 | RANGE_BUILDING → RANGE_LOCKED | Range locks, trading begins |
| 09:00-16:00 | 15:00-22:00 | RANGE_LOCKED | Monitoring for breakout signals |
| After 16:00 | After 22:00 | RANGE_LOCKED → DONE | Market close, slot complete |

---

## State 1: PRE_HYDRATION

### When It Starts
- **Initialization**: When `StreamStateMachine` is first created
- **After Trading Day Rollover**: When `UpdateTradingDate()` resets state for a new trading day

### What Happens
1. **Loads Historical Data**: Reads CSV files from `data/raw/{INSTRUMENT}_1m_{yyyy-MM-dd}.csv`
2. **Filters Bars**: Keeps only bars within the range window (02:00-09:00 Chicago for S1)
3. **Populates Buffer**: Stores bars in `_barBuffer` for later range computation
4. **Sets Flag**: `_preHydrationComplete = true` when done

### Transition Condition
```csharp
if (_preHydrationComplete)
{
    Transition(utcNow, StreamState.ARMED, "PRE_HYDRATION_COMPLETE");
}
```

### Transition To
- **ARMED** - When pre-hydration completes successfully

### Code Location
- `StreamStateMachine.cs` lines 361-372

---

## State 2: ARMED

### When It Starts
- **After PRE_HYDRATION**: When `_preHydrationComplete == true`
- **Expected Time**: Immediately after pre-hydration (could be hours before range start)

### What Happens
1. **Waits for Range Start**: Checks if `utcNow >= RangeStartUtc` every `Tick()` call
2. **Logs Diagnostics**: Every 5 minutes or when past range start time (if enabled)
3. **Validates Pre-Hydration**: Ensures `_preHydrationComplete == true` (logs error if false)

### Key Variables
- **RangeStartUtc**: Chicago 02:00 converted to UTC (08:00 UTC for S1)
- **SlotTimeUtc**: Chicago 09:00 converted to UTC (15:00 UTC for S1)
- **RangeStartChicagoTime**: 2026-01-14 02:00:00 Chicago
- **SlotTimeChicagoTime**: 2026-01-14 09:00:00 Chicago

### Transition Condition
```csharp
if (utcNow >= RangeStartUtc)
{
    Transition(utcNow, StreamState.RANGE_BUILDING, "RANGE_BUILD_START");
    // Then computes initial range from pre-hydrated bars
}
```

### Transition To
- **RANGE_BUILDING** - When current UTC time reaches `RangeStartUtc` (08:00 UTC / Chicago 02:00)

### What Should Happen at Transition
1. Logs `RANGE_WINDOW_STARTED` event
2. Computes initial range from bars in `_barBuffer` (range_start → now)
3. Sets `RangeHigh`, `RangeLow`, `FreezeClose` from historical data
4. Range will be updated incrementally as live bars arrive

### Code Location
- `StreamStateMachine.cs` lines 374-516

---

## State 3: RANGE_BUILDING

### When It Starts
- **At Range Start Time**: When `utcNow >= RangeStartUtc` (08:00 UTC / Chicago 02:00)
- **Duration**: From range start until slot time (7 hours for 09:00 slot)

### What Happens
1. **Receives Live Bars**: Each `OnBar()` call adds bars to `_barBuffer`
2. **Updates Range Incrementally**: Range high/low updated as new bars arrive
3. **Tracks Gaps**: Monitors for data gaps and enforces gap tolerance rules
4. **Logs Heartbeats**: Every 7 minutes (if enabled)
5. **Checks for Slot Time**: Continuously checks if `utcNow >= SlotTimeUtc`

### Gap Tolerance Rules
- **MAX_SINGLE_GAP_MINUTES**: 3.0 minutes
- **MAX_TOTAL_GAP_MINUTES**: 6.0 minutes  
- **MAX_GAP_LAST_10_MINUTES**: 2.0 minutes
- If violated → `_rangeInvalidated = true` (prevents trading)

### Transition Condition
```csharp
if (utcNow >= SlotTimeUtc)
{
    // Compute final range if not already computed incrementally
    // Then transition to RANGE_LOCKED
}
```

### Transition To
- **RANGE_LOCKED** - When `utcNow >= SlotTimeUtc` (15:00 UTC / Chicago 09:00)
- **DONE** (via Commit) - If range is invalidated due to gap violations

### What Should Happen at Transition
1. Computes final range from all bars (if not already computed incrementally)
2. Logs `RANGE_COMPUTE_COMPLETE` or `RANGE_LOCKED_INCREMENTAL`
3. Sets final `RangeHigh`, `RangeLow`, `FreezeClose`
4. Computes breakout levels: `brk_long = range_high + tick_size`, `brk_short = range_low - tick_size`
5. Checks for immediate entry (if freeze_close already broke out)
6. Logs intended brackets

### Code Location
- `StreamStateMachine.cs` lines 518-815

---

## State 4: RANGE_LOCKED

### When It Starts
- **At Slot Time**: When `utcNow >= SlotTimeUtc` (15:00 UTC / Chicago 09:00)

### What Happens
1. **Monitors Price**: Each `OnBar()` call checks for breakout signals
2. **Breakout Detection**:
   - **Long**: `high >= brk_long` → Enter long
   - **Short**: `low <= brk_short` → Enter short
3. **Price Tracking**: Updates MFE (Maximum Favorable Excursion) and manages stop-loss
4. **Break-Even Logic**: Moves stop to break-even when 65% of target is reached
5. **Market Close Check**: Stops accepting new entries after market close (16:00 Chicago / 22:00 UTC)

### Entry Conditions
- **Breakout**: Price breaks above `brk_long` or below `brk_short`
- **Immediate Entry**: If `freeze_close` already broke out at lock time
- **Market Close**: No entry after 16:00 Chicago

### Transition Condition
```csharp
if (utcNow >= MarketCloseUtc && !_entryDetected)
{
    Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
    // State becomes DONE
}
```

### Transition To
- **DONE** - When market closes without entry, or when trade is closed/completed

### Code Location
- `StreamStateMachine.cs` lines 817-900+

---

## State 5: DONE

### When It Starts
- **After Market Close**: If no entry occurred
- **After Trade Completion**: When trade is closed (target hit, stop hit, or break-even)

### What Happens
1. **Journal Committed**: `_journal.Committed = true`
2. **No Further Processing**: `Tick()` returns early if committed
3. **Final Summary**: Logs slot end summary with results

### No Transitions
- **Final State**: No transitions from DONE (hard no re-arming)

### Code Location
- `StreamStateMachine.cs` line 352-357

---

## Complete Example Timeline: 09:00 Slot (S1) on 2026-01-14

### 00:00 Chicago (06:00 UTC) - Robot Starts
```
State: PRE_HYDRATION
Action: Loading ES_1m_2026-01-14.csv
Bars Loaded: ~420 bars (02:00-09:00 window)
```

### 00:05 Chicago (06:05 UTC) - Pre-Hydration Completes
```
State: PRE_HYDRATION → ARMED
Action: Transition to ARMED
Flag: _preHydrationComplete = true
```

### 02:00 Chicago (08:00 UTC) - Range Building Starts
```
State: ARMED → RANGE_BUILDING
Condition: utcNow >= RangeStartUtc (08:00 UTC)
Action: 
  - Logs RANGE_WINDOW_STARTED
  - Computes initial range from pre-hydrated bars
  - Sets RangeHigh, RangeLow, FreezeClose
  - Begins accepting live bars
```

### 02:00-09:00 Chicago (08:00-15:00 UTC) - Range Building Active
```
State: RANGE_BUILDING
Action:
  - Receives live bars via OnBar()
  - Updates RangeHigh/RangeLow incrementally
  - Tracks gaps between bars
  - Logs heartbeats every 7 minutes
```

### 09:00 Chicago (15:00 UTC) - Range Locks
```
State: RANGE_BUILDING → RANGE_LOCKED
Condition: utcNow >= SlotTimeUtc (15:00 UTC)
Action:
  - Computes final range (if not already computed)
  - Logs RANGE_LOCKED or RANGE_LOCKED_INCREMENTAL
  - Computes breakout levels:
    * brk_long = RangeHigh + 0.25 (tick_size)
    * brk_short = RangeLow - 0.25
  - Checks for immediate entry
  - Logs intended brackets
```

### 09:00-16:00 Chicago (15:00-22:00 UTC) - Trading Window
```
State: RANGE_LOCKED
Action:
  - Monitors each bar for breakout
  - If high >= brk_long → Enter long
  - If low <= brk_short → Enter short
  - Tracks MFE and manages stop-loss
  - Moves stop to break-even at 65% of target
```

### 16:00 Chicago (22:00 UTC) - Market Close
```
State: RANGE_LOCKED → DONE
Condition: utcNow >= MarketCloseUtc (22:00 UTC) && !_entryDetected
Action:
  - Commits journal with "NO_TRADE_MARKET_CLOSE"
  - Logs slot end summary
  - State becomes DONE (no re-arming)
```

---

## Transition Summary Table

| From State | To State | Condition | Time Example |
|------------|----------|-----------|--------------|
| PRE_HYDRATION | ARMED | `_preHydrationComplete == true` | Immediately after CSV load |
| ARMED | RANGE_BUILDING | `utcNow >= RangeStartUtc` | 08:00 UTC (Chicago 02:00) |
| RANGE_BUILDING | RANGE_LOCKED | `utcNow >= SlotTimeUtc` | 15:00 UTC (Chicago 09:00) |
| RANGE_BUILDING | DONE | `_rangeInvalidated == true` | Anytime if gaps violate tolerance |
| RANGE_LOCKED | DONE | `utcNow >= MarketCloseUtc && !_entryDetected` | 22:00 UTC (Chicago 16:00) |
| RANGE_LOCKED | DONE | Trade completed | When target/stop hit |

---

## Key Methods Called

### Tick() - Called Every Second
- Checks current state
- Evaluates transition conditions
- Logs diagnostics
- Updates internal state

### OnBar() - Called When New Bar Arrives
- Adds bar to `_barBuffer`
- Updates range high/low (if in RANGE_BUILDING)
- Checks for breakout (if in RANGE_LOCKED)
- Tracks gaps

### UpdateTradingDate() - Called on Trading Day Rollover
- Resets state to PRE_HYDRATION
- Clears bar buffer
- Resets flags
- Recalculates RangeStartUtc and SlotTimeUtc

---

## Critical Timing Values (09:00 Slot, S1)

| Value | Chicago Time | UTC Time (CST) | Purpose |
|-------|--------------|----------------|---------|
| RangeStartUtc | 02:00 | 08:00 | When range building starts |
| SlotTimeUtc | 09:00 | 15:00 | When range locks |
| MarketCloseUtc | 16:00 | 22:00 | When trading window closes |

---

## The Bug That Was Fixed

**Problem**: After `UpdateTradingDate()`, state was preserved as `ARMED` but `_preHydrationComplete` was reset to `false`. This caused:
- Stream stuck in ARMED state
- Early `break` in ARMED handler (line 381)
- Never checking `utcNow >= RangeStartUtc`
- Never transitioning to RANGE_BUILDING

**Fix**: Reset state to `PRE_HYDRATION` on trading day rollover, allowing pre-hydration to re-run and properly set the flag before entering ARMED.
