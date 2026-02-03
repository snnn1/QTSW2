# Slot Persistence Integration Notes

**Status**: Implementation Complete - Integration Testing Required

These are critical integration points to validate during testing. The implementation is complete, but these scenarios must be explicitly tested.

---

## 1. Forced Flatten Trigger Wiring

### Implementation Status
- ✅ `HandleForcedFlatten()` method implemented in `StreamStateMachine.cs`
- ⚠️ **TODO**: Wire up trigger in `RobotEngine.Tick()` at forced flatten time (15:55 CT)

### Integration Requirements

**When integrating `HandleForcedFlatten()` call:**

1. **Fire Once Per Slot Lifecycle**
   - Guard with `ExecutionInterruptedByClose == false` check
   - Only call if `SlotStatus == ACTIVE` (already implemented in method)
   - Prevent double-flatten loops

2. **Timing Order**
   - Ensure forced flatten check runs **BEFORE** any commit or expiry checks in that tick
   - Current order in `Tick()`:
     1. Committed check
     2. **Slot expiry check** (should run before forced flatten)
     3. **Forced flatten check** (should run here)
     4. Other state transitions

3. **Integration Point**
   - Add forced flatten time check in `RobotEngine.Tick()`
   - For each active stream, check if `now >= forcedFlattenTimeUtc` (15:55 CT)
   - Call `streamStateMachine.HandleForcedFlatten(utcNow)` if conditions met

### Example Integration Code (for reference)
```csharp
// In RobotEngine.Tick(), after expiry check but before other transitions:
var forcedFlattenTimeUtc = _time.ConvertChicagoLocalToUtc(_activeTradingDate.Value, "15:55");
if (utcNow >= forcedFlattenTimeUtc)
{
    foreach (var stream in _streams.Values)
    {
        if (!stream.Committed && 
            stream.SlotStatus == SlotStatus.ACTIVE && 
            !stream.ExecutionInterruptedByClose)
        {
            stream.HandleForcedFlatten(utcNow);
        }
    }
}
```

---

## 2. Re-Entry Timing Race (Market Open)

### Implementation Status
- ✅ `CheckMarketOpenReentry()` implemented with time-based gate
- ⚠️ **TODO**: Enhance "market live" signal detection

### Current Implementation
- Time gate: `now >= RangeStartChicagoTime` (session open)
- Market live signal: Currently assumes market is live if past session open
- **LIMITATION**: No explicit tick observation tracking

### Integration Testing Requirements

**Validate re-entry does NOT attempt:**

1. **Before First Live Tick**
   - Test: Start strategy exactly at market open time
   - Expected: Wait for at least one tick before attempting re-entry
   - Current: May attempt immediately if time gate passes

2. **During Connection Warm-Up**
   - Test: Restart during market hours, connection still establishing
   - Expected: Wait for connection stable + first tick
   - Current: May attempt if time gate passes

3. **Reliability for Edge Cases**
   - **Thin markets**: May have long gaps between ticks
   - **Overnight sessions**: May have no ticks for extended periods
   - **Reconnect scenarios**: Connection drops and reconnects mid-session

### Recommended Enhancement (Future)
```csharp
// Add explicit tick observation tracking
private DateTimeOffset? _lastTickObservedUtc;

// In Tick() or OnMarketData():
_lastTickObservedUtc = utcNow;

// In CheckMarketOpenReentry():
var marketLive = _lastTickObservedUtc.HasValue && 
                 (utcNow - _lastTickObservedUtc.Value).TotalSeconds < 60; // Within last 60 seconds
if (!marketLive) return; // Wait for market to be live
```

### Testing Scenarios

1. **Market Open Re-entry Test**
   - Setup: Slot interrupted by forced flatten on Day 1
   - Action: Restart strategy exactly at market open (e.g., 08:00 CT)
   - Verify: Re-entry waits for first tick, then submits MARKET order

2. **Thin Market Test**
   - Setup: Slot interrupted, market opens
   - Action: Simulate 5-minute gap with no ticks
   - Verify: Re-entry does not attempt until tick observed

3. **Reconnect Test**
   - Setup: Slot interrupted, market open, connection drops
   - Action: Reconnect 10 minutes later
   - Verify: Re-entry waits for new tick after reconnect

---

## 3. Carry-Forward Correctness on Restart

### Implementation Status
- ✅ `SlotInstanceKey` persistence implemented
- ✅ Deterministic `ReentryIntentId` generation implemented
- ✅ Journal carry-forward mechanism implemented in `UpdateTradingDate()`
- ⚠️ **TODO**: Explicit restart lifecycle test

### Integration Testing Requirements

**Full Lifecycle Test:**

1. **Day 1 Entry**
   - Slot enters on Day 1 (e.g., 2026-02-01)
   - Entry fills, protective orders submitted
   - `SlotInstanceKey` = `"{Stream}_{SlotTimeChicago}_2026-02-01"`
   - `OriginalIntentId` stored in journal

2. **Forced Flatten at Close**
   - At 15:55 CT, forced flatten triggers
   - `HandleForcedFlatten()` called
   - `ExecutionInterruptedByClose = true`
   - `OriginalIntentId` preserved
   - Slot remains `ACTIVE` (not committed)

