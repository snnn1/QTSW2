# Hybrid Range Initialization - Blocking Code Audit

## Goal
When strategy is enabled mid-range (e.g., 05:00 during 02:00-07:30 range), it must:
1. Compute range immediately from historical bars (range_start → now)
2. Continue updating range from live bars via OnBarUpdate
3. Lock range at slot_time and place orders
4. Work identically in DRYRUN and SIM

## Current Problem
Strategy waits until slot_time to compute range, preventing mid-range initialization.

---

## A. Blocks on Historical Bar Usage

### Block A1: Bar Provider Availability Check
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 369  
**Condition**: `if (!_rangeComputed && !_hydrationAttempted && _barProvider != null)`

**What it blocks**:
- Historical hydration only occurs if `_barProvider != null`
- In SIM mode, `_barProvider` is typically null (only DRYRUN has it)
- Prevents SIM mode from initializing range from history

**Code**:
```csharp
// Hydrate from historical bars if starting late (allow even after slot_time)
// Attempt hydration if bar provider exists and we haven't hydrated yet
if (!_rangeComputed && !_hydrationAttempted && _barProvider != null)
{
    _hydrationAttempted = true;
    TryHydrateFromHistory(utcNow);
}
```

**Impact**: SIM mode cannot hydrate from history, must wait for live bars.

---

### Block A2: Post-Slot Hydration Provider Check
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 423  
**Condition**: `if (barCountBeforeHydration == 0 && _barProvider != null && !_hydrationAttempted)`

**What it blocks**:
- Only attempts hydration at slot_time if `_barProvider != null`
- Same restriction as A1 - SIM mode blocked

**Code**:
```csharp
if (barCountBeforeHydration == 0 && _barProvider != null && !_hydrationAttempted)
{
    // Attempt hydration post-slot_time
    _hydrationAttempted = true;
    TryHydrateFromHistory(utcNow);
}
```

**Impact**: SIM mode cannot hydrate even at slot_time if no live bars received.

---

### Block A3: ComputeRangeRetrospectively Provider Dependency
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 1011-1030  
**Condition**: `if (_barProvider != null)` else uses `_barBuffer`

**What it blocks**:
- Range computation uses provider if available, otherwise uses buffer
- If provider is null (SIM mode), relies entirely on buffer
- Buffer may be empty if strategy started mid-range

**Code**:
```csharp
if (_barProvider != null)
{
    // DRYRUN mode: Query bars from provider
    bars.AddRange(_barProvider.GetBars(Instrument, RangeStartUtc, SlotTimeUtc));
}
else
{
    // Live mode: Use buffered bars
    lock (_barBufferLock)
    {
        bars.AddRange(_barBuffer);
    }
}
```

**Impact**: SIM mode cannot query historical bars for range computation, only uses buffered live bars.

---

### Block A4: RobotEngine Bar Provider Initialization
**File**: `modules/robot/core/RobotEngine.cs`  
**Line**: 35, 689  
**Condition**: `_barProvider` is optional, typically null in SIM mode

**What it blocks**:
- `_barProvider` is only set if provided to constructor
- SIM mode typically doesn't provide bar provider
- DRYRUN mode gets provider, SIM doesn't

**Code**:
```csharp
private IBarProvider? _barProvider; // Optional: for historical bar hydration (SIM/DRYRUN modes)
// ...
var newSm = new StreamStateMachine(..., barProvider: _barProvider, ...);
```

**Impact**: SIM mode has no access to historical bars at all.

---

## B. Late-Start Trading Restrictions

### Block B1: Late-Start Detection
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 340-364  
**Condition**: `if (utcNow >= SlotTimeUtc && !_rangeComputed && !_lateStartAfterSlot)` and `if (barCount == 0)`

**What it blocks**:
- Detects if stream started after slot_time with zero bars
- Sets `_lateStartAfterSlot = true` flag
- This flag later blocks trading (see B2)

**Code**:
```csharp
if (utcNow >= SlotTimeUtc && !_rangeComputed && !_lateStartAfterSlot)
{
    lock (_barBufferLock)
    {
        var barCount = _barBuffer.Count;
        // If slot_time has passed and we're computing now, mark as late start
        if (barCount == 0)
        {
            _lateStartAfterSlot = true;
            // Log STREAM_LATE_START_DETECTED
        }
    }
}
```

**Impact**: If strategy starts after slot_time with no live bars, marks as late start and blocks trading.

---

### Block B2: Late-Start Trading Block
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 627-647  
**Condition**: `if (_lateStartAfterSlot)`

**What it blocks**:
- If `_lateStartAfterSlot` is true, commits stream as NO_TRADE
- Range is computed but trading is skipped
- Prevents hybrid initialization from trading

