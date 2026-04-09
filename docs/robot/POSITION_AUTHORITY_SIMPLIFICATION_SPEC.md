# POSITION_AUTHORITY_SIMPLIFICATION_SPEC

**Status:** Target architecture — **strict control contract** (design).  
**Scope:** Execution / reconciliation **control** and **decision-making** — not a broker adapter redesign.  
**Approach:** Two **independent** layers: (1) **position authority** — who explains the broker book; (2) **safety overlays** — operational locks and freshness that may block execution **without** changing authority. **UNKNOWN** is the default whenever quantities do not cleanly match REAL or RECOVERY, **including** when recovery rows match broker quantities but **recovery is not trusted** (see §1).

---

## Authority vs safety overlays (non-negotiable separation)

### Position authority (single question only)

Authority answers **one** question and **nothing else**:

> **Who explains the broker position?**

Allowed answers: **REAL** | **RECOVERY** | **UNKNOWN** only.

- Authority is computed from: `broker_qty`, `real_open_qty`, `recovery_open_qty`, `journal_open_qty` (derived), and **one boolean** `recovery_is_trusted` (see below). **Not** a fourth authority state — trust is a **predicate** used only to decide whether the RECOVERY quantity pattern is acceptable.
- **Overlays must not redefine authority.** If a kill-switch fires, authority may still be REAL; execution is blocked by the overlay, not by re-labeling authority.

### Safety overlays (orthogonal)

These are **separate** booleans / latches. They **may block execution in any authority state** (including REAL). They do **not** change REAL / RECOVERY / UNKNOWN.

| Overlay | Role |
|---------|------|
| **Hard lock** (e.g. hard fail-closed execution lock after broker flatten) | Block execution until cleared per policy. |
| **Unmapped fill kill-switch** (unsafe lock from unmapped execution) | Block execution until operator / structural clear. |
| **Stale snapshot** | Refuse to trust measurements; block execution until fresh snapshot. |
| **Manual lock** (operator / structural manual unlock path) | Block execution until cleared. |

**Rule:** Logs should report **authority** and **overlays** separately, e.g. `authority=REAL`, `blocked_by=unmapped_fill_kill_switch`.

---

## 1. Definitions (exact)

All quantities are for a **single instrument** (and its canonical execution family as defined for journal aggregation, e.g. `ExecutionJournal.GetPositionAuthorityOpenQuantitiesForInstrument`).

| Symbol | Meaning |
|--------|--------|
| **broker_qty** | Authoritative broker position quantity (**absolute** contracts) from the account snapshot for that instrument. |
| **real_open_qty** | Sum of open quantity from **non-recovered** strategy / intended journal rows (the “real” robot book). |
| **recovery_open_qty** | Sum of open quantity from **recovered / synthetic** journal rows (temporary scaffolding). |
| **journal_open_qty** | `real_open_qty + recovery_open_qty`. |

### Position authority state (exactly one at a time)

Evaluate in order below. **UNKNOWN** is the explicit **default** for all cases not matching REAL or RECOVERY.

| State | Predicate |
|-------|------------|
| **REAL** | `real_open_qty == broker_qty` **and** `recovery_open_qty == 0`. |
| **RECOVERY** | `real_open_qty < broker_qty` **and** `recovery_open_qty > 0` **and** `journal_open_qty == broker_qty` **and** `recovery_is_trusted == true`. |
| **UNKNOWN** | **All other cases.** |

### `recovery_is_trusted` (single boolean, not a new authority state)

**Plain English:** Trust recovery **only if this running process created it and saw it happen** — not because it was inferred, reconstructed, guessed, or “seems recent.” Otherwise orphan or replayed journal rows could become authority.

**Hard rule (not a heuristic):**

```
recovery_is_trusted = true  ONLY IF:
    RECOVERY_ROW_CREATED  was observed for the open recovery row(s) in THIS process
    AND  there has been no restart / reconstruction gap since that event
Otherwise:
    recovery_is_trusted = false
```

- **Observed** means the process **handled** the `RECOVERY_ROW_CREATED` log/event on the code path that created the row (same run, durable correlation). If the process **did not** see that event — e.g. recovery appeared only after **restart**, **journal reload**, or **reconstruction** without a matching observed create in this session — **`recovery_is_trusted = false`**.
- **No restart/reconstruction gap** means: from the observed `RECOVERY_ROW_CREATED` through the current evaluation, the system has **not** lost continuity that would allow stale or foreign recovery rows without a fresh observed create (define “gap” in implementation as: process restart, cold journal load without in-memory create receipt, or explicit reconstruction pass that rewrites recovery without a new `RECOVERY_ROW_CREATED` — all ⇒ false until a new observed create).

