# Code Deduplication & Complexity Analysis

## Summary
Analysis of code duplication and overcomplexity in the Robot.Core codebase, with specific refactoring recommendations.

---

## 1. Time Construction Duplication ⚠️ HIGH PRIORITY

### Problem
The pattern of constructing Chicago times and converting to UTC is duplicated in **3 locations**:

1. **Constructor** (StreamStateMachine.cs:166-176)
2. **UpdateTradingDate** (StreamStateMachine.cs:303-312)
3. **ApplyDirectiveUpdate** (StreamStateMachine.cs:212-216)

### Current Code Pattern
```csharp
// PHASE 1: Construct Chicago times directly (authoritative)
var rangeStartChicago = _spec.sessions[Session].range_start_time;
RangeStartChicagoTime = _time.ConstructChicagoTime(tradingDate, rangeStartChicago);
SlotTimeChicagoTime = _time.ConstructChicagoTime(tradingDate, SlotTimeChicago);
var marketCloseChicagoTime = _time.ConstructChicagoTime(tradingDate, _spec.entry_cutoff.market_close_time);

// PHASE 2: Derive UTC times from Chicago times (derived representation)
RangeStartUtc = RangeStartChicagoTime.ToUniversalTime();
SlotTimeUtc = SlotTimeChicagoTime.ToUniversalTime();
MarketCloseUtc = marketCloseChicagoTime.ToUniversalTime();
```

### Refactoring Solution
**Extract to private method:**
```csharp
private void RecomputeTimeBoundaries(DateOnly tradingDate)
{
    var rangeStartChicago = _spec.sessions[Session].range_start_time;
    RangeStartChicagoTime = _time.ConstructChicagoTime(tradingDate, rangeStartChicago);
    SlotTimeChicagoTime = _time.ConstructChicagoTime(tradingDate, SlotTimeChicago);
    var marketCloseChicagoTime = _time.ConstructChicagoTime(tradingDate, _spec.entry_cutoff.market_close_time);
    
    RangeStartUtc = RangeStartChicagoTime.ToUniversalTime();
    SlotTimeUtc = SlotTimeChicagoTime.ToUniversalTime();
    MarketCloseUtc = marketCloseChicagoTime.ToUniversalTime();
}
```

**Usage:**
- Constructor: `RecomputeTimeBoundaries(dateOnly);`
- UpdateTradingDate: `RecomputeTimeBoundaries(newTradingDate);`
- ApplyDirectiveUpdate: Only needs SlotTime update, but could call partial method

**Impact:** Reduces ~30 lines of duplicated code, ensures consistency

---

## 2. Trading Date String Conversion Duplication ⚠️ MEDIUM PRIORITY

### Problem
Pattern `_activeTradingDate?.ToString("yyyy-MM-dd") ?? ""` appears **13+ times** in RobotEngine.cs

### Current Code Pattern
```csharp
var tradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, ...));
```

### Refactoring Solution
**Add helper property/method:**
```csharp
private string TradingDateString => _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
```

**Usage:**
- Replace all instances with `TradingDateString`
- Reduces risk of format inconsistencies
- Single source of truth for date string format

**Impact:** Reduces ~13 lines, improves maintainability

---

## 3. State Reset Complexity ⚠️ MEDIUM PRIORITY

### Problem
Long list of state resets in `UpdateTradingDate` (lines 340-365) - **26 fields reset manually**

### Current Code Pattern
```csharp
RangeHigh = null;
RangeLow = null;
FreezeClose = null;
FreezeCloseSource = "UNSET";
_lastCloseBeforeLock = null;
_entryDetected = false;
_intendedDirection = null;
_intendedEntryPrice = null;
_intendedEntryTimeUtc = null;
_triggerReason = null;
_rangeComputed = false;
_rangeIntentAssertEmitted = false;
_firstBarAcceptedAssertEmitted = false;
_rangeLockAssertEmitted = false;
_preHydrationComplete = false;
_largestSingleGapMinutes = 0.0;
_totalGapMinutes = 0.0;
_lastBarOpenChicago = null;
_rangeInvalidated = false;
_rangeInvalidatedNotified = false;
// ... more resets
```

