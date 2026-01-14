# Current State: What's Happening When Strategies Are Enabled

## State Flow When Strategies Are Enabled

### 1. **Stream Creation (From Timetable)**
When strategies are enabled from the timetable:
- `RobotEngine` creates `StreamStateMachine` instances for each enabled stream
- Calls `Arm()` on each stream
- Streams start in **`PRE_HYDRATION`** state

### 2. **PRE_HYDRATION State** (Initial State)
**What's happening:**
- Loading historical bars from CSV files (`data/raw/{instrument}/1m/{date}.csv`)
- Reading bars from `range_start` to `min(now, slot_time)`
- Inserting bars into internal buffer
- Setting `_preHydrationComplete = true` when done

**Duration:** Usually < 1 second (file read + parse)

**Next:** Transitions to `ARMED` state automatically

### 3. **ARMED State** (Waiting for Range Start)
**What's happening:**
- Stream is ready but waiting
- On each `Tick()` call (every 1 second), checks:
  ```csharp
  if (utcNow >= RangeStartUtc)
  ```
- **If false:** Continues waiting in ARMED
- **If true:** Transitions to `RANGE_BUILDING`

**Duration:** Depends on current time vs range start time
- If current time < range start: Waits until range start time
- If current time >= range start: Immediately transitions to RANGE_BUILDING

**Example:**
- S1 session: range_start = 02:00 Chicago
- If now = 01:30 Chicago → Waits 30 minutes
- If now = 02:30 Chicago → Immediately transitions

### 4. **RANGE_BUILDING State** (Active Range Computation)
**What's happening:**
- Accepting live bars via `OnBar()`
- Updating `RangeHigh` and `RangeLow` incrementally
- Tracking gaps for tolerance checks
- Computing range from all bars in buffer
- Waiting for `slot_time` to lock the range

**Duration:** From range start until slot time
- Example: S1 slot 07:30 → Range building from 02:00 to 07:30

**Next:** Transitions to `RANGE_LOCKED` at slot time

### 5. **RANGE_LOCKED State** (Trading Window)
**What's happening:**
- Range is finalized (no more updates)
- Breakout levels computed
- Watching for breakout signals
- Can place trades if breakout occurs
- Waiting for market close or trade execution

**Duration:** From slot time until market close or trade

**Next:** Transitions to `DONE` when committed

## Current State Determination

To know what state you're in **right now**, check:

### For Each Stream:

1. **Check Pre-Hydration:**
   - If `_preHydrationComplete == false` → **PRE_HYDRATION**
   - If `_preHydrationComplete == true` → Continue to step 2

2. **Check Range Start Time:**
   - Current UTC time vs `RangeStartUtc`
   - If `utcNow < RangeStartUtc` → **ARMED** (waiting)
   - If `utcNow >= RangeStartUtc` → Continue to step 3

3. **Check Slot Time:**
   - Current UTC time vs `SlotTimeUtc`
   - If `utcNow < SlotTimeUtc` → **RANGE_BUILDING** (active)
   - If `utcNow >= SlotTimeUtc` → Continue to step 4

4. **Check Committed:**
   - If `Committed == true` → **DONE**
   - If `Committed == false` → **RANGE_LOCKED** (trading window)

## Typical Timeline (S1 Session, Slot 07:30)

```
00:00 - Strategy enabled
       State: PRE_HYDRATION
       Action: Loading CSV bars

00:01 - Pre-hydration complete
       State: ARMED
       Action: Waiting for 02:00

02:00 - Range start time reached
       State: RANGE_BUILDING
       Action: Accepting bars, computing range

07:30 - Slot time reached
       State: RANGE_LOCKED
       Action: Range finalized, watching for breakout

16:00 - Market close (or trade executed)
       State: DONE
       Action: Committed, no more activity
```

## What to Check in Logs

Look for these events to understand current state:

1. **`STREAM_ARMED`** - Stream was enabled
2. **`PRE_HYDRATION_START`** - Started loading CSV
3. **`PRE_HYDRATION_COMPLETE`** - CSV loading done
4. **`PRE_HYDRATION_COMPLETE`** transition → **`ARMED`** state
5. **`RANGE_WINDOW_STARTED`** - Range building began
6. **`RANGE_LOCKED`** - Range finalized at slot time
7. **`HEARTBEAT`** - Periodic status (every 7 minutes in RANGE_BUILDING)

## Quick State Check

**If you want to know the current state:**

1. Check logs for most recent state transition
2. Compare current time to:
   - Range start time (from spec: S1=02:00, S2=08:00)
   - Slot time (from timetable)
3. Check if `_preHydrationComplete` flag is set

**Most likely current states:**
- **Before range start:** `ARMED` (waiting)
- **During range window:** `RANGE_BUILDING` (active)
- **After slot time:** `RANGE_LOCKED` (trading)
- **After market close/trade:** `DONE` (committed)
