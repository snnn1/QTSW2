# Full investigation: S1 ES1 / NG1 NO_TRADE + slot_time 07:30 (2026-03-20)

**Date (trading day):** 2026-03-20  
**Severity:** High — intended S1 slots (09:00 Chicago) were overwritten mid–range-build by a bad `timetable_current.json` revision, causing `NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION` and no entry brackets for ES1/NG1.  
**Related:** [2026-03-19 S1 orders cancelled](2026-03-19_S1_ORDERS_CANCELLED_INVESTIGATION.md) (separate incident: restart / UNOWNED_ENTRY_RESTART).  
**Summaries hub:** [docs/robot/summaries/README.md](../summaries/README.md) · [Master roll-up Mar 17–20](../summaries/MASTER_RECENT_ISSUES_AND_FIXES_2026-03-17_through_2026-03-20.md)

---

## 1. Executive summary

| Question | Answer |
|----------|--------|
| **What happened?** | While ES1 and NG1 were in `RANGE_BUILDING` with correct slot **09:00**, a timetable poll picked up a **new file revision** whose content hash was **`61de6927…`**. That revision listed **`slot_time: "07:30"`** for those S1 streams. `ApplyDirectiveUpdate` applied it, shifting `SlotTimeUtc` to **12:30 UTC** (07:30 CT). At range lock (~08:29 / ~08:39 CT), “minutes past slot” was computed against **07:30**, so the robot treated initial submission as **materially late** and committed **NO_TRADE**. |
| **Did the robot invent 07:30?** | **No.** It applied **`directive.slot_time`** from disk via `ApplyTimetable` → `ApplyDirectiveUpdate`. |
| **Why 07:30 specifically?** | **07:30** is the legitimate S1 slot for **YM1** in the normal timetable. The bad revision likely reflects a **data/export mistake** (e.g. YM’s slot copied into ES/NG rows, spreadsheet column shift, or bad merge). The exact authoring step is **not** in repo logs; only the **effect** is proven below. |
| **Was the bad file permanent?** | **No.** Within **~1 minute** of ES1’s incident, the timetable hash **reverted** from **`61de6927…`** back to **`513b31f5…`**, indicating **oscillating writers** or a quick correction. |

---

## 2. Symptom summary

- **ES1 / NG1:** `Committed: true`, `CommitReason: NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION`, `StopBracketsSubmittedAtLock: false`, `EntryDetected: false`.
- **Hydration:** `RANGE_BUILDING_START` showed top-level `slot_time_chicago: "09:00"`; `RANGE_LOCKED` showed **`"07:30"`** and `range_end_time_chicago` aligned with **07:30 CT** (slot boundary used for range-end labeling).
- **YM1 (MYM):** Orders submitted per daily summary (expected — YM1’s real slot is 07:30).
- **Daily summary:** `logs/robot/daily_20260320.md` — 2× entry stops (MYM), `range_locked: 3`, latest NG `RANGE_LOCKED` at 13:39 UTC.

---

## 3. Timeline (authoritative log correlation)

Times below are **UTC** unless labeled CT.

### 3.1 ES1

| Time (UTC) | Time (CT) | Source | Event |
|------------|-----------|--------|--------|
| 2026-03-20T11:12:01.006Z | 06:12 | `hydration_2026-03-20.jsonl` | `RANGE_BUILDING_START` — `slot_time_chicago` **09:00** |
| 2026-03-20T13:29:00.716Z | 08:29 | `robot_ENGINE.jsonl` | `TIMETABLE_UPDATED` — `previous_hash = 513b31f5…`, **`new_hash = 61de6927…`** |
| 2026-03-20T13:29:00.815Z | 08:29 | `robot_ES.jsonl` | `DIRECTIVE_UPDATE_APPLIED` — **`old_slot = 09:00`, `new_slot = 07:30`**, session **S1** |
| 2026-03-20T13:29:00.815Z | 08:29 | `hydration_2026-03-20.jsonl` | `RANGE_LOCKED` — top-level `slot_time_chicago` **07:30**, `range_end_time_chicago` **2026-03-20T07:30:00-05:00** |
| 2026-03-20T13:30:00.712Z | 08:30 | `robot_ENGINE.jsonl` | `TIMETABLE_UPDATED` — **`61de6927…` → `513b31f5…`** (revert) |

