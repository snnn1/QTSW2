#!/usr/bin/env python3
"""
Diagnose Real ORDER_STUCK Orders (post ORDER_REJECTED fix)

Analyzes last N ORDER_STUCK_DETECTED candidates to determine why orders
are not resolving. Classifies each as A/B/C/D/E and produces root cause distribution.

Usage:
  python tools/diagnose_real_order_stuck.py [--limit 50] [--feed path]
"""
import argparse
import json
import sys
from collections import defaultdict
from datetime import datetime, timezone
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from modules.watchdog.config import (
    FRONTEND_FEED_FILE,
    ORDER_STUCK_ENTRY_THRESHOLD_SECONDS,
    ORDER_STUCK_PROTECTIVE_THRESHOLD_SECONDS,
)


def parse_ts(ev: dict) -> datetime | None:
    raw = ev.get("timestamp_utc") or ev.get("ts_utc") or ev.get("timestamp")
    if not raw:
        return None
    try:
        dt = datetime.fromisoformat(str(raw).replace("Z", "+00:00"))
        return dt.replace(tzinfo=timezone.utc) if dt.tzinfo is None else dt
    except Exception:
        return None


def extract_broker_order_id(ev: dict) -> str | None:
    data = ev.get("data") or {}
    return (
        data.get("broker_order_id")
        or data.get("order_id")
        or ev.get("broker_order_id")
        or ev.get("order_id")
    )


