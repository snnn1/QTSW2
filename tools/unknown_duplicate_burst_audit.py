#!/usr/bin/env python3
"""
Duplicate EXECUTION_UPDATE_UNKNOWN_ORDER callback audit (robot JSONL).

Characterizes bursts only — no fix suggestions.

Usage:
  python tools/unknown_duplicate_burst_audit.py
  python tools/unknown_duplicate_burst_audit.py --log-dir c:/Users/jakej/QTSW2/logs/robot
"""
from __future__ import annotations

import argparse
import bisect
import json
import sys
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Tuple

_TOOLS = Path(__file__).resolve().parent
if str(_TOOLS) not in sys.path:
    sys.path.insert(0, str(_TOOLS))

try:
    from log_audit import parse_ts, resolve_log_dir  # type: ignore
except Exception:  # pragma: no cover

    def resolve_log_dir(cli: Optional[str]) -> Path:
        return Path(cli) if cli else Path("logs") / "robot"

    def parse_ts(ts: str) -> Optional[datetime]:
        if not ts:
            return None
        try:
            if str(ts).endswith("Z"):
                return datetime.fromisoformat(str(ts)[:-1]).replace(tzinfo=timezone.utc)
            dt = datetime.fromisoformat(str(ts))
            if dt.tzinfo is None:
                dt = dt.replace(tzinfo=timezone.utc)
            return dt.astimezone(timezone.utc)
        except Exception:
            return None


TARGET = "EXECUTION_UPDATE_UNKNOWN_ORDER"

REGISTRY_BEFORE_EVENTS = frozenset(
    {
        "ORDER_REGISTERED",
        "ORDER_REGISTRY_REGISTERED",
        "ORDER_REGISTRY_FLATTEN_REGISTERED",
        "ORDER_REGISTRY_ADOPTED",
        "ORDER_ACKNOWLEDGED",
        "ORDER_REGISTRY_EXEC_RESOLVED",
        "ORDER_REGISTRY_LIFECYCLE",
        "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
    }
)

SUBMIT_BEFORE_EVENTS = frozenset(
    {
        "ENTRY_SUBMIT_PRECHECK",
        "ORDER_SUBMITTED",
        "ORDER_SUBMIT_ATTEMPT",
        "ORDER_SUBMIT_SUCCESS",
        "ENTRY_SUBMITTED",
        "ENTRY_ORDERS_RESUBMITTED",
        "FLATTEN_ORDER_SUBMITTED",
        "FLATTEN_SUBMITTED",
        "FLATTEN_SENT",
        "STOP_BRACKETS_SUBMIT_ATTEMPT",
        "STOP_BRACKETS_SUBMITTED",
    }
)


def _row_ts(obj: Dict[str, Any]) -> Optional[datetime]:
    t = obj.get("ts_utc") or obj.get("ts") or obj.get("timestamp")
    if isinstance(t, (int, float)):
        return datetime.fromtimestamp(float(t) / 1000.0, tz=timezone.utc)
    return parse_ts(str(t or ""))


def _data(obj: Dict[str, Any]) -> Dict[str, Any]:
    d = obj.get("data")
    return d if isinstance(d, dict) else {}


def _norm_str(v: Any) -> str:
    return str(v).strip() if v is not None else ""


def row_oid(obj: Dict[str, Any]) -> str:
    d = _data(obj)
    return _norm_str(d.get("broker_order_id") or d.get("order_id"))


def row_instrument(obj: Dict[str, Any]) -> str:
    d = _data(obj)
    return _norm_str(obj.get("instrument") or d.get("instrument"))


def unknown_signature(obj: Dict[str, Any]) -> Tuple[str, str, str, str]:
    d = _data(obj)
    oid = _norm_str(d.get("broker_order_id") or d.get("order_id"))
    st = _norm_str(d.get("order_state"))
    fq = _norm_str(d.get("fill_quantity") or d.get("fill_qty"))
    eid = _norm_str(d.get("execution_id") or d.get("broker_execution_id") or d.get("broker_exec_id"))
    return (oid, st, fq, eid)


def load_robot_jsonl(log_dir: Path) -> List[Dict[str, Any]]:
    rows: List[Dict[str, Any]] = []
    for fp in sorted(log_dir.glob("robot_*.jsonl")):
        if fp.name.lower() == "robot_skeleton.jsonl":
            continue
        try:
            with fp.open("r", encoding="utf-8", errors="replace") as fh:
                for line in fh:
                    line = line.strip()
                    if not line:
                        continue
                    try:
                        obj = json.loads(line)
                    except json.JSONDecodeError:
                        continue
                    if not isinstance(obj, dict):
                        continue
                    dt = _row_ts(obj)
                    if dt is None:
                        continue
                    rows.append({"ts": dt, "event": str(obj.get("event") or ""), "obj": obj})
        except OSError:
            continue
    rows.sort(key=lambda r: r["ts"])
    return rows


def max_events_in_ms_window(sorted_ts_ms: List[int], window_ms: int) -> int:
    if not sorted_ts_ms:
        return 0
    best = 1
    j = 0
    n = len(sorted_ts_ms)
    for i in range(n):
        while j <= i and sorted_ts_ms[i] - sorted_ts_ms[j] > window_ms:
            j += 1
        best = max(best, i - j + 1)
    return best


