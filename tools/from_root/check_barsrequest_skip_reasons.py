#!/usr/bin/env python3
"""Check why BarsRequest is skipped for CL2 and YM2"""
import json
from pathlib import Path
from collections import defaultdict

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

# Find BARSREQUEST_SKIPPED events for CL2 and YM2
print("="*80)
print("BARSREQUEST SKIP REASONS:")
print("="*80)

for stream in ['CL2', 'YM2']:
    print(f"\n  Stream: {stream}")
    print(f"  {'-'*76}")
    
    skipped = [e for e in events 
               if e.get('ts_utc', '').startswith('2026-01-26') and 
               e.get('stream') == stream and
               e.get('event') == 'BARSREQUEST_SKIPPED']
    
    if skipped:
        for s in skipped:
            s_data = s.get('data', {})
            if isinstance(s_data, dict):
                reason = s_data.get('reason', 'N/A')
                instrument = s_data.get('instrument', 'N/A')
                print(f"    Reason: {reason}")
                print(f"    Instrument: {instrument}")
                print(f"    Time: {s.get('ts_utc', 'N/A')[:19]}")
    else:
        print(f"    No BARSREQUEST_SKIPPED events found")
    
    # Check for BARSREQUEST_REQUESTED
    requested = [e for e in events 
                if e.get('ts_utc', '').startswith('2026-01-26') and 
                e.get('stream') == stream and
                e.get('event') == 'BARSREQUEST_REQUESTED']
    
    if requested:
        print(f"    BARSREQUEST_REQUESTED events: {len(requested)}")
    else:
        print(f"    No BARSREQUEST_REQUESTED events")

# Check all streams for comparison
print(f"\n{'='*80}")
print("ALL STREAMS BARSREQUEST STATUS:")
print(f"{'='*80}")

all_streams = defaultdict(lambda: {'requested': 0, 'skipped': 0, 'failed': 0})

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        stream = e.get('stream', '')
        event_type = e.get('event', '')
        
        if event_type == 'BARSREQUEST_REQUESTED':
            all_streams[stream]['requested'] += 1
        elif event_type == 'BARSREQUEST_SKIPPED':
            all_streams[stream]['skipped'] += 1
        elif event_type == 'BARSREQUEST_FAILED':
            all_streams[stream]['failed'] += 1

for stream in sorted(all_streams.keys()):
    data = all_streams[stream]
    if data['requested'] > 0 or data['skipped'] > 0 or data['failed'] > 0:
        print(f"  {stream}: Requested={data['requested']}, Skipped={data['skipped']}, Failed={data['failed']}")

print(f"\n{'='*80}")
