#!/usr/bin/env python3
"""
Targeted audit after one trade (or a log slice):

  Check A — EXECUTION_DEDUP_SKIPPED_PERMANENT vs later processing for same execution_id
  Check B — dedup_key / payloads containing "noid" (permanent key fallback)
  Check C — OnOrderUpdate raw_callback churn: same order_id + order_state with gaps >100ms

Pass engine/health JSONL paths that contain RobotEvents (event + data fields).
execution_trace.jsonl uses different field names (source, event_type).
"""

from __future__ import annotations

import argparse
import glob
import json
import sys
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path
from typing import Any


def parse_ts(s: str | None) -> datetime | None:
    if not s:
        return None
    s = str(s).replace("Z", "+00:00").replace("\\u002B", "+")
    if s.endswith("+00:00"):
        s = s[:-6]
    for fmt in ("%Y-%m-%dT%H:%M:%S.%f", "%Y-%m-%dT%H:%M:%S"):
        try:
            return datetime.strptime(s[:26], fmt).replace(tzinfo=timezone.utc)
        except ValueError:
            continue
    try:
        return datetime.fromisoformat(s.replace("Z", "+00:00"))
    except ValueError:
        return None


def iter_jsonl(path: Path):
    try:
        with path.open(encoding="utf-8", errors="replace") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    yield json.loads(line)
                except json.JSONDecodeError:
                    continue
    except OSError as e:
        print(f"Skip {path}: {e}", file=sys.stderr)


def check_a_engine_events(rows: list[tuple[datetime, dict[str, Any]]]) -> None:
    """Rows sorted by time: (ts, obj). event in obj.get('event') or message."""
    dedup_times: dict[str, list[datetime]] = defaultdict(list)
    fill_times: dict[str, list[datetime]] = defaultdict(list)
    for ts, o in rows:
        ev = (o.get("event") or o.get("message") or "").strip()
        data = o.get("data") or {}
        if ev == "EXECUTION_DEDUP_SKIPPED_PERMANENT":
            eid = str(data.get("execution_id") or "").strip()
            if eid:
                dedup_times[eid].append(ts)
        if ev in ("INTENT_FILL_UPDATE", "EXECUTION_FILLED", "EXECUTION_PARTIAL_FILL"):
            eid = str(data.get("execution_id") or data.get("broker_exec_id") or "").strip()
            if eid:
                fill_times[eid].append(ts)
    print("--- Check A: DEDUP_PERMANENT vs later fill-like events (same execution_id) ---")
    suspicious = []
    for eid, dts in dedup_times.items():
        fts = fill_times.get(eid, [])
        if not fts:
            continue
        for dt in dts:
            later = [t for t in fts if t > dt]
            if later:
                suspicious.append((eid, dt.isoformat(), min(later).isoformat()))
    if not dedup_times:
        print("  No EXECUTION_DEDUP_SKIPPED_PERMANENT lines in provided engine logs.")
        print("  (After a trade, point --engine at the JSONL that contains Robot execution events.)")
    elif not suspicious:
        print("  OK: No fill-like event strictly after a permanent dedup for the same execution_id.")
    else:
        print("  WARNING: Possible dedup key failure (processing after dedup for same execution_id):")
        for eid, t1, t2 in suspicious[:20]:
            print(f"    execution_id={eid!r} dedup~{t1}  later_event~{t2}")
        if len(suspicious) > 20:
            print(f"    ... +{len(suspicious) - 20} more")


def check_b_noid(rows: list[dict[str, Any]]) -> None:
    print("--- Check B: payloads mentioning noid (dedup_key fallback) ---")
    n = 0
    for o in rows:
        blob = json.dumps(o, ensure_ascii=False)
        if "noid" in blob.lower():
            data = o.get("data") or {}
            dk = data.get("dedup_key")
            ev = o.get("event") or o.get("message") or ""
            if "noid" in blob.lower():
                n += 1
                if n <= 15:
                    print(f"  [{ev}] dedup_key={dk!r} (snippet has noid)")
    if n == 0:
        print("  No noid in scanned lines (or no engine logs provided).")
    else:
        print(f"  Total lines with 'noid': {n}")
        print("  If many: execution_id was often missing; consider tightening NT execution id capture.")


def check_c_order_trace(trace_path: Path, gap_ms: float) -> None:
    print(f"--- Check C: OnOrderUpdate raw_callback, same order_id+order_state, gap > {gap_ms:.0f}ms ---")
    events: list[tuple[datetime, str, str, str]] = []
    for o in iter_jsonl(trace_path):
        if o.get("event") != "EXECUTION_TRACE":
            continue
        if o.get("source") != "OnOrderUpdate" or o.get("call_stage") != "raw_callback":
            continue
        ts = parse_ts(str(o.get("ts_utc") or ""))
        if ts is None:
            continue
        oid = str(o.get("order_id") or "")
        st = str(o.get("order_state") or "")
        events.append((ts, oid, st, str(o.get("instrument") or "")))
    events.sort(key=lambda x: x[0])
    by_key: dict[tuple[str, str, str], list[datetime]] = defaultdict(list)
    for ts, oid, st, inst in events:
        by_key[(inst, oid, st)].append(ts)
    churn = []
    for (inst, oid, st), tms in by_key.items():
        if len(tms) < 2:
            continue
        for i in range(1, len(tms)):
            delta = (tms[i] - tms[i - 1]).total_seconds() * 1000.0
            if delta > gap_ms:
                churn.append((inst, oid, st, delta, tms[i - 1], tms[i]))
    if not churn:
        print(f"  No pairs with gap > {gap_ms:.0f}ms for identical (instrument, order_id, order_state).")
    else:
        print("  Same state repeated after cooldown (50ms order dedup allows this over time):")
        for row in sorted(churn, key=lambda x: -x[3])[:25]:
            inst, oid, st, delta, t0, t1 = row
            oshow = oid if len(oid) <= 28 else oid[:24] + "..."
            print(f"    {inst} order={oshow} state={st} gap_ms={delta:.1f}  {t0.isoformat()} -> {t1.isoformat()}")


def main() -> int:
    ap = argparse.ArgumentParser(description="Audit execution dedup / noid / order churn.")
    ap.add_argument(
        "--engine",
        nargs="*",
        default=[],
        help="Glob(s) for engine/health JSONL with event+data (e.g. logs/health/*.jsonl)",
    )
    ap.add_argument(
        "--trace",
        type=Path,
        default=Path("logs/robot/execution_trace.jsonl"),
        help="execution_trace.jsonl for Check C",
    )
    ap.add_argument("--gap-ms", type=float, default=100.0, help="Check C minimum gap between duplicate order states")
    args = ap.parse_args()

    engine_rows: list[tuple[datetime, dict[str, Any]]] = []
    flat_for_b: list[dict[str, Any]] = []
    for pattern in args.engine:
        for p in sorted(glob.glob(pattern, recursive=True)):
            path = Path(p)
            for o in iter_jsonl(path):
                flat_for_b.append(o)
                ts = parse_ts(str(o.get("ts_utc") or o.get("timestamp_utc") or ""))
                if ts is not None:
                    engine_rows.append((ts, o))
    engine_rows.sort(key=lambda x: x[0])

    check_a_engine_events(engine_rows)
    print()
    check_b_noid(flat_for_b)
    print()
    if args.trace.is_file():
        check_c_order_trace(args.trace, args.gap_ms)
    else:
        print(f"--- Check C: skipped (no file {args.trace}) ---")

    return 0


if __name__ == "__main__":
    raise SystemExit(main())
