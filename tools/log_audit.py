#!/usr/bin/env python3
"""
Robot log audit (JSONL).

Goals:
- One-command summary of today's activity and recent errors
- Works even if log directory is overridden in NinjaTrader

Log dir resolution (precedence):
1) --log-dir
2) env QTSW2_LOG_DIR
3) env QTSW2_PROJECT_ROOT + \\logs\\robot
4) ./logs/robot (relative to cwd)
"""

from __future__ import annotations

import argparse
import json
import os
from dataclasses import dataclass
from datetime import datetime, timedelta, timezone, date
from pathlib import Path
from typing import Any, Dict, Iterable, Optional, Tuple


try:
    # Python 3.9+
    from zoneinfo import ZoneInfo
except Exception:  # pragma: no cover
    ZoneInfo = None  # type: ignore


CHICAGO_TZ = "America/Chicago"


def resolve_log_dir(cli_log_dir: Optional[str]) -> Path:
    if cli_log_dir:
        return Path(cli_log_dir)

    env_log_dir = os.environ.get("QTSW2_LOG_DIR")
    if env_log_dir:
        return Path(env_log_dir)

    env_root = os.environ.get("QTSW2_PROJECT_ROOT")
    if env_root:
        return Path(env_root) / "logs" / "robot"

    return Path("logs") / "robot"


def parse_ts(ts: str) -> Optional[datetime]:
    if not ts:
        return None
    try:
        # Handle trailing Z
        if ts.endswith("Z"):
            dt = datetime.fromisoformat(ts[:-1]).replace(tzinfo=timezone.utc)
        else:
            dt = datetime.fromisoformat(ts)
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            dt = dt.astimezone(timezone.utc)
        return dt
    except Exception:
        return None


def to_day(dt_utc: datetime, tz_name: str) -> date:
    if tz_name == "utc":
        return dt_utc.date()
    if tz_name == "local":
        return dt_utc.astimezone().date()
    if tz_name == "chicago" and ZoneInfo is not None:
        return dt_utc.astimezone(ZoneInfo(CHICAGO_TZ)).date()
    # Fallback
    return dt_utc.date()


@dataclass
class NormEvent:
    ts_utc: datetime
    level: str
    source: str
    instrument: str
    event: str
    message: str
    data: Dict[str, Any]
    file: str


def normalize_event(obj: Dict[str, Any], file_name: str) -> Optional[NormEvent]:
    ts_raw = obj.get("ts_utc") or obj.get("ts") or obj.get("timestamp")
    if isinstance(ts_raw, (int, float)):
        dt = datetime.fromtimestamp(float(ts_raw) / 1000.0, tz=timezone.utc)
    else:
        dt = parse_ts(str(ts_raw or ""))
    if dt is None:
        return None

    # New schema: RobotLogEvent
    event_name = obj.get("event") or obj.get("event_type") or obj.get("name") or ""
    level = obj.get("level") or ""
    source = obj.get("source") or ""
    instrument = obj.get("instrument") or ""
    message = obj.get("message") or event_name or ""

    data = obj.get("data") or {}
    if not isinstance(data, dict):
        data = {"payload": data}

    return NormEvent(
        ts_utc=dt,
        level=str(level or "").upper() or "INFO",
        source=str(source or ""),
        instrument=str(instrument or ""),
        event=str(event_name or ""),
        message=str(message or ""),
        data=data,
        file=file_name,
    )


def iter_events(log_dir: Path) -> Iterable[NormEvent]:
    patterns = [
        "robot_*.jsonl",
        "robot_*_*.jsonl",  # rotated logs
    ]
    files: list[Path] = []
    for pat in patterns:
        files.extend(sorted(log_dir.glob(pat)))

    # Exclude archive folder by default
    files = [f for f in files if "archive" not in f.parts]

    for fp in files:
        try:
            with fp.open("r", encoding="utf-8") as f:
                for line in f:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        obj = json.loads(line)
                        if not isinstance(obj, dict):
                            continue
                    except Exception:
                        continue
                    e = normalize_event(obj, fp.name)
                    if e is not None:
                        yield e
        except Exception:
            continue


