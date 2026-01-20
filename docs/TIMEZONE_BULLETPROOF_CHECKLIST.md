# Timezone Bulletproof Checklist

## Issue 1: Bar Timestamp Interpretation

### Requirements

1. **Log raw bar time, Kind, and chosen interpretation at startup**
   - Log `Times[0][0]` raw value
   - Log `DateTimeKind` (Unspecified, UTC, Local)
   - Log which interpretation was chosen (UTC vs Chicago)
   - Log reasoning (positive bar age, reasonable age, etc.)

2. **Lock the interpretation for the entire run**
   - Store chosen interpretation in a class-level variable
   - Use same interpretation for all bars during strategy lifetime
   - Never re-evaluate interpretation after startup

3. **Alert/fail if the interpretation would flip**
   - On each bar, verify current interpretation still gives valid bar age
   - If interpretation would change, log CRITICAL alert
   - Optionally fail closed (reject bar or stop strategy)

### Implementation Checklist

- [ ] Add `_barTimeInterpretation` field to strategy class (UTC or Chicago)
- [ ] Add `_barTimeInterpretationLocked` boolean flag
- [ ] In `OnBarUpdate()`, check if interpretation is locked
- [ ] If not locked, detect interpretation (try both UTC and Chicago)
- [ ] Log detection result with raw values and reasoning
- [ ] Lock interpretation after first successful detection
- [ ] On subsequent bars, verify locked interpretation still works
- [ ] If verification fails, log CRITICAL alert and optionally fail

### Code Pattern

```csharp
private BarTimeInterpretation? _barTimeInterpretation;
private bool _barTimeInterpretationLocked = false;

private void DetectAndLockBarTimeInterpretation(DateTime barTime)
{
    if (_barTimeInterpretationLocked) return;
    
    // Try UTC interpretation
    var utcAge = CalculateBarAge(barTime, interpretAsUtc: true);
    
    // Try Chicago interpretation  
    var chicagoAge = CalculateBarAge(barTime, interpretAsUtc: false);
    
    // Choose interpretation
    BarTimeInterpretation chosen;
    string reason;
    if (utcAge > 0 && utcAge < 10)
    {
        chosen = BarTimeInterpretation.UTC;
        reason = $"UTC gives positive age {utcAge} minutes";
    }
    else if (chicagoAge > 0 && chicagoAge < 10)
    {
        chosen = BarTimeInterpretation.Chicago;
        reason = $"Chicago gives positive age {chicagoAge} minutes";
    }
    else
    {
        chosen = BarTimeInterpretation.Chicago; // Fallback
        reason = "Fallback to Chicago (documented behavior)";
    }
    
    // Log detection
    LogEvent(new {
        eventType = "BAR_TIME_INTERPRETATION_DETECTED",
        raw_bar_time = barTime.ToString("O"),
        kind = barTime.Kind.ToString(),
        chosen_interpretation = chosen.ToString(),
        reason = reason,
        utc_age_minutes = utcAge,
        chicago_age_minutes = chicagoAge
    });
    
    // Lock interpretation
    _barTimeInterpretation = chosen;
    _barTimeInterpretationLocked = true;
}

private void VerifyBarTimeInterpretation(DateTime barTime)
{
    if (!_barTimeInterpretationLocked) return;
    
    var age = CalculateBarAge(barTime, _barTimeInterpretation == BarTimeInterpretation.UTC);
    
    if (age < 0 || age > 60) // Invalid age
    {
        LogEvent(new {
            eventType = "BAR_TIME_INTERPRETATION_MISMATCH",
            severity = "CRITICAL",
            locked_interpretation = _barTimeInterpretation.ToString(),
            current_bar_age_minutes = age,
            raw_bar_time = barTime.ToString("O"),
            warning = "Bar time interpretation would flip - this should not happen"
        });
        
        // Optionally fail closed
        // throw new InvalidOperationException("Bar time interpretation mismatch");
    }
}
```

### Files to Update

- `modules/robot/ninjatrader/RobotSimStrategy.cs`
- `modules/robot/ninjatrader/RobotSkeletonStrategy.cs`
- `RobotCore_For_NinjaTrader/NinjaTraderExtensions.cs`

---

## Issue 2: Trading Date Handling

### Requirements

1. **Backend emits trading_date (Chicago) as date-only string YYYY-MM-DD**
   - Timetable JSON must include `trading_date` field
   - Format: `"YYYY-MM-DD"` (e.g., `"2026-01-19"`)
   - Value is Chicago date (not UTC, not local)
   - No time component, no timezone suffix

2. **Frontend never derives trading date from browser time unless explicitly "Chicago now"**
   - Never use `new Date()` for trading date
   - Never use `Date.now()` for trading date
   - Only derive from backend `trading_date` field
   - If "Chicago now" needed, use explicit Chicago timezone conversion

