# Timezone Detection Example - How It Works

## Scenario: Live Bar Arrives from NinjaTrader

### Step 1: NinjaTrader Provides Bar Time

**Current Time**: 2026-01-20 14:30:00 UTC (8:30 AM Chicago time)

**NinjaTrader's `Times[0][0]`**: `2026-01-20T14:30:00` (DateTimeKind.Unspecified)

**Problem**: Is this UTC or Chicago time?

---

### Step 2: Detection Logic Tries Both Interpretations

#### Interpretation A: Treat as UTC
```csharp
var barUtcIfUtc = new DateTimeOffset(DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Utc), TimeSpan.Zero);
// Result: 2026-01-20T14:30:00+00:00 UTC

var barAgeIfUtc = (nowUtc - barUtcIfUtc).TotalMinutes;
// nowUtc = 2026-01-20T14:30:00 UTC
// barUtcIfUtc = 2026-01-20T14:30:00 UTC
// barAgeIfUtc = 0 minutes ✓ (bar is current)
```

#### Interpretation B: Treat as Chicago Time
```csharp
var barUtcIfChicago = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
// Converts: 2026-01-20T14:30:00 (Chicago) → 2026-01-20T20:30:00+00:00 UTC
// Result: 2026-01-20T20:30:00 UTC (6 hours ahead!)

var barAgeIfChicago = (nowUtc - barUtcIfChicago).TotalMinutes;
// nowUtc = 2026-01-20T14:30:00 UTC
// barUtcIfChicago = 2026-01-20T20:30:00 UTC
// barAgeIfChicago = -360 minutes ✗ (bar is 6 hours in the FUTURE)
```

---

### Step 3: Detection Logic Chooses Correct Interpretation

**Decision Logic**:
- UTC gives: 0 minutes (reasonable, positive)
- Chicago gives: -360 minutes (negative = future bar)

**Result**: Choose UTC interpretation ✓

```csharp
_barTimeInterpretation = BarTimeInterpretation.UTC;
barUtc = barUtcIfUtc;  // 2026-01-20T14:30:00+00:00 UTC
selectedInterpretation = "UTC";
```

---

### Step 4: Lock Interpretation and Log

```csharp
_barTimeInterpretationLocked = true;

_engine.LogEngineEvent(nowUtc, "BAR_TIME_INTERPRETATION_LOCKED", new Dictionary<string, object>
{
    { "locked_interpretation", "UTC" },
    { "first_bar_age_minutes", 0.0 },
    { "bar_age_if_utc", 0.0 },
    { "bar_age_if_chicago", -360.0 },
    { "invariant", "Bar time interpretation LOCKED = UTC. First bar age = 0 minutes. Reason = UTC interpretation gives reasonable bar age (0 min), Chicago gives -360 min" }
});
```

---

### Step 5: Pass Bar to Engine

```csharp
_engine.OnBar(barUtc, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
// barUtc = 2026-01-20T14:30:00+00:00 UTC
// nowUtc = 2026-01-20T14:30:00+00:00 UTC
```

---

### Step 6: Engine Processes Bar (UTC → Chicago Conversion)

**Inside `RobotEngine.OnBar()`**:

```csharp
// 1. Check bar age (must be at least 1 minute old)
var barAgeMinutes = (utcNow - barUtc).TotalMinutes;
// barAgeMinutes = 0 minutes
// MIN_BAR_AGE_MINUTES = 1.0
// Result: Bar rejected as "too recent" (partial bar protection)

// BUT: If bar was 1+ minutes old, continue...

// 2. Convert UTC to Chicago for internal processing
var barChicagoTime = _time.ConvertUtcToChicago(barUtc);
// barUtc = 2026-01-20T14:30:00+00:00 UTC
// barChicagoTime = 2026-01-20T08:30:00-06:00 (Chicago CST)

// 3. Extract trading date from Chicago time
var barChicagoDate = DateOnly.FromDateTime(barChicagoTime.DateTime);
// barChicagoDate = 2026-01-20

// 4. Validate against locked trading date
if (_activeTradingDate.Value != barChicagoDate)
{
    // Reject bar - wrong trading date
}

// 5. Deliver to streams (filtered by session/time windows)
foreach (var stream in _streams.Values)
{
    stream.OnBar(barUtc, barChicagoTime, open, high, low, close, utcNow);
}
```

---

## Example: Very Old Historical Bar (Edge Case)

### Scenario: First bar is 14 days old

