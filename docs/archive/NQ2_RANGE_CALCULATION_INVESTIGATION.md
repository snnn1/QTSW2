# NQ2 Range Calculation Investigation

## Problem Summary

NQ2's range was calculated incorrectly as **[26170.25, 26197]** but should be **[25536, 26197]**.

## Root Cause Analysis

### Key Finding: Only 3 Bars Used Instead of 180

**Evidence from logs:**
- Range window: `08:00:00` to `11:00:00` Chicago time (180 minutes)
- Expected bar count: **180 bars** (1-minute bars)
- Actual bar count used: **3 bars**
- Range calculated: `[26170.25, 26197]`
- Correct range should be: `[25536, 26197]`

### Timeline of Events

1. **17:00:37 UTC (11:00:37 Chicago)** - Stream initialized, transitioned to ARMED
2. **17:00:38 UTC** - PRE_HYDRATION completed with **0 bars**
   - `PRE_HYDRATION_COMPLETE_SIM` logged with `bar_count: 0`
   - `HYDRATION_SNAPSHOT` showed:
     - `barsrequest_raw_count: 0`
     - `barsrequest_accepted_count: 0`
     - `historical_bar_count: 0`
     - `live_bar_count: 0`
3. **17:00:43 UTC (11:00:43 Chicago)** - Range calculation executed
   - Only **3 bars** were in the buffer
   - Bars were: `08:00:00`, `08:01:00`, `08:02:00`
   - All bars marked as `Source=CSV` (not from NinjaTrader BarsRequest)

### Critical Discovery: Bars Are CSV-Sourced, Not Historical

**Bar source analysis:**
- All bars accepted during range calculation show `Source=CSV`
- This indicates bars came from CSV file-based pre-hydration, NOT from NinjaTrader BarsRequest
- BarsRequest returned **0 bars** (`barsrequest_raw_count: 0`)

### Why Only 3 Bars?

**Hypothesis 1: CSV file only contains 3 bars**
- CSV pre-hydration may have only loaded 3 bars from file
- Need to check CSV file contents for NQ2 on 2026-01-29

**Hypothesis 2: BarsRequest failed silently**
- BarsRequest was called but returned 0 bars
- No error was logged, suggesting silent failure or no data available
- System fell back to CSV pre-hydration, which only had 3 bars

**Hypothesis 3: Bars filtered out incorrectly**
- Bars may have been loaded but filtered out by date/time window logic
- Need to check bar filtering logs

### Pre-Hydration Behavior

**SIM Mode Pre-Hydration Logic:**
- In SIM mode, pre-hydration should:
  1. Request historical bars from NinjaTrader using `BarsRequest` API
  2. Fall back to CSV file-based pre-hydration if BarsRequest fails
  3. Transition to ARMED when bars are loaded OR when past range start time

**What Actually Happened:**
- BarsRequest was called but returned 0 bars
- CSV fallback only provided 3 bars
- Pre-hydration completed with 0 bars (forced transition after timeout)
- Live bars started arriving at 08:00 Chicago time
- When range was calculated at 11:00, only 3 live bars (08:00, 08:01, 08:02) were available

### Range Calculation Window

**Expected behavior:**
- Range window: `[08:00:00, 11:00:00)` Chicago time (exclusive end)
- Should use all bars with OPEN times in this window
- Expected: 180 bars (one per minute)

**Actual behavior:**
- Only 3 bars were in the buffer
- All 3 bars were from the beginning of the window (08:00-08:02)
- Range calculation used these 3 bars:
  - High: 26197 ✅ (correct)
  - Low: 26170.25 ❌ (should be 25536)

### Why Restoration Shows Wrong Range

**Restoration is working correctly ✅**
- Restoration correctly restored the range that was originally locked: `[26170.25, 26197]`
- The problem is the **original calculation**, not restoration
- Restoration cannot fix a calculation that was wrong from the start

## Questions to Investigate

1. **Why did BarsRequest return 0 bars?**
   - Was BarsRequest called correctly?
   - Did NinjaTrader have historical data for NQ2 on 2026-01-29?
   - Was the time range correct?

2. **Why did CSV pre-hydration only have 3 bars?**
   - Check CSV file contents for NQ2
   - Verify CSV file has bars for 08:00-11:00 window
   - Check if CSV loading logic filtered out bars incorrectly

3. **Why weren't more bars loaded before range calculation?**
   - Range was calculated at 11:00 Chicago time
   - Live bars should have been arriving since 08:00
   - Why only 3 bars accumulated over 3 hours?