**Explicitly does *not* qualify as trust:** recency heuristics, timestamps alone, “aligned with broker,” parity guesses, or telemetry-only inference without **`RECOVERY_ROW_CREATED` observed in this process** + **no gap**.

**What this spec explicitly does *not* add:** extra authority states, recovery subtypes, trust scores, or multi-level “recovery quality” — that would undo simplification.

**UNKNOWN includes (non-exhaustive):**

- `real_open_qty > broker_qty` (journal “high” / overlap vs broker).
- `real_open_qty == broker_qty` **and** `recovery_open_qty > 0` **after** the **auto-resolve** pass (§2.1) has run — i.e. still contradictory / could not close recovered rows cleanly (see §2.1). *Before* maintenance, this pattern is an **input** to auto-resolve, not a stable terminal classification.
- Recovery present but **`journal_open_qty ≠ broker_qty`** (recovery scaffolding does not fully bridge to broker).
- Quantity pattern would match old RECOVERY **but** `recovery_is_trusted == false` (no observed `RECOVERY_ROW_CREATED` in this process, restart/reconstruction gap, or other §1 failure — treat as **UNKNOWN**, not RECOVERY).
- Contradictory or **missing** data (e.g. cannot compute quantities reliably).
- Any ambiguity not satisfying REAL or RECOVERY exactly.

**Interpretation**

- **Only one** authority state applies at a given evaluation instant.
- **Recovery rows** are scaffolding, not a parallel truth. **RECOVERY** authority means: broker is fully explained by **journal total**, with real book short and recovery book making up the gap, **and** **`recovery_is_trusted`** per the **hard rule** above (`RECOVERY_ROW_CREATED` observed here + no restart/reconstruction gap).
- **UNKNOWN** means: do not assume a single coherent story; **no normal execution** (see control contract).

---

## 2. Control contract (execution behavior)

This is the **sharp** rule set: authority sets **baseline** execution posture; **overlays** may **deny** execution even when baseline would allow.

| Authority | Baseline execution posture |
|-----------|----------------------------|
| **REAL** | **Normal execution may be allowed** — entries, protectives, targets, routine flatten as policy permits. |
| **RECOVERY** | **No new directional trading.** Repair / recovery / flatten **only**, per policy. |
| **UNKNOWN** | **No normal execution** — stop, lock, alert; repair-only if explicitly allowlisted and safe. |

**Overlays:** Any active overlay (hard lock, unmapped-fill kill-switch, stale snapshot, manual lock) **may block execution in any authority state**, including REAL. Denial reason must cite the **overlay**, not a reclassified authority.

**Composition rule (conceptual):**

```
may_execute_normal = (authority == REAL) AND NOT(overlay_blocks)
may_execute_repair_only = (authority == RECOVERY) AND NOT(overlay_blocks) AND repair_policy_allows
must_not_execute_normal = (authority == UNKNOWN) OR overlay_blocks
```

### 2.1 Auto-resolve: converge RECOVERY → REAL (maintenance, not a new state)

**Goal:** Recovery rows are **temporary scaffolding**. When the **real** book alone already explains the broker, the system should **automatically** close recovered rows, **recompute** quantities, and **transition** authority to **REAL** (recovery open quantity becomes zero). No new authority states — this is a **journal maintenance** step that runs **before** (or as the first step of) authority evaluation for control decisions.

**Auto-resolve rule (superseded-by-real)**

When all of the following hold:

- `real_open_qty == broker_qty` (non-recovered exposure equals broker),
- `recovery_open_qty > 0`,
- `broker_qty > 0` (non–broker-flat context per current journal helpers; broker-flat uses separate close paths),

then:

1. **Close** all applicable open **integrity-recovered** rows for that instrument (same semantics as completing them with a “superseded by real” completion reason).
2. **Recompute** `real_open_qty`, `recovery_open_qty`, `journal_open_qty`.
3. **Re-derive** authority. If `recovery_open_qty == 0` and `real_open_qty == broker_qty` → **REAL** (and `recovery_is_trusted` is irrelevant because there is no recovery quantity).

If the close **cannot** be applied or quantities remain inconsistent → **UNKNOWN**; log block reason (see §13).

**Relationship to RECOVERY semantics:** While in **RECOVERY** (gap bridged by recovery), baseline remains repair-only. Auto-resolve does **not** apply until **real** already matches **broker**; it clears leftover recovery **after** the strategy book has caught up.

---

## 3. Worked examples

### Example A — REAL (clean)

