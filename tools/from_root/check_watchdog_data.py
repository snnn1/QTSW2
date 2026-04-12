#!/usr/bin/env python3
"""Check watchdog feed data for slot_time_chicago and range data"""
import json
from pathlib import Path

feed_file = Path("logs/robot/frontend_feed.jsonl")
if not feed_file.exists():
    print(f"Feed file not found: {feed_file}")
    exit(1)

events = []
with open(feed_file, 'r', encoding='utf-8') as f:
    for line in f.readlines()[-1000:]:
        if line.strip():
            try:
                events.append(json.loads(line))
            except:
                pass

print(f"Total events checked: {len(events)}")

# Check RANGE_LOCKED events
rl_events = [e for e in events if e.get('event_type') == 'RANGE_LOCKED']
print(f"\nRANGE_LOCKED events: {len(rl_events)}")
for e in rl_events[-5:]:
    print(f"  Stream: {e.get('stream')}")
    print(f"    slot_time_chicago: {e.get('slot_time_chicago')}")
    print(f"    data.range_high: {e.get('data', {}).get('range_high')}")
    print(f"    data.range_low: {e.get('data', {}).get('range_low')}")
    print()

# Check RANGE_LOCK_SNAPSHOT events
snapshot_events = [e for e in events if e.get('event_type') == 'RANGE_LOCK_SNAPSHOT']
print(f"\nRANGE_LOCK_SNAPSHOT events: {len(snapshot_events)}")
for e in snapshot_events[-5:]:
    print(f"  Stream: {e.get('stream')}")
    print(f"    slot_time_chicago: {e.get('slot_time_chicago')}")
    print(f"    data.range_high: {e.get('data', {}).get('range_high')}")
    print(f"    data.range_low: {e.get('data', {}).get('range_low')}")
    print()

# Check STREAM_STATE_TRANSITION to RANGE_LOCKED
transitions = [e for e in events if e.get('event_type') == 'STREAM_STATE_TRANSITION' and e.get('data', {}).get('new_state') == 'RANGE_LOCKED']
print(f"\nSTREAM_STATE_TRANSITION to RANGE_LOCKED: {len(transitions)}")
for e in transitions[-5:]:
    print(f"  Stream: {e.get('stream')}")
    print(f"    slot_time_chicago: {e.get('slot_time_chicago')}")
    print(f"    data.range_high: {e.get('data', {}).get('range_high')}")
    print(f"    data.range_low: {e.get('data', {}).get('range_low')}")
    print()
