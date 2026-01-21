# Timetable Trading Date and Session Window Relationship

## Overview

The **timetable's `trading_date`** is the **authoritative source** for determining which trading date the robot operates on. The **session window** is **derived from** this trading date.

## Flow Diagram

```
timetable_current.json
    └─> trading_date: "2026-01-20"
            │
            ├─> Locked into _activeTradingDate (immutable)
            │
            └─> Used to compute session window:
                    Session Start: (2026-01-20 - 1 day) at 17:00 CST = Jan 19 17:00 CST
                    Session End:   2026-01-20 at 16:00 CST = Jan 20 16:00 CST
                    Window:        [Jan 19 17:00 CST, Jan 20 16:00 CST)
```

## Step-by-Step Process

### 1. Timetable Loading

**File:** `data/timetable/timetable_current.json`

**Structure:**
```json
{
  "trading_date": "2026-01-20",
  "timezone": "America/Chicago",
  "as_of": "2026-01-20T10:00:00Z",
  "streams": [
    {
      "stream": "ES1",
      "instrument": "ES",
      "session": "S1",
      "slot_time": "07:30",
      "enabled": true
    },
    ...
  ]
}
```

**Key Field:** `trading_date` (format: "YYYY-MM-DD")

### 2. Trading Date Locking

**Location:** `modules/robot/core/RobotEngine.cs` - `ReloadTimetableIfChanged()` method

**Process:**
1. Timetable is loaded from file
2. `trading_date` field is extracted: `"2026-01-20"`
3. Parsed into `DateOnly`: `DateOnly.Parse("2026-01-20")`
4. Locked into `_activeTradingDate` (immutable for engine run)
5. Event logged: `TRADING_DATE_LOCKED`

**Code:**
```csharp
timetableTradingDate = DateOnly.Parse(timetable.trading_date);
_activeTradingDate = timetableTradingDate.Value; // Locked!
```

**Important:** Once locked, `_activeTradingDate` cannot be changed during the engine run (immutable).

### 3. Session Window Computation

**Location:** `modules/robot/core/RobotEngine.cs` - `GetSessionWindow()` method

**Process:**
1. Uses `_activeTradingDate` (from timetable)
2. Computes session start: `(trading_date - 1 day) at session_start_time`
3. Computes session end: `trading_date at market_close_time`
4. Returns window: `[session_start, session_end)`

**Code:**
```csharp
private (DateTimeOffset sessionStartChicago, DateTimeOffset sessionEndChicago) GetSessionWindow(DateOnly tradingDate, string instrument = "")
{
    // tradingDate comes from _activeTradingDate (which came from timetable)
    var sessionStartTime = GetSessionStartTime(instrument); // From TradingHours or "17:00"
    var previousDay = tradingDate.AddDays(-1);
    var sessionStartChicago = _time.ConstructChicagoTime(previousDay, sessionStartTime);
    
    var marketCloseTime = _spec.entry_cutoff.market_close_time; // "16:00"
    var sessionEndChicago = _time.ConstructChicagoTime(tradingDate, marketCloseTime);
    
    return (sessionStartChicago, sessionEndChicago);
}
```

### 4. Bar Validation

**Location:** `modules/robot/core/RobotEngine.cs` - `OnBar()` method

**Process:**
1. Bar arrives with UTC timestamp
2. Convert to Chicago time
3. Get session window using `_activeTradingDate`
4. Validate: `session_start <= bar_chicago < session_end`
5. Accept or reject based on window membership

**Code:**
```csharp
var (sessionStartChicago, sessionEndChicago) = GetSessionWindow(_activeTradingDate.Value, instrument);

if (barChicagoTime < sessionStartChicago || barChicagoTime >= sessionEndChicago)
{
    // REJECT - bar outside session window
    LogEvent("BAR_DATE_MISMATCH", ...);
    return;
}
// ACCEPT - bar within session window
```

## Example: Complete Flow

**Timetable:**
```json
{
  "trading_date": "2026-01-20"
}
```

**Step 1: Lock Trading Date**
- `_activeTradingDate` = `DateOnly(2026, 1, 20)`

**Step 2: Compute Session Window**
- Session Start: `Jan 19 at 17:00 CST` (previous day + session start time)
- Session End: `Jan 20 at 16:00 CST` (trading date + market close)
- Window: `[Jan 19 17:00 CST, Jan 20 16:00 CST)`

**Step 3: Validate Bars**
- Bar at `Jan 19 17:30 CST` → **ACCEPTED** (within window)
- Bar at `Jan 19 16:30 CST` → **REJECTED** (before session start)
- Bar at `Jan 20 15:30 CST` → **ACCEPTED** (within window)
- Bar at `Jan 20 16:00 CST` → **REJECTED** (at/exceeds session end)

## Key Relationships

### Timetable Trading Date → Session Window

**Formula:**
```
timetable.trading_date = "2026-01-20"
    ↓
_activeTradingDate = DateOnly(2026, 1, 20)
    ↓
Session Window = [
    (_activeTradingDate - 1 day) at session_start_time,
    _activeTradingDate at market_close_time
)
    ↓
Session Window = [Jan 19 17:00 CST, Jan 20 16:00 CST)
```

### Why This Matters

1. **Timetable is authoritative:** The timetable's `trading_date` determines which trading date we're operating on
2. **Session window is derived:** The session window is computed FROM the trading date, not independent
3. **Bars validated against window:** All bars are validated against the session window for that trading date
4. **Immutable once locked:** Trading date cannot change mid-run (prevents confusion)

## Important Notes

### Trading Date Immutability

Once `_activeTradingDate` is locked from the timetable:
- It cannot be changed during the engine run
- If timetable updates with different `trading_date`, it's logged but ignored
- This ensures consistency throughout a trading session

### Session Window Per Trading Date

Each trading date has its own session window:
- **Jan 20:** `[Jan 19 17:00 CST, Jan 20 16:00 CST)`
- **Jan 21:** `[Jan 20 17:00 CST, Jan 21 16:00 CST)`
- **Jan 22:** `[Jan 21 17:00 CST, Jan 22 16:00 CST)`

The window is **always** computed relative to the timetable's `trading_date`.

### Timetable Updates

If the timetable file is updated:
- `ReloadTimetableIfChanged()` is called periodically
- If `trading_date` changes, it's logged but **not applied** (already locked)
- Stream configurations can be updated, but trading date remains locked

## Summary

**Timetable `trading_date`** → **`_activeTradingDate`** → **Session Window** → **Bar Validation**

The timetable's `trading_date` is the **root source** that determines:
1. Which trading date we're operating on
2. What the session window boundaries are
3. Which bars belong to this trading session

The session window is **not independent** - it's **derived from** the timetable's trading date.
