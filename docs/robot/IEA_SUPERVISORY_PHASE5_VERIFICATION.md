# Phase 5 Supervisory: Verification Report

Verification of the four hardening checks before calling Phase 5 fully hardened.

---

## 1. Resume Semantics from SUSPENDED and HALTED

### Question
Is it impossible for cooldown expiry, operator ack, clearing a kill switch, or incidental state refresh to move an instrument back to ACTIVE if:
- recovery/bootstrap is still unresolved
- unowned live orders remain
- registry integrity is still failing
- flatten is still in progress

### Findings

**Paths that can transition to ACTIVE:**
- `TryExpireCooldown`: COOLDOWN → ACTIVE only. Calls `CanResumeSupervisoryActive` before transitioning.
- `AcknowledgeInstrument`: AWAITING_OPERATOR_ACK → ACTIVE only. Calls `CanResumeSupervisoryActive` before transitioning.
- **SUSPENDED and HALTED**: No code path transitions these to ACTIVE. Valid transitions exist in the state table, but no implementation performs them. Once SUSPENDED or HALTED, the instrument stays there until process restart.

**CanResumeSupervisoryActive checks (lines 278–293):**
```csharp
if (IsInRecovery) return false;                    // ✓ recovery/bootstrap unresolved
if (_flattenLatchByInstrument.ContainsKey(...)) return false;  // ✓ flatten in progress
var workingUnowned = ... WHERE WORKING AND UNOWNED;
if (workingUnowned.Count > 0) return false;        // ✓ unowned live orders
// COOLDOWN: elapsed >= 60s
```

**Registry integrity:** Not checked directly. When registry divergence is detected, `RequestRecovery` is called, which transitions to RECOVERY_PENDING. So `IsInRecovery` is true and resume is blocked.

**Verdict:** ✓ **Resume semantics are correct.** Both resume paths gate on `CanResumeSupervisoryActive`, which blocks when recovery unresolved, flatten in progress, or unowned orders exist. Registry integrity failure implies recovery state, so it is indirectly covered.

**Gap:** There is no way to transition SUSPENDED or HALTED back to ACTIVE. That is fail-closed (no accidental resume) but means operator cannot clear these states without restart. Per-instrument kill switch and global kill switch (file) have no explicit “clear” path that would transition HALTED → ACTIVE.

---

## 2. Threshold Escalation Hysteresis

### Question
Avoid patterns like: cooldown expires → one more trigger → immediate re-cooldown (repeated flapping).

### Findings

**Current behavior:**
- Rolling window: 5 minutes. Incidents older than 5 minutes are dropped.
- When cooldown expires, `TryExpireCooldown` transitions COOLDOWN → ACTIVE.
- A new trigger (e.g. `REPEATED_RECONCILIATION_MISMATCH`) adds to the window and, if below SUSPEND/HALT thresholds, transitions ACTIVE → COOLDOWN again.

**Flapping scenario:** Cooldown expires at T+61s. At T+62s, one reconciliation mismatch triggers `RequestSupervisoryAction`. Counts: `_cooldownCountInWindow` counts entries with "COOLDOWN" in reason; `_flattenCountInWindow` counts "FLATTEN" or "RECOVERY"; `_recoveryHaltCountInWindow` counts "HALT". A generic reason like `REPEATED_RECONCILIATION_MISMATCH` does not increment these. So the first new incident would hit the `else` branch and go to COOLDOWN again. **Flapping is possible.**

**No hysteresis today:**
- No cooldown counter decay
- No state-sensitive escalation
- No minimum dwell time before de-escalation

**Verdict:** ✓ **FIXED (2025-03-12):** Minimum dwell time implemented.
- `MIN_DWELL_BEFORE_COOLDOWN_SECONDS = 120` (2 minutes)
- When transitioning COOLDOWN→ACTIVE or AWAITING_OPERATOR_ACK→ACTIVE, set _lastResumeToActiveUtc
- When a trigger would escalate ACTIVE→COOLDOWN, require dwell >= 120s; if not met, log SUPERVISORY_TRIGGER_DWELL_SUPPRESSED and return

---

## 3. Operator Acknowledgement as Magic Reset