def max_identical_in_ms_window(events: List[Dict[str, Any]], window_ms: int) -> int:
    """Max count of rows sharing the same signature within any window of window_ms."""
    by_sig: Dict[Tuple[str, str, str, str], List[int]] = defaultdict(list)
    for e in events:
        ms = int(e["ts"].timestamp() * 1000)
        by_sig[unknown_signature(e["obj"])].append(ms)
    best = 0
    for ms_list in by_sig.values():
        ms_list.sort()
        best = max(best, max_events_in_ms_window(ms_list, window_ms))
    return best


def classify_duplicate_pattern(ev_rows: List[Dict[str, Any]]) -> str:
    """A) HARD_DUPLICATE B) SOFT_DUPLICATE C) SPREAD_REPEAT or SINGLE."""
    if len(ev_rows) < 2:
        return "SINGLE"
    ts_sorted = sorted(int(r["ts"].timestamp() * 1000) for r in ev_rows)
    hard = max_identical_in_ms_window(ev_rows, 100)
    if hard >= 3:
        return "HARD_DUPLICATE"
    multi_sig = len({unknown_signature(r["obj"]) for r in ev_rows}) >= 2
    max1s = max_events_in_ms_window(ts_sorted, 1000)
    span_ms = ts_sorted[-1] - ts_sorted[0]
    if max1s >= 2 and multi_sig and span_ms <= 1000:
        return "SOFT_DUPLICATE"
    if max1s >= 2 and span_ms <= 1000 and not multi_sig:
        return "SOFT_DUPLICATE"
    if span_ms > 1000 and len(ev_rows) >= 2:
        return "SPREAD_REPEAT"
    if len(ev_rows) >= 2:
        return "SPREAD_REPEAT"
    return "SINGLE"


def pre_context_flags(
    all_rows: List[Dict[str, Any]],
    ts_list: List[datetime],
    instrument: str,
    oid: str,
    first_unknown_ts: datetime,
    pad_sec: float = 3.0,
) -> Tuple[bool, bool]:
    """Returns (has_registry_same_oid_before, has_submit_family_before_first)."""
    lo = first_unknown_ts - timedelta(seconds=pad_sec)
    hi = first_unknown_ts
    i0 = bisect.bisect_left(ts_list, lo)
    i1 = bisect.bisect_left(ts_list, hi)
    has_reg = False
    has_submit = False
    for j in range(i0, i1):
        r = all_rows[j]
        ev = r["event"]
        if ev in REGISTRY_BEFORE_EVENTS:
            o = row_oid(r["obj"])
            if o == oid:
                has_reg = True
        if ev in SUBMIT_BEFORE_EVENTS:
            ins = row_instrument(r["obj"])
            if instrument and ins == instrument:
                has_submit = True
    return has_reg, has_submit


