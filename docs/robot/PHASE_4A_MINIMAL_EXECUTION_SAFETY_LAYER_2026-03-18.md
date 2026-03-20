# Phase 4A: Minimal Execution Safety Layer — Implementation Summary

**Date**: 2026-03-18  
**Scope**: Narrow, high-value safety checks for supervised prop-firm/live use  
**Constraint**: No redesign, no broad recovery systems, no architectural complexity increase

---

## 1. Target Failure Classes and Implementation Status

| Class | Description | Implementation | Status |
|-------|-------------|----------------|--------|
| **A** | Exposure without verified protection | UNPROTECTED_POSITION_TIMEOUT (10s) → flatten | Existing |
| **B** | Entry/protective lifecycle stall | ORDER_STUCK_DETECTED (120s/90s), EXECUTION_COMMAND_STALLED (8s) | Existing |
| **C** | Persistent reconciliation mismatch | MismatchEscalationCoordinator (10s→30s) → instrument blocked | Existing |
| **D** | Flatten attempted but exposure remains | OnForcedFlattenFailed → StandDownStreamsForInstrument | **Added** |

---

## 2. Exact Safety Rules Added (Phase 4A)

### D. Flatten Failure / Exposure Remaining

**Rule**: When forced flatten fails after retries, or flatten verify detects broker position still exists, the system must:

1. Emit a clear CRITICAL event (`FORCED_FLATTEN_FAILED` or `FORCED_FLATTEN_EXPOSURE_REMAINING`)
2. Call `OnForcedFlattenFailed(instrument, reason, utcNow)` on the engine
3. Engine invokes `StandDownStreamsForInstrument(instrument, utcNow, reason)` to freeze the instrument
4. No silent continuation — instrument remains blocked until operator intervention

**Trigger points** (StreamStateMachine):

- After `MAX_FLATTEN_RETRIES` (3) attempts fail → `FORCED_FLATTEN_FAILED`
- After flatten submit + verify cycle: broker position still nonzero → `FORCED_FLATTEN_EXPOSURE_REMAINING`

---

## 3. Thresholds Used

| Check | Threshold | Location |
|-------|-----------|----------|
| **A. Exposure without protection** | `UNPROTECTED_POSITION_TIMEOUT_SECONDS = 10` | NinjaTraderSimAdapter |
| **B. Entry order stuck** | `ORDER_STUCK_ENTRY_THRESHOLD_SECONDS = 120` | Watchdog config |
| **B. Protective order stuck** | `ORDER_STUCK_PROTECTIVE_THRESHOLD_SECONDS = 90` | Watchdog config |
| **B. IEA command stall** | `COMMAND_STALL_CRITICAL_MS = 8000` | InstrumentExecutionAuthority |
| **C. Mismatch persistent** | `MISMATCH_PERSISTENT_THRESHOLD_MS = 10000` | MismatchEscalationModels |
| **C. Mismatch fail-closed** | `MISMATCH_FAIL_CLOSED_THRESHOLD_MS = 30000` | MismatchEscalationModels |
| **D. Flatten retries** | `MAX_FLATTEN_RETRIES = 3` | StreamStateMachine |

---

## 4. Events Used / Added

### Existing Events (unchanged)

| Event | Severity | Failure Class |
|-------|----------|---------------|
| `UNPROTECTED_POSITION_TIMEOUT` | CRITICAL | A |
| `UNPROTECTED_POSITION_TIMEOUT_FLATTENED` | CRITICAL | A |
| `ORDER_STUCK_DETECTED` | WARN | B |
| `EXECUTION_COMMAND_STALLED` | CRITICAL | B |
| `RECONCILIATION_MISMATCH_DETECTED` | WARN | C |
| `RECONCILIATION_MISMATCH_PERSISTENT` | CRITICAL | C |
| `RECONCILIATION_MISMATCH_FAIL_CLOSED` | CRITICAL | C |
| `RECONCILIATION_MISMATCH_BLOCKED` | WARN | C |
| `FORCED_FLATTEN_FAILED` | CRITICAL | D |
| `FORCED_FLATTEN_EXPOSURE_REMAINING` | CRITICAL | D |

### New Behavior (no new event types)

- `OnForcedFlattenFailed` callback added to RobotEngine; NinjaTrader implementation calls `StandDownStreamsForInstrument`.
- Watchdog operator snapshot: `FORCED_FLATTEN_EXPOSURE_REMAINING` added to instrument snapshot events; yields `action_required=FLATTEN`, `confidence=HIGH`.

---

## 5. Files Changed

| File | Change |
|------|--------|
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | Added `OnForcedFlattenFailed` → `StandDownStreamsForInstrument` |
| `RobotCore_For_NinjaTrader/StreamStateMachine.cs` | Call `_engine?.OnForcedFlattenFailed` after FORCED_FLATTEN_FAILED and FORCED_FLATTEN_EXPOSURE_REMAINING |
| `modules/robot/core/RobotEngine.cs` | Added no-op `OnForcedFlattenFailed` (harness/skeleton) |
| `modules/robot/core/StreamStateMachine.cs` | Same `OnForcedFlattenFailed` calls as NT |
| `modules/watchdog/state/operator_snapshot.py` | Added `FORCED_FLATTEN_EXPOSURE_REMAINING` to instrument snapshot; `action_required=FLATTEN`, `confidence=HIGH` when FORCED_FLATTEN_FAILED or FORCED_FLATTEN_EXPOSURE_REMAINING |
| `tests/test_operator_snapshot.py` | Added `test_phase4a_forced_flatten_exposure_remaining_critical_blocked` |

---

## 6. Tests Added

| Test | Purpose |
|------|---------|
| `test_phase4a_forced_flatten_exposure_remaining_critical_blocked` | Verifies `FORCED_FLATTEN_EXPOSURE_REMAINING` yields CRITICAL, action_required=FLATTEN, action_label="FLATTEN NOW", confidence=HIGH |

---

## 7. Intentionally Left for Later

- **A/B/C unit tests**: Existing coverage via integration/replay; no new C# unit tests added for A/B/C in this phase.
- **Advanced autonomous recovery**: Out of scope.
- **Broker polling layers**: Out of scope.
- **Operator workflow automation**: Out of scope.
- **New event taxonomy**: Existing events sufficient; no `EXECUTION_SAFETY_*` events added.

---

## 8. Success Criteria Met

After this phase, the robot can say:

| Statement | Mechanism |
|-----------|-----------|
| "I have exposure but no valid protection → flatten" | UNPROTECTED_POSITION_TIMEOUT (10s) + ProtectiveCoverageCoordinator |
| "This order lifecycle is stuck → block" | ORDER_STUCK_DETECTED, EXECUTION_COMMAND_STALLED → StandDownStreamsForInstrument |
| "This mismatch is not resolving → freeze" | MismatchEscalationCoordinator → RECONCILIATION_MISMATCH_FAIL_CLOSED → instrument blocked |
| "Flatten failed → remain blocked and alert" | OnForcedFlattenFailed → StandDownStreamsForInstrument; operator snapshot CRITICAL + FLATTEN NOW |

---

## 9. Build Verification

- `RobotCore_For_NinjaTrader/Robot.Core.csproj` — builds successfully
- `modules/robot/harness/Robot.Harness.csproj` — builds successfully
- `tests/test_operator_snapshot.py` — 27 tests pass (including `test_phase4a_forced_flatten_exposure_remaining_critical_blocked`)
