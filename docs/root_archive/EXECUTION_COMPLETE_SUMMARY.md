# Complete Execution Summary

## Overview

This document provides a comprehensive summary of the entire execution flow in the QTSW2 trading robot, from initialization through order placement, fill handling, and P&L calculation.

---

## 1. Initialization & Setup

### 1.1 RobotEngine Creation

**Location**: `modules/robot/core/RobotEngine.cs`

**Constructor Parameters**:
- `projectRoot`: Project root directory
- `timetablePollInterval`: How often to poll for timetable updates
- `executionMode`: DRYRUN, SIM, or LIVE
- `instrument`: Execution instrument from NinjaTrader (e.g., "MGC")
- `masterInstrumentName`: MasterInstrument.Name from NinjaTrader (e.g., "GC")

**Initialization Steps**:
1. Load logging configuration
2. Initialize ExecutionJournal (for idempotency)
3. Load ParitySpec (instrument specifications)
4. Load ExecutionPolicy (order quantities, enabled instruments)
5. Create ExecutionAdapter based on mode:
   - DRYRUN → `NullExecutionAdapter` (logs only)
   - SIM → `NinjaTraderSimAdapter` (real NT API)
   - LIVE → `NinjaTraderLiveAdapter` (future)
6. Initialize RiskGate (fail-closed safety checks)
7. Initialize KillSwitch (emergency stop)
8. Initialize HealthMonitor (system health tracking)

### 1.2 NinjaTrader Strategy Setup

**Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

**Setup Flow**:
1. Verify SIM account (fail-closed if not Sim)
2. Extract execution instrument from `Instrument.FullName` (e.g., "MGC 03-26" → "MGC")
3. Extract MasterInstrument.Name (e.g., "GC")
4. Create RobotEngine with both values
5. Set NT context: `adapter.SetNTContext(Account, Instrument)`
6. Wire NT events:
   - `Account.OrderUpdate += OnOrderUpdate`
   - `Account.ExecutionUpdate += OnExecutionUpdate`

---

## 2. Timetable Processing

### 2.1 Timetable File

**Location**: `data/timetable/timetable_current.json`

**Structure**:
```json
{
  "trading_date": "2025-02-01",
  "timezone": "America/Chicago",
  "as_of": "2025-02-01T14:30:00Z",
  "streams": [
    {
      "stream": "GC1",
      "instrument": "GC",        // Canonical instrument
      "session": "S1",
      "slot_time": "07:30",
      "enabled": true
    }
  ]
}
```

### 2.2 Timetable Application

**Location**: `RobotEngine.ApplyTimetable()`

**Processing Steps**:

1. **Validate Trading Date**:
   - Must match today's Chicago date
   - Timezone must be "America/Chicago"
   - Trading date locks on first bar

2. **Determine Execution Instrument** (Priority Order):
   - **Priority 1**: NinjaTrader anchor (`_executionInstrument`) if MasterInstrument matches canonical
   - **Priority 2**: Execution policy enabled instrument
   - **Priority 3**: Timetable instrument (fallback)

3. **MasterInstrument Matching** (Authoritative Rule):
   ```csharp
   // Compare MasterInstrument.Name directly with timetable canonical
   if (_masterInstrumentName != timetableCanonical) {
       // Skip directive - log STREAM_SKIPPED with CANONICAL_MISMATCH
       continue;
   }
   ```

4. **Create StreamStateMachine** for each enabled stream:
   - Stream ID: Canonicalized (e.g., "MES1" → "ES1")
   - Execution Instrument: Determined from priority above (e.g., "MES")
   - Canonical Instrument: Base instrument (e.g., "ES")
   - Order Quantity: From execution policy

5. **Log Summary**:
   - `TIMETABLE_PARSING_COMPLETE` event
   - Includes: accepted, skipped, skipped_reasons

---

## 3. Stream State Machine Lifecycle

### 3.1 State Transitions

