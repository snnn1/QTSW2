# Late-Start Hydration Validation Guide

## Overview

This guide provides validation steps to verify correctness of the late-start hydration implementation, focusing on three critical areas:

1. Bar ordering guarantee
2. Exactly-one-minute boundary test
3. Late start with partial post-slot bars

## 1. Bar Ordering Guarantee

### Implementation Status: ✅ VERIFIED

**Current Implementation:**
- `CheckMissedBreakout()` explicitly sorts bars: `barsSnapshot.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc))`
- Bars are sorted by UTC timestamp before scanning
- Earliest breakout wins (first chronological breakout detected)

**Why This Matters:**
- If bars arrive out of order, earliest breakout must be detected first
- Prevents misclassifying breakout direction
- Ensures deterministic behavior

**Validation Steps:**
1. Check logs for `MISSED_BREAKOUT_SCAN_BAR` events (if diagnostic logs enabled)
2. Verify bars are processed in chronological order
3. Confirm earliest breakout is detected (not a later one)

**Code Location:**
- `modules/robot/core/StreamStateMachine.cs:783` - Explicit sort before scanning

## 2. Exactly-One-Minute Boundary Test

### Edge Cases to Validate

#### Scenario 1: Breakout at slot_time + 1s
**Expected:** Detected → NO_TRADE_LATE_START_MISSED_BREAKOUT

**Test Setup:**
- Start strategy 2 minutes after slot_time
- Range: High=5000, Low=4990
- Bar at slot_time + 1s: High=5001 (exceeds range high)

**Validation:**
- Check `LATE_START_MISSED_BREAKOUT` event is emitted
- Verify `breakout_time_chicago` is slot_time + 1s
- Verify `breakout_direction` is "LONG"
- Verify stream commits as `NO_TRADE_LATE_START_MISSED_BREAKOUT`

**Code Verification:**
- `CheckMissedBreakout()` uses `barChicagoTime >= slotChicago` (inclusive)
- Breakout detection uses `bar.High > rangeHigh` (strict inequality)

#### Scenario 2: Touch at exactly slot_time
**Expected:** Ignored (range window is exclusive)

**Test Setup:**
- Range computed from bars in `[range_start, slot_time)` window
- Bar at exactly slot_time: High=5000 (equals range high, not exceeds)

**Validation:**
- Range computation uses `< SlotTimeChicagoTime` (exclusive)
- Bar at slot_time is NOT included in range computation
- If bar at slot_time touches range boundary, it's NOT a breakout (strict inequality)

**Code Verification:**
- `ComputeRangeRetrospectively()` uses `barChicagoTime < endTimeChicagoActual` (exclusive)
- Breakout detection requires `bar.High > rangeHigh` (strict, not >=)

#### Scenario 3: Breakout at slot_time + 1 bar (1 minute later)
**Expected:** Detected

**Test Setup:**
- Start strategy 5 minutes after slot_time
- Range: High=5000, Low=4990
- Bar at slot_time + 1 minute: High=5001

**Validation:**
- Breakout detected
- `breakout_time_chicago` is slot_time + 1 minute
- Stream commits as `NO_TRADE_LATE_START_MISSED_BREAKOUT`

#### Scenario 4: Price equals range high/low (no breach)
**Expected:** NOT a breakout

**Test Setup:**
- Range: High=5000, Low=4990
- Bar: High=5000 (equals range high, not exceeds)
- Bar: Low=4990 (equals range low, not exceeds)

**Validation:**
- No breakout detected
- Stream proceeds normally
- Can trade when actual breakout occurs

**Code Verification:**
- Uses strict inequalities: `bar.High > rangeHigh` and `bar.Low < rangeLow`
- Equals is NOT a breakout

### Diagnostic Logging

Enable diagnostic logs to see boundary validation:
- `HYDRATION_BOUNDARY_CONTRACT` - Shows range build window and scan window
- `MISSED_BREAKOUT_SCAN_BAR` - Shows each bar checked in scan window (if diagnostic logs enabled)
- `LATE_START_MISSED_BREAKOUT` - Shows breakout details when detected

## 3. Late Start with Partial Post-Slot Bars

### Scenario: Most Important "Real Money" Test

**Test Setup:**
1. Start robot 2-3 minutes after slot_time
2. Ensure bars are present from range_start through slot_time (full range)
3. Ensure bars are present from slot_time to now (partial post-slot)
4. Verify NO breakout occurred in post-slot window

**Expected Behavior:**
1. ✅ Range locks successfully from `[range_start, slot_time)` bars
2. ✅ Stream transitions to ARMED/RANGE_LOCKED normally
3. ✅ Trade triggers when breakout happens later (after strategy start)