- `broker_qty = 3`
- `real_open_qty = 3`, `recovery_open_qty = 0` → `journal_open_qty = 3`
- **Authority = REAL.**  
- With no overlays: normal execution **may** be allowed.

### Example B — RECOVERY (valid)

- `broker_qty = 5`
- `real_open_qty = 2`, `recovery_open_qty = 3` → `journal_open_qty = 5`
- Check: `real < broker`, `recovery > 0`, `journal == broker`, **`recovery_is_trusted = true`** → **Authority = RECOVERY.**  
- No new directional trading; repair-only paths.

### Example B2 — quantity match but untrusted recovery → UNKNOWN

- `broker_qty = 5`
- `real_open_qty = 2`, `recovery_open_qty = 3` → `journal_open_qty = 5`
- Same quantities as Example B, but **`recovery_is_trusted = false`** (e.g. no **`RECOVERY_ROW_CREATED`** observed in this process, or a restart/reconstruction gap since create).
- **Authority = UNKNOWN** — do **not** treat orphan recovery as explaining the broker.

### Example C — overlap (journal vs broker) → UNKNOWN

- `broker_qty = 3`
- `real_open_qty = 4`, `recovery_open_qty = 0` → `journal_open_qty = 4`
- Real book exceeds broker → **UNKNOWN** (not REAL, not RECOVERY).

### Example D — journal high with recovery → UNKNOWN

- `broker_qty = 4`
- `real_open_qty = 2`, `recovery_open_qty = 3` → `journal_open_qty = 5`
- `journal_open_qty ≠ broker_qty` while recovery present → **UNKNOWN**.

### Example E — unmapped fill overlay with “otherwise REAL” quantities

- `broker_qty = 2`, `real_open_qty = 2`, `recovery_open_qty = 0` → **Authority = REAL** (quantities match REAL).
- Unmapped fill fires **unsafe / kill-switch overlay** → execution **blocked** by overlay.
- **Do not** flip authority to UNKNOWN because of the fill; **authority stays REAL**, **blocked_by = unmapped_fill_kill_switch** (or equivalent).

### Example F — auto-resolve: real matches broker, recovery still open → REAL after maintenance

- Before maintenance: `broker_qty = 5`, `real_open_qty = 5`, `recovery_open_qty = 3` (stale scaffolding).
- Run **auto-resolve** (§2.1): close recovered rows → `recovery_open_qty = 0`.
- After recompute: **Authority = REAL** (normal execution subject to overlays).

---

## 4. Anti-complexity rule

**Old diagnostic / reconciliation sub-states must not become new primary control states under different names.**

Examples: release-readiness contradiction lists, reconciliation lane labels, transient parity sub-reasons — may remain **telemetry** or **repair scheduling**, but **must not** replace REAL / RECOVERY / UNKNOWN as the sole authority vocabulary for “who explains the broker?” If a new label starts gating trading, it is a **regression** unless explicitly promoted through this spec.

**Same bar for “trust”:** `recovery_is_trusted` is a **boolean predicate** feeding the RECOVERY row only, defined by **§1 hard rule** (`RECOVERY_ROW_CREATED` + no gap). Do **not** add named authority subtypes for recovery, trust scores, recency heuristics, or layered “recovery quality” enums that effectively become a shadow state machine.

---

## 5. Relationship to today’s code (grounding)

Today, `JournalIntegrityGuarantee.EvaluatePositionAuthorityState` uses `REAL_DOMINANT` / `RECOVERY_REQUIRED` / `CONFLICT` with **looser** RECOVERY than this spec. Migration: **one** derivation function for REAL / RECOVERY / UNKNOWN; map legacy enums for logs only during transition.

`ExecutionSafetyGate` mixes parity, authority, exposure, and overlays — **target** is: compute **authority** first; apply **overlays** second; keep parity/exposure as **UNKNOWN** drivers or **overlay** inputs without renaming authority.

---

## 6. Mapping: current system → simplified roles

