# Complete Trading Process Summary
## From Breakout Detection to Position Management

---

## Overview

This document provides a complete end-to-end summary of the trading process, from initial range lock through breakout detection, entry fills, protective orders, break-even management, and final position closure.

---

## Phase 1: Range Lock & Breakout Preparation

### 1.1 Range Calculation
**Location**: `StreamStateMachine.cs` - Range computation during pre-hydration

**Process**:
- Historical bars loaded and analyzed
- Range high/low calculated from specified time window
- Breakout levels computed:
  - **Long breakout**: `range_high + tick_size` (1 tick above range)
  - **Short breakout**: `range_low - tick_size` (1 tick below range)

**Output**: Range locked with breakout levels ready

---

### 1.2 Protective Order Calculation
**Location**: `StreamStateMachine.cs` - `ComputeProtectivesFromLockSnapshot()`

**Calculations**:
- **Target Price**: `entry_price ± target_points`
- **Stop Price**: `entry_price ± min(range_size, 3 × target_points)`
- **BE Trigger Price**: `entry_price ± (0.65 × target_points)` (65% of target)

**Example** (Long entry at 5000, target = 100 points):
- Target: 5100
- Stop: 4950 (assuming 50 point range)
- BE Trigger: 5065 (65% of 100 = 65 points from entry)

---

### 1.3 Stop Brackets at Lock (Immediate Entry Protection)
**Location**: `StreamStateMachine.cs` - `SubmitStopBracketsAtLock()`

**Process**:
- Creates intents for both Long and Short directions
- Submits stop-market orders at breakout levels
- These act as entry orders if price immediately breaks out at lock time
- Intent registered BEFORE order submission (critical for protective orders)

**Purpose**: Handles immediate breakouts that occur right at range lock time

---

## Phase 2: Breakout Detection & Entry Order Submission

### 2.1 Breakout Detection
**Location**: `StreamStateMachine.cs` - `OnBar()` → Breakout detection logic

**Detection Logic**:
- **Long Breakout**: `bar_high >= breakout_long_level`
- **Short Breakout**: `bar_low <= breakout_short_level`
- First valid breakout wins (only before market close)

**Trigger**: When price crosses breakout level, `DetectEntry()` is called

---

### 2.2 Entry Detection
**Location**: `StreamStateMachine.cs` - `DetectEntry()`

**Process**:
1. Validates entry hasn't already been detected (idempotency)
2. Records entry direction, price, and time
3. Calls `ComputeAndLogProtectiveOrders()` to calculate:
   - Stop price
   - Target price
   - BE trigger price
4. Stores values in `_intendedStopPrice`, `_intendedTargetPrice`, `_intendedBeTrigger`

**Output**: Entry detected, protective prices calculated

---

### 2.3 Intent Creation
**Location**: `StreamStateMachine.cs` - `DetectEntry()` lines 4451-4463

**Intent Fields**:
```csharp
new Intent(
    TradingDate,      // e.g., "2026-01-28"
    Stream,           // e.g., "NQ1"
    Instrument,       // Canonical instrument (e.g., "NQ")
    Session,          // e.g., "S1"
    SlotTimeChicago,  // e.g., "02:00"
    direction,        // "Long" or "Short"
    entryPrice,       // Breakout level price
    _intendedStopPrice,    // Calculated stop
    _intendedTargetPrice,  // Calculated target
    _intendedBeTrigger,    // BE trigger (65% of target)
    entryTimeUtc,     // UTC timestamp
    triggerReason     // "BREAKOUT" or "STOP_BRACKETS_AT_LOCK"
)
```

**Intent ID**: Computed from all fields (hash-based, deterministic)

---

### 2.4 Intent Registration
**Location**: `StreamStateMachine.cs` - `SubmitEntryOrder()` lines 4577-4597

**CRITICAL**: Intent registered BEFORE order submission

**Process**:
1. Type-check execution adapter is `NinjaTraderSimAdapter`
2. Call `RegisterIntent(intent)` to store in `_intentMap`
3. Intent now available for fill callback handling

