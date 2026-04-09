# Phase 2 forensic reduction — reconciliation audit

- **Source logs:** `C:\Users\jakej\QTSW2\logs\robot`
- **Trading date:** `2026-04-08`
- **Reference:** `docs/robot/audits/RECONCILIATION_DAY_AUDIT_AUTO.md` (broad extraction); cluster count there (~600–650) varies with **log churn** and optional `--max-incidents`; this run rescans **all** JSONL for `2026-04-08`.
- **Method:** High-signal clusters (`gap>12.0 min` or instrument change) = **686**; merged when same instrument set overlap, same run_id overlap where present, and inter-cluster gap ≤ **45.0 min**.

## SECTION 1 — Distinct incident count

- **Total distinct incidents (merged):** **42**
- **High-signal clusters before merge:** **686**

### Distinct incidents by instrument

- `MNG`: **23**
- `__no_instrument__`: **10**
- `MGC`: **8**
- `MNQ`: **1**

### Distinct incidents by run_id (incident may span multiple; counts are per-incident attribution)

- `d5f1301efc7f4c40…` **20**
- `49a42fcce41446c6…` **19**
- `761bd7b3ebd0471a…` **18**
- `bcee97bebfd24ae6…` **18**
- `0c27d2236e014f26…` **17**
- `3fb902688d504716…` **16**
- `53062f5338d74a56…` **16**
- `2fd94587f3f3470b…` **1**
- `8477095ccdc443ff…` **1**

## SECTION 2 — Outcome classification

- **CLEAN_TRANSIENT:** **15**
- **RESOLVED_FORCED:** **0**
- **UNRESOLVED:** **11**
- **SUSPICIOUS_FALSE_RESOLUTION:** **16**

**Evidence rules (event names + payload substrings only):** `SUSPICIOUS_FALSE_RESOLUTION` = within the **same merged incident**, a `PASS_SUMMARY` / `RESOLVED` / `CONVERGED` / `MISMATCH_CLEARED` / `JOURNAL_INTEGRITY_RECOVERED` line occurs **before** a later `RECONCILIATION_QTY_MISMATCH` / `STILL_OPEN` / `STUCK` in time order. `RESOLVED_FORCED` includes `RECONCILIATION_FORCED_CONVERGENCE`, fail-closed strings, or flatten/emergency/hard-gate language in payloads. `UNRESOLVED` = last non-noise event in the merged window is mismatch/stuck/still_open, or no clean resolution event.

## SECTION 3 — Journal integrity failure analysis

- **JOURNAL_INTEGRITY_FAILED + RECONCILIATION_JOURNAL_INTEGRITY_FAILED row count:** **4182**
- **Primary cause candidates:** **0**
- **Secondary / symptomatic (near qty mismatch or churn markers):** **4**
- **Unclear:** **4178**

**Triage rules:** `secondary_symptom` if a qty-mismatch-class event occurs within **120s** before/after the journal line on the global timeline, or churn markers with incident overlap; `primary_candidate` if inside an active incident window but no nearby mismatch; else `unclear`.

## SECTION 4 — Top 20 worst incidents

### 1. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['49a42fcce41446c684890cf97d07f49b']
- **start_ts_utc:** `2026-04-08T17:56:11.445279+00:00`
- **end_ts_utc:** `2026-04-08T17:56:11.445279+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_QTY_MISMATCH_STILL_OPEN', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_QTY_MISMATCH_STILL_OPEN → RECONCILIATION_STUCK
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 2. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['761bd7b3ebd0471abeef7594d39dbc4e']
- **start_ts_utc:** `2026-04-08T17:56:11.445279+00:00`
- **end_ts_utc:** `2026-04-08T17:56:11.445279+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 3. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['bcee97bebfd24ae69f8226a5dda8c027']
- **start_ts_utc:** `2026-04-08T18:02:00.862005+00:00`
- **end_ts_utc:** `2026-04-08T18:02:00.862005+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 4. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764']
- **start_ts_utc:** `2026-04-08T18:49:48.404234+00:00`
- **end_ts_utc:** `2026-04-08T18:49:48.404234+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 5. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764']
- **start_ts_utc:** `2026-04-08T18:54:24.132079+00:00`
- **end_ts_utc:** `2026-04-08T18:54:24.132079+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 6. Instrument `—`

- **Stream:** —
- **run_id(s):** ['d5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T18:48:23.740601+00:00`
- **end_ts_utc:** `2026-04-08T18:48:55.162849+00:00`
- **First qty fields (if present):** `{}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 7. Instrument `—`

- **Stream:** —
- **run_id(s):** ['d5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T18:53:06.391788+00:00`
- **end_ts_utc:** `2026-04-08T18:53:37.740010+00:00`
- **First qty fields (if present):** `{}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 8. Instrument `—`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764']
- **start_ts_utc:** `2026-04-08T15:50:33.477184+00:00`
- **end_ts_utc:** `2026-04-08T15:50:33.477184+00:00`
- **First qty fields (if present):** `{}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 9. Instrument `—`

