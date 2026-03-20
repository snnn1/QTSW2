#!/usr/bin/env python3
"""
ORDER_STUCK_DETECTED Loop Diagnostic

Audits the last N ORDER_STUCK_DETECTED candidates by:
1. Simulating state_manager._pending_orders from feed events
2. Extracting stuck-order candidates (would have fired ORDER_STUCK_DETECTED)
3. Tracing ORDER_SUBMIT_SUCCESS, fill, cancel, rejection for each
4. Classifying submission triggers from event data
5. Checking ORDER_REJECTED handling (watchdog must remove from pending)

Usage:
  python tools/diagnose_order_stuck_loop.py [--limit 20] [--feed path]
"""
import argparse
import json
import sys
from collections import defaultdict
from datetime import datetime, timezone, timedelta
from pathlib import Path

# Add project root for imports
ROOT = Path(__file__).resolve().parents[1]
sys.path.insert(0, str(ROOT))

from modules.watchdog.config import (
    ROBOT_LOGS_DIR,
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


def load_events(feed_path: Path, max_events: int = 50000) -> list[dict]:
    events = []
    if not feed_path.exists():
        return events
    with open(feed_path, "r", encoding="utf-8", errors="replace") as f:
        for line in f:
            line = line.strip()
            if not line:
                continue
            try:
                events.append(json.loads(line))
            except json.JSONDecodeError:
                continue
            if len(events) >= max_events:
                break
    return events


def simulate_pending_and_stuck(
    events: list[dict],
    handle_rejected: bool = True,
) -> tuple[list[dict], list[dict]]:
    """
    Simulate state_manager: track _pending_orders, detect stuck candidates.
    Returns (stuck_candidates, all_order_events_by_id).
    """
    pending: dict[str, dict] = {}
    stuck_candidates: list[dict] = []
    order_events: dict[str, list[dict]] = defaultdict(list)

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
                }

        elif et == "EXECUTION_FILLED":
            if oid:
                pending.pop(oid, None)

        elif et == "ORDER_CANCELLED":
            if oid:
                pending.pop(oid, None)

        elif et == "ORDER_REJECTED" and handle_rejected:
            if oid:
                pending.pop(oid, None)

        # Check stuck (same logic as state_manager.check_stuck_orders)
        threshold = ORDER_STUCK_ENTRY_THRESHOLD_SECONDS
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
                    "working_duration_seconds": int(working_sec),
                    "threshold_seconds": thr,
                    "submitted_at": sub_at.isoformat(),
                    "detected_at": ts.isoformat(),
                })
                pending.pop(bid, None)

    return stuck_candidates, dict(order_events)


def classify_submission_trigger(ev: dict) -> str:
    data = ev.get("data") or {}
    reason = (data.get("reason") or data.get("note") or "").lower()
    if "recovery" in reason or "resubmit" in reason:
        return "recovery_resubmit"
    if "restart" in reason or "retry" in reason:
        return "restart_retry"
    if "reconcil" in reason:
        return "reconciliation"
    if "initial" in reason or "slot" in reason:
        return "initial_submission"
    return "unknown"


def run_diagnosis(
    feed_path: Path,
    limit: int = 20,
) -> dict:
    events = load_events(feed_path)
    if not events:
        return {"error": f"No events in {feed_path}", "feed_path": str(feed_path)}

    # With ORDER_REJECTED handling (current fix)
    stuck_with_fix, order_events = simulate_pending_and_stuck(events, handle_rejected=True)
    # Without (bug)
    stuck_without_fix, _ = simulate_pending_and_stuck(events, handle_rejected=False)

    # Take last N stuck candidates (from simulation without fix to see what would have fired)
    candidates = stuck_without_fix[-limit:]

    results = []
    for c in candidates:
        oid = c["broker_order_id"]
        evs = order_events.get(oid, [])
        submit_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_SUBMIT_SUCCESS"), None)
        fill_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "EXECUTION_FILLED"), None)
        cancel_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_CANCELLED"), None)
        reject_ev = next((e for e in evs if (e.get("event_type") or e.get("event") or e.get("@event")) == "ORDER_REJECTED"), None)

        trigger = classify_submission_trigger(submit_ev) if submit_ev else "no_submit_event"

        results.append({
            "broker_order_id": oid,
            "intent_id": c.get("intent_id", ""),
            "instrument": c.get("instrument", ""),
            "role": c.get("role", ""),
            "stream_key": c.get("stream_key", ""),
            "working_duration_seconds": c.get("working_duration_seconds"),
            "has_submit": submit_ev is not None,
            "has_fill": fill_ev is not None,
            "has_cancel": cancel_ev is not None,
            "has_rejection": reject_ev is not None,
            "submission_trigger": trigger,
            "would_still_stuck_with_fix": any(s["broker_order_id"] == oid for s in stuck_with_fix),
        })

    unique_ids = len({r["broker_order_id"] for r in results})
    rejection_count = sum(1 for r in results if r["has_rejection"])
    recovery_trigger_count = sum(1 for r in results if r["submission_trigger"] == "recovery_resubmit")
    recon_trigger_count = sum(1 for r in results if r["submission_trigger"] == "reconciliation")

    # Root cause classification
    root_causes = []
    if rejection_count > 0:
        root_causes.append("B) rejection handling failure (ORDER_REJECTED now removes from pending - fix applied)")
    if recovery_trigger_count >= len(results) * 0.5:
        root_causes.append("D) recovery loop (most submissions from recovery resubmit)")
    if recon_trigger_count >= len(results) * 0.5:
        root_causes.append("C) reconciliation loop (most submissions from reconciliation)")
    if unique_ids < len(results) * 0.5:
        root_causes.append("E) combination (repeated broker_order_ids suggest state mismatch or replay)")

    return {
        "feed_path": str(feed_path),
        "total_events": len(events),
        "stuck_candidates_without_fix": len(stuck_without_fix),
        "stuck_candidates_with_fix": len(stuck_with_fix),
        "rejection_handling_bug": rejection_count > 0 and len(stuck_with_fix) < len(stuck_without_fix),
        "last_n_audit": results,
        "summary": {
            "unique_broker_order_ids": unique_ids,
            "with_rejection": rejection_count,
            "with_recovery_trigger": recovery_trigger_count,
            "with_reconciliation_trigger": recon_trigger_count,
        },
        "root_cause_classification": root_causes if root_causes else ["A) broker state mismatch or E) combination - inspect logs"],
    }


def main():
    ap = argparse.ArgumentParser(description="Diagnose ORDER_STUCK_DETECTED loop")
    ap.add_argument("--limit", type=int, default=20, help="Max stuck candidates to audit")
    ap.add_argument("--feed", type=Path, default=FRONTEND_FEED_FILE, help="Feed file path")
    args = ap.parse_args()

    feed = args.feed
    if not feed.is_absolute():
        feed = ROOT / feed

    out = run_diagnosis(feed, limit=args.limit)
    print(json.dumps(out, indent=2, default=str))


if __name__ == "__main__":
    main()