def audit(log_dir: Path, hours_back: int, tz_name: str) -> Tuple[str, int]:
    now = datetime.now(timezone.utc)
    cutoff = now - timedelta(hours=hours_back)
    today = to_day(now, tz_name)

    events = [e for e in iter_events(log_dir) if e.ts_utc >= cutoff]
    events.sort(key=lambda e: e.ts_utc)

    total = len(events)
    errors = [e for e in events if e.level == "ERROR" or "ERROR" in (e.event or "")]
    warns = [e for e in events if e.level == "WARN"]

    today_events = [e for e in events if to_day(e.ts_utc, tz_name) == today]

    def count_event(prefix: str) -> int:
        return sum(1 for e in today_events if (e.event or "").startswith(prefix))

    range_locked = sum(1 for e in today_events if e.event == "RANGE_LOCKED")
    range_events = sum(1 for e in today_events if (e.event or "").startswith("RANGE_"))

    order_submitted = sum(1 for e in today_events if e.event == "ORDER_SUBMITTED")
    exec_filled = sum(1 for e in today_events if e.event == "EXECUTION_FILLED")
    order_rejected = sum(1 for e in today_events if e.event in ("ORDER_REJECTED", "ORDER_SUBMIT_FAIL"))

    entry_stop = 0
    for e in today_events:
        if e.event == "ORDER_SUBMITTED":
            ot = str(e.data.get("order_type") or "")
            if ot.upper() == "ENTRY_STOP":
                entry_stop += 1

    panic_like = sum(1 for e in today_events if "PANIC" in (e.event or "").upper() or e.event in ("ENGINE_STOP", "ENGINE_START"))

    latest_errors = list(reversed(errors[-10:]))

    lines: list[str] = []
    lines.append("=" * 80)
    lines.append("ROBOT LOG AUDIT")
    lines.append("=" * 80)
    lines.append(f"log_dir: {log_dir}")
    lines.append(f"timezone: {tz_name}")
    lines.append(f"window: last {hours_back}h (cutoff UTC: {cutoff.isoformat()})")
    lines.append(f"total_events_in_window: {total}")
    lines.append("")
    lines.append("=" * 80)
    lines.append(f"TODAY SUMMARY ({today})")
    lines.append("=" * 80)
    lines.append(f"today_events: {len(today_events)}")
    lines.append(f"errors_in_window: {len(errors)}")
    lines.append(f"warns_in_window: {len(warns)}")
    lines.append("")
    lines.append("Ranges:")
    lines.append(f"  range_events: {range_events}")
    lines.append(f"  range_locked: {range_locked}")
    lines.append("")
    lines.append("Orders:")
    lines.append(f"  order_submitted: {order_submitted}")
    lines.append(f"  entry_stop_submitted: {entry_stop}")
    lines.append(f"  executions_filled: {exec_filled}")
    lines.append(f"  order_rejected: {order_rejected}")
    lines.append("")
    lines.append("Stability:")
    lines.append(f"  panic_or_restart_like_events: {panic_like}")
    lines.append(f"  log_backpressure_drop: {count_event('LOG_BACKPRESSURE_DROP')}")
    lines.append(f"  log_worker_loop_error: {count_event('LOG_WORKER_LOOP_ERROR')}")
    lines.append(f"  log_write_failure: {count_event('LOG_WRITE_FAILURE')}")
    lines.append("")
    lines.append("Latest errors (up to 10):")
    if not latest_errors:
        lines.append("  [OK] none")
    else:
        for e in latest_errors:
            lines.append(f"  {e.ts_utc.isoformat()}Z | {e.instrument} | {e.event} | {e.message[:140]}")

    return "\n".join(lines) + "\n", (0 if len(errors) == 0 else 1)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", default=None, help="Path to logs/robot directory")
    ap.add_argument("--hours-back", type=int, default=24, help="Lookback window in hours")
    ap.add_argument("--tz", choices=["utc", "chicago", "local"], default="chicago", help="What 'today' means")
    ap.add_argument("--write-md", action="store_true", help="Write a markdown file next to logs")
    args = ap.parse_args()

    log_dir = resolve_log_dir(args.log_dir)
    if not log_dir.exists():
        print(f"Log directory not found: {log_dir}")
        return 2

    text, code = audit(log_dir, args.hours_back, args.tz)
    print(text)

    if args.write_md:
        now = datetime.now(timezone.utc)
        out_path = log_dir / f"audit_{now.strftime('%Y%m%d_%H%M%S')}.md"
        out_path.write_text(text, encoding="utf-8")
        print(f"Wrote: {out_path}")

    return code


if __name__ == "__main__":
    raise SystemExit(main())