**States** (in order):
1. **PRE_HYDRATION**: Loading historical bars
2. **ARMED**: Ready for range building
3. **RANGE_BUILDING**: Building range from bars
4. **RANGE_LOCKED**: Range computed, waiting for breakout
5. **DONE**: Trade completed or no trade
6. **SUSPENDED_DATA_INSUFFICIENT**: Not enough data

### 3.2 State Flow

```
PRE_HYDRATION
    ↓ (bars loaded, pre-hydration complete)
ARMED
    ↓ (range start time reached)
RANGE_BUILDING
    ↓ (slot_time reached)
RANGE_LOCKED
    ↓ (breakout detected OR market close)
DONE
```

---

## 4. Range Building

### 4.1 Range Window

**Definition**:
- **Start**: Session start time (from TradingHours, default 17:00 CT)
- **End**: Slot time from timetable (e.g., "07:30" CT)
- **Window**: `[range_start, slot_time)` (slot_time is EXCLUSIVE)

### 4.2 Bar Collection

**Sources** (precedence order):
1. **LIVE**: Live feed bars (`OnBar()` callback)
2. **BARSREQUEST**: Historical bars from NinjaTrader API
3. **CSV**: File-based pre-hydration (DRYRUN only)

**Bar Filtering**:
- Only bars within `[range_start, slot_time)` are used
- Future bars filtered out
- Partial/in-progress bars filtered out
- Deduplication: LIVE > BARSREQUEST > CSV

### 4.3 Range Computation

**At Slot Time** (`TryLockRange()`):
1. Compute range from bar buffer:
   - `RangeHigh = max(bar.high)`
   - `RangeLow = min(bar.low)`
2. Calculate breakout levels:
   - `brkLong = round_to_tick(RangeHigh + tickSize)`
   - `brkShort = round_to_tick(RangeLow - tickSize)`
3. Get freeze close (last bar close)
4. Lock range (immutable after lock)
5. Transition to `RANGE_LOCKED` state

**Range Lock Events**:
- `RANGE_LOCKED` event persisted
- `HYDRATION_EVENT` persisted
- Range values stored in journal

---

## 5. Entry Detection & Submission

### 5.1 Entry Detection

**Two Detection Methods**:

1. **Immediate Entry at Lock**:
   ```csharp
   // Check if freeze_close already beyond breakout level
   if (freezeClose >= brkLong) → Immediate Long
   if (freezeClose <= brkShort) → Immediate Short
   ```

2. **Breakout Detection** (after lock):
   ```csharp
   // Check each bar for breakout
   if (bar.high >= brkLong) → Long Breakout
   if (bar.low <= brkShort) → Short Breakout
   ```

**Entry Cutoff**: Market close (default 16:00 CT)
- Breakouts after market close are ignored (NoTrade)

### 5.2 Entry Submission Flow

**Location**: `StreamStateMachine.RecordIntendedEntry()`

**Steps**:

1. **Create Intent**:
   ```csharp
   var intent = new Intent(
       tradingDate: "2025-02-01",
       stream: "GC1",
       instrument: "GC",              // Canonical
       session: "S1",
       slotTimeChicago: "07:30",
       direction: "Long",
       entryPrice: brkLong,
       stopPrice: stopPrice,
       targetPrice: targetPrice,
       beTrigger: beTrigger,
       entryTimeUtc: entryTimeUtc,
       triggerReason: "BREAKOUT" or "IMMEDIATE_AT_LOCK"
   );
   ```

2. **Compute Intent ID**:
   ```csharp
   intentId = ExecutionJournal.ComputeIntentId(
       tradingDate, stream, instrument, session, slotTimeChicago,
       direction, entryPrice, stopPrice, targetPrice, beTrigger
   );
   // Hash of 15 canonical fields → 16-char hex string
   ```

3. **RiskGate Check** (All gates must pass):
   - ✅ Kill switch not enabled
   - ✅ Timetable validated
   - ✅ Stream armed
   - ✅ Within allowed session window
   - ✅ Trading date set
   - ✅ Recovery state OK (if applicable)

