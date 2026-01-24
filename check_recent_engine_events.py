"""Check recent ENGINE events from feed"""
import json
from pathlib import Path

feed_file = Path('logs/robot/frontend_feed.jsonl')
if not feed_file.exists():
    print("Feed file not found")
    exit(1)

events = []
with open(feed_file, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                continue

# Get last 100 events
recent = events[-100:] if len(events) > 100 else events

# Filter ENGINE events
engine_events = [e for e in recent if 'ENGINE' in e.get('event_type', '')]

print(f"Found {len(engine_events)} ENGINE events in last {len(recent)} events")
print("\nLast 15 ENGINE events:")
for e in engine_events[-15:]:
    ts = e.get('timestamp_chicago', e.get('timestamp_utc', ''))[:19]
    et = e.get('event_type', '')
    print(f"  {ts} {et}")
