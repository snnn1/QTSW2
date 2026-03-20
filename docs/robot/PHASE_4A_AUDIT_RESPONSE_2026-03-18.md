# Phase 4A Audit Response — Execution Safety Layer

**Date**: 2026-03-18  
**Audit verdict**: Correct direction, minimal scope respected, but only partially complete

---

## 1. Corrections to Audit Findings

### A. Exposure Without Protection — Audit Said "Missing Timeout"

**Audit claim**: "T = ?" not defined; "conditionally safe, not guaranteed."

**Reality**: Time-bounded check exists and is enforced:

| Item | Implementation |
|------|----------------|
| **T** | `UNPROTECTED_POSITION_TIMEOUT_SECONDS = 10` (NinjaTraderSimAdapter) |
| **"Confirmed"** | `ProtectiveStopAcknowledged` / `ProtectiveTargetAcknowledged` = true when broker returns `OrderState.Accepted` (order live on broker) |
| **Trigger** | `CheckUnprotectedPositions` runs on every execution update; if entry filled and protectives not acknowledged within 10s → `FailClosed` → flatten |
| **Event** | `UNPROTECTED_POSITION_TIMEOUT` → `UNPROTECTED_POSITION_TIMEOUT_FLATTENED` |

**Gap acknowledged**: If protectives *silently* fail to place (no OrderUpdate at all), we rely on the 10s timeout. The timeout is measured from `EntryFillTime`; if no protective OrderUpdate ever arrives, we flatten. So we are covered for "protectives never confirm."

**Assessment**: ✅ Time-safe, not just event-safe.

---

### C. Persistent Reconciliation Mismatch — Audit Said "No Persistence Window"

**Audit claim**: "No persistence window"; "event-driven, immediate policy evaluation."

**Reality**: MismatchEscalationCoordinator has explicit time-based escalation:

| Stage | Threshold | Action |
|-------|-----------|--------|
| DETECTED | First observation | Log RECONCILIATION_MISMATCH_DETECTED |
| PERSISTENT_MISMATCH | `MISMATCH_PERSISTENT_THRESHOLD_MS = 10000` | Block instrument, RECONCILIATION_MISMATCH_BLOCKED |
| FAIL_CLOSED | `MISMATCH_FAIL_CLOSED_THRESHOLD_MS = 30000` | RECONCILIATION_MISMATCH_FAIL_CLOSED, no auto-clear |

`PersistenceMs = utcNow - FirstDetectedUtc`; escalation only occurs when mismatch persists for the full window.

**Oscillation risk**: If mismatch clears and reappears within 2 consecutive audit ticks, `ConsecutiveCleanPassCount` can clear the instrument before 10s. Edge case; escalation still occurs for sustained mismatch.

**Assessment**: ✅ Time-hardened (10s → block, 30s → fail-closed).

---

## 2. Audit Findings We Agree With

### B. Lifecycle Stall — Weak Coverage

**Audit claim**: "No explicit timeout → stall → action"; "event-driven, not time-bounded."

**Reality**:

| Mechanism | Location | Behavior |
|-----------|----------|----------|
| **EXECUTION_COMMAND_STALLED** | IEA, `COMMAND_STALL_CRITICAL_MS = 8000` | Queue work exceeds 8s → emit event → `_onEnqueueFailureCallback` → `StandDownStreamsForInstrument` |
| **ORDER_STUCK_DETECTED** | Watchdog (Python) | Derived from `ORDER_SUBMIT_SUCCESS` without fill/cancel within 120s (entry) or 90s (protective). **Alert only** — robot does not consume this; no instrument block. |

**Gap**: Entry order stuck at broker (submitted, never fills, never cancels) is detected by the watchdog and alerted, but the **robot does not block** on it. The robot has no in-process timer for "entry submitted → max wait → stall → block."

**Assessment**: ⚠️ Correct — B is weak for entry/protective lifecycle stall at the robot level.

---

### D. Flatten Failure — Edge Case

**Audit**: "Flatten partially succeeds (qty reduced but not zero) → FORCED_FLATTEN_EXPOSURE_REMAINING. Does system attempt another flatten? If NOT: system is frozen (good) but exposure still exists (danger remains)."

**Reality**: We do **not** retry flatten after FORCED_FLATTEN_EXPOSURE_REMAINING. We freeze and call `OnForcedFlattenFailed`. Correct fail-closed behavior.

**Recommendation adopted**: Add `MANUAL_FLATTEN_REQUIRED` log when FORCED_FLATTEN_EXPOSURE_REMAINING fires — communication improvement, not logic.

---

## 3. Summary: What Is Truly Safe vs Conditionally Safe

| Failure Class | Audit Said | Corrected Assessment |
|---------------|------------|------------------------|
| **A** | ⚠️ Partial, missing timeout | ✅ **Strong** — 10s timeout, broker-acknowledged protectives |
| **B** | ⚠️ Weak, no time-based | ⚠️ **Partial** — IEA 8s stall → block ✓; entry/protective stuck at broker → watchdog alert only, no robot block |
| **C** | ⚠️ Partial, no persistence | ✅ **Strong** — 10s persistent, 30s fail-closed |
| **D** | ✅ Strong | ✅ **Strong** — freeze + MANUAL_FLATTEN_REQUIRED log added |

---

## 4. Minimal Fixes Applied

1. **MANUAL_FLATTEN_REQUIRED** (optional, communication): When `FORCED_FLATTEN_EXPOSURE_REMAINING` fires, emit an additional log line with `MANUAL_FLATTEN_REQUIRED` to make operator action explicit. Added to StreamStateMachine (both NT and modules), RobotEventTypes, and operator_snapshot (action_required=FLATTEN, confidence=HIGH).

2. **No new timing logic added**: A and C already have bounded timers. B's gap (entry stuck at broker) would require robot-side tracking of submission time and max-wait enforcement. Deferred — watchdog provides operator visibility; adding robot-side stall detection would increase complexity.

---

## 5. Intentionally Deferred

- **B: Robot-side entry/protective stall timer**: Would require tracking `submitted_at` per intent and enforcing max wait (e.g. 30s) before block. Watchdog already detects and alerts; robot block would be redundant for supervised use but adds code paths. Defer to Phase 4B or later if needed.
