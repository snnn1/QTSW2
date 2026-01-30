# Mid-Session Start Behavior - What Happens When You Start the Robot During a Range

**Date**: 2026-01-30  
**Question**: What happens if you turn on the robot in the middle of a range? Will it wait until the end or try to capture bars as they come?

---

## Answer: It Captures Bars As They Come (No Waiting)

**Short Answer**: The robot **does NOT wait**. It immediately starts capturing bars and reconstructing the range from available historical + live bars.

---

## Detailed Behavior

### Scenario 1: Starting During Range Building (After Range Start, Before Slot Time)

**Example**: Range starts at 02:00 CT, slot time is 07:30 CT, you start at 05:00 CT

**What Happens**:

1. **Mid-Session Restart Detected**
   - Robot detects you're starting mid-session (after `range_start_time`)
   - Logs `MID_SESSION_RESTART_DETECTED` event
   - Policy: **"RESTART_FULL_RECONSTRUCTION"**

2. **BarsRequest for Historical Bars**
   - Requests historical bars from `range_start_time` (02:00) to `current_time` (05:00)
   - Loads all bars that occurred between range start and now
   - Feeds bars directly to stream buffers via `LoadPreHydrationBars()`

3. **Immediate Range Building**
   - Stream transitions: `PRE_HYDRATION` → `ARMED` → `RANGE_BUILDING`
   - Range is computed from:
     - **Historical bars** (02:00 - 05:00) from BarsRequest
     - **Live bars** (05:00+) as they arrive
   - Range high/low are computed from all available bars

4. **Continues Until Slot Time**
   - Continues capturing live bars until `slot_time` (07:30)
   - Updates range high/low as new bars arrive
   - Locks range at `slot_time` (07:30)

**Result**: Range is reconstructed from historical + live bars. It does NOT wait - it actively builds the range.

---

### Scenario 2: Starting After Slot Time (Range Already Locked)

**Example**: Range starts at 02:00 CT, slot time is 07:30 CT, you start at 09:00 CT

**What Happens**:

1. **Mid-Session Restart Detected**
   - Robot detects you're starting after `slot_time`
   - Logs `MID_SESSION_RESTART_DETECTED` event
   - Policy: **"RESTART_FULL_RECONSTRUCTION"**

2. **BarsRequest for Historical Bars**
   - Requests historical bars from `range_start_time` (02:00) to `current_time` (09:00)
   - **Note**: Range building window is already closed (slot_time passed)
   - BarsRequest loads bars up to current time for visibility

3. **Range Reconstruction**
   - Range is computed from historical bars (02:00 - 07:30)
   - Range locks immediately (slot_time already passed)
   - Stream transitions: `PRE_HYDRATION` → `ARMED` → `RANGE_BUILDING` → `RANGE_LOCKED`

4. **Trading Begins**
   - Range is locked with historical data
   - Robot can trade based on reconstructed range
   - **Warning**: Result may differ from uninterrupted operation

**Result**: Range is reconstructed from historical bars only. Range locks immediately since slot_time has passed.

---

## Key Code References

### Mid-Session Restart Detection

**Location**: `StreamStateMachine.cs` (lines 314-363)

```csharp
// Mid-session restart if:
// 1. Journal exists (stream was initialized before)
// 2. Journal is not committed (stream was active)
// 3. Current time is after range start (session has begun)
isMidSessionRestart = !existing.Committed && nowChicago >= RangeStartChicagoTime;

if (isMidSessionRestart)
{
    // RESTART BEHAVIOR POLICY: "Restart = Full Reconstruction"
    // When strategy restarts mid-session:
    // - BarsRequest loads historical bars from range_start to min(slot_time, now)
    // - Range is recomputed from all available bars (historical + live)
    // - This may differ from uninterrupted operation if restart occurs after slot_time
    // - Result: Deterministic reconstruction, but may differ from continuous run
}
```

### BarsRequest Time Range for Restart

**Location**: `RobotSimStrategy.cs` (lines 640-688)