### Refactoring Solution
**Extract to private method:**
```csharp
private void ResetDailyState()
{
    // Range tracking
    RangeHigh = null;
    RangeLow = null;
    FreezeClose = null;
    FreezeCloseSource = "UNSET";
    
    // Entry tracking
    _lastCloseBeforeLock = null;
    _entryDetected = false;
    _intendedDirection = null;
    _intendedEntryPrice = null;
    _intendedEntryTimeUtc = null;
    _triggerReason = null;
    
    // Range computation
    _rangeComputed = false;
    
    // Assertion flags
    _rangeIntentAssertEmitted = false;
    _firstBarAcceptedAssertEmitted = false;
    _rangeLockAssertEmitted = false;
    
    // Pre-hydration
    _preHydrationComplete = false;
    
    // Gap tracking
    _largestSingleGapMinutes = 0.0;
    _totalGapMinutes = 0.0;
    _lastBarOpenChicago = null;
    _rangeInvalidated = false;
    _rangeInvalidatedNotified = false;
}
```

**Usage:**
- `UpdateTradingDate`: Call `ResetDailyState()` instead of inline resets
- Makes intent clear: "reset all daily state"
- Easier to maintain if new fields added

**Impact:** Improves readability, reduces cognitive load

---

## 4. Journal Creation Duplication ⚠️ LOW PRIORITY

### Problem
Similar journal creation logic in:
1. **Constructor** (StreamStateMachine.cs:183-193)
2. **UpdateTradingDate** (StreamStateMachine.cs:323-332)

### Current Code Pattern
```csharp
var existing = journals.TryLoad(tradingDateStr, Stream);
_journal = existing ?? new StreamJournal
{
    TradingDate = tradingDateStr,
    Stream = Stream,
    Committed = false,
    CommitReason = null,
    LastState = State.ToString(),
    LastUpdateUtc = DateTimeOffset.MinValue.ToString("o"), // or utcNow.ToString("o")
    TimetableHashAtCommit = null
};
```

### Refactoring Solution
**Extract to private method:**
```csharp
private StreamJournal GetOrCreateJournal(DateOnly tradingDate, DateTimeOffset utcNow, bool isInitialization)
{
    var tradingDateStr = tradingDate.ToString("yyyy-MM-dd");
    var existing = _journals.TryLoad(tradingDateStr, Stream);
    
    if (existing != null)
        return existing;
    
    return new StreamJournal
    {
        TradingDate = tradingDateStr,
        Stream = Stream,
        Committed = false,
        CommitReason = null,
        LastState = State.ToString(),
        LastUpdateUtc = isInitialization ? DateTimeOffset.MinValue.ToString("o") : utcNow.ToString("o"),
        TimetableHashAtCommit = null
    };
}
```

**Usage:**
- Constructor: `_journal = GetOrCreateJournal(dateOnly, DateTimeOffset.MinValue, isInitialization: true);`
- UpdateTradingDate: `_journal = GetOrCreateJournal(newTradingDate, utcNow, isInitialization: false);`

**Impact:** Reduces ~20 lines, ensures consistency

---

## 5. Logging Pattern Duplication ⚠️ LOW PRIORITY

### Problem
Repeated pattern of creating event objects with similar structure

### Current Code Pattern
```csharp
LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "EVENT_NAME", state: "ENGINE",
    new { field1 = value1, field2 = value2 }));
```

### Refactoring Solution
**Consider helper methods for common event patterns:**
```csharp
private void LogEngineEvent(DateTimeOffset utcNow, string eventType, object data)
{
    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: eventType, state: "ENGINE", data));
}
```

**Impact:** Minor improvement, may not be worth it if patterns vary significantly

---

## 6. UpdateTradingDate Guard Complexity ⚠️ MEDIUM PRIORITY

### Problem
Complex nested conditionals in `UpdateTradingDate` (lines 222-301) checking:
- Is initialization?
- Is backward date?
- Is committed journal?
- Multiple early returns

### Current Code Pattern
```csharp
public void UpdateTradingDate(DateOnly newTradingDate, DateTimeOffset utcNow)
{
    // Guard: Prevent mid-session trading date changes
    if (_activeTradingDate.HasValue && _activeTradingDate.Value == newTradingDate)
        return;
    
    // Guard: If trading date is already locked, reject changes
    if (_activeTradingDate.HasValue && _activeTradingDate.Value != newTradingDate)
    {
        // Log warning and reject
        return;
    }
    
    // ... complex initialization/backward date logic
    // ... committed journal logic
    // ... time reconstruction
    // ... state reset
}
```