| Current component | Path (representative) | Role in **current** system | Role in **simplified** system | keep / change / remove_later |
|-------------------|------------------------|-----------------------------|------------------------------|------------------------------|
| `JournalParityChecker` | `JournalIntegrityGuarantee.cs` | Structural parity | Drives **UNKNOWN** or diagnostics; not a substitute for authority | keep; change |
| `EvaluatePositionAuthorityState` | same | 3-state enum | Single REAL / RECOVERY / UNKNOWN | change |
| `ExecutionJournal.GetPositionAuthorityOpenQuantitiesForInstrument` | `ExecutionJournal.cs` | real vs recovered | Source for quantities | keep; verify |
| `ExecutionSafetyGate` | `ExecutionSafetyGate.cs` | Mixed checks | Authority first; overlays second | change |
| `NinjaTraderSimAdapter` submit guards | `NinjaTraderSimAdapter.cs` | Calls gate | One authority + overlay composition | change |
| `InstrumentExecutionAuthority` | `InstrumentExecutionAuthority*.cs` | IEA / recovery | RECOVERY-mode only | keep; narrow |
| `RobotEngine` journal / reconcile | `RobotEngine.cs` | Repair hooks | State production; not “trade OK” alone | change |
| `HardFailClosedExecutionModel` | `HardFailClosedExecutionModel.cs` | Hard lock | **Overlay** | keep |
| Release readiness / reconciliation blockers | various | Many inputs | Diagnostic / repair; not authority | change |

---

## 7. Target control loop (plain English + pseudocode)

1. Measure `broker_qty` (fresh snapshot when required for trading decisions).
2. Measure `real_open_qty`, `recovery_open_qty`, `journal_open_qty`.
3. **Auto-resolve (§2.1):** if `real_open_qty == broker_qty` and `recovery_open_qty > 0` and policy allows supersede close, close recovered rows and **re-measure** quantities.
4. Compute **`recovery_is_trusted`** (boolean) per §1 **hard rule** (`RECOVERY_ROW_CREATED` observed in this process + no restart/reconstruction gap) — **not** a separate authority state.
5. **Derive authority** ∈ { REAL, RECOVERY, UNKNOWN } — **default UNKNOWN** if ambiguous or untrusted recovery.
6. **Log** evaluation and any authority transition (§13).
7. Evaluate **overlays** (hard lock, unmapped kill-switch, stale snapshot, manual lock).
8. Apply **control contract** (§2): baseline from authority; deny if any overlay blocks.

```
maintain_journal_for_authority(journal, broker_qty, ...):
    // Close integrity-recovered rows when real book alone matches broker
    if broker_qty > 0 and real_open_qty == broker_qty and recovery_open_qty > 0:
        close_recovered_rows_superseded_by_real(...)
        recompute real_open_qty, recovery_open_qty

derive_authority(broker_qty, real_open_qty, recovery_open_qty, recovery_is_trusted):
    journal_open_qty = real_open_qty + recovery_open_qty
    if real_open_qty == broker_qty and recovery_open_qty == 0:
        return REAL
    if (real_open_qty < broker_qty and recovery_open_qty > 0
            and journal_open_qty == broker_qty and recovery_is_trusted):
        return RECOVERY
    return UNKNOWN   // default

may_normal_execute(authority, overlays):
    if overlays.any_block():
        return false, overlays.reasons
    if authority == REAL:
        return true
    return false
```

---

## 8. Signals that must not become primary trading inputs

| Signal | Classification |
|--------|----------------|
| Reconciliation completion / schedule | Diagnostic + scheduling |
| Journal completion blocked | Repair workflow; not authority |
| Partial alignment counts | Diagnostic |
| Transient parity / working-order detail | Diagnostic or UNKNOWN drivers |
| IEA sub-states | Internal to repair |
| Release readiness / contradictions | Diagnostic until decoupled |

---

## 9. Phased refactor (summary)

**PHASE 1:** Single `DerivePositionAuthority`; execution gate = authority **then** overlays; legacy diagnostics remain.  
**PHASE 2:** Recovery rows strictly temporary; REAL implies no open recovery qty; RECOVERY requires **`recovery_is_trusted`** per §1.

---

## 10. Definition of done

- Every “why trade / why not” is explainable as **authority** + **overlay list** + optional **diagnostic** detail (including why `recovery_is_trusted` was false when relevant: missing `RECOVERY_ROW_CREATED` receipt vs gap vs both).
- UNKNOWN always blocks normal execution unless spec explicitly allowlists an exception.
- Overlays never **rename** authority.
- One operator-facing authority signal; overlays separately visible.
- Untrusted recovery never yields RECOVERY authority.

---

## 11. Constraints

No giant rewrite; preserve existing safety behavior; transitional logging allowed.

---

## 12. Top 5 code areas to touch first (Phase 1)

1. `JournalIntegrityGuarantee.cs` — single derivation; UNKNOWN default; **emit** `POSITION_AUTHORITY_TRANSITION` when authority changes vs previous evaluation for that instrument; run **auto-resolve** before final authority for control (align with §2.1).  
2. `ExecutionSafetyGate.cs` — authority vs overlay ordering; map deny reasons to `POSITION_AUTHORITY_BLOCKED_UNKNOWN` when authority is UNKNOWN (in addition to existing snapshot/parity reasons).  
3. `NinjaTraderSimAdapter.cs` — gate composition.  
4. `ExecutionJournal.cs` — quantity definitions; **`CloseRecoveredRowsSupersededByRealExposure`**; standardize **recovery row created/closed** logs (§13).  
5. `RobotEngine.cs` — reconcile vs trade permission; ensure integrity / reconcile loop runs supersede close on applicable paths.

