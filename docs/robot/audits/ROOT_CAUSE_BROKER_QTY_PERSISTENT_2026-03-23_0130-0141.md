# Root-cause audit — Why `broker_qty > 0` with `journal_qty == 0` persisted (MNQ, MYM, MNG)

**Mode:** Read-only forensic audit (code + on-disk logs + one on-disk journal file).  
**No fixes proposed.** Conclusions are evidence-backed; gaps are called out explicitly.

---

## Scope

| Item | Value |
|------|--------|
| **Primary UTC window** | `2026-03-23T01:30:00Z` → `2026-03-23T01:41:00Z` |
| **Extended context** | Used through ~`01:00Z` (prior MNQ flatten / orphan) and through `01:41Z+` where needed |
| **Logs** | `logs/robot/robot_ENGINE.jsonl`, `logs/robot/robot_MNQ.jsonl`, `logs/health/_MNQ_.jsonl` (sampled); `robot_MYM.jsonl` / `robot_MNG.jsonl` not exhaustively re-listed (ENGINE duplicates key rows) |
| **Health** | `logs/health/_MNQ_.jsonl` contains mirrored MNQ rows; no separate health lines required for core conclusions |
| **Journals (disk)** | `data/execution_journals/2026-03-20_NG2_cef6b767b7a307c1.json` (intent tied to incident chain) |

---

## Section 1 — Instrument-by-instrument timelines

### Conventions

- **`account_qty` / `broker_qty`:** From `RECONCILIATION_CONTEXT` / `RECONCILIATION_QTY_MISMATCH` (`broker_qty` or `account_qty` field — same pass).
- **`iea_instance_id`:** From ENGINE payloads where present; `RECOVERY_SCOPE_CLASSIFIED` in this log export often serializes nested objects as string (no separate `iea_instance_id` on that row).
- **`run_id`:** Strategy/engine instance id from JSONL.

### 1.1 MNQ

| timestamp (UTC) | instrument | account_qty | journal_qty | event | run_id | iea_instance_id | notes |
|-----------------|------------|-------------|-------------|-------|--------|-----------------|-------|
| 2026-03-23T01:35:08.8513912Z | MNQ | 1 | 0 | RECONCILIATION_CONTEXT | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | First critical chain in window |
| same | MNQ | 1 | 0 | RECONCILIATION_QTY_MISMATCH | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | |
| same | MNQ | 1 | 0 | POSITION_DRIFT_DETECTED / EXPOSURE_INTEGRITY_VIOLATION | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | |
| same | MNQ | 1 | 0 | RECOVERY_SCOPE_CLASSIFIED | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | Log payload shows `execution_instrument_key = MES` (known misleading host-chart logging on build that produced this log; **routing** uses `RequestRecoveryForInstrument` — see §3) |
| same | MNQ | 1 | 0 | DESTRUCTIVE_ACTION_EXECUTED | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | RECONCILIATION instrument recovery |
| 2026-03-23T01:35:22.7678517Z | MNQ | — | — | RECOVERY_REQUEST_RECEIVED | — | **2** | `reason`: `STALE_QTSW2_ORDER_DETECTED` (ENGINE) |
| 2026-03-23T01:35:22.9848515Z | MNQ | — | — | NT_ACTION_ENQUEUED FLATTEN_INSTRUMENT | `aef22d3142944e2d8fad91857b7058ec` | — | `instrument_key`: **MNQ**, reason `STALE_QTSW2_ORDER_DETECTED` |
| 2026-03-23T01:35:26.2229886Z | MNQ | — | — | NT_ACTION_SUCCESS | `aef22d3142944e2d8fad91857b7058ec` | — | Same correlation `RECOVERY:MNQ:20260323013522767` |
| 2026-03-23T01:35:23.2742378Z | MNQ | — | — | RECOVERY_ALREADY_ACTIVE / RECONCILIATION_QTY_MISMATCH | `aef22d3142944e2d8fad91857b7058ec` | **2** | Recovery already FLATTENING |
| 2026-03-23T01:40:00.545Z / 01:40:00.646Z | MNQ | 1 | 0 | RECONCILIATION_CONTEXT + QTY_MISMATCH + RECOVERY_* | `497fca2a7e624bcebeefa7cd01cc2ba4`, `abbbb954f61345b7baa1d1f2f2f5e23b` | **2** (ESCALATED) | **Still 1 vs 0** after flatten success ~5 min earlier |
| 2026-03-23T01:41:00Z | MNQ | 1 | 0 | RECONCILIATION_CONTEXT | multiple run_ids | — | Persists through end of window |

