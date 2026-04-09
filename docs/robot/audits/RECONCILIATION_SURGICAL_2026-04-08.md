# Surgical reconciliation audits

- **Logs:** `C:\Users\jakej\QTSW2\logs\robot`
- **Trading date:** `2026-04-08`
- **Incident model:** same as `reconciliation_forensic_reduction.load_incidents_for_day` (**709** high-signal clusters before merge).

## 1 — False-resolution audit

**Population:** **16** merged incidents classified `SUSPICIOUS_FALSE_RESOLUTION` (first resolution-class event before first regression-class event in the same window).

### 1.1

- **Window:** `2026-04-08 13:34:00.621937+00:00` → `2026-04-08 13:46:33.867103+00:00`
- **instruments:** ['MNQ']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T13:34:00.621937+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T13:35:00.636030+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
  - payload fields: `{'journal_qty': '2', 'account_qty': '1'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS (×2) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_PASS_SUMMARY → RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG (×6) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_PASS_SUMMARY → RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG (×77) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_PASS_SUMMARY → RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_CONTEXT

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '2', 'broker_qty': '1'}`
- At regression: `{'journal_qty': '2', 'account_qty': '1'}`
- **Delta resolution → regression:** account_qty: None → '1'; journal_qty: None → '2'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.2

- **Window:** `2026-04-08 17:02:17.818177+00:00` → `2026-04-08 17:47:04.490939+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T17:02:17.818177+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T17:02:44.868916+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_CYCLE_STATE → JOURNAL_INTEGRITY_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → JOURNAL_INTEGRITY_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG → RECONCILIATION_CYCLE_STATE → RECONCILIATION_PASS_SUMMARY → RECONCILIATION_CYCLE_STATE → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG (×5) → JOURNAL_INTEGRITY_RECOVERED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG → RECONCILIATION_CYCLE_STATE → RECONCILIATION_PASS_SUMMARY → RECONCILIATION_CYCLE_STATE → JOURNAL_INTEGRITY_RECOVERED → RECONCILIATION_SCHEDULER_OUTCOME (×2) → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG (×4) → RECONCILIATION_RECOVERED_INTENT_FAILED (×3) → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG → RECONCILIATION_CYCLE_STATE → RECONCILIATION_PASS_SUMMARY → RECONCILIATION_CYCLE_STATE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG → RECONCILIATION_CYCLE_STATE → RECONCILIATION_PASS_SUMMARY → RECONCILIATION_CYCLE_STATE → RECONCILIATION_SCHEDULER_OUTCOME → RECONCILIATION_RECOVERED_INTENT_FAILED (×2) → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG → RECONCILIATION_CYCLE_STATE → … (**35** more distinct event runs)

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'broker_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.3

- **Window:** `2026-04-08 17:47:15.974917+00:00` → `2026-04-08 17:56:11.051438+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T17:48:00.478268+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T17:48:00.963799+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG → RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.4

- **Window:** `2026-04-08 17:56:14.765968+00:00` → `2026-04-08 18:02:00.858629+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T17:56:16.974935+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T17:56:22.954222+00:00` **RECONCILIATION_QTY_MISMATCH_STILL_OPEN** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_CYCLE_STATE → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG (×4) → RECONCILIATION_SCHEDULER_OUTCOME → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG (×2) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED (×6) → RECONCILIATION_PASS_SUMMARY (×6) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED (×3) → RECONCILIATION_PASS_SUMMARY (×3) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED (×3) → RECONCILIATION_PASS_SUMMARY (×3) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.5

- **Window:** `2026-04-08 18:02:00.862005+00:00` → `2026-04-08 18:02:24.459251+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:02:00.862005+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:02:00.863007+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.6

- **Window:** `2026-04-08 18:03:00.873843+00:00` → `2026-04-08 18:03:01.706431+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:03:00.873843+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:03:01.706431+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.7

- **Window:** `2026-04-08 18:03:02.580250+00:00` → `2026-04-08 18:48:02.514093+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:04:00.452700+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:04:00.538047+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.8

- **Window:** `2026-04-08 18:49:01.792433+00:00` → `2026-04-08 18:49:01.826137+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:49:01.792433+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:49:01.826137+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.9

