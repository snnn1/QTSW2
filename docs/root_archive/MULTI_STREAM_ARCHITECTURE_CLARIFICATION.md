# Multi-Stream Architecture Clarification

## Your Question: Will ES1/ES2 Streams Be Affected?

**Answer: NO - ES1 and ES2 streams work correctly with the fix.**

## Architecture: One Strategy Instance = Multiple Streams

### Current Architecture

```
Single Strategy Instance (MES chart):
  ├── RobotEngine (one instance)
  │   ├── StreamStateMachine("ES1", ...)  ← ES1 stream
  │   └── StreamStateMachine("ES2", ...)  ← ES2 stream
  │
  └── NinjaTraderSimAdapter (one instance)
      └── _orderMap (shared across all streams)
          ├── intentId_ES1 → OrderInfo
          └── intentId_ES2 → OrderInfo
```

**Key Points:**
1. **One strategy instance** handles **multiple streams** (ES1, ES2, etc.)
2. Both streams use the **same execution instrument** (MES)
3. Orders are tracked by **intent ID**, which **includes stream ID**

### Intent ID Structure

Intent IDs include the stream ID:
```csharp
ExecutionJournal.ComputeIntentId(
    TradingDate,    // "2026-01-30"
    Stream,         // "ES1" or "ES2" ← DISTINGUISHES STREAMS
    Instrument,     // "ES"
    Session,        // "S1" or "S2"
    SlotTimeChicago,// "07:30"
    Direction,      // "Long" or "Short"
    EntryPrice,     // ...
    ...
)
```

**Example Intent IDs:**
- ES1 Long: `hash("2026-01-30", "ES1", "ES", "S1", "07:30", "Long", ...)`
- ES2 Long: `hash("2026-01-30", "ES2", "ES", "S1", "07:30", "Long", ...)`

These are **different** intent IDs, so orders are tracked separately.

## How the Fix Works

### Instrument Filtering

```csharp
if (e.Order?.Instrument != Instrument)
{
    return; // Ignore orders from other strategy instances
}
```

**For ES1/ES2 streams:**
- Both streams submit orders on **same instrument** (MES)
- Both orders **pass the instrument filter** ✅
- Both orders are processed by the **same adapter instance** ✅
- Orders are tracked separately by **intent ID** (includes stream ID) ✅

**Result:** ES1 and ES2 orders are handled correctly, no interference.

## Edge Case: Invalid Deployment

### Scenario: Two Strategy Instances on Same Instrument

```
Instance A (MES chart):
  └── RobotEngine → StreamStateMachine("ES1")
  
Instance B (MES chart, same account):
  └── RobotEngine → StreamStateMachine("ES1")
```

**Problem:**
- Both instances have `Instrument = MES`
- Both instances receive ALL order updates
- Both instances process each other's orders
- Orders tracked separately, but both instances act on them

**This is an invalid deployment** - you shouldn't run two strategy instances on the same instrument/account.

**Current Fix Behavior:**
- Both instances process each other's orders (expected for invalid deployment)
- No errors, but orders may be processed twice

**Future Hardening (Your Recommendation):**
```csharp
// Guard: (account, executionInstrument) must be unique
private static readonly HashSet<(string account, string instrument)> _activeInstances = new();

if (!_activeInstances.Add((Account.Name, Instrument.FullName)))
{
    Log("CRITICAL: Duplicate strategy instance detected - stand down", LogLevel.Error);
    _initFailed = true;
    return;
}
```

## Summary

| Scenario | Fix Behavior | Status |
|----------|-------------|--------|
| ES1 + ES2 streams (same instance) | ✅ Works correctly | **CORRECT** |
| MES + MNG instances (different instruments) | ✅ Works correctly | **CORRECT** |
| MES + MES instances (same instrument) | ⚠️ Both process orders | **Invalid deployment** |

**Your ES1/ES2 streams are safe** - the fix handles them correctly because:
1. They're in the same strategy instance
2. They share the same adapter
3. Orders are distinguished by intent ID (includes stream ID)

The edge case you mentioned (same instrument, multiple instances) is a deployment issue, not a bug in the fix.
