# Forced Flatten Behavior - Market Close

## Question
After forced flatten (5 minutes before market close), will streams automatically re-enter at market open with the same orders?

## Answer: **NO**

### What Happens at Forced Flatten

1. **Forced Flatten Occurs** (e.g., 15:55 CT, 5 minutes before market close):
   - All positions are closed (market exit)
   - All working orders are canceled
   - Streams are **committed** (marked as DONE for that trading day)

2. **Stream State After Flatten**:
   - Stream state: `DONE`
   - Journal: `Committed = true`
   - Stream is **ended** for that trading day

### What Happens at Market Open (Next Trading Day)

**Each trading day is independent:**

1. **New Trading Date Detected**:
   - System detects new trading date (e.g., next day)
   - Streams are **reinitialized** for the new trading date
   - Each stream starts fresh: `PRE_HYDRATION` → `ARMED` → `RANGE_BUILDING` → etc.

2. **Timetable Determines Behavior**:
   - System loads timetable for the new trading date
   - Timetable determines:
     - Which streams are **enabled**
     - What **slot_time** each stream uses
     - Which **session** (S1, S2, etc.)

3. **New Orders, Not Old Orders**:
   - Streams compute **new ranges** from scratch
   - **New breakout levels** are calculated
   - **New entry orders** are placed (if enabled in timetable)
   - **NOT** the same orders from the previous day

### Key Points

- **No automatic re-entry**: Streams do NOT automatically re-enter with the same orders
- **Each day is independent**: Trading days are separate - no carryover of orders or positions
- **Timetable-driven**: Whether a stream trades on the next day depends on the timetable
- **Fresh start**: Each day starts from `PRE_HYDRATION` state

### Example Timeline

**Day 1 (2026-02-02)**:
- 15:55 CT: Forced flatten → All positions closed, orders canceled
- Stream committed: `DONE` state, `Committed = true`
- End of day

**Day 2 (2026-02-03)**:
- Market open: New trading date detected
- Stream reinitialized: `PRE_HYDRATION` state
- Timetable loaded: Checks if stream is enabled for 2026-02-03
- If enabled: Stream proceeds through normal lifecycle (ARMED → RANGE_BUILDING → etc.)
- If disabled: Stream remains `IDLE` or `DONE`

### Blueprint Reference

From `NinjaTrader Robot Blueprint (Execution Layer).txt`:

> **Commit point occurs at earliest of:**
> - Entry filled
> - Stream marked NO_TRADE by market close cutoff (market_close_time)
> - **Forced flatten ends the stream**
>
> **After commit: ignore updates for that stream/day (do not resurrect or re-arm)**

This confirms that:
1. Forced flatten commits/ends the stream for that day
2. After commit, streams do NOT resurrect or re-arm
3. Each trading day starts fresh

### Summary

**No, you will NOT get back in at market open with the same orders.**

- Forced flatten ends the stream for that trading day
- The next trading day starts fresh based on the timetable
- If the stream is enabled in the timetable for the next day, it will trade again, but with:
  - New ranges (computed from new day's data)
  - New breakout levels
  - New entry orders
  - New stop/target prices (based on new range)