### Question
Ack can clear the supervisory acknowledgement requirement, but it cannot erase unresolved technical hazards.

### Findings

**AcknowledgeInstrument (lines 226–273):**
- Only applies when `_supervisoryState == AWAITING_OPERATOR_ACK`.
- Calls `CanResumeSupervisoryActive(instrument)` before clearing.
- If `CanResumeSupervisoryActive` returns false, logs `OPERATOR_ACK_REJECTED` with `RESUME_CRITERIA_NOT_MET` and returns false.
- Only on success does it set `_supervisoryState = ACTIVE` and clear `_operatorAckRequired`.

**CanResumeSupervisoryActive** blocks when:
- `IsInRecovery` (recovery/bootstrap unresolved)
- Flatten latch active
- Unowned working orders present

**Verdict:** ✓ **Operator acknowledgement is bounded.** It cannot clear AWAITING_OPERATOR_ACK when recovery, flatten, or unowned orders are present. It does not bypass technical safety.

---

## 4. Global Kill Switch Semantics

### Question
- Does global kill switch block new entries only?
- Does it also block protective modifications?
- Does it still allow recovery-essential work?
- Does it ever auto-flatten, or is that a separate policy?

### Findings

**Where CheckGates is called:**
- Only in `StreamStateMachine.SubmitStopBracketsAtLock` (line 3313).
- That is the path for submitting entry stop brackets at range lock.

**What CheckGates blocks when kill switch is active:**
- Gate 1 fails → `return (false, "KILL_SWITCH_ACTIVE", failedGates)`.
- So **entry orders at lock** are blocked.

**What does NOT go through CheckGates:**
- Protective orders (stop/target): Submitted via IEA `EnqueueNtAction` → `ExecuteSubmitProtectives` → `SubmitProtectiveStopReal`. Uses `Executor.IsExecutionAllowed()` (connection recovery only), **not** RiskGate.
- Order modifications (BE stop moves): Same IEA path, no RiskGate.
- Flatten: Calls adapter `Flatten()` / `FlattenEmergency()` directly. RiskGate comment: "Flatten operations call adapter's Flatten() directly → permitted (bypasses RiskGate)".

**IsExecutionAllowed (RobotEngine):**
- Checks only `_recoveryState == CONNECTED_OK || RECOVERY_COMPLETE`.
- Does **not** check kill switch.

**Verdict:** ✓ **FIXED (2025-03-12):** Kill switch check added to `IsExecutionAllowed()`.

| Operation              | Goes through RiskGate? | Blocked by kill switch? |
|-------------------------|------------------------|--------------------------|
| Entry orders at lock    | Yes (CheckGates)       | ✓ Yes                    |
| Protective orders       | No (uses IsExecutionAllowed) | ✓ Yes (via IsExecutionAllowed) |
| Order modifications     | No (uses IsExecutionAllowed) | ✓ Yes (via IsExecutionAllowed) |
| Flatten                 | No (by design)         | ✓ Allowed (recovery)     |

**Recovery-essential work:** Flatten bypasses RiskGate, so recovery flatten is allowed. Order updates and fills use `EnqueueRecoveryEssential`, which does not go through RiskGate. So recovery-essential work is allowed.

**Auto-flatten:** Global kill switch does not trigger auto-flatten. The `blockInstrumentCallback` (IEA enqueue failure) triggers `FlattenEmergency`, but that is a different path. Kill switch itself does not auto-flatten.

**Implementation:** `RobotEngine.IsExecutionAllowed()` now returns false when `_killSwitch.IsEnabled()`. This blocks protectives (IEA path) and BE modifications (same path) in addition to entry orders (RiskGate path).

---

## Summary

| Check                         | Status | Notes                                                                 |
|------------------------------|--------|-----------------------------------------------------------------------|
| 1. Resume semantics           | ✓ Pass | CanResumeSupervisoryActive gates all resume paths correctly           |
| 2. Threshold hysteresis       | ✓ Fixed | 2 min dwell before re-escalation to COOLDOWN                         |
| 3. Operator ack bounded       | ✓ Pass | Ack cannot clear unresolved technical hazards                         |
| 4. Global kill switch         | ✓ Fixed | IsExecutionAllowed now checks kill switch; blocks protectives + mods |