### 3.2 NG1

| Time (UTC) | Time (CT) | Source | Event |
|------------|-----------|--------|--------|
| 2026-03-20T13:39:03.148Z | 08:39 | `robot_ENGINE.jsonl` | `TIMETABLE_UPDATED` — **`513b31f5…` → `61de6927…`** |
| 2026-03-20T13:39:03.148Z | 08:39 | `robot_NG.jsonl` | `DIRECTIVE_UPDATE_APPLIED` — **`09:00` → `07:30`**, session **S1** |
| 2026-03-20T13:39:03.148Z | 08:39 | `hydration_2026-03-20.jsonl` | `RANGE_LOCKED` with **07:30** / 07:30 CT range end |

### 3.3 Earlier same day: hash churn (context)

`robot_ENGINE.jsonl` on 2026-03-20 also shows repeated transitions:

- **`513b31f5…` ↔ `3d2e121b…`** around **11:49** and **12:09–12:10** UTC (enabled count 9 ↔ 7).
- **`513b31f5…` ↔ `61de6927…`** around **13:29–13:32** UTC (enabled count stays **9** — **slot/content change**, not enable/disable).

This supports **multiple processes or rapid rewrites** of `timetable_current.json`, not a single stable file for the whole session.

---

## 4. Evidence excerpts (where to grep)

### 4.1 Hydration — ES1 `RANGE_LOCKED` (truncated)

File: `logs/robot/hydration_2026-03-20.jsonl`

- `event_type`: `RANGE_LOCKED`, `stream_id`: `ES1`
- `slot_time_chicago`: **`"07:30"`**
- `data.range_end_time_chicago`: **`2026-03-20T07:30:00.0000000-05:00`**
- `data.range_end_time_utc`: **`2026-03-20T12:30:00.0000000+00:00`**

Compare to earlier `RANGE_BUILDING_START` for ES1: `slot_time_chicago` **`"09:00"`** and `data.slot_time_chicago` at **09:00 CT**.

### 4.2 Stream logs — directive application

- `logs/robot/robot_ES.jsonl` — search `2026-03-20T13:29:00` and `DIRECTIVE_UPDATE_APPLIED`, `old_slot`, `new_slot`.
- `logs/robot/robot_NG.jsonl` — search `2026-03-20T13:39:03` and `DIRECTIVE_UPDATE_APPLIED`.

### 4.3 Engine — timetable hash

- `logs/robot/robot_ENGINE.jsonl` — search `TIMETABLE_UPDATED` and `2026-03-20T13:29` / `13:39` and hashes **`513b31f576863ef5df05f7085d2d2fd4a5193f94c96e2c1b37142365a8219ec8`**, **`61de6927cce9a09e0df52bdb128359acb5fd6fbb0f2388b3b3ab6c782aa5c233`**.

### 4.4 Execution journals (snapshot)

Files:

- `logs/robot/journal/2026-03-20_ES1.json`
- `logs/robot/journal/2026-03-20_NG1.json`

Notable fields:

- `CommitReason`: `NO_TRADE_MATERIALLY_DELAYED_INITIAL_SUBMISSION`
- `SlotInstanceKey`: **`ES1_09:00_2026-03-20`** / **`NG1_09:00_2026-03-20`** (instance identity from **arm/init**, not overwritten by mid-day slot string — useful for ops).
- `TimetableHashAtCommit`: **`513b31f5…`** — this is **not** the mid-day poll hash. In code, `_timetableHash` on `StreamStateMachine` is set **once at construction** from the timetable hash at stream creation; it is **not** updated on later polls. So the journal field means **“hash at stream init”**, not “hash at commit time.”