**Code**:
```csharp
// ENFORCE: Compute allowed, trade forbidden when started after slot_time
if (_lateStartAfterSlot)
{
    // Log that trading is skipped due to late start, but range was computed
    // Commit as NO_TRADE but with range computed (for audit/parity)
    Commit(utcNow, "NO_TRADE_LATE_START_RANGE_COMPUTED", "STREAM_LATE_START_SKIP_TRADING");
    break;
}
```

**Impact**: Even if range is computed from history, trading is blocked if late-start flag is set.

---

## C. NO_DATA / Zero-Bars Logic

### Block C1: Zero Bars at Slot Time
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 456-491  
**Condition**: `if (finalBarCount == 0)`

**What it blocks**:
- After hydration attempt, if buffer is still empty, commits NO_TRADE
- Sends emergency alert
- Prevents trading even if historical bars could be queried

**Code**:
```csharp
// Only commit NO_TRADE if BOTH buffer is empty AND no historical bars available
if (finalBarCount == 0)
{
    // Emit "NO DATA → NO TRADE" high-priority alert
    // Persist incident record
    // Commit stream as NO_TRADE_RANGE_DATA_MISSING
    Commit(utcNow, "NO_TRADE_RANGE_DATA_MISSING", "RANGE_DATA_UNAVAILABLE_AFTER_HISTORY");
    break;
}
```

**Impact**: If no bars in buffer after hydration attempt, trading is blocked. Doesn't check if provider can query bars directly.

---

### Block C2: Range Computation Failure
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 519-535  
**Condition**: `if (!rangeResult.Success)`

**What it blocks**:
- If `ComputeRangeRetrospectively` returns `Success = false`, commits NO_TRADE
- Happens even if partial data exists

**Code**:
```csharp
if (!rangeResult.Success)
{
    // Range data missing - mark stream as NO_TRADE for the day
    // Commit stream as NO_TRADE_RANGE_DATA_MISSING
    Commit(utcNow, "NO_TRADE_RANGE_DATA_MISSING", "RANGE_DATA_MISSING");
    break;
}
```

**Impact**: If range computation fails (e.g., no bars in window), trading is blocked. Doesn't allow partial range computation.

---

## D. OnBarUpdate Misuse / Range Building Logic

### Block D1: Range Computation Only at Slot Time
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 416  
**Condition**: `if (utcNow >= SlotTimeUtc && !_rangeComputed)`

**What it blocks**:
- Range computation only occurs when slot_time is reached
- No immediate computation when entering RANGE_BUILDING state
- Prevents mid-range initialization

**Code**:
```csharp
if (utcNow >= SlotTimeUtc && !_rangeComputed)
{
    // Compute range retrospectively
    var rangeResult = ComputeRangeRetrospectively(utcNow);
    // ...
}
```

**Impact**: Strategy must wait until slot_time to compute range, cannot initialize mid-range.

---

### Block D2: OnBar Only Buffers, Doesn't Compute Incrementally
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 707-827  
**Condition**: `if (State == StreamState.RANGE_BUILDING)` and `if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)`

**What it blocks**:
- OnBar only buffers bars, doesn't compute range incrementally
- Range is computed retrospectively at slot_time only
- No immediate range computation when entering RANGE_BUILDING

**Code**:
```csharp
public void OnBar(DateTimeOffset barUtc, decimal high, decimal low, decimal close, DateTimeOffset utcNow)
{
    if (State == StreamState.RANGE_BUILDING)
    {
        // Only buffer bars that fall within [range_start, slot_time)
        if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
        {
            lock (_barBufferLock)
            {
                _barBuffer.Add(new Bar(...));
            }
        }
    }
    // No range computation here - only at slot_time
}
```

**Impact**: Range is never computed until slot_time, preventing mid-range initialization.

---

### Block D3: TryHydrateFromHistory Early Return
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Line**: 1530  
**Condition**: `if (hydrationCheckChicago < RangeStartChicagoTime) return;`

**What it blocks**:
- If current time is before range_start, hydration returns early
- Prevents hydration before range_start time
- But for hybrid model, we want to hydrate immediately when entering RANGE_BUILDING

**Code**:
```csharp
// Check if we're starting late (current time > range start)
// If before range start, wait for normal bar flow
if (hydrationCheckChicago < RangeStartChicagoTime)
    return; // Not late yet, wait for normal bar flow
```

**Impact**: Hydration only happens if "late" (after range_start). For hybrid model, we want immediate hydration when entering RANGE_BUILDING.

---

## Summary of Blocking Code