- **Window:** `2026-04-08 18:49:57.974513+00:00` → `2026-04-08 18:50:29.313579+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:50:00.398874+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:50:00.554871+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.10

- **Window:** `2026-04-08 18:51:00.592449+00:00` → `2026-04-08 18:51:00.632385+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:51:00.592449+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:51:00.632385+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.11

- **Window:** `2026-04-08 18:51:00.742079+00:00` → `2026-04-08 18:52:34.881120+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:51:01.496622+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:51:01.682639+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.12

- **Window:** `2026-04-08 18:52:53.949807+00:00` → `2026-04-08 18:53:01.196140+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:52:53.949807+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:53:00.474974+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG (×6) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.13

- **Window:** `2026-04-08 18:54:00.623308+00:00` → `2026-04-08 18:54:09.131162+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:54:00.623308+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:54:01.108508+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_ASSEMBLE_MISMATCH_DIAG → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.14

- **Window:** `2026-04-08 18:55:00.429052+00:00` → `2026-04-08 18:55:01.545856+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:55:00.429052+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:55:00.430074+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.15

- **Window:** `2026-04-08 18:55:11.871405+00:00` → `2026-04-08 19:40:01.139784+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T18:56:00.480048+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T18:56:00.513961+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS (×2) → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

### 1.16

- **Window:** `2026-04-08 19:40:27.774532+00:00` → `2026-04-08 19:53:08.368038+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']

**Anchor events (evidence):**

- **First resolution-class:** `2026-04-08T19:41:00.397911+00:00` **RECONCILIATION_PASS_SUMMARY** inst=`` file=`robot_ENGINE.jsonl`
  - payload fields: `{}`
- **First regression-class after it:** `2026-04-08T19:41:00.528097+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload fields: `{'journal_qty': '3', 'account_qty': '2'}`
- **Distinct event runs between (exclusive):** RECONCILIATION_MISMATCH_METRICS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED

**Qty / gate (payload-only):**

- At first resolution: `{}`
- Last row before regression: `{'journal_qty': '3', 'account_qty': '2'}`
- At regression: `{'journal_qty': '3', 'account_qty': '2'}`
- **Delta resolution → regression:** account_qty: None → '2'; journal_qty: None → '3'
- **Interpretation (conservative):** if resolution event is `JOURNAL_INTEGRITY_RECOVERED` or `PASS_SUMMARY` without `CONVERGED`/`RESOLVED`, treat as **ambiguous semantics** unless product docs define success; compare qty fields above for objective movement.

## 2 — Unresolved-tail audit

**Population:** **11** merged incidents classified `UNRESOLVED`.

### 2.1

- **Window:** `2026-04-08 15:50:33.477184+00:00` → `2026-04-08 15:50:33.477184+00:00`
- **instruments:** —
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764']

- **Last non-noise event in window:** `2026-04-08T15:50:33.477184+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
  - payload: `{}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T15:50:38.837780+00:00` **RECONCILIATION_ASSEMBLE_MISMATCH_DIAG** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** missing/empty `{}`

### 2.2

- **Window:** `2026-04-08 17:56:11.445279+00:00` → `2026-04-08 17:56:11.445279+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['49a42fcce41446c684890cf97d07f49b']

- **Last non-noise event in window:** `2026-04-08T17:56:11.445279+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T17:56:16.974935+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** present `{'journal_qty': '3', 'account_qty': '2'}`

### 2.3

- **Window:** `2026-04-08 17:56:11.445279+00:00` → `2026-04-08 17:56:11.445279+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['761bd7b3ebd0471abeef7594d39dbc4e']

- **Last non-noise event in window:** `2026-04-08T17:56:11.445279+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T17:56:16.974935+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** present `{'journal_qty': '3', 'account_qty': '2'}`

### 2.4

- **Window:** `2026-04-08 18:02:00.862005+00:00` → `2026-04-08 18:02:00.862005+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['bcee97bebfd24ae69f8226a5dda8c027']

- **Last non-noise event in window:** `2026-04-08T18:02:00.862005+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T18:02:00.863007+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** present `{'journal_qty': '3', 'account_qty': '2'}`

### 2.5

