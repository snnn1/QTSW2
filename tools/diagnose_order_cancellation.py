#!/usr/bin/env python3
"""
Diagnose why the robot cancelled all orders.
Correlates push notifications, robot logs, and connection events.
"""
import json
import sys
from pathlib import Path
from datetime import datetime, timezone, timedelta
from collections import defaultdict

try:
    from zoneinfo import ZoneInfo
except ImportError:
    ZoneInfo = None

CHICAGO = "America/Chicago" if ZoneInfo else None
ROOT = Path(__file__).parent.parent
LOG_DIR = ROOT / "logs" / "robot"
WATCHDOG_LEDGER = ROOT / "data" / "watchdog" / "alert_ledger.jsonl"


def parse_ts(s: str):
    if not s:
        return None
    try:
        s = str(s).replace("Z", "+00:00")
        return datetime.fromisoformat(s)
    except Exception:
        return None


def load_jsonl(path: Path, since_hours: int = 48):
    events = []
    if not path.exists():
        return events
    cutoff = datetime.now(timezone.utc) - timedelta(hours=since_hours)
    try:
        with open(path, "r", encoding="utf-8-sig") as f:
            for line in f:
                line = line.strip()
                if not line:
                    continue
                try:
                    e = json.loads(line)
                    ts = parse_ts(e.get("ts_utc") or e.get("timestamp_utc") or e.get("timestamp"))
                    if ts and ts >= cutoff:
                        events.append(e)
                except json.JSONDecodeError:
                    pass
    except Exception as ex:
        print(f"  [WARN] Could not read {path}: {ex}")
    return events


def main():
    since_hours = int(sys.argv[1]) if len(sys.argv) > 1 else 24
    print("=" * 80)
    print("ORDER CANCELLATION DIAGNOSIS")
    print("=" * 80)
    print(f"Looking at last {since_hours} hours")
    print()

    # 1. Connection / fail-closed events (root cause of "cancel all")
    engine_events = []
    for p in LOG_DIR.glob("robot_ENGINE*.jsonl"):
        engine_events.extend(load_jsonl(p, since_hours))

    cancel_related = [
        "CONNECTION_LOST",
        "CONNECTION_LOST_SUSTAINED",
        "DISCONNECT_FAIL_CLOSED_ENTERED",
        "ROBOT_ORDERS_CANCELLED",
        "CANCEL_ROBOT_ORDERS_ERROR",
        "RECOVERY_CANCELLED_ROBOT_ORDERS",
        "CRITICAL_EVENT_REPORTED",
        "CRITICAL_NOTIFICATION_SKIPPED",
    ]

    connection_events = [e for e in engine_events if e.get("event") in cancel_related]
    connection_events.sort(key=lambda x: parse_ts(x.get("ts_utc") or x.get("timestamp_utc")) or datetime.min)

    print("1. CONNECTION / FAIL-CLOSED EVENTS (robot_ENGINE.jsonl)")
    print("-" * 80)
    if connection_events:
        for e in connection_events[-30:]:  # Last 30
            ts = parse_ts(e.get("ts_utc") or e.get("timestamp_utc"))
            ts_str = ts.strftime("%Y-%m-%d %H:%M:%S") if ts else "?"
            evt = e.get("event", "?")
            data = e.get("data", {})
            conn = data.get("connection_name", "")
            status = data.get("connection_status", "")
            note = data.get("note", "")[:60]
            print(f"  {ts_str} | {evt}")
            if conn or status:
                print(f"           connection={conn} status={status}")
            if note:
                print(f"           {note}")
    else:
        print("  No matching events found.")
    print()

    # 2. Push notifications (Watchdog alert ledger)
    print("2. WATCHDOG PUSH NOTIFICATIONS (alert_ledger.jsonl)")
    print("-" * 80)
    if WATCHDOG_LEDGER.exists():
        alerts = load_jsonl(WATCHDOG_LEDGER, since_hours)
        for a in alerts[-20:]:
            ts = parse_ts(a.get("timestamp") or a.get("raised_at"))
            ts_str = ts.strftime("%Y-%m-%d %H:%M:%S") if ts else "?"
            alert_type = a.get("alert_type", a.get("type", "?"))
            msg = (a.get("message") or a.get("msg", ""))[:70]
            print(f"  {ts_str} | {alert_type}")
            if msg:
                print(f"           {msg}")
    else:
        print(f"  File not found: {WATCHDOG_LEDGER}")
    print()

    # 3. Robot push notifications (PUSHOVER, CRITICAL_EVENT)
    push_events = [e for e in engine_events if "NOTIFICATION" in (e.get("event") or "") or "PUSHOVER" in (e.get("event") or "")]
    push_events.sort(key=lambda x: parse_ts(x.get("ts_utc")) or datetime.min)
    print("3. ROBOT PUSH NOTIFICATIONS (from robot_ENGINE)")
    print("-" * 80)
    if push_events:
        for e in push_events[-15:]:
            ts = parse_ts(e.get("ts_utc"))
            ts_str = ts.strftime("%Y-%m-%d %H:%M:%S") if ts else "?"
            print(f"  {ts_str} | {e.get('event')}")
            d = e.get("data", {})
            if d.get("event_type"):
                print(f"           event_type={d['event_type']}")
            if d.get("notification_sent"):
                print(f"           notification_sent={d['notification_sent']}")
    else:
        print("  No notification events found.")
    print()

    # 4. Summary / root cause
    print("4. ROOT CAUSE SUMMARY")
    print("-" * 80)
    disconnect_entered = [e for e in connection_events if e.get("event") == "DISCONNECT_FAIL_CLOSED_ENTERED"]
    if disconnect_entered:
        last = disconnect_entered[-1]
        ts = parse_ts(last.get("ts_utc"))
        data = last.get("data", {})
        conn = data.get("connection_name", "?")
        print(f"  Most recent DISCONNECT_FAIL_CLOSED_ENTERED: {ts.strftime('%Y-%m-%d %H:%M') if ts else '?'}")
        print(f"  Connection: {conn}")
        print()
        print("  CAUSE: Connection loss triggered fail-closed mode.")
        print("  When NinjaTrader loses connection (ConnectionLost), the robot enters DISCONNECT_FAIL_CLOSED.")
        print("  In this state:")
        print("    - All new orders are BLOCKED (RiskGate)")
        print("    - Working orders may be cancelled by NinjaTrader/broker when connection drops")
        print("    - On reconnect, recovery runs CancelRobotOwnedWorkingOrders")
        print()
        print("  Check: Network stability, NinjaTrader connection, broker connectivity.")
    else:
        print("  No DISCONNECT_FAIL_CLOSED_ENTERED in window.")
        print("  If orders were cancelled, check: slot expiry, manual flatten, protective failure.")
    print()
    print("  Log dir: ", LOG_DIR)
    print("  Run: python tools/log_audit.py --hours-back 24 --tz chicago")


if __name__ == "__main__":
    main()