def classify_storm_tracking(
    has_registry_oid_before: bool, has_submit_before: bool
) -> str:
    if has_registry_oid_before:
        return "CALLBACK_STORM_AFTER_TRACKING"
    if has_submit_before:
        return "CALLBACK_STORM_BEFORE_TRACKING"
    return "UNKNOWN_SOURCE"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", default=None)
    args = ap.parse_args()

    log_dir = resolve_log_dir(args.log_dir)
    if not log_dir.is_dir():
        print(f"Log dir not found: {log_dir}", file=sys.stderr)
        return 1

    all_rows = load_robot_jsonl(log_dir)
    ts_list = [r["ts"] for r in all_rows]

    unk_events: List[Dict[str, Any]] = []
    for r in all_rows:
        if r["event"] != TARGET:
            continue
        oid = row_oid(r["obj"])
        ins = row_instrument(r["obj"])
        if not oid:
            continue
        unk_events.append({**r, "oid": oid, "instrument": ins})

    by_group: Dict[Tuple[str, str], List[Dict[str, Any]]] = defaultdict(list)
    for e in unk_events:
        by_group[(e["instrument"], e["oid"])].append(e)

    def ms_list(ev: List[Dict[str, Any]]) -> List[int]:
        return sorted(int(x["ts"].timestamp() * 1000) for x in ev)

    analyses: List[Dict[str, Any]] = []
    for (ins, oid), ev in by_group.items():
        ev_sorted = sorted(ev, key=lambda x: x["ts"])
        ts_ms = ms_list(ev_sorted)
        uniq_ts = len({x["ts"] for x in ev_sorted})
        total = len(ev_sorted)
        max10 = max_events_in_ms_window(ts_ms, 10)
        max100 = max_events_in_ms_window(ts_ms, 100)
        max1s = max_events_in_ms_window(ts_ms, 1000)
        dup_cls = classify_duplicate_pattern(ev_sorted)
        first_u = ev_sorted[0]["ts"]
        has_reg, has_sub = pre_context_flags(all_rows, ts_list, ins, oid, first_u)
        storm_cls = classify_storm_tracking(has_reg, has_sub)
        analyses.append(
            {
                "instrument": ins or "(empty)",
                "oid": oid,
                "total": total,
                "unique_ts": uniq_ts,
                "max_10ms": max10,
                "max_100ms": max100,
                "max_1s": max1s,
                "dup_class": dup_cls,
                "storm_class": storm_cls,
                "has_registry_before": has_reg,
                "has_submit_before": has_sub,
                "events": ev_sorted,
            }
        )

    burst_groups = [a for a in analyses if a["total"] >= 2]
    n_burst = len(burst_groups)
    hard_n = sum(1 for a in analyses if a["dup_class"] == "HARD_DUPLICATE")
    soft_n = sum(1 for a in analyses if a["dup_class"] == "SOFT_DUPLICATE")
    spread_n = sum(1 for a in analyses if a["dup_class"] == "SPREAD_REPEAT")

    def pct(num: float, den: float) -> float:
        return (100.0 * num / den) if den else 0.0

    avg_burst = sum(a["total"] for a in burst_groups) / n_burst if n_burst else 0.0
    largest = max((a["total"] for a in analyses), default=0)

    before_n = sum(1 for a in burst_groups if a["storm_class"] == "CALLBACK_STORM_BEFORE_TRACKING")
    after_n = sum(1 for a in burst_groups if a["storm_class"] == "CALLBACK_STORM_AFTER_TRACKING")
    unk_n = sum(1 for a in burst_groups if a["storm_class"] == "UNKNOWN_SOURCE")
    primary_tracking = (
        "BEFORE_TRACKING"
        if before_n >= after_n and before_n >= unk_n
        else ("AFTER_TRACKING" if after_n >= unk_n else "UNKNOWN_SOURCE")
    )

    print("=" * 78)
    print("EXECUTION_UPDATE_UNKNOWN_ORDER duplicate callback audit (log-derived)")
    print(f"Log dir: {log_dir.resolve()}")
    print(f"UNKNOWN rows (with broker_order_id): {len(unk_events)}")
    print(f"Groups (instrument, broker_order_id): {len(analyses)}")
    print(f"Groups with burst (count >= 2): {n_burst}")
    print()
    print(
        "Duplicate pattern: HARD = same signature >= 3x within 100ms; "
        "SOFT = >= 2 events within 1s with same order_id (incl. varying fields); "
        "SPREAD = repeat over > 1s (and not HARD)."
    )
    print(
        "Storm context: +/- 3s before first UNKNOWN; registry = same broker_order_id; "
        "submit family = same instrument."
    )
    print("=" * 78)

    top10 = sorted(burst_groups, key=lambda a: (-a["max_100ms"], -a["total"]))[:10]
    print()
    print("TOP 10 WORST BURSTS (by max events / 100ms, then total count)")
    print("-" * 78)
    for i, a in enumerate(top10, 1):
        combo = f"{a['dup_class']} | {a['storm_class']}"
        print(f"{i}. INSTRUMENT={a['instrument']}")
        print(f"   BROKER_ORDER_ID={a['oid']}")
        print(f"   COUNT={a['total']}  unique_ts={a['unique_ts']}  max/10ms={a['max_10ms']}  max/100ms={a['max_100ms']}  max/1s={a['max_1s']}")
        print(f"   CLASSIFICATION={combo}")
        print()

    print("=" * 78)
    print("SUMMARY")
    print("=" * 78)
    d_all = len(analyses)
    print(
        f"1. Duplicate pattern (of all {d_all} groups): "
        f"HARD_DUPLICATE {pct(hard_n, d_all):.1f}% ({hard_n}); "
        f"SOFT_DUPLICATE {pct(soft_n, d_all):.1f}% ({soft_n}); "
        f"SPREAD_REPEAT {pct(spread_n, d_all):.1f}% ({spread_n}); "
        f"SINGLE {d_all - hard_n - soft_n - spread_n}"
    )
    if n_burst:
        bh = sum(1 for a in burst_groups if a["dup_class"] == "HARD_DUPLICATE")
        bs = sum(1 for a in burst_groups if a["dup_class"] == "SOFT_DUPLICATE")
        bp = sum(1 for a in burst_groups if a["dup_class"] == "SPREAD_REPEAT")
        print(
            f"   (Of {n_burst} burst groups only): HARD {pct(bh, n_burst):.1f}%, "
            f"SOFT {pct(bs, n_burst):.1f}%, SPREAD {pct(bp, n_burst):.1f}%"
        )
    print(f"2. Avg burst size (groups with count >= 2): {avg_burst:.2f}")
    print(f"3. Largest burst observed: {largest} events in one group")
    print(
        f"4. Primary cause (burst groups, CALLBACK_STORM_*): "
        f"{primary_tracking} "
        f"(BEFORE_TRACKING={before_n}, AFTER_TRACKING={after_n}, UNKNOWN_SOURCE={unk_n})"
    )
    tracked = before_n + after_n
    if tracked:
        print(
            f"   BEFORE_TRACKING vs AFTER_TRACKING only ({tracked} burst groups with submit or registry cue): "
            f"{pct(before_n, tracked):.1f}% before, {pct(after_n, tracked):.1f}% after"
        )
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
