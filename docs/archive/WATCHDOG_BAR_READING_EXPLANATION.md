# How Watchdog Reads Bars

## Overview

The watchdog monitors bar data flow by reading events from robot log files. It doesn't directly connect to NinjaTrader - instead, it reads events that the robot writes to log files.

## Data Flow Architecture

```
NinjaTrader → Robot Strategy → Robot Logger → Robot Log Files → Event Feed Generator → Frontend Feed → Aggregator → Event Processor → State Manager → Watchdog UI
```

## Step-by-Step Flow

### 1. Robot Writes Bar Events to Log Files

**Location**: `modules/robot/ninjatrader/RobotSimStrategy.cs`

When NinjaTrader calls `OnBarUpdate()`, the robot strategy:
- Processes the bar
- Logs events to JSONL files in `logs/robot/` directory
- Events include: `ONBARUPDATE_CALLED`, `BAR_ACCEPTED`, `BAR_RECEIVED_NO_STREAMS`

**Log File Format**: `logs/robot/robot_<INSTRUMENT>.jsonl`

**Example Event**:
```json
{
  "ts_utc": "2026-02-04T02:52:00Z",
  "event": "ONBARUPDATE_CALLED",
  "instrument": "MES",
  "execution_instrument_full_name": "MES 03-26",
  "data": { ... }
}
```

### 2. Event Feed Generator Reads Robot Logs

**Location**: `modules/watchdog/event_feed.py` - `EventFeedGenerator` class

**Process**:
1. **Scans Robot Log Files**: Reads from `logs/robot/*.jsonl`
2. **Filters Events**: Only processes "live-critical" events (including bar events)
3. **Rate Limiting**: 
   - `BAR_RECEIVED_NO_STREAMS`: Rate-limited to every 60 seconds
   - `ENGINE_TICK_CALLSITE`: Rate-limited to every 5 seconds
4. **Writes to Frontend Feed**: Appends filtered events to `automation/logs/frontend_feed.jsonl`

**Key Methods**:
- `process_new_events()`: Main entry point - reads robot logs and writes to frontend feed
- `_read_log_file_incremental()`: Reads new events since last position (tracks byte position)
- `_process_event()`: Filters and transforms events

### 3. Aggregator Reads Frontend Feed

**Location**: `modules/watchdog/aggregator.py` - `Aggregator` class

**Process**:
1. **Reads Frontend Feed**: Reads from `automation/logs/frontend_feed.jsonl`
2. **Tracks Cursor Position**: Maintains cursor to read only new events
3. **Special Bar Handling**: 
   - Always reads bar events from end of file (even if cursor is ahead)
   - Ensures bar tracking stays current
   - Reads last 10 bar events on each cycle

**Key Methods**:
- `_process_new_events()`: Reads new events since cursor
- `_read_recent_bar_events_from_end()`: Reads recent bar events from end of file
- `_read_feed_events_since()`: Reads events since cursor position

### 4. Event Processor Updates State

**Location**: `modules/watchdog/event_processor.py` - `EventProcessor` class

**Process**:
1. **Receives Events**: Gets events from aggregator
2. **Processes Bar Events**: 
   - `ONBARUPDATE_CALLED`: Updates last bar time per execution instrument
   - `BAR_ACCEPTED`: Updates last bar time
   - `BAR_RECEIVED_NO_STREAMS`: Updates last bar time
3. **Updates State Manager**: Calls `update_last_bar()` with instrument and timestamp

**Key Event Types**:
- `ONBARUPDATE_CALLED`: Called when NinjaTrader's OnBarUpdate() is invoked
- `BAR_ACCEPTED`: Bar was accepted by a stream
- `BAR_RECEIVED_NO_STREAMS`: Bar received but no streams exist yet

### 5. State Manager Tracks Bar Times

**Location**: `modules/watchdog/state_manager.py` - `StateManager` class

**Process**:
1. **Stores Last Bar Time**: `_last_bar_utc_by_execution_instrument` dictionary
   - Key: Execution instrument full name (e.g., "MES 03-26")
   - Value: UTC timestamp of last bar received
