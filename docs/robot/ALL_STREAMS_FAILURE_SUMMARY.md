# All Streams Failure Summary - January 20, 2026

## Critical Finding: SYSTEM-WIDE FAILURE

**ALL 10 streams failed to compute ranges today.** This is not isolated to ES1/GC1 - it's a complete system failure affecting every stream.

## Stream Status

### All Streams Stuck in PRE_HYDRATION
- **ES1** (S1, 08:00) - PRE_HYDRATION, not committed
- **ES2** (S2, 09:30) - PRE_HYDRATION, not committed
- **GC1** (S1, 08:00) - PRE_HYDRATION, not committed
- **GC2** (S2, 09:30) - PRE_HYDRATION, not committed
- **CL2** (S2, 10:30) - PRE_HYDRATION, not committed
- **NQ1** (S1, 09:00) - PRE_HYDRATION, not committed
- **NQ2** (S2, 10:00) - PRE_HYDRATION, not committed
- **NG1** (S1, 07:30) - PRE_HYDRATION, not committed
- **NG2** (S2, 09:30) - PRE_HYDRATION, not committed
- **RTY2** (S2, 09:30) - PRE_HYDRATION, not committed

**Result:** Zero ranges computed. Zero journal files created.

## BarsRequest Status Per Instrument

### ES (ES1, ES2)
- **BarsRequest Events:** 8 events found
- **Status:** BarsRequest SKIPPED (reason: "Starting before range_start_time")
- **Bars Loaded:** Some bars loaded but then skipped
- **Issue:** BarsRequest executed but was skipped due to timing

### GC (GC1, GC2)
- **BarsRequest Events:** 0 events found
- **Status:** **NO BarsRequest executed at all**
- **Issue:** Strategy instances not running for GC

### CL (CL2)
- **BarsRequest Events:** 4 events found
- **Status:** BarsRequest SKIPPED (reason: "Starting before range_start_time")
- **Bars Loaded:** Some bars loaded but then skipped
- **Issue:** BarsRequest executed but was skipped due to timing

### NQ (NQ1, NQ2)
- **BarsRequest Events:** 0 events found
- **Status:** **NO BarsRequest executed at all**
- **Issue:** Strategy instances not running for NQ

### NG (NG1, NG2)
- **BarsRequest Events:** 8 events found
- **Status:** BarsRequest SKIPPED (reason: "Starting before range_start_time")
- **Bars Loaded:** Some bars loaded but then skipped
- **Issue:** BarsRequest executed but was skipped due to timing

### RTY (RTY2)
- **BarsRequest Events:** 3 events found
- **Status:** BarsRequest SKIPPED (reason: "Starting before range_start_time")
- **Issue:** BarsRequest executed but was skipped due to timing

## Bar Acceptance Status

**ALL instruments: 100% rejection rate**

- **ES:** 0 accepted, 37,052 rejected
- **GC:** 0 accepted, 6,386 rejected
- **CL:** 0 accepted, 10,502 rejected
- **NQ:** 0 accepted, 4,351 rejected
- **NG:** 0 accepted, 37,052 rejected
- **RTY:** 0 accepted, 8,228 rejected

**Total:** 0 bars accepted, 103,571 bars rejected

**Reason:** All bars rejected as partial (< 1 minute old)

## State Transitions

**NO state transitions occurred for ANY stream:**
- No `HYDRATION_SUMMARY` events
- No `PRE_HYDRATION_TIMEOUT_NO_BARS` events
- No `STATE_TRANSITION` events to ARMED
- No `STATE_TRANSITION` events to RANGE_BUILDING
- No `RANGE_LOCK_ASSERT` events

**All streams stuck in PRE_HYDRATION forever.**

## Range Computation

**NO ranges computed:**
- No `RANGE_COMPUTE_START` events
- No `RANGE_LOCK_ASSERT` events
- No journal files created
- No range high/low values computed

## Root Cause Analysis

### Primary Issue: BarsRequest Timing Logic

**Problem:** BarsRequest is being SKIPPED for most instruments because:
- Strategy started at 11:08 UTC (05:08 Chicago)
- Range start times are later (e.g., 06:30, 07:30, 08:00 Chicago)
- Code at `RobotSimStrategy.cs` line 223 checks: `if (nowChicago < rangeStartChicagoTime)`
- If true, BarsRequest is skipped with reason: "Starting before range_start_time"

**Impact:**
- Historical bars not loaded
- Streams start with empty buffers
- No pre-hydration data available

### Secondary Issue: Missing Strategy Instances

**Problem:** Some instruments (GC, NQ) have NO BarsRequest events at all:
- No `BARSREQUEST_INITIALIZATION` events
- No `BARSREQUEST_SKIPPED` events
- No BarsRequest execution attempted

**Impact:**
- Strategy instances not running for GC and NQ
- No historical bars requested
- No bars loaded

### Tertiary Issue: 100% Bar Rejection

**Problem:** All live bars rejected as partial (< 1 minute old):
- `RobotEngine.OnBar()` line 485: `MIN_BAR_AGE_MINUTES = 1.0`
- All bars < 1 minute old are rejected
- This is correct behavior for live bars, but prevents any bars from reaching streams

**Impact:**
- No bars ever added to stream buffers
- `barCount = 0` for all streams
- Streams cannot compute ranges without bars

### Quaternary Issue: Transition Logic Not Working

**Problem:** Transition condition should work with time threshold:
- Code at `StreamStateMachine.cs` line 563: `if (barCount > 0 || nowChicago >= RangeStartChicagoTime)`
- When range start time passes, condition should be TRUE
- But transition never occurs

**Impact:**
- Streams stuck in PRE_HYDRATION
- Never transition to ARMED → RANGE_BUILDING → RANGE_LOCKED
- No ranges computed

## Why This Is Worse Than Expected

1. **Not just ES1/GC1** - ALL 10 streams failed
2. **Not just missing BarsRequest** - Some BarsRequest executed but were skipped
3. **Not just bar rejection** - Even if bars were accepted, streams wouldn't transition
4. **System-wide failure** - Multiple cascading issues

## Immediate Actions Needed

1. **Fix BarsRequest Timing Logic**
   - Don't skip BarsRequest when starting before range start time
   - Load historical bars even if strategy starts early
   - Historical bars are needed for pre-hydration regardless of timing

2. **Verify Strategy Instances**
   - Ensure strategy instances are running for ALL instruments (GC, NQ)
   - Check NinjaTrader configuration
   - Verify all instruments are enabled

3. **Fix Transition Logic**
   - Deploy diagnostic logging (`PRE_HYDRATION_CONDITION_CHECK`)
   - Verify `Tick()` is being called on streams
   - Verify `HandlePreHydrationState()` is being called
   - Debug why transition condition isn't working

4. **Review Bar Acceptance Strategy**
   - Consider accepting historical bars even if < 1 minute old
   - Or adjust age check for historical bars
   - Add fallback mechanism when no historical bars available

## Expected vs Actual

### Expected Behavior
- Strategy starts → BarsRequest loads historical bars → Bars accepted → Streams transition → Ranges computed

### Actual Behavior
- Strategy starts → BarsRequest skipped/missing → No bars loaded → All bars rejected → Streams stuck → No ranges computed

## Related Documents

- `docs/robot/TODAY_FAILURE_SUMMARY.md` - Detailed analysis of today's failures
- `docs/robot/08_00_RANGE_FAILURE_ANALYSIS.md` - Technical deep dive
- `modules/robot/core/StreamStateMachine.cs` - Pre-hydration transition logic
- `modules/robot/ninjatrader/RobotSimStrategy.cs` - BarsRequest timing logic
