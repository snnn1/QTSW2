#!/usr/bin/env python3
"""Check all range-related events"""
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

# Find all range-related events
range_events = {}

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        event_type = e.get('event', '')
        stream = e.get('stream', 'UNKNOWN')
        
        if event_type in ['RANGE_INITIALIZED_FROM_HISTORY', 'HYDRATION_SUMMARY']:
            if stream not in range_events:
                range_events[stream] = []
            range_events[stream].append(e)

print("="*80)
print("ALL COMPUTED RANGES:")
print("="*80)

for stream in sorted(range_events.keys()):
    print(f"\n  Stream: {stream}")
    print(f"  {'-'*76}")
    
    for event in sorted(range_events[stream], key=lambda x: x.get('ts_utc', '')):
        event_type = event.get('event', '')
        data = event.get('data', {})
        
        if isinstance(data, dict):
            if event_type == 'RANGE_INITIALIZED_FROM_HISTORY':
                print(f"    RANGE_INITIALIZED_FROM_HISTORY:")
                print(f"      Range high: {data.get('range_high', 'N/A')}")
                print(f"      Range low: {data.get('range_low', 'N/A')}")
                print(f"      Bars used: {data.get('bars_used', 'N/A')}")
                print(f"      Range start: {data.get('range_start_chicago', 'N/A')}")
                print(f"      Slot time: {data.get('slot_time_chicago', 'N/A')}")
                print(f"      Timestamp: {event.get('ts_utc', 'N/A')[:19]}")
            
            elif event_type == 'HYDRATION_SUMMARY':
                range_high = data.get('range_high', 'N/A')
                range_low = data.get('range_low', 'N/A')
                if range_high != 'N/A' or range_low != 'N/A':
                    print(f"    HYDRATION_SUMMARY:")
                    print(f"      Range high: {range_high}")
                    print(f"      Range low: {range_low}")
                    print(f"      Loaded bars: {data.get('loaded_bars', 'N/A')}")
                    print(f"      Completeness: {data.get('completeness_pct', 'N/A')}%")
                    print(f"      Timestamp: {event.get('ts_utc', 'N/A')[:19]}")

print(f"\n{'='*80}")
print("SUMMARY:")
print(f"{'='*80}")

streams_with_ranges = []
for stream, events_list in range_events.items():
    for event in events_list:
        if event.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY':
            data = event.get('data', {})
            if isinstance(data, dict) and data.get('range_high') is not None:
                high = data.get('range_high')
                low = data.get('range_low')
                spread = None
                if high is not None and low is not None:
                    try:
                        spread = float(high) - float(low)
                    except:
                        pass
                streams_with_ranges.append({
                    'stream': stream,
                    'high': high,
                    'low': low,
                    'spread': spread
                })
                break

if streams_with_ranges:
    print(f"\n  Streams with computed ranges:")
    for s in streams_with_ranges:
        print(f"    {s['stream']}: High={s['high']}, Low={s['low']}, Spread={s['spread']}")
else:
    print(f"\n  No ranges computed yet")

print(f"\n{'='*80}")
