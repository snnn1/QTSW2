# Execution Gate Invariant Violation Analysis

## What's Happening

**EXECUTION_GATE_INVARIANT_VIOLATION** events are being triggered when:
- Slot time + 1 minute has passed (`barUtc >= slotTimePlusInterval`)
- Execution is **NOT** allowed (`!finalAllowed`)
- State is OK (`stateOk == true`, meaning state is `RANGE_LOCKED`)
- Slot has been reached (`slotReached == true`)

**This is an invariant violation** because if the slot time has passed, state is RANGE_LOCKED, and slot is reached, execution **should** be allowed, but it's not.

## Root Cause Analysis

Looking at the gate evaluation logic in `StreamStateMachine.cs` (lines 2929-2937), execution is allowed only when **ALL** of these gates pass:

```csharp
var finalAllowed = realtimeOk && 
                  !string.IsNullOrEmpty(tradingDay) &&
                  sessionActive &&
                  slotReached &&
                  timetableEnabled &&
                  streamArmed &&
                  stateOk &&
                  entryDetectionModeOk &&
                  canDetectEntries;
```

### Most Likely Causes

Based on the code, the most likely gates blocking execution are:

1. **`canDetectEntries` is false** (Most Likely)
   - Requires: `stateOk && !_entryDetected && slotReached && barUtc < MarketCloseUtc && breakout levels computed`
   - **Common failure**: Breakout levels not computed yet (`_brkLongRounded` or `_brkShortRounded` is null)
   - **Common failure**: Entry already detected (`_entryDetected == true`)

2. **`streamArmed` is false**
   - Requires: `!_journal.Committed && State != StreamState.DONE`
   - **Common failure**: Journal is committed (stream already executed for the day)
   - **Common failure**: State is DONE

3. **`slotReached` is false**
   - Requires: `barUtc >= slotTimeUtcParsed`
   - **Less likely** since violation only triggers when slot time + 1 minute has passed

4. **`sessionActive` is false**
   - Requires: Session exists in spec and is valid
   - **Less likely** if timetable is validated

## Why This Happens

The violation occurs because there's a **timing window** where:
1. Slot time has passed (slot reached)
2. State is RANGE_LOCKED (good)
3. But breakout levels haven't been computed yet, OR entry was already detected, OR journal is committed

This is actually **expected behavior** in some cases:
- **Breakout levels not computed**: Normal during pre-hydration or if range isn't locked yet
- **Entry already detected**: Normal if entry was detected and executed
- **Journal committed**: Normal if stream already executed for the day

## The Real Issue

**The violation check is too aggressive**. It triggers even when execution is legitimately blocked for valid reasons (e.g., entry already detected, journal committed, breakout levels not ready).

### Current Check (Line 2970):
```csharp
if (barUtc >= slotTimePlusInterval && !finalAllowed && stateOk && slotReached)
```

This triggers whenever execution isn't allowed, even if it's blocked for legitimate reasons.

## Recommendations

### Option 1: Make Violation Check More Specific
Only trigger violation if execution is blocked for **unexpected** reasons:

```csharp
// Only violate if execution should be allowed but isn't due to unexpected reasons
var unexpectedBlock = !finalAllowed && 
                      stateOk && 
                      slotReached && 
                      streamArmed &&  // Stream should be armed
                      !_entryDetected &&  // Entry not detected yet
                      _brkLongRounded.HasValue && _brkShortRounded.HasValue;  // Breakout levels ready

if (barUtc >= slotTimePlusInterval && unexpectedBlock)
{
    // This is a real violation
}
```

### Option 2: Reduce Violation Severity
Change violation from ERROR/CRITICAL to WARNING, since many are expected:
- Entry already detected → Expected
- Journal committed → Expected  
- Breakout levels not ready → Expected during initialization

### Option 3: Add Context to Violation
Include which specific gate is failing in the violation payload so it's clear why execution is blocked.

## Current Impact

- **85 notifications sent** in Jan 26-27
- **Rate limiting not working** (HealthMonitor recreated per run)
- **Most violations are likely false positives** (execution blocked for valid reasons)

## Next Steps

1. **Fix rate limiting** (move to NotificationService singleton) ✅ Identified
2. **Review violation logic** to only trigger on truly unexpected conditions
3. **Add gate failure details** to violation payload for better diagnostics
4. **Consider reducing severity** if violations are mostly expected