**Persistence:** From first detection in window through **at least** `01:41:00Z`, `RECONCILIATION_CONTEXT` continues to report **MNQ `broker_qty: 1`, `journal_qty: 0`**.

### 1.2 MYM

| timestamp (UTC) | instrument | account_qty | journal_qty | event | run_id | iea_instance_id | notes |
|-----------------|------------|-------------|-------------|-------|--------|-----------------|-------|
| 2026-03-23T01:35:08.8513912Z | MYM | 2 | 0 | RECONCILIATION_CONTEXT / RECOVERY_SCOPE / DESTRUCTIVE_ACTION | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | MYM lacks first-line RECONCILIATION_QTY_MISMATCH in same second as MNQ in grep slice (MYM still broker_ahead in CONTEXT) |
| 2026-03-23T01:35:11.5707639Z | MYM | 2 | 0 | RECONCILIATION_CONTEXT / RECOVERY_SCOPE | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | RECONCILIATION_IEA_UNAVAILABLE MYM `broker_working=1`; ORDER_REGISTRY_MISSING_FAIL_CLOSED |
| 2026-03-23T01:35:37.4920754Z | MYM | — | — | NT_ACTION_ENQUEUED BOOTSTRAP flatten | `6e1e39fa113c40789cd750d277c022a2` | — | MYM chart IEA **3** binds ~01:35:36 |
| 2026-03-23T01:35:39.0437789Z | MYM | — | — | NT_ACTION_ENQUEUED RECOVERY flatten | `6e1e39fa113c40789cd750d277c022a2` | — | reason `STALE_QTSW2_ORDER_DETECTED` |
| 2026-03-23T01:35:49.4188296Z | MYM | — | — | NT_ACTION_SUCCESS BOOTSTRAP | `6e1e39fa113c40789cd750d277c022a2` | — | |
| 2026-03-23T01:35:50.4716533Z | MYM | — | — | NT_ACTION_SUCCESS RECOVERY | `6e1e39fa113c40789cd750d277c022a2` | — | |
| 2026-03-23T01:36:01.3998569Z | MYM | 2 | 0 | RECONCILIATION_CONTEXT | `6e1e39fa113c40789cd750d277c022a2` | — | **~11s after second NT_ACTION_SUCCESS**, snapshot still **2 vs 0** |
| same | MYM | — | — | MYM attribution row | `6e1e39fa113c40789cd750d277c022a2` | **3** | Same second: `broker_position_qty: 0`, `journal_open_qty: 0` vs CONTEXT `broker_qty: 2` (**reader disagreement** — §3) |
| 2026-03-23T01:40–01:41Z | MYM | 2 | 0 | RECONCILIATION_CONTEXT | multiple | — | Unchanged through window end |

### 1.3 MNG

