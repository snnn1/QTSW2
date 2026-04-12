#!/usr/bin/env python3
"""Check why streams aren't locking ranges"""
import json
from pathlib import Path
from datetime import datetime

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

print("="*80)
print("RANGE LOCKING ANALYSIS")
print("="*80)
print("""
Streams should transition from RANGE_BUILDING to RANGE_LOCKED at slot_time.
If streams are stuck in RANGE_BUILDING past slot_time, there's a problem.
""")

# Read timetable
timetable_path = Path("data/timetable/timetable_current.json")
enabled_streams = []
if timetable_path.exists():
    timetable = json.loads(timetable_path.read_text())
    enabled_streams = [s.get('stream') for s in timetable.get('streams', []) if s.get('enabled')]

# Get all streams from events
all_streams = set(e.get('stream') for e in today_events if e.get('stream'))
all_streams = sorted([s for s in all_streams if s in enabled_streams])

for stream_id in all_streams:
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    # Get latest state
    state_events = [e for e in stream_events if e.get('state')]
    transitions = [e for e in stream_events if 'TRANSITION' in e.get('event', '')]
    
    latest_state = None
    latest_state_time = None
    
    if state_events:
        latest_state_event = max(state_events, key=lambda x: x.get('ts_utc', ''))
        latest_state = latest_state_event.get('state')
        latest_state_time = latest_state_event.get('ts_utc', '')[:19]
    
    if transitions:
        latest_transition = max(transitions, key=lambda x: x.get('ts_utc', ''))
        transition_data = latest_transition.get('data', {})
        if transition_data.get('new_state'):
            transition_time = latest_transition.get('ts_utc', '')[:19]
            if not latest_state_time or transition_time > latest_state_time:
                latest_state = transition_data.get('new_state')
                latest_state_time = transition_time
    
    if not latest_state:
        continue
    
    # Get slot time and range info
    hydration_summaries = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    latest_hydration = max(hydration_summaries, key=lambda x: x.get('ts_utc', '')) if hydration_summaries else None
    
    # Get range lock events
    range_locks = [e for e in stream_events if 'RANGE_LOCK' in e.get('event', '')]
    
    # Get range compute events
    range_computes = [e for e in stream_events if 'RANGE_COMPUTE' in e.get('event', '')]
    
    print(f"\n{stream_id}:")
    print(f"  Current state: {latest_state} (since {latest_state_time})")
    
    if latest_hydration:
        data = latest_hydration.get('data', {})
        slot_time_str = data.get('slot_time_chicago', '')
        range_high = data.get('reconstructed_range_high')
        range_low = data.get('reconstructed_range_low')
        
        print(f"  Range: [{range_low}, {range_high}]")
        print(f"  Slot time: {slot_time_str}")
        
        # Parse slot time to check if we're past it
        if slot_time_str:
            try:
                # Format: 2026-01-26T09:30:00.0000000-06:00
                slot_part = slot_time_str.split('T')[1].split('.')[0] if 'T' in slot_time_str else ''
                print(f"  Slot time (parsed): {slot_part}")
            except:
                pass
    
    print(f"  Range lock events: {len(range_locks)}")
    if range_locks:
        for lock in range_locks[-3:]:
            print(f"    {lock.get('ts_utc', '')[:19]} - {lock.get('event', 'N/A')}")
    
    print(f"  Range compute events: {len(range_computes)}")
    if range_computes:
        latest_compute = max(range_computes, key=lambda x: x.get('ts_utc', ''))
        compute_data = latest_compute.get('data', {})
        print(f"    Latest: {latest_compute.get('ts_utc', '')[:19]} - {latest_compute.get('event', 'N/A')}")
        if compute_data.get('success') == False:
            print(f"      Failed: {compute_data.get('reason', 'N/A')}")
    
    # Check if stuck in RANGE_BUILDING past slot time
    if latest_state == 'RANGE_BUILDING':
        print(f"  WARNING: Still in RANGE_BUILDING - should have locked at slot_time")
        if latest_hydration:
            data = latest_hydration.get('data', {})
            slot_time_str = data.get('slot_time_chicago', '')
            if slot_time_str:
                print(f"  Check: Is current time past slot_time?")
                print(f"  Current UTC: {datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')}")

print(f"\n{'='*80}")
print(f"Current UTC time: {datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')}")
