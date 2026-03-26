#!/usr/bin/env python3
"""
Group execution_trace.jsonl by execution_id for OnExecutionUpdate raw_callback rows.

Output columns: execution_id, total_callbacks, unique_fill_qty, unique_timestamps
Flag rows where total_callbacks > 3 AND unique_fill_qty == 1 (duplicate callbacks, single reported fill qty).

Usage:
  python tools/analyze_execution_trace_by_execution_id.py
  python tools/analyze_execution_trace_by_execution_id.py --trace path/to/execution_trace.jsonl
  python tools/analyze_execution_trace_by_execution_id.py --csv out.csv
"""

from __future__ import annotations

import argparse
import csv
import json
import sys
from collections import defaultdict
from pathlib import Path
from typing import Any, DefaultDict, Dict, List, Set, Tuple

from log_audit import resolve_log_dir


def _get(d: Dict[str, Any], *keys: str) -> Any:
    for k in keys:
        if k in d and d[k] is not None:
            return d[k]
    return None


def load_rows(path: Path) -> List[Dict[str, Any]]:
    rows: List[Dict[str, Any]] = []
    with path.open(encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                obj = json.loads(line)
            except json.JSONDecodeError:
                continue
            if not isinstance(obj, dict):
                continue
            rows.append(obj)
    return rows


def analyze(path: Path) -> Tuple[List[Tuple[str, int, int, int]], int, int]:
    """Returns (summary_rows, skipped_empty_exec_id, matching_callbacks_total)."""
    per_exec: DefaultDict[str, List[Dict[str, Any]]] = defaultdict(list)
    skipped_empty = 0
    matching = 0

    for obj in load_rows(path):
        ev = str(_get(obj, "event", "Event") or "")
        if ev and ev != "EXECUTION_TRACE":
            continue
        src = str(_get(obj, "source", "Source") or "")
        stage = str(_get(obj, "call_stage", "callStage") or "")
        if src != "OnExecutionUpdate" or stage != "raw_callback":
            continue
        matching += 1
        eid = str(_get(obj, "execution_id", "executionId") or "").strip()
        if not eid:
            skipped_empty += 1
            continue
        per_exec[eid].append(obj)

    out: List[Tuple[str, int, int, int]] = []
    for eid, items in per_exec.items():
        ts_set: Set[str] = set()
        fq_set: Set[int] = set()
        for it in items:
            ts = _get(it, "ts_utc", "tsUtc", "TsUtc")
            ts_set.add(str(ts) if ts is not None else "")
            fq = _get(it, "fill_qty", "fillQty")
            try:
                fq_set.add(int(fq) if fq is not None else 0)
            except (TypeError, ValueError):
                fq_set.add(0)
        out.append((eid, len(items), len(fq_set), len(ts_set)))

    out.sort(key=lambda x: (-x[1], x[0]))
    return out, skipped_empty, matching


def main() -> int:
    ap = argparse.ArgumentParser(description="execution_trace.jsonl: group OnExecutionUpdate raw_callback by execution_id")
    ap.add_argument("--trace", default=None, help="Path to execution_trace.jsonl (default: <log-dir>/execution_trace.jsonl)")
    ap.add_argument("--log-dir", default=None, help="Robot log directory (default: logs/robot)")
    ap.add_argument("--csv", default=None, help="Optional CSV output path")
    ap.add_argument(
        "--flag-callbacks",
        type=int,
        default=3,
        help="Flag when total_callbacks > this value (default: 3)",
    )
    args = ap.parse_args()

    if args.trace:
        trace_path = Path(args.trace)
    else:
        trace_path = resolve_log_dir(args.log_dir) / "execution_trace.jsonl"

    if not trace_path.is_file():
        print(f"ERROR: trace file not found: {trace_path.resolve()}", file=sys.stderr)
        return 2

    summary, skip_empty, matching_callbacks = analyze(trace_path)

    print(f"trace_file: {trace_path.resolve()}")
    print(f"execution_ids (OnExecutionUpdate + raw_callback + non-empty execution_id): {len(summary)}")
    print(f"OnExecutionUpdate raw_callback rows (all): {matching_callbacks}")
    print(f"skipped_empty_execution_id (callbacks with no execution_id): {skip_empty}")
    print()

    header = ("execution_id", "total_callbacks", "unique_fill_qty", "unique_timestamps", "flagged")
    rows_out: List[Dict[str, Any]] = []

    flagged: List[Tuple[str, int, int, int]] = []
    for eid, total, ufq, uts in summary:
        is_flagged = total > args.flag_callbacks and ufq == 1
        if is_flagged:
            flagged.append((eid, total, ufq, uts))
        rows_out.append(
            {
                "execution_id": eid,
                "total_callbacks": total,
                "unique_fill_qty": ufq,
                "unique_timestamps": uts,
                "flagged": "YES" if is_flagged else "",
            }
        )

    # Console table
    colw = (40, 16, 16, 18, 6)
    hdr = f"{'execution_id'.ljust(colw[0])} {'total_callbacks'.rjust(colw[1])} {'unique_fill_qty'.rjust(colw[2])} {'unique_timestamps'.rjust(colw[3])} {'flag'.ljust(colw[4])}"
    print(hdr)
    print("-" * len(hdr))
    for row in rows_out:
        print(
            f"{row['execution_id'][: colw[0] - 1].ljust(colw[0])} "
            f"{row['total_callbacks']!s:>{colw[1]}} "
            f"{row['unique_fill_qty']!s:>{colw[2]}} "
            f"{row['unique_timestamps']!s:>{colw[3]}} "
            f"{str(row['flagged']).ljust(colw[4])}"
        )

    print()
    print(f"--- FLAGGED (total_callbacks > {args.flag_callbacks} AND unique_fill_qty == 1): {len(flagged)} ---")
    for eid, total, ufq, uts in flagged:
        print(f"  {eid}  callbacks={total}  unique_fill_qty={ufq}  unique_timestamps={uts}")

    if args.csv:
        cp = Path(args.csv)
        cp.parent.mkdir(parents=True, exist_ok=True)
        with cp.open("w", newline="", encoding="utf-8") as f:
            w = csv.DictWriter(f, fieldnames=list(header))
            w.writeheader()
            for row in rows_out:
                w.writerow({k: row[k] for k in header})

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
