# Analyzer Time Calculations - Comprehensive Analysis

**Date**: January 25, 2026  
**Purpose**: Deep dive into how all time-related calculations work in the analyzer

---

## Executive Summary

The analyzer uses multiple time calculations for trade lifecycle management:
- **Entry Time**: When trade is entered (immediate or breakout)
- **Exit Time**: When trade exits (target, stop, or time expiry)
- **Expiry Time**: When trade expires if not exited earlier
- **MFE End Time**: When MFE tracking stops (next day same slot)
- **Peak Time**: Timestamp when maximum favorable movement occurred

All times are calculated in **America/Chicago timezone** and handle special cases like Friday-to-Monday transitions, DST changes, and edge cases.

---

## Time Calculation Overview

### Time Flow Diagram

```
Range End Time (Slot End)
  ↓
Entry Time (immediate or breakout)
  ↓
Trade Active Period
  ├─ T1 Trigger Check (65% of target)
  ├─ Target/Stop Hit Check
  └─ Expiry Check (next day same slot - 1 minute)
  ↓
Exit Time (target/stop/time)
  ↓
MFE Tracking Continues (until next day same slot)
  ↓
Peak Time (maximum favorable movement)
```

---

## 1. Entry Time Calculation

### Location
- **Primary**: `modules/analyzer/logic/entry_logic.py::EntryDetector.detect_entry()`
- **Helper**: `modules/analyzer/logic/entry_logic.py::EntryDetector._handle_dual_immediate_entry()`

### Calculation Logic

#### Immediate Entry (Freeze Close Breaks Out)

**Condition**: `freeze_close >= brk_long` OR `freeze_close <= brk_short`

**Entry Time**:
```python
entry_time = end_ts  # Range end timestamp (slot end time)
```

**Example**:
- Range end: `2025-01-06 07:30:00` (Monday 07:30)
- Freeze close: `4001.75` (breaks above `brk_long = 4001.50`)
- **Entry Time**: `2025-01-06 07:30:00` (immediate at slot end)

**Rationale**: Price is already at breakout level when range closes, so entry occurs immediately at range end time.

#### Post-Range Breakout Entry

**Condition**: No immediate entry, first bar breaks out after range end

**Entry Time**:
```python
entry_time = first_breakout_bar["timestamp"]
```

**Example**:
- Range end: `2025-01-06 07:30:00`
- Breakout bar: `2025-01-06 08:15:00` (first bar where `high >= brk_long`)
- **Entry Time**: `2025-01-06 08:15:00` (timestamp of breakout bar)

**Rationale**: Entry occurs when breakout is confirmed by price action (bar high/low crosses breakout level).

#### Dual Immediate Entry (Both Conditions Met)

**Condition**: `freeze_close >= brk_long` AND `freeze_close <= brk_short` (very small range)

**Entry Time**:
```python
entry_time = end_ts  # Same as immediate entry
```

**Direction Selection**: Choose closer breakout level (distance calculation)
- `long_distance = abs(freeze_close - brk_long)`
- `short_distance = abs(freeze_close - brk_short)`
- If `long_distance <= short_distance` → Long
- Otherwise → Short

**Entry Time**: Always `end_ts` (immediate entry)

---

## 2. Expiry Time Calculation

### Location
- **Primary**: `modules/analyzer/logic/time_logic.py::TimeManager.get_expiry_time()`
- **Alternative**: `modules/analyzer/logic/price_tracking_logic.py::PriceTracker.get_expiry_time()`

### Calculation Logic

#### Regular Day (Monday-Thursday)

**Formula**:
```python
if date.weekday() != 4:  # Not Friday
    expiry_date = date + pd.Timedelta(days=1)  # Next day
    expiry_time = expiry_date.replace(
        hour=hour_part,
        minute=minute_part - 1,  # 1 minute before slot time
        second=59
    )
```

**Example**:
- Trade Date: `2025-01-06` (Monday)
- Slot Time: `07:30`
- **Expiry Time**: `2025-01-07 07:29:59` (Tuesday 07:29:59)

