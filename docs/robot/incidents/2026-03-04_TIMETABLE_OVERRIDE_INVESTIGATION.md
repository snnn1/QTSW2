# Timetable Override Investigation (2026-03-04)

## Summary

Only 3 streams (YM1, NQ1, RTY2) should have traded today per the timetable, but 8 streams traded. The timetable was overwritten mid-day by a master matrix save, and the robot reloaded it and created additional streams.

## Root Cause: Mid-Day Timetable Overwrite

### Flow

1. **Robot starts** (11:16 UTC) with timetable hash `4d08fd38...` → 3 streams enabled: YM1, NQ1, RTY2
2. **~2.5 hours later** (13:52 UTC): Timetable file is overwritten with hash `0eaf6cd8...` → more streams enabled
3. **Robot polls** every 5 seconds, detects file change, reloads timetable
4. **ApplyTimetable** runs with new timetable → creates NQ2, GC2, YM2, CL2, ES2 (in addition to existing)
5. Those new streams reach RANGE_LOCKED, place orders, get filled

### Evidence (hydration log)

| Time (UTC) | Timetable hash | Streams initialized |
|------------|----------------|---------------------|
| 11:16 | `4d08fd38...` | YM1, NQ1, RTY2 |
| 13:52 | `0eaf6cd8...` | NQ2, GC2, YM2, CL2, ES2, RTY2 |

### What triggers timetable overwrite?

| Trigger | Location | Effect |
|---------|----------|--------|
| **Master matrix save** | `file_manager.save_master_matrix()` | Always calls `write_execution_timetable_from_master_matrix()` | 
| **Matrix app auto-update** | `useMatrixController.js` | Resequence every 20 min when `autoUpdateEnabled` |
| **Manual resequence** | Matrix app UI | User clicks "Resequence" |
| **Full rebuild** | Matrix app UI | User clicks "Rebuild" |
| **Pipeline run** | `run_pipeline_standalone.py` | Analyzer → Merger → Matrix |

### Why different enabled streams?

The timetable engine uses `final_allowed` from the master matrix for the current date. Resequence:
1. Removes last N days from matrix
2. Restores sequencer state from checkpoint
3. Re-runs sequencer forward using analyzer data
4. Applies filters (DOW, DOM, etc.)

Different analyzer data or resequence state → different `final_allowed` → different enabled streams in timetable.

## Recommendation: Lock Timetable for Trading Day

### Option A: Robot ignores timetable updates after initial load (simplest)

- Once trading date is locked and streams are created, **do not apply timetable changes** that add new streams
- Only apply timetable changes that remove streams (stand down streams that become disabled)
- Or: ignore all timetable updates after first successful load for the day

### Option B: Separate timetable for robot vs. matrix app

- **timetable_current.json** = written once at start of day (e.g. pre-market), never overwritten by matrix app
- **timetable_preview.json** = what matrix app writes during the day (for UI only)
- Robot reads only `timetable_current.json`; matrix app writes to `timetable_preview.json` during the day

### Option C: Disable matrix app auto-update during trading hours

- Add a check: if `autoUpdateEnabled` and current time is in trading window, skip resequence
- Or: disable auto-update by default; user must explicitly enable

### Option D: Timetable write guard

- Before overwriting `timetable_current.json`, check if trading date matches today
- If so, and robot may be running, write to `timetable_pending.json` instead
- Robot never reads `timetable_pending`; operator manually promotes if desired

## Fix Applied

**Robot applies mid-day timetable updates for existing streams only.** When streams already exist:
- **Slot-time changes** (e.g. NG 09:30 → 11:00) are applied via `ApplyDirectiveUpdate`
- **New stream additions** are blocked and logged as `STREAM_ADDITION_BLOCKED`

This allows legitimate slot-time changes while preventing matrix auto-update from adding streams that shouldn't trade today.

## Immediate Mitigation (if fix not yet deployed)

1. **Disable auto-update** in Matrix Timetable App if it is enabled (checkbox in UI)
2. **Close Matrix app** during trading hours, or keep it open only for viewing (no resequence/rebuild)
3. **Run matrix/timetable once** before market open (e.g. 6:00 AM Chicago). Do not run again until after market close.

## Related Files

- `modules/matrix/file_manager.py` — save_master_matrix calls timetable write
- `modules/timetable/timetable_engine.py` — write_execution_timetable_from_master_matrix
- `modules/matrix_timetable_app/frontend/src/hooks/useMatrixController.js` — auto-update (20 min resequence)
- `RobotCore_For_NinjaTrader/RobotEngine.cs` — PollAndParseTimetable, ReloadTimetableIfChanged, ApplyTimetable