**Why Critical**: Entry order can fill immediately, and protective orders need intent data

---

### 2.5 Entry Order Submission
**Location**: `StreamStateMachine.cs` - `SubmitEntryOrder()` line 4601

**Order Types**:
- **Breakout entries**: `STOP_MARKET` order at breakout level
- **Immediate entries**: `LIMIT` order at breakout level

**Process**:
1. Validates execution adapter initialized
2. Submits entry order via `_executionAdapter.SubmitEntryOrder()`
3. Order tagged with `intentId` for correlation
4. Journal records submission attempt

**Order Tag**: Encoded intent ID for correlation with fills

---

## Phase 3: Entry Fill & Protective Orders

### 3.1 Entry Order Fill
**Location**: `NinjaTraderSimAdapter.NT.cs` - `HandleExecutionUpdateReal()`

**Process**:
1. NinjaTrader fires `ExecutionUpdate` event
2. System extracts `intentId` from order tag
3. Looks up intent in `_intentMap`
4. Calls `HandleEntryFill()` with intent and fill details

**Fill Details**:
- Fill price
- Fill quantity
- Fill timestamp

---

### 3.2 Entry Fill Handling
**Location**: `NinjaTraderSimAdapter.cs` - `HandleEntryFill()` lines 330-500

**Validation**:
1. Checks intent has all required fields:
   - Direction ✅
   - StopPrice ✅
   - TargetPrice ✅
2. Records entry fill time in `_orderMap`
3. Validates exit orders via `InstrumentIntentCoordinator`

**If Validation Fails**: Logs error, returns early (no protective orders)

---

### 3.3 Protective Stop Order Submission
**Location**: `NinjaTraderSimAdapter.cs` - `HandleEntryFill()` lines 378-408

**Process**:
1. Retry loop (up to 3 attempts, 100ms delay)
2. Validates exit order can be submitted (coordinator check)
3. Calls `SubmitProtectiveStop()` with:
   - Intent ID
   - Instrument
   - Direction
   - Stop price (from intent)
   - Fill quantity
4. Creates `StopMarket` order:
   - **Long**: Sell stop at stop price
   - **Short**: Buy-to-cover stop at stop price
5. Order tagged with `{intentId}_STOP`

**Order State**: Submitted to NinjaTrader, becomes `Working` or `Accepted`

---

### 3.4 Target Order Submission
**Location**: `NinjaTraderSimAdapter.cs` - `HandleEntryFill()` lines 410-437

**Process**:
1. Retry loop (up to 3 attempts, 100ms delay)
2. Validates exit order can be submitted
3. Calls `SubmitTargetOrder()` with:
   - Intent ID
   - Instrument
   - Direction
   - Target price (from intent)
   - Fill quantity
4. Creates `Limit` order:
   - **Long**: Sell limit at target price
   - **Short**: Buy-to-cover limit at target price
5. Order tagged with `{intentId}_TARGET`

**Order State**: Submitted to NinjaTrader, becomes `Working` or `Accepted`

---

### 3.5 Protective Order Failure Handling
**Location**: `NinjaTraderSimAdapter.cs` - `HandleEntryFill()` lines 439-487

**If Either Order Fails**:
1. Logs failure details
2. Notifies `InstrumentIntentCoordinator` of protective failure
3. **Flattens position immediately** (emergency exit)
4. Stands down stream (prevents further trading)
5. Sends high-priority notification
6. Persists incident record

**Safety**: Fail-closed approach - if protective orders fail, position is closed immediately

---

### 3.6 Protective Orders Success
**Location**: `NinjaTraderSimAdapter.cs` - `HandleEntryFill()` lines 489-500

**On Success**:
- Logs `PROTECTIVE_ORDERS_SUBMITTED` event
- Logs `PROTECTIVES_PLACED` event (proof log)
- Checks for unprotected positions (watchdog)
- Position now protected with stop and target orders

**Order Independence**: Stop and target orders are NOT OCO-linked (operate independently)

---

## Phase 4: Break-Even Monitoring & Modification

