#!/usr/bin/env python3
"""Check latest BAR_ADMISSION_TO_COMMIT_DECISION events to see if new code is running"""
import json
from pathlib import Path

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

# Get all BAR_ADMISSION_TO_COMMIT_DECISION events
commit_decisions = [e for e in today_events if e.get('event') == 'BAR_ADMISSION_TO_COMMIT_DECISION']

print("="*80)
print("BAR_ADMISSION_TO_COMMIT_DECISION EVENTS")
print("="*80)
print(f"Total events: {len(commit_decisions)}")

if commit_decisions:
    # Group by stream
    by_stream = {}
    for e in commit_decisions:
        stream = e.get('stream', 'N/A')
        if stream not in by_stream:
            by_stream[stream] = []
        by_stream[stream].append(e)
    
    print(f"\nEvents by stream:")
    for stream in sorted(by_stream.keys()):
        stream_events = by_stream[stream]
        latest = max(stream_events, key=lambda x: x.get('ts_utc', ''))
        print(f"  {stream}: {len(stream_events)} events")
        print(f"    Latest: {latest.get('ts_utc', '')[:19]}")
        data = latest.get('data', {})
        print(f"    State: {latest.get('state', 'N/A')}")
        print(f"    Will commit: {data.get('will_commit', 'N/A')}")
        print(f"    Reason: {data.get('reason', 'N/A')}")
    
    # Check if NG1, NG2, YM1 have any
    for stream in ['NG1', 'NG2', 'YM1']:
        if stream not in by_stream:
            print(f"\n  {stream}: NO EVENTS FOUND")
            # Check latest BAR_ADMISSION_PROOF for this stream
            proof_events = [e for e in today_events 
                          if e.get('stream') == stream and 
                          e.get('event') == 'BAR_ADMISSION_PROOF']
            if proof_events:
                latest_proof = max(proof_events, key=lambda x: x.get('ts_utc', ''))
                print(f"    Latest BAR_ADMISSION_PROOF: {latest_proof.get('ts_utc', '')[:19]}")
                data = latest_proof.get('data', {})
                print(f"    Comparison result: {data.get('comparison_result', 'N/A')}")
                print(f"    State: {latest_proof.get('state', 'N/A')}")
else:
    print("\n  NO EVENTS FOUND - Robot may not be running with new code yet")

print(f"\n{'='*80}")