**Current Time**: 2026-01-20 14:30:00 UTC

**NinjaTrader's `Times[0][0]`**: `2026-01-06T14:30:00` (14 days ago)

#### Interpretation A: UTC
```csharp
barUtcIfUtc = 2026-01-06T14:30:00+00:00 UTC
barAgeIfUtc = (2026-01-20T14:30:00 - 2026-01-06T14:30:00) = 20,160 minutes (14 days)
```

#### Interpretation B: Chicago
```csharp
barUtcIfChicago = 2026-01-06T20:30:00+00:00 UTC (converted from Chicago)
barAgeIfChicago = (2026-01-20T14:30:00 - 2026-01-06T20:30:00) = 19,800 minutes (13.75 days)
```

**Decision**: Both are very old (>1000 minutes)

**New Fix**: Prefer UTC when both are very old
```csharp
bool bothVeryOld = absAgeIfUtc > 1000 && absAgeIfChicago > 1000;
if (bothVeryOld)
{
    // Prefer UTC for very old bars (indicates live bars will be UTC)
    _barTimeInterpretation = BarTimeInterpretation.UTC;
    barUtc = barUtcIfUtc;
    selectedInterpretation = "UTC";
    selectionReason = "Both interpretations give very old bars (>1000 min) - preferring UTC for live bars";
}
```

**Result**: UTC chosen ✓

**Why This Matters**: When live bars arrive later, they'll be interpreted as UTC (correct), not Chicago (wrong).

---

## Example: Live Bar After Lock (Subsequent Bars)

### Scenario: Interpretation already locked to UTC

**Current Time**: 2026-01-20 14:35:00 UTC

**NinjaTrader's `Times[0][0]`**: `2026-01-20T14:35:00`

**Locked Interpretation**: UTC

```csharp
if (_barTimeInterpretation == BarTimeInterpretation.UTC)
{
    barUtc = new DateTimeOffset(DateTime.SpecifyKind(barExchangeTime, DateTimeKind.Utc), TimeSpan.Zero);
    // barUtc = 2026-01-20T14:35:00+00:00 UTC
}
else
{
    barUtc = NinjaTraderExtensions.ConvertBarTimeToUtc(barExchangeTime);
    // Not executed - UTC path taken
}

// Verify interpretation still valid
var barAge = (nowUtc - barUtc).TotalMinutes;
// barAge = 0 minutes ✓ (valid)

// Pass to engine
_engine.OnBar(barUtc, Instrument.MasterInstrument.Name, open, high, low, close, nowUtc);
```

---

## Complete Flow Diagram

```
NinjaTrader Bar Arrives
    ↓
Times[0][0] = 2026-01-20T14:30:00 (Unspecified)
    ↓
┌─────────────────────────────────────┐
│ Detection Logic (First Bar Only)  │
├─────────────────────────────────────┤
│ Try UTC:      Age = 0 min    ✓      │
│ Try Chicago:  Age = -360 min ✗      │
│                                     │
│ Choose: UTC (reasonable age)        │
│ Lock: _barTimeInterpretationLocked │
└─────────────────────────────────────┘
    ↓
barUtc = 2026-01-20T14:30:00+00:00 UTC
    ↓
┌─────────────────────────────────────┐
│ RobotEngine.OnBar()                 │
├─────────────────────────────────────┤
│ 1. Check bar age (≥1 min)           │
│ 2. Convert UTC → Chicago            │
│    barChicagoTime = 08:30:00-06:00  │
│ 3. Extract trading date             │
│    barChicagoDate = 2026-01-20      │
│ 4. Validate against locked date     │
│ 5. Deliver to streams               │
└─────────────────────────────────────┘
    ↓
StreamStateMachine.OnBar()
    ↓
Filter by session/time window
    ↓
Add to bar buffer
    ↓
Compute range when ready
```

---

## Key Points

1. **Input**: NinjaTrader provides ambiguous time (UTC or Chicago?)
2. **Detection**: Try both interpretations, choose the one that makes sense
3. **Lock**: Once chosen, use that interpretation for all subsequent bars
4. **Engine**: Always receives UTC, converts to Chicago internally
5. **Processing**: All business logic uses Chicago time (trading hours, sessions)

---

## Why This Works

- **UTC for Input**: Standardized, unambiguous, no DST issues
- **Chicago for Processing**: Matches exchange trading hours
- **Automatic Detection**: Handles both historical (old) and live (recent) bars
- **Locked Interpretation**: Prevents mid-run flips that would cause errors