---

## 13. Transition logging (standard events)

**Purpose:** Operators and auditors must see **authority**, **quantities**, and **why** the system moved between REAL / RECOVERY / UNKNOWN without introducing new states.

**Standard event names** (vocabulary stable; implementation may alias legacy names during transition):

| Event | When |
|-------|------|
| `POSITION_AUTHORITY_EVALUATED` | After quantities + `recovery_is_trusted` (§1 hard rule) + (post–auto-resolve) derivation — every evaluation used for control or logged heartbeat. |
| `POSITION_AUTHORITY_TRANSITION` | **`previous_state` ≠ `new_state`** for the same instrument in the same session / evaluation chain. |
| `RECOVERY_ROW_CREATED` | Integrity or repair path creates or upserts an open recovered journal row for position authority. **Must** be emitted in-process when the row is written; this event is the **receipt** that feeds §1 `recovery_is_trusted` together with “no gap” since create. |
| `RECOVERY_ROW_CLOSED` | A recovered row is completed (any completion reason), including superseded-by-real and broker-flat. |
| `RECOVERY_SUPERSEDED_BY_REAL` | Auto-resolve (§2.1) closed recovered rows because `real_open_qty == broker_qty` (alias or wrap legacy `RECONCILIATION_RECOVERED_ROW_CLOSED_SUPERSEDED`). |
| `POSITION_AUTHORITY_BLOCKED_UNKNOWN` | Normal execution denied because **authority == UNKNOWN** (include **reason** — e.g. `recovery_is_trusted` false: no observed `RECOVERY_ROW_CREATED` or restart/reconstruction gap; quantity mismatch; missing data). |

**Payload fields (each event should include all that apply):**

| Field | Description |
|-------|-------------|
| `instrument` | Execution / journal instrument key as used for authority. |
| `broker_qty` | `broker_qty` (abs as policy defines). |
| `real_open_qty` | After any maintenance step in that evaluation. |
| `recovery_open_qty` | After any maintenance step in that evaluation. |
| `previous_state` | Prior authority { REAL, RECOVERY, UNKNOWN } or null on first eval. |
| `new_state` | Current authority. |
| `reason` | Human-readable; for UNKNOWN blocks, explicit cause. |

**Note:** Do not add scoring dimensions or recovery subtypes to payloads — optional **diagnostic** strings are fine.

---

## 14. Current code alignment and conflicts (for implementers)

**Already present (narrow / extend, do not duplicate):**

- `JournalIntegrityGuarantee.EnsureJournalIntegrity` calls `ExecutionJournal.CloseRecoveredRowsSupersededByRealExposure` on some paths; logs `POSITION_AUTHORITY_EVALUATED` via `logEngine` callback.  
- `ExecutionJournal` emits `RECONCILIATION_RECOVERED_ROW_CLOSED_SUPERSEDED` today — map or rename to **`RECOVERY_SUPERSEDED_BY_REAL`** for external contract while keeping completion reasons stable if needed.  
- `FeatureFlags.EnablePositionAuthorityEnforcement` gates an **extra** supersede pass when `real == broker` and `recovered > 0` — **conflicts** with “always converge” if left off; prefer policy default **on** for auto-resolve once validated.

**Conflicts to narrow:**

| Area | Issue |
|------|--------|
| `EvaluatePositionAuthorityState` | Returns `RECOVERY_REQUIRED` whenever `real_open < broker` **without** requiring `journal_open == broker` or `recovery_is_trusted` — **looser** than §1; must converge to one `DerivePositionAuthority` implementation. |
| Enum names | `REAL_DOMINANT` / `RECOVERY_REQUIRED` / `CONFLICT` vs REAL / RECOVERY / UNKNOWN — **mapping layer** only; no new enum values. |
| Gate | `ExecutionSafetyGate` requires `REAL_DOMINANT` for structural safety — consistent with “no normal execution” under RECOVERY/UNKNOWN; **UNKNOWN** and **blocked** reasons should be logged per §13. |
| Duplicate NT vs modules | `ExecutionJournal` / `JournalIntegrityGuarantee` exist under `modules` and `RobotCore_For_NinjaTrader` — changes may need **paired** edits until unified. |

---

*End of spec.*
