# Session Window Logic Explanation

## How Session Window Is Determined

The session window defines which bars belong to a trading date. It's computed in `GetSessionWindow()` method in `RobotEngine.cs`.

## Current Implementation

### Session Start: **17:00 CST** (Hardcoded - Temporary)

**Location:** `modules/robot/core/RobotEngine.cs` line 837
```csharp
// Session starts previous calendar day at 17:00 CST (temporary hardcode, standard CME futures session start)
const string SESSION_START_TIME = "17:00";
var previousDay = tradingDate.AddDays(-1);
var sessionStartChicago = _time.ConstructChicagoTime(previousDay, SESSION_START_TIME);
```

**Why 17:00 CST?**
- Standard CME futures session start time
- Evening session begins at 5:00 PM Chicago time
- This is a **temporary hardcode** - should eventually come from TradingHours

**Note:** The comment says "temporary hardcode" - this should be derived from NinjaTrader's `TradingHours` template in the future.

### Session End: **16:00 CST** (From Spec)

**Location:** `modules/robot/core/RobotEngine.cs` line 842
```csharp
// Session ends at market close on trading date (from spec)
var marketCloseTime = _spec.entry_cutoff.market_close_time; // "16:00"
var sessionEndChicago = _time.ConstructChicagoTime(tradingDate, marketCloseTime);
```

**Source:** `configs/analyzer_robot_parity.json` line 29
```json
"entry_cutoff": {
  "type": "MARKET_CLOSE",
  "market_close_time": "16:00",
  "rule": "If breakout occurs after market_close_time, treat as NoTrade (no entry)."
}
```

**Why 16:00 CST?**
- Market close time for CME futures
- Defined in the parity spec as the entry cutoff time
- This is the authoritative source (not hardcoded)

## Session Window Formula

For trading date **T**, the session window is:

```
Session Start: (T - 1 day) at 17:00 CST
Session End:   T at 16:00 CST
Window:        [Session Start, Session End)  // inclusive start, exclusive end
```

**Example for Jan 20:**
- Session Start: Jan 19 at 17:00 CST
- Session End: Jan 20 at 16:00 CST
- Window: `[Jan 19 17:00 CST, Jan 20 16:00 CST)`

## Why This Logic?

### Futures Trading Sessions Span Calendar Dates

Futures markets trade overnight:
- **Evening session:** Starts previous day at 17:00 CST
- **Day session:** Continues until 16:00 CST on trading date
- **Total session:** ~23 hours spanning two calendar dates

### Calendar Date Comparison Was Wrong

**Old (broken) logic:**
```csharp
if (bar_chicago_date != active_trading_date) {
    // REJECT - wrong!
}
```

**Problem:** This rejected valid evening session bars (Jan 19 17:00-23:59 CST) for trading date Jan 20.

**New (correct) logic:**
```csharp
if (bar_chicago < session_start || bar_chicago >= session_end) {
    // REJECT - correct!
}
```

**Solution:** Validates against session window, not calendar date.

## Future Improvements

### Session Start Should Come From TradingHours

The code comment indicates this is temporary:
```csharp
// Session starts previous calendar day at 17:00 CST (temporary hardcode, standard CME futures session start)
```

**Future implementation should:**
1. Query NinjaTrader's `TradingHours` template for the instrument
2. Extract the actual session start time (may vary by instrument)
3. Use that instead of hardcoded 17:00 CST

**Why this matters:**
- Different instruments may have different session start times
- TradingHours is the authoritative source in NinjaTrader
- Hardcoding assumes all instruments start at 17:00 CST

### Session End Already From Spec

Session end is correctly sourced from the spec file (`market_close_time: "16:00"`), which is good.

## Code Locations

**Implementation:**
- `modules/robot/core/RobotEngine.cs` - `GetSessionWindow()` method (line 831)
- `RobotCore_For_NinjaTrader/RobotEngine.cs` - Same method (mirrored)

**Configuration:**
- `configs/analyzer_robot_parity.json` - `entry_cutoff.market_close_time` (line 29)

**Usage:**
- `modules/robot/core/RobotEngine.cs` - `OnBar()` method (line 576)
- Used to validate if bars fall within session window

## Summary

**Current Logic:**
- **Session Start:** Hardcoded `17:00 CST` (temporary)
- **Session End:** From spec `"16:00"` (authoritative)
- **Window:** `[previous_day 17:00 CST, trading_date 16:00 CST)`

**Future Enhancement:**
- Session start should come from NinjaTrader `TradingHours` template
- This will make it instrument-specific and authoritative
