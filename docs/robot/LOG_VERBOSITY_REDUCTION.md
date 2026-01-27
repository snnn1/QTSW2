# Log Verbosity Reduction

## Problem
Robot was producing excessive logs:
- **22,749 events in 24 hours** (~948 events/hour)
- **14.78 MB log file** with 23,959 lines
- Top offenders:
  - `DATA_LOSS_DETECTED`: 2,672 events (111/hour)
  - `DATA_STALL_RECOVERED`: 2,667 events (111/hour)
  - `TIMETABLE_UPDATED/LOADED/VALIDATED/PARSING_COMPLETE`: 1,512 each (4 events per poll)
  - `BAR_DATE_MISMATCH`: 1,880 events

## Root Causes

1. **Timetable polling too frequent**: Polling every 2 seconds, logging 4 events even when unchanged
2. **Multiple engine instances**: 7 instances polling independently
3. **DATA_LOSS_DETECTED not rate-limited**: Logged every time stall detected (no per-instrument rate limit)
4. **Diagnostic logs enabled**: Increases verbosity for debugging

## Changes Made

### 1. Timetable Logging (RobotEngine.cs)
**Before**: Logged 4 events (`TIMETABLE_UPDATED`, `TIMETABLE_LOADED`, `TIMETABLE_VALIDATED`, `TIMETABLE_PARSING_COMPLETE`) on every poll, even when timetable unchanged.

**After**: Only log timetable events when:
- Timetable actually changed (`poll.Changed == true`)
- Initial load (`previousHash == null`)
- Forced reload (`force == true`)

**Impact**: Reduces timetable logging by ~99% when timetable is stable (from 4 events every 2 seconds to 0 events until timetable changes).

### 2. DATA_LOSS_DETECTED Rate Limiting (HealthMonitor.cs)
**Before**: Logged every time data stall detected (no rate limit per instrument).

**After**: Rate-limited to **once per 15 minutes per instrument**.

**Impact**: Reduces `DATA_LOSS_DETECTED` logging by ~95% (from 111/hour to ~4/hour per instrument).

### 3. Diagnostic Logs Disabled (logging.json)
**Before**: `enable_diagnostic_logs: true`

**After**: `enable_diagnostic_logs: false`

**Impact**: Reduces diagnostic event verbosity (bar diagnostics, slot gate diagnostics, tick heartbeats).

## Files Modified

1. `modules/robot/core/RobotEngine.cs`
   - Added `timetableActuallyChanged` check
   - Conditional logging for timetable events

2. `modules/robot/core/HealthMonitor.cs`
   - Added `_lastDataLossLogUtcByInstrument` dictionary
   - Added `DATA_LOSS_LOG_RATE_LIMIT_MINUTES` constant (15 minutes)
   - Rate-limited `DATA_LOSS_DETECTED` logging

3. `RobotCore_For_NinjaTrader/RobotEngine.cs`
   - Same timetable logging fix (synced)

4. `RobotCore_For_NinjaTrader/HealthMonitor.cs`
   - Same DATA_LOSS_DETECTED rate limiting (synced)

5. `configs/robot/logging.json`
   - Changed `enable_diagnostic_logs` from `true` to `false`

## Expected Impact

**Before**: ~948 events/hour
**After**: ~50-100 events/hour (estimated 90% reduction)

**Log file growth**: From ~14.78 MB/day to ~1.5 MB/day (estimated)

## Additional Recommendations

1. **Increase timetable poll interval**: Currently 2 seconds. Consider increasing to 10-30 seconds if real-time responsiveness isn't critical.
   - Location: `modules/robot/ninjatrader/RobotSimStrategy.cs` line 90
   - Change: `TimeSpan.FromSeconds(2)` → `TimeSpan.FromSeconds(10)` or `TimeSpan.FromSeconds(30)`

2. **Monitor log volume**: Run `tools/analyze_log_volume.py` periodically to track log growth.

3. **Re-enable diagnostic logs**: Only when debugging specific issues (set `enable_diagnostic_logs: true` temporarily).

## Notes

- Timetable events still logged on initial load and when timetable actually changes
- DATA_LOSS_DETECTED still logged, just rate-limited (once per 15 min per instrument)
- Diagnostic logs can be re-enabled via `configs/robot/logging.json` if needed for debugging
- All changes are backward compatible (no breaking changes)

## Additional Issue: frontend_feed.jsonl (989 MB)

**Problem**: The `frontend_feed.jsonl` file is 989 MB (almost 1 GB) with no rotation.

**Solution**: Added automatic rotation at 100 MB in `modules/watchdog/event_feed.py`.

**To rotate the current large file**:
1. Stop the watchdog service (if running)
2. Run: `python tools/cleanup_old_logs.py`
3. Restart the watchdog service

**Future**: The file will automatically rotate when it reaches 100 MB. Old archives are cleaned up after 30 days.
