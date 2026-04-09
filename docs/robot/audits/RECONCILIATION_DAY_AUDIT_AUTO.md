# Reconciliation audit (log-backed)
- **log_dir:** `C:\Users\jakej\QTSW2\logs\robot`
- **trading_date:** `2026-04-08` (`trading_date` field **or** Chicago date from `ts_utc` when field empty)
- **clusters (all events):** 2677 | **high-signal clusters:** showing **100** of **621** (gap > 12.0 min or instrument change)

## SECTION 1 — Scope
Sources: `robot_ENGINE.jsonl`, `robot_ENGINE_*.jsonl`, `archive/robot_ENGINE*.jsonl`.

## SECTION 2 — Raw extraction summary
- `RECONCILIATION_ASSEMBLE_MISMATCH_DIAG`: **35508**
- `RECONCILIATION_MISMATCH_METRICS`: **5933**
- `RECONCILIATION_PASS_SUMMARY`: **2614**
- `JOURNAL_INTEGRITY_FAILED`: **2095**
- `RECONCILIATION_JOURNAL_INTEGRITY_FAILED`: **2087**
- `RECONCILIATION_SCHEDULER_OUTCOME`: **2052**
- `RECONCILIATION_STUCK`: **660**
- `RECONCILIATION_SECONDARY_INSTANCE_SKIPPED`: **626**
- `RECONCILIATION_ORDER_SOURCE_BREAKDOWN`: **467**
- `RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT`: **211**
- `RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH`: **210**
- `JOURNAL_INTEGRITY_INVARIANT_CYCLE`: **186**
- `RECONCILIATION_CYCLE_STATE`: **112**
- `RECOVERY_SCOPE_CLASSIFIED`: **107**
- `RECONCILIATION_CONTEXT`: **106**
- `RECONCILIATION_QTY_MISMATCH`: **106**
- `RECONCILIATION_RECOVERED_INTENT_FAILED`: **80**
- `IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED`: **36**
- `IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS`: **33**
- `JOURNAL_INTEGRITY_RECOVERED`: **17**
- `RECONCILIATION_CONVERGED`: **8**
- `STARTUP_RECONCILIATION_REPORT`: **7**
- `RECONCILIATION_QTY_MISMATCH_STILL_OPEN`: **4**
- `RECONCILIATION_RESOLVED`: **1**

**Note:** Diagnostic noise (`RECONCILIATION_ASSEMBLE_MISMATCH_DIAG`, `RECONCILIATION_CYCLE_STATE`, `RECONCILIATION_MISMATCH_METRICS`, `RECONCILIATION_PASS_SUMMARY`, `RECONCILIATION_SCHEDULER_OUTCOME`): **46219** lines (omitted from incident traces; still counted in §2).

## SECTION 3–4 — Incidents (clustered, high-signal only)
*Incident sections capped by `--max-incidents 100` (100 shown, 621 total). Re-run without the flag for the full list.*


### Incident 1
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['2fd94587f3f3470b94728e519a496923', '8477095ccdc443ffb7f0b6f8867090db'] |
| start_ts_utc | 2026-04-08T02:39:58.202582+00:00 |
| end_ts_utc | 2026-04-08T02:40:07.270996+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T02:39:58.202582+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_024005.jsonl`
- `2026-04-08T02:40:07.270996+00:00` **RECONCILIATION_RESOLVED** inst=`` file=`robot_ENGINE_20260408_104417.jsonl`

### Incident 2
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T10:03:43.781792+00:00 |
| end_ts_utc | 2026-04-08T10:03:48.244784+00:00 |
| events | 7 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'STARTUP_RECONCILIATION_REPORT', 'fields': {'broker_qty': '0', 'journal_qty': '0'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T10:03:43.781792+00:00` → `2026-04-08T10:03:48.244784+00:00` **STARTUP_RECONCILIATION_REPORT** inst=`` file=`robot_ENGINE_20260408_104417.jsonl` **(×7)**