| Block | File | Line | What It Blocks | Impact |
|-------|------|------|----------------|--------|
| **A1** | StreamStateMachine.cs | 369 | Historical hydration requires `_barProvider != null` | SIM mode cannot hydrate |
| **A2** | StreamStateMachine.cs | 423 | Post-slot hydration requires `_barProvider != null` | SIM mode cannot hydrate at slot_time |
| **A3** | StreamStateMachine.cs | 1011 | Range computation uses provider if available, else buffer | SIM mode relies on buffer only |
| **A4** | RobotEngine.cs | 35, 689 | `_barProvider` is null in SIM mode | SIM mode has no historical access |
| **B1** | StreamStateMachine.cs | 340-364 | Late-start detection sets flag | Blocks trading if started after slot_time |
| **B2** | StreamStateMachine.cs | 627-647 | Late-start flag blocks trading | Prevents trading even with computed range |
| **C1** | StreamStateMachine.cs | 456-491 | Zero bars at slot_time commits NO_TRADE | Blocks trading if no bars after hydration |
| **C2** | StreamStateMachine.cs | 519-535 | Range computation failure commits NO_TRADE | Blocks trading if range computation fails |
| **D1** | StreamStateMachine.cs | 416 | Range computation only at slot_time | Prevents mid-range initialization |
| **D2** | StreamStateMachine.cs | 707-827 | OnBar only buffers, doesn't compute | No incremental range computation |
| **D3** | StreamStateMachine.cs | 1530 | Hydration blocked before range_start | Prevents early hydration |

---

## Proposed Minimal Removals

### Removal 1: Remove Bar Provider Dependency
**Files**: `StreamStateMachine.cs` lines 369, 423, 1011-1030

**Change**: Always attempt to query historical bars from provider if available, regardless of mode. If provider is null, use buffer. But don't block hydration or computation based on provider availability.

**Specific**:
- Remove `&& _barProvider != null` checks from hydration conditions
- Modify `ComputeRangeRetrospectively` to always try provider first, then buffer
- Allow SIM mode to have bar provider (set in RobotEngine)

---

### Removal 2: Remove Late-Start Trading Block
**Files**: `StreamStateMachine.cs` lines 340-364, 627-647

**Change**: Remove late-start detection and trading block entirely.

**Specific**:
- Remove `_lateStartAfterSlot` flag and all related logic
- Remove late-start detection code (lines 340-364)
- Remove late-start trading block (lines 627-647)

---

### Removal 3: Remove NO_DATA Blocking Logic
**Files**: `StreamStateMachine.cs` lines 456-491, 519-535

**Change**: Allow range computation with partial data. Don't commit NO_TRADE if bars exist (even if partial).

**Specific**:
- Remove zero-bars check that commits NO_TRADE (line 456-491)
- Modify range computation failure to allow partial ranges
- Only commit NO_TRADE if truly no data available (not just empty buffer)

---

### Removal 4: Enable Immediate Range Computation
**Files**: `StreamStateMachine.cs` lines 331-336, 416

**Change**: Compute range immediately when entering RANGE_BUILDING state, not just at slot_time.

**Specific**:
- Add range computation in ARMED → RANGE_BUILDING transition
- Compute range from history immediately when entering RANGE_BUILDING
- Continue updating range from live bars until slot_time
- At slot_time, lock range and place orders

---

### Removal 5: Remove Early Return from TryHydrateFromHistory
**Files**: `StreamStateMachine.cs` line 1530

**Change**: Allow hydration even before range_start time.

**Specific**:
- Remove `if (hydrationCheckChicago < RangeStartChicagoTime) return;` check
- Allow hydration immediately when entering RANGE_BUILDING state

---

## Acceptance Criteria Check

**Q1**: "If the strategy is enabled at 05:00 during a 02:00–07:30 range, does it immediately compute the range from history?"

**Current Answer**: ❌ No - waits until slot_time (07:30)

**After Removals**: ✅ Yes - computes immediately when entering RANGE_BUILDING

---

**Q2**: "Do live bars then update the same range until 07:30?"

**Current Answer**: ❌ No - range is computed retrospectively at slot_time only

**After Removals**: ✅ Yes - range computed incrementally from live bars

---

**Q3**: "Is there any code path that still blocks this?"

**Current Answer**: ✅ Yes - multiple blocks (A1-A4, B1-B2, C1-C2, D1-D3)

**After Removals**: ❌ No - all blocks removed

---

## Implementation Notes

1. **Bar Provider in SIM Mode**: Need to ensure SIM mode can access historical bars (may require setting `_barProvider` in RobotEngine for SIM mode)

2. **Incremental Range Computation**: Current code computes retrospectively. For hybrid model, may need incremental updates (or compute retrospectively each tick until slot_time)

3. **Range Lock Timing**: Range should be locked at slot_time, not before. Live bars should update range until slot_time.

4. **No State Machine Changes**: Keep existing states (IDLE → ARMED → RANGE_BUILDING → RANGE_LOCKED → DONE). Just change when range computation occurs.
