# Incident Packs — Detailed Summary

**Purpose:** Deterministic replay packs for IEA (Instrument Execution Authority). Each pack exercises a specific scenario and asserts invariants on the final snapshot.

**Run all packs:** CI job `incident-packs` in `.github/workflows/replay-determinism.yml` discovers packs under `modules/robot/replay/incidents/*/`, **sorts directories lexicographically**, and runs determinism + invariants for each.

---

## Step Index Definition

**Step index** = number of events processed so far (1-based).

- Step 1 = after processing event at index 0
- Step N = after processing event at index N−1
- **Final step** = event count

Invariants with `latest_step_index: N` are evaluated only when `stepIndex == N` (i.e. after processing all N events).

---

## Event Vocabulary

**Replay input events** (in `canonical.json` / `events.jsonl`):

| Event | Purpose |
|-------|---------|
| IntentRegistered | Register intent with IEA |
| IntentPolicyRegistered | Register policy before submission |
| ExecutionUpdate | Fill (entry or protective) |
| OrderUpdate | Order state change (e.g. Accepted, Rejected, CANCELLED) |
| Tick | Price tick for BE evaluation |

**Replay-derived behaviors** (not input events):

- BE triggers (stop modified to break-even)
- Order state transitions (from OrderUpdate processing)
- Protection acknowledgement tracking
- Dedup state updates

**Note:** There is no Submit event. Replay assumes orders exist when ExecutionUpdate or OrderUpdate references them; OrderMap entries are created on first reference.

---

## expected.json Schema

```json
{
  "invariants": [
    { "id": "unique-id", "type": "INVARIANT_TYPE", "params": { ... }, "expected": {} }
  ]
}
```

- **invariants** (required): List of invariant expectations. Each has `id`, `type`, `params`, `expected`.
- **expected_final_snapshot_hash** (optional): Not used. Determinism test compares per-step hashes across runs; invariants handle correctness. Pinning full snapshot hash causes churn when new snapshot fields are added.
- **expected_snapshot_fields** (optional): Not implemented. Use invariants for selective asserts.

---

## Invariant Types (Reference)

| Invariant | What It Checks | Failure Reason |
|-----------|----------------|----------------|
| **NO_DUPLICATE_EXECUTION_PROCESSED** | No ExecutionUpdate with the same `executionId` processed more than once | `DUPLICATE_EXECUTION` |
| **INTENT_REQUIRES_POLICY_BEFORE_SUBMISSION** | Every filled intent has a policy registered before its entry fill | `INTENT_WITHOUT_POLICY` |
| **NO_UNPROTECTED_POSITION** | Filled intents get protective orders acknowledged within N events | `UNPROTECTED_TIMEOUT` |
| **BE_PRICE_CROSSED_BY_STEP** | BE trigger price was crossed by tick at or before given step | `BE_PRICE_NOT_CROSSED` |
| **BE_TRIGGERED_BY_STEP** | BE was actually triggered (stop modified) by given step | `BE_NOT_TRIGGERED` |
| **ORDER_STATE_BY_STEP** | Order for given intent has expected state (e.g. CANCELLED) at given step | `ORDER_STATE_MISMATCH`, `ORDER_NOT_FOUND` |

---

## BE Trigger Rules

- **Long:** BE triggers when tick price **≥** trigger. **Equality counts as crossed.**
- **Short:** BE triggers when tick price **≤** trigger. **Equality counts as crossed.**
- **Tick alignment:** Trigger and tick prices must be tick-size aligned for determinism. Future packs should ensure both values are valid for the instrument’s tick size.

---

## Pack 1: SAMPLE

**Path:** `modules/robot/replay/incidents/SAMPLE/`

**Purpose:** Minimal smoke test. Basic IEA flow: intent → fill → tick. No policy, no BE logic.

| Field | Value |
|-------|-------|
| canonical_instrument | MNQ (intent uses MNQ) |
| execution_instrument_key | MNQ |
| event_count | 3 |
| final_step | 3 |

**Scenario:**
- Single Long MNQ intent (entry 21500, stop 21480, target 21520, BE 21505)
- Entry fills at 21500.25
- Tick at 21502 (below BE trigger)