3. **Trading Date Rollover**
   - Date rolls to Day 2 (e.g., 2026-02-02)
   - `UpdateTradingDate()` detects post-entry active slot
   - Clones-forward journal preserving `SlotInstanceKey`
   - `PriorJournalKey` = `"2026-02-01_{Stream}"`
   - State preserved (no `ResetDailyState()`)

4. **Restart NinjaTrader**
   - Strategy restarts on Day 2
   - Journal loaded for Day 2
   - `SlotInstanceKey` loaded: `"{Stream}_{SlotTimeChicago}_2026-02-01"` (preserved!)
   - `ExecutionInterruptedByClose = true` (preserved!)
   - `OriginalIntentId` preserved
   - `ReentryIntentId` computed deterministically: `"{SlotInstanceKey}_REENTRY"`

5. **Market Opens**
   - Market opens on Day 2
   - `CheckMarketOpenReentry()` triggers
   - Loads bracket levels from `ExecutionJournalEntry` via `OriginalIntentId`
   - Submits MARKET order with `ReentryIntentId` (distinct from `OriginalIntentId`)
   - Re-entry fills exactly once (idempotency via `ReentrySubmitted` flag)

### Validation Points

**Test explicitly validates:**

1. **SlotInstanceKey Persistence**
   - ✅ Same `SlotInstanceKey` across Day 1 → Day 2 → Restart
   - ✅ Format: `"{Stream}_{SlotTimeChicago}_{StartTradingDate}"`
   - ✅ Never overwritten after initial generation

2. **Deterministic ReentryIntentId**
   - ✅ Same `ReentryIntentId` computed on restart
   - ✅ Derived from `SlotInstanceKey` (not `OriginalIntentId`)
   - ✅ Does NOT include TradingDate (stable across rollover)

3. **Journal Reload Logic**
   - ✅ Day 2 journal contains all lifecycle fields from Day 1
   - ✅ `PriorJournalKey` references Day 1 journal
   - ✅ Post-entry state preserved (ranges, entry tracking, etc.)

### Test Script (Manual)

```csharp
// Day 1: Entry fills
// Verify: SlotInstanceKey = "NQ2_09:30_2026-02-01"
// Verify: OriginalIntentId stored

// Day 1: Forced flatten at 15:55 CT
// Verify: ExecutionInterruptedByClose = true
// Verify: SlotStatus = ACTIVE (not DONE)
// Verify: OriginalIntentId preserved

// Day 2: Date rollover
// Verify: SlotInstanceKey preserved = "NQ2_09:30_2026-02-01"
// Verify: PriorJournalKey = "2026-02-01_NQ2"
// Verify: TradingDate updated = "2026-02-02"
// Verify: No ResetDailyState() called

// Day 2: Restart strategy
// Verify: Journal loaded for 2026-02-02
// Verify: SlotInstanceKey = "NQ2_09:30_2026-02-01" (preserved!)
// Verify: ExecutionInterruptedByClose = true (preserved!)
// Verify: ReentryIntentId computed = "NQ2_09:30_2026-02-01_REENTRY"

// Day 2: Market opens
// Verify: CheckMarketOpenReentry() triggers
// Verify: Bracket levels loaded from OriginalIntentId
// Verify: MARKET order submitted with ReentryIntentId
// Verify: Re-entry fills exactly once
// Verify: Protective bracket submitted
// Verify: ProtectionAccepted = true
// Verify: ExecutionInterruptedByClose cleared
```

---

## Testing Checklist

### Phase 1: Unit Tests (Code-Level)
- [ ] `CalculateNextSlotTimeUtc()` handles Friday→Monday skip correctly
- [ ] `HandleForcedFlatten()` never calls `Commit()` for post-entry slots
- [ ] `UpdateTradingDate()` clones-forward post-entry journals correctly
- [ ] `ReentryIntentId` generation is deterministic

### Phase 2: Integration Tests (Component-Level)
- [ ] Forced flatten trigger fires once per slot lifecycle
- [ ] Re-entry timing respects market live signal
- [ ] Journal carry-forward preserves `SlotInstanceKey` across restart

### Phase 3: End-to-End Tests (System-Level)
- [ ] Full lifecycle: Entry → Forced Flatten → Rollover → Restart → Re-entry
- [ ] Idempotency: Re-entry prevented across restarts
- [ ] Fail-closed: Re-entry safety failures result in `FAILED_RUNTIME`

---

## Known Limitations

1. **Market Live Signal**: Currently assumes market is live if past session open. Should be enhanced with explicit tick observation.

2. **Forced Flatten Trigger**: Not yet wired up in `RobotEngine.Tick()`. Must be integrated before production use.

3. **Re-entry Order Submission**: `CheckMarketOpenReentry()` logs re-entry attempt but does not actually submit order. Execution adapter must call `HandleReentryFill()` on fill.

4. **Protective Order Submission**: Re-entry protective orders must be submitted by execution adapter, which should call `HandleReentryProtectionAccepted()` on acceptance.

---

## Related Documentation

- `SLOT_PERSISTENT_STRATEGY_AUDIT.md` - Initial audit identifying day-scoped vs slot-persistent mismatch
- `FORCED_FLATTEN_BEHAVIOR.md` - Original forced flatten behavior documentation
- `c:\Users\jakej\.cursor\plans\slot-persistent_post-entry_implementation_assessment_e8e07296.plan.md` - Full implementation plan
