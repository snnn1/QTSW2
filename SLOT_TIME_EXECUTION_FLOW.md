# What Happens When First Timetable Slot Time is Reached

## Overview

When `slot_time` (e.g., "09:00" Chicago time) is reached, the stream transitions from **RANGE_BUILDING** → **RANGE_LOCKED** and begins breakout detection.

## Step-by-Step Execution Flow

### 1. **Slot Time Check** (Every Tick)
```
State: RANGE_BUILDING
Condition: utcNow >= SlotTimeUtc && !_rangeComputed
```

The `Tick()` method checks every second if:
- Current time has reached or passed `SlotTimeUtc`
- Range has not been computed yet

### 2. **Bar Data Validation**

**If NO bars available:**
- Attempts historical hydration (if `_barProvider` exists)
- If still no bars after hydration:
  - Logs `NO_DATA_NO_TRADE_INCIDENT`
  - Persists incident record to `data/execution_incidents/`
  - Sends **high-priority Pushover alert** (emergency priority)
  - Commits stream as `NO_TRADE_RANGE_DATA_MISSING`
  - Stream goes to `DONE` state (no trading today)

**If bars ARE available:**
- Proceeds to range computation

### 3. **Range Computation**

Logs `RANGE_COMPUTE_START` with:
- Range window (Chicago time): `range_start` to `slot_time`
- Bar buffer count
- Time conversion details

**Computes retrospectively:**
- `RangeHigh` = highest high in window
- `RangeLow` = lowest low in window  
- `FreezeClose` = close price at slot_time (or last bar before slot_time)
- `FreezeCloseSource` = where freeze close came from

**Logs `RANGE_COMPUTE_COMPLETE`** with:
- Range high/low values
- Range size
- Bar count used
- Computation duration (ms)
- First/last bar timestamps (UTC and Chicago)

### 4. **Transition to RANGE_LOCKED**

**State Transition:**
```
RANGE_BUILDING → RANGE_LOCKED
```

**Logs `RANGE_LOCK_ASSERT`** with:
- Invariant checks (first bar >= range_start, last bar < slot_time)
- Bars used count
- Range boundaries

### 5. **Breakout Level Computation**

**Computes breakout levels:**
- `_brkLongRounded` = RangeHigh rounded to tick size
- `_brkShortRounded` = RangeLow rounded to tick size

**Logs `BREAKOUT_LEVELS_COMPUTED`** with:
- Raw and rounded breakout levels
- Tick size used
- Range values

### 6. **Late Start Check**

**If stream started AFTER slot_time:**
- Range is computed (for parity/audit)
- **Trading is SKIPPED** (safety policy)
- Logs `STREAM_LATE_START_SKIP_TRADING`
- Commits as `NO_TRADE_LATE_START_RANGE_COMPUTED`
- Stream goes to `DONE` state

**If stream started BEFORE slot_time:**
- Proceeds to entry detection

### 7. **Immediate Entry Check** (At Lock)

**Checks if price is already outside range:**
- If `FreezeClose >= RangeHigh`: Long breakout already occurred
- If `FreezeClose <= RangeLow`: Short breakout already occurred

**If immediate entry detected:**
- Logs `IMMEDIATE_ENTRY_AT_LOCK`
- Calls `CheckImmediateEntryAtLock()` which:
  - Creates intent
  - Submits entry order
  - Sets up protective stop/target

### 8. **Breakout Monitoring** (On Each Bar)

**After transition to RANGE_LOCKED:**

For each incoming bar (`OnBar()`):
- Checks if `barUtc >= SlotTimeUtc` (after slot time)
- Checks if `barUtc < MarketCloseUtc` (before market close)
- Checks if breakout levels are computed

**If conditions met:**
- Calls `CheckBreakoutEntry(barUtc, high, low, utcNow)`

**Breakout Detection:**
- **Long breakout**: `high >= _brkLongRounded`
- **Short breakout**: `low <= _brkShortRounded`

**If breakout detected:**
- Logs `BREAKOUT_DETECTED`
- Creates execution intent
- Submits entry order via `_executionAdapter`
- On fill: Submits protective stop and target orders
- If protective orders fail: Flattens position, stands down stream, alerts operator

### 9. **Execution Gates** (Risk Checks)

Before submitting orders, checks:
- ✅ Realtime data (not historical)
- ✅ Valid trading day
- ✅ Session active
- ✅ Slot time reached
- ✅ Timetable enabled
- ✅ Stream armed
- ✅ State is RANGE_LOCKED
- ✅ Entry not already detected
- ✅ Execution mode allows trading (SIM/LIVE, not DRYRUN)

**If any gate fails:**
- Logs `EXECUTION_GATE_INVARIANT_VIOLATION`
- Order submission blocked

### 10. **Order Submission** (SIM/LIVE Mode)

**Entry Order:**
- Direction: LONG or SHORT (based on breakout)
- Entry price: Market order or limit at breakout level
- Quantity: From parity spec

**On Entry Fill:**
- Logs `ENTRY_FILLED`
- **Protective Orders** (with retry):
  - **Stop Loss**: `_brkLongRounded` (for shorts) or `_brkShortRounded` (for longs)
  - **Target**: Base target distance from entry
- If protective orders fail after retries:
  - Flattens position immediately
  - Stands down stream
  - Persists incident record
  - Sends emergency alert

### 11. **Market Close**

**When `barUtc >= MarketCloseUtc`:**
- If no entry detected: Commits as `NO_TRADE_MARKET_CLOSE`
- If entry detected: Position remains open (managed by protective orders)
- Stream goes to `DONE` state

## Summary Timeline

```
T-0:00  Range Start Time
  └─> Stream transitions ARMED → RANGE_BUILDING
  └─> Bars buffered for range window

T+slot_time  Slot Time Reached
  ├─> Check bar availability
  ├─> Compute range (high/low/freeze_close)
  ├─> Compute breakout levels
  ├─> Transition RANGE_BUILDING → RANGE_LOCKED
  ├─> Check immediate entry
  └─> Begin breakout monitoring

T+slot_time+  After Slot Time
  └─> Monitor each bar for breakout
  └─> On breakout: Submit entry order
  └─> On fill: Submit protective orders
  └─> Continue until market close

T+market_close  Market Close
  └─> Commit stream (NO_TRADE or DONE)
  └─> Stream goes to DONE state
```

## Key Safety Features

1. **No Data Protection**: If no bars at slot_time → NO_TRADE + alert
2. **Late Start Protection**: If started after slot_time → Compute range but skip trading
3. **Protective Order Guarantee**: Entry fill requires stop/target placement (with retry)
4. **Fail-Closed**: If protective orders fail → Flatten position + stand down stream
5. **Execution Gates**: Multiple safety checks before order submission

## Log Events You'll See

**At Slot Time:**
- `RANGE_COMPUTE_START`
- `RANGE_COMPUTE_COMPLETE`
- `RANGE_LOCK_ASSERT`
- `BREAKOUT_LEVELS_COMPUTED`
- `RANGE_LOCKED` (state transition)

**After Slot Time:**
- `BREAKOUT_DETECTED` (when breakout occurs)
- `ENTRY_ORDER_SUBMITTED`
- `ENTRY_FILLED`
- `PROTECTIVE_STOP_SUBMITTED`
- `PROTECTIVE_TARGET_SUBMITTED`

**If Issues:**
- `NO_DATA_NO_TRADE_INCIDENT` (no bars)
- `STREAM_LATE_START_SKIP_TRADING` (started late)
- `PROTECTIVE_ORDERS_FAILED_FLATTENED` (protective order failure)