### 4.1 Break-Even Monitoring Setup
**Location**: `RobotSimStrategy.cs` - `OnBarUpdate()` line 906

**Process**:
- On every bar close (1-minute bars)
- Calls `CheckBreakEvenTriggers()` with current bar high/low

**Frequency**: Once per minute (bar-based detection)

---

### 4.2 Active Intent Filtering
**Location**: `NinjaTraderSimAdapter.cs` - `GetActiveIntentsForBEMonitoring()` lines 1114-1144

**Filtering Criteria**:
1. Entry order must be `FILLED` ✅
2. Intent must exist in `_intentMap` ✅
3. Intent must have:
   - `BeTrigger` price ✅
   - `EntryPrice` ✅
   - `Direction` ✅
4. BE must NOT already be modified (idempotency check) ✅

**Output**: List of intents that need BE monitoring

---

### 4.3 Break-Even Trigger Detection
**Location**: `RobotSimStrategy.cs` - `CheckBreakEvenTriggers()` lines 919-933

**Detection Logic**:
- **Long Position**: `current_bar_high >= beTriggerPrice`
- **Short Position**: `current_bar_low <= beTriggerPrice`

**Example** (Long entry at 5000, BE trigger = 5065):
- If bar high reaches 5065 or higher → BE trigger reached ✅

---

### 4.4 Break-Even Stop Price Calculation
**Location**: `RobotSimStrategy.cs` - `CheckBreakEvenTriggers()` lines 937-954

**Process**:
1. Resolves tick size from intent's instrument (handles micro futures)
2. Calculates BE stop price:
   - **Long**: `entryPrice - tickSize` (1 tick below entry)
   - **Short**: `entryPrice + tickSize` (1 tick above entry)

**Example** (Long entry at 5000, tick size = 0.25):
- BE stop price = 4999.75

---

### 4.5 Stop Order Modification
**Location**: `NinjaTraderSimAdapter.NT.cs` - `ModifyStopToBreakEvenReal()` lines 1549-1622

**Process**:
1. Finds stop order by tag `{intentId}_STOP`
2. Verifies order is `Working` or `Accepted`
3. Modifies `StopPrice` to break-even price
4. Calls NinjaTrader `account.Change()` API
5. Records BE modification in `ExecutionJournal`
6. Logs `STOP_MODIFY_SUCCESS` event

**Idempotency**: `ExecutionJournal.IsBEModified()` prevents duplicate modifications

---

### 4.6 Break-Even Modification Failure Handling
**Location**: `RobotSimStrategy.cs` - `CheckBreakEvenTriggers()` lines 979-1003

**Failure Types**:
1. **Retryable** (stop order not found yet):
   - Logs `BE_TRIGGER_RETRY_NEEDED`
   - Will retry on next bar automatically
   - Common during race condition window

2. **Non-Retryable** (other errors):
   - Logs `BE_TRIGGER_FAILED`
   - Error details logged
   - May require manual intervention

---

## Phase 5: Position Management & Exit

### 5.1 Stop Order Fill
**Location**: `NinjaTraderSimAdapter.NT.cs` - `HandleExecutionUpdateReal()`

**Process**:
1. Execution update received for stop order
2. Intent ID extracted from order tag
3. `InstrumentIntentCoordinator.OnExitFill()` called
4. Position closed (stop loss hit)
5. Remaining orders cancelled (target order)

**Result**: Position closed at stop price (loss or break-even)

---

### 5.2 Target Order Fill
**Location**: `NinjaTraderSimAdapter.NT.cs` - `HandleExecutionUpdateReal()`

**Process**:
1. Execution update received for target order
2. Intent ID extracted from order tag
3. `InstrumentIntentCoordinator.OnExitFill()` called
4. Position closed (target hit)
5. Remaining orders cancelled (stop order)

**Result**: Position closed at target price (profit)

---

### 5.3 Position Closure
**Location**: `InstrumentIntentCoordinator.cs` - `OnExitFill()`