**Validation Steps:**

#### Step 1: Verify Range Formation
Check logs for:
- `HYDRATION_SUMMARY` event
- `reconstructed_range_high` and `reconstructed_range_low` are populated
- `late_start = true`
- `missed_breakout = false`

#### Step 2: Verify Normal Transition
Check logs for:
- `PRE_HYDRATION_TO_ARMED_TRANSITION` event
- Stream transitions to ARMED state
- No `NO_TRADE_LATE_START_MISSED_BREAKOUT` event

#### Step 3: Verify Range Lock
Check logs for:
- `RANGE_LOCKED` event (when slot_time passes)
- Range values match reconstructed range
- Breakout levels computed correctly

#### Step 4: Verify Trade Trigger
When breakout occurs after strategy start:
- `BREAKOUT_DETECTED` event
- Entry order submitted
- Trade executes normally

**Code Path Verification:**
```
PRE_HYDRATION
  → Range reconstructed from [range_start, slot_time) bars
  → CheckMissedBreakout() returns false (no breakout in [slot_time, now])
  → Transition to ARMED
  → Wait for slot_time
  → Transition to RANGE_LOCKED
  → Wait for breakout
  → Trade when breakout occurs
```

## Validation Checklist

### Bar Ordering
- [ ] Bars are sorted before missed-breakout scan
- [ ] Earliest breakout is detected first
- [ ] No out-of-order processing

### Boundary Tests
- [ ] Breakout at slot_time + 1s is detected
- [ ] Touch at exactly slot_time is ignored (not in range)
- [ ] Breakout at slot_time + 1 bar is detected
- [ ] Price equals range boundary is NOT a breakout
- [ ] Range build window is exclusive at slot_time
- [ ] Missed-breakout scan window is inclusive at slot_time

### Late Start Happy Path
- [ ] Range forms correctly from [range_start, slot_time) bars
- [ ] Stream transitions normally when no breakout occurred
- [ ] Trade triggers when breakout happens after start
- [ ] All existing behavior preserved

## Log Events to Monitor

### Success Indicators
- `HYDRATION_SUMMARY` with `late_start=true`, `missed_breakout=false`
- `PRE_HYDRATION_TO_ARMED_TRANSITION`
- `RANGE_LOCKED` (when slot_time passes)
- `BREAKOUT_DETECTED` (when breakout occurs)

### Failure Indicators
- `NO_TRADE_LATE_START_MISSED_BREAKOUT` (breakout already occurred)
- `HYDRATION_RANGE_COMPUTE_ERROR` (range computation failed, but non-blocking)

### Diagnostic Events
- `HYDRATION_BOUNDARY_CONTRACT` - Shows window boundaries
- `MISSED_BREAKOUT_SCAN_BAR` - Shows each bar checked (if diagnostic logs enabled)

## Testing Commands

### Enable Diagnostic Logs
Set `_enableDiagnosticLogs = true` in StreamStateMachine to see detailed boundary validation logs.

### Check Logs for Boundary Semantics
```bash
# Check for boundary contract logs
grep "HYDRATION_BOUNDARY_CONTRACT" logs/robot/robot_*.jsonl

# Check for missed-breakout scan details
grep "MISSED_BREAKOUT_SCAN_BAR" logs/robot/robot_*.jsonl

# Check for late-start handling
grep "LATE_START_MISSED_BREAKOUT\|HYDRATION_SUMMARY" logs/robot/robot_*.jsonl | grep "late_start"
```

## Implementation Notes

### Code Locations

**Bar Ordering:**
- `StreamStateMachine.cs:783` - Explicit sort before scanning

**Boundary Semantics:**
- Range build: `ComputeRangeRetrospectively()` uses `< endTimeChicagoActual` (exclusive)
- Missed-breakout scan: `CheckMissedBreakout()` uses `>= slotChicago` (inclusive)
- Breakout detection: Uses strict inequalities `>` and `<` (not `>=` or `<=`)

**Late Start Handling:**
- `HandlePreHydrationState()` - SIM mode branch (line ~1195)
- `HandlePreHydrationState()` - DRYRUN mode branch (line ~1285)
- `CheckMissedBreakout()` - Helper method (line ~770)

## Summary

The implementation correctly handles:
1. ✅ Bar ordering (explicit sort before scan)
2. ✅ Boundary semantics (exclusive range build, inclusive scan, strict inequalities)
3. ✅ Late start happy path (range forms, no breakout detected, proceeds normally)

All edge cases are handled correctly, and the implementation preserves existing behavior while adding safe late-start handling.
