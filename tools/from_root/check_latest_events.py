"""Check latest events in feed"""
import json
from pathlib import Path
from datetime import datetime, timezone
import pytz

CHICAGO_TZ = pytz.timezone("America/Chicago")
feed_file = Path('logs/robot/frontend_feed.jsonl')

if not feed_file.exists():
    print("Feed file not found")
    exit(1)

with open(feed_file, 'r', encoding='utf-8') as f:
    lines = f.readlines()

print("=" * 80)
print("LATEST EVENTS IN FEED")
print("=" * 80)

# Get last 50 events
recent_lines = lines[-50:] if len(lines) > 50 else lines
events = []
for line in recent_lines:
    if line.strip():
        try:
            events.append(json.loads(line.strip()))
        except:
            continue

print(f"\n[INFO] Checking last {len(events)} events\n")

# Show all events
for i, event in enumerate(events):
    ts_utc_str = event.get('timestamp_utc', '')
    try:
        ts_utc = datetime.fromisoformat(ts_utc_str.replace('Z', '+00:00'))
        if ts_utc.tzinfo is None:
            ts_utc = ts_utc.replace(tzinfo=timezone.utc)
        ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
        now = datetime.now(timezone.utc)
        elapsed = (now - ts_utc).total_seconds()
    except:
        ts_chicago = ts_utc_str[:19]
        elapsed = 0
    
    event_type = event.get('event_type', '')
    print(f"{i+1:2d}. {ts_chicago.strftime('%H:%M:%S')} ({elapsed:5.1f}s ago) {event_type}")

# Check specifically for heartbeats
heartbeats = [e for e in events if e.get('event_type') == 'ENGINE_TICK_HEARTBEAT']
print(f"\n[INFO] Heartbeats in last {len(events)} events: {len(heartbeats)}")

if heartbeats:
    print("\n  Heartbeat events found:")
    for hb in heartbeats:
        ts_utc_str = hb.get('timestamp_utc', '')
        try:
            ts_utc = datetime.fromisoformat(ts_utc_str.replace('Z', '+00:00'))
            if ts_utc.tzinfo is None:
                ts_utc = ts_utc.replace(tzinfo=timezone.utc)
            ts_chicago = ts_utc.astimezone(CHICAGO_TZ)
            print(f"    {ts_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")
        except:
            print(f"    {ts_utc_str[:19]}")

print("\n" + "=" * 80)