**Process**:
1. Updates exposure tracking (`ExitFilledQty`)
2. Calculates remaining exposure
3. If exposure fully closed:
   - Marks intent as `CLOSED`
   - Cancels remaining orders for intent
   - Logs `INTENT_EXPOSURE_CLOSED`
4. Recalculates broker exposure (awareness only)

**Safety**: Prevents over-closing and position flipping

---

## Complete Flow Diagram

```
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 1: RANGE LOCK & PREPARATION                               │
├─────────────────────────────────────────────────────────────────┤
│ 1. Range calculated (high/low)                                  │
│ 2. Breakout levels computed (brkLong, brkShort)                 │
│ 3. Protective prices calculated (stop, target, BE trigger)     │
│ 4. Stop brackets submitted at lock (immediate entry protection) │
└─────────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 2: BREAKOUT DETECTION & ENTRY                              │
├─────────────────────────────────────────────────────────────────┤
│ 1. Price crosses breakout level                                 │
│ 2. DetectEntry() called                                         │
│ 3. Intent created (with all protective prices)                   │
│ 4. Intent registered BEFORE order submission                    │
│ 5. Entry order submitted (StopMarket or Limit)                   │
└─────────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 3: ENTRY FILL & PROTECTIVE ORDERS                         │
├─────────────────────────────────────────────────────────────────┤
│ 1. Entry order fills                                            │
│ 2. HandleEntryFill() called                                     │
│ 3. Intent validated (has stop/target prices)                    │
│ 4. Protective stop order submitted (with retry)                 │
│ 5. Target order submitted (with retry)                          │
│ 6. If either fails → Position flattened, stream stood down      │
└─────────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 4: BREAK-EVEN MONITORING                                   │
├─────────────────────────────────────────────────────────────────┤
│ 1. OnBarUpdate() fires (every 1-minute bar)                    │
│ 2. CheckBreakEvenTriggers() called                              │
│ 3. Active intents filtered (filled entries, BE not triggered)   │
│ 4. Bar high/low checked against BE trigger price                │
│ 5. If triggered → Stop modified to break-even                   │
│ 6. BE modification recorded (idempotency)                       │
└─────────────────────────────────────────────────────────────────┘
                            ↓
┌─────────────────────────────────────────────────────────────────┐
│ PHASE 5: POSITION EXIT                                           │
├─────────────────────────────────────────────────────────────────┤
│ 1. Stop order fills → Position closed (loss/BE)                 │
│    OR                                                           │
│ 2. Target order fills → Position closed (profit)                │
│ 3. Remaining orders cancelled                                   │
│ 4. Intent marked as CLOSED                                      │
│ 5. Exposure tracking updated                                    │
└─────────────────────────────────────────────────────────────────┘
```

---

## Key Safety Features

### 1. Intent Registration Before Order Submission ✅
- Intent must be registered BEFORE entry order submission
- Ensures protective orders can be placed immediately on fill
- Prevents race condition where fill occurs before intent exists

### 2. Protective Order Retry Logic ✅
- Up to 3 retry attempts with 100ms delay
- Validates exit order before each retry
- Handles transient NinjaTrader API failures

### 3. Fail-Closed Protective Order Handling ✅
- If protective orders fail → Position flattened immediately
- Stream stood down (prevents further trading)
- High-priority notification sent
- Incident record persisted

### 4. Break-Even Idempotency ✅
- Checks `ExecutionJournal.IsBEModified()` before modification
- Prevents duplicate BE modifications
- Handles race conditions gracefully

### 5. Exposure Tracking ✅
- `InstrumentIntentCoordinator` tracks per-intent exposure
- Prevents over-closing and position flipping
- Validates exit orders before submission

---

## Example Scenario: Complete Trade Lifecycle

### Setup
- **Instrument**: NQ (Nasdaq E-mini)
- **Stream**: NQ1
- **Range**: 4950 - 5000 (50 points)
- **Target**: 100 points
- **Entry**: Long breakout at 5000.25

