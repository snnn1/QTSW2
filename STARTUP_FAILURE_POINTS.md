# Startup Failure Points Analysis

## Potential Issues That Could Prevent or Break Startup

This document identifies all potential failure points in the startup sequence and their impact.

---

## 🔴 Critical Failures (Prevent Startup)

### 1. **Account Verification Failure**
**Location**: `RobotSimStrategy.OnStateChange()` - `State.DataLoaded`

**Failure Conditions**:
- `Account is null`
- `Account.IsSimAccount == false`

**Impact**: 
- Strategy **aborts immediately**
- Logs error: `"ERROR: Account is null"` or `"ERROR: Account '{name}' is not a Sim account"`
- **No engine created, no recovery**

**Code**:
```csharp
if (Account is null)
{
    Log($"ERROR: Account is null. Aborting.", LogLevel.Error);
    return; // Strategy stops here
}

if (!Account.IsSimAccount)
{
    Log($"ERROR: Account '{Account.Name}' is not a Sim account. Aborting.", LogLevel.Error);
    return; // Strategy stops here
}
```

---

### 2. **Spec File Missing or Invalid**
**Location**: `RobotEngine.Start()`

**Failure Conditions**:
- Spec file not found: `configs/analyzer_robot_parity.json`
- Spec validation fails (missing fields, wrong values)
- Spec deserialization fails

**Impact**:
- **Engine throws exception**
- Logs: `SPEC_INVALID` event
- **Startup fails completely**
- Strategy cannot proceed

**Code**:
```csharp
try
{
    _spec = ParitySpec.LoadFromFile(_specPath);
    _time = new TimeService(_spec.timezone);
}
catch (Exception ex)
{
    LogEvent(..., eventType: "SPEC_INVALID", ...);
    throw; // Exception propagates, startup fails
}
```

**Validation Checks** (in `ParitySpec.ValidateOrThrow()`):
- `spec_name` must be "analyzer_robot_parity"
- `spec_revision` must exist
- `timezone` must be "America/Chicago"
- `sessions` must include S1 and S2
- `instruments` must exist and not be empty
- `entry_cutoff` and `breakout` must be valid

---

### 3. **Timetable Missing or Invalid**
**Location**: `RobotEngine.ReloadTimetableIfChanged()`

**Failure Conditions**:
- Timetable file not found: `data/timetable/timetable_current.json`
- Timetable JSON invalid (parse error)
- Timetable missing `trading_date` field
- Timetable `trading_date` invalid format
- Timetable timezone != "America/Chicago"

**Impact**:
- **Calls `StandDown()`** (fail-closed)
- Logs: `TIMETABLE_INVALID`, `TIMETABLE_MISSING_TRADING_DATE`, or `TIMETABLE_INVALID_TRADING_DATE`
- **No streams created**
- **No trading date locked**
- Engine starts but cannot trade

**Code**:
```csharp
var poll = _timetablePoller.Poll(_timetablePath, utcNow);
if (poll.Error is not null)
{
    LogEvent(..., eventType: "TIMETABLE_INVALID", ...);
    StandDown(); // Fail-closed
    return;
}

// Later...
if (string.IsNullOrWhiteSpace(tradingDateStr))
{
    LogEvent(..., eventType: "TIMETABLE_MISSING_TRADING_DATE", ...);
    StandDown(); // Fail-closed
    return;
}
```

**StandDown() Behavior**:
- Clears all streams
- Logs `ENGINE_STAND_DOWN` event
- Engine remains running but cannot trade

---

### 4. **Instrument Not in Spec**
**Location**: `StreamStateMachine` constructor

**Failure Conditions**:
- Timetable stream references instrument not in spec
- Example: Timetable has "ES" but spec doesn't have "ES" instrument

**Impact**:
- **Constructor throws `InvalidOperationException`**
- Stream creation fails
- **Startup fails** (exception propagates)

**Code**:
```csharp
if (!spec.TryGetInstrument(Instrument, out var inst))
    throw new InvalidOperationException($"Instrument not found in parity spec: {Instrument}");
```

---

## 🟡 Partial Failures (Degraded Operation)

### 5. **GetSessionInfo Returns Null**
**Location**: `RobotSimStrategy.RequestHistoricalBarsForPreHydration()`

