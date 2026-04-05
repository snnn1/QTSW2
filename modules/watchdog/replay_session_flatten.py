#!/usr/bin/env python3
"""
Replay JSONL through SessionFlattenStateTracker (watchdog aggregation model).

Usage:
  python -m modules.watchdog.replay_session_flatten --date 2026-04-03
  python modules/watchdog/replay_session_flatten.py --date 2026-04-03 --engine-log path/to/robot_ENGINE.jsonl
  python modules/watchdog/replay_session_flatten.py --feed-file logs/robot/frontend_feed.jsonl --date 2026-04-03
  python modules/watchdog/replay_session_flatten.py --file modules/watchdog/tests/session_flatten_cases.jsonl

--feed-file replays the same source live watchdog tail-ingests (after event_feed filtering, run_id, rate limits,
ordering). Pre-filtering by TRACKED_TYPES is skipped; SessionFlattenStateTracker still accepts only session-flatten types.

Does not infer position; state is driven only by engine-emitted event types the tracker ingests.
"""
from __future__ import annotations

import argparse
import json
import sys
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

# Repo root = parents[2] from modules/watchdog/replay_session_flatten.py
_REPO_ROOT = Path(__file__).resolve().parents[2]


def _default_engine_log() -> Path:
    return _REPO_ROOT / "logs" / "robot" / "robot_ENGINE.jsonl"


TRACKED_TYPES = frozenset(
    {
        "SESSION_CLOSE_SOURCE_SELECTED",
        "SESSION_RESOLVED",
        "SESSION_CLOSE_RESOLVED",
        "FLATTEN_TRIGGER_SET",
        "SESSION_CLOSE_FALLBACK_WARNING",
        "FORCED_FLATTEN_TRIGGERED",
        "FORCED_FLATTEN_REQUEST_SUBMITTED",
        "FORCED_FLATTEN_FAILED",
        "FLATTEN_BROKER_FLAT_CONFIRMED",
        "FORCED_FLATTEN_BROKER_TIMEOUT",
        "FORCED_FLATTEN_EXPOSURE_REMAINING",
        "MANUAL_FLATTEN_REQUIRED",
    }
)


def normalize_robot_engine_line(obj: Dict[str, Any]) -> Dict[str, Any]:
    """Map RobotLogEvent JSON line to watchdog-style event dict for SessionFlattenStateTracker."""
    data = obj.get("data")
    if not isinstance(data, dict):
        data = {}
    ev = str(obj.get("event") or obj.get("@event") or obj.get("event_type") or "").strip()
    ts = str(obj.get("timestamp_utc") or obj.get("ts_utc") or "").strip()
    out: Dict[str, Any] = {
        "event_type": ev,
        "timestamp_utc": ts,
        "data": data,
    }
    for k in (
        "trading_date",
        "session",
        "instrument",
        "run_id",
    ):
        v = obj.get(k)
        if v not in (None, ""):
            out[k] = v
    # Promote common nested fields
    for k in (
        "trading_date",
        "session_class",
        "session",
        "instrument",
        "has_session",
        "session_close_utc",
        "session_close_chicago",
        "flatten_trigger_utc",
        "flatten_trigger_chicago",
        "buffer_seconds",
        "source",
        "reason",
        "session_close_forced_flatten",
        "failure_phase",
    ):
        if k not in out or out.get(k) in (None, ""):
            v = data.get(k)
            if v not in (None, ""):
                out[k] = v
    if "session_class" not in out and out.get("session"):
        out["session_class"] = out["session"]
    return out


def parse_ts(s: str) -> Optional[datetime]:
    if not s:
        return None
    try:
        dt = datetime.fromisoformat(s.replace("Z", "+00:00"))
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt.astimezone(timezone.utc)
    except Exception:
        return None


@dataclass
class ReplayCollector:
    critical: List[Tuple[str, Dict[str, Any]]] = field(default_factory=list)
    at_risk: List[Tuple[str, Dict[str, Any]]] = field(default_factory=list)
    emitted: Dict[str, bool] = field(default_factory=dict)

    def append_alert(self, alert_type: str, _severity: str, ctx: Dict[str, Any], dedupe_key: str) -> bool:
        if self.emitted.get(dedupe_key):
            return False
        self.emitted[dedupe_key] = True
        if alert_type == "SESSION_FLATTEN_NOT_CONFIRMED_CRITICAL":
            self.critical.append((dedupe_key, ctx))
        elif alert_type == "SESSION_FLATTEN_AT_RISK_WARNING":
            self.at_risk.append((dedupe_key, ctx))
        return True


