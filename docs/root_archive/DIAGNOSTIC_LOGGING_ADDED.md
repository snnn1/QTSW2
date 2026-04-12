# Diagnostic Logging Added - Verification of Fixes

**Date**: 2026-01-30  
**Status**: ✅ **DIAGNOSTIC LOGGING IMPLEMENTED**

---

## Overview

Added diagnostic logging to verify that all three fixes are working correctly:

1. **Tick() from OnMarketData()** - Logs when Tick() is called from OnMarketData()
2. **INSTRUMENT_MISMATCH rate-limiting** - Logs when rate-limiting is active
3. **Safety assertion for stuck RANGE_BUILDING** - Logs when assertion is checking (even when OK)

---

## Diagnostic #1: Tick() Called from OnMarketData()

### Event Name
`TICK_CALLED_FROM_ONMARKETDATA`

### Location
**File**: `modules/robot/ninjatrader/RobotSimStrategy.cs`  
**Method**: `OnMarketData()`

### Implementation
```csharp
// DIAGNOSTIC: Log Tick() calls from OnMarketData to verify continuous execution fix is working
var executionInstrumentFullName = Instrument?.FullName ?? "UNKNOWN";
var diagCanLogTick = !_lastOnMarketDataTickLogUtc.TryGetValue(executionInstrumentFullName, out var diagPrevTickLogUtc) ||
                   (utcNow - diagPrevTickLogUtc).TotalMinutes >= ON_MARKET_DATA_TICK_RATE_LIMIT_MINUTES;

if (diagCanLogTick && _engine != null)
{
    _lastOnMarketDataTickLogUtc[executionInstrumentFullName] = utcNow;
    _engine.LogEngineEvent(utcNow, "TICK_CALLED_FROM_ONMARKETDATA", new Dictionary<string, object>
    {
        { "instrument", Instrument?.MasterInstrument?.Name ?? "UNKNOWN" },
        { "execution_instrument_full_name", executionInstrumentFullName },
        { "tick_price", tickPrice },
        { "market_data_type", e.MarketDataType.ToString() },
        { "note", $"DIAGNOSTIC: Tick() called from OnMarketData() - verifies continuous execution fix is working. Rate-limited to once per {ON_MARKET_DATA_TICK_RATE_LIMIT_MINUTES} minute(s)." }
    });
}
```

### Rate Limit
**Once per 5 minutes per instrument**

### What to Look For
- **Event appears**: Confirms Tick() is being called from OnMarketData()
- **Frequency**: Should appear every 5 minutes per instrument when ticks are flowing
- **If missing**: Indicates OnMarketData() isn't being called or fix isn't working

---

## Diagnostic #2: INSTRUMENT_MISMATCH Rate-Limiting Active

### Event Name
`INSTRUMENT_MISMATCH_RATE_LIMITED`

### Location
**File**: `modules/robot/core/Execution/NinjaTraderSimAdapter.NT.cs`  
**Methods**: `SubmitEntryOrderReal()` and `SubmitStopEntryOrderReal()`

### Implementation
```csharp
else
{
    // DIAGNOSTIC: Log when rate-limiting is active (verifies rate-limiting fix is working)
    // Log this diagnostic once per 10 minutes to confirm rate-limiting without flooding
    var shouldLogDiag = !_lastInstrumentMismatchDiagLogUtc.TryGetValue(instrument, out var lastDiagLogUtc) ||
                       (utcNow - lastDiagLogUtc).TotalMinutes >= 10.0;
    
    if (shouldLogDiag)
    {
        _lastInstrumentMismatchDiagLogUtc[instrument] = utcNow;
        var minutesSinceLastLog = lastLogUtc != DateTimeOffset.MinValue ? (utcNow - lastLogUtc).TotalMinutes : 0;
        _log.Write(RobotEvents.ExecutionBase(utcNow, intentId, instrument, "INSTRUMENT_MISMATCH_RATE_LIMITED", new
        {
            requested_instrument = instrument,
            strategy_instrument = (_ntInstrument as Instrument)?.FullName ?? "NULL",
            reason = "INSTRUMENT_MISMATCH",
            rate_limiting_active = true,
            minutes_since_last_log = Math.Round(minutesSinceLastLog, 1),
            rate_limit_minutes = INSTRUMENT_MISMATCH_RATE_LIMIT_MINUTES,
            note = $"DIAGNOSTIC: INSTRUMENT_MISMATCH rate-limiting is working. Last log was {Math.Round(minutesSinceLastLog, 1)} minutes ago. This block is suppressed."
        }));
    }
}
```

### Rate Limit
**Once per 10 minutes per instrument** (when rate-limiting is active)

