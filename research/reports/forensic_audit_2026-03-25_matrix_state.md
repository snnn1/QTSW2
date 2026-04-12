# Forensic Audit — Matrix / Timetable / State Origin (2026-03-25)

**Scope:** Treat NQ1 / NG1 `ORDER_SUBMIT_FAIL` (~09:16 Chicago, 14:15–14:18 UTC) as a *downstream symptom* unless upstream artifacts are proven inconsistent or unlawful.  
**Compared stream:** NQ2 later success (~10:30 Chicago).  
**Evidence:** `data/timetable/eligibility_2026-03-25.json`, `logs/robot/hydration_2026-03-25.jsonl`, `logs/matrix_build_journal.jsonl`, `data/master_matrix/master_matrix_20260325_140710_29340n.parquet`, `modules/timetable/timetable_engine.py`, `modules/matrix/file_manager.py`, `RobotCore_For_NinjaTrader/RobotEngine.cs`, `logs/robot/robot_MNQ.jsonl`, `logs/robot/robot_MNG.jsonl`, `reports/forensic_audit_2026-03-25_execution.md`.

---

## Executive conclusion

1. **No artifact was found that *should* have blocked NQ1/NG1 while leaving NQ2 valid for the same session.** Frozen eligibility for **2026-03-25** enables **NQ1**, **NG1**, and **NQ2**. Hydration for all three shows the **same** `timetable_hash`, sensible **slot times** (NQ1/NG1 **09:00** Chicago, NQ2 **10:30** Chicago), and the same trading-day anchor.
2. **Nothing in the matrix/timetable chain instructs “SIM verify before submit” or mid-session restart behavior.** `MID_SESSION_RESTART_NOTIFICATION` and the attempt to re-arm **RANGE_BUILDING** are **robot lifecycle** decisions, not fields in `timetable_current.json` or the eligibility JSON.
3. **Primary matrix-layer finding is a *phase / derivation* risk, not a proven root cause of the 09:16 rejection:** rolling matrix saves call `write_execution_timetable_from_master_matrix(..., trade_date=None, execution_mode=True)`. That forces the Python engine to use **`trade_date = max(master_matrix.trade_date)`** (see below), while **`timetable_current.json`’s top-level `trading_date`** is **recomputed from Chicago wall clock** at write time. That split can confuse operators and can **bypass** the intended `eligibility_{trade_date}.json` lookup in execution mode when the eligibility file for the **matrix-max** date is missing. **Logs from 09:16 still show coherent slots and eligibility for the live session**, so this mismatch is **not established** as the trigger for the SIM rejection.
4. **Why NQ2 succeeded later:** Evidence points to **downstream runtime** (new engine `run_id`, new `iea_instance_id`, `SIM_ACCOUNT_VERIFIED` **before** the 10:30 window). **Upstream** eligibility and slot **directives** for NQ2 were already consistent in the same freeze/hydration corpus as NQ1/NG1.

**Answering the success-criterion question:**  
*“What exact matrix/timetable/state artifact made the robot try to restore or submit in a way that should not have happened?”*  

**Not shown.** Logs show **restart recovery and submit** consistent with **eligible** S1 streams at **09:00** and prior **RANGE_BUILDING** state. The **unexpected** part is **order rejection text** (“SIM account not verified”), which is **execution-layer**, not a timetable row.

---

## 1. Failure-case upstream state (NQ1, NG1)

### 1.1 Eligibility freeze (`data/timetable/eligibility_2026-03-25.json`)

- `trading_date`: **2026-03-25**
- `freeze_time_utc`: **2026-03-25T21:59:50Z** (late-day freeze timestamp on disk; robot still requires file presence and matching `trading_date` field when loading for the session day.)
- **NQ1:** `enabled: true`
- **NG1:** `enabled: true`
- **NQ2:** `enabled: true` (for comparison)
- `matrix_hash` / `source_matrix_hash`: **null** in this file (no stronger matrix lineage on the artifact itself).

**Interpretation:** Upstream **explicitly allowed** NQ1 and NG1 for that session date.

