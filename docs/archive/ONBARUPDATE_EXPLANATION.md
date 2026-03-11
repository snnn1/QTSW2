# OnBarUpdate() - Complete Explanation

**Date**: 2026-01-30  
**Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs` (lines 981-1252)

---

## What is OnBarUpdate()?

`OnBarUpdate()` is a **NinjaTrader framework method** that NinjaTrader calls automatically whenever a bar closes. It's part of NinjaTrader's strategy lifecycle and is the primary mechanism for receiving completed bar data.

---

## When Does NinjaTrader Call OnBarUpdate()?

NinjaTrader calls `OnBarUpdate()`:
- **When a bar closes** (for 1-minute bars, this happens every minute)
- **Only after the bar is complete** (OHLC data is finalized)
- **Automatically** - you don't call it manually
- **Once per bar close** - not continuously

**Important**: `OnBarUpdate()` is **NOT** called continuously - it only fires when bars close. This is why we also use `OnMarketData()` to ensure continuous `Tick()` execution.

---

## What Does OnBarUpdate() Do in This Robot?

### 1. **Diagnostic Logging** (Lines 985-1045)
- Logs `ONBARUPDATE_CALLED` event to confirm NinjaTrader is calling the method
- Rate-limited to once per minute per instrument
- Provides "ground truth" - confirms bars are being received

### 2. **Safety Guards** (Lines 1013-1022)
- Checks if initialization failed (`_initFailed`) - exits early if so
- Checks if engine is ready (`_engineReady`) - exits early if not
- Checks if enough bars exist (`CurrentBar < 1`) - exits early if not

### 3. **Bar Time Interpretation** (Lines 1047-1171)
- **First Bar**: Detects and locks timezone interpretation
  - Determines if `Times[0][0]` is UTC or Chicago time
  - Locks interpretation to prevent mid-run flips
  - Logs `BAR_TIME_INTERPRETATION_LOCKED` event
  
- **Subsequent Bars**: Uses locked interpretation
  - Converts bar time to UTC using locked interpretation
  - Verifies bar age is reasonable (0-60 minutes)
  - Rate-limited warnings if interpretation mismatch occurs

### 4. **Bar Data Extraction** (Lines 1173-1176)
- Extracts OHLC data from NinjaTrader:
  - `Open[0]` - Opening price
  - `High[0]` - Highest price
  - `Low[0]` - Lowest price
  - `Close[0]` - Closing price

### 5. **Bar Period Validation** (Lines 1178-1209)
- **CRITICAL INVARIANT**: Enforces 1-minute bars
- Validates `BarsPeriod.BarsPeriodType == BarsPeriodType.Minute`
- Validates `BarsPeriod.Value == 1`
- Logs `BARS_PERIOD_INVALID` and exits if validation fails
- **Why**: Bar timestamp conversion assumes 1-minute bars

### 6. **Bar Timestamp Conversion** (Lines 1211-1213)
- Converts bar timestamp from **close time** to **open time**
- Formula: `barUtcOpenTime = barUtc.AddMinutes(-1)`
- **Why**: Analyzer parity - bars are normalized to open time
- **Assumption**: 1-minute bars (enforced above)

### 7. **Deliver Bar to Engine** (Line 1216)
- Calls `_engine.OnBar()` with:
  - Bar UTC open time
  - Canonical instrument name (e.g., "ES" not "MES")
  - OHLC data
  - Current UTC time
  
- **What `_engine.OnBar()` does**:
  - Routes bar to appropriate streams (canonicalization)
  - Validates bar against trading date
  - Processes bar through stream state machine
  - Updates range calculations
  - Triggers range lock when conditions met

### 8. **Drive Tick() Execution** (Line 1220)
- Calls `_engine.Tick(nowUtc)` after bar delivery
- **Why**: Ensures time-based logic runs when bars arrive
- **Note**: This is **bar-driven** Tick() execution
- **Important**: `OnMarketData()` also calls `Tick()` for **continuous** execution
- **Idempotent**: Safe to call multiple times - state transitions are gated by time thresholds and current state, not call count
  - Calling `Tick()` twice in the same millisecond is safe
  - `Tick()` frequency is irrelevant — only monotonic time matters

### 9. **Exception Handling** (Lines 1225-1251)
- Catches all exceptions to prevent NinjaTrader crashes
- Logs `ONBARUPDATE_EXCEPTION` event
- Does NOT rethrow - allows strategy to continue
- **Why**: Prevents chart crashes from bar processing errors

---

## Key Characteristics

### ✅ What OnBarUpdate() IS:
- **Bar-driven**: Only fires when bars close
- **Discrete**: Not continuous - fires once per bar
- **Complete**: Bar data is finalized when called
- **Reliable**: NinjaTrader guarantees it's called for each bar close

### ❌ What OnBarUpdate() IS NOT:
- **NOT continuous**: Doesn't fire between bar closes
- **NOT tick-driven**: Doesn't fire on every tick
- **NOT time-driven**: Doesn't fire on a timer
- **NOT guaranteed**: Can stop if NinjaTrader has issues

---

## Why We Also Use OnMarketData()

**Problem**: `OnBarUpdate()` only fires when bars close. If bars stop closing (e.g., data feed issues), `Tick()` stops running, and time-based logic (like range lock checks) stops executing.

**Solution**: `OnMarketData()` calls `Tick()` on every tick, ensuring continuous execution even when bars aren't closing.

**Result**: 
- `OnBarUpdate()` → Bar-driven `Tick()` execution (when bars arrive)
- `OnMarketData()` → Tick-driven `Tick()` execution (continuous)

**Both are needed** for robust operation.

### Bars vs Ticks: Fundamental Distinction

**Bars** → **Information** (price aggregation)
- Represent **what happened** (price action over time)
- Provide OHLC data for analysis
- Discrete events (one per bar close)

**Ticks** → **Clock pulses** (time advancement)
- Represent **when logic may advance** (time-based state transitions)
- Drive continuous execution of time-sensitive logic
- Continuous events (many per second)

**This system uses**:
- **Bars** for **what happened** (price data, range calculations)
- **Ticks** for **when logic may advance** (range lock checks, state transitions)

**This separation** (bars = information, ticks = clock pulses) is a hallmark of robust trading engines.

---

## Flow Diagram

```
NinjaTrader Bar Closes
        ↓
