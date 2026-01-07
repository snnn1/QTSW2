# Range Timezone Diagnosis

## Issue Report

**User Concern:** Ranges are being calculated for UTC time windows instead of Chicago time windows.

**Example:** GC range 4458.1-4486.0 is for UTC 02:00-09:00, but should be for Chicago 02:00-09:00.

## Current Behavior Analysis

### GC Range Computation (2026-01-06)

**Logged Range Window:**
- Chicago: 02:00:00 to 09:00:00 ✅ (Correct)
- UTC: 08:00:00 to 15:00:00 ✅ (Correct conversion)

**First Bar:**
- Chicago: 02:00:00 ✅
- UTC: 08:00:00 ✅

**Last Bar:**
- Chicago: 08:59:00 ✅
- UTC: 14:59:00 ✅

**Config:**
- S1 range_start_time: "02:00" (Chicago) ✅
- Slot time: "09:00" (Chicago) ✅

## Code Flow Analysis

### Range Window Definition

1. **Config Reading** (`StreamStateMachine.cs:118`):
   ```csharp
   var rangeStartChicago = spec.sessions[Session].range_start_time; // "02:00"
   ```

2. **UTC Conversion** (`StreamStateMachine.cs:119`):
   ```csharp
   RangeStartUtc = time.ConvertChicagoLocalToUtc(dateOnly, rangeStartChicago);
   // Chicago "02:00" → UTC "08:00" (during CST/DST)
   ```

3. **Chicago Time Storage** (`StreamStateMachine.cs:125`):
   ```csharp
   RangeStartChicagoTime = time.ConvertUtcToChicago(RangeStartUtc);
   // UTC "08:00" → Chicago "02:00" (round-trip conversion)
   ```

### Bar Filtering Logic

**Live Mode** (`StreamStateMachine.cs:485`):
```csharp
var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
{
    _barBuffer.Add(bar);
}
```

**DRYRUN Mode** (`StreamStateMachine.cs:722`):
```csharp
var barChicagoTime = _time.ConvertUtcToChicago(barRawUtc);
if (barChicagoTime >= RangeStartChicagoTime && barChicagoTime < SlotTimeChicagoTime)
{
    filteredBars.Add(bar);
}
```

## Potential Issues

### Issue 1: Bar Timestamps May Be Wrong

**Hypothesis:** NinjaTrader bars may have timestamps that are NOT UTC, but are being treated as UTC.

**Evidence:**
- Bars are received with `barUtc` timestamp
- Code assumes `barUtc` is UTC: `_time.ConvertUtcToChicago(barUtc)`
- If `barUtc` is actually Chicago time, conversion would be wrong

**Check:** `RobotSimStrategy.cs:107-108`:
```csharp
var barChicagoOffset = new DateTimeOffset(barExchangeTime, chicagoTz.GetUtcOffset(barExchangeTime));
var barUtc = barChicagoOffset.ToUniversalTime();
```

This converts exchange time (Chicago) to UTC correctly. ✅

### Issue 2: Round-Trip Conversion Issue

**Hypothesis:** Converting Chicago → UTC → Chicago may introduce errors.

**Analysis:**
- `ConvertChicagoLocalToUtc("02:00")` → UTC "08:00" ✅
- `ConvertUtcToChicago(UTC "08:00")` → Chicago "02:00" ✅
- Round-trip is correct ✅

### Issue 3: User Misunderstanding

**Hypothesis:** User may be confused about what the range represents.

**Reality:**
- Range IS calculated for Chicago 02:00-09:00 ✅
- Bars included are from Chicago 02:00-09:00 ✅
- UTC timestamps (08:00-15:00) are just for reference ✅

## Root Cause Hypothesis

**Most Likely:** The user is seeing UTC timestamps in logs and thinking the range is for UTC time, but it's actually correctly calculated for Chicago time.

**However, if bars are actually wrong:** NinjaTrader may be providing bars with incorrect timestamps, or the conversion in `RobotSimStrategy` may be incorrect.

## Diagnostic Steps

1. ✅ Verify config: `range_start_time` is "02:00" (Chicago) - **CONFIRMED**
2. ✅ Verify conversion: Chicago "02:00" → UTC "08:00" - **CONFIRMED**
3. ✅ Verify filtering: Uses Chicago time comparison - **CONFIRMED**
4. ⚠️ **NEED TO CHECK:** Are the actual bar timestamps correct?

## Next Steps

1. **Add diagnostic logging** to verify bar timestamps match expected Chicago time window
2. **Check if bars are being filtered correctly** - verify first/last bar times
3. **Verify NinjaTrader bar timestamp conversion** is correct

## Conclusion

The code appears correct - ranges ARE being calculated for Chicago time windows. However, we need to verify:
1. Bar timestamps are correct
2. Bar filtering is working as expected
3. User's expectation matches actual behavior