| timestamp (UTC) | instrument | account_qty | journal_qty | event | run_id | iea_instance_id | notes |
|-----------------|------------|-------------|-------------|-------|--------|-----------------|-------|
| 2026-03-23T01:35:08.8513912Z | MNG | 2 | 0 | RECONCILIATION_CONTEXT / RECOVERY_* | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | |
| 2026-03-23T01:35:11+ | MNG | 2 | 0 | Repeated CONTEXT / RECONCILIATION_IEA_UNAVAILABLE | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | `broker_working: 0` in IEA_UNAVAILABLE rows |
| 2026-03-23T01:35:21.5836099Z | MNG | — | — | EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION | `abbbb954f61345b7baa1d1f2f2f5e23b` | — | “must run on strategy thread” |
| 2026-03-23T01:36:30.9300960Z | MNG | — | — | RECOVERY_REQUEST_RECEIVED | — | **7** | `reason`: `RECONCILIATION_QTY_MISMATCH` |
| 2026-03-23T01:36:39.5779702Z | MNG | — | — | NT_ACTION_ENQUEUED | `4832fce648c5474ba8ec3a12d247a43f` | — | `RECOVERY:MNG:…`, FLATTEN_INSTRUMENT |
| 2026-03-23T01:36:52.2721153Z | MNG | — | — | NT_ACTION_SUCCESS | `4832fce648c5474ba8ec3a12d247a43f` | — | |
| 2026-03-23T01:37:18Z – 01:41:00Z | MNG | 2 | 0 | RECONCILIATION_CONTEXT | many run_ids | — | **Still 2 vs 0 after NT_ACTION_SUCCESS** |

---

## Section 2 — Flatten chain audit (per episode)

**Evidence search:** `NT_ACTION_ENQUEUED`, `NT_ACTION_SUCCESS`, `RECOVERY_FLATTEN_REQUESTED`, `RECOVERY_REQUEST_RECEIVED`, `FLATTEN_REQUESTED` (per-instrument log), `EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION`.  
**Result:** **No** `FLATTEN_SKIPPED_ACCOUNT_FLAT` lines in `robot_ENGINE.jsonl` (grep count: **0** for that string in file).

### MNQ

| timestamp | instrument | flatten_stage | event | reason | run_id | evidence |
|-----------|------------|---------------|-------|--------|--------|----------|
| 01:35:22.9848515Z | MNQ | enqueued | NT_ACTION_ENQUEUED | STALE_QTSW2_ORDER_DETECTED | `aef22d3142944e2d8fad91857b7058ec` | `instrument_key`: MNQ |
| 01:35:26.2229886Z | MNQ | executed (adapter) | NT_ACTION_SUCCESS | (correlation RECOVERY:MNQ:…) | `aef22d3142944e2d8fad91857b7058ec` | Success does not assert broker flat |
| 01:35:08–01:35:11Z | MES | bootstrap + recovery | NT_ACTION_* / RECOVERY_* | BOOTSTRAP + REGISTRY_BROKER_DIVERGENCE | `abbbb954f61345b7baa1d1f2f2f5e23b` | Parallel **MES** flatten activity on MES-host run |

**Broker cleared?** **Not evidenced** by reconciliation: `RECONCILIATION_CONTEXT` still `broker_qty: 1` at 01:40–01:41Z.

### MYM

| timestamp | instrument | flatten_stage | event | reason | run_id | evidence |
|-----------|------------|---------------|-------|--------|--------|----------|
| 01:35:37.4920754Z | MYM | enqueued | NT_ACTION_ENQUEUED | BOOTSTRAP_FLATTEN_THEN_RECONSTRUCT | `6e1e39fa113c40789cd750d277c022a2` | |
| 01:35:39.0437789Z | MYM | enqueued | NT_ACTION_ENQUEUED | STALE_QTSW2_ORDER_DETECTED | `6e1e39fa113c40789cd750d277c022a2` | |
| 01:35:49–01:35:50Z | MYM | executed | NT_ACTION_SUCCESS | both correlations | `6e1e39fa113c40789cd750d277c022a2` | |
| 01:36:01.3998569Z | MYM | post-success snapshot | RECONCILIATION_CONTEXT | — | `6e1e39fa113c40789cd750d277c022a2` | **broker_qty still 2** |

### MNG