4. **Idempotency Check**:
   ```csharp
   if (_executionJournal.IsIntentSubmitted(intentId, tradingDate, stream)) {
       // Already submitted - skip (idempotent)
       return;
   }
   ```

5. **Submit Entry Order**:
   ```csharp
   // DRYRUN: Log only
   // SIM/LIVE: Submit via adapter
   var result = _executionAdapter.SubmitEntryOrder(
       intentId,
       executionInstrument,  // e.g., "MGC"
       direction,
       entryPrice,
       quantity,            // From execution policy
       entryOrderType,      // "MARKET" or "LIMIT"
       utcNow
   );
   ```

6. **Journal Recording**:
   ```csharp
   _executionJournal.RecordSubmission(
       intentId, tradingDate, stream,
       executionInstrument, "ENTRY",
       brokerOrderId, utcNow, entryPrice
   );
   ```

---

## 6. Fill Handling

### 6.1 Execution Update Flow

**Location**: `NinjaTraderSimAdapter.HandleExecutionUpdateReal()`

**Steps**:

1. **Extract Intent ID**:
   ```csharp
   var encodedTag = GetOrderTag(order);
   var intentId = RobotOrderIds.DecodeIntentId(encodedTag);
   ```

2. **Resolve Intent Context** (`ResolveIntentContextOrFailClosed()`):
   ```csharp
   // Get from _intentMap
   var intent = _intentMap[intentId];
   
   // Extract required fields:
   - tradingDate = intent.TradingDate
   - stream = intent.Stream
   - direction = intent.Direction
   - contractMultiplier = Instrument.MasterInstrument.PointValue
   
   // Validate all fields non-empty (fail-closed if missing)
   ```

3. **Classify Fill Type**:
   ```csharp
   if (orderInfo.IsEntryOrder == true) {
       // Entry fill
   } else if (orderInfo.OrderType == "STOP" || orderInfo.OrderType == "TARGET") {
       // Exit fill
   } else {
       // Unknown - orphan and fail-closed
   }
   ```

4. **Record Fill** (Delta Quantity Only):
   ```csharp
   // Entry fill
   _executionJournal.RecordEntryFill(
       intentId, tradingDate, stream,
       fillPrice,
       fillQuantity,        // DELTA ONLY (this fill's qty)
       utcNow,
       contractMultiplier,
       direction,
       executionInstrument,
       canonicalInstrument
   );
   
   // Exit fill
   _executionJournal.RecordExitFill(
       intentId, tradingDate, stream,
       exitFillPrice,
       exitFillQuantity,    // DELTA ONLY (this fill's qty)
       exitOrderType,       // "STOP" or "TARGET"
       utcNow
   );
   ```

### 6.2 Entry Fill Handling

**Location**: `NinjaTraderSimAdapter.HandleEntryFill()`

**Steps**:

1. **Validate Intent Completeness**:
   - Direction must exist
   - StopPrice must exist
   - TargetPrice must exist
   - If missing → Flatten immediately (fail-closed)

2. **Submit Protective Orders** (OCO Pair):
   ```csharp
   // Generate OCO group
   var ocoGroup = $"QTSW2:{intentId}_PROTECTIVE";
   
   // Submit stop (with retry)
   SubmitProtectiveStop(intentId, instrument, direction, stopPrice, quantity, ocoGroup);
   
   // Submit target (with retry)
   SubmitTargetOrder(intentId, instrument, direction, targetPrice, quantity, ocoGroup);
   ```

3. **Track Order State**:
   - Record entry fill time
   - Track protective order acknowledgments
   - Monitor for protective failures

### 6.3 Exit Fill Handling

**Location**: `NinjaTraderSimAdapter.HandleExecutionUpdateReal()`

**Steps**:

