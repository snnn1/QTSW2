# Forced Flatten Canonical Event Audit

**Date:** 2026-03-12  
**Objective:** Ensure both forced flatten execution paths (immediate same-cycle and emergency fallback) emit sufficient canonical events for replay to reconstruct the full lifecycle.

---

## Replay Reconstruction Requirements

Replay must be able to reconstruct:

1. **Forced flatten was triggered** — session-close risk boundary reached
2. **Flatten was submitted** — order sent to broker (or same-cycle drain executed)
3. **Broker position became flat** — verification that exposure was closed
4. **Terminal state was reached** — slot marked for reentry, journal persisted

---

## Execution Paths

| Path | When Used | Description |
|------|-----------|-------------|
| **Immediate** | `RequestSessionCloseFlattenImmediate` supported and succeeds | Enqueue cancel+flatten NT actions, drain same-cycle under lock |
| **Emergency** | Immediate returns `null` or fails | Direct `EmergencyFlatten` bypass |
| **Phase 2 (legacy)** | `ForcedFlattenEnqueuedAtUtc` set last tick | Queued flatten completed this tick via `DrainNtActions` |

---

## Canonical Events Emitted (ExecutionEventWriter)

All events write to `automation/logs/execution_events/{trading_date}/{instrument}.jsonl`.

### 1. SESSION_FORCED_FLATTEN_TRIGGERED

- **When:** Start of `HandleForcedFlatten` (post-entry case, after OriginalIntentId resolved)
- **Path:** All three
- **Payload:** `stream`, `intent_id`, `instrument`
- **Purpose:** Replay knows forced flatten was triggered for this stream

### 2. SESSION_FORCED_FLATTEN_SUBMITTED

- **When:** Immediately before flatten execution
- **Path:** 
  - **Immediate:** Before `RequestSessionCloseFlattenImmediate` (payload: `path=immediate`)
  - **Emergency:** Before `emergencyFlatten` (payload: `path=emergency`)
  - **Phase 2:** When Phase 2 completion detected (payload: `path=phase2_queued`)
- **Payload:** `path`, `stream`, `intent_id`
- **Purpose:** Replay knows flatten was submitted and which path was used

### 3. SESSION_FORCED_FLATTENED

- **When:** In `CompleteForcedFlattenPersist` (all paths converge here)
- **Path:** All three
- **Payload:** `execution_interrupted`, `original_intent_id`, `position_flat`, `slot_remains_active_for_reentry`
- **Family:** TERMINAL
- **Purpose:** Replay knows broker position is flat and terminal state reached

---

## Event Sequence by Path

### Immediate Path

```
SESSION_FORCED_FLATTEN_TRIGGERED
SESSION_FORCED_FLATTEN_SUBMITTED (path=immediate)
[RequestSessionCloseFlattenImmediate executes: cancel + flatten, DrainNtActions]
SESSION_FORCED_FLATTENED
```

### Emergency Path

```
SESSION_FORCED_FLATTEN_TRIGGERED
SESSION_FORCED_FLATTEN_SUBMITTED (path=emergency)
[emergencyFlatten executes]
[CancelIntentOrders if NinjaTraderSimAdapter]
SESSION_FORCED_FLATTENED
```

### Phase 2 (Legacy) Path

```
SESSION_FORCED_FLATTEN_TRIGGERED
SESSION_FORCED_FLATTEN_SUBMITTED (path=phase2_queued)
[CompleteForcedFlattenPersist]
SESSION_FORCED_FLATTENED
```

---

## Robot Log Events (Operational)

In addition to canonical events, robot log (`logs/robot/robot_*.jsonl`) continues to receive:

- `FORCED_FLATTEN_TRIGGERED` (engine level, once per session)
- `NT_ACTION_START` / `NT_ACTION_SUCCESS` (immediate path, from StrategyThreadExecutor)
- `SESSION_FORCED_FLATTENED` (from `CompleteForcedFlattenPersist`)
- `FORCED_FLATTEN_EXPOSURE_REMAINING` (if position not flat)
- `FORCED_FLATTEN_IMMEDIATE_FAILED_EMERGENCY_FALLBACK` (when immediate fails)

---

## Files Touched

| File | Change |
|------|--------|
| `ExecutionEventFamilies.cs` | Added `SESSION_FORCED_FLATTEN_TRIGGERED`, `SESSION_FORCED_FLATTEN_SUBMITTED` |
| `StreamStateMachine.cs` | Added `_eventWriter`, emit all three events in both paths |
| `RobotEngine.cs` | Pass `eventWriter: _eventWriter` when creating streams |

---

## Verification

1. **Immediate path:** Run session through close with NinjaTrader Sim; check `automation/logs/execution_events/{date}/{instrument}.jsonl` for the three event types.
2. **Emergency path:** Use adapter that returns `null` from `RequestSessionCloseFlattenImmediate` (e.g. Live adapter); verify `path=emergency` in SUBMITTED payload.
3. **Replay:** Incident pack extractor / replay loader should be able to reconstruct forced flatten lifecycle from canonical events alone.
