# BarsRequest Model Verification

## Your Statement

> "In SIM mode, BarsRequest is a synchronous call that asks NinjaTrader for all fully closed historical bars for a bounded time window up to 'now'. Those bars are sanitized, tagged as BARSREQUEST, merged into the stream buffer, and used to seed range computation once the range window opens."

## Verification Against Current Implementation

### ✅ 1. "BarsRequest is a synchronous call"

**Code**: `NinjaTraderBarRequest.cs:49`
```csharp
var barsSeries = barsRequest.Request();  // Synchronous call
```

**Status**: ✅ **CONGRUENT** - `Request()` is synchronous, blocks until bars arrive

---

### ✅ 2. "asks NinjaTrader for all fully closed historical bars"

**Code**: `NinjaTraderBarRequest.cs:38-45`
```csharp
var barsRequest = new BarsRequest(instrument)
{
    BarsPeriod = new BarsPeriod { BarsPeriodType = BarsPeriodType.Minute, Value = 1 },
    StartTime = startTimeUtc.DateTime,
    EndTime = endTimeUtc.DateTime,
    TradingHours = instrument.MasterInstrument.TradingHours
};
```

**Status**: ✅ **CONGRUENT** - BarsRequest queries NinjaTrader's historical database for fully closed bars

**Note**: BarsRequest returns fully closed bars by design (NinjaTrader API behavior)

---

### ✅ 3. "for a bounded time window up to 'now'"

**Code**: `RobotSimStrategy.cs:210-212`
```csharp
var endTimeChicago = nowChicago < slotTimeChicagoTime
    ? nowChicago.ToString("HH:mm")  // Up to "now" if before slot time
    : slotTimeChicago;              // Up to slot_time if after slot time
```

**Status**: ✅ **CONGRUENT** (with nuance)

**Bounded by**:
- **Primary**: `min(now, slot_time)` - Up to "now" if started before slot time
- **Secondary**: `slot_time` - Limited to slot_time if restarting after slot time

**Example**:
- Started at 08:00 → Request: 07:30 to 08:00 ✅ (up to "now")
- Started at 09:15 → Request: 07:30 to 09:00 ✅ (limited to slot_time, not "now")

**Why the nuance**: Restart policy prevents loading bars beyond slot_time to maintain deterministic range input set.

---

### ✅ 4. "Those bars are sanitized"

**Code**: `RobotEngine.cs:628-647`
```csharp
foreach (var bar in bars)
{
    // Filter 1: Reject future bars
    if (bar.TimestampUtc > utcNow)
    {
        barsFilteredFuture++;
        continue;
    }
    
    // Filter 2: Reject partial/in-progress bars (must be at least 1 minute old)
    var barAgeMinutes = (utcNow - bar.TimestampUtc).TotalMinutes;
    if (barAgeMinutes < MIN_BAR_AGE_MINUTES)
    {
        barsFilteredPartial++;
        continue;
    }
    
    // Bar passed all filters
    filteredBars.Add(bar);
}
```

**Status**: ✅ **CONGRUENT** - Bars are sanitized:
- Future bars filtered out
- Partial/in-progress bars filtered out (< 1 minute old)
- Only fully closed bars accepted

---

### ✅ 5. "tagged as BARSREQUEST"

**Code**: `StreamStateMachine.cs:1348-1349`
```csharp
var barSource = isHistorical ? BarSource.BARSREQUEST : BarSource.LIVE;
AddBarToBuffer(new Bar(barUtc, open, high, low, close, null), barSource);
```

**Code**: `RobotEngine.cs:718`
```csharp
stream.OnBar(bar.TimestampUtc, bar.Open, bar.High, bar.Low, bar.Close, utcNow, isHistorical: true);
```

**Status**: ✅ **CONGRUENT** - Bars from BarsRequest are tagged as `BarSource.BARSREQUEST`:
- `isHistorical: true` passed to `OnBar()`
- `OnBar()` sets `barSource = BarSource.BARSREQUEST`
- Added to buffer with BARSREQUEST tag

---

### ✅ 6. "merged into the stream buffer"

**Code**: `StreamStateMachine.cs:AddBarToBuffer()` (via `OnBar()`)
```csharp
// Bar added to _barBuffer
_barBuffer.Add(bar);
_barSourceMap[barUtc] = BarSource.BARSREQUEST;
_historicalBarCount++;

// Buffer sorted chronologically
_barBuffer.Sort((a, b) => a.TimestampUtc.CompareTo(b.TimestampUtc));
```

**Status**: ✅ **CONGRUENT** - Bars are:
- Added to `_barBuffer` (stream's internal buffer)
- Tracked in `_barSourceMap` (for deduplication)
- Sorted chronologically
- Deduplicated if same timestamp exists (BARSREQUEST precedence)

---

### ✅ 7. "used to seed range computation once the range window opens"

**Code**: `StreamStateMachine.cs:719-723`
```csharp
if (!_rangeComputed && utcNow < SlotTimeUtc)
{
    // Compute range up to current time (not slot_time yet)
    var initialRangeResult = ComputeRangeRetrospectively(utcNow, endTimeUtc: utcNow);
    
    if (initialRangeResult.Success)
    {
        RangeHigh = initialRangeResult.RangeHigh;
        RangeLow = initialRangeResult.RangeLow;
        // ...
    }
}
```

**Code**: `StreamStateMachine.cs:687-699` (ComputeRangeRetrospectively uses buffered bars)
```csharp
// Uses _barBuffer to compute range
foreach (var bar in barsInRange)
{
    rangeHigh = Math.Max(rangeHigh, bar.High);
    rangeLow = Math.Min(rangeLow, bar.Low);
}
```

**Status**: ✅ **CONGRUENT** - Buffered bars are used to:
- Seed initial range computation when entering RANGE_BUILDING state
- Compute range from all bars in buffer (BarsRequest + live bars)
- Update incrementally as live bars arrive
- Lock at slot_time

---

## Summary

| Statement Part | Status | Notes |
|---------------|--------|-------|
| Synchronous call | ✅ CONGRUENT | `barsRequest.Request()` blocks |
| Fully closed historical bars | ✅ CONGRUENT | NinjaTrader API returns closed bars |
| Bounded time window up to "now" | ✅ CONGRUENT | `min(now, slot_time)` - nuanced |
| Bars sanitized | ✅ CONGRUENT | Future/partial bars filtered |
| Tagged as BARSREQUEST | ✅ CONGRUENT | `BarSource.BARSREQUEST` |
| Merged into stream buffer | ✅ CONGRUENT | Added to `_barBuffer` |
| Seed range computation | ✅ CONGRUENT | Used in `ComputeRangeRetrospectively()` |

## Conclusion

✅ **Your statement is CONGRUENT with the current implementation.**

All parts match the code behavior. The only nuance is the "up to 'now'" part, which is actually `min(now, slot_time)` to handle restart scenarios correctly.

## Code Flow Summary

```
1. Synchronous BarsRequest.Request()
   └─ Returns fully closed historical bars

2. Time window: [range_start, min(now, slot_time)]
   └─ Bounded up to "now" (or slot_time if restarting)

3. Bars sanitized (future/partial filtered)
   └─ Only fully closed bars pass

4. Tagged as BarSource.BARSREQUEST
   └─ isHistorical: true → BARSREQUEST

5. Merged into stream buffer
   └─ Added to _barBuffer, sorted, deduplicated

6. Seed range computation
   └─ ComputeRangeRetrospectively() uses buffered bars
```
