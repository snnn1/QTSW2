# Robot Issues After RANGE_BUILDING Fix

## Summary
After fixing the issue where streams could enter `RANGE_BUILDING` when there's no range to build (market closed or no bars), several potential issues were identified in the robot code.

## Issues Identified

### 1. **ARMED_WAITING_FOR_BARS Log Spam**
**Problem**: The `ARMED_WAITING_FOR_BARS` diagnostic event is logged every time `HandleArmedState()` checks for bars and finds none. If `Tick()` is called frequently (e.g., every second), this could generate excessive log entries.

**Impact**: Log spam, making it harder to find important events.

**Location**: `modules/robot/core/StreamStateMachine.cs:1424`

**Current Behavior**:
- Every `Tick()` call in `ARMED` state after `RangeStartUtc` checks `GetBarBufferCount()`
- If `barCount == 0`, logs `ARMED_WAITING_FOR_BARS` immediately
- No rate limiting on this log

**Fix Needed**: Add rate limiting similar to other diagnostic logs (e.g., log once per 5 minutes).

### 2. **OnBar Processing After Commit**
**Problem**: `OnBar()` doesn't check if the stream is committed before processing bars. While bars are still buffered (which may be intentional for logging), the method continues processing even after commit.

**Impact**: Unnecessary processing after stream has committed, though likely harmless since state handlers check committed status.

**Location**: `modules/robot/core/StreamStateMachine.cs:2075`

**Current Behavior**:
- `OnBar()` checks trading date but not committed status
- Continues to buffer bars and process even after commit
- `Tick()` correctly checks committed and returns early (line 711)

**Fix Needed**: Add early return in `OnBar()` if stream is committed (optional optimization).

### 3. **Race Condition: Bars Arrive Right After Check**
**Problem**: There's a small window where bars could arrive right after we check `GetBarBufferCount() == 0` but before we return. This is handled correctly (we'll check again on next `Tick()`), but could cause a brief delay in transition.

**Impact**: Minimal - handled correctly by checking again on next `Tick()`.

**Mitigation**: Current behavior is acceptable - no fix needed.

### 4. **Race Condition: Transition to RANGE_BUILDING Right Before Market Close**
**Problem**: If a stream transitions to `RANGE_BUILDING` right before market close, there's a window where it could be in `RANGE_BUILDING` state briefly before `HandleRangeBuildingState()` checks market close.

**Impact**: Minimal - `HandleRangeBuildingState()` checks market close first (line 1549), so stream will commit quickly.

**Mitigation**: Current behavior is acceptable - market close check happens immediately in `HandleRangeBuildingState()`.

### 5. **Missing Bar Count Check in HandleRangeBuildingState**
**Problem**: `HandleRangeBuildingState()` doesn't check if bars are available before attempting range computation. If a stream somehow enters `RANGE_BUILDING` with no bars (shouldn't happen with our fix, but defensive), range computation will fail.

**Impact**: Low - our fix prevents this scenario, but defensive check would be safer.

**Location**: `modules/robot/core/StreamStateMachine.cs:1545`

**Current Behavior**:
- `HandleRangeBuildingState()` checks market close first
- Then proceeds with range computation without checking bar availability
- Range computation will fail gracefully if no bars available

**Fix Needed**: Add defensive check for bar availability (optional, but safer).

### 6. **ARMED State Diagnostic Logging**
**Problem**: `ARMED_STATE_DIAGNOSTIC` is logged every 5 minutes while waiting for range start, but `ARMED_WAITING_FOR_BARS` is logged every `Tick()` when waiting for bars. This inconsistency could cause confusion.

**Impact**: Minor - inconsistent logging behavior.

**Location**: `modules/robot/core/StreamStateMachine.cs:1391` vs `1424`

**Fix Needed**: Rate-limit `ARMED_WAITING_FOR_BARS` to match `ARMED_STATE_DIAGNOSTIC` frequency (every 5 minutes).

## Recommended Fixes

### Priority 1: Rate-Limit ARMED_WAITING_FOR_BARS
**Impact**: HIGH (prevents log spam)
**Effort**: LOW

```csharp
// Add rate-limiting field
private DateTimeOffset? _lastArmedWaitingForBarsLogUtc = null;

// In HandleArmedState(), before logging ARMED_WAITING_FOR_BARS:
var shouldLogWaitingForBars = !_lastArmedWaitingForBarsLogUtc.HasValue || 
    (utcNow - _lastArmedWaitingForBarsLogUtc.Value).TotalMinutes >= 5.0;

if (shouldLogWaitingForBars)
{
    _lastArmedWaitingForBarsLogUtc = utcNow;
    LogHealth("INFO", "ARMED_WAITING_FOR_BARS", ...);
}
```

### Priority 2: Add Committed Check in OnBar (Optional)
**Impact**: LOW (optimization only)
**Effort**: LOW

```csharp
public void OnBar(...)
{
    // Early return if committed
    if (_journal.Committed)
    {
        return;
    }
    // ... rest of method
}
```

### Priority 3: Defensive Bar Check in HandleRangeBuildingState (Optional)
**Impact**: LOW (defensive safety)
**Effort**: LOW

```csharp
private void HandleRangeBuildingState(DateTimeOffset utcNow)
{
    // Check for market close cutoff
    if (!_entryDetected && utcNow >= MarketCloseUtc)
    {
        LogNoTradeMarketClose(utcNow);
        Commit(utcNow, "NO_TRADE_MARKET_CLOSE", "MARKET_CLOSE_NO_TRADE");
        return;
    }
    
    // Defensive check: ensure bars are available
    var barCount = GetBarBufferCount();
    if (barCount == 0)
    {
        // Should not happen (our fix prevents this), but defensive check
        LogHealth("WARN", "RANGE_BUILDING_NO_BARS", 
            "RANGE_BUILDING state reached with no bars available. This should not happen.",
            new { bar_count = barCount });
        // Wait for bars or market close
        return;
    }
    
    // ... rest of method
}
```

## Testing Checklist
- [ ] Stream in ARMED waiting for bars - verify ARMED_WAITING_FOR_BARS is rate-limited (not spammed)
- [ ] Stream commits - verify OnBar() doesn't process bars after commit (if fix implemented)
- [ ] Stream transitions to RANGE_BUILDING right before market close - verify it commits quickly
- [ ] Bars arrive right after barCount check - verify stream transitions on next Tick()
- [ ] Stream somehow enters RANGE_BUILDING with no bars - verify defensive check handles it (if fix implemented)
