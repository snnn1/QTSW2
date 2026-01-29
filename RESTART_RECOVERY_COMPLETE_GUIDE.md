# Complete Guide: Restart & Recovery When Trades Are Active

## Executive Summary

**Question**: What happens when you restart NinjaTrader with active trades?

**Answer**: The robot remembers everything and continues trading safely:
1. ✅ **State Persisted**: Stream journals track what happened
2. ✅ **Execution Journal**: Records all fills, orders, BE modifications
3. ✅ **Rehydration**: Loads historical bars via BarsRequest
4. ✅ **Reconstruction**: Rebuilds range from historical + live bars
5. ✅ **Duplicate Prevention**: Won't double-enter trades
6. ✅ **Active Position Handling**: Monitors existing positions, manages protective orders

---

## How The Robot Remembers

### 1. Stream Journal (Per Stream Per Day)

**Location**: `logs/robot/journal/{tradingDate}_{stream}.json`

**What It Stores**:
```json
{
  "TradingDate": "2026-01-28",
  "Stream": "ES1",
  "Committed": false,
  "CommitReason": null,
  "LastState": "RANGE_LOCKED",
  "LastUpdateUtc": "2026-01-28T14:30:00Z",
  "StopBracketsSubmittedAtLock": true,
  "EntryDetected": false
}
```

**Key Fields**:
- `Committed`: `true` = stream done for the day (entry filled or market close)
- `StopBracketsSubmittedAtLock`: `true` = stop orders already placed
- `EntryDetected`: `true` = entry order filled
- `LastState`: Last state before restart (e.g., "RANGE_LOCKED")

**Purpose**: Prevents duplicate order submission, tracks stream progress

---

### 2. Execution Journal (Per Intent)

**Location**: `data/execution_journals/{tradingDate}_{stream}_{intentId}.json`

**What It Stores**:
```json
{
  "IntentId": "abc123...",
  "TradingDate": "2026-01-28",
  "Stream": "ES1",
  "Instrument": "MES",
  "EntrySubmitted": true,
  "EntrySubmittedAt": "2026-01-28T14:30:00Z",
  "EntryFilled": true,
  "EntryFilledAt": "2026-01-28T14:31:00Z",
  "FillPrice": 2650.0,
  "FillQuantity": 1,
  "BEModified": false,
  "StopOrderId": "357414280488",
  "TargetOrderId": "357414280489"
}
```

**Key Fields**:
- `EntryFilled`: `true` = entry order filled
- `BEModified`: `true` = break-even stop already modified
- `StopOrderId` / `TargetOrderId`: Broker order IDs

**Purpose**: Idempotency - prevents duplicate submissions, tracks fills

---

## Restart Flow (Step by Step)

### Step 1: Strategy Initialization

**When**: NinjaTrader strategy starts

**What Happens**:
1. Load timetable and validate
2. Create/load stream state machines
3. **Check for existing journals** → detect restart

**Code**: `StreamStateMachine` constructor (line 289)
```csharp
var existing = journals.TryLoad(tradingDateStr, Stream);
var isRestart = existing != null;
```

---

### Step 2: Detect Mid-Session Restart

**When**: Journal exists but not committed, and current time >= range start

**What Happens**:
1. Log `MID_SESSION_RESTART_DETECTED` event
2. Set `isMidSessionRestart = true`
3. Policy: **"Restart = Full Reconstruction"**

**Code**: `StreamStateMachine` constructor (line 305)
```csharp
isMidSessionRestart = !existing.Committed && nowChicago >= RangeStartChicagoTime;
```

**Policy**: Reconstruct range from historical + live bars (may differ from uninterrupted run)

---

### Step 3: Restore State Flags

**What Gets Restored**:

1. **Stop Brackets Flag**:
   ```csharp
   _stopBracketsSubmittedAtLock = existing.StopBracketsSubmittedAtLock;
   ```
   - If `true`: Won't resubmit stop orders
   - If `false`: Will retry on restart

2. **Entry Detection**:
   ```csharp
   if (!existing.EntryDetected)
   {
       _entryDetected = _executionJournal.HasEntryFillForStream(tradingDateStr, Stream);
   }
   ```
   - Checks execution journal for any filled entry
   - Prevents duplicate entry detection

3. **Breakout Levels** (if RANGE_LOCKED):
   ```csharp
   if (existing.LastState == "RANGE_LOCKED")
   {
       ComputeBreakoutLevelsAndLog(utcNow);
   }
   ```
   - Recomputes breakout levels from range high/low
   - Ready for when state transitions back to RANGE_LOCKED

**Code**: `StreamStateMachine` constructor (line 356-397)

---

### Step 4: Pre-Hydration (Historical Bars)

