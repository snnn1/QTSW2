# Timetable single publisher & replay isolation

**Purpose:** Operational summary of the minimal control-plane hardening (Mar 2026 strategy).

## Live execution file: `data/timetable/timetable_current.json`

- **Authoritative Python publisher:** `TimetableEngine.publish_execution_timetable_current` → `_write_execution_timetable_file` in `modules/timetable/timetable_engine.py`.
- **Validation:** Always `validate_streams_before_execution_write` immediately before atomic write (tmp → replace).
- **Dashboard:** `POST /api/timetable/execution` calls `TimetableEngine` only (no direct `json.dump` to `timetable_current.json`).
- **Matrix:** `modules/matrix/file_manager.py` already used `TimetableEngine`; `project_root` is set so `logs/timetable_publish.jsonl` resolves correctly.

## Publish ledger

- **Path:** `logs/timetable_publish.jsonl` (append-only JSON lines).
- **Fields:** `timestamp` (UTC ISO), `content hash` (aligned with robot content hash semantics), `writer`, `source`, `streams_count`, `path`.
- **Implementation:** `modules/timetable/timetable_publish_ledger.py`, `modules/timetable/timetable_content_hash.py`.

## Replay isolation (harness)

- **File:** `data/timetable/timetable_replay_current.json` — updated by `HistoricalReplay.UpdateTimetableTradingDateIfReplay`; **never** writes `timetable_current.json`.
- **Template:** `timetable_replay.json` is read-only for replay prep (not overwritten each day).
- **Harness:** `Program.cs` uses `timetable_replay_current.json` when present after update; `--timetable-path` still overrides.

## Bypass / samples

- **`QTSW2_SKIP_TIMETABLE_INSTRUMENT_SLOT_GUARD`:** Logs at **ERROR** when set (`timetable_write_guard.py`).
- **`--write-sample-timetable`:** Writes `data/timetable/timetable_harness_sample.json` only (not live current).
- **Phase 5 tests:** Use `timetable_phase5_test.json` under a temp root.

## Write-path closure audit

### Scope

- Applies to **authoritative in-repo write paths** to **`data/timetable/timetable_current.json`** (live execution timetable).
- **Manual edits, sync tools, and out-of-repo scripts** can still overwrite that file — that is an **operational bypass**, not covered by code closure.

### Confirmed closure

- All **in-repo Python** live writes route through **`TimetableEngine._write_execution_timetable_file`** (atomic tmp → replace, validation on the write path).
- **Dashboard** routes through **`publish_execution_timetable_current`** (no direct `json.dump` to `timetable_current.json`).
- **No remaining C# writes** to live **`timetable_current.json`** (replay/harness/tests use **`timetable_replay_current.json`**, `timetable_harness_sample.json`, `timetable_validate_*.json`, temp `timetable*.json`, etc.).
- **Replay / harness / test outputs** are isolated to **non-live** filenames and paths.

### Residual known gaps

- **Manual / out-of-repo overwrite** risk remains.
- **Python publish-ledger content hash** vs **NinjaTrader `TimetableContentHasher`** is **best-effort** (forensic parity may require matching C# serialization).
- **Execution-plane mutability** is **separate** from publication: **valid** published timetable changes can still update streams in **`PRE_HYDRATION` / `RANGE_BUILDING`** until a stricter execution policy is chosen.

### Bottom-line verdict

- **Control-plane publication** is effectively **single-publisher** in **committed code**.
- **Mar-20-style poisoned publication** (stray writers corrupting `timetable_current.json`) is **materially reduced**.
- **Remaining work**, if any, is **execution policy** (e.g. freeze slot-defining fields mid-build), **not** publication integrity.

| Area | Status |
|------|--------|
| Live timetable writer | Single in-repo publisher |
| Validation | Centralized in write path |
| Replay isolation | Complete |
| Test isolation | Complete |
| Manual overwrite risk | Still possible |
| Mid-build slot mutation | Still possible |

## Related

- Incident: `docs/robot/incidents/2026-03-20_S1_TIMETABLE_SLOT_CORRUPTION_NO_TRADE_INVESTIGATION.md`
- Write-path audit: `docs/robot/TIMETABLE_WRITE_PATHS_AUDIT.md` (if present)
