#!/usr/bin/env python3
"""Check how comparison_result is stored in logs"""
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

# Get NG2 BAR_ADMISSION_PROOF events
ng2_proof = [e for e in today_events 
            if e.get('stream') == 'NG2' and 
            e.get('event') == 'BAR_ADMISSION_PROOF']

print("="*80)
print("NG2 COMPARISON_RESULT FORMAT CHECK")
print("="*80)
print(f"Total BAR_ADMISSION_PROOF events: {len(ng2_proof)}")

if ng2_proof:
    # Check first few events to see format
    print(f"\nFirst 5 events:")
    for i, e in enumerate(ng2_proof[:5]):
        data = e.get('data', {})
        result = data.get('comparison_result')
        result_type = type(result).__name__
        print(f"  Event {i+1}:")
        print(f"    comparison_result: {result} (type: {result_type})")
        print(f"    comparison_result == True: {result == True}")
        print(f"    comparison_result == 'True': {result == 'True'}")
        print(f"    comparison_result is True: {result is True}")
    
    # Count with different checks
    count_bool_true = sum(1 for e in ng2_proof if e.get('data', {}).get('comparison_result') == True)
    count_str_true = sum(1 for e in ng2_proof if e.get('data', {}).get('comparison_result') == 'True')
    count_truthy = sum(1 for e in ng2_proof if e.get('data', {}).get('comparison_result'))
    
    print(f"\nCounts:")
    print(f"  comparison_result == True (bool): {count_bool_true}")
    print(f"  comparison_result == 'True' (str): {count_str_true}")
    print(f"  comparison_result (truthy): {count_truthy}")
    
    # Check if any passed
    passed = [e for e in ng2_proof if e.get('data', {}).get('comparison_result') == True]
    if passed:
        print(f"\n  Found {len(passed)} bars that passed (bool True)")
        latest = max(passed, key=lambda x: x.get('ts_utc', ''))
        data = latest.get('data', {})
        print(f"  Latest passed bar:")
        print(f"    Time: {latest.get('ts_utc', '')[:19]}")
        print(f"    Bar time: {data.get('bar_time_chicago', 'N/A')}")
        print(f"    Range: [{data.get('range_start_chicago', 'N/A')}, {data.get('slot_time_chicago', 'N/A')})")

print(f"\n{'='*80}")
