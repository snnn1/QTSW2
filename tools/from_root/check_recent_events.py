#!/usr/bin/env python3
"""Check recent event types"""
import json
from pathlib import Path
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")
if not log_file.exists():
    print("Log file not found")
    exit(1)

events = []
with open(log_file, 'r', encoding='utf-8-sig') as f:  # Handle BOM
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

recent = events[-200:] if len(events) > 200 else events

print("="*80)
print("RECENT EVENT TYPES (last 200 events)")
print("="*80)

event_types = defaultdict(int)
for e in recent:
    event_types[e.get('event', 'N/A')] += 1

for event_type, count in sorted(event_types.items(), key=lambda x: -x[1])[:20]:
    print(f"  {event_type}: {count}")

# Check specifically for stream-related events
print("\n" + "="*80)
print("STREAM-RELATED EVENTS")
print("="*80)

stream_events = [e for e in recent if any(x in e.get('event', '') for x in ['STREAM', 'ARMED', 'PRE_HYDRATION', 'TRANSITION', 'TICK_TRACE', 'RANGE_START'])]
print(f"Found {len(stream_events)} stream-related events")

if stream_events:
    for e in stream_events[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event = e.get('event', 'N/A')
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream', payload.get('stream_id', 'N/A'))
        print(f"  {ts} | {event} | Stream: {stream}")

print("\n" + "="*80)
