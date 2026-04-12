#!/usr/bin/env python3
"""Check NG1 BAR_ADMISSION_PROOF details"""
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

# Get NG1 BAR_ADMISSION_PROOF events
ng1_proof = [e for e in today_events 
            if e.get('stream') == 'NG1' and 
            e.get('event') == 'BAR_ADMISSION_PROOF']

print("="*80)
print("NG1 BAR_ADMISSION_PROOF ANALYSIS")
print("="*80)
print(f"Total BAR_ADMISSION_PROOF events: {len(ng1_proof)}")

if ng1_proof:
    # Check comparison results
    comparison_results = Counter()
    for e in ng1_proof[:100]:  # Check first 100
        data = e.get('data', {})
        if isinstance(data, dict):
            result = data.get('comparison_result', 'N/A')
            comparison_results[result] += 1
    
    print(f"\nComparison results (first 100):")
    for result, count in comparison_results.most_common():
        print(f"  {result}: {count}")
    
    # Check latest event
    latest = ng1_proof[-1]
    data = latest.get('data', {})
    print(f"\nLatest BAR_ADMISSION_PROOF:")
    print(f"  Time: {latest.get('ts_utc', '')[:19]}")
    print(f"  Comparison result: {data.get('comparison_result', 'N/A')}")
    print(f"  Comparison detail: {data.get('comparison_detail', 'N/A')}")
    print(f"  Bar time Chicago: {data.get('bar_time_chicago', 'N/A')}")
    print(f"  Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
    print(f"  Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")

print(f"\n{'='*80}")
