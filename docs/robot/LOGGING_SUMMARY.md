# Robot Logging System Summary

## Overview

The QTSW2 robot uses a structured JSONL (JSON Lines) logging system with automatic daily summaries, log rotation, and comprehensive audit tools.

## Log Format

### File Format: JSONL (JSON Lines)

Each log file contains one JSON object per line. Each line is a complete, parseable JSON event.

### Event Structure

Every log event follows this structure:

```json
{
  "ts_utc": "2026-01-22T21:51:15.9111629+00:00",
  "level": "INFO",
  "source": "RobotEngine",
  "instrument": "ES",
  "account": "SIM",
  "run_id": "abc123",
  "event": "ORDER_SUBMITTED",
  "message": "ORDER_SUBMITTED",
  "data": {
    "payload": {
      "broker_order_id": "fb2ac0246ed14f108d11a4f5abc4f569",
      "order_type": "ENTRY_STOP",
      "direction": "Long",
      "stop_price": 6964.75,
      "quantity": 1,
      "oco_group": "QTSW2:OCO_ENTRY:2026-01-22:ES2:09:30",
      "account": "SIM"
    }
  },
  "intent_id": "f84bb382601463c7"
}
```

### Key Fields

- **`ts_utc`**: ISO 8601 timestamp in UTC (always present)
- **`level`**: `INFO`, `WARN`, `ERROR`, `DEBUG`
- **`source`**: Component name (`RobotEngine`, `StreamStateMachine`, `NinjaTraderSimAdapter`, etc.)
- **`instrument`**: Instrument symbol (`ES`, `NQ`, `GC`, etc.) or empty string for engine-level events
- **`event`**: Event type identifier (e.g., `ORDER_SUBMITTED`, `RANGE_LOCKED`, `EXECUTION_FILLED`)
- **`message`**: Human-readable message (often same as `event`)
- **`data`**: Event-specific payload (structure varies by event type)
- **`account`**: Account identifier (or `null`)

## Log File Organization

### Primary Log Files

Located in the resolved log directory (see "Log Directory Resolution" below):

- **`robot_ENGINE.jsonl`**: Engine-level events, health monitoring, logging service diagnostics
- **`robot_<INSTRUMENT>.jsonl`**: Per-instrument events (e.g., `robot_ES.jsonl`, `robot_NQ.jsonl`)

### Rotated Logs

When log files exceed size limits (configurable, default ~50MB):

- **`robot_<INSTRUMENT>_YYYYMMDD_HHMMSS.jsonl`**: Timestamped rotated files
- **`archive/`**: Older rotated logs moved here after `archive_days` (default 7)

### Sidecar Files

- **`robot_logging_errors.txt`**: Logging worker errors/warnings (fallback if JSONL write fails)
- **`notification_errors.log`**: Pushover notification failures and diagnostics
- **`daily_YYYYMMDD.md`**: Auto-generated human-readable daily summary (updated periodically by logging worker)

### Journal Files

- **`journal/YYYY-MM-DD_<STREAM>.json`**: Per-stream state snapshots (range locks, commit status)

## Log Directory Resolution

The runtime resolves the log directory in this order (highest to lowest precedence):

