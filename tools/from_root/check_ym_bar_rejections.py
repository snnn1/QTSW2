#!/usr/bin/env python3
"""Check why YM bars pass admission but aren't committed"""
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

print("="*80)
print("YM BAR REJECTION ANALYSIS:")
print("="*80)

for stream in ['YM1', 'YM2']:
    print(f"\n  Stream: {stream}")
    print(f"  {'-'*76}")
    
    stream_events = [e for e in events 
                    if e.get('ts_utc', '').startswith('2026-01-26') and 
                    e.get('stream') == stream]
    
    # Check BAR_BUFFER_REJECTED
    rejected = [e for e in stream_events if e.get('event') == 'BAR_BUFFER_REJECTED']
    print(f"    BAR_BUFFER_REJECTED: {len(rejected)}")
    
    if rejected:
        rejection_reasons = defaultdict(int)
        for e in rejected:
            data = e.get('data', {})
            if isinstance(data, dict):
                reason = data.get('rejection_reason', 'N/A')
                rejection_reasons[reason] += 1
        print(f"    Rejection reasons:")
        for reason, count in sorted(rejection_reasons.items(), key=lambda x: x[1], reverse=True):
            print(f"      - {reason}: {count}")
    
    # Check BAR_BUFFER_ADD_ATTEMPT
    attempts = [e for e in stream_events if e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT']
    print(f"    BAR_BUFFER_ADD_ATTEMPT: {len(attempts)}")
    
    # Check BAR_ADMISSION_PROOF details
    admission = [e for e in stream_events if e.get('event') == 'BAR_ADMISSION_PROOF']
    print(f"    BAR_ADMISSION_PROOF: {len(admission)}")
    
    if admission:
        # Check comparison results
        comparison_results = defaultdict(int)
        for e in admission:
            data = e.get('data', {})
            if isinstance(data, dict):
                result = data.get('comparison_result', False)
                comparison_results[result] += 1
        print(f"    Comparison results:")
        for result, count in sorted(comparison_results.items(), key=lambda x: x[1], reverse=True):
            print(f"      - {result}: {count}")
        
        # Check a sample admission event
        sample = admission[0]
        s_data = sample.get('data', {})
        if isinstance(s_data, dict):
            print(f"\n    Sample BAR_ADMISSION_PROOF:")
            print(f"      Bar time Chicago: {s_data.get('bar_time_chicago', 'N/A')}")
            print(f"      Range start: {s_data.get('range_start_chicago', 'N/A')}")
            print(f"      Slot time: {s_data.get('slot_time_chicago', 'N/A')}")
            print(f"      Comparison result: {s_data.get('comparison_result', 'N/A')}")
            print(f"      Bar source: {s_data.get('bar_source', 'N/A')}")

print(f"\n{'='*80}")
print("COMPARING WITH NQ2:")
print(f"{'='*80}")

nq2_events = [e for e in events 
             if e.get('ts_utc', '').startswith('2026-01-26') and 
             e.get('stream') == 'NQ2']

nq2_attempts = [e for e in nq2_events if e.get('event') == 'BAR_BUFFER_ADD_ATTEMPT']
nq2_rejected = [e for e in nq2_events if e.get('event') == 'BAR_BUFFER_REJECTED']
nq2_committed = [e for e in nq2_events if e.get('event') == 'BAR_BUFFER_ADD_COMMITTED']

print(f"\n  NQ2:")
print(f"    BAR_BUFFER_ADD_ATTEMPT: {len(nq2_attempts)}")
print(f"    BAR_BUFFER_REJECTED: {len(nq2_rejected)}")
print(f"    BAR_BUFFER_ADD_COMMITTED: {len(nq2_committed)}")

print(f"\n{'='*80}")