def load_events_tail(feed_path: Path, max_lines: int = 100000) -> list[dict]:
    """Read events from end of file (most recent first in memory, then reverse for chronological)."""
    if not feed_path.exists():
        return []
    events = []
    with open(feed_path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                continue
            if len(events) > max_lines:
                events = events[-max_lines:]
    return events


def simulate_and_collect(
    events: list[dict],
) -> tuple[list[dict], dict[str, list], dict[str, str], dict[str, int], dict[str, set], dict[str, list]]:
    """
    Simulate state_manager, collect stuck candidates, and build context:
    - order_events: broker_order_id -> list of events
    - stream_state: stream_key -> last state (from STREAM_STATE_TRANSITION)
    - position: instrument -> net qty
    - intent_closed: intent_id -> set of "closed" timestamps
    - oco_groups: oco_group -> list of broker_order_ids
    """
    pending: dict[str, dict] = {}
    stuck_candidates: list[dict] = []
    order_events: dict[str, list[dict]] = defaultdict(list)
    stream_state: dict[str, str] = {}
    position: dict[str, int] = defaultdict(int)
    intent_closed: dict[str, set[str]] = defaultdict(set)
    oco_groups: dict[str, list[str]] = defaultdict(list)

    for ev in events:
        et = ev.get("event_type") or ev.get("event") or ev.get("@event", "")
        data = ev.get("data") or {}
        ts = parse_ts(ev)
        if not ts:
            continue

        oid = extract_broker_order_id(ev)
        if oid:
            order_events[oid].append({**ev, "_parsed_ts": ts})

        if et == "ORDER_SUBMIT_SUCCESS":
            if oid:
                order_type = str(data.get("order_type", "entry") or "entry").upper()
                if "PROTECTIVE" in order_type and "TARGET" in order_type:
                    role = "target"
                elif "PROTECTIVE" in order_type and "STOP" in order_type:
                    role = "stop"
                else:
                    role = "entry"
                pending[oid] = {
                    "submitted_at": ts,
                    "intent_id": data.get("intent_id", "") or ev.get("intent_id", ""),
                    "instrument": data.get("instrument", "") or ev.get("instrument", ""),
                    "role": role,
                    "stream_key": data.get("stream_key", "") or data.get("stream", "") or ev.get("stream_id", ""),
                    "oco_group": data.get("oco_group", ""),
                    "direction": data.get("direction", ""),
                }
                oco = data.get("oco_group", "")
                if oco:
                    oco_groups[oco].append(oid)

        elif et == "EXECUTION_FILLED":
            if oid:
                pending.pop(oid, None)
            inst = data.get("instrument", "") or ev.get("instrument", "")
            qty = data.get("quantity") or data.get("qty") or data.get("fill_quantity")
            direction = (data.get("direction", "") or "").lower()
            if inst and qty is not None:
                try:
                    q = int(qty)
                    if "short" in direction or "sell" in direction:
                        position[inst] -= q
                    else:
                        position[inst] += q
                except (ValueError, TypeError):
                    pass

        elif et == "ORDER_CANCELLED":
            if oid:
                pending.pop(oid, None)

        elif et == "ORDER_REJECTED":
            if oid:
                pending.pop(oid, None)

        elif et == "STREAM_STATE_TRANSITION":
            stream = data.get("stream_id", "") or data.get("stream", "") or ev.get("stream_id", "")
            new_state = data.get("new_state", "")
            if stream and new_state:
                stream_state[stream] = new_state

        elif et == "INTENT_EXPOSURE_CLOSED":
            iid = data.get("intent_id", "") or ev.get("intent_id", "")
            if iid:
                intent_closed[iid].add(ts.isoformat())

        # Check stuck
        for bid, info in list(pending.items()):
            sub_at = info.get("submitted_at")
            if not sub_at:
                continue
            role = info.get("role", "entry")
            thr = (
                ORDER_STUCK_PROTECTIVE_THRESHOLD_SECONDS
                if role in ("stop", "target")
                else ORDER_STUCK_ENTRY_THRESHOLD_SECONDS
            )
            working_sec = (ts - sub_at).total_seconds()
            if working_sec >= thr:
                stuck_candidates.append({
                    "broker_order_id": bid,
                    "intent_id": info.get("intent_id", ""),
                    "instrument": info.get("instrument", ""),
                    "role": role,
                    "stream_key": info.get("stream_key", ""),
                    "oco_group": info.get("oco_group", ""),
                    "direction": info.get("direction", ""),
                    "working_duration_seconds": int(working_sec),
                    "threshold_seconds": thr,
                    "submitted_at": sub_at.isoformat(),
                    "detected_at": ts.isoformat(),
                })
                pending.pop(bid, None)

    return stuck_candidates, dict(order_events), stream_state, dict(position), dict(intent_closed), dict(oco_groups)


def classify_stuck_order(
    c: dict,
    order_events: dict[str, list],
    position: dict[str, int],
    intent_closed: dict[str, set],
    oco_groups: dict[str, list],
) -> str:
    """
    Classify into A/B/C/D/E:
    A - Legitimately waiting (valid, price not reached)
    B - Should have been cancelled but wasn't (OCO sibling filled, position flat, etc.)
    C - Should have been replaced but wasn't (recovery/reconciliation)
    D - Broker state mismatch
    E - Missing lifecycle event (fill/cancel on broker but no event)
    """
    oid = c["broker_order_id"]
    inst = c.get("instrument", "")
    role = c.get("role", "entry")
    intent_id = c.get("intent_id", "")
    oco_group = c.get("oco_group", "")

    evs = order_events.get(oid, [])
    submit_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_SUBMIT_SUCCESS"), None)
    fill_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "EXECUTION_FILLED"), None)
    cancel_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_CANCELLED"), None)
    reject_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_REJECTED"), None)

    if fill_ev or cancel_ev or reject_ev:
        # Event exists but order still in stuck: cancel/fill arrived after threshold (feed ordering)
        # or event format mismatch prevented pop
        return "E"

    # B: OCO sibling filled - this order should have been cancelled by broker
    if oco_group:
        siblings = [x for x in oco_groups.get(oco_group, []) if x != oid]
        for s in siblings:
            sevs = order_events.get(s, [])
            sf = next((e for e in sevs if (e.get("event_type") or e.get("event") or e.get("@event")) == "EXECUTION_FILLED"), None)
            if sf:
                return "B"  # OCO sibling filled, this should have been cancelled

    # B: Position flat but entry/protective still exists
    pos = position.get(inst, 0)
    if pos == 0:
        if role == "entry":
            return "B"  # Position flat, entry order should not exist
        if role in ("stop", "target"):
            return "B"  # Position flat, protective should have been cancelled

    # B: Intent closed but protective still exists
    if role in ("stop", "target") and intent_id and intent_id in intent_closed:
        return "B"  # Intent closed, protective should have been cancelled

    # C: Hard to detect from feed - would need reconciliation/resubmit events
    # D: Hard to detect from feed - broker state not visible
    # Default to A: legitimately waiting (price not reached, no cancel expected)
    return "A"


