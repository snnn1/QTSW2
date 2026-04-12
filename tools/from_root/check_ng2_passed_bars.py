#!/usr/bin/env python3
"""Check NG2 bars that passed time check"""
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

# Get NG2 bars that passed time check (string "True")
ng2_proof = [e for e in today_events 
            if e.get('stream') == 'NG2' and 
            e.get('event') == 'BAR_ADMISSION_PROOF' and
            e.get('data', {}).get('comparison_result') == 'True']

print("="*80)
print("NG2 BARS THAT PASSED TIME CHECK")
print("="*80)
print(f"Total bars that passed: {len(ng2_proof)}")

if ng2_proof:
    # Get time range
    first = min(ng2_proof, key=lambda x: x.get('ts_utc', ''))
    last = max(ng2_proof, key=lambda x: x.get('ts_utc', ''))
    
    print(f"\nFirst bar that passed:")
    print(f"  Event time: {first.get('ts_utc', '')[:19]}")
    data = first.get('data', {})
    print(f"  Bar time Chicago: {data.get('bar_time_chicago', 'N/A')}")
    print(f"  Range start: {data.get('range_start_chicago', 'N/A')}")
    print(f"  Slot time: {data.get('slot_time_chicago', 'N/A')}")
    
    print(f"\nLast bar that passed:")
    print(f"  Event time: {last.get('ts_utc', '')[:19]}")
    data = last.get('data', {})
    print(f"  Bar time Chicago: {data.get('bar_time_chicago', 'N/A')}")
    print(f"  Range start: {data.get('range_start_chicago', 'N/A')}")
    print(f"  Slot time: {data.get('slot_time_chicago', 'N/A')}")
    
    # Check for commit decisions in this time range
    ng2_events = [e for e in today_events if e.get('stream') == 'NG2']
    commit_decisions = [e for e in ng2_events 
                       if e.get('event') == 'BAR_ADMISSION_TO_COMMIT_DECISION' and
                       e.get('ts_utc', '') >= first.get('ts_utc', '') and
                       e.get('ts_utc', '') <= last.get('ts_utc', '')]
    
    print(f"\nBAR_ADMISSION_TO_COMMIT_DECISION events in this time range: {len(commit_decisions)}")
    
    if len(commit_decisions) == 0:
        print(f"\n  CRITICAL: {len(ng2_proof)} bars passed time check but 0 commit decisions!")
        print(f"  This means the code path between time check and commit decision is broken.")
        print(f"  The bars ARE in range, but something is preventing them from reaching commit decision.")
        
        # Check for BAR_INVALID events
        invalid = [e for e in ng2_events 
                  if e.get('event') == 'BAR_INVALID' and
                  e.get('ts_utc', '') >= first.get('ts_utc', '') and
                  e.get('ts_utc', '') <= last.get('ts_utc', '')]
        print(f"\n  BAR_INVALID events in this range: {len(invalid)}")
        
        # Check stream state during this time
        state_events = [e for e in ng2_events 
                       if e.get('state') and
                       e.get('ts_utc', '') >= first.get('ts_utc', '') and
                       e.get('ts_utc', '') <= last.get('ts_utc', '')]
        if state_events:
            states = set(e.get('state') for e in state_events)
            print(f"  Stream states observed: {', '.join(sorted(states))}")

print(f"\n{'='*80}")