**Event sequence:**

| Step | Event | Payload Summary |
|------|-------|-----------------|
| 1 | IntentRegistered | intent_001, Long MNQ, entry=21500, beTrigger=21505 |
| 2 | ExecutionUpdate | exec_001, fill 21500.25, qty 1 |
| 3 | Tick | tickPrice=21502 |

**Invariants:**
- `NO_DUPLICATE_EXECUTION_PROCESSED` — catches replay regressions early

**What it tests:**
- Replay loader accepts JSONL
- IEA processes IntentRegistered → ExecutionUpdate → Tick
- Determinism: same input → same snapshot hash across runs
- No crash, no precondition violation

---

## Pack 2: MNQ_BE_NO_TRIGGER

**Path:** `modules/robot/replay/incidents/MNQ_BE_NO_TRIGGER/`

**Purpose:** Break-even **does not** fire when tick is below trigger. Policy-before-submission invariant.

| Field | Value |
|-------|-------|
| canonical_instrument | NQ |
| execution_instrument_key | MNQ |
| event_count | 4 |
| final_step | 4 |

**Scenario:**
- Long NQ intent (entry 21500, BE trigger 21505), execution instrument MNQ
- Policy registered before fill
- Entry fills at 21500.25
- Tick at **21502** (below 21505) → BE should **not** fire

**Event sequence:**

| Step | Event | Payload Summary |
|------|-------|-----------------|
| 1 | IntentRegistered | b332f40221252b4a, Long NQ, entry=21500, beTrigger=21505 |
| 2 | IntentPolicyRegistered | Policy for intent |
| 3 | ExecutionUpdate | nq_exec_001, fill 21500.25 |
| 4 | Tick | tickPrice=21502 (< 21505) |

**Invariants:**
- `NO_DUPLICATE_EXECUTION_PROCESSED`
- `INTENT_REQUIRES_POLICY_BEFORE_SUBMISSION`

**What it tests:**
- Policy must precede fill (INTENT_REQUIRES_POLICY)
- Deduplication of executions
- BE logic correctly does **not** trigger when price hasn’t crossed
- Intent ID alignment (computed hash `b332f40221252b4a` used consistently)

---

## Pack 3: MYM_BE_TRIGGER

**Path:** `modules/robot/replay/incidents/MYM_BE_TRIGGER/`

**Purpose:** Break-even **does** fire when tick crosses trigger. Full BE pipeline. **Equality counts as crossed.**

| Field | Value |
|-------|-------|
| canonical_instrument | YM |
| execution_instrument_key | MYM |
| event_count | 4 |
| final_step | 4 |

**Scenario:**
- Long YM intent (entry 49875, BE trigger 49940), execution instrument MYM
- Policy registered before fill
- Entry fills at 49875.5
- Tick at **49940** (equals trigger) → BE **should** fire (equality counts as crossed)

**Event sequence:**

| Step | Event | Payload Summary |
|------|-------|-----------------|
| 1 | IntentRegistered | edd93f90843dc0ea, Long YM, entry=49875, beTrigger=49940 |
| 2 | IntentPolicyRegistered | Policy for intent |
| 3 | ExecutionUpdate | mym_exec_001, fill 49875.5 |
| 4 | Tick | tickPrice=49940 (>= 49940) |

**Invariants:**
- `NO_DUPLICATE_EXECUTION_PROCESSED`
- `INTENT_REQUIRES_POLICY_BEFORE_SUBMISSION`
- `BE_PRICE_CROSSED_BY_STEP` — BE price crossed by step 4 (intent edd93f90843dc0ea)
- `BE_TRIGGERED_BY_STEP` — BE actually triggered by step 4

**What it tests:**
- BE monitoring finds active intents (IntentMap + OrderMap key alignment)
- `EvaluateBreakEvenDirect` runs on tick and detects price cross (including equality)
- BE state transitions: `triggerReached` → `triggered`
- Snapshot `BeState` contains `triggered:{intentId}`

---

## Pack 4: MNQ_OCO_SIBLING_CANCELLED

**Path:** `modules/robot/replay/incidents/MNQ_OCO_SIBLING_CANCELLED/`