1. **Record Exit Fill**:
   ```csharp
   _executionJournal.RecordExitFill(...);
   ```

2. **Check Trade Completion**:
   ```csharp
   // In RecordExitFill:
   if (ExitFilledQuantityTotal == EntryFilledQuantityTotal) {
       // Trade complete - compute P&L
       ComputePnL();
       TradeCompleted = true;
   }
   ```

3. **Flatten Remaining Position** (if needed):
   ```csharp
   _coordinator?.OnExitFill(intentId, filledTotal, utcNow);
   ```

---

## 7. P&L Calculation

### 7.1 Calculation Trigger

**Location**: `ExecutionJournal.RecordExitFill()`

**Condition**: Only when `ExitFilledQuantityTotal == EntryFilledQuantityTotal`

**If Partial Exit**: Do NOT compute P&L, do NOT mark completed

**If Overfill**: Trigger emergency (fail-closed)

### 7.2 Weighted Average Prices

**Entry**:
```csharp
// Cumulative weighted average
EntryFillNotional += fillPrice * fillQuantity;  // Delta qty
EntryAvgFillPrice = EntryFillNotional / EntryFilledQuantityTotal;
```

**Exit**:
```csharp
// Cumulative weighted average
ExitFillNotional += exitFillPrice * exitFillQuantity;  // Delta qty
ExitAvgFillPrice = ExitFillNotional / ExitFilledQuantityTotal;
```

### 7.3 P&L Formula

**Points Calculation**:
```csharp
if (direction == "Long") {
    points = ExitAvgFillPrice - EntryAvgFillPrice;
} else if (direction == "Short") {
    points = EntryAvgFillPrice - ExitAvgFillPrice;
}
```

**Dollar Calculation**:
```csharp
RealizedPnLPoints = points;
RealizedPnLGross = points * EntryFilledQuantityTotal * contractMultiplier;
RealizedPnLNet = RealizedPnLGross - slippageDollars - commission - fees;
```

**Completion**:
```csharp
TradeCompleted = true;
CompletedAtUtc = utcNow.ToString("o");
CompletionReason = exitOrderType;  // "STOP" or "TARGET"
```

---

## 8. Protective Orders

### 8.1 Break-Even Modification

**Trigger**: When price reaches 65% of target

**Location**: `StreamStateMachine.HandleRangeLockedState()`

**Logic**:
```csharp
var beTrigger = entryPrice + (targetPrice - entryPrice) * 0.65m;
if (currentPrice >= beTrigger) {
    ModifyStopToBreakEven(intentId, entryPrice + 1 tick);
}
```

**Implementation**:
```csharp
// Find existing stop order
var stopOrder = account.Orders.FirstOrDefault(o => 
    o.Tag == $"{intentId}_STOP" && 
    o.OrderState == OrderState.Working);

// Modify stop price
stopOrder.StopPrice = beStopPrice;
account.Change(new[] { stopOrder });
```

### 8.2 Protective Order Failure Handling

**Failure Modes**:
1. Intent incomplete (missing Direction/StopPrice/TargetPrice)
2. Protective order submission failure
3. Protective order rejection

**Fail-Closed Response**:
1. Log CRITICAL event
2. Flatten position immediately
3. Stand down stream
4. Persist incident record
5. Raise high-priority alert

---

## 9. Orphan Fill Handling

### 9.1 Orphan Detection

**Location**: `NinjaTraderSimAdapter.ResolveIntentContextOrFailClosed()`

**Failure Reasons**:
- `INTENT_NOT_FOUND`: Intent not in `_intentMap`
- `MISSING_TRADING_DATE`: TradingDate is empty/whitespace
- `MISSING_STREAM`: Stream is empty/whitespace
- `MISSING_DIRECTION`: Direction is missing
- `MISSING_MULTIPLIER`: Contract multiplier unavailable
- `UNKNOWN_EXIT_TYPE`: Exit order type not recognized

### 9.2 Orphan Logging

