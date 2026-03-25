#!/usr/bin/env python3
"""
Deep dive: lifecycle invalid transitions vs mismatch, and fail_closed composition.
Uses the same log paths and Chicago day window as tools/daily_audit.py.
"""
from __future__ import annotations

import argparse
import json
import sys
from collections import Counter, defaultdict
from datetime import date, datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence, Tuple

_TOOLS = Path(__file__).resolve().parent
if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))

from log_audit import NormEvent, normalize_event, to_day

from daily_audit import CHICAGO, collect_robot_jsonl_paths, iter_jsonl


def resolve_project_root() -> Path:
    return Path(__file__).resolve().parents[1]


def resolve_log_dir(cli: Optional[str]) -> Path:
    if cli:
        return Path(cli).resolve()
    return resolve_project_root() / "logs" / "robot"


def ingest_days_single_pass(
    log_dir: Path, want: Sequence[date], tz_name: str
) -> Dict[date, List[NormEvent]]:
    """One scan of all JSONL; bucket by calendar day in tz_name (same idea as daily_audit)."""
    want_set = set(want)
    buckets: Dict[date, List[NormEvent]] = {d: [] for d in want}
    for path in collect_robot_jsonl_paths(log_dir):
        for _, line in iter_jsonl(path):
            if not line:
                continue
            try:
                obj = json.loads(line)
            except Exception:
                continue
            if not isinstance(obj, dict):
                continue
            ev = normalize_event(obj, path.name)
            if ev is None:
                continue
            # log_audit.to_day expects "chicago" | "utc" | "local", not IANA names
            bucket_tz = "utc" if tz_name == "utc" else "chicago"
            d = to_day(ev.ts_utc, bucket_tz)
            if d not in want_set:
                continue
            buckets[d].append(ev)
    for d in want_set:
        buckets[d].sort(key=lambda e: e.ts_utc)
    return buckets


def is_mismatch_signal(ev: str) -> bool:
    return "RECONCILIATION_MISMATCH" in ev and "CLEARED" not in ev and "METRICS" not in ev


def is_fail_closed_count(ev: str) -> bool:
    return "FAIL_CLOSED" in ev or ev.startswith("DISCONNECT_FAIL_CLOSED")


def analyze(
    events: Sequence[NormEvent],
    window_sec: float,
) -> Tuple[Dict[str, Any], Dict[str, Any]]:
    invalid: List[Tuple[datetime, str, str, str]] = []  # ts, from_s, to_s, iea
    mismatches: List[datetime] = []
    fail_closed_events: List[Tuple[datetime, str]] = []

    for e in events:
        ev = e.event or ""
        if ev == "SUPERVISORY_STATE_TRANSITION_INVALID":
            d = e.data or {}
            invalid.append(
                (
                    e.ts_utc,
                    str(d.get("from_state") or ""),
                    str(d.get("to_state") or ""),
                    str(d.get("iea_instance_id") or ""),
                )
            )
        if is_mismatch_signal(ev):
            mismatches.append(e.ts_utc)
        if is_fail_closed_count(ev):
            fail_closed_events.append((e.ts_utc, ev))

    pair_ctr: Counter[Tuple[str, str]] = Counter()
    for _, fs, ts, _ in invalid:
        pair_ctr[(fs, ts)] += 1

    w = timedelta(seconds=window_sec)
    # For each invalid: next mismatch within window
    inv_then_mm = 0
    for ts, _, _, _ in invalid:
        if any(ts < m <= ts + w for m in mismatches):
            inv_then_mm += 1
    # For each mismatch: prior invalid within window
    mm_after_inv = 0
    for m in mismatches:
        if any(m - w <= ts < m for ts, _, _, _ in invalid):
            mm_after_inv += 1

    return (
        {
            "invalid_total": len(invalid),
            "mismatch_total": len(mismatches),
            "pair_histogram": dict(pair_ctr.most_common(20)),
            "invalid_followed_by_mismatch_within_s": inv_then_mm,
            "mismatch_preceded_by_invalid_within_s": mm_after_inv,
            "window_sec": window_sec,
        },
        {
            "fail_closed_total": len(fail_closed_events),
            "fail_closed_by_exact_event": dict(Counter(ev for _, ev in fail_closed_events).most_common(50)),
        },
    )


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", default=None, help="Default: <repo>/logs/robot")
    ap.add_argument("--tz", default=CHICAGO)
    ap.add_argument("--window-sec", type=float, default=60.0)
    ap.add_argument(
        "--dates",
        nargs="+",
        required=True,
        help="Audit dates YYYY-MM-DD",
    )
    args = ap.parse_args()
    log_dir = resolve_log_dir(args.log_dir)
    want_dates = [date.fromisoformat(ds) for ds in args.dates]
    print(f"Scanning {log_dir} once for {len(want_dates)} day(s)...", flush=True)
    buckets = ingest_days_single_pass(log_dir, want_dates, args.tz)

    for ad in want_dates:
        ds = ad.isoformat()
        events = buckets.get(ad) or []
        print(f"\n{'='*60}\nDATE {ds}  log_dir={log_dir}  events={len(events)}\n{'='*60}")
        corr, fc = analyze(events, args.window_sec)
        print("\n--- SUPERVISORY_STATE_TRANSITION_INVALID ---")
        print(f"count: {corr['invalid_total']}")
        print(f"(from_state, to_state) top: {corr['pair_histogram']}")
        print("\n--- Correlation with RECONCILIATION_MISMATCH (excl CLEARED/METRICS) ---")
        print(f"mismatch_signal_lines: {corr['mismatch_total']}")
        print(f"window: {corr['window_sec']}s")
        inv_n = corr["invalid_total"]
        mm_n = corr["mismatch_total"]
        print(
            f"invalid then mismatch (invalid first, mismatch within window): {corr['invalid_followed_by_mismatch_within_s']}"
            + (f" / {inv_n}" if inv_n else " (n/a: no invalid)")
        )
        print(
            f"mismatch with prior invalid within window: {corr['mismatch_preceded_by_invalid_within_s']}"
            + (f" / {mm_n}" if mm_n else " (n/a: no mismatch)")
        )
        print("\n--- FAIL_CLOSED (substring rule, same as daily_audit safety scratch) ---")
        print(f"total lines: {fc['fail_closed_total']}")
        for k, v in list(fc["fail_closed_by_exact_event"].items())[:25]:
            print(f"  {v:6d}  {k}")
        if len(fc["fail_closed_by_exact_event"]) > 25:
            print(f"  ... ({len(fc['fail_closed_by_exact_event'])} distinct event types)")

    print("\nDone.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