1. **Constructor override**: `customLogDir` parameter passed to `RobotEngine(...)`
2. **Environment variable**: `QTSW2_LOG_DIR` (e.g., `C:\Users\jakej\QTSW2\logs\robot`)
3. **Config file**: `configs\robot\logging.json` → `"log_dir": "<path>"`
4. **Default**: `<projectRoot>\logs\robot\`

The `projectRoot` itself is resolved by:

1. **Environment variable**: `QTSW2_PROJECT_ROOT` (recommended for NinjaTrader)
2. **Walk-up search**: Starting from current working directory, walk up until finding `configs\analyzer_robot_parity.json`

### For NinjaTrader Users

Set these environment variables before starting NinjaTrader:

```powershell
$env:QTSW2_PROJECT_ROOT = "C:\Users\jakej\QTSW2"
$env:QTSW2_LOG_DIR = "C:\Users\jakej\QTSW2\logs\robot"  # Optional, defaults to <projectRoot>\logs\robot
```

**Important**: Restart NinjaTrader after setting environment variables so they are picked up.

## Startup Sanity Events

On engine start, the robot emits diagnostic events to confirm log location:

- **`PROJECT_ROOT_RESOLVED`**: Shows resolved project root path
- **`LOG_DIR_RESOLVED`**: Shows resolved log directory path
- **`LOGGING_CONFIG_LOADED`**: Confirms logging configuration loaded

Search for these events in `robot_ENGINE.jsonl` to verify you're looking at the correct log directory.

## Daily Summaries

The logging service automatically generates `daily_YYYYMMDD.md` files containing:

- **Event counts**: Total events, errors, warnings
- **Range activity**: `RANGE_LOCKED` events, range computation status
- **Order activity**: `ORDER_SUBMITTED`, `ORDER_REJECTED`, `EXECUTION_FILLED` counts
- **Stability metrics**: Panic/restart events, backpressure drops, logging errors
- **Latest errors**: Most recent error events (up to 10)

These summaries are written periodically (every few minutes) and on flush/rotation.

## Audit Tools

### 1. Quick Audit (`tools/log_audit.py`)

One-command summary of today's activity and recent errors:

```bash
python tools/log_audit.py --hours-back 24 --tz chicago
```

**Options**:
- `--log-dir <path>`: Override log directory (if not using env vars)
- `--hours-back <N>`: Window of time to analyze (default: 24)
- `--tz <timezone>`: Timezone for "today" calculation (`chicago`, `utc`, `local`)
- `--write-md`: Write a markdown summary file

**Output**:
- Total events in window
- Today's summary (errors, warnings, ranges, orders)
- Latest errors (up to 10)

### 2. Loud Errors (`check_loud_errors.py`)

Summary of critical error events:

```bash
python check_loud_errors.py --log-dir "logs\robot" --hours-back 24
```

**Shows**:
- Error event counts by type
- Violation events
- Backpressure/drop events
- Logging pipeline errors

### 3. Critical Events (`check_recent_critical_events.py`)

Recent critical events window:

```bash
python check_recent_critical_events.py --window-hours 6
```

### 4. Today's Orders (`tools/check_today_orders.py`)

Quick check for today's order submissions and range locks:

```bash
python tools/check_today_orders.py
```

**Shows**:
- Top event types
- `RANGE_LOCKED` events
- `ORDER_SUBMITTED`/`ORDER_SUBMIT_SUCCESS` events
- Execution/fill events
- Error summary

## Key Events to Monitor

### Range Building

- **`RANGE_LOCKED`**: Range successfully computed and locked (contains `range_high`, `range_low`)
- **`RANGE_COMPUTE_FAILED`**: Range computation failed (check `data` for reason)
- **`RANGE_BUILDING`**: Range computation in progress
- **`RANGE_HYDRATED_FROM_HISTORY`**: Range loaded from historical data

### Order Lifecycle

- **`ORDER_SUBMITTED`**: Order submitted to broker (check `data.payload.order_type`, `direction`, `stop_price`)
- **`ORDER_SUBMIT_SUCCESS`**: Order submission confirmed by broker
- **`ORDER_ACKNOWLEDGED`**: Order acknowledged by broker
- **`ORDER_REJECTED`**: Order rejected (check `data` for reason)
- **`EXECUTION_FILLED`**: Order filled (check `data` for fill price, quantity)
- **`PROTECTIVES_PLACED`**: Stop-loss/take-profit orders placed after entry

### Execution Gate

- **`EXECUTION_GATE_INVARIANT_VIOLATION`**: Execution gate blocked an order unexpectedly (check `data.error`)

### Logging Health

- **`LOG_BACKPRESSURE_DROP`**: Events dropped due to queue overflow (indicates logging bottleneck)
- **`LOG_WORKER_LOOP_ERROR`**: Background logging worker crashed/recovered
- **`LOG_WRITE_FAILURE`**: Disk write failure (check disk space/permissions)
- **`LOG_HEALTH_ERROR`**: Logging service health check failed

### Engine Lifecycle

- **`ENGINE_START`**: Engine started
- **`ENGINE_STOP`**: Engine stopped
- **`PANIC`**: Critical error causing engine shutdown

## Example Workflow

### 1. Check if logs are being written

```bash
# Look for startup events
grep "LOG_DIR_RESOLVED\|PROJECT_ROOT_RESOLVED" logs/robot/robot_ENGINE.jsonl | tail -5
```

### 2. Check today's activity

```bash
python tools/log_audit.py --hours-back 24 --tz chicago
```

### 3. Check for order submissions

```bash
python tools/check_today_orders.py
```

### 4. Check for errors

```bash
python check_loud_errors.py --hours-back 24
```

### 5. Read daily summary

```bash
cat logs/robot/daily_20260122.md
```

## Log Rotation and Archival

- **Rotation**: When a log file exceeds size limit (default ~50MB), it's renamed with timestamp
- **Archival**: Rotated logs older than `archive_days` (default 7) are moved to `archive/` subdirectory
- **Cleanup**: Old archives can be manually deleted (no automatic cleanup beyond archival)

## Configuration

Logging behavior is configured in `configs\robot\logging.json`:

```json
{
  "max_file_size_mb": 50,
  "archive_days": 7,
  "log_level": "INFO",
  "log_dir": null
}
```

- **`max_file_size_mb`**: Maximum log file size before rotation
- **`archive_days`**: Days before rotated logs are archived
- **`log_level`**: Minimum log level (`DEBUG`, `INFO`, `WARN`, `ERROR`)
- **`log_dir`**: Override log directory (null = use default resolution)

## Troubleshooting

### Logs not appearing in expected location

1. Check `PROJECT_ROOT_RESOLVED` and `LOG_DIR_RESOLVED` events in `robot_ENGINE.jsonl`
2. Verify `QTSW2_PROJECT_ROOT` environment variable is set (if using NinjaTrader)
3. Check `configs\robot\logging.json` for `log_dir` override

### High error counts

1. Run `python check_loud_errors.py` to see error breakdown
2. Check `RANGE_COMPUTE_FAILED` events for range computation issues
3. Check `EXECUTION_GATE_INVARIANT_VIOLATION` for execution gate issues
4. Review `robot_logging_errors.txt` for logging service errors

### Missing daily summaries

- Daily summaries are written periodically (every few minutes)
- Check `robot_logging_errors.txt` for write failures
- Verify disk space and permissions

### Events dropped (backpressure)

- Check for `LOG_BACKPRESSURE_DROP` events
- Indicates logging queue overflow (logging worker can't keep up)
- Consider reducing log verbosity or increasing worker thread priority