| timestamp | instrument | flatten_stage | event | reason | run_id | evidence |
|-----------|------------|---------------|-------|--------|--------|----------|
| 01:35:21.5836099Z | MNG | emergency path blocked | EMERGENCY_FLATTEN_CONSTRAINT_VIOLATION | strategy-thread constraint | `abbbb954f61345b7baa1d1f2f2f5e23b` | Not a successful flatten |
| 01:36:39.5779702Z | MNG | enqueued | NT_ACTION_ENQUEUED | RECONCILIATION_QTY_MISMATCH | `4832fce648c5474ba8ec3a12d247a43f` | |
| 01:36:52.2721153Z | MNG | executed | NT_ACTION_SUCCESS | RECOVERY:MNG:… | `4832fce648c5474ba8ec3a12d247a43f` | |
| 01:37:18Z+ | MNG | post-success reconciliation | RECONCILIATION_CONTEXT | — | multiple | **broker_qty still 2** |

**Cancel / order state (MNQ chain):** `robot_MNQ.jsonl` — `ORDER_REGISTRY_LIFECYCLE` **CANCELED** for `408453625017`, `REGISTRY_BROKER_DIVERGENCE` for `408453625012`, `MANUAL_OR_EXTERNAL_ORDER_DETECTED` / `STALE_QTSW2_ORDER_DETECTED` for intent `cef6b767b7a307c1`.

---

## Section 3 — Broker reader vs flatten reader

### 3.1 Reconciliation `account_qty` / `broker_qty`

**Code:** `modules/robot/core/Execution/ReconciliationRunner.cs` — `RunInternal` calls `_adapter.GetAccountSnapshot(utcNow)`, then sums `PositionSnapshot.Quantity` per instrument root / exec variant.

**Adapter:** `NinjaTraderSimAdapter.GetAccountSnapshot` → `GetAccountSnapshotReal` (`NinjaTraderSimAdapter.cs` ~2223–2250).

### 3.2 Same source?

- **Reconciliation** uses **`GetAccountSnapshot`** (full account positions list).
- **Flatten / IEA pre-checks** use **IEA-scoped** and **instrument-scoped** NT reads inside `NinjaTraderSimAdapter.NT.cs` / IEA executor paths (not identical function entry point).
- **Attribution / recovery** logs in the same second as reconciliation sometimes show **`broker_position_qty: 0`** while **`RECONCILIATION_CONTEXT`** shows **`broker_qty: 2`** (MYM @ `01:36:01.3998569Z`). That is **direct log evidence** of **inconsistent broker quantity** between subsystems in the same processing burst.

### 3.3 Can one path think flat while reconciliation sees qty > 0?

**Yes — evidenced** for MYM at `01:36:01.3998569Z` (CONTEXT `broker_qty: 2` vs MYM row `broker_position_qty: 0`).

**Causes consistent with evidence (ranked as hypotheses, not proven singly):**

1. **Different read APIs / scope** (account snapshot vs IEA `GetAccountPositionForInstrument` / gate snapshot).
2. **Timing / ordering** within the same second (non-atomic reads).
3. **Multi-instrument / multi-chart** aggregation: reconciliation sums **all** positions matching symbol roots across the account snapshot; a per-IEA view may see a subset.

**Caching:** `SnapshotMetricsCollector` wraps snapshot calls; it records latency/count — **not** shown in JSONL as divergent caches. **No log proof** of stale snapshot cache vs live NT; only **cross-field contradiction** proves inconsistency.

### 3.4 `FLATTEN_SKIPPED_ACCOUNT_FLAT`

**Not observed** in `robot_ENGINE.jsonl` for this incident (zero grep hits). **Cannot** claim flatten was skipped for that reason from these logs.

---

## Section 4 — Journal qty audit

### 4.1 How `journal_qty` is computed

**Code:** `ExecutionJournal.GetOpenJournalQuantitySumForInstrument` (`ExecutionJournal.cs` ~1526–1550):

- Loads open journal entries by instrument key.
- Sums `max(0, EntryFilledQuantityTotal - ExitFilledQuantityTotal)` for rows matching `executionInstrument` or optional canonical variant (`ReconciliationRunner` passes `execVariant` e.g. `M` + root).

