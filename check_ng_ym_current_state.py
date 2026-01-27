#!/usr/bin/env python3
"""Check current state of NG1, NG2, YM1 streams"""
import json
from pathlib import Path
from datetime import datetime

# Read timetable
timetable_path = Path("data/timetable/timetable_current.json")
if timetable_path.exists():
    timetable = json.loads(timetable_path.read_text())
    streams = [s for s in timetable.get('streams', []) if s.get('stream') in ['NG1', 'NG2', 'YM1']]
    print("="*80)
    print("NG1/NG2/YM1 TIMETABLE INFO")
    print("="*80)
    for s in streams:
        print(f"\n{s.get('stream')}:")
        print(f"  Enabled: {s.get('enabled')}")
        print(f"  Slot time: {s.get('slot_time')}")
        print(f"  Session: {s.get('session')}")
        print(f"  Instrument: {s.get('instrument')}")

# Read latest robot events
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

print("\n" + "="*80)
print("NG1/NG2/YM1 CURRENT STATE")
print("="*80)

for stream_id in ['NG1', 'NG2', 'YM1']:
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    if not stream_events:
        print(f"\n{stream_id}: NO EVENTS FOUND")
        continue
    
    # Get latest state
    state_events = [e for e in stream_events if e.get('state')]
    latest_state_event = max(state_events, key=lambda x: x.get('ts_utc', '')) if state_events else None
    
    # Get latest HYDRATION_SUMMARY
    hydration_summaries = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    latest_hydration = max(hydration_summaries, key=lambda x: x.get('ts_utc', '')) if hydration_summaries else None
    
    # Get latest PRE_HYDRATION_TO_ARMED_TRANSITION
    transitions = [e for e in stream_events if e.get('event') == 'PRE_HYDRATION_TO_ARMED_TRANSITION']
    latest_transition = max(transitions, key=lambda x: x.get('ts_utc', '')) if transitions else None
    
    # Get latest commit
    commits = [e for e in stream_events if 'COMMIT' in e.get('event', '') or e.get('event') == 'STREAM_STAND_DOWN']
    latest_commit = max(commits, key=lambda x: x.get('ts_utc', '')) if commits else None
    
    print(f"\n{stream_id}:")
    if latest_state_event:
        print(f"  Latest state: {latest_state_event.get('state')} (at {latest_state_event.get('ts_utc', '')[:19]})")
    if latest_hydration:
        data = latest_hydration.get('data', {})
        print(f"  Latest HYDRATION_SUMMARY:")
        print(f"    Time: {latest_hydration.get('ts_utc', '')[:19]}")
        print(f"    Bars loaded: {data.get('loaded_bars', 'N/A')}")
        print(f"    Range high: {data.get('reconstructed_range_high', 'N/A')}")
        print(f"    Range low: {data.get('reconstructed_range_low', 'N/A')}")
        print(f"    Missed breakout: {data.get('missed_breakout', 'N/A')}")
    if latest_transition:
        print(f"  Transitioned to ARMED: {latest_transition.get('ts_utc', '')[:19]}")
    if latest_commit:
        print(f"  Committed: {latest_commit.get('event')} at {latest_commit.get('ts_utc', '')[:19]}")
    
    # Check if stream is past slot time
    if latest_hydration:
        data = latest_hydration.get('data', {})
        slot_time_str = data.get('slot_time_chicago', '')
        if slot_time_str:
            try:
                # Parse slot time (format: 2026-01-26T09:30:00.0000000-06:00)
                slot_time_part = slot_time_str.split('T')[1].split('.')[0] if 'T' in slot_time_str else ''
                print(f"  Slot time: {slot_time_part}")
            except:
                pass

print(f"\n{'='*80}")
print(f"Current UTC time: {datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')}")