- **Window:** `2026-04-08 18:02:31.142726+00:00` → `2026-04-08 18:02:31.142726+00:00`
- **instruments:** —
- **run_ids:** ['3fb902688d504716a89c09edd5d645a5']

- **Last non-noise event in window:** `2026-04-08T18:02:31.142726+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T18:02:36.431668+00:00` **RECONCILIATION_ASSEMBLE_MISMATCH_DIAG** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** missing/empty `{}`

### 2.6

- **Window:** `2026-04-08 18:48:23.740601+00:00` → `2026-04-08 18:48:55.162849+00:00`
- **instruments:** —
- **run_ids:** ['d5f1301efc7f4c40b3c6fde30e87e003']

- **Last non-noise event in window:** `2026-04-08T18:48:55.162849+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T18:49:00.410914+00:00` **RECONCILIATION_ASSEMBLE_MISMATCH_DIAG** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** missing/empty `{}`

### 2.7

- **Window:** `2026-04-08 18:49:26.568642+00:00` → `2026-04-08 18:49:26.568642+00:00`
- **instruments:** —
- **run_ids:** ['d5f1301efc7f4c40b3c6fde30e87e003']

- **Last non-noise event in window:** `2026-04-08T18:49:26.568642+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T18:49:31.794904+00:00` **RECONCILIATION_ASSEMBLE_MISMATCH_DIAG** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** missing/empty `{}`

### 2.8

- **Window:** `2026-04-08 18:49:48.404234+00:00` → `2026-04-08 18:49:48.404234+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764']

- **Last non-noise event in window:** `2026-04-08T18:49:48.404234+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T18:49:50.112304+00:00` **RECONCILIATION_ASSEMBLE_MISMATCH_DIAG** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** present `{'journal_qty': '3', 'account_qty': '2'}`

### 2.9

- **Window:** `2026-04-08 18:53:06.391788+00:00` → `2026-04-08 18:53:37.740010+00:00`
- **instruments:** —
- **run_ids:** ['d5f1301efc7f4c40b3c6fde30e87e003']

- **Last non-noise event in window:** `2026-04-08T18:53:37.740010+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE.jsonl`
  - payload: `{}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T18:53:42.982398+00:00` **RECONCILIATION_ASSEMBLE_MISMATCH_DIAG** inst=`` file=`robot_ENGINE.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** missing/empty `{}`

### 2.10

- **Window:** `2026-04-08 18:54:24.132079+00:00` → `2026-04-08 18:54:24.132079+00:00`
- **instruments:** ['MNG']
- **run_ids:** ['0c27d2236e014f26b176719ab3f19764']

- **Last non-noise event in window:** `2026-04-08T18:54:24.132079+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T18:54:27.464856+00:00` **RECONCILIATION_ASSEMBLE_MISMATCH_DIAG** inst=`` file=`robot_ENGINE.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** present `{'journal_qty': '3', 'account_qty': '2'}`

### 2.11

- **Window:** `2026-04-08 18:54:40.505557+00:00` → `2026-04-08 18:54:40.505557+00:00`
- **instruments:** —
- **run_ids:** ['d5f1301efc7f4c40b3c6fde30e87e003']

- **Last non-noise event in window:** `2026-04-08T18:54:40.505557+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE.jsonl`
  - payload: `{}`
- **Tail looks like stall/gate/no-progress (event name substring):** no
- **Incident end within ~3 min of last event in day extract:** no (day last_ts=`2026-04-08 19:53:11.988915+00:00`)
- **Activity within 45 min after window (same inst or run_id):** yes — first: `2026-04-08T18:54:45.732280+00:00` **RECONCILIATION_ASSEMBLE_MISMATCH_DIAG** inst=`` file=`robot_ENGINE.jsonl`
  - (+24 more lines in window)
- **Broker/journal fields at last non-noise row:** missing/empty `{}`

## 3 — MNG deep dive

**Merged incidents touching MNG:** **23**

**Of those, with at least one `RECONCILIATION_QTY_MISMATCH` / `STILL_OPEN`:** **14**

### 3.1

- **Window:** `2026-04-08 17:02:17.818177+00:00` → `2026-04-08 17:47:04.490939+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T17:02:44.868916+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** yes

