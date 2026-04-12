#!/usr/bin/env python3
"""Check current range from stream state or latest range update"""
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

# Find all events with range_high/range_low for NQ2
nq2_range_events = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2'):
        data = e.get('data', {})
        if isinstance(data, dict):
            if 'range_high' in data or 'range_low' in data:
                nq2_range_events.append(e)

print("="*80)
print("CURRENT RANGE FROM EVENTS:")
print("="*80)

if nq2_range_events:
    # Sort by timestamp, get latest
    nq2_range_events.sort(key=lambda x: x.get('ts_utc', ''), reverse=True)
    
    print(f"\n  Found {len(nq2_range_events)} events with range data")
    print(f"\n  Latest range event:")
    
    latest = nq2_range_events[0]
    event_type = latest.get('event', 'N/A')
    timestamp = latest.get('ts_utc', 'N/A')
    data = latest.get('data', {})
    
    print(f"    Event: {event_type}")
    print(f"    Timestamp: {timestamp}")
    
    if isinstance(data, dict):
        range_high = data.get('range_high')
        range_low = data.get('range_low')
        
        if range_high is not None and range_low is not None:
            print(f"\n    CURRENT RANGE:")
            print(f"      Range High: {range_high}")
            print(f"      Range Low: {range_low}")
            try:
                spread = float(range_high) - float(range_low)
                print(f"      Spread: {spread}")
            except:
                pass
            
            # Show other relevant fields
            print(f"\n    Other fields:")
            for key, value in sorted(data.items()):
                if key not in ['range_high', 'range_low']:
                    print(f"      {key}: {value}")
        else:
            print(f"\n    No range values in this event")
            print(f"    Available fields: {list(data.keys())}")
    
    # Show recent range events
    if len(nq2_range_events) > 1:
        print(f"\n  Recent range events:")
        for i, event in enumerate(nq2_range_events[:5]):
            ts = event.get('ts_utc', 'N/A')[:19]
            evt_type = event.get('event', 'N/A')
            evt_data = event.get('data', {})
            if isinstance(evt_data, dict):
                high = evt_data.get('range_high', 'N/A')
                low = evt_data.get('range_low', 'N/A')
                print(f"    {i+1}. {evt_type} at {ts}: High={high}, Low={low}")
else:
    print(f"\n  No events with range data found for NQ2")

print(f"\n{'='*80}")