### What to Look For
- **Event appears**: Confirms rate-limiting is working (blocks are being suppressed)
- **`minutes_since_last_log`**: Shows how long since last INSTRUMENT_MISMATCH log
- **`rate_limit_minutes`**: Should be 60 (1 hour)
- **If missing**: Indicates either no INSTRUMENT_MISMATCH blocks OR rate-limiting not working

---

## Diagnostic #3: Safety Assertion Checking

### Event Name
`RANGE_BUILDING_SAFETY_ASSERTION_CHECK`

### Location
**File**: `modules/robot/core/StreamStateMachine.cs`  
**Method**: `Tick()`

### Implementation
```csharp
// DIAGNOSTIC: Log safety assertion check (verifies assertion is running)
// Rate-limited to once per 15 minutes to confirm assertion is active without spam
var shouldLogAssertionCheck = !_lastStuckRangeBuildingAlertUtc.HasValue ||
                             (utcNow - _lastStuckRangeBuildingAlertUtc.Value).TotalMinutes >= 15.0;

if (shouldLogAssertionCheck && minutesPastSlotTime <= STUCK_RANGE_BUILDING_THRESHOLD_MINUTES)
{
    // Log that assertion is checking but threshold not exceeded (diagnostic confirmation)
    var nowChicago = _time.ConvertUtcToChicago(utcNow);
    var slotTimeChicago = _time.ConvertUtcToChicago(SlotTimeUtc);
    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
        "RANGE_BUILDING_SAFETY_ASSERTION_CHECK", State.ToString(),
        new
        {
            minutes_past_slot_time = Math.Round(minutesPastSlotTime, 1),
            threshold_minutes = STUCK_RANGE_BUILDING_THRESHOLD_MINUTES,
            slot_time_chicago = slotTimeChicago.ToString("o"),
            current_time_chicago = nowChicago.ToString("o"),
            status = "OK",
            note = $"DIAGNOSTIC: Safety assertion is checking. Stream is {Math.Round(minutesPastSlotTime, 1)} minutes past slot time (threshold: {STUCK_RANGE_BUILDING_THRESHOLD_MINUTES} min). Assertion is active and monitoring."
        }));
}
```

### Rate Limit
**Once per 15 minutes per stream** (when state is RANGE_BUILDING and threshold not exceeded)

### What to Look For
- **Event appears**: Confirms safety assertion is running and checking
- **`status: "OK"`**: Indicates stream is within threshold (not stuck)
- **`minutes_past_slot_time`**: Shows how many minutes past slot time
- **`threshold_minutes`**: Should be 10.0
- **If missing**: Indicates either not in RANGE_BUILDING state OR assertion not running

---

## How to Verify Fixes Are Working

### Fix #1: Continuous Tick() Execution

**Check for**:
```
Event: TICK_CALLED_FROM_ONMARKETDATA
Frequency: Every 5 minutes per instrument
```

**If working**:
- ✅ Event appears regularly
- ✅ Shows Tick() is being called from OnMarketData()
- ✅ Continuous execution fix is active

**If not working**:
- ❌ Event doesn't appear
- ❌ OnMarketData() may not be called
- ❌ Fix may not be deployed

---

### Fix #2: INSTRUMENT_MISMATCH Rate-Limiting

**Check for**:
```
Event: INSTRUMENT_MISMATCH_RATE_LIMITED
Frequency: Every 10 minutes per instrument (when blocks occur)
```

**If working**:
- ✅ Event appears when blocks are suppressed
- ✅ `minutes_since_last_log` shows time since last log
- ✅ Rate-limiting is preventing log flooding

**If not working**:
- ❌ Event doesn't appear (may indicate no blocks OR rate-limiting broken)
- ❌ Check for excessive INSTRUMENT_MISMATCH logs (should be max once per hour)

---

### Fix #3: Safety Assertion

**Check for**:
```
Event: RANGE_BUILDING_SAFETY_ASSERTION_CHECK
Frequency: Every 15 minutes per stream (when in RANGE_BUILDING)
```

**If working**:
- ✅ Event appears when stream is in RANGE_BUILDING state
- ✅ `status: "OK"` when within threshold
- ✅ Assertion is monitoring and would catch stuck states

**If stuck state detected**:
- ⚠️ Event `RANGE_BUILDING_STUCK_PAST_SLOT_TIME` appears (CRITICAL)
- ⚠️ `minutes_past_slot_time` > 10.0
- ⚠️ Indicates Tick() may not be running or range lock failing

---

## Summary

✅ **All diagnostic logging implemented**

1. ✅ `TICK_CALLED_FROM_ONMARKETDATA` - Verifies continuous Tick() execution
2. ✅ `INSTRUMENT_MISMATCH_RATE_LIMITED` - Verifies rate-limiting is working
3. ✅ `RANGE_BUILDING_SAFETY_ASSERTION_CHECK` - Verifies safety assertion is monitoring

**Status**: Ready to verify fixes are working in production