### Refactoring Solution
**Simplify with early returns and clearer guard methods:**
```csharp
public void UpdateTradingDate(DateOnly newTradingDate, DateTimeOffset utcNow)
{
    // Guard: Prevent mid-session trading date changes (new requirement)
    if (_activeTradingDate.HasValue && _activeTradingDate.Value != newTradingDate)
    {
        LogTradingDateChangeRejected(newTradingDate, utcNow);
        return;
    }
    
    // Guard: No-op if same date
    if (_activeTradingDate.HasValue && _activeTradingDate.Value == newTradingDate)
        return;
    
    // Handle initialization vs rollover
    var previousTradingDateStr = TradingDate;
    var isInitialization = string.IsNullOrWhiteSpace(previousTradingDateStr);
    var existingJournal = _journals.TryLoad(newTradingDate.ToString("yyyy-MM-dd"), Stream);
    
    // Handle committed journal (early return)
    if (existingJournal != null && existingJournal.Committed)
    {
        HandleCommittedJournal(existingJournal, newTradingDate, utcNow, isInitialization);
        return;
    }
    
    // Normal path: update times and state
    RecomputeTimeBoundaries(newTradingDate);
    _journal = GetOrCreateJournal(newTradingDate, utcNow, isInitialization);
    _journals.Save(_journal);
    
    // Reset state only for forward rollover
    if (!isInitialization && !IsBackwardDate(previousTradingDateStr, newTradingDate))
    {
        ResetDailyState();
        // ... additional reset logic
    }
}

private void HandleCommittedJournal(StreamJournal journal, DateOnly newDate, DateTimeOffset utcNow, bool isInitialization)
{
    _journal = journal;
    State = StreamState.DONE;
    // ... logging logic
}

private bool IsBackwardDate(string previousDateStr, DateOnly newDate)
{
    if (string.IsNullOrWhiteSpace(previousDateStr))
        return false;
    
    if (TimeService.TryParseDateOnly(previousDateStr, out var prevDate))
        return newDate < prevDate;
    
    return false;
}
```

**Impact:** Improves readability, reduces nesting, makes logic flow clearer

---

## 7. EnsureStreamsCreated Validation Duplication ⚠️ LOW PRIORITY

### Problem
Timezone validation appears in:
1. `EnsureStreamsCreated` (line 517)
2. `ReloadTimetableIfChanged` (line 591)

### Current Code Pattern
```csharp
if (timetable.timezone != "America/Chicago")
{
    var tradingDateStr = _activeTradingDate?.ToString("yyyy-MM-dd") ?? "";
    LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: tradingDateStr, eventType: "TIMETABLE_INVALID", state: "ENGINE",
        new { reason = "TIMEZONE_MISMATCH", timezone = timetable.timezone }));
    StandDown();
    return;
}
```

### Refactoring Solution
**Extract validation method:**
```csharp
private bool ValidateTimetableTimezone(TimetableContract timetable, DateTimeOffset utcNow)
{
    if (timetable.timezone != "America/Chicago")
    {
        LogEvent(RobotEvents.EngineBase(utcNow, tradingDate: TradingDateString, eventType: "TIMETABLE_INVALID", state: "ENGINE",
            new { reason = "TIMEZONE_MISMATCH", timezone = timetable.timezone }));
        StandDown();
        return false;
    }
    return true;
}
```

**Impact:** Reduces duplication, ensures consistent error handling

---

## Priority Summary

1. **HIGH**: Time construction duplication (affects 3 locations, ~30 lines)
2. **MEDIUM**: Trading date string conversion (13+ instances)
3. **MEDIUM**: State reset complexity (26 fields, readability issue)
4. **MEDIUM**: UpdateTradingDate guard complexity (nested conditionals)
5. **LOW**: Journal creation duplication (2 locations, ~20 lines)
6. **LOW**: Timezone validation duplication (2 locations)

---

## Recommended Refactoring Order

1. Extract `RecomputeTimeBoundaries()` method
2. Add `TradingDateString` property
3. Extract `ResetDailyState()` method
4. Simplify `UpdateTradingDate()` guards
5. Extract `GetOrCreateJournal()` method (optional)
6. Extract `ValidateTimetableTimezone()` method (optional)

---

## Testing Considerations

After refactoring, verify:
- Time boundaries computed correctly in all scenarios
- State resets work correctly on rollover
- Journal creation handles existing/new journals correctly
- Trading date string format consistent everywhere
- No behavioral changes (refactoring only, no logic changes)
