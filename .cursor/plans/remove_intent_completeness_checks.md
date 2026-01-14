# Remove Intent Completeness Checks

## Goal
Remove redundant Intent Completeness Check 2 (RiskGate gate) and simplify Check 1 (rely on execution adapter null checks).

## Current State

### Check 1: Before Intent Creation (StreamStateMachine.cs line 1257)
```csharp
if (_intendedStopPrice == null || _intendedTargetPrice == null || _intendedBeTrigger == null)
{
    // Intent incomplete - cannot execute
    _log.Write(... "EXECUTION_SKIPPED" ... new { reason = "INTENT_INCOMPLETE" ... });
    return;
}
```

### Check 2: RiskGate Gate (RiskGate.cs line 71-75)
```csharp
// Gate 5: Intent completeness
if (completenessFlag != "COMPLETE")
{
    return (false, "INTENT_INCOMPLETE");
}
```

**Problem**: completenessFlag is hard-coded to "COMPLETE" (line 1315), so this check always passes and is redundant.

### Execution Adapter Already Checks (NinjaTraderSimAdapter.cs line 270)
```csharp
if (intent.Direction == null || intent.StopPrice == null || intent.TargetPrice == null)
{
    _log.Write(... "EXECUTION_ERROR" ... new { error = "Intent incomplete" ... });
    return;
}
```

## Changes Required

### 1. Remove Check 2: RiskGate Completeness Gate
**File**: `modules/robot/core/Execution/RiskGate.cs`

Remove Gate 5 (lines 71-75):
```csharp
// REMOVE:
// Gate 5: Intent completeness
if (completenessFlag != "COMPLETE")
{
    return (false, "INTENT_INCOMPLETE");
}
```

Remove `completenessFlag` parameter from `CheckGates()` method signature (line 36).

### 2. Update CheckGates Call Site
**File**: `modules/robot/core/StreamStateMachine.cs` (line 1305-1316)

Remove `completenessFlag` argument from `CheckGates()` call:
```csharp
// Change from:
var (allowed, reason) = _riskGate.CheckGates(
    _executionMode,
    TradingDate,
    Stream,
    Instrument,
    Session,
    SlotTimeChicago,
    timetableValidated: true,
    streamArmed: !_journal.Committed && State != StreamState.DONE,
    completenessFlag: "COMPLETE",  // REMOVE THIS LINE
    utcNow);

// To:
var (allowed, reason) = _riskGate.CheckGates(
    _executionMode,
    TradingDate,
    Stream,
    Instrument,
    Session,
    SlotTimeChicago,
    timetableValidated: true,
    streamArmed: !_journal.Committed && State != StreamState.DONE,
    utcNow);
```

### 3. Simplify Check 1: Remove Redundant Null Check
**File**: `modules/robot/core/StreamStateMachine.cs` (line 1257-1264)

Remove the null check entirely - execution adapter already handles this:
```csharp
// REMOVE:
if (_intendedStopPrice == null || _intendedTargetPrice == null || _intendedBeTrigger == null)
{
    // Intent incomplete - cannot execute
    _log.Write(RobotEvents.Base(_time, utcNow, TradingDate, Stream, Instrument, Session, SlotTimeChicago, SlotTimeUtc,
        "EXECUTION_SKIPPED", State.ToString(),
        new { reason = "INTENT_INCOMPLETE", direction, entry_price = entryPrice }));
    return;
}

// Intent will be created with null values, execution adapter will catch it and log error
```

## Files to Modify

1. `modules/robot/core/Execution/RiskGate.cs` - Remove Gate 5, remove completenessFlag parameter
2. `modules/robot/core/StreamStateMachine.cs` - Remove completenessFlag argument, remove Check 1
3. `RobotCore_For_NinjaTrader/Execution/RiskGate.cs` - Sync changes
4. `RobotCore_For_NinjaTrader/StreamStateMachine.cs` - Sync changes

## Testing Considerations

- Verify orders still submit correctly when intent is complete
- Verify execution adapter catches incomplete intents (null checks)
- Verify RiskGate still works with remaining gates (now 5 gates instead of 6)
