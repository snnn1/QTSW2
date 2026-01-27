#!/usr/bin/env python3
"""Check what caused state reset around 16:42"""
import json
from pathlib import Path
from datetime import datetime

# Read latest robot events
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
today_events.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("STATE RESET INVESTIGATION")
print("="*80)

for stream_id in ['NG1', 'NG2', 'YM1']:
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    # Get events around 16:42
    reset_window = [e for e in stream_events 
                   if e.get('ts_utc', '') >= '2026-01-26T16:40:00' and 
                   e.get('ts_utc', '') <= '2026-01-26T16:45:00']
    
    # Get transition events
    transitions = [e for e in stream_events if 'TRANSITION' in e.get('event', '')]
    
    # Get state changes
    state_changes = [e for e in stream_events if e.get('state')]
    
    print(f"\n{stream_id} - Events around 16:42:")
    if reset_window:
        for e in reset_window[:20]:  # First 20 events
            event_type = e.get('event', 'N/A')
            state = e.get('state', 'N/A')
            ts = e.get('ts_utc', '')[:19]
            print(f"  {ts} | {event_type} | state={state}")
    
    # Check for transitions to RANGE_BUILDING
    print(f"\n{stream_id} - Transitions to RANGE_BUILDING:")
    for t in transitions:
        data = t.get('data', {})
        if data.get('new_state') == 'RANGE_BUILDING':
            print(f"  {t.get('ts_utc', '')[:19]} - {t.get('event', 'N/A')}")
            print(f"    Previous: {data.get('previous_state', 'N/A')}")
            print(f"    Reason: {data.get('transition_reason', 'N/A')}")

print(f"\n{'='*80}")