### 4.2 On-disk journal for incident intent `cef6b767b7a307c1`

**File:** `data/execution_journals/2026-03-20_NG2_cef6b767b7a307c1.json`

| Field | Value |
|-------|--------|
| `Instrument` | `MNG` |
| `Stream` | `NG2` |
| `EntryFilledQuantityTotal` | 2 |
| `ExitFilledQuantityTotal` | 2 |
| `TradeCompleted` | **true** |
| `CompletionReason` | `RECONCILIATION_BROKER_FLAT` |
| `CompletedAtUtc` / `ExitFilledAtUtc` | **2026-03-23T01:02:04.7645568Z** |

So **before** the primary window (`01:30Z`), this journal was already **closed** on disk with **net open qty 0**.

### 4.3 Expected `journal_qty` for MNG?

**If** the engine’s journal cache matches disk: **0** open contracts from this intent — **consistent** with logged `journal_qty: 0`.

### 4.4 Why broker still “ahead” for MNG?

**Evidence-backed:** Reconciliation **continues** to report **`broker_qty: 2`** for **MNG** through **`01:41Z`** even after:

- `NT_ACTION_SUCCESS` for **MNG** flatten at **`01:36:52Z`**.

So **either**:

- **D)** The **account snapshot** used by reconciliation still sees **2 contracts** in positions that match the **MNG** reconciliation key after flatten, **or**
- The flatten **did not close** the exposure that reconciliation attributes to **MNG** (instrument mapping / wrong contract / partial close) — **cannot distinguish** without NT order/execution detail beyond JSONL.

**Not supported by disk journal:** “journal reconstruction gap” **for this specific intent** after `01:02Z` — that row is **completed**.

### 4.5 MNQ / MYM

**No journal file cited** in this audit for MNQ/MYM open exposure. `RECONCILIATION_PASS_SUMMARY` at `01:35:08.851Z` reports `instruments_checked: 0`, `note: "No open journals to reconcile"` — aligns with **journal-driven orphan closure** not running for those keys in that pass, while qty mismatch still fires from **positions**.

---

## Section 5 — Multi-instance contribution

Distinct `run_id` values observed on ENGINE rows in/near the window (non-exhaustive):  
`abbbb954f61345b7baa1d1f2f2f5e23b`, `aef22d3142944e2d8fad91857b7058ec`, `6e1e39fa113c40789cd750d277c022a2`, `4832fce648c5474ba8ec3a12d247a43f`, `497fca2a7e624bcebeefa7cd01cc2ba4`, `a978bf52ccd04d1ba3547b78d290f70c`, `4031182ce1c64d54abab586210876fac`.

| instrument | instances_observing | instances_requesting_recovery / flatten | notes |
|------------|---------------------|-------------------------------------------|-------|
| MNQ | ≥2 (MES host + MNQ chart) | MNQ flatten enqueued on `aef22…` (IEA **2**); MES bootstrap/recovery on `abbbb…` (IEA **1**) | Same account, multiple charts |
| MYM | ≥2 | MYM flatten on `6e1e39…` (IEA **3**) | |
| MNG | ≥3 | MNG recovery flatten on `4832fce…` (IEA **7**); emergency violation on `abbbb…` | |

**RECOVERY_ALREADY_ACTIVE** / **RECOVERY_ESCALATED** appear with long `existing_reasons` strings — **multi-instance amplification** of recovery **requests** is **evidenced**; flatten enqueue is **per IEA** (MNQ/MYM/MNG separate `instrument_key` in `NT_ACTION_ENQUEUED`).

---

## Section 6 — Root cause by instrument (primary bucket)

Use taxonomy from task §5.