**Purpose:** OCO entry bracket — when one leg fills, the other is cancelled. NinjaTrader reports the cancelled sibling as `Rejected` with comment "CancelPending". Must be treated as CANCELLED, not REJECTED. Replay path does not call `RecordRejection` by design.

| Field | Value |
|-------|-------|
| canonical_instrument | NQ |
| execution_instrument_key | MNQ |
| event_count | 6 |
| final_step | 6 |

**Scenario:**
- Long and short NQ entry stops in same OCO group (breakout strategy), execution instrument MNQ
- Long fills at 21550.25
- Short receives OrderUpdate: `Rejected` + Comment `"Order 'nq_short_ord_001' can't be submitted: order status is CancelPending."`
- **Expected:** Short order state = CANCELLED (not REJECTED). No RecordRejection.

**Event sequence:**

| Step | Event | Payload Summary |
|------|-------|-----------------|
| 1 | IntentRegistered | de7776d06d5b5edc, Long NQ, entry=21550 |
| 2 | IntentRegistered | e079d2d1e350342d, Short NQ, entry=21500 |
| 3 | IntentPolicyRegistered | Policy for long |
| 4 | IntentPolicyRegistered | Policy for short |
| 5 | ExecutionUpdate | nq_long_fill_001, long fills at 21550.25 |
| 6 | OrderUpdate | nq_short_ord_001, orderState=Rejected, comment contains "CancelPending" |

**Invariants:**
- `NO_DUPLICATE_EXECUTION_PROCESSED`
- `INTENT_REQUIRES_POLICY_BEFORE_SUBMISSION`
- `ORDER_STATE_BY_STEP` — short intent (e079d2d1e350342d) order state = CANCELLED at step 6

**What it tests:**
- `ProcessOrderUpdateCore` treats Rejected + "CancelPending" as OCO sibling cancel → state CANCELLED
- OrderMap entry created for entry order on first OrderUpdate (replay has no submit event)
- No false REJECTED state; replay path does not call RecordRejection for OCO cancellation

---

## Summary Table

| Pack | canonical_instrument | execution_instrument_key | event_count | final_step | Key Scenario | Invariants |
|------|----------------------|--------------------------|-------------|------------|--------------|------------|
| SAMPLE | MNQ | MNQ | 3 | 3 | Basic fill, tick below BE | 1 |
| MNQ_BE_NO_TRIGGER | NQ | MNQ | 4 | 4 | Tick below BE → no trigger | 2 |
| MYM_BE_TRIGGER | YM | MYM | 4 | 4 | Tick at BE → trigger | 4 |
| MNQ_OCO_SIBLING_CANCELLED | NQ | MNQ | 6 | 6 | OCO cancel → CANCELLED not REJECTED | 3 |

---

## Negative Control Pack (Excluded from CI)

**NEGATIVE_BE_NOT_TRIGGERED** — Same events as MNQ_BE_NO_TRIGGER (tick below trigger). Invariant asserts `BE_TRIGGERED_BY_STEP` → expected FAIL with `BE_NOT_TRIGGERED`. Run manually to validate invariant harness. Folder contains `.ci-skip` so CI skips it.

---

## Run Commands

```bash
# Single pack (e.g. MYM_BE_TRIGGER)
dotnet run --project modules/robot/replay_host/Robot.ReplayHost.csproj -- \
  --determinism-test --file modules/robot/replay/incidents/MYM_BE_TRIGGER/canonical.json \
  --run-invariants --expected modules/robot/replay/incidents/MYM_BE_TRIGGER/expected.json

# All packs (CI does this automatically, sorted lexicographically)
# Each pack under modules/robot/replay/incidents/*/ with canonical.json is run
```

---

## Determinism Test

Each pack runs twice; per-step snapshot hashes are compared. If any step diverges, the test fails with `DETERMINISM:FAIL`. Invariants are checked after each step; step-specific invariants (e.g. `latest_step_index`) run only when `stepIndex == latest_step_index`.

Snapshot hashing excludes nondeterministic fields (timestamps, GUIDs) or derives them deterministically from the event stream.
