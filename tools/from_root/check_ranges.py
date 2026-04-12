#!/usr/bin/env python3
"""Check computed ranges from hydration"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

# Read all robot log files
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
    except Exception as e:
        pass

# Find HYDRATION_SUMMARY and RANGE_INITIALIZED_FROM_HISTORY events
hydration_summaries = []
range_initialized = []

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        event_type = e.get('event', '')
        if event_type == 'HYDRATION_SUMMARY':
            hydration_summaries.append(e)
        elif event_type == 'RANGE_INITIALIZED_FROM_HISTORY':
            range_initialized.append(e)

print("="*80)
print("COMPUTED RANGES:")
print("="*80)

# Group by stream
streams = {}
for h in hydration_summaries:
    stream = h.get('stream', 'UNKNOWN')
    if stream not in streams:
        streams[stream] = {'hydration': None, 'range': None}
    streams[stream]['hydration'] = h

for r in range_initialized:
    stream = r.get('stream', 'UNKNOWN')
    if stream not in streams:
        streams[stream] = {'hydration': None, 'range': None}
    streams[stream]['range'] = r

# Display ranges for each stream
for stream in sorted(streams.keys()):
    print(f"\n  Stream: {stream}")
    print(f"  {'-'*76}")
    
    h = streams[stream]['hydration']
    r = streams[stream]['range']
    
    if h:
        data = h.get('data', {})
        if isinstance(data, dict):
            print(f"    HYDRATION_SUMMARY:")
            print(f"      Expected bars: {data.get('expected_bars', 'N/A')}")
            print(f"      Loaded bars: {data.get('loaded_bars', 'N/A')}")
            print(f"      Completeness: {data.get('completeness_pct', 'N/A')}%")
            print(f"      Range high: {data.get('range_high', 'N/A')}")
            print(f"      Range low: {data.get('range_low', 'N/A')}")
            print(f"      Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
            print(f"      Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")
            print(f"      Late start: {data.get('late_start', 'N/A')}")
            print(f"      Missed breakout: {data.get('missed_breakout', 'N/A')}")
    
    if r:
        data = r.get('data', {})
        if isinstance(data, dict):
            print(f"    RANGE_INITIALIZED_FROM_HISTORY:")
            print(f"      Range high: {data.get('range_high', 'N/A')}")
            print(f"      Range low: {data.get('range_low', 'N/A')}")
            print(f"      Bars used: {data.get('bars_used', 'N/A')}")
            print(f"      Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
            print(f"      Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")

print(f"\n{'='*80}")