OnBarUpdate() Called
        ↓
Safety Guards (init, engine ready, bars exist)
        ↓
Bar Time Interpretation (detect/lock timezone)
        ↓
Extract OHLC Data (Open, High, Low, Close)
        ↓
Validate Bar Period (must be 1-minute)
        ↓
Convert Bar Time (close time → open time)
        ↓
_engine.OnBar() → Routes bar to streams
        ↓
_engine.Tick() → Time-based logic execution
        ↓
Done (wait for next bar close)
```

---

## Important Invariants

### 1. **Bar Period Must Be 1 Minute**
```csharp
if (BarsPeriod.BarsPeriodType != BarsPeriodType.Minute || BarsPeriod.Value != 1)
{
    // Error - cannot continue
    return;
}
```
**Why**: Bar timestamp conversion assumes 1-minute bars (`AddMinutes(-1)`).

### 2. **Bar Time Interpretation is Locked**
```csharp
if (!_barTimeInterpretationLocked)
{
    // First bar: Detect and lock
    _barTimeInterpretationLocked = true;
}
else
{
    // Subsequent bars: Use locked interpretation
}
```
**Why**: Prevents mid-run timezone flips that would corrupt bar timestamps.

### 3. **Tick() Must Run Even Without Bars**
```csharp
// OnBarUpdate() calls Tick() when bars arrive
_engine.Tick(nowUtc);

