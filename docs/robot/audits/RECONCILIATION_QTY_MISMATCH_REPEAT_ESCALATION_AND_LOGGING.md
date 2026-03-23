# Reconciliation qty mismatch — repeat escalation + logging (2026-03-23)

## 1. Logging fix (implemented)

**Problem:** `HandleReconciliationQtyMismatchP2` used  
`ExecutionInstrumentResolver.ResolveExecutionInstrumentKey(account, inst, _executionInstrument)`, which **prefers the host chart** (`_executionInstrument`). On multi-chart accounts, `RECOVERY_SCOPE_CLASSIFIED`, `DESTRUCTIVE_ACTION_EXECUTED`, and `RECONCILIATION_INSTRUMENT_RECOVERY_POLICY_DENIED` logged `execution_instrument_key` = **MES** while the mismatch row was **MNQ/MYM/MNG**, contradicting `RequestRecoveryForInstrument` / adapter routing (`Resolve(..., inst, null)`).

**Change:**

- `execution_instrument_key` is now **`ResolveExecutionInstrumentKey(account, inst, null)`** (same as IEA recovery routing), with fallback `inst.ToUpperInvariant()`.
- **`emitter_host_execution_instrument`** added (nullable): strategy instance that emitted the log row (`_executionInstrument` when set).
- **`instrument`** added on `DESTRUCTIVE_ACTION_EXECUTED` for reconciliation context parity.

**Files:** `RobotCore_For_NinjaTrader/RobotEngine.cs`, `NT_ADDONS/RobotEngine.cs` (mirror).

**Policy note:** `DestructiveActionPolicy` for `RECONCILIATION` does **not** branch on `ExecutionInstrumentKey`; alignment is **observability + attribution snapshot consistency** (`EvaluateReconciliationQuantityMismatch` stores the key on the result).

---

## 2. Why repeated `RECONCILIATION_QTY_MISMATCH` “escalates” without converging

### 2.1 Reconciliation is periodic, not one-shot

`ReconciliationRunner` continues to evaluate account vs journal on each cycle. As long as **`account_qty != journal_qty`**, it can emit **`RECONCILIATION_QTY_MISMATCH`** again and call `onQuantityMismatch` → `HandleReconciliationQtyMismatchP2`. There is **no single “mismatch cleared” latch** at the engine layer that suppresses further critical events while the underlying quantities disagree.

### 2.2 `RECOVERY_ALREADY_ACTIVE` + `RECOVERY_ESCALATED` are expected on repeats

In `InstrumentExecutionAuthority.RecoveryPhase3.RequestRecovery`, when state is **not** `NORMAL`:

1. Log **`RECOVERY_ALREADY_ACTIVE`** (existing state + accumulated reasons).
2. Append `new_reason` to `_recoveryReasons`.
3. Log **`RECOVERY_ESCALATED`**.
4. **Still invoke** `_onRecoveryRequestedCallback` (same as first trigger path for the “already active” branch — see `RecoveryPhase3.cs` ~187–205).

So **repeated reconciliation triggers do not “start recovery again”**; they **stack reasons and re-fire the reconstruction callback**. If the mismatch **never clears** (broker and/or journal unchanged), logs show **escalation without convergence** even when behavior is “working as coded.”

### 2.3 Multi-instance amplification

Each **strategy / chart** with reconciliation enabled can observe the **same** account position for instrument **X** while its **local** journal sum is zero. Multiple engines can therefore call `RequestRecoveryForInstrument(X, …)` in parallel, increasing **`RECOVERY_ALREADY_ACTIVE`** / **`RECOVERY_ESCALATED`** volume for the same logical mismatch.

---

## 3. Classifying repeat escalations (operational)

Use log correlation (same `instrument`, `account_qty`, `journal_qty` on `RECONCILIATION_CONTEXT` / `RECOVERY_SCOPE_CLASSIFIED`) plus the following signatures:

| Hypothesis | Typical log / state evidence |
|------------|------------------------------|
| **Recovery already active** | `RECOVERY_ALREADY_ACTIVE` with `current_state` ∈ `RECOVERY_PENDING`, `RECONSTRUCTING`, `RECOVERY_ACTION_REQUIRED`, `FLATTENING`; repeated `RECOVERY_ESCALATED` with `reason` / `new_reason` including `RECONCILIATION_QTY_MISMATCH`. |
| **Stale broker state not clearing** | `RECONCILIATION_CONTEXT` / mismatch rows still show **`account_qty > 0`** over many cycles after flatten attempts; or position reader vs flatten path disagree (**`FLATTEN_SKIPPED_ACCOUNT_FLAT`** while another path still sees qty). |
| **Journal not updating** | Persistent **`journal_qty == 0`** (or unchanged) while broker shows exposure; no matching open journal rows closed/reconciled after fills. |
| **Flatten not changing broker state** | `NT_ACTION_ENQUEUED` / flatten lifecycle shows submit or skip, but subsequent snapshots still **`account_qty > 0`**; or **`PROTECTIVE_EMERGENCY_FLATTEN`** / thread errors (“requires strategy thread”) preventing execution. |

**Note:** Several buckets often **co-occur** (e.g. journal never catches up **and** recovery stays in `FLATTENING` while reconciliation keeps firing).

---

## 4. Implemented: debounce + single-writer + escalation cap (control layer)

See `modules/robot/core/reconciliation/ReconciliationStateTracker.cs`, `ReconciliationRunner` (runner gate), `RobotEngine` (`_reconciliationWriterInstanceId`, `NotifyMismatchHandlingDispatched`, `RECONCILIATION_RESOLVED` on pass complete), and `InstrumentExecutionAuthority.RecoveryPhase3.RequestRecovery` (suppress duplicate `RECOVERY_ESCALATED` for `RECONCILIATION_QTY_MISMATCH`).

- **`RECONCILIATION_QTY_MISMATCH_STILL_OPEN`** — owner, same qty, within debounce (default 45s) or pre-dispatch coalesce window; no callback / no recovery re-request.
- **`RECONCILIATION_SECONDARY_INSTANCE_SKIPPED`** — non-owner strategy instance; full critical chain skipped.
- **`RECONCILIATION_RESOLVED`** — `account_qty == journal_qty` on pass complete; tracker reset.
- **Metrics** (monotonic): `ReconciliationMismatchTotal`, `ReconciliationDebouncedTotal`, `ReconciliationSecondarySkippedTotal`, `ReconciliationResolvedTotal` on `ReconciliationStateTracker.Shared.Metrics`.

Harness: `--test RECONCILIATION_STATE_TRACKER`.

## 5. Further follow-ups

1. **Stale owner** if the owning chart stops without clearing the episode (optional TTL or heartbeat).
2. **Forensic joins:** pair `RECONCILIATION_QTY_MISMATCH` with **`iea_instance_id`**, **`RECOVERY_REQUEST_RECEIVED`**, **`FLATTEN_SKIPPED_ACCOUNT_FLAT`**, and position snapshot source to separate “reader drift” from “true open position.”

---

## References (code) — updated

- `reconciliation/ReconciliationStateTracker.cs` — debounce, single-writer, metrics, escalation log gate.
- `Execution/ReconciliationRunner.cs` — pre-log gate; `RECONCILIATION_QTY_MISMATCH_STILL_OPEN` / `RECONCILIATION_SECONDARY_INSTANCE_SKIPPED`.
- `RobotEngine.HandleReconciliationQtyMismatchP2` — `NotifyMismatchHandlingDispatched` after each handling branch; `RECONCILIATION_RESOLVED` on pass complete.
- `ExecutionInstrumentResolver.ResolveExecutionInstrumentKey` — third arg prefers host when non-null.
- `NinjaTraderSimAdapter.RequestRecoveryForInstrument` — `Resolve(..., instrument, null)`.
- `InstrumentExecutionAuthority.RecoveryPhase3.RequestRecovery` — at most one `RECOVERY_ESCALATED` per episode for `RECONCILIATION_QTY_MISMATCH`; callback still invoked.