**Failure Conditions**:
- Instrument not in spec
- Session "S1" not in spec
- Spec not loaded yet (shouldn't happen)

**Impact**:
- **Falls back to hardcoded defaults**: `"02:00"` and `"07:30"`
- Logs warning: `"Cannot get session info from spec - using defaults"`
- **BarsRequest may request wrong time range**
- **Silent misalignment** if spec times differ from defaults

**Code**:
```csharp
var sessionInfo = _engine.GetSessionInfo(instrumentName, sessionName);
if (!sessionInfo.HasValue)
{
    Log($"Cannot get session info from spec - using defaults", LogLevel.Warning);
    rangeStartChicago = "02:00"; // Hardcoded fallback
    slotTimeChicago = "07:30";   // Hardcoded fallback
}
```

**Risk**: This defeats the purpose of reading from spec. Should be treated as error, not warning.

---

### 6. **BarsRequest Fails or Returns No Bars**
**Location**: `RobotSimStrategy.RequestHistoricalBarsForPreHydration()`

**Failure Conditions**:
- NinjaTrader BarsRequest API throws exception
- BarsRequest returns null or empty list
- All bars filtered out (future/partial)

**Impact**:
- **Logs warning** but continues
- **No historical bars loaded**
- Streams rely on live bars only
- **Range may be incomplete** if started after range_start

**Code**:
```csharp
try
{
    var bars = NinjaTraderBarRequest.RequestBarsForTradingDate(...);
    if (bars.Count > 0)
    {
        _engine.LoadPreHydrationBars(...);
    }
    else
    {
        Log($"No historical bars returned - will use file-based or live bars", LogLevel.Warning);
    }
}
catch (Exception ex)
{
    Log($"Failed to request historical bars: {ex.Message}. Will use file-based or live bars.", LogLevel.Warning);
    // Continues without historical bars
}
```

**Risk**: Silent degradation. May cause `NO_BARS_IN_WINDOW` errors later.

---

### 7. **Trading Date Not Locked When Requesting Bars**
**Location**: `RobotSimStrategy.RequestHistoricalBarsForPreHydration()`

**Failure Conditions**:
- `engine.Start()` called but timetable invalid
- Trading date not locked yet

**Impact**:
- **Returns early** with warning
- **No historical bars requested**
- Streams start without pre-hydration

**Code**:
```csharp
var tradingDateStr = _engine.GetTradingDate();
if (string.IsNullOrEmpty(tradingDateStr))
{
    Log("Cannot request historical bars - trading date not yet locked", LogLevel.Warning);
    return; // Early return, no bars requested
}
```

**Risk**: This should not happen in normal operation, but if it does, streams start empty.

---

### 8. **Streams Not Created When Loading Bars**
**Location**: `RobotEngine.LoadPreHydrationBars()`

**Failure Conditions**:
- `RequestHistoricalBarsForPreHydration()` called before `engine.Start()` completes
- Timetable invalid, so streams not created
- Race condition

**Impact**:
- **Logs**: `PRE_HYDRATION_BARS_SKIPPED` with reason "Streams not yet created"
- **Bars discarded**
- Streams start without historical bars

**Code**:
```csharp
if (_streams.Count == 0)
{
    LogEvent(..., eventType: "PRE_HYDRATION_BARS_SKIPPED", 
        new { reason = "Streams not yet created" });
    return; // Bars discarded
}
```

**Risk**: Timing issue. BarsRequest should be called after streams are created.

---

## 🟢 Non-Critical Issues (Logged Only)

### 9. **Kill Switch Enabled**
**Location**: `RiskGate.CheckGates()`

**Failure Conditions**:
- Kill switch file missing (fail-closed: enabled by default)
- Kill switch file has `enabled: true`

**Impact**:
- **Order execution blocked**
- Logs: `KILL_SWITCH_ACTIVE`
- Engine continues running
- Streams continue processing
- **No orders placed**

**Code**:
```csharp
if (!File.Exists(_killSwitchPath))
{
    // Fail-closed: enabled by default
    return true; // Kill switch enabled
}
```

**Note**: This doesn't prevent startup, only prevents trading.

---

### 10. **Startup After Range Window**
**Location**: `RobotEngine.CheckStartupTiming()`

**Failure Conditions**:
- Strategy started after `RangeStartChicagoTime`
- Example: Started at 08:00 but range_start is 02:00

**Impact**:
- **Logs warning**: `STARTUP_TIMING_WARNING`
- **Range may be incomplete**
- Historical bars may not cover full range
- **No error, just warning**

**Code**:
```csharp
if (utcNow >= stream.RangeStartUtc)
{
    LogEvent(..., eventType: "STARTUP_TIMING_WARNING", ...);
    // Continues normally
}
```

---

## 🔵 Race Conditions & Timing Issues

### 11. **BarsRequest Called Before Streams Created**
**Current Flow**:
```csharp
engine.Start();                    // Creates streams
RequestHistoricalBarsForPreHydration(); // Requests bars
```

**Risk**: If `Start()` fails to create streams (timetable invalid), BarsRequest still runs but bars are discarded.

**Mitigation**: `LoadPreHydrationBars()` checks if streams exist, but this is defensive only.

---

### 12. **Tick Timer Started Before Engine Ready**
**Location**: `RobotSimStrategy.OnStateChange()` - `State.Realtime`

**Current Flow**:
```csharp
State.DataLoaded:
    engine.Start();
    RequestHistoricalBarsForPreHydration();
    
State.Realtime:
    StartTickTimer(); // Calls engine.Tick() every second
```

**Risk**: If `Start()` throws exception, tick timer still starts, calling `Tick()` on invalid engine state.

**Mitigation**: Tick timer checks `_engine != null`, but engine may be in invalid state.

---

## 📋 Summary: Failure Modes

| Failure Point | Severity | Impact | Recovery |
|--------------|----------|--------|----------|
| Account not SIM | 🔴 Critical | Strategy aborts | Manual fix |
| Spec missing/invalid | 🔴 Critical | Startup fails | Fix spec file |
| Timetable missing/invalid | 🔴 Critical | StandDown(), no trading | Fix timetable |
| Instrument not in spec | 🔴 Critical | Stream creation fails | Add to spec |
| GetSessionInfo null | 🟡 Partial | Uses hardcoded defaults | Should be error |
| BarsRequest fails | 🟡 Partial | No historical bars | Degraded operation |
| Trading date not locked | 🟡 Partial | No bars requested | Should not happen |
| Streams not created | 🟡 Partial | Bars discarded | Timing issue |
| Kill switch enabled | 🟢 Non-critical | No orders placed | Disable kill switch |
| Startup after range | 🟢 Non-critical | Incomplete range | Warning only |

---

## 🛡️ Recommendations

### High Priority Fixes

1. **Make GetSessionInfo failure an error**:
   - If `GetSessionInfo()` returns null, **throw exception** instead of using defaults
   - Prevents silent misalignment

2. **Validate BarsRequest success**:
   - If BarsRequest fails and no bars loaded, **log error** (not warning)
   - Consider failing startup if critical for operation

3. **Add startup readiness check**:
   - Before starting tick timer, verify:
     - Trading date locked
     - Streams created
     - Spec loaded
   - If not ready, delay tick timer start

### Medium Priority Fixes

4. **Better error messages**:
   - Include file paths in error messages
   - Include expected vs actual values
   - Suggest fixes

5. **Defensive checks**:
   - Verify streams exist before calling `LoadPreHydrationBars()`
   - Verify trading date locked before requesting bars
   - Add null checks in tick timer

### Low Priority Improvements

6. **Graceful degradation**:
   - If BarsRequest fails, try file-based pre-hydration (SIM mode)
   - If no historical bars, log clearly but continue

7. **Startup validation**:
   - Add `ValidateStartup()` method that checks all prerequisites
   - Call before entering realtime state

---

## 🔍 How to Diagnose Startup Failures

**Check Logs For**:
1. `ENGINE_START` - Engine started
2. `SPEC_LOADED` - Spec loaded successfully
3. `TRADING_DATE_LOCKED` - Trading date locked
4. `STREAMS_CREATED` - Streams created
5. `PRE_HYDRATION_BARS_LOADED` - Historical bars loaded
6. `OPERATOR_BANNER` - Startup complete

**If Missing**:
- `SPEC_LOADED` → Spec file issue
- `TRADING_DATE_LOCKED` → Timetable issue
- `STREAMS_CREATED` → Timetable or spec issue
- `PRE_HYDRATION_BARS_LOADED` → BarsRequest issue (non-critical)

**Error Events to Watch**:
- `SPEC_INVALID`
- `TIMETABLE_INVALID`
- `TIMETABLE_MISSING_TRADING_DATE`
- `STREAMS_CREATION_FAILED`
- `PRE_HYDRATION_BARS_SKIPPED`