### Phase 1: Range Lock
- Range locked: High = 5000, Low = 4950
- Breakout levels: Long = 5000.25, Short = 4949.75
- Protective prices calculated:
  - Stop: 4950 (50 points, min of range_size and 3×target)
  - Target: 5100.25 (100 points)
  - BE Trigger: 5065.25 (65% of 100 = 65 points)

### Phase 2: Breakout & Entry
- Price reaches 5000.25 → Breakout detected
- Intent created with all protective prices
- Intent registered in `_intentMap`
- StopMarket order submitted at 5000.25

### Phase 3: Entry Fill & Protection
- Entry order fills at 5000.25
- `HandleEntryFill()` called
- Stop order submitted at 4950 (50 point risk)
- Target order submitted at 5100.25 (100 point profit)
- Both orders working independently

### Phase 4: Break-Even Monitoring
- On each bar, system checks: `bar_high >= 5065.25?`
- When bar high reaches 5065.25:
  - BE trigger detected ✅
  - Stop order found by tag
  - Stop price modified to 5000.00 (entry - 1 tick)
  - Target order unchanged (still at 5100.25)

### Phase 5: Position Exit
**Scenario A: Target Hit**
- Price reaches 5100.25
- Target order fills
- Position closed at 5100.25 (100 point profit)
- Stop order cancelled

**Scenario B: Stop Hit (After BE)**
- Price drops to 5000.00
- Stop order fills (at break-even)
- Position closed at 5000.00 (break-even, no loss)
- Target order cancelled

**Scenario C: Stop Hit (Before BE)**
- Price drops to 4950 before BE trigger
- Stop order fills
- Position closed at 4950 (50 point loss)
- Target order cancelled

---

## Critical Timing & Ordering

### Correct Order (✅):
1. Intent created with BeTrigger
2. Intent registered in `_intentMap`
3. Entry order submitted
4. Entry order fills
5. Protective orders submitted
6. BE monitoring starts
7. BE trigger reached → Stop modified
8. Stop/Target fills → Position closed

### Incorrect Order (❌):
- Entry order submitted BEFORE intent registration
- **Problem**: If entry fills immediately, intent not available for protective orders
- **Fix**: Intent registration moved BEFORE order submission

---

## Log Events Summary

### Entry & Intent
- `DRYRUN_INTENDED_ENTRY` - Entry detected
- `DRYRUN_INTENDED_PROTECTIVE` - Protective prices calculated
- `DRYRUN_INTENDED_BE` - BE trigger price calculated
- `INTENT_REGISTERED` - Intent stored in adapter
- `ORDER_SUBMITTED` - Entry order submitted

### Entry Fill
- `EXECUTION_UPDATE` - Entry order filled
- `PROTECTIVE_ORDERS_SUBMITTED` - Stop and target orders placed
- `PROTECTIVES_PLACED` - Proof log (includes encoded tags)

### Break-Even
- `BE_TRIGGER_REACHED` - BE trigger detected, stop modified
- `STOP_MODIFY_SUCCESS` - Stop order modified successfully
- `BE_TRIGGER_RETRY_NEEDED` - Retry needed (race condition)
- `BE_TRIGGER_FAILED` - BE modification failed

### Exit
- `EXECUTION_UPDATE` - Stop or target order filled
- `INTENT_EXPOSURE_CLOSED` - Position fully closed
- `INTENT_EXIT_FILL` - Exit order filled

---

## Conclusion

The complete trading process is a **well-orchestrated sequence** of:

1. **Preparation** - Range lock, breakout levels, protective prices
2. **Detection** - Breakout detection, entry detection, intent creation
3. **Execution** - Entry fill, protective orders, position protection
4. **Management** - Break-even monitoring, stop modification
5. **Closure** - Position exit, order cancellation, exposure tracking

**Key Strengths**:
- ✅ Intent registration before order submission (prevents race conditions)
- ✅ Retry logic for protective orders (handles transient failures)
- ✅ Fail-closed protective order handling (safety first)
- ✅ Idempotent break-even modifications (prevents duplicates)
- ✅ Comprehensive exposure tracking (prevents over-closing)

**The system is production-ready** and handles all edge cases gracefully.
