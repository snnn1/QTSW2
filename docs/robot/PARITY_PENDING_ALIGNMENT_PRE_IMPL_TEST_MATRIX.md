# PARITY_PENDING_ALIGNMENT — Pre-Implementation Test Matrix

**Objective:** Prove `PARITY_PENDING_ALIGNMENT` never classifies a real mismatch as “pending” unless the broker/journal delta is **exactly**, **directionally**, and **identity-correctly** explained by **valid, owned, unapplied** execution-derived pending entries.

**Scope:** Pending-alignment classification design only. No production code in this document. No architecture redesign.

**Core model:** Third state beside `PARITY_OK` and `POSITION_MISMATCH`. Pending alignment is valid **only if** invariants **I1–I5** all hold for the pending set used in explanation.

**Sign convention (matrix default):** `signed_delta = brokerPos - journalOpen`. Pending entries use the same sign (positive = net long contribution to parity instrument).

---

## Section 1 — Invariant summary

| Invariant | Description | Why it matters | Failure consequence |
|-----------|-------------|----------------|---------------------|
| **I1** — Deduplication | Each durable execution identity may contribute **at most one** pending row (retries/replays must not double-count). | Prevents inflating explanation without new real executions. | **Not** `PARITY_PENDING_ALIGNMENT`; typically `POSITION_MISMATCH` if delta unexplained after dedupe, or invalid ledger state. |
| **I2** — Quantity correctness | After **filtering to I5-valid** rows only: aggregate **magnitude** of pending must match **magnitude** of position delta (see note below with I4). | Prevents partial or bloated “explanations.” | **Not** `PARITY_PENDING_ALIGNMENT`; `POSITION_MISMATCH` (or `PARITY_OK` only if `signed_delta==0` and no invalid pending). |
| **I3** — Ownership correctness | Only **robot-owned / trusted** executions may appear in the pending explanation set. Unknown/manual/unowned fills must not justify pending alignment. | Prevents masking external or untrusted activity. | **Not** `PARITY_PENDING_ALIGNMENT`; typically `POSITION_MISMATCH` or `UNKNOWN_ORDER_PRESENT` per existing gate order. |
| **I4** — Directional correctness | **Sum of signed pending deltas** must equal **`brokerPos - journalOpen`** (same convention as parity view). | Prevents wrong-sign pending from “matching” absolute delta (e.g. broker +1, journal 0, pending −1). | **Not** `PARITY_PENDING_ALIGNMENT`; `POSITION_MISMATCH`. |
| **I5** — Identity validity | Each pending row counted toward explanation must be **not already reflected** in the journal (e.g. execution id absent from journaled applied set, or strictly after journal watermark for that durable key—**exact rule is a coverage-gap decision**, see Section 5). | Prevents **stale** pending from **recycling** to explain a **new** mismatch when quantity/sign accidentally still match. | **Not** `PARITY_PENDING_ALIGNMENT`; exclude stale → re-evaluate; unexplained delta ⇒ `POSITION_MISMATCH`. |

**I2 + I4 together:** Practically, for a single net direction of `signed_delta`, enforcing **I4** fixes sign; **I2** adds that there is no spurious offsetting pending (e.g. net +1 but two rows +2 and −1 without matching **I4** would already fail). Matrix rows below state explicit expectations.

---

## Section 2 — Test matrix

**Columns:**

- **ExpectedStatus:** `PARITY_OK` | `PARITY_PENDING_ALIGNMENT` | `POSITION_MISMATCH` | `UNKNOWN_ORDER_PRESENT` | `INSUFFICIENT_DATA`
- **JournalAppliedState:** which executions / fills are already reflected in journal open qty (or “none”, “all listed execs”, “execA only”, etc.).

