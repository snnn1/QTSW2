# Watchdog stream table vs matrix eligibility count (audit)

**Date:** 2026-03-17  
**Symptom:** Matrix timetable banner showed “N eligible” while the watchdog stream feed listed fewer rows (or mismatched expectations).

## Root causes

### 1. Different sources of truth (dashboard)

- **`GET /api/timetable/eligibility/status`** previously loaded the **newest** `data/timetable/eligibility_*.json` by **file mtime**.
- **Watchdog** (and execution) use **`data/timetable/timetable_current.json`** for **enabled** streams and trading date.

If eligibility for **day A** was newer on disk than eligibility for **day B** while `timetable_current.json` was for **day B**, the UI could show **A’s** `eligible_stream_count` while the watchdog table reflected **B’s** enabled set.

**Fix:** Prefer `eligibility_{trading_date}.json` where `trading_date` is read from `timetable_current.json`. Fall back to latest-by-mtime only if there is no timetable or no matching file. Response may include:

- `timetable_enabled_stream_count`
- `eligibility_timetable_count_mismatch` when freeze count ≠ timetable enabled count
- `source` describing which path was used

If the timetable exists but **no** `eligibility_*.json` files exist, return `status: no_eligibility_file` and counts from the timetable (so the banner does not invent a freeze).

### 2. Watchdog `get_stream_states` branch conditions

- Logic used `if timetable_streams_metadata and enabled_streams:` (truthiness).
- **`{}`** metadata and **`set()`** (zero enabled) are **falsy**, so the code skipped the timetable-driven path and fell back to **watchdog-only** keys → fewer rows when there was no event state yet, or wrong path when nothing was enabled.

**Fix:** When the poller has loaded the timetable (`enabled_streams is not None`), **always** build rows from `enabled_streams` with `meta = timetable_streams_metadata or {}`, inferring instrument/session from stream id when metadata is missing (`_canonical_from_stream_id`, `_session_from_stream_id`). Only use the watchdog-only fallback when `enabled_streams is None` (timetable unavailable).

## Files touched

- `modules/dashboard/backend/main.py` — `get_eligibility_status`
- `modules/watchdog/aggregator.py` — `get_stream_states`, helpers above
- `modules/matrix_timetable_app/frontend/src/App.jsx` — banner branches for `no_eligibility_file` / `none` / mismatch hint
- `modules/matrix_timetable_app/frontend/src/api/matrixApi.js` — JSDoc

## Verification

1. Align `timetable_current.json` `trading_date` with an `eligibility_{date}.json`; banner count should match that file and watchdog row count should match enabled streams in the timetable (including streams with empty robot state).
2. Temporarily remove all `eligibility_*.json`; banner should show `no_eligibility_file` and timetable enabled count, not a stale mtime winner.
