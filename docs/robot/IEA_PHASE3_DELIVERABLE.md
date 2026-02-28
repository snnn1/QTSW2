# IEA + Break-Even Hardening — Phase 3 Deliverable

**Date**: 2026-02-26

---

## Files Modified

| File | Changes |
|------|---------|
| `RobotCore_For_NinjaTrader/Execution/IIEAOrderExecutor.cs` | Added `SetProtectionStateWorkingForAdoptedStop(string intentId)` |
| `RobotCore_For_NinjaTrader/Execution/InstrumentExecutionAuthority.NT.cs` | ScanAndAdoptExistingProtectives: call `Executor.SetProtectionStateWorkingForAdoptedStop(intentId)` when adopting STOP |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.cs` | SetProtectionState overload with reason; PruneIntentState helper; _firstMissingStopUtcByIntent → ConcurrentDictionary; Part 3 constants and _firstEnqueuedUtcByIntent; SetProtectionStateWorkingForAdoptedStop impl; EnqueueNtAction records firstEnqueuedUtc |
| `RobotCore_For_NinjaTrader/Execution/NinjaTraderSimAdapter.NT.cs` | Replace SetProtectionState(None) with PruneIntentState at terminal paths; CheckEnqueuedBacklog; _firstMissingStopUtcByIntent usages for ConcurrentDictionary |
| `RobotCore_For_NinjaTrader/Execution/ReplayExecutor.cs` | Added no-op `SetProtectionStateWorkingForAdoptedStop` |
| `RobotCore_For_NinjaTrader/RobotEventTypes.cs` | Added BE_PROTECTION_STATE_PRUNED, PROTECTION_ENQUEUED_BACKLOG_WARN, PROTECTION_ENQUEUED_BACKLOG_TIMEOUT |

---

## Methods Modified

| Method | Change |
|--------|--------|
| `ScanAndAdoptExistingProtectives` | When adopting STOP, call `Executor.SetProtectionStateWorkingForAdoptedStop(intentId)` |
| `SetProtectionState(string, ProtectionState, string?)` | Added optional `reason`; when state ≠ Enqueued, remove from _firstEnqueuedUtcByIntent |
| `EnqueueNtAction` | Record `_firstEnqueuedUtcByIntent.TryAdd(intentId, UtcNow)` when enqueuing SUBMIT_PROTECTIVES |
| `PruneIntentState` (new) | TryRemove from _protectionStateByIntent, _firstMissingStopUtcByIntent, _firstEnqueuedUtcByIntent; log BE_PROTECTION_STATE_PRUNED |
| `FlattenIntentReal` | Call PruneIntentState instead of SetProtectionState(None) |
| `ProcessExecutionUpdateContinuation` (exit fill) | Call PruneIntentState instead of SetProtectionState(None) |
| `ExecuteSubmitProtectives` | Call PruneIntentState instead of SetProtectionState(None) on failure paths |
| `CheckAllInstrumentsForFlatPositions` | Call PruneIntentState instead of SetProtectionState(None) |
| `HandleEntryFill` (non-NT path) | Call PruneIntentState instead of SetProtectionState(None) on protective failure |
| `EvaluateBreakEvenCoreImpl` | Call CheckEnqueuedBacklog at start |
| `CheckEnqueuedBacklog` (new) | Iterate _firstEnqueuedUtcByIntent; if Enqueued and elapsed ≥ WARN_SEC log WARN (rate-limited 30s); if flag and elapsed ≥ TIMEOUT_SEC log CRITICAL and FailClosed |

---

## New Events

| Event | Level | When |
|-------|-------|------|
| `BE_PROTECTION_STATE_PRUNED` | DEBUG | Intent removed from BE state (intent_id, reason) |
| `PROTECTION_ENQUEUED_BACKLOG_WARN` | WARN | Enqueued > 5s (rate-limited 30s per intent) |
| `PROTECTION_ENQUEUED_BACKLOG_TIMEOUT` | CRITICAL | Enqueued > 30s when feature flag ON; triggers FailClosed |

---

## Behavioral Differences

1. **Restart with open position + working stop**: ProtectionState set to Working when stop adopted; no BE_STOP_VISIBILITY_TIMEOUT.
2. **Terminal intents**: ProtectionState and _firstMissingStopUtcByIntent entries removed (pruned) instead of set to None; prevents growth and stale state.
3. **Enqueued backlog** (Part 3, flag OFF by default): WARN at 5s (rate-limited); if flag ON, CRITICAL + FailClosed at 30s.

---

## Edge Cases

- **ScanAndAdoptExistingProtectives**: Only sets Working when adopting STOP. TARGET-only adoption does not set Working (correct).
- **PruneIntentState**: Logs only when _protectionStateByIntent actually had an entry (removed == true).
- **Part 3**: ENABLE_PROTECTION_ENQUEUED_TIMEOUT = false; no behavioral change unless toggled.
- **_firstMissingStopUtcByIntent**: Changed to ConcurrentDictionary for thread-safe Remove; PruneIntentState and BE path both use TryRemove.

---

## Not Changed

- ExecutionUpdateRouter
- Dedup logic
- IEA worker threading model
- Break-even trigger calculation
- NT action queue drain scheduling
- OrderMap insertion timing