**Location**: `data/execution_incidents/orphan_fills_YYYY-MM-DD.jsonl`

**Format**:
```json
{
  "event_type": "ORPHAN_FILL",
  "timestamp_utc": "2025-02-01T14:30:00Z",
  "intent_id": "abc123def456",
  "tag": "encoded_tag",
  "order_type": "ENTRY",
  "instrument": "MGC",
  "fill_price": 2500.50,
  "fill_quantity": 1,
  "stream": "GC1",
  "reason": "INTENT_NOT_FOUND",
  "action_taken": "EXECUTION_BLOCKED"
}
```

**Fail-Closed Actions**:
- Log CRITICAL event
- Write orphan record to JSONL
- Stand down stream (if known)
- Block instrument execution
- **DO NOT call journal with empty strings**

---

## 10. Execution Modes

### 10.1 DRYRUN Mode

**Behavior**:
- No real orders placed
- All execution logged only
- Uses `NullExecutionAdapter`
- Bar data from CSV files or BarsRequest
- Full execution flow simulated

**Use Cases**:
- Testing
- Development
- Backtesting validation

### 10.2 SIM Mode

**Behavior**:
- Real orders in NinjaTrader SIM account
- Uses `NinjaTraderSimAdapter`
- Real NT API calls
- Real fill callbacks
- Real P&L calculation

**Requirements**:
- Strategy must run in SIM account
- NT context must be set (`SetNTContext()`)
- NT events must be wired

**Safety**:
- SIM account verification (fail-closed if not Sim)
- All fail-closed behaviors active
- Full risk gates enforced

### 10.3 LIVE Mode

**Status**: Not yet enabled

**Future Implementation**:
- Real orders in live brokerage account
- Uses `NinjaTraderLiveAdapter`
- Additional safety checks required

---

## 11. Key Invariants & Safety Mechanisms

### 11.1 Fail-Closed Principles

1. **Missing Context**: Orphan fill → Block execution
2. **Journal Corruption**: Stand down stream
3. **Intent Incomplete**: Flatten position immediately
4. **Protective Failure**: Flatten position immediately
5. **Overfill**: Trigger emergency, stand down stream
6. **MasterInstrument Mismatch**: Skip directive, log loudly

### 11.2 Idempotency

**ExecutionJournal**:
- Prevents double-submission
- Persists per `(tradingDate, stream, intentId)`
- Survives restarts
- Journal corruption → Stand down stream

### 11.3 Risk Gates

**All gates must pass**:
- Kill switch not enabled
- Timetable validated
- Stream armed
- Within session window
- Trading date set
- Recovery state OK

**If any gate fails**: Order submission blocked, logged

---

## 12. Data Flow Summary

### 12.1 Complete Flow Diagram

```
Timetable File
    ↓
RobotEngine.ApplyTimetable()
    ↓ (MasterInstrument matching)
StreamStateMachine Created
    ↓
PRE_HYDRATION → ARMED → RANGE_BUILDING
    ↓ (bars collected)
RANGE_LOCKED
    ↓ (breakout detected)
RecordIntendedEntry()
    ↓
RiskGate.CheckGates()
    ↓ (all gates pass)
ExecutionJournal.IsIntentSubmitted()
    ↓ (not submitted)
ExecutionAdapter.SubmitEntryOrder()
    ↓
NinjaTrader API: CreateOrder() + Submit()
    ↓
NT Event: ExecutionUpdate
    ↓
HandleExecutionUpdateReal()
    ↓
ResolveIntentContextOrFailClosed()
    ↓ (context resolved)
ExecutionJournal.RecordEntryFill()
    ↓ (entry fill recorded)
HandleEntryFill()
    ↓
SubmitProtectiveStop() + SubmitTargetOrder()
    ↓
NT Events: OrderUpdate (acknowledgments)
    ↓
(Price moves to BE trigger)
ModifyStopToBreakEven()
    ↓
(Stop or Target fills)
HandleExecutionUpdateReal()
    ↓
ExecutionJournal.RecordExitFill()
    ↓ (exit qty == entry qty)
ComputePnL()
    ↓
TradeCompleted = true
```

