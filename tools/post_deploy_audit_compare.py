#!/usr/bin/env python3
"""
Post-deploy comparison: ORDER_REGISTRY_MISSING signals, supervisory invalids, trade_readiness.

Run 3–5 FULL Chicago days after deploy, then either:
  python tools/post_deploy_audit_compare.py --dates 2026-03-25 2026-03-26 ...
or (re)generate audit JSON first:
  python tools/post_deploy_audit_compare.py --dates ... --run-daily-audit

Column definitions (raw JSONL unless noted):
  - orm_mismatch: any line with data.mismatch_type == ORDER_REGISTRY_MISSING (DETECTED / PERSISTENT / BLOCKED / …; excludes event names containing METRICS)
  - orm_fail_closed_raw: ORDER_REGISTRY_MISSING_FAIL_CLOSED event lines
  - orm_fail_closed_bursts: distinct bursts of ORM fail_closed (gap > burst-gap-ms => new burst)
  - supervisory_invalid: SUPERVISORY_STATE_TRANSITION_INVALID lines
  - trade_readiness: trade_readiness.decision from reports/daily_audit/<date>.json (--run-daily-audit if missing)
"""
from __future__ import annotations

import argparse
import json
import subprocess
import sys
from datetime import date, datetime
from pathlib import Path
from typing import Any, Dict, List, Optional, Sequence

_TOOLS = Path(__file__).resolve().parent
if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))

from log_audit import NormEvent

from audit_signal_correlation import ingest_days_single_pass, resolve_log_dir
from daily_audit import CHICAGO


def resolve_project_root() -> Path:
    return Path(__file__).resolve().parents[1]


def resolve_reports_dir(cli: Optional[str]) -> Path:
    if cli:
        return Path(cli).resolve()
    return resolve_project_root() / "reports" / "daily_audit"


def count_fail_closed_bursts(timestamps: Sequence[datetime], gap_ms: float) -> int:
    ts = sorted(timestamps)
    if not ts:
        return 0
    bursts = 1
    prev = ts[0]
    gap_s = gap_ms / 1000.0
    for t in ts[1:]:
        if (t - prev).total_seconds() > gap_s:
            bursts += 1
        prev = t
    return bursts


def metrics_for_day(events: Sequence[NormEvent]) -> Dict[str, Any]:
    orm_mismatch = 0
    orm_fc_ts: List[datetime] = []
    sup_inv = 0

    for e in events:
        ev = e.event or ""
        data = e.data if isinstance(e.data, dict) else {}

        if data.get("mismatch_type") == "ORDER_REGISTRY_MISSING" and "METRICS" not in (ev or ""):
            orm_mismatch += 1
        if ev == "ORDER_REGISTRY_MISSING_FAIL_CLOSED":
            orm_fc_ts.append(e.ts_utc)
        if ev == "SUPERVISORY_STATE_TRANSITION_INVALID":
            sup_inv += 1

    return {
        "orm_mismatch": orm_mismatch,
        "orm_fail_closed_raw": len(orm_fc_ts),
        "orm_fail_closed_bursts": 0,  # filled below
        "_ts": orm_fc_ts,
        "supervisory_invalid": sup_inv,
    }


def load_trade_readiness(reports_dir: Path, d: date) -> str:
    p = reports_dir / f"{d.isoformat()}.json"
    if not p.is_file():
        return "MISSING_JSON"
    try:
        with p.open("r", encoding="utf-8") as f:
            o = json.load(f)
        tr = o.get("trade_readiness") or {}
        dec = tr.get("decision")
        mode = (o.get("meta") or {}).get("audit_mode", "")
        eng = (o.get("meta") or {}).get("engine_full")
        suffix = ""
        if mode:
            suffix = f" ({mode}" + (f", engine_full={eng}" if eng is not None else "") + ")"
        return f"{dec}{suffix}" if dec else f"UNKNOWN{suffix}"
    except Exception:
        return "READ_ERROR"


def run_daily_audit(project_root: Path, audit_date: date, tz: str) -> None:
    script = project_root / "tools" / "daily_audit.py"
    cmd = [sys.executable, str(script), "--date", audit_date.isoformat(), "--tz", tz]
    subprocess.run(cmd, cwd=str(project_root), check=True)


def main() -> int:
    ap = argparse.ArgumentParser(description="Post-deploy column comparison (logs + optional audit JSON).")
    ap.add_argument("--dates", nargs="+", required=True, help="YYYY-MM-DD (Chicago audit days)")
    ap.add_argument("--log-dir", default=None)
    ap.add_argument("--reports-dir", default=None)
    ap.add_argument("--tz", default=CHICAGO, help="Must match daily_audit (default: America/Chicago)")
    ap.add_argument(
        "--burst-gap-ms",
        type=float,
        default=2000.0,
        help="New ORM fail_closed burst if gap to previous event exceeds this (default 2000)",
    )
    ap.add_argument(
        "--run-daily-audit",
        action="store_true",
        help="Run tools/daily_audit.py for each date before reading trade_readiness",
    )
    ap.add_argument("--markdown", action="store_true", help="Print a markdown table row per date")
    args = ap.parse_args()

    want = [date.fromisoformat(x) for x in args.dates]
    log_dir = resolve_log_dir(args.log_dir)
    reports_dir = resolve_reports_dir(args.reports_dir)
    project_root = resolve_project_root()

    if args.run_daily_audit:
        for d in want:
            print(f"Running daily_audit for {d.isoformat()} ...", flush=True)
            run_daily_audit(project_root, d, args.tz)

    print(f"Scanning logs once: {log_dir}", flush=True)
    buckets = ingest_days_single_pass(log_dir, want, args.tz)

    rows: List[Dict[str, Any]] = []
    for d in sorted(want):
        events = buckets.get(d) or []
        m = metrics_for_day(events)
        m["orm_fail_closed_bursts"] = count_fail_closed_bursts(m.pop("_ts"), args.burst_gap_ms)
        m["date"] = d.isoformat()
        m["trade_readiness"] = load_trade_readiness(reports_dir, d)
        rows.append(m)

    if args.markdown:
        print()
        print(
            "| date | ORM mismatch (data.mismatch_type) | ORM_FAIL_CLOSED raw | ORM_FAIL_CLOSED bursts | "
            "SUPERVISORY_STATE_TRANSITION_INVALID | trade_readiness |"
        )
        print("|------|----------------------------------:|--------------------:|-----------------------:|------------------------------------:|-----------------|")
        for r in rows:
            print(
                f"| {r['date']} | {r['orm_mismatch']} | {r['orm_fail_closed_raw']} | {r['orm_fail_closed_bursts']} | "
                f"{r['supervisory_invalid']} | {r['trade_readiness']} |"
            )
        print()
    else:
        for r in rows:
            print(f"{r['date']}: orm_mismatch={r['orm_mismatch']} orm_fc_raw={r['orm_fail_closed_raw']} "
                  f"orm_fc_bursts={r['orm_fail_closed_bursts']} sup_invalid={r['supervisory_invalid']} "
                  f"trade_readiness={r['trade_readiness']}")

    print(f"(burst_gap_ms={args.burst_gap_ms})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
