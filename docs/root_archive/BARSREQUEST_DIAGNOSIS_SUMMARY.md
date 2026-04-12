# BarsRequest "No Execution Instruments Found" Diagnosis Summary

**Date**: 2026-02-02  
**Issue**: Warning "WARNING: No execution instruments found for BarsRequest"

## Root Cause

**`GetAllExecutionInstrumentsForBarsRequest()` returns empty list because streams are not being created.**

The method `GetAllExecutionInstrumentsForBarsRequest()` in `RobotEngine.cs` (line 4046) returns an empty list when:
1. `_spec is null || _time is null || !_activeTradingDate.HasValue` → Returns empty list immediately
2. No enabled streams exist (`_streams.Count == 0`) → No execution instruments to extract
3. All streams are committed → `enabledStreams.Count == 0` → No execution instruments

## Key Findings

### ✅ Robot Status
- **Robot is running**: ENGINE_TICK events are present (last seen: 2026-02-02 11:01:52 UTC)
- **Timetable loaded**: 7 streams enabled for today (2026-02-02)
  - Enabled: NQ1, NQ2, CL1, CL2, ES2, GC1, GC2
  - Disabled: ES1, YM1, YM2, NG1, NG2, RTY1, RTY2

### ❌ Missing Components

1. **No journals for today**: No journal files found for 2026-02-02
   - This is actually GOOD - means streams should be created fresh
   - Most recent journals are from 2026-01-31

2. **No stream creation events**: No `STREAM_CREATED` or `STREAM_ARMED` events found in recent logs
   - This is the ROOT CAUSE - streams aren't being created

3. **No BarsRequest events**: No BarsRequest events at all in logs
   - BarsRequest can't run if streams don't exist
   - `GetBarsRequestTimeRange()` returns null when no streams exist

4. **No timetable loading events**: No `TIMETABLE_LOADED` events in recent logs
   - This suggests timetable may not be loading properly, OR
   - Events aren't being logged to the feed

## Root Cause Analysis

**Primary Issue**: Streams are not being created for today (2026-02-02)

**Why streams aren't being created**:
1. **Timetable may not be loading/applying correctly** - No `TIMETABLE_LOADED` events found
2. **Engine.Start() may be failing silently** - Engine is running (ENGINE_TICK present) but streams not created
3. **Stream creation logic may be blocked** - `ApplyTimetable()` may not be executing
4. **Spec/Time/TradingDate may not be initialized** - `GetAllExecutionInstrumentsForBarsRequest()` checks these first

**Code Flow**:
```
RobotSimStrategy.OnStateChange(DataLoaded)
  → _engine.Start()
    → ApplyTimetable() [should create streams]
      → Streams should be created → _streams populated
  → GetAllExecutionInstrumentsForBarsRequest()
    → Returns empty list if _streams.Count == 0
  → Warning logged: "No execution instruments found"
```

## What Should Happen (Normal Flow)

1. `ENGINE_START` event
2. `TIMETABLE_LOADED` event (with stream count)
3. `STREAM_CREATED` events (one per enabled stream)
4. `STREAM_ARMED` events
5. `BARSREQUEST_STREAM_STATUS` event
6. `BARSREQUEST_REQUESTED` event
7. `BARSREQUEST_EXECUTED` event (with bars_returned count)

## Current State

- ✅ Step 1: ENGINE_START events present
- ❌ Step 2: TIMETABLE_LOADED events missing
- ❌ Step 3: STREAM_CREATED events missing
- ❌ Step 4-7: Cannot proceed without streams

## Next Steps to Diagnose

### 1. Check if Timetable is Actually Loading

```powershell
# Check for any timetable-related events in robot logs
Get-Content logs\robot\robot_ENGINE.jsonl -Tail 5000 | Select-String -Pattern "TIMETABLE|STREAM" | Select-Object -Last 20
```

### 2. Verify Engine.Start() is Completing

Check if there are any errors preventing stream creation:
- Look for ERROR level events
- Check for exceptions in logs
- Verify timetable file is valid JSON
- Check if `ApplyTimetable()` is being called

### 3. Check if Spec/Time/TradingDate are Initialized

The warning occurs because `GetAllExecutionInstrumentsForBarsRequest()` returns empty if:
- `_spec is null` → Spec not loaded
- `_time is null` → TimeService not initialized
- `!_activeTradingDate.HasValue` → Trading date not locked

### 4. Check Robot Logs for Stream Creation