---

## 13. Critical Data Structures

### 13.1 Intent

**Fields**:
- `TradingDate`: "2025-02-01"
- `Stream`: "GC1"
- `Instrument`: "GC" (canonical)
- `Session`: "S1"
- `SlotTimeChicago`: "07:30"
- `Direction`: "Long" or "Short"
- `EntryPrice`: Breakout level
- `StopPrice`: Protective stop
- `TargetPrice`: Profit target
- `BeTrigger`: Break-even trigger price

### 13.2 ExecutionJournalEntry

**Identity Fields**:
- `IntentId`: Hash of canonical fields
- `TradingDate`: Required (non-empty)
- `Stream`: Required (non-empty)

**Entry Tracking**:
- `EntryFilledQuantityTotal`: Cumulative entry qty
- `EntryAvgFillPrice`: Weighted average
- `EntryFillNotional`: Sum(price * qty)
- `EntryFilledAtUtc`: First entry fill time

**Exit Tracking**:
- `ExitFilledQuantityTotal`: Cumulative exit qty
- `ExitAvgFillPrice`: Weighted average
- `ExitFillNotional`: Sum(price * qty)
- `ExitOrderType`: "STOP" or "TARGET"
- `ExitFilledAtUtc`: First exit fill time

**Completion Fields**:
- `TradeCompleted`: true when exit qty == entry qty
- `RealizedPnLGross`: Gross P&L in dollars
- `RealizedPnLNet`: Net P&L (gross - costs)
- `RealizedPnLPoints`: Points P&L
- `CompletedAtUtc`: Completion timestamp
- `CompletionReason`: Exit order type

### 13.3 StreamStateMachine

**Key Properties**:
- `Stream`: Stream ID (e.g., "GC1")
- `ExecutionInstrument`: What to trade (e.g., "MGC")
- `CanonicalInstrument`: Logic identity (e.g., "GC")
- `State`: Current state (PRE_HYDRATION → DONE)
- `RangeHigh` / `RangeLow`: Computed range
- `FreezeClose`: Last bar close price
- `_brkLongRounded` / `_brkShortRounded`: Breakout levels

---

## 14. Logging & Audit Trail

### 14.1 Event Types

**Engine Events**:
- `EXECUTION_MODE_SET`: Mode configured
- `TIMETABLE_PARSING_COMPLETE`: Timetable processed
- `STREAM_SKIPPED`: Directive skipped (with reason)
- `STREAM_INITIALIZED`: Stream created

**Execution Events**:
- `ORDER_SUBMIT_ATTEMPT`: Order submission started
- `ORDER_SUBMIT_SUCCESS`: Order submitted successfully
- `ORDER_SUBMIT_FAIL`: Order submission failed
- `EXECUTION_FILLED`: Fill received
- `EXECUTION_PARTIAL_FILL`: Partial fill received
- `PROTECTIVE_ORDERS_SUBMITTED`: Stop + target submitted
- `STOP_MODIFY_SUCCESS`: Break-even modification successful
- `TRADE_COMPLETED`: Trade completed with P&L

**Error Events**:
- `ORPHAN_FILL_CRITICAL`: Fill cannot be attributed
- `EXECUTION_BLOCKED`: Order blocked by risk gate
- `INTENT_INCOMPLETE_UNPROTECTED_POSITION`: Intent missing fields
- `EXECUTION_JOURNAL_CORRUPTION`: Journal corruption detected

### 14.2 Persistence

**Execution Journal**:
- Location: `data/execution_journals/{tradingDate}_{stream}_{intentId}.json`
- Purpose: Idempotency, audit trail
- Format: JSON (one file per intent)

