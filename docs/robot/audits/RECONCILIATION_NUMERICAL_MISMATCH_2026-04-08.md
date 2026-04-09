# Numerical reconciliation mismatch audit

- **log_dir:** `C:\Users\jakej\QTSW2\logs\robot`
- **trading_date:** `2026-04-08` (field or Chicago `ts_utc` per reconciliation_day_audit)
- **Episode rule:** same `instrument` + `run_id`, consecutive mismatch rows within **120s**.
- **Row rule:** `RECONCILIATION_QTY_MISMATCH*` **or** payload with both journal qty and account/broker qty.
- **Outcome:** first `RECONCILIATION_CONVERGED` or `RECONCILIATION_STUCK` for same instrument after last row, within **3600s**; else `UNRESOLVED`.

## SECTION 1 — Scope

Engine JSONL only; numerical delta analysis (not full event audit).

## SECTION 2 — Extract

- Mismatch-derived rows used for episodes: **2208**
- Distinct episodes (groups): **1040**

## SECTION 3 — Group

Per episode table uses **first** row in group (initial snapshot); classification uses first vs last + outcome.

## SECTION 4 — Classification

- **NON_QTY_MISMATCH:** first/last deltas zero (rare).
- **TRANSIENT_SYNC:** CONVERGED and |delta|≤1 on first and last with equality at end.
- **JOURNAL_LAG / JOURNAL_OVERSHOOT:** CONVERGED, journal<account / journal>account on first snapshot, equal at last.
- **PERSISTENT_MISMATCH***:** STUCK or UNRESOLVED; sub-tags by first delta sign.
- **UNKNOWN:** otherwise.

## SECTION 5 — TSV

File: `c:\Users\jakej\QTSW2\docs\robot\audits\RECONCILIATION_NUMERICAL_MISMATCH_2026-04-08.tsv` (tab-separated).

## SECTION 6 — Aggregate

- **total episodes:** 1040
- **PERSISTENT_MISMATCH_JOURNAL_HIGH:** 1035
- **PERSISTENT_MISMATCH_JOURNAL_LOW:** 5

**per instrument (episodes):**

- `MNG`: 975
- `MNQ`: 60
- `UNKNOWN`: 5

- **average |delta| (first row):** 1.0057692307692307

**outcome (within horizon after episode end):**

- **CONVERGED:** 0 (0.0%)
- **STUCK:** 1036 (99.6%)
- **UNRESOLVED:** 4 (0.4%)

## SECTION 7 — Patterns (log-backed only)

- **First-row journal vs account:** journal &lt; account: **5** episodes; journal &gt; account: **1035**.
- **Dominant first deltas:** -1→1033, 2→4, -2→2, 1→1
- **Top instruments by episode count:** MNG=975, MNQ=60, UNKNOWN=5
- **CONVERGED vs STUCK/UNRESOLVED:** see §6 (CONVERGED **0**, STUCK **1036**, UNRESOLVED **4**).
- **Same run_id with ≥2 episodes (possible repeated mismatch windows):** **14** run_ids — ['bcee97bebfd24ae69f8226a5dda8c027', '3fb902688d504716a89c09edd5d645a5', '53062f5338d74a56934dd9991b9475dc', '0c27d2236e014f26b176719ab3f19764', '761bd7b3ebd0471abeef7594d39dbc4e', '49a42fcce41446c684890cf97d07f49b', 'd5f1301efc7f4c40b3c6fde30e87e003', '5f5843b4555340d6b1c28271af183677', '9279378c45214cfca618cabc0a028e37', '4248284f08ce472bbf97199710a40944', '8e78d16df1604ffeafb80b36c2bf5c34', 'e5784702db1a4b83b04aff3786576740', '0498781b316044e48020edb50c442385', '6943a436427749b69e3f5b01e6c26e6f']

## SECTION 8 — Constraints

Classifications are rule-based on extracted numerics and outcome events; ambiguous rows marked UNKNOWN.