Check robot log files directly for:
- `STREAM_CREATED` events
- `STREAM_ARMED` events
- `TIMETABLE_LOADED` events
- `SPEC_LOADED` events
- `TRADING_DATE_LOCKED` events

## Quick Fix (If Testing)

If you want to force stream creation:

1. **Delete any old journals** (if they exist):
   ```powershell
   Remove-Item logs\robot\journal\2026-02-02_*.json -ErrorAction SilentlyContinue
   ```

2. **Restart the robot** - streams should be created fresh

3. **Monitor logs** for `STREAM_CREATED` events

## Immediate Fix Steps

### Step 1: Verify Timetable is Loading

Check if timetable file exists and is valid:
```powershell
Test-Path data\timetable\timetable_current.json
Get-Content data\timetable\timetable_current.json | ConvertFrom-Json | Select-Object trading_date, streams
```

### Step 2: Check for Engine Initialization Errors

Look for errors in robot logs:
```powershell
Get-Content logs\robot\robot_ENGINE.jsonl -Tail 1000 | Select-String -Pattern "ERROR|Exception|Failed" | Select-Object -Last 20
```

### Step 3: Check if EnsureStreamsCreated() is Being Called

`EnsureStreamsCreated()` is called from `Tick()` (line 835). Check if `Tick()` is being called:
- `ENGINE_TICK_CALLSITE` events are present (good sign)
- But `EnsureStreamsCreated()` may not be creating streams if:
  - `_spec is null` (spec not loaded)
  - `_time is null` (TimeService not initialized)
  - `!_activeTradingDate.HasValue` (trading date not locked)
  - `_lastTimetable is null` (timetable not loaded)

### Step 4: Restart Robot and Monitor

1. Restart NinjaTrader strategy
2. Watch for `TIMETABLE_LOADED` event (should appear before `TIMETABLE_VALIDATED`)
3. Watch for `STREAM_CREATED` events (should appear after `TIMETABLE_LOADED`)
4. If streams are created, BarsRequest should proceed

### Step 5: Check if Spec is Loading

The spec file must load for streams to be created:
```powershell
Test-Path configs\analyzer_robot_parity.json
```

**Key Finding**: `TIMETABLE_VALIDATED` events are present, but `TIMETABLE_LOADED` events are NOT in the feed. This suggests:
- Timetable validation is happening
- But `TIMETABLE_LOADED` event may not be logged to feed, OR
- `EnsureStreamsCreated()` is not being called, OR
- `ApplyTimetable()` is not being called from `EnsureStreamsCreated()`

**Critical Code Path**:
```
Tick() (line 830)
  → ReloadTimetableIfChanged()
    → Timetable validated → TIMETABLE_VALIDATED logged
    → _lastTimetable stored
  → If _activeTradingDate.HasValue:
      → EnsureStreamsCreated() (line 835)
        → Checks: _spec is null || _time is null || !_activeTradingDate.HasValue?
          → YES: Log STREAMS_CREATION_SKIPPED, return
        → Checks: _lastTimetable == null?
          → YES: Log STREAMS_CREATION_FAILED, return
        → Calls ApplyTimetable() → Creates streams
```

**Check for these diagnostic events**:
- `STREAMS_CREATION_SKIPPED` → Missing spec/time/trading date
- `STREAMS_CREATION_FAILED` → No timetable loaded

## Questions to Answer

1. **When did this warning start appearing?**
   - Today (2026-02-02)?
   - After a restart?
   - After a specific change?

2. **Are there any errors in NinjaTrader Output?**
   - Check for exceptions during strategy initialization
   - Check for timetable loading errors

3. **Is the spec file loading?**
   - Check for `SPEC_LOADED` events
   - Verify `configs\analyzer_robot_parity.json` exists

4. **Is trading date being locked?**
   - Check for `TRADING_DATE_LOCKED` events
   - Verify timetable has `trading_date` field

## Code References

- **Stream Creation**: `RobotEngine.cs` line ~1014-1092 (`ApplyTimetable()`)
- **BarsRequest Initiation**: `RobotSimStrategy.cs` line ~320-440 (`OnStateChange()` DataLoaded)
- **BarsRequest Range Check**: `RobotEngine.cs` line ~4169 (`GetBarsRequestTimeRange()`)
- **BarsRequest Execution**: `RobotSimStrategy.cs` line ~680-1050 (`RequestHistoricalBarsForPreHydration()`)
