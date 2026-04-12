#!/usr/bin/env python3
"""Check execution gate violation details"""
import json
from pathlib import Path

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
print("EXECUTION GATE VIOLATION DETAILS")
print("="*80)

# Get recent execution gate violations
violations = [e for e in today_events 
             if 'EXECUTION_GATE' in e.get('event', '') and 
             'VIOLATION' in e.get('event', '')]

if violations:
    # Get latest violation for each stream
    by_stream = {}
    for v in violations:
        stream = v.get('stream', 'N/A')
        if stream not in by_stream:
            by_stream[stream] = []
        by_stream[stream].append(v)
    
    for stream_id in sorted(by_stream.keys()):
        stream_violations = by_stream[stream_id]
        latest = max(stream_violations, key=lambda x: x.get('ts_utc', ''))
        
        print(f"\n{stream_id}:")
        print(f"  Total violations: {len(stream_violations)}")
        print(f"  Latest: {latest.get('ts_utc', '')[:19]}")
        
        data = latest.get('data', {})
        if isinstance(data, dict):
            print(f"  Reason: {data.get('reason', 'N/A')}")
            print(f"  Error: {data.get('error', 'N/A')}")
            print(f"  Message: {data.get('message', 'N/A')}")
            print(f"  Failed gates: {data.get('failed_gates', 'N/A')}")
            print(f"  State: {latest.get('state', 'N/A')}")
            
            # Show all keys to see what's available
            print(f"  All data keys: {list(data.keys())}")

print(f"\n{'='*80}")
