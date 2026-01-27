#!/usr/bin/env python3
"""Check if bars that passed time check reached commit decision"""
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
today_events.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("BARS THAT PASSED TIME CHECK vs COMMIT DECISIONS")
print("="*80)

for stream_id in ['NG1', 'NG2', 'YM1']:
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    # Get all BAR_ADMISSION_PROOF events that passed time check
    passed_proof = [e for e in stream_events 
                   if e.get('event') == 'BAR_ADMISSION_PROOF' and
                   e.get('data', {}).get('comparison_result') == True]
    
    # Get all BAR_ADMISSION_TO_COMMIT_DECISION events
    commit_decisions = [e for e in stream_events 
                       if e.get('event') == 'BAR_ADMISSION_TO_COMMIT_DECISION']
    
    print(f"\n{stream_id}:")
    print(f"  Bars that passed time check: {len(passed_proof)}")
    print(f"  Commit decisions logged: {len(commit_decisions)}")
    
    if len(passed_proof) > 0 and len(commit_decisions) == 0:
        print(f"  ISSUE: {len(passed_proof)} bars passed time check but 0 commit decisions!")
        print(f"  This means bars are NOT reaching the commit decision code.")
        
        # Check time range of passed bars
        if passed_proof:
            first_passed = min(passed_proof, key=lambda x: x.get('ts_utc', ''))
            last_passed = max(passed_proof, key=lambda x: x.get('ts_utc', ''))
            
            first_data = first_passed.get('data', {})
            last_data = last_passed.get('data', {})
            
            print(f"\n  First bar that passed:")
            print(f"    Time: {first_passed.get('ts_utc', '')[:19]}")
            print(f"    Bar time: {first_data.get('bar_time_chicago', 'N/A')}")
            print(f"    Range: [{first_data.get('range_start_chicago', 'N/A')}, {first_data.get('slot_time_chicago', 'N/A')})")
            
            print(f"\n  Last bar that passed:")
            print(f"    Time: {last_passed.get('ts_utc', '')[:19]}")
            print(f"    Bar time: {last_data.get('bar_time_chicago', 'N/A')}")
            print(f"    Range: [{last_data.get('range_start_chicago', 'N/A')}, {last_data.get('slot_time_chicago', 'N/A')})")
            
            # Check if there are any BAR_INVALID events around this time
            invalid_bars = [e for e in stream_events 
                          if e.get('event') == 'BAR_INVALID' and
                          e.get('ts_utc', '') >= first_passed.get('ts_utc', '') and
                          e.get('ts_utc', '') <= last_passed.get('ts_utc', '')]
            print(f"\n  BAR_INVALID events in this time range: {len(invalid_bars)}")
            
            # Check if there are any exceptions or errors
            errors = [e for e in stream_events 
                     if ('ERROR' in e.get('event', '') or 'EXCEPTION' in e.get('event', '')) and
                     e.get('ts_utc', '') >= first_passed.get('ts_utc', '') and
                     e.get('ts_utc', '') <= last_passed.get('ts_utc', '')]
            print(f"  ERROR/EXCEPTION events in this time range: {len(errors)}")

print(f"\n{'='*80}")