**Rationale**: Trade expires 1 minute before next day's same slot time. This ensures the trade doesn't overlap with the next day's range calculation.

#### Friday Trades

**Formula**:
```python
if date.weekday() == 4:  # Friday
    expiry_date = date + pd.Timedelta(days=ConfigManager.FRIDAY_TO_MONDAY_DAYS)  # Monday (3 days)
    expiry_time = expiry_date.replace(
        hour=hour_part,
        minute=minute_part - 1,  # 1 minute before slot time
        second=59
    )
```

**Example**:
- Trade Date: `2025-01-10` (Friday)
- Slot Time: `07:30`
- **Expiry Date**: `2025-01-13` (Monday, 3 days ahead)
- **Expiry Time**: `2025-01-13 07:29:59` (Monday 07:29:59)

**Rationale**: Friday trades skip the weekend and expire on Monday at the same slot time (minus 1 minute).

#### Edge Case: Slot Time at :00 (e.g., 08:00, 09:00)

**Special Handling**:
```python
if minute == 0:
    expiry_minute = 59  # Previous hour's 59th minute
    expiry_hour = hour - 1 if hour > 0 else 23
```

**Example**:
- Slot Time: `08:00`
- **Expiry Time**: `07:59:59` (previous day's 07:59:59)

**Rationale**: When slot time is exactly on the hour, expiry is 1 minute before (previous hour's 59th minute).

### TimeManager vs PriceTracker Expiry

**Two Implementations Exist**:

1. **TimeManager.get_expiry_time()** (`time_logic.py` lines 85-139):
   - **Used by engine** for initial expiry calculation (line 157 in engine.py)
   - Expires **1 minute before** slot time
   - Example: `07:30` slot → expires at `07:29:59` next day
   - Handles edge case: `:00` slots expire at previous hour's `:59`

2. **PriceTracker.get_expiry_time()** (`price_tracking_logic.py` lines 899-944):
   - **Not currently used** by engine (legacy/alternative implementation)
   - Expires **at** slot time (not 1 minute before)
   - Example: `07:30` slot → expires at `07:30:00` next day
   - Simpler implementation (no 1-minute offset)

**Current Usage**: Engine uses `TimeManager.get_expiry_time()` exclusively (line 157 in engine.py)

**Status**: `PriceTracker.get_expiry_time()` appears to be unused legacy code. Consider removing or documenting if needed for future use.

---

## 3. MFE End Time Calculation

### Location
- **Primary**: `modules/analyzer/logic/price_tracking_logic.py::PriceTracker._get_peak_end_time()`
- **Engine**: `modules/analyzer/breakout_core/engine.py` (lines 96-120)

### Calculation Logic

#### Regular Day (Monday-Thursday)

**Formula**:
```python
if date.weekday() != 4:  # Not Friday
    mfe_end_date = date + pd.Timedelta(days=1)  # Next day
    mfe_end_time = mfe_end_date.replace(
        hour=hour_part,
        minute=minute_part,
        second=0
    )
```

**Example**:
- Trade Date: `2025-01-06` (Monday)
- Slot Time: `07:30`
- **MFE End Time**: `2025-01-07 07:30:00` (Tuesday 07:30:00)

**Rationale**: MFE tracking continues until next day's same slot time to capture overnight movement.

#### Friday Trades

**Formula**:
```python
if date.weekday() == 4:  # Friday
    mfe_end_date = date + pd.Timedelta(days=ConfigManager.FRIDAY_TO_MONDAY_DAYS)  # Monday (3 days)
    mfe_end_time = mfe_end_date.replace(
        hour=hour_part,
        minute=minute_part,
        second=0
    )
```

**Example**:
- Trade Date: `2025-01-10` (Friday)
- Slot Time: `07:30`
- **MFE End Date**: `2025-01-13` (Monday, 3 days ahead)
- **MFE End Time**: `2025-01-13 07:30:00` (Monday 07:30:00)

**Rationale**: Friday trades track MFE through the weekend until Monday's same slot time.

### MFE Tracking Behavior

**Key Points**:
1. **MFE tracking continues even after trade exits** (target/stop hit)
2. **MFE tracking stops if original stop loss is hit** (before MFE end time)
3. **MFE end time is exact slot time** (not 1 minute before like expiry)

**Example Timeline**:
```
Friday 07:30: Entry
Friday 10:00: Target hit (trade exits)
Friday 10:00 - Monday 07:30: MFE tracking continues
Monday 07:30: MFE end time reached
Result: Peak = maximum favorable movement from entry to Monday 07:30
```

---

## 4. Exit Time Calculation

### Location
- **Primary**: `modules/analyzer/logic/price_tracking_logic.py::PriceTracker.execute_trade()`

### Exit Time by Result Type

#### Win (Target Hit)

**Exit Time**:
```python
exit_time = breakout_bar["timestamp"]  # Bar where target was hit
```

**Example**:
- Entry: `2025-01-06 07:30:00`
- Target hit bar: `2025-01-06 09:15:00`
- **Exit Time**: `2025-01-06 09:15:00`

**Rationale**: Exit occurs when target level is reached (determined by intra-bar execution logic).

#### BE (Break Even - T1 Triggered + Stop Hit)

**Exit Time**:
```python
exit_time = stop_hit_bar["timestamp"]  # Bar where break-even stop was hit
```

**Example**:
- Entry: `2025-01-06 07:30:00`
- T1 triggered: `2025-01-06 08:45:00` (65% of target reached)
- Stop hit: `2025-01-06 09:30:00` (break-even stop hit)
- **Exit Time**: `2025-01-06 09:30:00`

**Rationale**: Exit occurs when break-even stop loss is hit after T1 trigger.

#### Loss (Stop Hit Without T1)

**Exit Time**:
```python
exit_time = stop_hit_bar["timestamp"]  # Bar where initial stop was hit
```

**Example**:
- Entry: `2025-01-06 07:30:00`
- Stop hit: `2025-01-06 08:00:00` (initial stop hit, no T1)
- **Exit Time**: `2025-01-06 08:00:00`

**Rationale**: Exit occurs when initial stop loss is hit before T1 trigger.

#### TIME (Time Expired)

**Exit Time Calculation** (Complex - multiple scenarios):

**Scenario 1: Data extends to expiry time**
```python
bars_at_or_before_expiry = after[after["timestamp"] <= expiry_time]
if len(bars_at_or_before_expiry) > 0:
    expiry_bar = bars_at_or_before_expiry.iloc[-1]  # Last bar at or before expiry
    exit_time = expiry_time  # Use expiry_time, not bar timestamp
    exit_price = expiry_bar["close"]
```

**Scenario 2: Data doesn't extend to expiry time**
```python
if expiry_time > last_bar_after["timestamp"]:
    exit_time = expiry_time  # Use expiry_time even if data doesn't extend
    exit_price = last_bar_after["close"]  # Use last available bar's close
```

**Scenario 3: Trade still open (expiry_time in future)**
```python
if expiry_time > current_time:
    exit_time = pd.NaT  # Not a Time - trade still open
    exit_price = last_bar_in_data["close"]  # Current price
```

**Example**:
- Entry: `2025-01-06 07:30:00`
- Expiry Time: `2025-01-07 07:29:59`
- Last bar before expiry: `2025-01-07 07:25:00`
- **Exit Time**: `2025-01-07 07:29:59` (expiry_time, not bar timestamp)
- **Exit Price**: Close price of bar at `2025-01-07 07:25:00`

**Rationale**: Exit time is the expiry_time itself, but exit price comes from the last bar at or before expiry.

---

## 5. Peak Time Calculation

### Location
- **Primary**: `modules/analyzer/logic/price_tracking_logic.py::PriceTracker._calculate_final_mfe()`

### Calculation Logic

**Peak Time**:
```python
peak_time = bar["timestamp"]  # Timestamp of bar with maximum favorable movement
```

**Tracking**:
```python
for bar in mfe_bars:
    if direction == "Long":
        current_favorable = high - entry_price
    else:
        current_favorable = entry_price - low
    
    if current_favorable > max_favorable:
        max_favorable = current_favorable
        peak_time = bar["timestamp"]  # Update peak time
        peak_price = high if direction == "Long" else low
```

**Example**:
- Entry: `2025-01-06 07:30:00` at `4000.00`
- Peak bar: `2025-01-06 10:15:00` with high `4010.50`
- **Peak**: `10.50` points
- **Peak Time**: `2025-01-06 10:15:00`
- **Peak Price**: `4010.50`

**Rationale**: Peak time records when maximum favorable movement occurred, which may be after trade exit.

---

## 6. Timezone Handling

### All Times in America/Chicago

**Principle**: All timestamps are in `America/Chicago` timezone (CST/CDT).

**Data Source**: Translator handles timezone conversion - data arrives in Chicago timezone.

**Defensive Normalization**: Analyzer includes defensive code to ensure timezone consistency:

```python
# Normalize to Chicago timezone
chicago_tz = pytz.timezone("America/Chicago")
if first_data_tz is not None:
    if str(first_data_tz) != "America/Chicago":
        df["timestamp"] = df["timestamp"].dt.tz_convert(chicago_tz)
    elif first_data_tz is not chicago_tz:
        # Same timezone but different object instance - convert for consistency
        df["timestamp"] = df["timestamp"].dt.tz_convert(chicago_tz)
```

### DST (Daylight Saving Time) Handling

**DST Transitions**:
- **Spring Forward**: Second Sunday in March (lose 1 hour)
- **Fall Back**: First Sunday in November (gain 1 hour)

**Handling**: Pandas/pytz automatically handles DST transitions when using `America/Chicago` timezone.

**Example**:
- `2025-03-09 02:00:00 CST` → `2025-03-09 03:00:00 CDT` (spring forward)
- `2025-11-02 02:00:00 CDT` → `2025-11-02 01:00:00 CST` (fall back)

**Verification**: Tests include DST transition handling (`test_dst_transition_handling`).

---

## 7. Friday-to-Monday Handling

### Constant Used

**Location**: `modules/analyzer/logic/config_logic.py`

```python
FRIDAY_TO_MONDAY_DAYS = 3  # Friday to Monday (skip Saturday, Sunday)
```

### Usage Locations

1. **Expiry Time** (`time_logic.py` line 109):
   ```python
   if date.weekday() == 4:  # Friday
       days_ahead = ConfigManager.FRIDAY_TO_MONDAY_DAYS  # 3 days
   ```

2. **MFE End Time** (`price_tracking_logic.py` line 974):
   ```python
   if date.weekday() == 4:  # Friday
       days_ahead = self.config_manager.FRIDAY_TO_MONDAY_DAYS  # 3 days
   ```

3. **Engine MFE Calculation** (`engine.py` line 98):
   ```python
   if R.date.weekday() == 4:  # Friday
       mfe_end_date = R.date + pd.Timedelta(days=ConfigManager.FRIDAY_TO_MONDAY_DAYS)
   ```

4. **TimeManager Next Slot** (`time_logic.py` line 73):
   ```python
   if date.weekday() == 4:  # Friday
       days_ahead = ConfigManager.FRIDAY_TO_MONDAY_DAYS
   ```

### Calculation Example

**Friday Trade**:
- Trade Date: `2025-01-10` (Friday)
- Slot Time: `07:30`
- **Expiry Date**: `2025-01-13` (Monday) = Friday + 3 days
- **Expiry Time**: `2025-01-13 07:29:59` (Monday 07:29:59)
- **MFE End Time**: `2025-01-13 07:30:00` (Monday 07:30:00)

**Rationale**: Friday trades skip weekend (Saturday + Sunday) and continue to Monday.

---

## 8. Time Comparison Logic

### Entry Time vs Range End Time

**Range End Time**: Slot end timestamp (e.g., `07:30:00`)

**Entry Time**:
- **Immediate**: `entry_time == end_ts` (same as range end)
- **Breakout**: `entry_time > end_ts` (after range end)

**Comparison**:
```python
if entry_time > market_close:  # After 16:00
    return NoTrade  # No entry allowed after market close
```

### Expiry Time vs MFE End Time

**Key Difference**:
- **Expiry Time**: `slot_time - 1 minute` (e.g., `07:29:59`)
- **MFE End Time**: `slot_time` (e.g., `07:30:00`)

**Rationale**:
- Expiry is 1 minute before to avoid overlap with next day's range
- MFE end is exact slot time to capture full period

### Time Expiry Check

**Location**: `price_tracking_logic.py` line 487

```python
if bar["timestamp"] >= expiry_time:
    # Trade expired - exit with TIME result
    exit_time = expiry_time  # Use expiry_time, not bar timestamp
    exit_price = last_bar_at_or_before_expiry["close"]
```

**Important**: Uses `>=` comparison, so bar at exact expiry_time triggers expiry.

---

## 9. Edge Cases and Special Scenarios

### Edge Case 1: Trade Enters After Market Close

**Scenario**: Breakout happens after 16:00 (market close)

**Handling**:
```python
market_close = end_ts.replace(hour=16, minute=0)
if entry_time > market_close:
    return NoTrade  # No entry after market close
```

**Result**: NoTrade (entry not allowed after market close)

### Edge Case 2: Data Doesn't Extend to Expiry Time

**Scenario**: Expiry time is `Monday 07:29:59` but data only goes to `Monday 07:25:00`

**Handling**:
```python
bars_at_or_before_expiry = after[after["timestamp"] <= expiry_time]
if len(bars_at_or_before_expiry) > 0:
    expiry_bar = bars_at_or_before_expiry.iloc[-1]
    exit_price = expiry_bar["close"]
    exit_time = expiry_time  # Still use expiry_time
```

**Result**: Exit time = expiry_time, exit price = last available bar's close

### Edge Case 3: Trade Still Open (Expiry Time in Future)

**Scenario**: Current time is `Monday 07:20:00`, expiry is `Monday 07:29:59`

**Handling**:
```python
if expiry_time > current_time:
    exit_time = pd.NaT  # Trade still open
    exit_price = last_bar["close"]  # Current price
```

**Result**: ExitTime = empty/NaT, Result = "TIME" (but trade still open)

### Edge Case 4: MFE Data Gap

**Scenario**: MFE end time is `Monday 07:30:00` but data only goes to `Monday 07:25:00`

**Handling**:
```python
if data_end_time < mfe_end_time:
    mfe_bars = df[(df["timestamp"] >= entry_time)].copy()  # Use all available data
    # Log warning to stderr
    print(f"MFE: Data ends {time_diff:.1f} min before expected", file=sys.stderr)
```

**Result**: MFE calculated using available data, warning logged

### Edge Case 5: Break-Even Stop Hit at Expiry

**Scenario**: T1 triggered, break-even stop hit exactly at expiry time

**Handling**:
```python
# Check all bars at or before expiry for break-even stop
bars_at_or_before_expiry = after[after["timestamp"] <= expiry_time]
for bar in bars_at_or_before_expiry:
    if break_even_stop_hit:
        exit_time = be_stop_hit_time
        exit_reason = "BE"  # Not "TIME"
        break
```

**Result**: BE (not TIME) if break-even stop hit before expiry

---

## 10. Time Calculation Flow Examples

### Example 1: Regular Day Trade (Monday)

**Timeline**:
```
Monday 07:30:00 - Range end (freeze_close = 4001.75, brk_long = 4001.50)
Monday 07:30:00 - Entry (immediate Long entry)
Monday 08:45:00 - T1 triggered (65% of target reached)
Monday 09:15:00 - Target hit
Monday 09:15:00 - Exit (Win)
Monday 09:15:00 - Tuesday 07:30:00 - MFE tracking continues
Tuesday 07:30:00 - MFE end time reached
```

**Times**:
- Entry Time: `Monday 07:30:00`
- Exit Time: `Monday 09:15:00`
- Expiry Time: `Tuesday 07:29:59` (not reached)
- MFE End Time: `Tuesday 07:30:00`
- Peak Time: `Monday 10:00:00` (maximum favorable after exit)

### Example 2: Friday Trade with Time Expiry

**Timeline**:
```
Friday 07:30:00 - Range end
Friday 08:00:00 - Entry (breakout Long entry)
Friday 10:00:00 - T1 triggered
Friday 16:00:00 - Market close (trade still open)
Monday 07:29:59 - Expiry time reached
Monday 07:29:59 - Exit (TIME)
Monday 07:30:00 - MFE end time reached
```

**Times**:
- Entry Time: `Friday 08:00:00`
- Exit Time: `Monday 07:29:59` (expiry_time)
- Expiry Time: `Monday 07:29:59`
- MFE End Time: `Monday 07:30:00`
- Peak Time: `Friday 15:30:00` (maximum favorable before expiry)

### Example 3: Break-Even Stop Hit Before Expiry

**Timeline**:
```
Monday 07:30:00 - Entry
Monday 08:45:00 - T1 triggered (stop moved to break-even)
Monday 09:30:00 - Break-even stop hit
Monday 09:30:00 - Exit (BE)
Monday 09:30:00 - Tuesday 07:30:00 - MFE tracking continues
Tuesday 07:30:00 - MFE end time reached
```

**Times**:
- Entry Time: `Monday 07:30:00`
- Exit Time: `Monday 09:30:00` (break-even stop hit)
- Expiry Time: `Tuesday 07:29:59` (not reached)
- MFE End Time: `Tuesday 07:30:00`
- Peak Time: `Monday 10:00:00` (maximum favorable after exit)

---

## 11. Time Calculation Issues & Considerations

### Issue 1: Two Different Expiry Calculations

**Problem**: 
- `TimeManager.get_expiry_time()` expires **1 minute before** slot time
- `PriceTracker.get_expiry_time()` expires **at** slot time

**Current Usage**: Engine uses `TimeManager.get_expiry_time()` (correct)

**Recommendation**: Remove or update `PriceTracker.get_expiry_time()` to match, or document why two exist.

### Issue 2: Expiry Time Edge Case (:00 slots)

**Current Logic** (`time_logic.py` lines 119-124):
```python
if minute > 0:
    expiry_minute = minute - 1
else:
    expiry_minute = 59
    hour = hour - 1 if hour > 0 else 23
```

**Example**: `08:00` slot → expires at `07:59:59` previous day

**Potential Issue**: If hour is 0 (midnight), wraps to 23 (previous day 23:59:59)

**Verification Needed**: Test midnight slot times (00:00) if they exist.

### Issue 3: MFE End Time vs Expiry Time Mismatch

**Difference**:
- Expiry: `slot_time - 1 minute` (e.g., `07:29:59`)
- MFE End: `slot_time` (e.g., `07:30:00`)

**Impact**: MFE tracking continues 1 minute after trade expiry

**Rationale**: Intentional - MFE captures full period including the minute before next slot

**Status**: Working as designed

### Issue 4: Time Expiry Exit Price Selection

**Current Logic**:
```python
bars_at_or_before_expiry = after[after["timestamp"] <= expiry_time]
expiry_bar = bars_at_or_before_expiry.iloc[-1]  # Last bar at or before expiry
exit_price = expiry_bar["close"]
```

**Potential Issue**: If no bars exist at or before expiry_time, falls back to current bar (may be after expiry)

**Mitigation**: Code includes fallback logic, but edge case exists

**Status**: Handled with fallback, but could be more explicit

### Issue 5: Open Trades (Expiry Time in Future)

**Current Logic**:
```python
if expiry_time > current_time:
    exit_time = pd.NaT  # Trade still open
    exit_price = last_bar["close"]  # Current price
```

**Impact**: Open trades have empty ExitTime but Result = "TIME"

**Rationale**: Distinguishes expired trades from open trades

**Status**: Working as designed

---

## 12. Time Calculation Constants

### ConfigManager Constants

**Location**: `modules/analyzer/logic/config_logic.py`

```python
FRIDAY_TO_MONDAY_DAYS = 3  # Days from Friday to Monday
```

**Usage**: All Friday-to-Monday calculations use this constant

**Rationale**: Centralized constant makes it easy to change if needed (e.g., for holidays)

### Market Close Time

**Location**: `modules/analyzer/logic/config_logic.py`

```python
market_close_time = "16:00"  # Market close in HH:MM format (Chicago time)
```

**Usage**: Entry detection filters breakouts after market close

**Rationale**: No entries allowed after market close (16:00 Chicago time)

---

## 13. Time Calculation Validation

### Entry Time Validation

**Checks**:
1. Entry time >= range end time (for breakouts)
2. Entry time == range end time (for immediate entries)
3. Entry time <= market close (16:00)

**Validation**: Handled in `EntryDetector.detect_entry()`

### Expiry Time Validation

**Checks**:
1. Expiry time > entry time
2. Expiry time is in Chicago timezone
3. Expiry time accounts for Friday-to-Monday transition

**Validation**: Handled in `TimeManager.get_expiry_time()`

### MFE End Time Validation

**Checks**:
1. MFE end time >= entry time
2. MFE end time is in Chicago timezone
3. MFE end time accounts for Friday-to-Monday transition

**Validation**: Handled in `PriceTracker._get_peak_end_time()`

---

## 14. Time Calculation Testing

### Test Coverage

**Entry Logic Tests** (`test_entry_logic.py`):
- ✅ Immediate entry timing
- ✅ Breakout entry timing
- ✅ Dual immediate entry timing
- ✅ Timezone-aware timestamps
- ✅ DST transition handling

**Integration Tests** (`test_analyzer_integration.py`):
- ✅ Timezone handling
- ✅ Basic time calculations

### Test Gaps

**Potential Gaps**:
1. Expiry time edge cases (:00 slots, midnight)
2. MFE end time with data gaps
3. Break-even stop hit at exact expiry time
4. Open trades (expiry in future)
5. Multiple timezone object instances

---

## 15. Recommendations

### High Priority

1. **Verify Expiry Time Logic**: Ensure `TimeManager.get_expiry_time()` is used consistently (not `PriceTracker.get_expiry_time()`)

2. **Test Midnight Slots**: Verify expiry logic works correctly for `00:00` slot times (if they exist)

3. **Document Expiry vs MFE End**: Clearly document why expiry is 1 minute before but MFE end is exact time

### Medium Priority

1. **Consolidate Expiry Calculations**: Consider removing `PriceTracker.get_expiry_time()` if not used

2. **Add Time Validation**: Add explicit validation for time relationships (entry < expiry < MFE end)

3. **Improve Edge Case Handling**: Make exit price selection more explicit for TIME exits

### Low Priority

1. **Add Time Calculation Tests**: Test edge cases (midnight, data gaps, etc.)

2. **Document Timezone Assumptions**: Document that all times are Chicago timezone

3. **Add Time Calculation Examples**: Add more examples to documentation

---

## 16. Summary

### Key Time Calculations

1. **Entry Time**: Range end (immediate) or breakout bar timestamp
2. **Expiry Time**: Next day same slot - 1 minute (Monday if Friday)
3. **MFE End Time**: Next day same slot (Monday if Friday)
4. **Exit Time**: Target/stop hit bar timestamp, or expiry_time for TIME exits
5. **Peak Time**: Bar timestamp with maximum favorable movement

### Timezone Handling

- All times in **America/Chicago** timezone
- DST transitions handled automatically by pandas/pytz
- Defensive normalization ensures consistency

### Friday-to-Monday Handling

- Uses `ConfigManager.FRIDAY_TO_MONDAY_DAYS = 3`
- Applied consistently to expiry and MFE end times
- Skips weekend (Saturday + Sunday)

### Edge Cases

- Market close filtering (no entries after 16:00)
- Data gaps (uses available data, logs warnings)
- Open trades (ExitTime = NaT)
- Break-even stop at expiry (checks before expiry)

---

**Document Generated**: January 25, 2026  
**Analysis Scope**: All time-related calculations in analyzer  
**Files Analyzed**:
- `logic/time_logic.py`
- `logic/price_tracking_logic.py`
- `logic/entry_logic.py`
- `breakout_core/engine.py`
- `logic/config_logic.py`