| Test_ID | Scenario | BrokerPos | JournalOpen | PendingEntries (explicit) | OwnershipState | JournalAppliedState | ExpectedStatus | InvariantsExercised | Why |
|---------|----------|-----------|-------------|---------------------------|----------------|---------------------|----------------|---------------------|-----|
| **Group A — Baseline / happy path** |
| A-01 | Single owned unapplied fill explains gap | 1 | 0 | `[+1 execA owned unapplied]` | all_owned | none applied | PARITY_PENDING_ALIGNMENT | I1,I2,I3,I4,I5 | Full explanation, valid identity. |
| A-02 | Journal caught up, pending cleared | 1 | 1 | `[]` | all_owned | execA applied to journal | PARITY_OK | — | Zero delta; no stale pending. |
| A-03 | No delta, no pending | 0 | 0 | `[]` | — | none | PARITY_OK | — | Baseline OK. |
| A-04 | Same as A-01 but two instruments unchanged (only one parity key under test) | 1 | 0 | `[+1 execA owned unapplied]` | all_owned | none | PARITY_PENDING_ALIGNMENT | I1–I5 | Confirms matrix applies per parity key; no cross-instrument bleed (**gap if multi-key coupling undecided**). |
| **Group B — Deduplication** |
| B-01 | Same execution recorded twice as two +1 rows | 1 | 0 | `[+1 execA owned unapplied, +1 execA owned unapplied]` | all_owned | none | POSITION_MISMATCH | I1 fails | Double-count same exec; must not PENDING. |
| B-02 | Deduping design: second record ignored; single row remains | 1 | 0 | effective `[+1 execA owned unapplied]` after dedupe | all_owned | none | PARITY_PENDING_ALIGNMENT | I1 pass | Implementation allowed only if **observable state** enforces I1. |
| B-03 | Replay same log twice (same exec ids) | 1 | 0 | after replay: must still be single logical pending or none | all_owned | none after first replay; journal still 0 | PARITY_PENDING_ALIGNMENT or PARITY_OK per dedupe moment | I1 | Second replay must not add second +1 without second real fill. |
| B-04 | Duplicate callback same execution id (API duplicate delivery) | 1 | 0 | `[+1 execA owned unapplied]` only once | all_owned | none | PARITY_PENDING_ALIGNMENT | I1 | Same as B-02/B-03; ledger must dedupe. |
| **Group C — Directionality** |
| C-01 | Positive delta, positive pending | 1 | 0 | `[+1 execA owned unapplied]` | all_owned | none | PARITY_PENDING_ALIGNMENT | I4 | Signed sum +1. |
| C-02 | Positive delta, negative pending | 1 | 0 | `[-1 execA owned unapplied]` | all_owned | none | POSITION_MISMATCH | I4 fails | Σpending = −1 ≠ +1. |
| C-03 | Negative delta, negative pending | 0 | 1 | `[-1 execA owned unapplied]` | all_owned | none (exec not yet in journal; journal shows 1) | PARITY_PENDING_ALIGNMENT | I4 | signed_delta = −1. |
| C-04 | Negative delta, positive pending | 0 | 1 | `[+1 execA owned unapplied]` | all_owned | none | POSITION_MISMATCH | I4 fails | Σpending = +1 ≠ −1. |
| **Group D — Exact quantity match** |
| D-01 | Under-explaining | 2 | 0 | `[+1 execA owned unapplied]` | all_owned | none | POSITION_MISMATCH | I2,I4 | Σ = +1 ≠ +2. |
| D-02 | Over-explaining | 1 | 0 | `[+2 execA owned unapplied]` | all_owned | none | POSITION_MISMATCH | I1 or I2 | One exec cannot be +2 without design allowing split rows (**decision**); if one row +2 for one exec, I1/I2 still must reject vs broker +1. |
| D-03 | Two fills explain +2 | 2 | 0 | `[+1 execA owned unapplied, +1 execB owned unapplied]` | all_owned | none | PARITY_PENDING_ALIGNMENT | I1,I2,I4,I5 | Valid aggregation. |
| D-04 | Offsetting junk within pending | 2 | 0 | `[+1 execA owned unapplied, +1 execB owned unapplied, −1 execC owned unapplied]` | all_owned | none (execC not applied) | POSITION_MISMATCH | I4 (and I2) | Σ = +1 ≠ +2 unless execC is valid (**if execC invalid** → still wrong net). |
| D-05 | Over-explaining two executions | 1 | 0 | `[+1 execA owned unapplied, +1 execB owned unapplied]` | all_owned | none | POSITION_MISMATCH | I2,I4 | Σ = +2 ≠ +1. |
| **Group E — Ownership** |
| E-01 | Owned execution only | 1 | 0 | `[+1 execA owned unapplied]` | all_owned | none | PARITY_PENDING_ALIGNMENT | I3 | Baseline trusted. |
| E-02 | Unknown ownership on explaining row | 1 | 0 | `[+1 execA unknown unapplied]` | execA_unknown | none | not PENDING; see note | I3 fails | Expect `POSITION_MISMATCH` or `UNKNOWN_ORDER_PRESENT` **per existing product ordering** (matrix records both outcomes as **not** `PARITY_PENDING_ALIGNMENT`). |
| E-03 | Mixed: one owned, one unknown | 2 | 0 | `[+1 execA owned unapplied, +1 execB unknown unapplied]` | mixed | none | not PENDING | I3 | Any unknown in explanation set invalidates PENDING. |
| E-04 | Recoverable-owned (if product defines it) | 1 | 0 | `[+1 execA recoverable_owned unapplied]` | recoverable_owned | none | PARITY_PENDING_ALIGNMENT **or** not PENDING | I3 | **Needs decision:** recoverable counts as trusted or not (Section 5). |
| E-05 | Manual / off-system fill in pending set | 1 | 0 | `[+1 execM manual unapplied]` | untrusted | none | not PENDING | I3 | Same as unknown. |
| **Group F — Identity validity (stale / recycle)** |
| F-01 | Pending says unapplied but journal already has execA | 1 | 1 | `[+1 execA owned unapplied]` (stale) | all_owned | execA applied | POSITION_MISMATCH **or** PARITY_OK after prune | I5 | If broker=journal=1 but stale +1 row exists: **not PENDING**; after dropping stale row → `PARITY_OK`. If implementation leaves orphan pending with zero delta → must not report PENDING. |
| F-02 | Journal includes execA; pending still lists execA as unapplied | 1 | 1 | `[+1 execA owned unapplied]` | all_owned | execA applied | not PENDING | I5 | Stale row excluded; then OK if no delta. |
| F-03 | Stale +1 from old fill; new fill adds broker +1 without journal | 2 | 1 | `[+1 execA owned stale (execA applied)]` | all_owned | execA applied; execB not applied | POSITION_MISMATCH | I5, I4 | True gap +1 only from execB; stale execA must not count → Σ valid pending 0 vs signed_delta +1 ⇒ MISMATCH (recycle attack). |
| F-04 | Stale row excluded; valid row remains | 2 | 1 | stale execA row + valid `[+1 execB owned unapplied]` | all_owned | execA applied | PARITY_PENDING_ALIGNMENT | I5 | Only execB counts. |
| **Group G — Partial fills / aggregation** |
| G-01 | Two partials same order, both unapplied, net +2 | 2 | 0 | `[+1 execA1 owned unapplied, +1 execA2 owned unapplied]` (same order, different fill ids) | all_owned | none | PARITY_PENDING_ALIGNMENT | I1,I2,I4,I5 | Two durable fill identities. |
| G-02 | First partial in journal, second only in pending | 1 | 1 | `[+1 execA2 owned unapplied]` | all_owned | execA1 applied (qty 1) | PARITY_PENDING_ALIGNMENT | I4 | signed_delta = +1. |
| G-03 | Both partials applied in journal, pending orphan | 1 | 1 | `[+1 execA2 owned unapplied]` stale | all_owned | execA1+execA2 applied | PARITY_OK after prune | I5 | Stale pending must not create PENDING. |
| G-04 | Partial fill pending +1 but journal already shows full cum qty for that order | 0 | 0 | `[+1 fill owned unapplied]` | all_owned | order fully applied per journal | PARITY_OK / MISMATCH | I5 | If journal already full, pending is stale → not PENDING. |
| **Group H — Restart / recovery** |
| H-01 | No persisted pending; broker/journal disagree | 1 | 0 | `[]` (lost on restart) | all_owned | none | POSITION_MISMATCH | I2,I4 | Cannot invent PENDING without ledger (**unless** re-derived from durable log on startup—Section 5). |
| H-02 | Persisted pending valid, journal not yet caught | 1 | 0 | `[+1 execA owned unapplied]` restored | all_owned | none | PARITY_PENDING_ALIGNMENT | I1–I5 | **Only if** design persists and restores; else N/A. |
| H-03 | Persisted pending stale (exec already in journal) | 1 | 1 | `[+1 execA owned unapplied]` restored | all_owned | execA applied | not PENDING | I5 | After prune → PARITY_OK. |
| H-04 | Persisted pending over-explains after journal advanced | 1 | 1 | `[+1 execA, +1 execB]` restored but only execA needed | all_owned | execA applied | POSITION_MISMATCH or prune to valid | I2,I5 | Must reconcile. |
| **Group I — Real mismatch protection** |
| I-01 | Broker drift, no pending | 2 | 0 | `[]` | — | none | POSITION_MISMATCH | I2,I4 | Nothing explains +2. |
| I-02 | Broker drift, wrong-direction pending | 2 | 0 | `[-2 execA owned unapplied]` | all_owned | none | POSITION_MISMATCH | I4 | |
| I-03 | Broker drift, stale matching pending only | 2 | 1 | `[+1 execA stale applied]` | all_owned | execA applied | POSITION_MISMATCH | I5 | Same as F-03. |
| I-04 | External/manual position appears (orphan) | 1 | 0 | `[]` | — | none | UNKNOWN_ORDER_PRESENT **or** POSITION_MISMATCH | I3 | **Per existing gate:** if unknown order detected first, expect `UNKNOWN_ORDER_PRESENT`; else `POSITION_MISMATCH`. Neither is PENDING. |
| I-05 | Trusted pending explains full delta but broker is wrong vs exchange (bad snapshot) | 1 | 0 | `[+1 execA owned unapplied]` | all_owned | none | INSUFFICIENT_DATA **or** MISMATCH | — | If inputs unreliable, classification must downgrade (**policy**). |

