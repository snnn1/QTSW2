# RANGE_BUILDING Persistence and Restart Recovery — Implementation Summary

**Date:** 2026-03-12  
**Goal:** Make mid-session restart deterministic for streams in RANGE_BUILDING by persisting partial state and restoring instead of resetting to PRE_HYDRATION/ARMED.

---

## Files Changed

### New Files

| File | Purpose |
|------|---------|
| `RobotCore_For_NinjaTrader/RangeBuildingSnapshot.cs` | Snapshot model: trading_date, stream, instrument, session, slot_time, range_start, last_processed_bar_time, bar_count, range_high, range_low, freeze_close, tick_size, bars[] |
| `RobotCore_For_NinjaTrader/RangeBuildingSnapshotPersister.cs` | Append-only persistence to `logs/robot/range_building_{trading_date}.jsonl` |
| `modules/robot/core/RangeBuildingSnapshot.cs` | Same (harness build) |
| `modules/robot/core/RangeBuildingSnapshotPersister.cs` | Same |
| `NT_ADDONS/RangeBuildingSnapshot.cs` | Same (NinjaTrader add-ons) |
| `NT_ADDONS/RangeBuildingSnapshotPersister.cs` | Same |
| `modules/robot/core/Tests/RangeBuildingSnapshotTests.cs` | Unit tests for Persist/LoadLatest, latest-wins, stream isolation |
| `docs/robot/RANGE_BUILDING_PERSISTENCE_IMPLEMENTATION.md` | This document |

### Modified Files

| File | Changes |
|------|---------|
| `RobotCore_For_NinjaTrader/StreamStateMachine.cs` | Added `_rangeBuildingSnapshotPersister`, `_lastProcessedBarTimeUtc`, `_restoredFromRangeBuildingSnapshot`; `RestoreRangeBuildingFromSnapshot()`, `PersistRangeBuildingSnapshot()`; persist on RANGE_BUILD_START and on each bar update; restore in constructor when `LastState == RANGE_BUILDING`; bar deduplication in `AddBarToBuffer`; immediate `TryLockRange` when slot time passed after restore |
| `modules/robot/core/StreamStateMachine.cs` | Same |
| `modules/robot/harness/Program.cs` | Added `--test RANGE_BUILDING_SNAPSHOT` entry point |

---

## Recovery Flow Summary

### Persist

1. **When entering RANGE_BUILDING** (ARMED → RANGE_BUILDING transition): Persist snapshot with current bar buffer, range_high, range_low, freeze_close, last bar timestamp.
2. **When range updates** (each bar in RANGE_BUILDING): Persist snapshot again (range_high/range_low/bar_count changed).

### Restore (Constructor)

1. **Precedence:** RANGE_LOCKED restore runs first (from hydration/ranges log). If it succeeds, done.
2. **If `LastState == RANGE_BUILDING` and !_rangeLocked:** Call `RestoreRangeBuildingFromSnapshot()`.
3. **Load latest snapshot** for (trading_date, stream_id).
4. **Validate identity:** trading_date, stream_id, instrument, session, slot_time must match current stream.
5. **Validate timestamp:** last_processed_bar_time must not be in the future.
6. **Restore bars** into `_barBuffer`, restore RangeHigh, RangeLow, FreezeClose, set `_lastProcessedBarTimeUtc`, `_restoredFromRangeBuildingSnapshot = true`, `State = RANGE_BUILDING`, `_preHydrationComplete = true`.
7. **If slot time passed:** Call `TryLockRange()` immediately.
8. **If no valid snapshot:** Emit `RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED` / `RANGE_BUILDING_RESTORE_FALLBACK_TO_EMPTY` and fall back to normal PRE_HYDRATION path.

### Bar Deduplication

- In `AddBarToBuffer`: If `_restoredFromRangeBuildingSnapshot` and `bar.TimestampUtc <= _lastProcessedBarTimeUtc`, skip (bar already in snapshot).
- Ensures BarsRequest + live bars do not double-count bars already restored.

---

## Logging Events

| Event | When |
|-------|------|
| `RANGE_BUILDING_SNAPSHOT_WRITTEN` | After each persist |
| `RANGE_BUILDING_SNAPSHOT_RESTORED` | Successful restore |
| `RANGE_BUILDING_SNAPSHOT_RESTORE_FAILED` | No snapshot, invalid last_processed_bar_time, or last bar in future |
| `RANGE_BUILDING_RESTORE_IDENTITY_MISMATCH` | Snapshot identity does not match current stream |
| `RANGE_BUILDING_RESTORE_SLOT_PASSED_LOCK_ATTEMPT` | Slot time passed, attempting immediate lock |
| `RANGE_BUILDING_RESTORE_FALLBACK_TO_EMPTY` | Persister null, falling back to empty ARMED |

---

## Test Results

```bash
dotnet run --project modules/robot/harness/Robot.Harness.csproj -- --test RANGE_BUILDING_SNAPSHOT
```

**Covered:**
- Persist and LoadLatest roundtrip
- Multiple snapshots — latest wins
- Missing snapshot returns null
- Different streams get their own snapshots (isolation)

**Not yet covered (integration/manual):**
- Restore RANGE_BUILDING before slot time (full StreamStateMachine setup)
- Restore RANGE_BUILDING after slot time + immediate lock
- Restore then BarsRequest continuation without duplicate bars
- Restore with invalid snapshot identity
- RANGE_LOCKED takes precedence over RANGE_BUILDING snapshot
- Parity: no-restart vs restart at mid-build produces identical lock result

---

## Remaining Edge Cases

1. **Snapshot file corruption:** Persister skips malformed lines; if entire file is corrupt, LoadLatest returns null → fallback to empty ARMED.
2. **Clock skew:** last_processed_bar_time in future → restore rejected.
3. **Timetable/slot change mid-session:** Identity validation rejects snapshot if session/slot_time changed.
4. **Very large bar count:** Snapshot includes full bar list; for long sessions (e.g. 8 hours × 60 bars) ~480 bars. JSON size is acceptable; consider throttling writes if needed.
5. **Concurrent writes:** Persister uses file lock; append is atomic at filesystem level for typical line lengths.

---

## Acceptance Criteria Status

| Criterion | Status |
|-----------|--------|
| Restart before slot time: stream restores partial state and continues building | Implemented |
| Restart after slot time: stream restores and attempts immediate lock | Implemented |
| No duplicate bars: restored + BarsRequest + live → correct final bar_count/range | Implemented (dedup via _lastProcessedBarTimeUtc) |
| Deterministic parity: restart run matches no-restart run | Design supports it; full parity test pending |
| Loud failure when no valid snapshot | Implemented (CRITICAL logs) |
