#!/usr/bin/env python3
"""
Audit inter-arrival gaps for MNG bar/tick events (frontend_feed.jsonl).

Compares gap distribution to DATA_STALL_THRESHOLD_SECONDS and suggests p99 / p99.5
for calibrating DATA_STALL_THRESHOLD_BY_INSTRUMENT_ROOT[\"MNG\"].

Usage:
  python scripts/audit_mng_tick_gaps.py
  python scripts/audit_mng_tick_gaps.py --days 10 --feed path/to/frontend_feed.jsonl
"""
from __future__ import annotations

import argparse
import json
import statistics
import sys
from datetime import datetime, timedelta, timezone
from pathlib import Path

BAR_TYPES = ("BAR_ACCEPTED", "BAR_RECEIVED_NO_STREAMS", "ONBARUPDATE_CALLED")


def _qtl(sorted_vals: list[float], p: float) -> float:
    if not sorted_vals:
        return float("nan")
    n = len(sorted_vals)
    if n == 1:
        return sorted_vals[0]
    idx = min(n - 1, max(0, int(round((p / 100.0) * (n - 1)))))
    return sorted_vals[idx]


def mng_match(ev: dict) -> bool:
    ex = (ev.get("execution_instrument_full_name") or "").strip()
    d = ev.get("data") or {}
    inst = (d.get("instrument") or ev.get("instrument") or "").strip()
    if ex.startswith("MNG ") or ex == "MNG":
        return True
    return inst == "MNG"


def parse_ts(s: str | None) -> datetime | None:
    if not s:
        return None
    s = s.replace("Z", "+00:00")
    try:
        dt = datetime.fromisoformat(s)
        if dt.tzinfo is None:
            dt = dt.replace(tzinfo=timezone.utc)
        return dt
    except Exception:
        return None


def main() -> int:
    ap = argparse.ArgumentParser(description="MNG tick/bar gap audit vs DATA_STALL threshold")
    ap.add_argument("--days", type=int, default=10, help="Rolling window from newest event (default 10)")
    ap.add_argument(
        "--feed",
        type=Path,
        default=None,
        help="Single jsonl file (default: logs/robot/frontend_feed.jsonl + archive/frontend_feed_*.jsonl)",
    )
    args = ap.parse_args()

    root = Path(__file__).resolve().parents[1]
    if args.feed:
        files = [args.feed]
    else:
        robot_logs = root / "logs" / "robot"
        files = [robot_logs / "frontend_feed.jsonl"] + sorted(robot_logs.glob("archive/frontend_feed_*.jsonl"))

    all_ts: list[datetime] = []
    for fp in files:
        if not fp.exists():
            continue
        with open(fp, "r", encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    ev = json.loads(line)
                except json.JSONDecodeError:
                    continue
                if ev.get("event_type") not in BAR_TYPES:
                    continue
                if not mng_match(ev):
                    continue
                ts = parse_ts(ev.get("timestamp_utc"))
                if ts:
                    all_ts.append(ts)

    if len(all_ts) < 2:
        print("No MNG bar/tick events found (need BAR_ACCEPTED|BAR_RECEIVED_NO_STREAMS|ONBARUPDATE_CALLED).")
        return 1

    all_ts.sort()
    cutoff = max(all_ts) - timedelta(days=args.days)
    ts_filtered = [t for t in all_ts if t >= cutoff]
    if len(ts_filtered) < 2:
        ts_filtered = all_ts
        print("Note: fewer than 2 events in window; using full sample.", file=sys.stderr)

    deltas = [(ts_filtered[i] - ts_filtered[i - 1]).total_seconds() for i in range(1, len(ts_filtered))]
    sd = sorted(deltas)

    try:
        from modules.watchdog.config import DATA_STALL_THRESHOLD_SECONDS
    except ImportError:
        sys.path.insert(0, str(root))
        from modules.watchdog.config import DATA_STALL_THRESHOLD_SECONDS

    cur = float(DATA_STALL_THRESHOLD_SECONDS)
    frac_pct = 100.0 * sum(1 for d in deltas if d > cur) / len(deltas)

    print("MNG gap audit (inter-arrival seconds between consecutive bar/tick events)")
    print(f"  files_scanned: {len([f for f in files if f.exists()])}")
    print(f"  window_days: {args.days}")
    print(f"  event_count: {len(ts_filtered)}  gap_count: {len(deltas)}")
    print(f"  mean: {statistics.mean(deltas):.2f}  max: {max(deltas):.2f}")
    print(f"  p50: {_qtl(sd, 50):.2f}  p90: {_qtl(sd, 90):.2f}  p95: {_qtl(sd, 95):.2f}  p99: {_qtl(sd, 99):.2f}  p99.5: {_qtl(sd, 99.5):.2f}")
    print(f"  current_default_threshold_s: {cur}")
    print(f"  pct_gaps_gt_default_threshold: {frac_pct:.4f}%")
    print(f"  recommended_override_s (p99): {_qtl(sd, 99):.1f}  (p99.5): {_qtl(sd, 99.5):.1f}")
    print("  (Large max/p99 gaps often include overnight/session breaks; tune MNG separately from ES/NQ.)")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
