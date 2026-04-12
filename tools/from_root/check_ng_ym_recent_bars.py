#!/usr/bin/env python3
"""Check recent bars for NG1, NG2, YM1"""
import json
from pathlib import Path
from datetime import datetime

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
print("RECENT BARS FOR NG1, NG2, YM1")
print("="*80)

for stream_id in ['NG1', 'NG2', 'YM1']:
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    # Get recent BAR_ADMISSION_PROOF events (last hour)
    recent_proof = [e for e in stream_events 
                   if e.get('event') == 'BAR_ADMISSION_PROOF' and
                   e.get('ts_utc', '').startswith('2026-01-26T18')]
    
    print(f"\n{stream_id}:")
    print(f"  Total BAR_ADMISSION_PROOF events: {len([e for e in stream_events if e.get('event') == 'BAR_ADMISSION_PROOF'])}")
    print(f"  Recent BAR_ADMISSION_PROOF (last hour): {len(recent_proof)}")
    
    if recent_proof:
        # Count comparison results
        passed = [e for e in recent_proof if e.get('data', {}).get('comparison_result') == True]
        failed = [e for e in recent_proof if e.get('data', {}).get('comparison_result') == False]
        
        print(f"    Passed time check: {len(passed)}")
        print(f"    Failed time check: {len(failed)}")
        
        if passed:
            latest_passed = max(passed, key=lambda x: x.get('ts_utc', ''))
            data = latest_passed.get('data', {})
            print(f"    Latest passed bar:")
            print(f"      Time: {latest_passed.get('ts_utc', '')[:19]}")
            print(f"      Bar time: {data.get('bar_time_chicago', 'N/A')}")
            print(f"      Range: [{data.get('range_start_chicago', 'N/A')}, {data.get('slot_time_chicago', 'N/A')})")
            
            # Check if there's a commit decision for this bar
            commit_decisions = [e for e in stream_events 
                              if e.get('event') == 'BAR_ADMISSION_TO_COMMIT_DECISION' and
                              e.get('ts_utc', '') >= latest_passed.get('ts_utc', '')]
            print(f"    Commit decisions after this bar: {len(commit_decisions)}")
    else:
        # Check latest admission proof overall
        all_proof = [e for e in stream_events if e.get('event') == 'BAR_ADMISSION_PROOF']
        if all_proof:
            latest = max(all_proof, key=lambda x: x.get('ts_utc', ''))
            data = latest.get('data', {})
            print(f"    Latest BAR_ADMISSION_PROOF: {latest.get('ts_utc', '')[:19]}")
            print(f"    Comparison result: {data.get('comparison_result', 'N/A')}")
            print(f"    Bar time: {data.get('bar_time_chicago', 'N/A')}")
            print(f"    Range: [{data.get('range_start_chicago', 'N/A')}, {data.get('slot_time_chicago', 'N/A')})")

print(f"\n{'='*80}")
