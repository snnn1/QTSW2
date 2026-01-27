#!/usr/bin/env python3
"""Check range values from RANGE_LOCKED events"""
import json
from pathlib import Path
from collections import defaultdict

log_dir = Path("logs/robot")
events = []

for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except:
        pass

today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]
locked_events = [e for e in today_events if e.get('event') == 'RANGE_LOCKED']
snapshot_events = [e for e in today_events if e.get('event') == 'RANGE_LOCK_SNAPSHOT']
compute_events = [e for e in today_events if e.get('event') == 'RANGE_COMPUTE_COMPLETE']

print("="*80)
print("RANGE VALUES CHECK")
print("="*80)
print()

# Group by stream and get latest
by_stream = defaultdict(list)
for event in snapshot_events + compute_events:
    stream = event.get('stream')
    if stream:
        by_stream[stream].append(event)

for stream in sorted(by_stream.keys()):
    stream_events = sorted(by_stream[stream], key=lambda x: x.get('ts_utc', ''))
    latest = stream_events[-1]
    data = latest.get('data', {})
    event_type = latest.get('event', 'UNKNOWN')
    
    print(f"{stream}:")
    print(f"  Latest Event: {event_type} at {latest.get('ts_utc', '')[:19]}")
    print(f"  Range High: {data.get('range_high')}")
    print(f"  Range Low: {data.get('range_low')}")
    if data.get('range_high') is not None and data.get('range_low') is not None:
        try:
            high = float(data.get('range_high'))
            low = float(data.get('range_low'))
            print(f"  Range Size: {high - low}")
        except:
            pass
    print()