---

## Section 3 — Pass/fail rules (decision checklist)

| Condition | Result |
|-----------|--------|
| `brokerPos` or `journalOpen` unavailable / not comparable for parity key | **INSUFFICIENT_DATA** (or hold prior classification—**policy gap**) |
| Unknown / untrusted order present per existing rules before parity | **UNKNOWN_ORDER_PRESENT** (no `PARITY_PENDING_ALIGNMENT`) |
| `brokerPos - journalOpen == 0` and no invalid/orphan pending rows | **PARITY_OK** |
| `brokerPos - journalOpen == 0` but stale or invalid pending rows remain | **Not** PENDING; prune stale → **PARITY_OK**; if prune impossible → **POSITION_MISMATCH** (fail-closed) |
| `brokerPos - journalOpen != 0` and any explaining row fails **I3** | **Not** PENDING; **POSITION_MISMATCH** or **UNKNOWN_ORDER_PRESENT** |
| `brokerPos - journalOpen != 0` and any counted row fails **I5** | **Not** PENDING; recompute without stale; then if still `≠ 0` unexplained → **POSITION_MISMATCH** |
| `brokerPos - journalOpen != 0` and **I1** violated (duplicate execution contribution) | **Not** PENDING → **POSITION_MISMATCH** (after dedupe fix if local) |
| `sum(signed pending valid rows) != (brokerPos - journalOpen)` | **Not** PENDING → **POSITION_MISMATCH** (**I4**, **I2**) |
| `brokerPos - journalOpen != 0` and valid set satisfies **I1–I5** exactly | **PARITY_PENDING_ALIGNMENT** |
| Signed match but magnitude pathologies (offsetting rows) inconsistent with **I2** | **POSITION_MISMATCH** |