3. **Any date-only value must never go through ISO UTC conversion step**
   - Never call `.toISOString()` on date-only values
   - Never use `Date.parse()` with ISO strings for dates
   - Parse `YYYY-MM-DD` strings directly
   - Store dates as strings or Date objects without time components

### Implementation Checklist

**Backend (`timetable_engine.py`):**
- [ ] Ensure `trading_date` field in timetable JSON is `YYYY-MM-DD` format
- [ ] Use Chicago timezone explicitly: `datetime.now(chicago_tz).date().isoformat()`
- [ ] Never include time component in `trading_date`
- [ ] Never include timezone in `trading_date` (it's date-only)

**Frontend (`matrixWorker.js`):**
- [ ] Read `trading_date` from timetable JSON (backend-provided)
- [ ] Parse `YYYY-MM-DD` string directly (no ISO conversion)
- [ ] Never derive trading date from `new Date()` or browser time
- [ ] If "Chicago now" needed, use explicit conversion function

**Frontend (`App.jsx`):**
- [ ] Pass `currentTradingDay` explicitly to worker (from backend or user selection)
- [ ] Never derive `currentTradingDay` from browser date
- [ ] Use date picker or backend `trading_date` as source

### Code Pattern

**Backend:**
```python
# timetable_engine.py
def write_execution_timetable_from_master_matrix(...):
    chicago_tz = pytz.timezone("America/Chicago")
    chicago_now = datetime.now(chicago_tz)
    trading_date = chicago_now.date().isoformat()  # YYYY-MM-DD
    
    timetable = {
        "trading_date": trading_date,  # Date-only, no time, no timezone
        "streams": [...]
    }
```

**Frontend:**
```javascript
// matrixWorker.js
function parseTradingDate(dateStr) {
    // Direct parse of YYYY-MM-DD, no ISO conversion
    const parts = dateStr.split('-');
    if (parts.length !== 3) throw new Error(`Invalid date format: ${dateStr}`);
    return new Date(parseInt(parts[0]), parseInt(parts[1]) - 1, parseInt(parts[2]));
    // Note: This creates Date object but we only use date components
}

// Never do this:
// const date = new Date().toISOString().split('T')[0]; // WRONG - uses browser timezone

// Always do this:
// const date = timetable.trading_date; // RIGHT - from backend
```

**If "Chicago now" needed:**
```javascript
// Explicit Chicago timezone conversion
function getChicagoDateNow() {
    const now = new Date();
    const chicagoOffset = -6 * 60; // CST offset in minutes (adjust for DST)
    const utc = now.getTime() + (now.getTimezoneOffset() * 60000);
    const chicagoTime = new Date(utc + (chicagoOffset * 60000));
    return chicagoTime.toISOString().split('T')[0]; // YYYY-MM-DD
}
```

### Files to Update

**Backend:**
- `modules/timetable/timetable_engine.py` - Ensure `trading_date` format
- `modules/matrix/file_manager.py` - Verify timetable JSON structure

**Frontend:**
- `modules/matrix_timetable_app/frontend/src/matrixWorker.js` - Date parsing
- `modules/matrix_timetable_app/frontend/src/App.jsx` - Date source
- `modules/matrix_timetable_app/frontend/src/components/TimetableTab.jsx` - Date display

---

## Validation Tests

### Issue 1 Tests

- [ ] Start strategy, verify interpretation logged
- [ ] Process 100 bars, verify interpretation never changes
- [ ] Inject bar with different timezone, verify alert logged
- [ ] Verify interpretation persists across strategy restarts (if state saved)

### Issue 2 Tests

- [ ] Backend generates timetable with `trading_date` in `YYYY-MM-DD` format
- [ ] Frontend reads `trading_date` without ISO conversion
- [ ] User in different timezone sees correct Chicago date
- [ ] Date picker selects date, frontend uses it directly (no browser time conversion)
- [ ] Timetable displays correct date regardless of user's timezone

---

## Key Principles

1. **Explicit over implicit**: Always specify timezone/interpretation explicitly
2. **Lock early**: Detect and lock bar time interpretation at startup
3. **Fail closed**: Alert on mismatches, optionally fail on critical errors
4. **Date-only strings**: Use `YYYY-MM-DD` format, never include time/timezone
5. **Backend authority**: Backend determines trading date, frontend consumes it
6. **No browser timezone**: Frontend never derives trading date from browser time

---

## References

- `docs/robot/TIMEZONE_FIX_2026-01-19.md` - Original bar timestamp fix
- `docs/TIMEZONE_ERROR_EXPLANATION.md` - Detailed error explanation
- `modules/robot/core/TimeService.cs` - Chicago timezone handling
- `modules/timetable/timetable_engine.py` - Backend timetable generation