**When**: State = `PRE_HYDRATION`

**What Happens**:
1. **BarsRequest** loads historical bars from `range_start` to `min(slot_time, now)`
2. Bars fed to engine via `OnBar()` callback
3. Bars stored in `_barBuffer` (in-memory)
4. Pre-hydration completes when:
   - Enough bars loaded (range start to slot_time)
   - OR current time >= slot_time (use available bars)

**Code**: `RobotSimStrategy.RequestHistoricalBarsForPreHydration()`

**Example**:
```
Restart at 10:00 AM
Range start: 08:00 AM
Slot time: 09:30 AM
BarsRequest loads: 08:00 AM - 09:30 AM (historical bars)
Then live bars continue from 10:00 AM
```

**Key Point**: Historical bars are loaded **before** state transitions to ARMED

---

### Step 5: State Machine Transitions

**State Flow After Restart**:

```
PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED → ...
```

**Restart Recovery Logic**:

1. **If LastState = RANGE_LOCKED**:
   - Pre-hydration loads bars
   - Range recomputed from historical + live bars
   - Breakout levels recomputed
   - State transitions: PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED
   - **Retry stop bracket placement** if not submitted yet

2. **If Entry Already Filled**:
   - `_entryDetected = true` (from execution journal)
   - State transitions to DONE (or manages protective orders)
   - **Won't place new entry orders**

3. **If Committed**:
   - Stream marked DONE
   - No further trading for that stream/day

**Code**: `StreamStateMachine.HandleRangeLockedState()` (line 2503)

---

### Step 6: Retry Failed Orders

**When**: Restart detected, stop brackets not submitted

**What Happens**:
```csharp
if (!_stopBracketsSubmittedAtLock && !_entryDetected)
{
    // Check if already submitted (idempotency)
    if (!_executionJournal.IsIntentSubmitted(intentId, TradingDate, Stream))
    {
        SubmitStopEntryBracketsAtLock(utcNow); // Retry
    }
}
```

**Purpose**: Handles case where stop orders failed before restart

**Code**: `StreamStateMachine.HandleRangeLockedState()` (line 2503-2540)

---

## Active Position Handling

### How Active Positions Are Detected

**On Restart**:
1. Robot checks NinjaTrader account positions
2. For each active position:
   - Finds matching intent from execution journal
   - Restores intent state
   - Verifies protective orders exist

**Code**: `NinjaTraderSimAdapter.GetActiveIntentsForBEMonitoring()`

**What Happens**:
- **Break-Even Monitoring**: Continues monitoring active positions
- **Protective Orders**: Verifies stop/target orders exist
- **New Entries**: Blocked for that instrument if position exists (per blueprint)

---

## Rehydration Details

### BarsRequest Pre-Hydration

**When**: Strategy reaches `Realtime` state

**What Happens**:
1. Strategy calls `RequestHistoricalBarsForPreHydration()`
2. For each execution instrument (MES, MGC, etc.):
   - Requests bars from `range_start` to `min(slot_time, now)`
   - Bars arrive via `OnBarUpdate()` callback
   - Engine feeds bars to stream state machines

**Code**: `RobotSimStrategy.OnStateChange()` (line 237)

**Example Timeline**:
```
Restart at 10:00 AM
Range start: 08:00 AM
Slot time: 09:30 AM

BarsRequest loads:
- 08:00 AM - 09:30 AM (historical bars)
- Then live bars continue from 10:00 AM

Result: Complete bar history from range start to now
```

---

### Range Reconstruction

**Policy**: "Restart = Full Reconstruction"

**What This Means**:
- Range recomputed from **all available bars** (historical + live)
- May differ from uninterrupted operation if restart occurs after slot_time
- Deterministic: Same bars → same range

**Example**:
```
Uninterrupted run:
- Bars: 08:00-09:30 → Range: 2650-2660
- Range locked at 09:30

Restart at 10:00:
- BarsRequest loads: 08:00-09:30 (historical)
- Range recomputed: 2650-2660 (same)
- Live bars continue from 10:00
```

**Trade-off**: Result may differ if restart timing affects bar availability

---

## Duplicate Prevention

### How Duplicates Are Prevented

**1. Execution Journal Check**:
```csharp
if (_executionJournal.IsIntentSubmitted(intentId, TradingDate, Stream))
{
    return; // Already submitted, skip
}
```

**2. Stream Journal Check**:
```csharp
if (_stopBracketsSubmittedAtLock)
{
    return; // Already submitted, skip
}
```

**3. Entry Detection Check**:
```csharp
if (_entryDetected)
{
    return; // Entry already filled, skip
}
```

**Result**: **Won't double-enter trades** even after restart

---

## Will It Take Trades After Restart?

