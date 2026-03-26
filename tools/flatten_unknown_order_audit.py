#!/usr/bin/env python3
"""
Audit EXECUTION_UPDATE_UNKNOWN_ORDER events for flatten orders from robot JSONL logs.

Diagnostic only (classify behavior from logs; no fix suggestions).

Usage:
  python tools/flatten_unknown_order_audit.py
  python tools/flatten_unknown_order_audit.py --log-dir c:/Users/jakej/QTSW2/logs/robot
"""
from __future__ import annotations

import argparse
import bisect
import json
import sys
from collections import Counter, defaultdict
from datetime import datetime, timedelta, timezone
from pathlib import Path
from typing import Any, Dict, List, Optional, Set, Tuple

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
QTSW2_FLATTEN = "QTSW2:FLATTEN:"

LIFECYCLE_EVENTS = frozenset(
    {
        "ORDER_REGISTRY_FLATTEN_REGISTERED",
        "FLATTEN_REQUESTED",
        "FLATTEN_SENT",
        "EXECUTION_UPDATE_UNKNOWN_ORDER",
        "EXECUTION_FILLED",
        "EXECUTION_PARTIAL_FILL",
        "EXECUTION_GHOST_FILL_DETECTED",
        "INTENT_FILL_UPDATE",
        "FLATTEN_VERIFY_FAIL",
        "FLATTEN_VERIFY_PASS",
        "ORDER_REGISTRY_EXEC_RESOLVED",
        "MANUAL_OR_EXTERNAL_ORDER_DETECTED",
        "ORDER_REGISTRY_LIFECYCLE",
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


def row_broker_oid(obj: Dict[str, Any]) -> str:
    d = _data(obj)
    return _norm_str(d.get("broker_order_id") or d.get("order_id"))


def flatten_correlation_tokens(tag: str, instrument: str) -> List[str]:
    """Substrings used to tie registry / flatten rows to a flatten UNKNOWN when broker_order_id differs."""
    if not tag:
        return [instrument] if instrument else []
    toks: List[str] = [tag]
    parts = tag.split(":")
    for p in parts:
        if len(p) >= 10 and any(c.isdigit() for c in p):
            toks.append(p)
    if len(parts) >= 5:
        toks.append(f"{parts[3]}:{parts[4]}")
    if instrument:
        toks.append(instrument)
    out: List[str] = []
    seen: Set[str] = set()
    for t in toks:
        if t and t not in seen:
            seen.add(t)
            out.append(t)
    return out


def row_matches_tokens(obj: Dict[str, Any], instrument: str, tokens: List[str], broker_oid: str) -> bool:
    ins = _norm_str(obj.get("instrument") or _data(obj).get("instrument"))
    if instrument and ins and ins != instrument:
        return False
    oid = row_broker_oid(obj)
    if broker_oid and oid == broker_oid:
        return True
    blob = json.dumps(obj, default=str)
    for t in tokens:
        if len(t) >= 6 and t in blob:
            return True
    return False


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


def is_flatten_unknown(obj: Dict[str, Any]) -> bool:
    if str(obj.get("event") or "") != TARGET:
        return False
    d = _data(obj)
    tag = _norm_str(d.get("tag"))
    intent_top = _norm_str(obj.get("intent_id"))
    intent_d = _norm_str(d.get("intent_id"))
    role = _norm_str(d.get("order_role")).upper()
    if tag.upper().startswith(QTSW2_FLATTEN.upper()):
        return True
    if intent_top.upper() == "FLATTEN" or intent_d.upper() == "FLATTEN":
        return True
    if role == "FLATTEN":
        return True
    return False


def fmt_line(r: Dict[str, Any], width: int = 52) -> str:
    d = _data(r["obj"])
    oid = row_broker_oid(r["obj"])[:24]
    tag = _norm_str(d.get("tag"))[:56]
    it = _norm_str(d.get("intent_id")) or _norm_str(r["obj"].get("intent_id"))
    return f"  {r['ts'].isoformat()} | {r['event'][:width]:{width}} | oid={oid} | intent={it} | tag={tag}"


def timeline_for_flatten_unknown(
    rows: List[Dict[str, Any]],
    ts_list: List[datetime],
    center_lo: datetime,
    center_hi: datetime,
    instrument: str,
    tokens: List[str],
    broker_oid: str,
    pad_sec: float,
) -> List[Dict[str, Any]]:
    lo = center_lo - timedelta(seconds=pad_sec)
    hi = center_hi + timedelta(seconds=pad_sec)
    i0 = bisect.bisect_left(ts_list, lo)
    i1 = bisect.bisect_right(ts_list, hi)
    out: List[Dict[str, Any]] = []
    seen: Set[Tuple[datetime, str, str]] = set()
    for j in range(i0, i1):
        r = rows[j]
        if r["event"] not in LIFECYCLE_EVENTS:
            continue
        if not row_matches_tokens(r["obj"], instrument, tokens, broker_oid):
            continue
        oid = row_broker_oid(r["obj"])
        k = (r["ts"], r["event"], oid)
        if k in seen:
            continue
        seen.add(k)
        out.append(r)
    # Registry rows often use a different broker_order_id than Sim UNKNOWN; tie by instrument + window.
    for j in range(i0, i1):
        r = rows[j]
        if r["event"] != "ORDER_REGISTRY_FLATTEN_REGISTERED":
            continue
        insr = r["instrument"] or _norm_str(_data(r["obj"]).get("instrument"))
        if instrument and insr != instrument:
            continue
        oid = row_broker_oid(r["obj"])
        k = (r["ts"], r["event"], oid)
        if k in seen:
            continue
        seen.add(k)
        out.append(r)
    out.sort(key=lambda x: x["ts"])
    return out


def classify_sequence(
    tl: List[Dict[str, Any]],
    instrument: str,
    *,
    global_after_unknown_fill: bool,
    global_after_unknown_resolved: bool,
) -> str:
    """Returns REGISTRY_DESYNC | TAG_ROUTING_FAILURE | LATE_RESOLUTION | CLEAN | UNCLASSIFIED."""

    events = [r["event"] for r in tl]
    has_reg = "ORDER_REGISTRY_FLATTEN_REGISTERED" in events
    unknow_rows = [r for r in tl if r["event"] == TARGET]
    has_unknown = bool(unknow_rows)
    fill_rows = [r for r in tl if r["event"] in ("EXECUTION_FILLED", "EXECUTION_PARTIAL_FILL")]
    has_fill = bool(fill_rows)
    has_ghost = "EXECUTION_GHOST_FILL_DETECTED" in events
    ifu_rows = [r for r in tl if r["event"] == "INTENT_FILL_UPDATE"]
    has_pass = "FLATTEN_VERIFY_PASS" in events
    has_fail = "FLATTEN_VERIFY_FAIL" in events

    def _min_ts(rs: List[Dict[str, Any]]) -> Optional[datetime]:
        return min((x["ts"] for x in rs), default=None)

    first_u = _min_ts(unknow_rows)
    first_f = _min_ts(fill_rows)

    if not has_unknown:
        return "CLEAN"

    # Strong log proxies for journal / verification breakdown (before overlap with desync/late).
    if has_ghost:
        return "TAG_ROUTING_FAILURE"
    if has_fill and not ifu_rows:
        return "TAG_ROUTING_FAILURE"
    if has_fail and not has_pass:
        return "TAG_ROUTING_FAILURE"

    if has_fill and first_u and first_f and first_u < first_f:
        return "LATE_RESOLUTION"
    if global_after_unknown_fill or global_after_unknown_resolved:
        return "LATE_RESOLUTION"

    if has_reg and has_unknown:
        return "REGISTRY_DESYNC"
    if has_unknown:
        return "REGISTRY_DESYNC"

    return "UNCLASSIFIED"


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("--log-dir", default=None)
    ap.add_argument("--window-sec", type=float, default=10.0)
    ap.add_argument(
        "--global-lookforward-sec",
        type=float,
        default=120.0,
        help="After last UNKNOWN in corpus for this (instrument,tag) group, search forward for fill / exec resolved.",
    )
    args = ap.parse_args()

    log_dir = resolve_log_dir(args.log_dir)
    if not log_dir.is_dir():
        print(f"Log dir not found: {log_dir}", file=sys.stderr)
        return 1

    rows = load_robot_jsonl(log_dir)
    ts_list = [r["ts"] for r in rows]

    flatten_unknown_rows = [r for r in rows if is_flatten_unknown(r["obj"])]
    n_flatten_unknown_events = len(flatten_unknown_rows)

    # Group: (instrument, tag) — same tag ties UNKNOWN bursts to one flatten leg
    groups: Dict[Tuple[str, str], List[Dict[str, Any]]] = defaultdict(list)
    for r in flatten_unknown_rows:
        d = _data(r["obj"])
        tag = _norm_str(d.get("tag"))
        ins = r["instrument"] or _norm_str(d.get("instrument"))
        groups[(ins, tag)].append(r)

    # Index rows by time for global lookforward
    def find_later_match(
        t_after: datetime,
        ins: str,
        tokens: List[str],
        broker_oid: str,
        events_ok: Set[str],
        until: datetime,
    ) -> bool:
        k = bisect.bisect_right(ts_list, t_after)
        for j in range(k, len(rows)):
            if rows[j]["ts"] > until:
                break
            rr = rows[j]
            if rr["event"] not in events_ok:
                continue
            if row_matches_tokens(rr["obj"], ins, tokens, broker_oid):
                return True
        return False

    by_class: Dict[str, List[Dict[str, Any]]] = defaultdict(list)
    class_instruments: Dict[str, Counter] = defaultdict(Counter)
    examples: Dict[str, Tuple[str, str, List[Dict[str, Any]]]] = {}

    registry_episodes: List[Dict[str, Any]] = []
    for r in rows:
        if r["event"] != "ORDER_REGISTRY_FLATTEN_REGISTERED":
            continue
        registry_episodes.append(r)

    # Per-group analysis (one row per logical flatten UNKNOWN episode)
    all_group_keys = list(groups.keys())
    late_count = 0
    late_resolve_any = 0
    never_resolve_groups = 0
    fail_initial_groups = 0
    total_groups = len(all_group_keys)

    for (ins, tag) in all_group_keys:
        gr = groups[(ins, tag)]
        ts_lo = min(x["ts"] for x in gr)
        ts_hi = max(x["ts"] for x in gr)
        broker_oid = row_broker_oid(gr[0]["obj"])
        tokens = flatten_correlation_tokens(tag, ins)

        last_u_ts = max(r["ts"] for r in gr)
        until = last_u_ts + timedelta(seconds=args.global_lookforward_sec)
        global_fill = find_later_match(
            last_u_ts,
            ins,
            tokens,
            broker_oid,
            {"EXECUTION_FILLED", "EXECUTION_PARTIAL_FILL"},
            until,
        )
        global_resolved = find_later_match(
            last_u_ts,
            ins,
            tokens,
            broker_oid,
            {"ORDER_REGISTRY_EXEC_RESOLVED"},
            until,
        )

        tl = timeline_for_flatten_unknown(
            rows, ts_list, ts_lo, ts_hi, ins, tokens, broker_oid, args.window_sec
        )
        cat = classify_sequence(
            tl,
            ins,
            global_after_unknown_fill=global_fill,
            global_after_unknown_resolved=global_resolved,
        )

        if cat == "LATE_RESOLUTION":
            late_count += 1

        fu = min((x["ts"] for x in tl if x["event"] == TARGET), default=None)
        ff = min(
            (x["ts"] for x in tl if x["event"] in ("EXECUTION_FILLED", "EXECUTION_PARTIAL_FILL")),
            default=None,
        )
        if fu is not None and ff is not None and fu < ff:
            late_resolve_any += 1
        elif fu is not None and (global_fill or global_resolved):
            late_resolve_any += 1

        ev_tl = [r["event"] for r in tl]
        has_fill_tl = any(x in ev_tl for x in ("EXECUTION_FILLED", "EXECUTION_PARTIAL_FILL"))
        resolved_any = (
            has_fill_tl
            or "ORDER_REGISTRY_EXEC_RESOLVED" in ev_tl
            or global_fill
            or global_resolved
        )
        if not resolved_any:
            never_resolve_groups += 1

        if fu is not None and (ff is None or fu < ff):
            fail_initial_groups += 1

        by_class[cat].append({"group": (ins, tag), "timeline": tl, "broker_oid": broker_oid})
        class_instruments[cat][ins] += 1
        if cat not in examples and tl:
            examples[cat] = (ins, tag, tl)

    # CLEAN: registry rows with no flatten UNKNOWN correlated in ±window
    def reg_matches_unknown(reg_row: Dict[str, Any]) -> bool:
        d = _data(reg_row["obj"])
        oid = row_broker_oid(reg_row["obj"])
        ins = reg_row["instrument"] or _norm_str(d.get("instrument"))
        t0 = reg_row["ts"]
        lo = t0 - timedelta(seconds=args.window_sec)
        hi = t0 + timedelta(seconds=args.window_sec)
        i0 = bisect.bisect_left(ts_list, lo)
        i1 = bisect.bisect_right(ts_list, hi)
        for j in range(i0, i1):
            rr = rows[j]
            if not is_flatten_unknown(rr["obj"]):
                continue
            d2 = _data(rr["obj"])
            o2 = row_broker_oid(rr["obj"])
            ins2 = rr["instrument"] or _norm_str(d2.get("instrument"))
            if o2 == oid or (ins and ins2 == ins):
                return True
        return False

    clean_groups = 0
    clean_example_tl: Optional[List[Dict[str, Any]]] = None
    for reg_row in registry_episodes:
        if not reg_matches_unknown(reg_row):
            clean_groups += 1
            if clean_example_tl is None:
                ins0 = reg_row["instrument"] or _norm_str(_data(reg_row["obj"]).get("instrument"))
                oid0 = row_broker_oid(reg_row["obj"])
                t0 = reg_row["ts"]
                lo = t0 - timedelta(seconds=args.window_sec)
                hi = t0 + timedelta(seconds=args.window_sec)
                i0 = bisect.bisect_left(ts_list, lo)
                i1 = bisect.bisect_right(ts_list, hi)
                clean_example_tl = []
                for j in range(i0, i1):
                    rr = rows[j]
                    if rr["event"] not in LIFECYCLE_EVENTS:
                        continue
                    ro = row_broker_oid(rr["obj"])
                    insr = rr["instrument"] or _norm_str(_data(rr["obj"]).get("instrument"))
                    if ro == oid0 or (ins0 and insr == ins0):
                        clean_example_tl.append(rr)
                clean_example_tl.sort(key=lambda x: x["ts"])

    reg_unknown_adjacent = len(registry_episodes) - clean_groups
    pct_reg_cov = (100.0 * reg_unknown_adjacent / len(registry_episodes)) if registry_episodes else 0.0

    print("=" * 78)
    print("FLATTEN EXECUTION_UPDATE_UNKNOWN_ORDER audit (log-derived)")
    print(f"Log dir: {log_dir.resolve()}")
    print(
        "Definitions: timelines merge lifecycle rows matching instrument + latch/tag tokens OR broker_order_id "
        f"within first/last UNKNOWN +/- {args.window_sec}s.\n"
        "TAG_ROUTING_FAILURE: ghost fill, verify fail without pass in window, or fill with no INTENT_FILL_UPDATE in timeline.\n"
        "REGISTRY_DESYNC: flatten registry present or UNKNOWN-only path; mapping miss (UNKNOWN) without routing signals above.\n"
        "LATE_RESOLUTION: UNKNOWN before first fill/exec-resolved in window, OR fill/resolved within global lookforward after last UNKNOWN.\n"
    )
    print(f"Flatten-related UNKNOWN event lines (step 1): {n_flatten_unknown_events}")
    print(f"Distinct flatten UNKNOWN groups (instrument, tag): {total_groups}")
    print(f"ORDER_REGISTRY_FLATTEN_REGISTERED rows: {len(registry_episodes)}")
    print(
        "Registry rows with flatten UNKNOWN in +/- window (same oid or instrument): "
        f"{reg_unknown_adjacent} ({pct_reg_cov:.1f}%)"
    )
    print("=" * 78)

    order = [
        "REGISTRY_DESYNC",
        "TAG_ROUTING_FAILURE",
        "LATE_RESOLUTION",
        "CLEAN",
        "UNCLASSIFIED",
    ]
    for cat in order:
        items = by_class.get(cat, [])
        if cat == "CLEAN":
            continue
        if not items:
            continue
        print()
        print(f"### {cat}")
        print(f"COUNT: {len(items)}")
        print(f"INSTRUMENTS: {dict(class_instruments[cat].most_common(16))}")
        ex = examples.get(cat)
        if ex:
            ins, tag, tl = ex
            print(f"EXAMPLE (instrument={ins}, tag={tag[:80]})")
            for line in [fmt_line(rr) for rr in tl[:20]]:
                print(line)
            if len(tl) > 20:
                print(f"  ... [{len(tl) - 20} more]")

    print()
    print("### CLEAN (flatten registrations with no correlated UNKNOWN in registry +/- window)")
    print(f"COUNT: {clean_groups} registry episodes without nearby flatten UNKNOWN")
    print("(Episodes = each ORDER_REGISTRY_FLATTEN_REGISTERED row checked vs +/- window UNKNOWN correlation.)")
    if clean_example_tl:
        print("EXAMPLE TIMELINE (registry episode with no nearby flatten UNKNOWN; instrument/oid-scoped +/- window):")
        for line in [fmt_line(rr) for rr in clean_example_tl[:20]]:
            print(line)
        if len(clean_example_tl) > 20:
            print(f"  ... [{len(clean_example_tl) - 20} more]")

    print()
    print("=" * 78)
    print("FINAL SUMMARY (group-scoped = distinct instrument+tag UNKNOWN clusters)")
    print("=" * 78)
    pct = lambda a, b: (100.0 * a / b) if b else 0.0
    print(
        f"1. Fail initial mapping (UNKNOWN before fill in +/-window, or UNKNOWN with no fill in window): "
        f"{pct(fail_initial_groups, total_groups):.1f}% ({fail_initial_groups}/{total_groups})"
    )
    print(
        f"2. Never resolve in lookforward ({args.global_lookforward_sec}s after last UNKNOWN: no fill, no EXEC_RESOLVED): "
        f"{pct(never_resolve_groups, total_groups):.1f}% ({never_resolve_groups}/{total_groups})"
    )
    print(
        f"3. Resolve late (UNKNOWN timestamp before first fill, or fill/exec-resolved after last UNKNOWN via lookforward): "
        f"{pct(late_resolve_any, total_groups):.1f}% ({late_resolve_any}/{total_groups}); "
        f"subset classified LATE_RESOLUTION only: {pct(late_count, total_groups):.1f}% ({late_count}/{total_groups})"
    )
    rd = len(by_class.get("REGISTRY_DESYNC", []))
    tr = len(by_class.get("TAG_ROUTING_FAILURE", []))
    prim = "REGISTRY_DESYNC" if rd >= tr else "TAG_ROUTING_FAILURE"
    print(f"4. Primary failure type (higher group count): {prim} (REGISTRY_DESYNC={rd}, TAG_ROUTING_FAILURE={tr})")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())
