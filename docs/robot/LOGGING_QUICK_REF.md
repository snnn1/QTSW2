# Robot Logging Quick Reference

## Log Location

**Default**: `<projectRoot>\logs\robot\`

**Override**: Set `QTSW2_LOG_DIR` environment variable

**Verify**: Look for `LOG_DIR_RESOLVED` event in `robot_ENGINE.jsonl`

## Key Files

| File | Purpose |
|------|---------|
| `robot_ENGINE.jsonl` | Engine events, health, logging diagnostics |
| `robot_<INSTRUMENT>.jsonl` | Per-instrument stream/execution events |
| `daily_YYYYMMDD.md` | Auto-generated daily summary |
| `robot_logging_errors.txt` | Logging worker errors (fallback) |

## Quick Commands

### Today's Summary
```bash
python tools/log_audit.py --hours-back 24 --tz chicago
```

### Check Orders
```bash
python tools/check_today_orders.py
```

### Loud Errors
```bash
python check_loud_errors.py --hours-back 24
```

### Critical Events
```bash
python check_recent_critical_events.py --window-hours 6
```

## Event Types

### Range Building
- `RANGE_LOCKED` ✅ Range computed successfully
- `RANGE_COMPUTE_FAILED` ❌ Range computation failed

### Orders
- `ORDER_SUBMITTED` → Order sent to broker
- `ORDER_SUBMIT_SUCCESS` → Broker confirmed
- `ORDER_REJECTED` → Order rejected
- `EXECUTION_FILLED` → Order filled

### Health
- `LOG_BACKPRESSURE_DROP` ⚠️ Events dropped (queue overflow)
- `LOG_WORKER_LOOP_ERROR` ❌ Logging worker crashed
- `EXECUTION_GATE_INVARIANT_VIOLATION` ⚠️ Execution gate blocked order

## Log Format

Each line is a JSON object:

```json
{
  "ts_utc": "2026-01-22T21:51:15.9111629+00:00",
  "level": "INFO",
  "source": "RobotEngine",
  "instrument": "ES",
  "event": "ORDER_SUBMITTED",
  "data": { ... }
}
```

## Common Searches

### Find order submissions
```bash
grep "ORDER_SUBMITTED" logs/robot/robot_*.jsonl
```

### Find range locks
```bash
grep "RANGE_LOCKED" logs/robot/robot_*.jsonl
```

### Find errors
```bash
grep '"level":"ERROR"' logs/robot/robot_ENGINE.jsonl
```

### Check log directory resolution
```bash
grep "LOG_DIR_RESOLVED\|PROJECT_ROOT_RESOLVED" logs/robot/robot_ENGINE.jsonl
```

## For NinjaTrader

Set before starting NinjaTrader:

```powershell
$env:QTSW2_PROJECT_ROOT = "C:\Users\jakej\QTSW2"
$env:QTSW2_LOG_DIR = "C:\Users\jakej\QTSW2\logs\robot"  # Optional
```

**Restart NinjaTrader** after setting environment variables.