### Incident 3
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T12:30:03.353763+00:00 |
| end_ts_utc | 2026-04-08T13:33:28.157171+00:00 |
| events | 329 |
| outcome_guess (event names only) | integrity_repair_signal |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T12:30:03.353763+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.353763+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.415550+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:03.863923+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:05.892079+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:06.153826+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:06.153826+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.366942+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.366942+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:08.392254+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:10.767953+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:10.767953+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.397530+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.739048+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:13.739048+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` → `2026-04-08T12:30:15.993745+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl` **(×2)**
- `2026-04-08T12:30:15.993745+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T12:30:15.993745+00:00` → `2026-04-08T12:30:15.993745+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_134952.jsonl` **(×2)**
- `2026-04-08T12:30:15.993745+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- … **125** more RLE group(s) in this cluster (329 raw events; see JSONL by time range)

### Incident 4
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:34:00.621937+00:00 |
| end_ts_utc | 2026-04-08T13:34:00.621937+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_CONTEXT', 'fields': {'broker_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:34:00.621937+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:34:00.621937+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 5
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:34:00.621937+00:00 |
| end_ts_utc | 2026-04-08T13:34:00.621937+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:34:00.621937+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 6
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc'] (+2 more) |
| start_ts_utc | 2026-04-08T13:34:00.717133+00:00 |
| end_ts_utc | 2026-04-08T13:35:00.636030+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:34:00.717133+00:00` → `2026-04-08T13:35:00.636030+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl` **(×4)**
- `2026-04-08T13:35:00.636030+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:35:00.636030+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 7
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:35:00.636030+00:00 |
| end_ts_utc | 2026-04-08T13:35:00.636030+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:35:00.636030+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 8
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc'] (+2 more) |
| start_ts_utc | 2026-04-08T13:35:00.728141+00:00 |
| end_ts_utc | 2026-04-08T13:36:00.742099+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:35:00.728141+00:00` → `2026-04-08T13:36:00.650851+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl` **(×4)**
- `2026-04-08T13:36:00.742099+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:36:00.742099+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 9
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:36:00.742099+00:00 |
| end_ts_utc | 2026-04-08T13:36:00.742099+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:36:00.742099+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 10
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T13:36:01.398955+00:00 |
| end_ts_utc | 2026-04-08T13:38:00.620637+00:00 |
| events | 8 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:36:01.398955+00:00` → `2026-04-08T13:38:00.562085+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl` **(×6)**
- `2026-04-08T13:38:00.620637+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:38:00.620637+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 11
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:38:00.620637+00:00 |
| end_ts_utc | 2026-04-08T13:38:00.620637+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:38:00.620637+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 12
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T13:38:00.959482+00:00 |
| end_ts_utc | 2026-04-08T13:40:00.616320+00:00 |
| events | 11 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:38:00.959482+00:00` → `2026-04-08T13:40:00.588090+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl` **(×9)**
- `2026-04-08T13:40:00.616320+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:40:00.616320+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 13
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:40:00.616320+00:00 |
| end_ts_utc | 2026-04-08T13:40:00.616320+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:40:00.616320+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 14
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T13:40:00.616320+00:00 |
| end_ts_utc | 2026-04-08T13:41:04.131816+00:00 |
| events | 14 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:40:00.616320+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:40:00.651261+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:40:00.651261+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:00.539176+00:00` → `2026-04-08T13:41:00.574727+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl` **(×2)**
- `2026-04-08T13:41:00.574727+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:00.575727+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:00.575727+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:00.636588+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:00.636588+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:00.730004+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:00.730004+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:04.131816+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:04.131816+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 15
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T13:41:13.359423+00:00 |
| end_ts_utc | 2026-04-08T13:41:21.476517+00:00 |
| events | 34 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:41:13.359423+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:13.359423+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:13.359423+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:13.359423+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:13.646182+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:13.646182+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:13.646182+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.084341+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.084341+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.084341+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.225103+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.225103+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.225103+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.522651+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.522651+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:14.522651+00:00` → `2026-04-08T13:41:15.142194+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl` **(×2)**
- `2026-04-08T13:41:16.376576+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:16.376576+00:00` → `2026-04-08T13:41:16.483875+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl` **(×2)**
- `2026-04-08T13:41:17.290744+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:17.290744+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:17.290744+00:00` → `2026-04-08T13:41:17.715491+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl` **(×2)**
- `2026-04-08T13:41:18.150619+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:18.150619+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:41:18.150619+00:00` → `2026-04-08T13:41:21.476517+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl` **(×8)**

### Incident 16
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:42:00.614757+00:00 |
| end_ts_utc | 2026-04-08T13:42:00.614757+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_CONTEXT', 'fields': {'broker_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:42:00.614757+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:42:00.614757+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 17
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:42:00.614757+00:00 |
| end_ts_utc | 2026-04-08T13:42:00.614757+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:42:00.614757+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 18
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T13:42:00.614757+00:00 |
| end_ts_utc | 2026-04-08T13:43:00.655393+00:00 |
| events | 15 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:42:00.614757+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:42:00.696047+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:42:00.696047+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:42:01.055427+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:42:01.055427+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:42:04.349412+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:42:04.349412+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:43:00.654376+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:43:00.654376+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:43:00.655393+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:43:00.655393+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:43:00.655393+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:43:00.655393+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:43:00.655393+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:43:00.655393+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 19
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:43:00.655393+00:00 |
| end_ts_utc | 2026-04-08T13:43:00.655393+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:43:00.655393+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 20
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T13:43:00.655393+00:00 |
| end_ts_utc | 2026-04-08T13:44:00.808325+00:00 |
| events | 5 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:43:00.655393+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:44:00.616720+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:44:00.616720+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:44:00.808325+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:44:00.808325+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 21
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:44:00.808325+00:00 |
| end_ts_utc | 2026-04-08T13:44:00.808325+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:44:00.808325+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 22
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc'] (+3 more) |
| start_ts_utc | 2026-04-08T13:44:00.808325+00:00 |
| end_ts_utc | 2026-04-08T13:46:00.553487+00:00 |
| events | 13 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:44:00.808325+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:44:00.963104+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:44:00.963104+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:44:41.833971+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:44:41.833971+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:45:00.558810+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:45:00.558810+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:45:00.585282+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:45:00.585282+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:45:00.586444+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:45:00.586444+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.553487+00:00` **RECONCILIATION_CONTEXT** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.553487+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 23
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5'] |
| start_ts_utc | 2026-04-08T13:46:00.553487+00:00 |
| end_ts_utc | 2026-04-08T13:46:00.553487+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:46:00.553487+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 24
| Field | Value |
|---|---|
| instruments | ['MNQ'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T13:46:00.553487+00:00 |
| end_ts_utc | 2026-04-08T13:46:11.960933+00:00 |
| events | 13 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '1', 'journal_qty': '2'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:46:00.553487+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.627996+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.627996+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.629404+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.629404+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.749653+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.749653+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.803417+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.803417+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.901865+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:00.901865+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:11.960933+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:11.960933+00:00` **RECONCILIATION_STUCK** inst=`MNQ` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 25
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', 'bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T13:46:32.192403+00:00 |
| end_ts_utc | 2026-04-08T13:46:33.867103+00:00 |
| events | 2 |
| outcome_guess (event names only) | integrity_repair_signal |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T13:46:32.192403+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`
- `2026-04-08T13:46:33.867103+00:00` **JOURNAL_INTEGRITY_RECOVERED** inst=`` file=`robot_ENGINE_20260408_134952.jsonl`

### Incident 26
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T15:00:01.674437+00:00 |
| end_ts_utc | 2026-04-08T15:29:47.011392+00:00 |
| events | 4632 |
| outcome_guess (event names only) | integrity_repair_signal |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:01.674437+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:02.854930+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:03.218037+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:03.218037+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:04.395569+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.397447+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.557701+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:05.557701+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **RECONCILIATION_IEA_OWNED_WORKING_INVARIANT_BREACH** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **RECONCILIATION_RECOVERY_ADOPTION_ATTEMPT** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **RECONCILIATION_JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- `2026-04-08T15:00:07.401983+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`
- … **4549** more RLE group(s) in this cluster (4632 raw events; see JSONL by time range)

### Incident 27
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['0c27d2236e014f26b176719ab3f19764'] |
| start_ts_utc | 2026-04-08T15:50:33.477184+00:00 |
| end_ts_utc | 2026-04-08T15:50:33.477184+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T15:50:33.477184+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_161122.jsonl`

### Incident 28
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] (+4 more) |
| start_ts_utc | 2026-04-08T17:02:17.818177+00:00 |
| end_ts_utc | 2026-04-08T17:02:41.808820+00:00 |
| events | 82 |
| outcome_guess (event names only) | integrity_repair_signal |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:02:17.818177+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:17.818177+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:17.818177+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:17.818177+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:17.818177+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:17.997384+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:17.997384+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:17.997384+00:00` **JOURNAL_INTEGRITY_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:17.997384+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:18.696638+00:00` **JOURNAL_INTEGRITY_RECOVERED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:18.696638+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:18.696638+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:18.696638+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:18.696638+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:18.696638+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:18.696638+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:18.696638+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:20.164960+00:00` **JOURNAL_INTEGRITY_RECOVERED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:24.067238+00:00` → `2026-04-08T17:02:26.396209+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:26.396209+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.396209+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.396209+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.396209+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.396209+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.396209+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.396209+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.666182+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.666182+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.666182+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.666182+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.666182+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.666182+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.666182+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:26.666182+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:27.962006+00:00` → `2026-04-08T17:02:28.740415+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**
- `2026-04-08T17:02:28.740415+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:28.740415+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:28.740415+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:28.740415+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:28.740415+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:28.740415+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:28.740415+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:28.771934+00:00` → `2026-04-08T17:02:28.771934+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:28.771934+00:00` → `2026-04-08T17:02:28.771934+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:28.771934+00:00` → `2026-04-08T17:02:28.771934+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:28.771934+00:00` → `2026-04-08T17:02:28.771934+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:28.771934+00:00` → `2026-04-08T17:02:28.771934+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:28.771934+00:00` → `2026-04-08T17:02:28.771934+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:28.771934+00:00` → `2026-04-08T17:02:28.771934+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:28.771934+00:00` → `2026-04-08T17:02:28.771934+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:02:36.975692+00:00` **IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.252685+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.252685+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.649868+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.649868+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.808820+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.808820+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.808820+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.808820+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.808820+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.808820+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.808820+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:41.808820+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 29
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:02:44.868916+00:00 |
| end_ts_utc | 2026-04-08T17:02:44.868916+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_CONTEXT', 'fields': {'broker_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:02:44.868916+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:44.868916+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 30
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:02:44.868916+00:00 |
| end_ts_utc | 2026-04-08T17:02:44.868916+00:00 |
| events | 3 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:02:44.868916+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:44.868916+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:44.868916+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 31
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['761bd7b3ebd0471abeef7594d39dbc4e'] |
| start_ts_utc | 2026-04-08T17:02:45.106975+00:00 |
| end_ts_utc | 2026-04-08T17:02:45.106975+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:02:45.106975+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 32
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc'] (+3 more) |
| start_ts_utc | 2026-04-08T17:02:45.106975+00:00 |
| end_ts_utc | 2026-04-08T17:02:49.501033+00:00 |
| events | 36 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:02:45.106975+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:45.106975+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:46.578319+00:00` **IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.333374+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.426662+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.426662+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.426662+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.426662+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.426662+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.426662+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.426662+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.426662+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.449839+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.449839+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.449839+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.449839+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.449839+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.449839+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.449839+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.449839+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.463208+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.463208+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.463208+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.463208+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.463208+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.463208+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.463208+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.463208+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.501033+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.501033+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.501033+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.501033+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.501033+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.501033+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.501033+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:02:49.501033+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 33
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc', 'bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T17:03:02.805721+00:00 |
| end_ts_utc | 2026-04-08T17:03:02.805721+00:00 |
| events | 3 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:03:02.805721+00:00` → `2026-04-08T17:03:02.805721+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**

### Incident 34
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc', 'bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T17:03:02.805721+00:00 |
| end_ts_utc | 2026-04-08T17:03:02.805721+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:03:02.805721+00:00` → `2026-04-08T17:03:02.805721+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:03:02.805721+00:00` → `2026-04-08T17:03:02.805721+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**

### Incident 35
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:03:03.031165+00:00 |
| end_ts_utc | 2026-04-08T17:03:03.031165+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:03:03.031165+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 36
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['761bd7b3ebd0471abeef7594d39dbc4e', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:03:03.031165+00:00 |
| end_ts_utc | 2026-04-08T17:03:52.349067+00:00 |
| events | 4 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:03:03.031165+00:00` **RECONCILIATION_RECOVERED_INTENT_FAILED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:03:03.031165+00:00` **JOURNAL_INTEGRITY_INVARIANT_CYCLE** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:03:21.039858+00:00` → `2026-04-08T17:03:52.349067+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 37
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e'] |
| start_ts_utc | 2026-04-08T17:04:00.496478+00:00 |
| end_ts_utc | 2026-04-08T17:04:00.825220+00:00 |
| events | 3 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:04:00.496478+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:04:00.825220+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:04:00.825220+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 38
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:04:00.825220+00:00 |
| end_ts_utc | 2026-04-08T17:04:00.825220+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:04:00.825220+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 39
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '49a42fcce41446c684890cf97d07f49b', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:04:00.825220+00:00 |
| end_ts_utc | 2026-04-08T17:04:07.669624+00:00 |
| events | 3 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:04:00.825220+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:04:06.120481+00:00` → `2026-04-08T17:04:07.669624+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 40
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['761bd7b3ebd0471abeef7594d39dbc4e'] |
| start_ts_utc | 2026-04-08T17:04:23.667463+00:00 |
| end_ts_utc | 2026-04-08T17:04:55.022365+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:04:23.667463+00:00` → `2026-04-08T17:04:55.022365+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 41
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e'] (+1 more) |
| start_ts_utc | 2026-04-08T17:05:00.543701+00:00 |
| end_ts_utc | 2026-04-08T17:05:01.421323+00:00 |
| events | 5 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:05:00.543701+00:00` → `2026-04-08T17:05:01.419314+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:05:01.421323+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:05:01.421323+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 42
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:05:01.421323+00:00 |
| end_ts_utc | 2026-04-08T17:05:01.421323+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:05:01.421323+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 43
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc'] |
| start_ts_utc | 2026-04-08T17:05:01.421323+00:00 |
| end_ts_utc | 2026-04-08T17:05:16.769251+00:00 |
| events | 3 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:05:01.421323+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:05:01.923063+00:00` → `2026-04-08T17:05:16.769251+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 44
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e'] |
| start_ts_utc | 2026-04-08T17:05:26.349585+00:00 |
| end_ts_utc | 2026-04-08T17:05:56.276613+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:05:26.349585+00:00` → `2026-04-08T17:05:56.276613+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 45
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:06:00.571879+00:00 |
| end_ts_utc | 2026-04-08T17:06:03.544294+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:06:00.571879+00:00` → `2026-04-08T17:06:03.544294+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 46
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['53062f5338d74a56934dd9991b9475dc'] |
| start_ts_utc | 2026-04-08T17:06:28.130973+00:00 |
| end_ts_utc | 2026-04-08T17:06:59.928912+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:06:28.130973+00:00` → `2026-04-08T17:06:59.928912+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 47
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e'] (+2 more) |
| start_ts_utc | 2026-04-08T17:07:00.540956+00:00 |
| end_ts_utc | 2026-04-08T17:07:02.088543+00:00 |
| events | 8 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:07:00.540956+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:07:00.540956+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:07:00.544094+00:00` → `2026-04-08T17:07:00.766914+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl` **(×3)**
- `2026-04-08T17:07:00.766914+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:07:02.088543+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:07:02.088543+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 48
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:07:02.088543+00:00 |
| end_ts_utc | 2026-04-08T17:07:02.088543+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:07:02.088543+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 49
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:07:02.088543+00:00 |
| end_ts_utc | 2026-04-08T17:07:08.506859+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:07:02.088543+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:07:08.506859+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 50
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:07:31.724995+00:00 |
| end_ts_utc | 2026-04-08T17:07:31.724995+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:07:31.724995+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 51
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:08:01.028876+00:00 |
| end_ts_utc | 2026-04-08T17:08:02.088490+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:08:01.028876+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:08:01.028876+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:08:01.491677+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:08:01.491677+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:08:02.088490+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:08:02.088490+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 52
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:08:03.381375+00:00 |
| end_ts_utc | 2026-04-08T17:08:35.006254+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:08:03.381375+00:00` → `2026-04-08T17:08:35.006254+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 53
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T17:09:00.462150+00:00 |
| end_ts_utc | 2026-04-08T17:09:01.058680+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:09:00.462150+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:00.462150+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:01.023714+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:01.023714+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:01.058680+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:01.058680+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 54
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:09:01.058680+00:00 |
| end_ts_utc | 2026-04-08T17:09:01.058680+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:09:01.058680+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 55
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:09:01.058680+00:00 |
| end_ts_utc | 2026-04-08T17:09:04.462270+00:00 |
| events | 5 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:09:01.058680+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:04.149861+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:04.149861+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:04.462270+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:09:04.462270+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 56
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:09:06.645396+00:00 |
| end_ts_utc | 2026-04-08T17:09:38.239247+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:09:06.645396+00:00` → `2026-04-08T17:09:38.239247+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 57
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['53062f5338d74a56934dd9991b9475dc', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:10:00.490849+00:00 |
| end_ts_utc | 2026-04-08T17:10:00.561651+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:10:00.490849+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:10:00.490849+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:10:00.490849+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:10:00.490849+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:10:00.561651+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:10:00.561651+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 58
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:10:09.835541+00:00 |
| end_ts_utc | 2026-04-08T17:10:09.835541+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:10:09.835541+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 59
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764'] |
| start_ts_utc | 2026-04-08T17:10:18.728824+00:00 |
| end_ts_utc | 2026-04-08T17:10:18.728824+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:10:18.728824+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:10:18.728824+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 60
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:10:41.336059+00:00 |
| end_ts_utc | 2026-04-08T17:10:41.336059+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:10:41.336059+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 61
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc'] (+2 more) |
| start_ts_utc | 2026-04-08T17:11:00.507000+00:00 |
| end_ts_utc | 2026-04-08T17:11:01.134543+00:00 |
| events | 10 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:11:00.507000+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:00.507000+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:00.669699+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:00.669699+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:00.792748+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:00.792748+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:01.134543+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:01.134543+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:01.134543+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:01.134543+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 62
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:11:01.134543+00:00 |
| end_ts_utc | 2026-04-08T17:11:01.134543+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:11:01.134543+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 63
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:11:01.134543+00:00 |
| end_ts_utc | 2026-04-08T17:11:01.230320+00:00 |
| events | 3 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:11:01.134543+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:01.230320+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:11:01.230320+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 64
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:11:12.755342+00:00 |
| end_ts_utc | 2026-04-08T17:11:44.147280+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:11:12.755342+00:00` → `2026-04-08T17:11:44.147280+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 65
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['53062f5338d74a56934dd9991b9475dc', 'bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T17:12:00.600198+00:00 |
| end_ts_utc | 2026-04-08T17:12:00.735486+00:00 |
| events | 4 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:12:00.600198+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:12:00.600198+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:12:00.735486+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:12:00.735486+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 66
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:12:15.533958+00:00 |
| end_ts_utc | 2026-04-08T17:12:15.533958+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:12:15.533958+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 67
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764'] |
| start_ts_utc | 2026-04-08T17:12:43.757279+00:00 |
| end_ts_utc | 2026-04-08T17:12:43.757279+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:12:43.757279+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:12:43.757279+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 68
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:12:46.896442+00:00 |
| end_ts_utc | 2026-04-08T17:12:46.896442+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:12:46.896442+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 69
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e'] |
| start_ts_utc | 2026-04-08T17:13:00.446060+00:00 |
| end_ts_utc | 2026-04-08T17:13:00.792566+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:13:00.446060+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:13:00.446060+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:13:00.579009+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:13:00.579009+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:13:00.792566+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:13:00.792566+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 70
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:13:00.792566+00:00 |
| end_ts_utc | 2026-04-08T17:13:00.792566+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:13:00.792566+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 71
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:13:00.792566+00:00 |
| end_ts_utc | 2026-04-08T17:13:01.780074+00:00 |
| events | 3 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:13:00.792566+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:13:01.780074+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:13:01.780074+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 72
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:13:18.242594+00:00 |
| end_ts_utc | 2026-04-08T17:13:49.585955+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:13:18.242594+00:00` → `2026-04-08T17:13:49.585955+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 73
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc'] (+2 more) |
| start_ts_utc | 2026-04-08T17:14:00.600333+00:00 |
| end_ts_utc | 2026-04-08T17:14:18.745920+00:00 |
| events | 10 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:14:00.600333+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:00.600333+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:00.976670+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:00.976670+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:01.227732+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:01.227732+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:03.607829+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:03.607829+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:18.745920+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:14:18.745920+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 74
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:14:20.947401+00:00 |
| end_ts_utc | 2026-04-08T17:14:52.303163+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:14:20.947401+00:00` → `2026-04-08T17:14:52.303163+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 75
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:15:00.818506+00:00 |
| end_ts_utc | 2026-04-08T17:15:00.818506+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_CONTEXT', 'fields': {'broker_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:15:00.818506+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:15:00.818506+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 76
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:15:00.818506+00:00 |
| end_ts_utc | 2026-04-08T17:15:00.818506+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:15:00.818506+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 77
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:15:00.818506+00:00 |
| end_ts_utc | 2026-04-08T17:15:05.497793+00:00 |
| events | 3 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:15:00.818506+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:15:05.497793+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:15:05.497793+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 78
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:15:23.908287+00:00 |
| end_ts_utc | 2026-04-08T17:15:55.458160+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:15:23.908287+00:00` → `2026-04-08T17:15:55.458160+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 79
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e'] (+1 more) |
| start_ts_utc | 2026-04-08T17:16:00.490102+00:00 |
| end_ts_utc | 2026-04-08T17:16:00.994965+00:00 |
| events | 8 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:16:00.490102+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:16:00.490102+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:16:00.652237+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:16:00.652237+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:16:00.776129+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:16:00.776129+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:16:00.994965+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:16:00.994965+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 80
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:16:26.896710+00:00 |
| end_ts_utc | 2026-04-08T17:16:26.896710+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:16:26.896710+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 81
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764'] |
| start_ts_utc | 2026-04-08T17:16:40.463572+00:00 |
| end_ts_utc | 2026-04-08T17:16:40.463572+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:16:40.463572+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:16:40.463572+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 82
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['d5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:16:58.254584+00:00 |
| end_ts_utc | 2026-04-08T17:16:58.254584+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:16:58.254584+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 83
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e'] (+1 more) |
| start_ts_utc | 2026-04-08T17:17:00.523637+00:00 |
| end_ts_utc | 2026-04-08T17:17:01.241458+00:00 |
| events | 8 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:17:00.523637+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:00.523637+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:01.100473+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:01.100473+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:01.209045+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:01.209045+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:01.241458+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:01.241458+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 84
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:17:01.241458+00:00 |
| end_ts_utc | 2026-04-08T17:17:01.241458+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:17:01.241458+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 85
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:17:01.241458+00:00 |
| end_ts_utc | 2026-04-08T17:17:04.256768+00:00 |
| events | 5 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:17:01.241458+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:01.560336+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:01.560336+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:04.256768+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:17:04.256768+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 86
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['761bd7b3ebd0471abeef7594d39dbc4e', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:17:29.610209+00:00 |
| end_ts_utc | 2026-04-08T17:17:59.895582+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:17:29.610209+00:00` → `2026-04-08T17:17:59.895582+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl` **(×2)**

### Incident 87
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc'] |
| start_ts_utc | 2026-04-08T17:18:01.951140+00:00 |
| end_ts_utc | 2026-04-08T17:18:10.123234+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:18:01.951140+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:18:01.951140+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:18:02.142363+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:18:02.142363+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:18:10.123234+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:18:10.123234+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 88
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T17:18:30.255591+00:00 |
| end_ts_utc | 2026-04-08T17:18:30.255591+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:18:30.255591+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 89
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T17:19:00.529003+00:00 |
| end_ts_utc | 2026-04-08T17:19:00.529003+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:19:00.529003+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:19:00.529003+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 90
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['53062f5338d74a56934dd9991b9475dc'] |
| start_ts_utc | 2026-04-08T17:19:00.568936+00:00 |
| end_ts_utc | 2026-04-08T17:19:00.568936+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:19:00.568936+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 91
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', '761bd7b3ebd0471abeef7594d39dbc4e'] |
| start_ts_utc | 2026-04-08T17:19:00.587656+00:00 |
| end_ts_utc | 2026-04-08T17:19:00.885838+00:00 |
| events | 4 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:19:00.587656+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:19:00.587656+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:19:00.885838+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:19:00.885838+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 92
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:19:00.885838+00:00 |
| end_ts_utc | 2026-04-08T17:19:00.885838+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:19:00.885838+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 93
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', 'd5f1301efc7f4c40b3c6fde30e87e003'] |
| start_ts_utc | 2026-04-08T17:19:00.885838+00:00 |
| end_ts_utc | 2026-04-08T17:19:08.571460+00:00 |
| events | 5 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_STUCK', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:19:00.885838+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:19:08.459209+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:19:08.459209+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:19:08.571460+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:19:08.571460+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 94
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['53062f5338d74a56934dd9991b9475dc'] |
| start_ts_utc | 2026-04-08T17:19:31.962295+00:00 |
| end_ts_utc | 2026-04-08T17:19:31.962295+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:19:31.962295+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 95
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T17:20:00.484027+00:00 |
| end_ts_utc | 2026-04-08T17:20:00.982777+00:00 |
| events | 6 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:20:00.484027+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:20:00.484027+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:20:00.584333+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:20:00.584333+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:20:00.982777+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:20:00.982777+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 96
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['53062f5338d74a56934dd9991b9475dc'] |
| start_ts_utc | 2026-04-08T17:20:03.340836+00:00 |
| end_ts_utc | 2026-04-08T17:20:03.340836+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:20:03.340836+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 97
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['0c27d2236e014f26b176719ab3f19764'] |
| start_ts_utc | 2026-04-08T17:20:09.012714+00:00 |
| end_ts_utc | 2026-04-08T17:20:09.012714+00:00 |
| events | 2 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:20:09.012714+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:20:09.012714+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 98
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['53062f5338d74a56934dd9991b9475dc'] |
| start_ts_utc | 2026-04-08T17:20:34.715611+00:00 |
| end_ts_utc | 2026-04-08T17:20:34.715611+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:20:34.715611+00:00` **RECONCILIATION_ORDER_SOURCE_BREAKDOWN** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 99
| Field | Value |
|---|---|
| instruments | ['MNG'] |
| run_ids | ['49a42fcce41446c684890cf97d07f49b', 'bcee97bebfd24ae69f8226a5dda8c027'] |
| start_ts_utc | 2026-04-08T17:21:00.653895+00:00 |
| end_ts_utc | 2026-04-08T17:21:00.654897+00:00 |
| events | 4 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |
| qty snapshot (first with fields) | `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}` |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:21:00.653895+00:00` **RECONCILIATION_SECONDARY_INSTANCE_SKIPPED** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:21:00.653895+00:00` **RECONCILIATION_STUCK** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:21:00.654897+00:00` **RECONCILIATION_CONTEXT** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`
- `2026-04-08T17:21:00.654897+00:00` **RECONCILIATION_QTY_MISMATCH** inst=`MNG` file=`robot_ENGINE_20260408_185043.jsonl`

### Incident 100
| Field | Value |
|---|---|
| instruments | — |
| run_ids | ['49a42fcce41446c684890cf97d07f49b'] |
| start_ts_utc | 2026-04-08T17:21:00.654897+00:00 |
| end_ts_utc | 2026-04-08T17:21:00.654897+00:00 |
| events | 1 |
| outcome_guess (event names only) | unknown_outcome_from_event_names |

**Event sequence (chronological, consecutive duplicates collapsed):**

- `2026-04-08T17:21:00.654897+00:00` **RECOVERY_SCOPE_CLASSIFIED** inst=`` file=`robot_ENGINE_20260408_185043.jsonl`

## SECTION 5 — Aggregate
- Total matching events: **53266** (incl. noise)
- High-signal events: **7047**
- Clusters (high-signal): **621** (incident sections emitted: **100**)

## SECTION 6 — System-level (strict)
Causal classification (partial fill / protective / journal lag / etc.) **not** inferred here — requires human review of payloads above. Double-count / stall / escalation require payload fields per line.

## SECTION 7 — Evidence note
Every row above is tied to a JSON line in the listed file at the given `ts_utc`. Re-run with same `--trading-date` for reproducibility.
