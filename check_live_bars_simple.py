#!/usr/bin/env python3
"""Simple check of what bars are being received right now."""

import json
from pathlib import Path
from datetime import datetime
import pytz

logs_dir = Path("logs/robot")
engine_log = logs_dir / "robot_ENGINE.jsonl"
CHICAGO_TZ = pytz.timezone("America/Chicago")

# Get current time
now_chicago = datetime.now(CHICAGO_TZ)
print(f"Current time (Chicago): {now_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")

# For trading date Jan 20, session window is:
# [Jan 19 17:00 CST, Jan 20 16:00 CST)
session_start = CHICAGO_TZ.localize(datetime(2026, 1, 19, 17, 0, 0))
session_end = CHICAGO_TZ.localize(datetime(2026, 1, 20, 16, 0, 0))

print(f"\nSession window for Jan 20:")
print(f"  Start: {session_start.strftime('%Y-%m-%d %H:%M:%S %Z')}")
print(f"  End:   {session_end.strftime('%Y-%m-%d %H:%M:%S %Z')}")

current_in_window = session_start <= now_chicago < session_end
print(f"\nCurrent time within session window: {current_in_window}")

if current_in_window:
    print("  [OK] Bars received NOW should be ACCEPTED")
else:
    if now_chicago < session_start:
        print("  [REJECTED] Before session start - bars will be REJECTED")
    else:
        print("  [REJECTED] After session end - bars will be REJECTED")
        print(f"    Session ended at 16:00 CST (about {(now_chicago - session_end).total_seconds()/60:.0f} minutes ago)")

# Check last events
print(f"\n{'='*60}")
print("Checking last events in log file...")

with open(engine_log, 'r', encoding='utf-8') as f:
    # Read last 50 lines
    lines = f.readlines()
    last_lines = lines[-50:] if len(lines) > 50 else lines
    
    bar_events = []
    for line in last_lines:
        line = line.strip()
        if not line:
            continue
        try:
            event = json.loads(line)
            event_type = event.get("event", "")
            if "BAR" in event_type:
                bar_events.append(event)
        except:
            pass

print(f"Found {len(bar_events)} bar-related events in last 50 log lines")

if bar_events:
    print("\nLast 10 bar events:")
    for i, event in enumerate(bar_events[-10:], 1):
        event_type = event.get("event", "")
        ts_utc_str = event.get("ts_utc", "")
        payload = event.get("data", {}).get("payload", {})
        instrument = payload.get("instrument", "UNKNOWN")
        
        if ts_utc_str:
            try:
                ts_utc = datetime.fromisoformat(ts_utc_str.replace("Z", "+00:00"))
                ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
                age_minutes = (now_chicago - ts_chicago.replace(tzinfo=CHICAGO_TZ)).total_seconds() / 60
                print(f"{i}. {event_type} - {instrument} ({age_minutes:.1f} minutes ago)")
            except:
                print(f"{i}. {event_type} - {instrument}")
else:
    print("\nNo recent bar events found - strategy may not be receiving bars right now")