// OnMarketData() also calls Tick() for continuous execution
// This ensures Tick() runs even when bars aren't closing
```
**Why**: Time-based logic (range lock checks) must run continuously, not just when bars arrive.

---

## Diagnostic Events Logged

1. **`ONBARUPDATE_CALLED`** - Confirms NinjaTrader is calling OnBarUpdate()
2. **`ONBARUPDATE_DIAGNOSTIC`** - Ground truth diagnostic (rate-limited)
3. **`BAR_TIME_DETECTION_STARTING`** - First bar timezone detection
4. **`BAR_TIME_INTERPRETATION_LOCKED`** - Timezone interpretation locked
5. **`BAR_TIME_INTERPRETATION_MISMATCH`** - Warning if interpretation would flip (rate-limited)
6. **`BARS_PERIOD_INVALID`** - Error if bar period is not 1-minute
7. **`ONBARUPDATE_EXCEPTION`** - Exception caught to prevent crash

---

## Relationship to Other Methods

### OnMarketData()
- **OnBarUpdate()**: Bar-driven `Tick()` execution (when bars close)
- **OnMarketData()**: Tick-driven `Tick()` execution (continuous)
- **Both needed**: Ensures `Tick()` runs even if bars stop closing

### Tick()
- **Called from**: `OnBarUpdate()` (bar-driven) and `OnMarketData()` (tick-driven)
- **Purpose**: Time-based logic execution (range lock checks, state transitions)
- **Idempotent**: Safe to call frequently
  - **Why idempotent**: All state transitions are gated by time thresholds and current state, not call count
  - Calling `Tick()` twice in the same millisecond is safe
  - `Tick()` frequency is irrelevant — only monotonic time matters

### OnBar()
- **Called from**: `OnBarUpdate()` only
- **Purpose**: Deliver bar data to engine for stream processing
- **Frequency**: Once per bar close

---

## Common Issues and Solutions

### Issue 1: OnBarUpdate() Not Being Called
**Symptoms**: No `ONBARUPDATE_CALLED` events in logs
**Causes**:
- Strategy not enabled on chart
- Data feed disconnected
- Instrument not configured correctly
**Solution**: Check NinjaTrader strategy status, data feed connection

### Issue 2: Bars Stop Closing
**Symptoms**: `OnBarUpdate()` stops firing
**Causes**:
- Data feed interruption
- Market closed
- Instrument delisted
**Solution**: `OnMarketData()` ensures `Tick()` continues even if bars stop

### Issue 3: Bar Time Interpretation Mismatch
**Symptoms**: `BAR_TIME_INTERPRETATION_MISMATCH` warnings
**Causes**:
- Historical bars arriving out of order after disconnect
- Timezone detection error on first bar
**Solution**: Rate-limited warnings prevent log flooding; interpretation is locked after first bar

### Issue 4: Invalid Bar Period
**Symptoms**: `BARS_PERIOD_INVALID` error
**Causes**:
- Strategy configured with non-1-minute bars
- BarsPeriod changed mid-run
**Solution**: Strategy requires 1-minute bars; cannot continue with other periods

---

## Summary

**OnBarUpdate()** is NinjaTrader's method for receiving completed bar data. In this robot, it:

1. ✅ Receives bar data when bars close
2. ✅ Validates bar period (must be 1-minute)
3. ✅ Converts bar timestamps (close → open time)
4. ✅ Delivers bars to engine for stream processing
5. ✅ Drives `Tick()` execution (bar-driven)
6. ✅ Handles exceptions gracefully

**Key Point**: `OnBarUpdate()` is **bar-driven** (fires when bars close), so we also use `OnMarketData()` for **continuous** `Tick()` execution to ensure time-based logic runs even when bars aren't closing.

### Design Principles

**Bars vs Ticks Separation**:
- **Bars** = **Information** (what happened - price aggregation)
- **Ticks** = **Clock pulses** (when logic may advance - time advancement)

**Tick() Idempotency**:
- `Tick()` is idempotent because all state transitions are gated by time thresholds and current state, not call count
- Calling `Tick()` twice in the same millisecond is safe
- `Tick()` frequency is irrelevant — only monotonic time matters

**This separation** (bars = information, ticks = clock pulses) is a hallmark of robust trading engines.

---

**Status**: ✅ **COMPLETE EXPLANATION**
