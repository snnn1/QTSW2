#!/usr/bin/env python3
"""Check stream range windows and why bars are outside"""
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

print("="*80)
print("STREAM RANGE WINDOWS ANALYSIS")
print("="*80)

for stream_id in ['NG1', 'NG2', 'YM1', 'CL2']:
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    # Get range info from HYDRATION_SUMMARY
    hydration = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    
    # Get latest admission proof to see range window
    admission_proof = [e for e in stream_events if e.get('event') == 'BAR_ADMISSION_PROOF']
    
    print(f"\n{stream_id}:")
    
    if hydration:
        latest = hydration[-1]
        data = latest.get('data', {})
        print(f"  Range start: {data.get('range_start_chicago', 'N/A')}")
        print(f"  Slot time: {data.get('slot_time_chicago', 'N/A')}")
        print(f"  Loaded bars: {data.get('loaded_bars', 'N/A')}")
        print(f"  Expected bars: {data.get('expected_bars', 'N/A')}")
    
    if admission_proof:
        latest = admission_proof[-1]
        data = latest.get('data', {})
        print(f"  Latest bar time: {data.get('bar_time_chicago', 'N/A')}")
        print(f"  Comparison result: {data.get('comparison_result', 'N/A')}")
        print(f"  Comparison detail: {data.get('comparison_detail', 'N/A')}")
    
    # Check for RANGE_COMPUTE_FAILED
    range_failed = [e for e in stream_events if e.get('event') == 'RANGE_COMPUTE_FAILED']
    if range_failed:
        latest = range_failed[-1]
        data = latest.get('data', {})
        print(f"  RANGE_COMPUTE_FAILED:")
        print(f"    Reason: {data.get('reason', 'N/A')}")
        print(f"    Bar count: {data.get('bar_count', 'N/A')}")

print(f"\n{'='*80}")
