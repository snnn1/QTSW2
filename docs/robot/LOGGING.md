# Robot Logging (QTSW2)

## Where logs go

The robot writes JSONL logs (one JSON object per line) into a single log directory.

**Default** (if nothing is configured):

- `<projectRoot>\logs\robot\`

### Overrides (recommended for NinjaTrader)

The runtime resolves log location in this order:

1. **Constructor override** (advanced): `customLogDir` passed into `RobotEngine(...)`
2. **Environment variable**: `QTSW2_LOG_DIR`
3. **Config file**: `configs\robot\logging.json` → `"log_dir": "<path>"`
4. Default: `<projectRoot>\logs\robot\`

`projectRoot` itself is resolved by `QTSW2_PROJECT_ROOT` (recommended) or by walking up from the current working directory until it finds `configs\analyzer_robot_parity.json`.

**For NinjaTrader**, set:

- `QTSW2_PROJECT_ROOT = C:\Users\jakej\QTSW2`
- (optional) `QTSW2_LOG_DIR = C:\Users\jakej\QTSW2\logs\robot`

Then restart NinjaTrader so the environment variables are picked up.

## Key files

In the resolved log directory:

- **`robot_ENGINE.jsonl`**: engine + health + logging service events
- **`robot_<INSTRUMENT>.jsonl`**: per-instrument stream/execution events (e.g. `robot_ES.jsonl`)
- **`robot_<INSTRUMENT>_YYYYMMDD_HHMMSS.jsonl`**: rotated logs (size-based)
- **`archive\`**: rotated logs older than `archive_days`
- **`robot_logging_errors.txt`**: logging worker warnings/errors (fallback sidecar)
- **`notification_errors.log`**: notification worker diagnostics (Pushover send failures, etc.)
- **`daily_YYYYMMDD.md`**: auto-written human summary (best-effort; updated by logging worker)

## Startup “sanity” events

On engine start, the robot emits:

- `PROJECT_ROOT_RESOLVED`
- `LOG_DIR_RESOLVED`
- `LOGGING_CONFIG_LOADED`

These are the fastest way to confirm you’re looking at the correct log directory.

## Standard checks (one-command)

From the repo root:

### 1) Quick audit (today + recent window)

```bash
python tools/log_audit.py --hours-back 24 --tz chicago --write-md
```

If logs are not in the default location, pass `--log-dir`:

```bash
python tools/log_audit.py --log-dir "C:\path\to\logs\robot" --hours-back 24
```

### 2) Loud error summary (last 24h by default)

```bash
python check_loud_errors.py
```

Or:

```bash
python check_loud_errors.py --hours-back 6
```

### 3) Recent critical events (last 6h by default)

```bash
python check_recent_critical_events.py
```

Or:

```bash
python check_recent_critical_events.py --window-hours 12
```

## What to look for

- **Ranges built**: `RANGE_LOCKED` (and other `RANGE_*` events)
- **Stop orders submitted**: `ORDER_SUBMITTED` with `data.order_type == "ENTRY_STOP"`
- **Order lifecycle**: `ORDER_SUBMITTED` → `ORDER_ACKNOWLEDGED` / `ORDER_REJECTED` → `EXECUTION_FILLED`
- **Logging health**:
  - `LOG_BACKPRESSURE_DROP` (events dropped)
  - `LOG_WORKER_LOOP_ERROR` (background worker crashed/recovered)
  - `LOG_WRITE_FAILURE` (disk write failures)

