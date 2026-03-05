# Session Eligibility & Timetable Override — Agent Handoff Summary

## Context

We are implementing a two-artifact system to fix the "timetable override" incident (2026-03-04) where the robot traded 8 streams instead of 3 because the matrix app auto-update overwrote the timetable mid-day.

## CME Session Rule (18:00 America/Chicago)

- **Utility**: `modules/timetable/cme_session.get_trading_date_cme(utc_now)`
- **Rule**: If Chicago time >= 18:00 → trading_date = next calendar day; else current calendar day
- **Deterministic**: Uses UTC, converts to Chicago; does NOT rely on machine timezone

## Two Artifacts (Spec)

### A) Session Eligibility Freeze (immutable)
- **File**: `data/timetable/eligibility_YYYY-MM-DD.json`
- **Contents**: `trading_date`, `freeze_time_utc`, `matrix_hash`, `eligible_stream_count`, `eligible_streams` (stream_key, enabled, reason)
- **Invariant**: Written once per trading_date at 18:00 CT. Never overwritten. Robot refuses to trade without it (fail-closed).

### B) Intraday Directives (mutable)
- **File**: `data/timetable/timetable_current.json` (current timetable = directives)
- **Contents**: stream_key, slot_time, and other intraday parameters
- **Invariant**: May change any time. Robot applies updates only if `stream_key ∈ eligible_set`.

## What's Implemented (Robot C#)

1. **EligibilityContract** (`Models.EligibilityContract.cs`) — schema for eligibility file
2. **Eligibility loading** — in `ReloadTimetableIfChanged()`:
   - Loads `eligibility_{trading_date}.json` after timetable trading_date is locked
   - Fail-closed if missing (`SESSION_ELIGIBILITY_MISSING`) or load fails (`SESSION_ELIGIBILITY_LOAD_FAILED`)
   - Stores `_eligibleSet` (HashSet of enabled stream keys)
3. **ApplyTimetable** — uses `_eligibleSet`:
   - Skips directives for streams not in eligible_set → logs `DIRECTIVE_IGNORED_NOT_ELIGIBLE`
   - Blocks new stream creation mid-day → logs `STREAM_ADDITION_BLOCKED` (reason=SESSION_FREEZE)
   - Applies slot-time updates for existing eligible streams
4. **Eligibility path**: `data/timetable/eligibility_YYYY-MM-DD.json` (see RobotEngine.cs ~line 503, 3060)

## What's Implemented (Additional)

5. **Eligibility builder** — `scripts/eligibility_builder.py` uses CME rule (UTC→Chicago). Builds from matrix (execution_mode=False). Exits immediately if file exists. Never overwrites (use `--force` for testing).
6. **Matrix-triggered** — `file_manager.save_master_matrix` triggers eligibility builder in background after timetable write (resequence, build, etc.).
7. **Startup safety** — Robot runs eligibility_builder when file missing; emits `SESSION_ELIGIBILITY_GENERATED_AT_STARTUP`.
8. **Policy: slot-time changes only if state < RANGE_LOCKED** — `ApplyDirectiveUpdate` rejects when state >= RANGE_LOCKED; caller logs `DIRECTIVE_IGNORED_STATE_LOCKED`.
9. **SESSION_ELIGIBILITY_FROZEN** — Logged with trading_date, freeze_time_utc, matrix_hash, eligible_stream_count.
10. **DIRECTIVE_UPDATE_APPLIED** — Logged when slot-time change is applied (old_slot, new_slot).
11. **Execution mode (Path B blocked)** — `build_streams_from_master_matrix(execution_mode=True)` loads eligibility from file; never uses manual filters. Emits `TIMETABLE_EXECUTION_MODE_ENABLED`, `PATH_B_BLOCKED_EXECUTION_MODE`, `SESSION_ELIGIBILITY_MISSING` (fail-closed).
12. **Robot logs** — SESSION_ELIGIBILITY_LOADED includes eligibility_hash, matrix_hash for deterministic replay.

## Key Files

| File | Purpose |
|------|---------|
| `RobotCore_For_NinjaTrader/Models.EligibilityContract.cs` | Eligibility schema |
| `RobotCore_For_NinjaTrader/RobotEngine.cs` | Load eligibility, enforce eligible_set, block new streams |
| `RobotCore_For_NinjaTrader/Execution/ReconciliationRunner.cs` | (Separate: RECONCILIATION_QTY_MISMATCH diagnostics) |
| `modules/timetable/cme_session.py` | CME trading date utility (UTC→Chicago, 18:00 rule) |
| `scripts/eligibility_builder.py` | Standalone eligibility builder |
| `modules/timetable/timetable_engine.py` | Writes timetable; calls eligibility_writer when writing |
| `modules/matrix/file_manager.py` | save_master_matrix → writes timetable (execution_mode=True) |
| `modules/matrix_timetable_app/frontend/.../useMatrixController.js` | Auto-update: resequence every 20 min |

## Incident Reference

- `docs/robot/incidents/2026-03-04_TIMETABLE_OVERRIDE_INVESTIGATION.md` — root cause, fix applied
- `docs/robot/incidents/2026-03-04_MYM_RECONCILIATION_QTY_MISMATCH_INVESTIGATION.md` — separate issue (journal_qty=0)

## Next Steps for Another Agent

1. **Eligibility runs from matrix** — Resequence or matrix save triggers the builder. Timetable tab shows last freeze time. Robot startup fallback if file missing.

## Trading Date Convention

- Eligibility for trading_date D is written at 18:00 CT on day D-1 (or at 18:00 CT on D if D is "today" and we're pre-market). Clarify with user: is 18:00 CT the session boundary? CME uses 17:00 CT. The spec says "18:00 CT" for freeze — use that.