**Orphan Fills**:
- Location: `data/execution_incidents/orphan_fills_YYYY-MM-DD.jsonl`
- Format: JSONL (one record per line)
- Purpose: Audit trail for unattributable fills

**Range Locked Events**:
- Location: `data/execution_journals/ranges/`
- Format: JSONL
- Purpose: Range reconstruction on restart

**Hydration Events**:
- Location: `data/execution_journals/hydration/`
- Format: JSONL
- Purpose: State reconstruction on restart

---

## 15. Execution Summary

### 15.1 Order Submission Sequence

1. **Entry Order**: Market or Limit at breakout level
2. **Entry Fill**: Triggers protective order submission
3. **Protective Stop**: StopMarket order (OCO group)
4. **Protective Target**: Limit order (OCO group)
5. **Break-Even**: Stop modification when trigger reached
6. **Exit Fill**: Stop or Target fills
7. **Trade Complete**: P&L calculated, journal updated

### 15.2 Safety Guarantees

✅ **SIM-only enforcement**: Fails closed if not Sim account
✅ **Idempotency**: ExecutionJournal prevents double-submission
✅ **Fail-closed**: Missing context → Block execution
✅ **Orphan handling**: Unattributable fills logged and blocked
✅ **Protective orders**: Always submitted after entry fill
✅ **Position protection**: Incomplete intent → Flatten immediately
✅ **MasterInstrument matching**: Explicit canonical matching
✅ **Delta quantities**: Journal methods accept delta only (no double-counting)
✅ **Weighted averages**: Correct partial fill handling
✅ **P&L gating**: Only computed on completion (exit qty == entry qty)

---

## 16. Key Files Reference

### Core Execution
- `modules/robot/core/RobotEngine.cs`: Main engine orchestrator
- `modules/robot/core/StreamStateMachine.cs`: Stream lifecycle and state management
- `modules/robot/core/Execution/ExecutionJournal.cs`: Trade journal and P&L calculation
- `modules/robot/core/Execution/NinjaTraderSimAdapter.cs`: SIM execution adapter
- `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`: Real NT API implementations
- `modules/robot/core/Execution/RiskGate.cs`: Fail-closed safety gates
- `modules/robot/core/Execution/KillSwitch.cs`: Emergency stop mechanism

### NinjaTrader Integration
- `modules/robot/ninjatrader/RobotSimStrategy.cs`: NT Strategy host
- `modules/robot/ninjatrader/RobotSkeletonStrategy.cs`: DRYRUN skeleton strategy

### Models
- `modules/robot/core/Execution/Intent.cs`: Trade intent representation
- `modules/robot/core/Execution/ExecutionJournalEntry.cs`: Journal entry model
- `modules/robot/core/Models.ExecutionPolicy.cs`: Execution policy configuration

---

## 17. Execution Checklist

### Pre-Execution
- [ ] Kill switch disabled
- [ ] Timetable file exists and valid
- [ ] Execution policy loaded
- [ ] SIM account verified (SIM mode)
- [ ] NT context set (SIM mode)
- [ ] NT events wired (SIM mode)

### During Execution
- [ ] Trading date locked from first bar
- [ ] Range computed at slot time
- [ ] Breakout levels calculated correctly
- [ ] Entry detected (immediate or breakout)
- [ ] Risk gates all pass
- [ ] Intent ID computed correctly
- [ ] Idempotency check passes
- [ ] Order submitted with correct execution instrument
- [ ] Fill recorded with delta quantity
- [ ] Context resolved (tradingDate, stream, direction)
- [ ] Protective orders submitted after entry fill
- [ ] Break-even modification triggered correctly
- [ ] Exit fill recorded with delta quantity
- [ ] P&L calculated only on completion
- [ ] Trade marked completed

### Post-Execution
- [ ] Journal persisted correctly
- [ ] P&L values stored
- [ ] Trade completion logged
- [ ] Stream committed
- [ ] No orphan fills
- [ ] All fills attributable to streams

---

**End of Execution Summary**