### 3.2

- **Window:** `2026-04-08 17:47:15.974917+00:00` → `2026-04-08 17:56:11.051438+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T17:48:01.463362+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.3

- **Window:** `2026-04-08 17:56:11.445279+00:00` → `2026-04-08 17:56:11.445279+00:00` | **outcome:** `UNRESOLVED`
- **First qty mismatch row:** `2026-04-08T17:56:11.445279+00:00` **RECONCILIATION_QTY_MISMATCH_STILL_OPEN** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** no protective keywords in sampled payloads (not proof of absence)
- **Journal integrity failure lines present:** no

### 3.4

- **Window:** `2026-04-08 17:56:14.765968+00:00` → `2026-04-08 18:02:00.858629+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T17:56:22.954222+00:00` **RECONCILIATION_QTY_MISMATCH_STILL_OPEN** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** no protective keywords in sampled payloads (not proof of absence)
- **Journal integrity failure lines present:** no

### 3.5

- **Window:** `2026-04-08 18:02:00.862005+00:00` → `2026-04-08 18:02:24.459251+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:02:00.862005+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.6

- **Window:** `2026-04-08 18:03:00.873843+00:00` → `2026-04-08 18:03:01.706431+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:03:00.873843+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.7

- **Window:** `2026-04-08 18:03:02.580250+00:00` → `2026-04-08 18:48:02.514093+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:04:01.175783+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.8

- **Window:** `2026-04-08 18:49:01.792433+00:00` → `2026-04-08 18:49:01.826137+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:49:01.792433+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.9

- **Window:** `2026-04-08 18:51:00.592449+00:00` → `2026-04-08 18:51:00.632385+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:51:00.592449+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.10

- **Window:** `2026-04-08 18:52:53.949807+00:00` → `2026-04-08 18:53:01.196140+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:53:00.621560+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.11

- **Window:** `2026-04-08 18:54:00.623308+00:00` → `2026-04-08 18:54:09.131162+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:54:00.623308+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.12

- **Window:** `2026-04-08 18:55:00.429052+00:00` → `2026-04-08 18:55:01.545856+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:55:01.545856+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.13

- **Window:** `2026-04-08 18:55:11.871405+00:00` → `2026-04-08 19:40:01.139784+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T18:57:03.944412+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### 3.14

- **Window:** `2026-04-08 19:40:27.774532+00:00` → `2026-04-08 19:53:08.368038+00:00` | **outcome:** `SUSPICIOUS_FALSE_RESOLUTION`
- **First qty mismatch row:** `2026-04-08T19:41:02.094881+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE.jsonl`
  - payload: `{'journal_qty': '3', 'account_qty': '2'}`
- **Protectives (payload keyword scan):** payload mentions protective/OCO/bracket-style language (substring)
- **Journal integrity failure lines present:** no

### Per `run_id` — MNG regression-like events (global day, instrument=MNG)

- `49a42fcce41446c684890cf97d07f49b`: **227** regression-class lines (`QTY_MISMATCH` / `STILL_OPEN` / `STUCK`)
- `bcee97bebfd24ae69f8226a5dda8c027`: **109** regression-class lines (`QTY_MISMATCH` / `STILL_OPEN` / `STUCK`)
- `53062f5338d74a56934dd9991b9475dc`: **107** regression-class lines (`QTY_MISMATCH` / `STILL_OPEN` / `STUCK`)
- `761bd7b3ebd0471abeef7594d39dbc4e`: **106** regression-class lines (`QTY_MISMATCH` / `STILL_OPEN` / `STUCK`)
- `3fb902688d504716a89c09edd5d645a5`: **102** regression-class lines (`QTY_MISMATCH` / `STILL_OPEN` / `STUCK`)
- `d5f1301efc7f4c40b3c6fde30e87e003`: **102** regression-class lines (`QTY_MISMATCH` / `STILL_OPEN` / `STUCK`)
- `0c27d2236e014f26b176719ab3f19764`: **93** regression-class lines (`QTY_MISMATCH` / `STILL_OPEN` / `STUCK`)

*Repeated high counts suggest the same run context saw multiple regression signals (not necessarily separate reconciliation failures).* 