| Instrument | Primary bucket | Evidence summary |
|------------|----------------|------------------|
| **MNQ** | **F — Mixed / multi-stage** | (1) **ORDER_REGISTRY_MISSING** / **STALE_QTSW2** / orphan intent `cef6b767…` / **REGISTRY_BROKER_DIVERGENCE** on MNQ IEA. (2) **NT_ACTION_SUCCESS** on MNQ flatten **does not** coincide with reconciliation clearing **broker_qty: 1** by 01:40Z. (3) Parallel **MES** recovery/flatten on another instance. |
| **MYM** | **D** (primary) **with F** (secondary) | **Two** `NT_ACTION_SUCCESS` flatten events **~11s before** `RECONCILIATION_CONTEXT` still shows **2 vs 0**; simultaneous attribution row shows **broker_position_qty: 0** → **inconsistent broker readers** or non-overlapping exposure. Multi-instance MYM IEA **3** + other charts. |
| **MNG** | **D** (primary) **with B** (secondary) | **Emergency flatten path failed** (thread constraint) at `01:35:21Z`. Later **NT_ACTION_SUCCESS** at `01:36:52Z` **without** reconciliation clearing **broker_qty: 2** through `01:41Z`. Journal for `cef6b767…` **already flat** at `01:02Z` — mismatch is **not** “open journal missing fills” for that file. |

---

## Section 7 — Overall primary cause

**Primary (evidence-ranked):**  
**`D` — Flatten executed (adapter reported success) but reconciliation’s account snapshot still showed `broker_qty > 0` for MNQ/MYM/MNG through the end of the window** — **or** the exposure reconciler attributes to those symbols was **not** the exposure the flatten closed (instrument / contract / aggregation mismatch). **MYM** adds **hard log proof** of **contradictory broker qty** in the same second between **RECONCILIATION_CONTEXT** and **IEA attribution** fields.

**Secondary:**  
**`F` — Multi-instance amplification** — many `run_id`s and **RECOVERY_ALREADY_ACTIVE** spam; **MES** vs **MNQ** vs **MYM** vs **MNG** IEAs all active.

**Contributing:**  
- **Stale / divergent broker order state** relative to registry (**STALE_QTSW2_ORDER_DETECTED**, **REGISTRY_BROKER_DIVERGENCE**, **ORDER_REGISTRY_MISSING_FAIL_CLOSED** on MYM).  
- **Emergency flatten unavailable** on strategy thread for **MNG** at `01:35:21Z` (**B** — path blocked, not “skipped flat”).  
- **Prior orphan / cross-chart contamination** (`ORPHAN_FILL_CRITICAL` @ `00:59Z` tying **MES** fill text to **MNG** IEA in ENGINE) — **upstream** of reconciliation noise.

**Explicit non-claims:**

- **Cannot** prove from JSONL alone whether NT actually held a physical position vs snapshot error.  
- **`FLATTEN_SKIPPED_ACCOUNT_FLAT`:** **not evidenced** in these logs.  
- **Journal “wrong” for `cef6b767…` after 01:02Z:** **contradicted by on-disk journal** (`TradeCompleted: true`).

---

## Code references (minimal)

| Topic | Location |
|-------|----------|
| Reconciliation snapshot + qty loop | `ReconciliationRunner.RunInternal` |
| Journal open qty sum | `ExecutionJournal.GetOpenJournalQuantitySumForInstrument` |
| Account snapshot entry | `NinjaTraderSimAdapter.GetAccountSnapshot` (~2223) |
| Recovery routing key | `NinjaTraderSimAdapter.RequestRecoveryForInstrument` — `ResolveExecutionInstrumentKey(_ieaAccountName, instrument, null)` (~749–755) |
| Resolver root-from-string | `ExecutionInstrumentResolver.ResolveExecutionInstrumentKey` (~20–41) |

---

## Related audit

Cross-layer trace for intent `cef6b767b7a307c1`: `docs/robot/audits/INTENT_cef6b767b7a307c1_ORIGIN_TRACE.md` (if present in repo).

---

*Audit generated from repository state as analyzed; log lines cite `logs/robot/robot_ENGINE.jsonl` and paths above.*