def run_diagnosis(feed_path: Path, limit: int = 50) -> dict:
    events = load_events_tail(feed_path)
    if not events:
        return {"error": f"No events in {feed_path}", "feed_path": str(feed_path)}

    stuck, order_events, stream_state, position, intent_closed, oco_groups = simulate_and_collect(events)
    candidates = stuck[-limit:]

    lifecycles = []
    classifications: dict[str, int] = defaultdict(int)

    for c in candidates:
        oid = c["broker_order_id"]
        evs = order_events.get(oid, [])
        submit_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_SUBMIT_SUCCESS"), None)
        fill_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "EXECUTION_FILLED"), None)
        cancel_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_CANCELLED"), None)
        reject_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_REJECTED"), None)

        cls = classify_stuck_order(c, order_events, position, intent_closed, oco_groups)
        classifications[cls] += 1

        lifecycles.append({
            "broker_order_id": oid,
            "instrument": c.get("instrument", ""),
            "stream": c.get("stream_key", ""),
            "role": c.get("role", ""),
            "intent_id": c.get("intent_id", ""),
            "submitted_at": c.get("submitted_at", ""),
            "working_duration_seconds": c.get("working_duration_seconds"),
            "has_submit": submit_ev is not None,
            "has_fill": fill_ev is not None,
            "has_cancel": cancel_ev is not None,
            "has_rejection": reject_ev is not None,
            "classification": cls,
            "position_at_detection": position.get(c.get("instrument", ""), 0),
        })

    n = len(candidates)
    dist = {k: round(100 * v / n, 1) for k, v in classifications.items()} if n else {}

    # Patterns
    by_instrument: dict[str, int] = defaultdict(int)
    by_role: dict[str, int] = defaultdict(int)
    working_times = [r["working_duration_seconds"] for r in lifecycles if r.get("working_duration_seconds") is not None]
    for r in lifecycles:
        by_instrument[r.get("instrument", "")] += 1
        by_role[r.get("role", "")] += 1

    avg_working = sum(working_times) / len(working_times) if working_times else 0
    max_working = max(working_times) if working_times else 0
    extreme_count = sum(1 for w in working_times if w >= 3000)

    # Top failure mechanisms (for B and E)
    b_orders = [r for r in lifecycles if r["classification"] == "B"]
    e_orders = [r for r in lifecycles if r["classification"] == "E"]
    failure_mechanisms = []
    if b_orders:
        failure_mechanisms.append({
            "mechanism": "Position flat but entry orders still working",
            "count": len(b_orders),
            "code_ref": "StreamStateMachine slot expiry / forced flatten should cancel entry orders",
            "instruments": list({r["instrument"] for r in b_orders}),
        })
    if e_orders:
        failure_mechanisms.append({
            "mechanism": "ORDER_CANCELLED arrived after 120s threshold (feed ordering)",
            "count": len(e_orders),
            "code_ref": "event_processor processes events in feed order; multi-file merge can delay cancel",
            "broker_order_ids": [r["broker_order_id"] for r in e_orders],
        })

    return {
        "feed_path": str(feed_path),
        "total_events": len(events),
        "stuck_count": len(stuck),
        "audited_count": n,
        "lifecycles": lifecycles,
        "root_cause_distribution": dist,
        "patterns": {
            "by_instrument": dict(by_instrument),
            "by_role": dict(by_role),
            "avg_working_seconds": round(avg_working, 1),
            "max_working_seconds": max_working,
            "extreme_3000s_plus": extreme_count,
        },
        "stream_state_snapshot": stream_state,
        "position_snapshot": position,
        "failure_mechanisms": failure_mechanisms,
        "verdict": "expected_behavior" if dist.get("A", 0) >= 50 else "mixed",
        "recommendations": _get_recommendations(dist, b_orders, e_orders),
    }


def _get_recommendations(dist: dict, b_orders: list, e_orders: list) -> list[str]:
    recs = []
    if dist.get("B", 0) > 0:
        recs.append("B: Investigate slot expiry / forced flatten - entry orders should be cancelled when position flattens or slot ends")
    if dist.get("E", 0) > 0:
        recs.append("E: Ensure event feed merge preserves chronological order; or extend ORDER_STUCK threshold for protective orders")
    if dist.get("A", 0) >= 70:
        recs.append("A: Majority legitimately waiting - consider raising threshold or suppressing during known quiet periods")
    return recs


def main():
    ap = argparse.ArgumentParser(description="Diagnose real ORDER_STUCK orders")
    ap.add_argument("--limit", type=int, default=50, help="Max stuck candidates to audit")
    ap.add_argument("--feed", type=Path, default=FRONTEND_FEED_FILE, help="Feed file path")
    args = ap.parse_args()

    feed = args.feed
    if not feed.is_absolute():
        feed = ROOT / feed

    out = run_diagnosis(feed, limit=args.limit)
    print(json.dumps(out, indent=2, default=str))


if __name__ == "__main__":
    main()
