#!/usr/bin/env python3
"""Check events after robot restart"""
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
restart_time = '2026-01-26T18:37:18'
after_restart = [e for e in today_events if e.get('ts_utc', '') >= restart_time]

print("="*80)
print("EVENTS AFTER ROBOT RESTART (18:37:18)")
print("="*80)

for stream_id in ['NG1', 'NG2', 'YM1']:
    stream_after = [e for e in after_restart if e.get('stream') == stream_id]
    
    commit_decisions = [e for e in stream_after if e.get('event') == 'BAR_ADMISSION_TO_COMMIT_DECISION']
    admission_proof = [e for e in stream_after if e.get('event') == 'BAR_ADMISSION_PROOF']
    passed_proof = [e for e in admission_proof if e.get('data', {}).get('comparison_result') == 'True']
    
    print(f"\n{stream_id}:")
    print(f"  BAR_ADMISSION_PROOF: {len(admission_proof)}")
    print(f"  Bars that passed time check: {len(passed_proof)}")
    print(f"  BAR_ADMISSION_TO_COMMIT_DECISION: {len(commit_decisions)}")
    
    if len(passed_proof) > 0 and len(commit_decisions) == 0:
        print(f"  ISSUE: Bars passed time check but no commit decisions!")
        if passed_proof:
            latest = max(passed_proof, key=lambda x: x.get('ts_utc', ''))
            print(f"  Latest passed bar time: {latest.get('ts_utc', '')[:19]}")
            data = latest.get('data', {})
            print(f"  Bar time: {data.get('bar_time_chicago', 'N/A')}")
            print(f"  Range: [{data.get('range_start_chicago', 'N/A')}, {data.get('slot_time_chicago', 'N/A')})")
    elif len(admission_proof) == 0:
        print(f"  No bars received after restart (stream may be past slot time)")

print(f"\n{'='*80}")
