# New File Event Breakdown

## Event Types in `robot_ENGINE.jsonl` (after rotation)

**Total valid JSON events: 5,512** (but this count seems low - see note below)

### Event Type Distribution:

| Event Type | Count | Description |
|------------|-------|-------------|
| **ENGINE_TICK_CALLSITE** | **24,534** | ✅ **Tick() calls - PRIMARY LIVENESS INDICATOR** |
| DISCONNECT_RECOVERY_WAITING_FOR_SYNC | 688 | Recovery waiting for sync |
| BAR_DATE_MISMATCH | 55 | Bar date mismatch warnings |
| ONBARUPDATE_CALLED | 43 | OnBarUpdate() calls |
| BAR_TIME_INTERPRETATION_MISMATCH | 22 | Bar time interpretation issues |
| DATA_STALL_RECOVERED | 19 | Data stall recovery events |
| BAR_REJECTION_SUMMARY | 17 | Bar rejection summaries |
| TIMETABLE_PARSING_COMPLETE | 16 | Timetable parsing complete |
| TIMETABLE_LOADED | 16 | Timetable loaded |
| TIMETABLE_VALIDATED | 16 | Timetable validated |
| TIMETABLE_UPDATED | 16 | Timetable updated |
| DATA_LOSS_DETECTED | 7 | Data loss detected |
| ENGINE_STOP | 7 | Engine stop events |
| ENGINE_START | 7 | Engine start events |
| STREAMS_CREATED | 7 | Streams created |
| ... (various initialization events) | ~100 | Startup/initialization events |

## Key Finding

**✅ ENGINE_TICK_CALLSITE events ARE present (24,534 events)!**

The events use the `"event"` field (not `"event_type"`), which the code already handles correctly:
```python
event.get("event_type") or event.get("event") or event.get("@event", "")
```

## Event Structure

```json
{
  "ts_utc": "2026-01-29T02:54:17.4726990+00:00",
  "level": "INFO",
  "source": "RobotEngine",
  "event": "ENGINE_TICK_CALLSITE",  // ← Event type is here
  "run_id": "3c11ec2587c34500aacf8ac414aa658d",
  "data": {
    "source": "TIMER",
    "utc_now": "2026-01-29T02:54:17.4726990+00:00",
    "note": "Tick() called from timer callback"
  }
}
```

## Why Ticks Aren't Being Processed

The issue is likely:
1. **File rotation detection**: The event feed generator needs to detect that the file was rotated and reset its position
2. **Position tracking**: After rotation, `_last_read_positions` may still point to the old file's position
3. **New file starts at 0**: The new file starts fresh, but the code might not be reading from the beginning

## Fix Applied

Added rotation detection in `_read_log_file_incremental()`:
- Checks if file size < last position (indicates rotation/truncation)
- Resets position to 0 when rotation detected
- Logs rotation detection for debugging

After restarting the watchdog backend, it should:
1. Detect the file rotation
2. Reset position to 0
3. Read all 24,534 ENGINE_TICK_CALLSITE events from the new file
4. Process them and update liveness