def replay_engine_log(
    log_path: Path,
    *,
    target_date: Optional[str],
    tracker: Any = None,
    verbose: bool = False,
    skip_tracked_type_filter: bool = False,
) -> Tuple[Any, ReplayCollector, datetime, List[str]]:
    """Ingest one JSONL file; optional trading_date filter. Returns tracker, alerts collector, eval time, trace lines.

    If skip_tracked_type_filter is True (frontend_feed replay), every line passes to ingest; the tracker no-ops
    non-session-flatten types. If False (raw engine / synthetic), only TRACKED_TYPES are passed (optimization).
    """
    from modules.watchdog.aggregator.session_flatten_state import (
        SESSION_FLATTEN_INGEST_EVENT_TYPES,
        SessionFlattenStateTracker,
        _parse_utc,
    )

    if tracker is None:
        tracker = SessionFlattenStateTracker()
    trace: List[str] = []
    max_ev_ts: Optional[datetime] = None

    with open(log_path, "r", encoding="utf-8-sig") as f:
        for line_num, line in enumerate(f, 1):
            line = line.strip()
            if not line:
                continue
            try:
                raw = json.loads(line)
            except json.JSONDecodeError:
                continue
            if not isinstance(raw, dict):
                continue
            ev = normalize_robot_engine_line(raw)
            td = str(ev.get("trading_date") or "").strip()
            if not td and isinstance(ev.get("data"), dict):
                td = str(ev["data"].get("trading_date") or "").strip()
            if target_date is not None and td != target_date:
                continue

            et = ev.get("event_type") or ""
            tss = ev.get("timestamp_utc") or raw.get("ts_utc") or ""
            tsv = parse_ts(str(tss))
            if tsv and (max_ev_ts is None or tsv > max_ev_ts):
                max_ev_ts = tsv

            if skip_tracked_type_filter:
                tracker.ingest(ev)
                if verbose and et in SESSION_FLATTEN_INGEST_EVENT_TYPES:
                    trace.append(f"{tss or line_num:>6} | {et}")
            elif et in TRACKED_TYPES:
                tracker.ingest(ev)
                if verbose:
                    trace.append(f"{tss or line_num:>6} | {et}")

    eval_ts = max_ev_ts or datetime.now(timezone.utc)
    for row in list(tracker._rows.values()):
        close_dt = _parse_utc(row.session_close_utc)
        if close_dt:
            eval_ts = max(eval_ts, close_dt + timedelta(seconds=2))

    collector = ReplayCollector()

    def is_active(_k: str) -> bool:
        return False

    tracker.tick_alerts(
        eval_ts,
        resolve_alert=lambda _: None,
        append_ledger_alert=collector.append_alert,
        enqueue_delivery=lambda *a, **k: None,
        is_alert_active=is_active,
    )
    return tracker, collector, eval_ts, trace


def main() -> int:
    if str(_REPO_ROOT) not in sys.path:
        sys.path.insert(0, str(_REPO_ROOT))

    ap = argparse.ArgumentParser(
        description="Replay robot ENGINE or frontend_feed JSONL for session/flatten validation."
    )
    ap.add_argument("--date", default=None, help="Trading date YYYY-MM-DD to filter (trading_date field).")
    ap.add_argument("--engine-log", type=Path, default=None, help="Path to robot_ENGINE.jsonl")
    ap.add_argument(
        "--feed-file",
        type=Path,
        default=None,
        help="Path to frontend_feed.jsonl (live-critical feed: same filtering/order as production ingest).",
    )
    ap.add_argument("--file", type=Path, default=None, help="JSONL file to replay (e.g. synthetic tests).")
    ap.add_argument("--verbose", action="store_true", help="Print each tracked event")
    args = ap.parse_args()

    if args.file and args.feed_file:
        print("ERROR: Use only one of --file or --feed-file.", file=sys.stderr)
        return 2

    skip_tracked_type_filter = False
    if args.feed_file:
        log_path = args.feed_file
        target_date = args.date.strip() if args.date else None
        skip_tracked_type_filter = True
    elif args.file:
        log_path = args.file
        target_date = args.date.strip() if args.date else None
    else:
        if not args.date:
            print("ERROR: --date is required unless --file or --feed-file is set.", file=sys.stderr)
            return 2
        target_date = args.date.strip()
        try:
            from modules.watchdog.config import ROBOT_LOGS_DIR as _logs
            _default = Path(_logs) / "robot_ENGINE.jsonl"
        except Exception:
            _default = _default_engine_log()
        log_path = args.engine_log or _default

    if not log_path.is_file():
        print(f"ERROR: Log file not found: {log_path}", file=sys.stderr)
        return 2

    tracker, collector, eval_ts, trace = replay_engine_log(
        log_path,
        target_date=target_date,
        verbose=args.verbose,
        skip_tracked_type_filter=skip_tracked_type_filter,
    )

    rows = tracker.list_rows_sorted()
    print(f"Replay: {log_path}")
    if skip_tracked_type_filter:
        print("Mode: frontend_feed (ingest all lines; tracker filters types — matches live tail ingest)")
    if target_date:
        print(f"Date filter: {target_date}")
    else:
        print("Date filter: (none — all trading_date values in file)")
    print(f"Evaluated alerts at (UTC): {eval_ts.isoformat()}")
    print()
    print("Flatten lifecycle (tracked event types):")
    for t in trace[:500]:
        print(f"  {t}")
    if len(trace) > 500:
        print(f"  ... ({len(trace) - 500} more)")
    if not trace:
        if not rows:
            print("  (no tracked events matched filter — check trading_date or engine log contents)")
        else:
            print(f"  ({len(rows)} rollup row(s); use --verbose to print each ingested event)")
    print()

    print(f"{'Date':<12} | {'Sess':<4} | {'Inst':<6} | {'Status':<14} | {'Req':<5} | {'Alert':<5}")
    print("-" * 72)
    for r in rows:
        key = (r["trading_date"], r["session_class"], r.get("instrument") or "__engine__")
        row_obj = tracker._rows.get(key)
        alert = "yes" if row_obj and row_obj.alert_emitted else "no"
        req = "yes" if row_obj and row_obj.flatten_required else "no"
        inst_disp = (r.get("instrument") or "").strip() or "-"
        print(
            f"{r['trading_date']:<12} | {r['session_class']:<4} | "
            f"{inst_disp:<6} | {r['flatten_status']:<14} | {req:<5} | {alert:<5}"
        )

    print()
    if collector.at_risk:
        print("At-risk warnings (would emit):")
        for k, ctx in collector.at_risk:
            print(f"  {k} {ctx}")
    if collector.critical:
        print("Critical alerts (would emit):")
        for k, ctx in collector.critical:
            print(f"  {k} {ctx}")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