### 1.2 Timetable / hash seen by hydration (`logs/robot/hydration_2026-03-25.jsonl`)

For **NQ1**, **NG1**, **NQ2** (first cold init lines):

- `timetable_hash`: **`40663d225ac672c05ebffbc5ef6b081ea56fc746c06bf470229b1a4728da691d`** (shared)
- `trading_day`: **2026-03-25**
- `slot_time_chicago`: **09:00** (NQ1, NG1), **10:30** (NQ2)
- `range_start_chicago`: **02:00** (S1) / **08:00** (S2) as expected for session grouping

**Interpretation:** The **ingested directive set** for the failure candidates matches **standard S1/S2 layout** and matches **NQ2** on the same hash.

### 1.3 Master matrix rows (authoritative matrix file near the incident)

File examined: `data/master_matrix/master_matrix_20260325_140710_29340n.parquet` (from `matrix_build_journal` at **2026-03-25T14:07:10Z**, i.e. shortly before the failure window).

- **`trade_date` column max in file:** **2026-03-18** (no row with `trade_date == 2026-03-25`).
- **Latest NQ1 row:** `trade_date` **2026-03-18**, `Time` **09:00**, `Session` **S1**.
- **Latest NG1 row:** `trade_date` **2026-03-18**, `Time` **09:00**, `Session` **S1**.
- **Latest NQ2 row:** `trade_date` **2026-03-18**, `Time` **10:30**, `Session` **S2**.

**Interpretation:** For the **live calendar day 2026-03-25**, the parquet **does not** contain that `trade_date`. Slot times in production are therefore **projected from the trailing matrix window** (consistent with `timetable_engine` “no data for target date → previous / latest” patterns for non–execution-mode paths). The **09:00 / 10:30** hydration values **line up with the last available matrix rows**, not with a bogus default such as wrong session order.

### 1.4 Matrix / timetable pipeline timing (`logs/matrix_build_journal.jsonl`)

Around the incident (UTC):

| UTC | Chicago (CDT, UTC−5) | Event |
|-----|----------------------|--------|
| 2026-03-25T14:07:01–14:07:10 | 09:07 | `resequence_start` → `matrix_saved` → `timetable_start` → `timetable_complete` |

A second cycle runs at **14:36** (outside the immediate 14:15–14:18 failure minute window but same morning session).

**Interpretation:** The “manual rebuild / matrix didn’t load” narrative **does not align with a total absence** of timetable material at **09:16** — a full **matrix + timetable** cycle completed **~9 minutes earlier**. Any **UI-only** inconsistency (frontend worker vs backend file) is **not reflected** in robot hydration hashes for these streams.

### 1.5 Slot journals

No **`slot_journal`** files for 2026-03-25 were found under `data/` in this workspace snapshot. **Watchdog** code can hydrate from slot journals, but **this audit cannot quote** journal rows for “committed / expired / skipped” without those files.

### 1.6 Restart / restore markers (robot)

**Hydration at the failure timestamps** (`hydration_2026-03-25.jsonl`): **NG1** and **NQ1** show **`RANGE_LOCKED`** with **final** range statistics and `breakout_levels_missing: false` at **14:16:35Z** / **14:16:38Z** — i.e. **post-range-freeze, entry-eligible** by normal stream semantics (not an upstream “skip” or “NoTrade” disposition from these fields).

From the execution-focused audit (see `reports/forensic_audit_2026-03-25_execution.md`):

- **NG1 / NQ1:** **`MID_SESSION_RESTART_NOTIFICATION`** and related re-init markers appear in the same **~09:16 Chicago** window as the failed submits (exact pre-restart state name differs by log line — **hydration** shows **`RANGE_LOCKED`** at the submit instant, which is **consistent** with posting entry brackets after range freeze).
- **Ordering:** For the same chart `run_id`, **`ORDER_SUBMIT_FAIL` precedes `IEA_BINDING` / `SIM_ACCOUNT_VERIFIED`**.

**Interpretation:** Restore + submit **happened**; the failure mode is **incompatible with “upstream said do not trade this stream.”** It **is** compatible with **runtime initialization ordering** on the adapter.

