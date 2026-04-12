#!/usr/bin/env python3
"""Check NG2 time check results"""
import json
from pathlib import Path
from collections import Counter

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

# Get NG2 BAR_ADMISSION_PROOF events
ng2_proof = [e for e in today_events 
            if e.get('stream') == 'NG2' and 
            e.get('event') == 'BAR_ADMISSION_PROOF']

print("="*80)
print("NG2 TIME CHECK ANALYSIS")
print("="*80)
print(f"Total BAR_ADMISSION_PROOF events: {len(ng2_proof)}")

if ng2_proof:
    # Count comparison results
    comparison_results = Counter()
    for e in ng2_proof:
        data = e.get('data', {})
        if isinstance(data, dict):
            result = data.get('comparison_result', 'N/A')
            comparison_results[result] += 1
    
    print(f"\nComparison results:")
    for result, count in comparison_results.most_common():
        print(f"  {result}: {count}")
    
    # Check if any bars with comparison_result=True have BAR_ADMISSION_TO_COMMIT_DECISION
    ng2_events = [e for e in today_events if e.get('stream') == 'NG2']
    commit_decisions = [e for e in ng2_events if e.get('event') == 'BAR_ADMISSION_TO_COMMIT_DECISION']
    
    print(f"\nBAR_ADMISSION_TO_COMMIT_DECISION events: {len(commit_decisions)}")
    
    if len(commit_decisions) == 0 and comparison_results.get(True, 0) > 0:
        print(f"\n  WARNING: {comparison_results.get(True, 0)} bars passed time check but 0 commit decisions logged!")
        print(f"  This suggests bars are failing between admission proof and commit decision.")
        
        # Check for exceptions or early returns
        bar_invalid = [e for e in ng2_events if e.get('event') == 'BAR_INVALID']
        print(f"\n  BAR_INVALID events: {len(bar_invalid)}")
        
        # Check latest few bars that passed
        passed_bars = [e for e in ng2_proof if e.get('data', {}).get('comparison_result') == True]
        print(f"\n  Latest bar that passed time check:")
        if passed_bars:
            latest = passed_bars[-1]
            data = latest.get('data', {})
            print(f"    Time: {latest.get('ts_utc', '')[:19]}")
            print(f"    Bar time: {data.get('bar_time_chicago', 'N/A')}")
            print(f"    Range: [{data.get('range_start_chicago', 'N/A')}, {data.get('slot_time_chicago', 'N/A')})")

print(f"\n{'='*80}")