2. **Detects Stalls**: Compares current time vs last bar time
   - If > 120 seconds AND market is open → **STALLED**
   - If market is closed → **ACCEPTABLE_SILENCE**

**Key Method**:
- `update_last_bar(instrument, timestamp_utc)`: Updates last bar time for instrument

### 6. Watchdog UI Displays Status

**Location**: `modules/watchdog/frontend/src/WatchdogPage.tsx`

**Process**:
1. **Polls Status**: Calls `/api/watchdog/status` endpoint
2. **Reads State**: Gets `data_stall_detected` and `worst_last_bar_age_seconds`
3. **Displays Badge**: Shows "DATA FLOWING", "DATA STALLED", or "DATA SILENT (OK)"

## Key Files

### Robot Side (Writes Events)
- `modules/robot/ninjatrader/RobotSimStrategy.cs`: Logs `ONBARUPDATE_CALLED` events
- `modules/robot/core/RobotLogger.cs`: Writes events to JSONL files

### Watchdog Side (Reads Events)
- `modules/watchdog/event_feed.py`: Reads robot logs, writes frontend feed
- `modules/watchdog/aggregator.py`: Reads frontend feed, coordinates processing
- `modules/watchdog/event_processor.py`: Processes bar events, updates state
- `modules/watchdog/state_manager.py`: Tracks bar times, detects stalls

## Rate Limiting

To prevent log file bloat, bar events are rate-limited:

- **BAR_RECEIVED_NO_STREAMS**: Every 60 seconds (very frequent)
- **ONBARUPDATE_CALLED**: Not rate-limited (but may be filtered)
- **ENGINE_TICK_CALLSITE**: Every 5 seconds (very frequent)

## Stall Detection Logic

**Threshold**: 120 seconds (2 minutes)

**Conditions**:
1. Last bar received > 120 seconds ago
2. Market is open
3. Instrument has enabled streams expecting bars

**Result**: Flagged as "DATA STALLED"

## Example Flow

```
1. NinjaTrader receives bar for MES 03-26
   ↓
2. RobotSimStrategy.OnBarUpdate() called
   ↓
3. Robot logs: {"event": "ONBARUPDATE_CALLED", "execution_instrument_full_name": "MES 03-26", ...}
   ↓
4. EventFeedGenerator reads robot log file
   ↓
5. EventFeedGenerator writes to frontend_feed.jsonl
   ↓
6. Aggregator reads from frontend_feed.jsonl
   ↓
7. EventProcessor processes event
   ↓
8. StateManager.update_last_bar("MES 03-26", timestamp)
   ↓
9. Watchdog UI polls status, sees recent bar → "DATA FLOWING"
```

## Troubleshooting

### If Watchdog Shows "DATA STALLED":

1. **Check Robot Logs**: Are `ONBARUPDATE_CALLED` events being written?
   ```bash
   grep "ONBARUPDATE_CALLED" logs/robot/robot_MES.jsonl | tail -5
   ```

2. **Check Frontend Feed**: Are events reaching the frontend feed?
   ```bash
   grep "ONBARUPDATE_CALLED" automation/logs/frontend_feed.jsonl | tail -5
   ```

3. **Check Watchdog State**: What does state manager see?
   ```bash
   python check_data_stall.py
   ```

4. **Check Event Feed Generator**: Is it running and processing events?
   - Check watchdog backend logs
   - Verify `EventFeedGenerator.process_new_events()` is being called

## Summary

The watchdog reads bars **indirectly** through log files:
- Robot writes bar events to JSONL log files
- EventFeedGenerator reads robot logs and writes to frontend feed
- Aggregator reads frontend feed and processes events
- EventProcessor updates state manager with last bar times
- State manager detects stalls by comparing current time vs last bar time
- Watchdog UI displays status based on state manager data

This architecture allows the watchdog to monitor robot health without requiring direct integration with NinjaTrader.