```csharp
// RESTART-AWARE: On restart (after slot_time), request bars up to now
// Use the earlier of: slotTimeChicago or now (to avoid future bars)
// CRITICAL: On restart after slot_time, request bars up to current time for visibility
var endTimeChicago = (nowChicagoDate == tradingDate && nowChicago < slotTimeChicagoTime)
    ? nowChicago.ToString("HH:mm")  // Before slot_time: request up to now
    : (nowChicagoDate == tradingDate && nowChicago >= slotTimeChicagoTime)
        ? nowChicago.ToString("HH:mm")  // RESTART: After slot_time, request up to now
        : slotTimeChicago;  // Future date: use slot_time
```

---

## Policy: "RESTART_FULL_RECONSTRUCTION"

### What This Means:

**Full Reconstruction**:
- Robot reconstructs the range from all available bars (historical + live)
- Does NOT wait for the next range window
- Does NOT skip the current range
- Actively builds range from available data

### Trade-offs:

**Pros**:
- ✅ Allows recovery from crashes/restarts
- ✅ Can trade immediately (doesn't wait)
- ✅ Deterministic reconstruction

**Cons**:
- ⚠️ Result may differ from uninterrupted operation
- ⚠️ If restart occurs after slot_time, range is based on historical bars only
- ⚠️ Same day may produce different results depending on restart timing

### Alternative Policy (Not Implemented):

**"Restart Invalidates Stream"**:
- Would mark stream as invalidated if restart occurs after slot_time
- Would prevent trading for that stream on that day
- More conservative but less flexible

**Current Choice**: Full reconstruction allows recovery from crashes/restarts, with the trade-off that results may differ.

---

## Timeline Examples

### Example 1: Start at 05:00 (During Range Building)

```
02:00 CT - Range Start Time
05:00 CT - Robot Starts ← YOU START HERE
         ↓
         Robot Detects Mid-Session Restart
         ↓
         BarsRequest: Load bars from 02:00 to 05:00
         ↓
         Stream: PRE_HYDRATION → ARMED → RANGE_BUILDING
         ↓
         Range Building: Uses historical (02:00-05:00) + live (05:00+)
         ↓
07:30 CT - Slot Time
         ↓
         Range Locks
         ↓
         Trading Begins
```

**Result**: Range built from 02:00-07:30 (historical + live). Robot does NOT wait.

---

### Example 2: Start at 09:00 (After Slot Time)

```
02:00 CT - Range Start Time
07:30 CT - Slot Time (Range Would Have Locked)
09:00 CT - Robot Starts ← YOU START HERE
         ↓
         Robot Detects Mid-Session Restart
         ↓
         BarsRequest: Load bars from 02:00 to 09:00
         ↓
         Stream: PRE_HYDRATION → ARMED → RANGE_BUILDING → RANGE_LOCKED
         ↓
         Range Building: Uses historical bars only (02:00-07:30)
         ↓
         Range Locks Immediately (slot_time already passed)
         ↓
         Trading Begins
```

**Result**: Range built from 02:00-07:30 (historical only). Range locks immediately.

---

## Important Notes

### 1. Range Building Window

- **Range building window**: `range_start_time` to `slot_time`
- If you start **during** this window: Range builds from historical + live bars
- If you start **after** this window: Range builds from historical bars only

### 2. BarsRequest Behavior

- **Before slot_time**: Requests bars from `range_start` to `current_time`
- **After slot_time**: Requests bars from `range_start` to `current_time` (for visibility)
- **Range computation**: Only uses bars from `range_start` to `slot_time` (window boundaries)

### 3. Deterministic Reconstruction

- Range is computed deterministically from available bars
- Same bars = same range (deterministic)
- Different bars = different range (may differ from uninterrupted operation)

### 4. No Waiting Behavior

- Robot **never waits** for the next range window
- It immediately starts building from available data
- This allows recovery from crashes/restarts

---

## Summary

### Question: Does it wait or capture bars as they come?

**Answer**: **It captures bars as they come** - no waiting.

### Behavior:

1. ✅ **Detects mid-session restart** if starting after `range_start_time`
2. ✅ **Requests historical bars** from `range_start` to `current_time`
3. ✅ **Immediately starts building range** from historical + live bars
4. ✅ **Does NOT wait** for the next range window
5. ✅ **Locks range at slot_time** (or immediately if slot_time already passed)

### Policy:

**"RESTART_FULL_RECONSTRUCTION"** - Full reconstruction from available bars, no waiting.

---

**Status**: ✅ **COMPLETE EXPLANATION**