---

## 2. Success-case upstream state (NQ2)

- Same **eligibility** artifact enables **NQ2**.
- Same **hydration hash** and **10:30** slot.
- **Master matrix** last NQ2 row: **2026-03-18**, **10:30**, **S2** — same projection situation as NQ1/NG1.

**Interpretation:** **No substantive upstream divergence** between failed streams and the later successful stream in the artifacts above.

---

## 3. Comparison table (failure vs success)

| stream | source artifact | value (failure case) | value (success case) | suspicious difference? |
|--------|-----------------|----------------------|----------------------|------------------------|
| NQ1 | eligibility_2026-03-25 | enabled **true** | NQ2 enabled **true** | **No** |
| NG1 | eligibility_2026-03-25 | enabled **true** | NQ2 enabled **true** | **No** |
| NQ1 / NG1 / NQ2 | hydration `timetable_hash` | **40663d22…** | **40663d22…** | **No** |
| NQ1 | hydration `slot_time_chicago` | **09:00** | NQ2 **10:30** | **No** — different session slots by design |
| NG1 | hydration `slot_time_chicago` | **09:00** | NQ2 **10:30** | **No** |
| NQ1 / NG1 / NQ2 | master matrix last `trade_date` | **2026-03-18** (all) | **2026-03-18** | **No** — common projection gap for live 25th |
| NQ1 / NQ2 | robot mid-session restore | RANGE_BUILDING path for NQ1 | Later clean bind before slot | **Different runtime**, **not** different matrix row for 25th |
| NQ1 / NQ2 | submit outcome | FAIL (SIM string) | SUCCESS | **Downstream** per log ordering |

---

## 4. Did the matrix cause an *invalid* restore?

| Question | Answer |
|----------|--------|
| Did matrix/timetable **instruct** restart recovery that should not happen? | **No.** Recovery is **not** a timetable field; it is emitted when the robot believes prior session state warrants it. |
| Did slot journal + matrix **preserve** a terminal stream as active? | **Not provable here** — slot journal files not present in repo. Nothing in eligibility/timetable **disabled** NQ1/NG1. |
| Did robot consume **stale** upstream while upstream was **wrong**? | **Not supported.** Eligibility and hydration **agree** on enabling and slot times. |

---

## 5. State handoff chain (lineage)

`master_matrix` (last rows **2026-03-18**)  
→ **`timetable_engine`** builds streams (see §6 for `trade_date=None` behavior)  
→ **`timetable_current.json`** (`trading_date` = **Chicago wall clock** at write — `timetable_engine._write_execution_timetable_file`)  
→ **Robot** locks `_activeTradingDate` from **timetable** → loads **`eligibility_{session}.json`** (must match `eligibility.trading_date`) — `RobotEngine.cs`  
→ **Streams** initialized / hydrated — `hydration_2026-03-25.jsonl`  
→ **Mid-session restart** path resumes **RANGE_BUILDING** — robot internal  
→ **Entry submit** → **SIM gate** on adapter

**First “wrong” node for the rejection symptom:** Between **submit** and **verified SIM**, i.e. **after** timetable + eligibility were already coherent.

**First “questionable” node for **operational** correctness:** **`trade_date=None` on rolling save** (matrix pipeline) vs **session `trading_date` embedded in JSON** — potential **MATRIX_RUNTIME_PHASE_MISMATCH** (§6), **not** shown to be the first wrong state for this specific rejection.

---

## 6. Matrix-specific root cause category

**Primary bucket:** **ROBOT_ONLY_ISSUE** — for the **observed missed fills**, because **no upstream artifact** was found that should have suppressed NQ1/NG1 **only**, and log ordering shows **SIM verification lag vs submit** on the same `run_id`.

**Secondary / latent bucket (hardening, not proven trigger):** **MATRIX_RUNTIME_PHASE_MISMATCH**

Evidence from code:

- `modules/matrix/master_matrix_rolling_resequence.py` calls `save_master_matrix(..., specific_date=None, ...)`.
- `modules/matrix/file_manager.py` passes `trade_date=specific_date` into `write_execution_timetable_from_master_matrix` → **`None`**.
- `modules/timetable/timetable_engine.py` / `build_streams_from_master_matrix`: if `trade_date is None`, **`trade_date = max(master_matrix['trade_date'])`** (e.g. **2026-03-18** while the wall-clock session may be **2026-03-25**).
- Execution mode loads **`eligibility_{trade_date}.json` for that derived date**. In this repo snapshot, only **`eligibility_2026-03-25.json`** / **`2026-03-26.json`** exist — **not** `2026-03-18`. So the engine **`SESSION_ELIGIBILITY_MISSING`** branch can **`execution_mode=False` fallback**, while the written JSON still gets **`trading_date`** from **Chicago now** (e.g. 25th). **Robot** then locks **25th** and loads **`eligibility_2026-03-25.json`** — a **split brain** between *how the file was built* and *what the header says*.

This deserves a **matrix/timetable fix** (§8) even though **09:16** is **not pinned** on it.

---

## 7. Why later success happened — upstream or downstream?

- **Upstream (matrix / eligibility / slot hash):** **Essentially unchanged** between NQ1/NG1 attempts and NQ2 — same freeze file class, same hydration hash family, same projection off **2026-03-18** matrix tail.
- **Downstream:** **Confirmed** — new MNQ strategy session, **`SIM_ACCOUNT_VERIFIED` well before 10:30**, different **`iea_instance_id`** (see execution audit).

**Conclusion:** Success difference is **predominantly downstream**, not a **new** matrix decision for NQ2 on **2026-03-25**.

---

## 8. Fix recommendations — **matrix / timetable layer only**

1. **Pass an explicit session `trade_date` into `write_execution_timetable_from_master_matrix` on every rolling save** — use the same **CME / session rule** the robot uses (or the computed value already used when writing JSON), **not** `None`. Goal: execution-mode eligibility lookup targets **`eligibility_{live_session}.json`**, and matrix row selection uses **previous-day / latest** logic anchored to the **same** calendar intent.
2. **Persist `build_trade_date` (matrix anchor) and `document_trading_date` (header) into `timetable_current.json` metadata** when they differ, and emit a **single WARN** in `matrix_build_journal` if **`max(matrix.trade_date)` < session `trading_date` by more than one session** — makes “matrix didn’t have today’s row yet” a **visible** operator signal.
3. **Eligibility builder parity:** When generating eligibility for session **D**, ensure **`source_matrix_hash`** and **`trade_date`** are populated so freezes **trace** to the matrix artifact used (currently null on `eligibility_2026-03-25.json`).
4. **Slot journal retention:** Ensure **per-day slot journals** land under a known path in-repo or in log aggregation so the next audit can answer “terminal vs recoverable” without **UNKNOWN**.

---

## 9. Limitations

- **`timetable_current.json` on disk now** shows **`trading_date` 2026-03-26** and different enable flags — it is **not** a snapshot of the 09:16 Chicago file; conclusions rely on **hydration**, **eligibility freeze**, **matrix journal**, and **robot JSONL**.
- **`eligibility_2026-03-25.json`** carries `freeze_time_utc` **2026-03-25T21:59:50Z** — that timestamp is **after** the 14:16Z failure. It proves **a** freeze existed for the 25th by end of day; it does **not** by itself version the file as it existed at 09:16 Chicago. **Robot would have refused to run** that morning if eligibility were missing or `trading_date`-mismatched (see `SESSION_ELIGIBILITY_*` in `RobotEngine.cs`), so some valid freeze for the session day **was** in effect during the incident window unless startup generation masked absence (check engine logs for `SESSION_ELIGIBILITY_MISSING` / `TryGenerateEligibilityAtStartup` if reproducing).
- **Slot journal files** for **2026-03-25** were **not** found in the workspace.

---

*Generated from repository artifacts; re-run after changes to rolling-save `trade_date` wiring to confirm `matrix_build_journal` + `timetable_publish` logs show aligned session dates.*