**Tight safety sentence:** `PARITY_PENDING_ALIGNMENT` **iff** `signed_delta == Σsigned_pending_valid` and **I1,I3,I5** hold on that set and no untrusted/unknown path applies.

---

## Section 4 — Minimal acceptance suite (`REQUIRED_BEFORE_CODING`)

Must pass (automated or scripted)**before** merging implementation:

| ID | Test_ID | Rationale |
|----|---------|-----------|
| R1 | A-01 | Happy path PENDING. |
| R2 | A-02 | OK after catch-up. |
| R3 | A-03 | Baseline OK. |
| R4 | B-01 | Dedup failure must not PENDING. |
| R5 | C-02 | Wrong sign must MISMATCH. |
| R6 | C-04 | Wrong sign negative branch. |
| R7 | D-01 | Under-explained. |
| R8 | D-05 | Over-explained. |
| R9 | D-03 | Valid multi-fill aggregation. |
| R10 | E-02 | Unknown ownership blocks PENDING. |
| R11 | F-03 | Stale recycle must MISMATCH. |
| R12 | F-04 | Stale excluded, valid still PENDING. |
| R13 | G-02 | Partial applied / partial pending. |
| R14 | H-01 | No ledger after restart ⇒ no false PENDING. |
| R15 | I-01 | Drift with no pending ⇒ MISMATCH. |
| R16 | I-03 | Stale-only “match” ⇒ Mismatch. |

---

## Section 5 — Coverage gaps / decisions needed before coding

1. **Persistence:** Does pending ledger **persist across process restart**? If **no**, **H-01** is mandatory fail-closed (`POSITION_MISMATCH` when in-memory pending is empty but delta exists). If **yes**, define durable store and **H-02/H-03/H-04** apply.

2. **Journal “applied” detection:** By **execution id**, **broker order id + fill sequence**, **intent id + sequence**, or **journal watermark per instrument**? **I5** cannot be implemented without this choice.

3. **Recoverable-owned:** Does **E-04** count as **I3** trusted? Binary decision.

4. **Multi-fill single execution:** Can one logical execution produce multiple pending rows (partial fills) vs single row updating quantity? Affects **I1** definition (“one execution” vs “one fill event id”).

5. **UNKNOWN_ORDER_PRESENT vs POSITION_MISMATCH ordering:** Matrix assumes **any** outcome that is **not** `PARITY_PENDING_ALIGNMENT` is safe; exact enum for **E-02/E-03/I-04** must match existing **ExecutionStructuralLayer** / parity caller contract.

6. **INSUFFICIENT_DATA:** When market/broker snapshot stale or missing, whether to suppress PENDING is policy; **I-05** requires explicit product rule.

7. **Per-instrument parity key:** **A-04** assumes isolation; if journal is cross-margined or aggregated differently, clarify scope.

---

**Document status:** Pre-implementation. All expectations trace to **I1–I5**.