- **Stream:** —
- **run_id(s):** ['3fb902688d504716a89c09edd5d645a5']
- **start_ts_utc:** `2026-04-08T18:02:31.142726+00:00`
- **end_ts_utc:** `2026-04-08T18:02:31.142726+00:00`
- **First qty fields (if present):** `{}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 10. Instrument `—`

- **Stream:** —
- **run_id(s):** ['d5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T18:49:26.568642+00:00`
- **end_ts_utc:** `2026-04-08T18:49:26.568642+00:00`
- **First qty fields (if present):** `{}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 11. Instrument `—`

- **Stream:** —
- **run_id(s):** ['d5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T18:54:40.505557+00:00`
- **end_ts_utc:** `2026-04-08T18:54:40.505557+00:00`
- **First qty fields (if present):** `{}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `UNRESOLVED`
- **One-sentence root-cause bucket:** unknown

### 12. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T18:03:02.580250+00:00`
- **end_ts_utc:** `2026-04-08T18:48:02.514093+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECOVERY_SCOPE_CLASSIFIED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → …
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

### 13. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T18:55:11.871405+00:00`
- **end_ts_utc:** `2026-04-08T19:40:01.139784+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → …
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

### 14. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T17:02:17.818177+00:00`
- **end_ts_utc:** `2026-04-08T17:47:04.490939+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_CONTEXT', 'fields': {'broker_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN → JOURNAL_INTEGRITY_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → JOURNAL_INTEGRITY_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → JOURNAL_INTEGRITY_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → JOURNAL_INTEGRITY_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → JOURNAL_INTEGRITY_RECOVERED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → RECONCILIATION_RECOVERED_INTENT_FAILED → JOURNAL_INTEGRITY_INVARIANT_CYCLE → JOURNAL_INTEGRITY_RECOVERED → …
- **Journal integrity failed in window:** yes
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

### 15. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T17:47:15.974917+00:00`
- **end_ts_utc:** `2026-04-08T17:56:11.051438+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → RECOVERY_SCOPE_CLASSIFIED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → …
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

### 16. Instrument `['MNQ']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T13:34:00.621937+00:00`
- **end_ts_utc:** `2026-04-08T13:46:33.867103+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_CONTEXT', 'fields': {'broker_qty': '1', 'journal_qty': '2'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → RECOVERY_SCOPE_CLASSIFIED → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → RECOVERY_SCOPE_CLASSIFIED → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → RECOVERY_SCOPE_CLASSIFIED → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → RECOVERY_SCOPE_CLASSIFIED → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → …
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

