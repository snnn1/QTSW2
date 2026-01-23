#!/usr/bin/env python3
"""
Scan recent robot JSONL logs for critical events and show the latest
occurrences with a small payload excerpt.

Focuses on events like:
- BARSREQUEST_FAILED
- RANGE_COMPUTE_FAILED
- EXECUTION_GATE_INVARIANT_VIOLATION
- LOG_BACKPRESSURE_DROP / LOG_WORKER_LOOP_ERROR / LOG_WRITE_FAILURE
"""

import json
import argparse
import os
from pathlib import Path
from datetime import datetime, timedelta, timezone
from collections import defaultdict


def parse_ts(ts: str) -> datetime | None:
    if not ts:
        return None
    # Handle trailing Z
    try:
        if ts.endswith("Z"):
            return datetime.fromisoformat(ts[:-1]).replace(tzinfo=timezone.utc)
        dt = datetime.fromisoformat(ts)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except Exception:
        return None


def compact(obj, max_len=220):
    try:
        s = json.dumps(obj, default=str, ensure_ascii=False)
    except Exception:
        s = str(obj)
    return s if len(s) <= max_len else s[:max_len] + "...[truncated]"


ap = argparse.ArgumentParser()
ap.add_argument("--log-dir", default=None, help="Path to logs/robot directory (overrides env/config)")
ap.add_argument("--window-hours", type=int, default=6, help="Lookback window in hours")
args = ap.parse_args()

LOG_DIR: Path
if args.log_dir:
    LOG_DIR = Path(args.log_dir)
else:
    env_log_dir = os.environ.get("QTSW2_LOG_DIR")
    env_root = os.environ.get("QTSW2_PROJECT_ROOT")
    if env_log_dir:
        LOG_DIR = Path(env_log_dir)
    elif env_root:
        LOG_DIR = Path(env_root) / "logs" / "robot"
    else:
        LOG_DIR = Path("logs/robot")

if not LOG_DIR.exists():
    print(f"Missing log dir: {LOG_DIR}")
    raise SystemExit(1)

WINDOW_HOURS = args.window_hours
cutoff = datetime.now(timezone.utc) - timedelta(hours=WINDOW_HOURS)

target_prefixes = (
    "BARSREQUEST_FAILED",
    "RANGE_COMPUTE_FAILED",
    "EXECUTION_GATE_INVARIANT_VIOLATION",
    "LOG_BACKPRESSURE_DROP",
    "LOG_WORKER_LOOP_ERROR",
    "LOG_WRITE_FAILURE",
    "LOG_CONVERSION_ERROR",
    "LOGGER_CONVERSION_ERROR",
)

events_by_type: dict[str, list[dict]] = defaultdict(list)

files = sorted(LOG_DIR.glob("robot_*.jsonl"))
for fp in files:
    try:
        with fp.open("r", encoding="utf-8") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    e = json.loads(line)
                except Exception:
                    continue
                ts = parse_ts(e.get("ts_utc", ""))
                if ts is None or ts < cutoff:
                    continue
                name = e.get("event", "")
                if not name:
                    continue
                if name.startswith(target_prefixes):
                    e["_file"] = fp.name
                    events_by_type[name].append(e)
    except Exception:
        continue

print("=" * 80)
print("RECENT CRITICAL EVENTS (last %dh)" % WINDOW_HOURS)
print("=" * 80)
print(f"Cutoff UTC: {cutoff.isoformat()}")
print(f"Log files scanned: {len(files)}")

if not events_by_type:
    print("\n[OK] No target critical events found in the last window.")
    raise SystemExit(0)

for name in sorted(events_by_type.keys()):
    items = sorted(events_by_type[name], key=lambda e: e.get("ts_utc", ""))
    last = items[-1]
    # Some events store details under data.payload, others store fields directly under data.
    data_obj = last.get("data")
    payload = None
    if isinstance(data_obj, dict):
        payload = data_obj.get("payload")
        if payload is None:
            payload = data_obj
    print(f"\n{name}: {len(items)} occurrence(s)")
    print(f"  Latest ts_utc: {last.get('ts_utc', 'N/A')}")
    print(f"  File: {last.get('_file', 'N/A')}")
    if isinstance(payload, dict):
        # Show a few high-signal keys if present
        keys = ["instrument", "stream_id", "stream", "reason", "error", "exception_type", "note"]
        excerpt = {k: payload.get(k) for k in keys if k in payload}
        print(f"  Payload excerpt: {compact(excerpt)}")
    else:
        print(f"  Payload: {compact(payload)}")

print("\n" + "=" * 80)