4. **Bar filtering logic:**
   - Check `RANGE_COMPUTE_BAR_FILTERING` logs
   - Verify time window filtering is correct
   - Check if bars were filtered by trading date

## Root Cause: BarsRequest Not Called on Restart

### Critical Finding

**BarsRequest is only called during `OnStateChange(State.DataLoaded)`, which happens ONCE when the strategy first loads.**

**When the stream restarted at 17:00:37:**
- `MID_SESSION_RESTART_DETECTED` was logged
- Stream was reinitialized
- **BUT:** `OnStateChange(State.DataLoaded)` was NOT called again
- **Result:** BarsRequest was never called for the restart
- **Result:** No historical bars were loaded
- **Result:** Range calculation used only 3 live bars that had arrived

### Evidence

1. **BarsRequest is called in background thread during DataLoaded:**
   ```csharp
   ThreadPool.QueueUserWorkItem(_ =>
   {
       RequestHistoricalBarsForPreHydration(instrument);
   });
   ```

2. **BarsRequest was called earlier (16:54:15):**
   - Logs show BarsRequest bars being processed at 16:54:15
   - These bars were rejected as duplicates (LIVE bars already existed)

3. **On restart (17:00:37):**
   - `MID_SESSION_RESTART_DETECTED` logged
   - Stream reinitialized
   - **No `DATALOADED_INITIALIZATION_COMPLETE` event**
   - **No `BARSREQUEST_QUEUED` event**
   - **No BarsRequest callback events**

4. **HYDRATION_SNAPSHOT shows 0 bars:**
   - `barsrequest_raw_count: 0`
   - `barsrequest_accepted_count: 0`
   - `live_bar_count: 0`

### Why BarsRequest Returns 0 Bars on Restart

**The problem is NOT that BarsRequest returned 0 bars - it's that BarsRequest was NEVER CALLED on restart.**

**BarsRequest is only called:**
- Once during `OnStateChange(State.DataLoaded)` when strategy first loads
- In a background thread (`ThreadPool.QueueUserWorkItem`)
- Before streams are fully initialized

**On restart:**
- Stream is reinitialized internally
- `OnStateChange` is NOT called again
- BarsRequest is NOT triggered
- No historical bars are loaded
- System relies on live bars only

### Solution Required

**BarsRequest must be called on restart, not just on initial load.**

**Options:**
1. **Call BarsRequest when stream restarts:**
   - Detect restart in `StreamStateMachine` constructor
   - Trigger BarsRequest from engine when restart detected
   - Ensure BarsRequest is called even if `OnStateChange` is not called

2. **Request bars up to "now" on restart:**
   - Current code requests bars up to slot time
   - On restart, should request bars up to current time
   - This ensures all historical bars are loaded

3. **Add restart detection to BarsRequest logic:**
   - Check if stream was restarted
   - If restarted, request bars from range start to current time
   - Don't rely on `OnStateChange` being called

## Next Steps

1. **Fix BarsRequest to be called on restart:**
   - Add restart detection to `RequestHistoricalBarsForPreHydration`
   - Call BarsRequest when `MID_SESSION_RESTART_DETECTED` is logged
   - Ensure BarsRequest is called even if `OnStateChange` is not called

2. **Verify BarsRequest time range on restart:**
   - On restart, request bars from range start to current time
   - Not just up to slot time
   - This ensures all historical bars are loaded

3. **Add diagnostic logging:**
   - Log when BarsRequest is called
   - Log when BarsRequest is skipped
   - Log BarsRequest parameters (time range, instrument)
   - Log BarsRequest results (bar count, errors)

## Status

- ✅ **Restoration is working correctly** - it restored what was actually locked
- ❌ **Original calculation was wrong** - only 3 bars used instead of 180
- 🔍 **Root cause:** Insufficient bars in buffer at calculation time
- 🔍 **Investigation needed:** Why weren't bars loaded/filtered correctly?

## Recommendations

1. **Add diagnostic logging:**
   - Log CSV file path and bar count when loading
   - Log BarsRequest parameters and results
   - Log bar filtering decisions with reasons

2. **Add validation:**
   - Warn if range calculation uses < 50% of expected bars
   - Fail range calculation if bar count is suspiciously low
   - Add assertion for minimum bar count

3. **Improve pre-hydration:**
   - Ensure BarsRequest is called with correct time range
   - Verify CSV fallback has sufficient data
   - Add timeout handling for bar loading
