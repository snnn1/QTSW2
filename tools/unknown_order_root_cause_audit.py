#!/usr/bin/env python3
"""
Root-cause classification for EXECUTION_UPDATE_UNKNOWN_ORDER from robot JSONL logs.

Diagnostic only — no fix suggestions.

Usage:
  python tools/unknown_order_root_cause_audit.py
  python tools/unknown_order_root_cause_audit.py --log-dir c:/Users/jakej/QTSW2/logs/robot
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

# Import log dir + ts parse from sibling module
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


TARGET_EVENT = "EXECUTION_UPDATE_UNKNOWN_ORDER"
QTSW2 = "QTSW2:"


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
                    rows.append(
                        {
                            "ts": dt,
                            "event": str(obj.get("event") or obj.get("event_type") or ""),
                            "instrument": str(obj.get("instrument") or _data(obj).get("instrument") or ""),
                            "obj": obj,
                            "file": fp.name,
                        }
                    )
        except OSError:
            continue
    rows.sort(key=lambda r: r["ts"])
    return rows


def build_ts_list(rows: List[Dict[str, Any]]) -> List[datetime]:
    return [r["ts"] for r in rows]


def build_oid_index(rows: List[Dict[str, Any]]) -> Dict[str, Tuple[List[datetime], List[int]]]:
    pairs: Dict[str, List[Tuple[datetime, int]]] = defaultdict(list)
    for i, r in enumerate(rows):
        d = _data(r["obj"])
        oid = _norm_str(d.get("broker_order_id") or d.get("order_id"))
        if oid:
            pairs[oid].append((r["ts"], i))
    out: Dict[str, Tuple[List[datetime], List[int]]] = {}
    for oid, plist in pairs.items():
        times = [x[0] for x in plist]
        idxs = [x[1] for x in plist]
        out[oid] = (times, idxs)
    return out


def context_window(
    rows: List[Dict[str, Any]],
    ts_list: List[datetime],
    center_ts: datetime,
    broker_order_id: str,
    execution_id: str,
    delta: timedelta,
) -> List[Dict[str, Any]]:
    lo = center_ts - delta
    hi = center_ts + delta
    i0 = bisect.bisect_left(ts_list, lo)
    i1 = bisect.bisect_right(ts_list, hi)
    out: List[Dict[str, Any]] = []
    oid = broker_order_id.strip()
    eid = execution_id.strip()
    for j in range(i0, i1):
        r = rows[j]
        d = _data(r["obj"])
        ro = _norm_str(d.get("broker_order_id") or d.get("order_id"))
        re = _norm_str(d.get("execution_id") or d.get("broker_execution_id") or d.get("broker_exec_id"))
        if oid and ro == oid:
            out.append(r)
            continue
        if eid and re == eid:
            out.append(r)
            continue
    return out


def _prior_same_oid(rows_ctx: List[Dict[str, Any]], unknown_ts: datetime, oid: str) -> List[Dict[str, Any]]:
    oid = oid.strip()
    return [r for r in rows_ctx if r["ts"] < unknown_ts and _norm_str(_data(r["obj"]).get("broker_order_id") or _data(r["obj"]).get("order_id")) == oid]


def find_prior_rows(
    oid_index: Dict[str, Tuple[List[datetime], List[int]]],
    rows: List[Dict[str, Any]],
    ts0: datetime,
    broker_order_id: str,
    max_lookback_sec: float,
) -> List[Dict[str, Any]]:
    oid = broker_order_id.strip()
    if not oid or oid not in oid_index:
        return []
    times, idxs = oid_index[oid]
    lo = ts0 - timedelta(seconds=max_lookback_sec)
    k = bisect.bisect_left(times, ts0)
    out: List[Dict[str, Any]] = []
    j = k - 1
    while j >= 0 and times[j] >= lo:
        out.append(rows[idxs[j]])
        j -= 1
    return out


def classify(
    rows_ctx: List[Dict[str, Any]],
    unknown_row: Dict[str, Any],
    rows: List[Dict[str, Any]],
    lookback_sec: float,
    oid_index: Dict[str, Tuple[List[datetime], List[int]]],
) -> str:
    obj = unknown_row["obj"]
    d = _data(obj)
    ts0 = unknown_row["ts"]
    tag = _norm_str(d.get("tag"))
    oid = _norm_str(d.get("broker_order_id") or d.get("order_id"))
    order_state = _norm_str(d.get("order_state"))

    intent_top = _norm_str(obj.get("intent_id"))
    intent_data = _norm_str(d.get("intent_id"))
    intent_id = intent_top or intent_data

    # C) EXTERNAL_ORDER — non-robot tag or empty tag
    if not tag or not tag.upper().startswith(QTSW2):
        return "EXTERNAL_ORDER"

    # E) TAG_DECODE_FAILURE — before DUPLICATE / MAPPING: flatten envelope is not an OrderMap key
    if "QTSW2:FLATTEN:" in tag or (tag.upper().startswith(QTSW2) and ":FLATTEN:" in tag.upper()):
        return "TAG_DECODE_FAILURE"
    if intent_id == "FLATTEN":
        return "TAG_DECODE_FAILURE"
    for r in rows_ctx:
        ev = r["event"]
        if "ORPHAN_FILL" in ev or "INTENT_NOT_FOUND" in ev:
            return "TAG_DECODE_FAILURE"
        msg = json.dumps(r["obj"], default=str).upper()
        if "TRADING_DATE_NULL" in msg:
            return "TAG_DECODE_FAILURE"

    priors_oid = _prior_same_oid(rows_ctx, ts0, oid)

    # A) LATE_CALLBACK — mapped fill for same broker_order_id before UNKNOWN; trade-complete proxies in context
    had_mapped_fill = False
    for r in priors_oid:
        dd = _data(r["obj"])
        if r["event"] != "EXECUTION_FILLED":
            continue
        ro = _norm_str(dd.get("broker_order_id") or dd.get("order_id"))
        if ro != oid:
            continue
        mapped = str(dd.get("mapped", "")).lower()
        pe = str(dd.get("position_effect", "")).upper()
        if mapped == "true" or pe == "CLOSE":
            had_mapped_fill = True
            break
    completion_markers = (
        "COMPLETED_INTENT_RECEIVED_FILL",
        "FLATTEN_SKIPPED_ACCOUNT_FLAT",
        "FLATTEN_RESOLVED",
        "FLATTEN_BROKER_FLAT_CONFIRMED",
        "TRADE_COMPLETED",
    )
    had_completion = any(r["event"] in completion_markers and r["ts"] < ts0 for r in rows_ctx)
    flatten_after = any(
        r["event"].startswith("FLATTEN_") and r["ts"] < ts0 for r in rows_ctx
    )
    if had_mapped_fill and (had_completion or flatten_after):
        return "LATE_CALLBACK"

    # D) DUPLICATE_CALLBACK — same execution_id in window with same fill_qty + order_state (>=3 lines),
    # or repeated UNKNOWN rows (NT often omits execution_id on these events).
    eid = _norm_str(d.get("execution_id") or d.get("broker_execution_id"))
    if eid:
        bucket: List[Tuple[str, str]] = []
        for r in rows_ctx:
            dd = _data(r["obj"])
            ei = _norm_str(dd.get("execution_id") or dd.get("broker_execution_id"))
            if ei != eid:
                continue
            fq = _norm_str(dd.get("fill_quantity") or dd.get("fill_qty"))
            os_ = _norm_str(dd.get("order_state"))
            bucket.append((fq, os_))
        if len(bucket) >= 3:
            c = Counter(bucket)
            if max(c.values()) >= 3:
                return "DUPLICATE_CALLBACK"
    unk_lines: List[Tuple[str, str]] = []
    for r in rows_ctx:
        if r["event"] != TARGET_EVENT:
            continue
        dd = _data(r["obj"])
        ro = _norm_str(dd.get("broker_order_id") or dd.get("order_id"))
        if ro != oid:
            continue
        fq = _norm_str(dd.get("fill_quantity") or dd.get("fill_qty"))
        os_ = _norm_str(dd.get("order_state"))
        unk_lines.append((fq, os_))
    if len(unk_lines) >= 3:
        c2 = Counter(unk_lines)
        if max(c2.values()) >= 3:
            return "DUPLICATE_CALLBACK"

    # B) MAPPING_LOST — order was registered / tracked earlier for same broker_order_id
    reg_events = (
        "ORDER_REGISTRY_REGISTERED",
        "ORDER_REGISTRY_FLATTEN_REGISTERED",
        "ORDER_REGISTRY_ADOPTED",
        "ORDER_REGISTRY_LIFECYCLE",
        "FLATTEN_ORDER_SUBMITTED",
        "ORDER_REGISTRY_EXEC_RESOLVED",
        "INTENT_FILL_UPDATE",
        "ORDER_ACKNOWLEDGED",
        "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
        "EXECUTION_FILLED",
        "EXECUTION_PARTIAL_FILL",
    )
    priors_extended = find_prior_rows(oid_index, rows, ts0, oid, lookback_sec) if oid else []
    for r in (*priors_oid, *priors_extended):
        if r["event"] in reg_events:
            dd = _data(r["obj"])
            ro = _norm_str(dd.get("broker_order_id") or dd.get("order_id"))
            if ro == oid or not oid:
                return "MAPPING_LOST"

    return "UNCLASSIFIED"


def fmt_timeline(ctx: List[Dict[str, Any]], limit: int = 18) -> str:
    lines: List[str] = []
    for r in ctx[:limit]:
        d = _data(r["obj"])
        oid = _norm_str(d.get("broker_order_id"))
        eid = _norm_str(d.get("execution_id") or d.get("broker_execution_id"))
        tag = _norm_str(d.get("tag"))[:72]
        lines.append(
            f"  {r['ts'].isoformat()} | {r['event'][:52]} | oid={oid} | eid={eid[:20]} | tag={tag}"
        )
    if len(ctx) > limit:
        lines.append(f"  ... [{len(ctx) - limit} more lines in window]")
    return "\n".join(lines)


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", default=None)
    ap.add_argument("--window-sec", type=float, default=5.0)
    ap.add_argument(
        "--lookback-sec",
        type=float,
        default=120.0,
        help="How far back in the merged log to search for registry/fill evidence (classification only).",
    )
    args = ap.parse_args()

    log_dir = resolve_log_dir(args.log_dir)
    if not log_dir.is_dir():
        print(f"Log dir not found: {log_dir}", file=sys.stderr)
        return 1

    rows = load_robot_jsonl(log_dir)
    ts_list = build_ts_list(rows)
    oid_index = build_oid_index(rows)
    unknowns = [r for r in rows if r["event"] == TARGET_EVENT]
    if not unknowns:
        print("No EXECUTION_UPDATE_UNKNOWN_ORDER events found.")
        return 0

    by_class: Dict[str, List[Dict[str, Any]]] = defaultdict(list)
    instruments: Dict[str, Counter] = defaultdict(Counter)
    examples: Dict[str, Dict[str, Any]] = {}

    delta = timedelta(seconds=args.window_sec)

    for u in unknowns:
        d = _data(u["obj"])
        oid = _norm_str(d.get("broker_order_id") or d.get("order_id"))
        eid = _norm_str(d.get("execution_id") or d.get("broker_execution_id"))
        ctx = context_window(rows, ts_list, u["ts"], oid, eid, delta)
        cat = classify(ctx, u, rows, args.lookback_sec, oid_index)
        by_class[cat].append({"row": u, "context": ctx})
        ins = u["instrument"] or _norm_str(d.get("instrument")) or "(empty)"
        instruments[cat][ins] += 1
        if cat not in examples:
            examples[cat] = {"sample": u, "context": ctx}

    print("=" * 72)
    print("EXECUTION_UPDATE_UNKNOWN_ORDER - log-derived classification")
    print(f"Log dir: {log_dir.resolve()}")
    print(f"Total UNKNOWN events: {len(unknowns)}")
    print("=" * 72)

    order = [
        "LATE_CALLBACK",
        "MAPPING_LOST",
        "EXTERNAL_ORDER",
        "DUPLICATE_CALLBACK",
        "TAG_DECODE_FAILURE",
        "UNCLASSIFIED",
    ]
    counts = {k: len(by_class.get(k, [])) for k in order}
    for cat in order:
        n = counts[cat]
        if n == 0:
            continue
        print()
        print(f"### {cat}")
        print(f"COUNT: {n}")
        inst_top = instruments[cat].most_common(12)
        print(f"INSTRUMENTS: {dict(inst_top)}")
        ex = examples.get(cat)
        if ex:
            print("EXAMPLE CASE (timeline, context window):")
            print(fmt_timeline(ex["context"], 20))

    print()
    print("=" * 72)
    print("FINAL SUMMARY")
    print("=" * 72)
    nonzero = [(k, v) for k, v in counts.items() if v > 0]
    nonzero.sort(key=lambda x: -x[1])
    if nonzero:
        primary = nonzero[0][0]
        secondary = [k for k, _ in nonzero[1:6]]
        print(f"1. Primary cause (highest count): {primary} ({nonzero[0][1]} events)")
        print(f"2. Secondary causes: {secondary}")
        tdf_n = counts.get("TAG_DECODE_FAILURE", 0)
        harmful = "performance issue"
        if primary in ("MAPPING_LOST", "TAG_DECODE_FAILURE", "UNCLASSIFIED", "LATE_CALLBACK"):
            harmful = "correctness bug (state/journal/IntentMap vs registry alignment; confirm per timeline)"
        if primary == "EXTERNAL_ORDER":
            harmful = "harmless (non-robot or untagged broker path) unless paired with unowned flatten storms"
        if primary == "DUPLICATE_CALLBACK":
            harmful = (
                "performance issue (duplicate NT callbacks; log evidence: "
                "three or more identical UNKNOWN lines in the configured time window)"
            )
            if tdf_n >= max(50, len(unknowns) // 10):
                harmful += (
                    f"; mixed severity: {tdf_n} TAG_DECODE_FAILURE events indicate "
                    "flatten/tag routing not in OrderMap (correctness), see that section"
                )
        print(f"3. Whether issue is: {harmful}")
    else:
        print("No events classified.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
