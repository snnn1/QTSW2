# Watchdog Connectivity Hardening — Implementation Summary

**Date:** 2026-03-14  
**Scope:** Four targeted improvements from the connectivity audit.

---

## 1. Prevent Cursor-Reset Replay from Inflating Disconnect Counts

**Files:** `aggregator.py`, `state_manager.py` (CursorManager)

**Changes:**
- **aggregator.start():** After loading the cursor, if it is empty or invalid:
  - Rebuild connection status from snapshot with `skip_session_metrics=True`
  - Advance cursor to the newest event per `run_id` in the tail
  - Save the cursor so no historical events are replayed on the next cycle
- **aggregator._process_feed_events_sync():** Same safe init when the cursor is empty mid-run (e.g., cursor file deleted), so replay inflation is avoided even if the cursor is lost after startup.

**Result:** Session statistics start clean; no historical CONNECTION_LOST events are re-counted.

---

## 2. Deduplicate Disconnect Events

**Files:** `config.py`, `state_manager.py`

**Changes:**
- **config.py:** Added `DISCONNECT_DEDUPE_WINDOW_SECONDS = 30`
- **state_manager.py:** In `update_connection_status()`:
  - Added `_session_last_disconnect_for_dedupe_utc` (cleared on CONNECTION_RECOVERED)
  - Before incrementing `_session_disconnect_count`, if the last disconnect was within 30 seconds, skip the increment
  - On CONNECTION_RECOVERED, clear the dedupe timestamp so the next LOST counts as a new disconnect

**Result:** Rapid flapping within 30 seconds is counted as one disconnect; distinct outages after recovery are counted separately.

---

## 3. Include CONNECTION_CONFIRMED in Connection Rebuild

**File:** `aggregator.py`

**Changes:**
- **aggregator._rebuild_connection_status_from_snapshot():** Added `CONNECTION_CONFIRMED` to `conn_types`
- Added optional `skip_session_metrics` parameter; when `True`, calls `update_connection_status()` directly instead of `process_event()`

**Result:** Startup rebuild correctly treats CONNECTION_CONFIRMED as Connected.

---

## 4. Harden Cursor Persistence

**File:** `state_manager.py` (CursorManager)

**Changes:**
- **load_cursor():**
  - Validates that the cursor is a dict with string keys and non-negative integer values
  - On malformed content or JSON decode error: logs a warning and returns `{}` (triggers safe init)
- **save_cursor():**
  - Writes to `frontend_cursor.json.tmp` first
  - Renames the temp file to `frontend_cursor.json` for atomic persistence
  - Keeps existing retry logic

**Result:** Cursor corruption is handled safely; saves are atomic and reduce risk of partial writes.

---

## Files Modified

| File | Changes |
|------|---------|
| `modules/watchdog/config.py` | Added `DISCONNECT_DEDUPE_WINDOW_SECONDS = 30` |
| `modules/watchdog/state_manager.py` | Dedupe logic in `update_connection_status`, `_is_cursor_valid`, CursorManager validation and atomic save |
| `modules/watchdog/aggregator.py` | Cursor-empty safe init in `start()` and `_process_feed_events_sync`, CONNECTION_CONFIRMED in rebuild, `skip_session_metrics` parameter |

---

## Expected Behavior After Changes

- **One disconnect → one count:** Each distinct outage increments the count once.
- **Rapid flapping → one count:** Multiple LOST events within 30 seconds count as one.
- **Watchdog restart → no historical replay:** Cursor-empty safe init advances the cursor so old events are not re-processed.
- **Connection rebuild:** CONNECTION_CONFIRMED is treated as Connected.
- **Cursor failures:** If the cursor is missing or corrupted, the watchdog initializes safely and does not inflate disconnect counts.
