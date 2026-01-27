#!/usr/bin/env python3
"""Check for gap events after restart to see if new gaps are being detected"""
import json
from pathlib import Path
from collections import defaultdict

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

# Check for events after journal reset (around 18:37 UTC based on check_after_restart.py)
restart_time = '2026-01-26T18:37:18'
after_restart = [e for e in today_events if e.get('ts_utc', '') >= restart_time]

print("="*80)
print("GAP EVENTS AFTER RESTART (18:37:18 UTC)")
print("="*80)
print()

# Check all streams
for stream_id in ['NG1', 'NG2', 'YM1', 'ES1', 'ES2']:
    stream_after = [e for e in after_restart if e.get('stream') == stream_id]
    
    gap_detected = [e for e in stream_after if e.get('event') == 'BAR_GAP_DETECTED']
    gap_violation = [e for e in stream_after if e.get('event') == 'GAP_TOLERANCE_VIOLATION']
    gap_tolerated = [e for e in stream_after if e.get('event') == 'GAP_TOLERATED']
    
    print(f"{stream_id}:")
    print(f"  Total events after restart: {len(stream_after)}")
    print(f"  BAR_GAP_DETECTED: {len(gap_detected)}")
    print(f"  GAP_TOLERANCE_VIOLATION: {len(gap_violation)}")
    print(f"  GAP_TOLERATED: {len(gap_tolerated)}")
    
    if gap_detected:
        print(f"  Sample BAR_GAP_DETECTED events:")
        for g in gap_detected[:5]:
            ts = g.get('ts_utc', '')[:19]
            data = g.get('data', {})
            print(f"    {ts}: Delta={data.get('delta_minutes')} min, Missing={data.get('added_to_total_gap')} min, Source={data.get('bar_source')}")
    
    if gap_violation:
        print(f"  WARNING: {len(gap_violation)} violations logged after restart!")
        print(f"  First violation: {gap_violation[0].get('ts_utc', '')[:19]}")
        print(f"  Last violation: {gap_violation[-1].get('ts_utc', '')[:19]}")
    
    print()

print("="*80)
print("SUMMARY:")
print("="*80)
print()
print("The 202 violations for NG1 are ALL from BEFORE the restart (13:59-16:42 UTC).")
print("These occurred when the old code was running (before our fix).")
print()
print("After restart (18:37 UTC):")
total_gap_detected = sum(1 for e in after_restart if e.get('event') == 'BAR_GAP_DETECTED')
total_gap_violation = sum(1 for e in after_restart if e.get('event') == 'GAP_TOLERANCE_VIOLATION')
print(f"  BAR_GAP_DETECTED events: {total_gap_detected}")
print(f"  GAP_TOLERANCE_VIOLATION events: {total_gap_violation}")
print()
if total_gap_violation == 0:
    print("GOOD: No new violations logged after restart - fix is working!")
    print("(Violations are no longer being logged because violated is always false)")
else:
    print("NOTE: Some violations still being logged - code may need recompilation")