---

## 5. Root cause chain (technical)

1. **`TimetableFilePoller`** reads `data/timetable/timetable_current.json` and computes a **content hash** (see §6).
2. When the hash **changes**, `RobotEngine` reloads and calls **`ApplyTimetable`**.
3. For each **enabled** directive with matching stream id, **`ApplyDirectiveUpdate(newSlotTimeChicago, …)`** runs if the stream is **`PRE_HYDRATION`** or **`RANGE_BUILDING`** and not committed (see `StreamStateMachine.ApplyDirectiveUpdate`).
4. That updates **`SlotTimeChicago`**, **`SlotTimeChicagoTime`**, **`SlotTimeUtc`** via `ConstructChicagoTime` / `ConvertChicagoToUtc`.
5. **Material delay** logic compares **`utcNow - SlotTimeUtc`** to **`initial_submission_freshness_minutes`** (parity spec). After the bad update, **`SlotTimeUtc`** is **12:30Z**; at lock (~**13:29Z**) the stream is **~59 minutes** past that slot → **NO_TRADE**.
6. **`EmitRangeLockedEvents`** sets `range_end_time_*` from **`SlotTimeChicagoTime`** (slot boundary), so hydration shows **07:30 CT** once the bad slot is applied.

---

## 6. Content hash semantics (why `61de6927…` matters)

**File:** `RobotCore_For_NinjaTrader/TimetableContentHasher.cs`

The hash is **SHA-256** of a normalized JSON payload that includes, per stream:

- `stream`, `instrument`, `session`, **`slot_time`**, `enabled`, `block_reason`, `decision_time`

It **excludes** `as_of` and `source` (metadata-only churn does not change the hash).

Therefore **`61de6927…` ≠ `513b31f5…` with the same enabled count** implies a **real contract change** — e.g. **wrong `slot_time` on one or more rows**, not just a timestamp bump.

---

## 7. What we could **not** prove from repo artifacts

| Gap | Notes |
|-----|--------|
| **Exact JSON** for hash `61de6927…` | Not committed; no snapshot found under `automation/logs` or repo search. |
| **Named process** that wrote the bad revision | Candidates: Matrix app resequence/export, `modules/timetable/timetable_engine.py`, manual save, watchdog copy, orchestrator stage. **Correlate OS file timestamps** on `timetable_current.json` and **Matrix / pipeline logs** around **08:29 CT** on 2026-03-20. |
| **Why** ES/NG got 07:30 | Strong **hypothesis**: same value as **YM1 S1** in the correct timetable → **row/column or merge error** in the authoring pipeline. |

---

## 8. Prevention (implemented) — stop bad data at the writer

**Principle:** Fix the **source** (`timetable_current.json` contract), not robot reactions to bad polls.

### 8.1 Timetable write guard (Python)

**Files:** `modules/timetable/timetable_write_guard.py`, `modules/matrix/config.py`, `modules/timetable/timetable_engine.py`

Before any atomic write in `_write_execution_timetable_file`, the engine calls **`validate_streams_before_execution_write`**:

1. Every stream’s `slot_time` must be in **`SLOT_ENDS[session]`** (same lists as parity / matrix config).
2. **S1 + `07:30`** is only allowed for instruments in **`S1_INSTRUMENTS_ALLOWED_EARLY_OPEN_SLOT`** (currently **`{"YM"}`**). ES/NG/NQ/etc. with S1 `07:30` causes **`ValueError`** — the file is **not** written, so the robot never sees the bad revision.

**Emergency override (not for routine use):** set env **`QTSW2_SKIP_TIMETABLE_INSTRUMENT_SLOT_GUARD=1`** to bypass rule (2) only; slot-in-session validation still runs.

**Tests:** `tests/test_timetable_write_guard.py`

**Full audit (all code paths):** [`docs/robot/TIMETABLE_WRITE_PATHS_AUDIT.md`](../TIMETABLE_WRITE_PATHS_AUDIT.md) — includes harness/replay exceptions and `tools/validate_timetable_execution_file.py`.

