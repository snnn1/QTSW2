#!/usr/bin/env python3
"""Check what caused strategy shutdown today."""
import json
from pathlib import Path
from datetime import datetime, timezone
from collections import defaultdict

today = datetime.now(timezone.utc).strftime("%Y-%m-%d")
log_dir = Path(__file__).parent.parent / "logs" / "robot"

# Actual shutdown triggers (exclude RECONCILIATION_PASS which is routine)
shutdown_keywords = [
    "ENGINE_STOP", "ENGINE_STAND_DOWN", "DISCONNECT_FAIL_CLOSED", "CONNECTION_LOST_SUSTAINED",
    "STRATEGY_DISABLED", "STREAM_STAND_DOWN", "PRICE_CONNECTION_LOSS_REPEATED",
    "EXECUTION_GATE_INVARIANT", "KILL_SWITCH", "RECONCILIATION_QTY_MISMATCH",
    "lost price connection", "more than 4 times",
]

events = []
for p in log_dir.glob("robot_*.jsonl"):
    try:
        with open(p, "r", encoding="utf-8-sig") as f:
            for line in f:
                if not line.strip():
                    continue
                try:
                    e = json.loads(line)
                    ts = e.get("ts_utc", "") or e.get("timestamp_utc", "")
                    if ts.startswith(today):
                        events.append(e)
                except Exception:
                    pass
    except Exception:
        pass

events.sort(key=lambda x: x.get("ts_utc", "") or x.get("timestamp_utc", ""))

shut = [
    e
    for e in events
    if any(kw in (e.get("event", "") or "").upper() for kw in [s.upper() for s in shutdown_keywords])
]

print("=" * 90)
print(f"STRATEGY SHUTDOWN ANALYSIS - {today}")
print("=" * 90)
print(f"Total events today: {len(events)}")
print(f"Shutdown/critical events: {len(shut)}")
print()

if shut:
    print("Timeline of shutdown-related events:")
    for e in shut[:60]:
        ts = (e.get("ts_utc") or e.get("timestamp_utc") or "")[:19]
        ev = e.get("event", "N/A")
        inst = e.get("instrument", e.get("execution_instrument", ""))
        data = e.get("data", {})
        reason = data.get("reason", data.get("message", ""))
        if isinstance(reason, dict):
            reason = str(reason.get("payload", reason))[:60]
        else:
            reason = str(reason)[:60] if reason else ""
        print(f"  {ts} | {ev:45} | {inst:8} | {reason}")
else:
    print("No shutdown events found. Checking ERROR level and CONNECTION events...")
    errs = [e for e in events if e.get("level") == "ERROR"]
    conn = [e for e in events if "CONNECTION" in (e.get("event") or "").upper()]
    print(f"  ERROR events: {len(errs)}")
    for e in errs[:15]:
        ts = (e.get("ts_utc") or "")[:19]
        print(f"    {ts} | {e.get('event', 'N/A')}")
    print(f"  CONNECTION events: {len(conn)}")
    for e in conn[:15]:
        ts = (e.get("ts_utc") or "")[:19]
        print(f"    {ts} | {e.get('event', 'N/A')}")