### Yes, If:

1. ✅ **Stream not committed** (`Committed = false`)
2. ✅ **Entry not filled** (`EntryDetected = false`)
3. ✅ **Before market close** (`now < MarketCloseUtc`)
4. ✅ **Range reconstructed** (bars loaded, range computed)
5. ✅ **Breakout occurs** (price breaks range)

### No, If:

1. ❌ **Stream committed** (`Committed = true`)
   - Entry filled OR market close reached
   
2. ❌ **Entry already filled** (`EntryDetected = true`)
   - Execution journal shows fill
   - Stream marked as done

3. ❌ **After market close** (`now >= MarketCloseUtc`)
   - No new entries after 16:00 CT

4. ❌ **Active position exists** (per blueprint)
   - Robot blocks new entries for that instrument
   - Still manages protective orders

---

## Example Scenarios

### Scenario 1: Restart Before Entry

**Timeline**:
- 09:00 AM: Range locked, stop brackets placed
- 09:15 AM: **RESTART**
- 09:20 AM: Restart complete
- 09:30 AM: Breakout occurs → **Entry fills** ✅

**What Happens**:
1. Restart detected (journal exists, not committed)
2. Pre-hydration loads bars 08:00-09:15
3. State transitions: PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED
4. Breakout levels recomputed
5. Stop brackets already placed (`StopBracketsSubmittedAtLock = true`)
6. Breakout detected → Entry order fills ✅

---

### Scenario 2: Restart After Entry Fill

**Timeline**:
- 09:30 AM: Entry fills, protective orders placed
- 10:00 AM: **RESTART**
- 10:05 AM: Restart complete

**What Happens**:
1. Restart detected
2. Execution journal shows entry filled (`EntryFilled = true`)
3. `_entryDetected = true` restored
4. Stream marked as done (or manages protective orders)
5. **No new entries** for that stream/day ✅
6. Break-even monitoring continues for active position ✅

---

### Scenario 3: Restart During Range Building

**Timeline**:
- 08:30 AM: Range building (collecting bars)
- 09:00 AM: **RESTART**
- 09:05 AM: Restart complete

**What Happens**:
1. Restart detected
2. Pre-hydration loads bars 08:00-09:00
3. State transitions: PRE_HYDRATION → ARMED → RANGE_BUILDING
4. Range recomputed from historical + live bars
5. Continues building range until slot_time ✅
6. Then locks range and places stop brackets ✅

---

## Key Files & Locations

### State Persistence

**Stream Journal**:
- **Location**: `logs/robot/journal/{tradingDate}_{stream}.json`
- **Class**: `StreamJournal`
- **Store**: `JournalStore.cs`

**Execution Journal**:
- **Location**: `data/execution_journals/{tradingDate}_{stream}_{intentId}.json`
- **Class**: `ExecutionJournalEntry`
- **Store**: `ExecutionJournal.cs`

### Restart Detection

**Code**: `StreamStateMachine` constructor (line 289-397)
- Detects restart
- Restores state flags
- Recomputes breakout levels

### Pre-Hydration

**Code**: `RobotSimStrategy.RequestHistoricalBarsForPreHydration()`
- Loads historical bars via BarsRequest
- Feeds bars to engine

### Retry Logic

**Code**: `StreamStateMachine.HandleRangeLockedState()` (line 2503)
- Retries stop bracket placement if failed before restart

---

## Safety Guarantees

### ✅ Duplicate Prevention

- **Execution Journal**: Tracks all submissions
- **Stream Journal**: Tracks stream progress
- **Idempotency Checks**: Won't resubmit same intent

### ✅ State Consistency

- **Journal-Based**: State restored from disk
- **Execution Journal**: Fills tracked per intent
- **Fail-Closed**: If journal corrupted, stream stands down

### ✅ Active Position Safety

- **Position Detection**: Checks account positions
- **Protective Orders**: Verifies/manages stop/target orders
- **Break-Even**: Continues monitoring active positions

---

## Summary

**Restart Behavior**: ✅ **SAFE & COMPLETE**

1. **Remembers Everything**:
   - Stream state (committed, entry detected, stop brackets)
   - Execution state (fills, orders, BE modifications)
   - Active positions

2. **Rehydrates Data**:
   - Loads historical bars via BarsRequest
   - Reconstructs range from historical + live bars
   - Recomputes breakout levels

3. **Continues Trading**:
   - Takes new trades if stream not committed
   - Prevents duplicates via journal checks
   - Manages active positions safely

4. **Handles Edge Cases**:
   - Retries failed orders on restart
   - Detects mid-session restarts
   - Prevents double-entry

**Bottom Line**: You can restart anytime - the robot remembers everything and continues safely! 🎯