### 17. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T19:40:27.774532+00:00`
- **end_ts_utc:** `2026-04-08T19:47:01.497031+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → RECOVERY_SCOPE_CLASSIFIED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_ORDER_SOURCE_BREAKDOWN → …
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

### 18. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027', 'd5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T17:56:14.765968+00:00`
- **end_ts_utc:** `2026-04-08T18:02:00.858629+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** IEA_ADOPTION_SCAN_NO_PROGRESS_NOT_SKIPPED → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_QTY_MISMATCH_STILL_OPEN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_QTY_MISMATCH_STILL_OPEN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → IEA_ADOPTION_SCAN_SKIPPED_NO_PROGRESS → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_CONTEXT → …
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

### 19. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['0c27d2236e014f26b176719ab3f19764', '3fb902688d504716a89c09edd5d645a5', '49a42fcce41446c684890cf97d07f49b', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'bcee97bebfd24ae69f8226a5dda8c027']
- **start_ts_utc:** `2026-04-08T18:52:53.949807+00:00`
- **end_ts_utc:** `2026-04-08T18:53:01.196140+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_CONTEXT → RECONCILIATION_QTY_MISMATCH → RECOVERY_SCOPE_CLASSIFIED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

### 20. Instrument `['MNG']`

- **Stream:** —
- **run_id(s):** ['3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc', '761bd7b3ebd0471abeef7594d39dbc4e', 'd5f1301efc7f4c40b3c6fde30e87e003']
- **start_ts_utc:** `2026-04-08T18:51:00.742079+00:00`
- **end_ts_utc:** `2026-04-08T18:52:34.881120+00:00`
- **First qty fields (if present):** `{'source_event': 'RECONCILIATION_SECONDARY_INSTANCE_SKIPPED', 'fields': {'account_qty': '2', 'journal_qty': '3'}}`
- **Key event chain (noise-stripped, de-duped):** RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_ORDER_SOURCE_BREAKDOWN → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_SECONDARY_INSTANCE_SKIPPED → RECONCILIATION_STUCK → RECONCILIATION_ORDER_SOURCE_BREAKDOWN
- **Journal integrity failed in window:** no
- **Forced action (event or payload):** no
- **Classification:** `SUSPICIOUS_FALSE_RESOLUTION`
- **One-sentence root-cause bucket:** unknown

## SECTION 5 — Systemic pattern test

1. **Mechanism clustering:** Outcome totals by class: {'CLEAN_TRANSIENT': 15, 'SUSPICIOUS_FALSE_RESOLUTION': 16, 'UNRESOLVED': 11} — largest class **`SUSPICIOUS_FALSE_RESOLUTION`** (evidence: per-incident classification above).
2. **Journal integrity driver vs symptom:** **4** / **4182** rows classified secondary/symptomatic vs **0** primary-candidate (evidence: §3 triage on timed adjacency to qty-mismatch events).
3. **Qty mismatches resolving cleanly:** **15** `CLEAN_TRANSIENT` vs **11** `UNRESOLVED` + **16** suspicious (evidence: §2 counts; `RESOLVED_FORCED` = **0**).
4. **Stall-loop evidence:** **4** merged incidents with ≥2 stall/gate/no-progress substring hits in expanded window (evidence: event names in `STALL_GATE` list).
5. **Early success:** **16** merged incidents with resolution-class then regression-class ordering in-window (evidence: §2 rule).
6. **Instrument concentration (qty mismatch event lines):** `MNG`=112, `MNQ`=9 (evidence: raw line counts for `RECONCILIATION_QTY_MISMATCH` / `STILL_OPEN`).

## SECTION 6 — Executive summary

- **Day characterization:** **structurally unstable** — derived from outcome mix: UNRESOLVED+SUSPICIOUS = **27** / **42**, CLEAN_TRANSIENT = **15**.
- **Top 3 engineering attention items (evidence-only):**
  1. Journal integrity row volume (**4182**) vs secondary classification (**4** symptomatic) — see §3.
  2. Forced or hard interventions: **0** `RESOLVED_FORCED` incidents (§2).
  3. Unresolved / suspicious outcomes: **11** + **16** (§2, §4).
- **Audit next before reconciliation logic changes:** Payload-level review of `RECONCILIATION_QTY_MISMATCH` / `RECONCILIATION_STUCK` / `RECONCILIATION_FORCED_CONVERGENCE` lines for the top instruments above; confirm stall-loop hypothesis with gate telemetry fields; cross-check `SUSPICIOUS_FALSE_RESOLUTION` cases against order/fill IDs in non-JSONL sources if available.