### 8.2 Matrix / UI writers (checked)

| Path | Writes `timetable_current.json`? | Uses write guard? |
|------|----------------------------------|-------------------|
| **`modules/matrix/file_manager.py`** after matrix save | Yes, via `TimetableEngine.write_execution_timetable_from_master_matrix` → `_write_execution_timetable_file` | **Yes** |
| **Matrix Timetable App** (`App.jsx`) | Yes — `useEffect` calls `saveExecutionTimetable` → POST **`/api/timetable/execution`** on dashboard | **Yes** (same `validate_streams_before_execution_write` added to `modules/dashboard/backend/main.py` `save_execution_timetable`) |
| **Dashboard** `generate_timetable` | Via `TimetableEngine` / `generate_timetable()` | **Yes** (engine path) |

Previously, **`/api/timetable/execution`** built JSON from the **UI worker** payload and wrote disk **without** the engine — that could overwrite a good Python-generated file with a bad client-built one. The dashboard endpoint now runs the **same validator** before atomic write; invalid payloads return **HTTP 400**.

### 8.3 Operations (still recommended)

1. **Single writer** — Prefer one primary publisher during RTH; if both matrix-save and Matrix UI are active, ordering can still race (last write wins) — but bad rows are now rejected at both writers.
2. **Versioned archive** — Optional: copy each successful write to `data/timetable/archive/` for forensics (hash / timestamp).

### 8.4 Forensics playbook (next time)

1. `grep TIMETABLE_UPDATED logs/robot/robot_ENGINE.jsonl` for the trading day.
2. `grep DIRECTIVE_UPDATE_APPLIED logs/robot/robot_*.jsonl` for `old_slot` / `new_slot`.
3. Compare `hydration_{day}.jsonl` `RANGE_BUILDING_START` vs `RANGE_LOCKED` `slot_time_chicago`.
4. Map hash `61de6927…` to a saved file once archiving exists.

---

## 9. Code references

| Area | File | Notes |
|------|------|--------|
| **Write-time prevention** | `modules/timetable/timetable_write_guard.py`, `timetable_engine._write_execution_timetable_file` | Rejects invalid rows before atomic write. |
| Timetable poll + hash | `TimetableFilePoller.cs`, `TimetableContentHasher.cs` | Content hash excludes `as_of` / `source`. |
| Apply slot updates | `RobotEngine.cs` — `ApplyTimetable` | Per-stream `ApplyDirectiveUpdate`. |
| Mid-build slot change | `StreamStateMachine.cs` — `ApplyDirectiveUpdate` | Allowed in `PRE_HYDRATION` / `RANGE_BUILDING`. |
| Range lock hydration | `StreamStateMachine.cs` — `EmitRangeLockedEvents` | `range_end_time_*` uses `SlotTimeChicagoTime`. |
| Journal hash field | `StreamStateMachine.cs` — commit path | `TimetableHashAtCommit = _timetableHash` (constructor snapshot). |

---

## 10. Reference: expected S1 slots (repo `data/timetable/timetable_current.json` as of investigation)

| Stream | session | Expected `slot_time` (illustrative) |
|--------|---------|-------------------------------------|
| ES1 | S1 | **09:00** |
| NG1 | S1 | **09:00** |
| YM1 | S1 | **07:30** |

The bad revision **`61de6927…`** behaved as if **ES1/NG1 temporarily adopted YM1’s S1 slot**.

---

*Investigation compiled from `logs/robot/hydration_2026-03-20.jsonl`, `logs/robot/robot_ENGINE.jsonl`, `logs/robot/robot_ES.jsonl`, `logs/robot/robot_NG.jsonl`, `logs/robot/journal/2026-03-20_ES1.json`, `logs/robot/journal/2026-03-20_NG1.json`, `logs/robot/daily_20260320.md`, and RobotCore sources.*
