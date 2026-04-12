# Phase 3 remediation — refined execution plan

**Related:** [phase3_gap_pressure_rank.md](phase3_gap_pressure_rank.md), [phase3_enforcement_audit_robotcore.md](phase3_enforcement_audit_robotcore.md), gaps **P3-G1 … P3-G5**.

This is the **implementation sequence** (not another broad audit). Each step closes one gap class and gates the next.

---

## Step 1 — G5 (safety isolation)

**Gap:** P3-G5 — kill switch mixed into `IsExecutionAllowed` / recovery guard semantics.

**Do:**

- Separate **kill switch** semantics from **connection recovery** (distinct predicates or callbacks).
- Ensure **EPA-facing paths** (`RiskGate`, `ExecutionPermissionAuthority` preflight, adapter `RecoveryExecutionDisallowed`) each see **distinct** inputs—no hidden AND that merges unrelated concerns.
- **Verify** no submit/flatten/cancel path bypasses the separated logic (spot-check call sites: `RobotEngine` wiring, `NinjaTraderSimAdapter.SetEngineCallbacks`, `RiskGate.CheckGates`).

**Exit criteria:** Recovery block and global kill can be reason-coded independently in logs and gates; Phase 2A RR policy remains consistent with adapter behavior.

---

## Step 2 — G2 (commit integrity)

**Gap:** P3-G2 — `JournalStore.Save` may fail while stream state still advances to terminal `DONE`.

**Do:**

- Enforce the **commit rule** from [03_transition_contract.md](03_transition_contract.md) §2: official terminal commit only when **`StreamJournal.Committed` + successful durable write**.
- Add **failure handling**: fail-closed (do not set `State=DONE` / do not emit “committed” logs) **or** an explicit **degraded** state if product requires continuation.
- **Verify** `StreamJournal` (post-successful save) is authoritative for “slot committed” in downstream readers.

**Exit criteria:** No silent success path when disk write fails; forensic reconstruction can rely on disk or an explicit “commit failed” signal.

---

## Step 3 — G1 (authority unification)

**Gap:** P3-G1 — mismatch decision in `MismatchEscalationCoordinator` vs enforcement in gates without one durable decision record.

**Do:**

- **Choose decision owner** (normative: EPA, unless §3 delegation register gains a row).
- **Unify** coordinator + EPA through **one** decision/apply path (single façade or explicit choke point).
- Add a **durable decision record** (minimal: append-only event or instrument-scoped file under run namespace per [05_persistence_model.md](05_persistence_model.md)).

**Exit criteria:** “Who blocked trading for instrument X at time T?” has one answer in data, not three competing in-memory flags.

---

## Step 4 — G4 (flatten truth)

**Gap:** P3-G4 — “flatten complete” ambiguous (submit vs broker flat).

**Do:**

- **Define** “flatten complete” (normative predicate, e.g. broker flat confirmed + journal/exposure consistent).
- **Tie to one signal** (single event, single journal marker, or single coordinator flag—**not** log reconstruction).
- **Stop** inferring completion only from scattered log lines.

**Exit criteria:** Automation and operators can query one field/event for “flatten completed” per intent/instrument episode.

---

## Step 5 — G3 (cleanup)

**Gap:** P3-G3 — multiple code paths set `RECOVERY_COMPLETE`.

**Do:**

- **Unify recovery exit** (single helper / single state machine transition).
- **Clean up** narrative inconsistencies (comments, log event names that implied a different owner).

**Exit criteria:** One obvious place where recovery leaves pending/running and enters complete; logs match that story.

---

## Order summary

| Step | Gap | Theme |
|------|-----|--------|
| 1 | G5 | Safety isolation |
| 2 | G2 | Commit integrity |
| 3 | G1 | Authority unification |
| 4 | G4 | Flatten truth |
| 5 | G3 | Recovery exit cleanup |
