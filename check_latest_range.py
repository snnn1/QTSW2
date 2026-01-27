#!/usr/bin/env python3
"""Check the latest range computation"""
import json
from pathlib import Path
from datetime import datetime

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

# Find all RANGE_INITIALIZED_FROM_HISTORY events
range_events = []
for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        if e.get('event') == 'RANGE_INITIALIZED_FROM_HISTORY':
            range_events.append(e)

# Sort by timestamp, get latest
if range_events:
    range_events.sort(key=lambda x: x.get('ts_utc', ''), reverse=True)
    latest = range_events[0]
    
    print("="*80)
    print("LATEST RANGE COMPUTATION:")
    print("="*80)
    
    stream = latest.get('stream', 'N/A')
    timestamp = latest.get('ts_utc', 'N/A')
    data = latest.get('data', {})
    
    if isinstance(data, dict):
        print(f"\n  Stream: {stream}")
        print(f"  Timestamp: {timestamp}")
        print(f"\n  Range High: {data.get('range_high', 'N/A')}")
        print(f"  Range Low: {data.get('range_low', 'N/A')}")
        
        high = data.get('range_high')
        low = data.get('range_low')
        if high is not None and low is not None:
            try:
                spread = float(high) - float(low)
                print(f"  Spread: {spread}")
            except:
                pass
        
        print(f"\n  Range Start: {data.get('range_start_chicago', 'N/A')}")
        print(f"  Slot Time: {data.get('slot_time_chicago', 'N/A')}")
        print(f"  Bars Used: {data.get('bars_used', 'N/A')}")
        
        # Show all data fields
        print(f"\n  All Fields:")
        for key, value in sorted(data.items()):
            print(f"    {key}: {value}")
    
    print(f"\n{'='*80}")
    
    # Show if there are multiple recent computations
    if len(range_events) > 1:
        print(f"\n  Note: Found {len(range_events)} total range computations")
        print(f"  Showing the latest one (most recent timestamp)")
        print(f"\n  Recent computations:")
        for i, event in enumerate(range_events[:5]):  # Show up to 5 most recent
            ts = event.get('ts_utc', 'N/A')[:19]
            stream_name = event.get('stream', 'N/A')
            event_data = event.get('data', {})
            if isinstance(event_data, dict):
                high = event_data.get('range_high', 'N/A')
                low = event_data.get('range_low', 'N/A')
                print(f"    {i+1}. {stream_name} at {ts}: High={high}, Low={low}")
    
    print(f"\n{'='*80}")
else:
    print("="*80)
    print("NO RANGE COMPUTATIONS FOUND")
    print("="*80)
