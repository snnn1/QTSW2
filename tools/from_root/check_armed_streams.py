#!/usr/bin/env python3
"""Check which streams are currently ARMED"""
import json
from pathlib import Path
from datetime import datetime

# Read timetable to get enabled streams
timetable_path = Path("data/timetable/timetable_current.json")
enabled_streams = []
if timetable_path.exists():
    timetable = json.loads(timetable_path.read_text())
    enabled_streams = [s.get('stream') for s in timetable.get('streams', []) if s.get('enabled')]

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
print("CURRENT STREAM STATES")
print("="*80)

# Get all unique streams from events
all_streams = set(e.get('stream') for e in today_events if e.get('stream'))
all_streams = sorted([s for s in all_streams if s in enabled_streams])

armed_streams = []
other_states = {}

for stream_id in all_streams:
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    # Get latest state from events that have a 'state' field
    state_events = [e for e in stream_events if e.get('state')]
    if not state_events:
        continue
    
    latest_state_event = max(state_events, key=lambda x: x.get('ts_utc', ''))
    latest_state = latest_state_event.get('state')
    latest_state_time = latest_state_event.get('ts_utc', '')[:19]
    
    # Check if stream is committed
    commits = [e for e in stream_events 
              if 'COMMIT' in e.get('event', '') or 
              e.get('event') == 'STREAM_STAND_DOWN' or
              e.get('event') == 'NO_TRADE']
    is_committed = len(commits) > 0
    
    if latest_state == 'ARMED':
        armed_streams.append({
            'stream': stream_id,
            'time': latest_state_time,
            'committed': is_committed
        })
    else:
        other_states[stream_id] = {
            'state': latest_state,
            'time': latest_state_time,
            'committed': is_committed
        }

print(f"\nARMED STREAMS ({len(armed_streams)}):")
if armed_streams:
    for s in armed_streams:
        status = "COMMITTED" if s['committed'] else "ACTIVE"
        print(f"  {s['stream']}: {status} (since {s['time']})")
else:
    print("  None")

print(f"\nOTHER STATES:")
if other_states:
    for stream_id, info in sorted(other_states.items()):
        status = "COMMITTED" if info['committed'] else "ACTIVE"
        print(f"  {stream_id}: {info['state']} ({status}, since {info['time']})")
else:
    print("  None")

# Get latest HYDRATION_SUMMARY for armed streams
print(f"\n{'='*80}")
print("ARMED STREAMS DETAILS")
print("="*80)

for s in armed_streams:
    stream_id = s['stream']
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    hydration_summaries = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    if hydration_summaries:
        latest_hydration = max(hydration_summaries, key=lambda x: x.get('ts_utc', ''))
        data = latest_hydration.get('data', {})
        
        print(f"\n{stream_id}:")
        print(f"  Bars loaded: {data.get('loaded_bars', 'N/A')}")
        print(f"  Range high: {data.get('reconstructed_range_high', 'N/A')}")
        print(f"  Range low: {data.get('reconstructed_range_low', 'N/A')}")
        print(f"  Completeness: {data.get('completeness_pct', 'N/A')}%")
        print(f"  Late start: {data.get('late_start', 'N/A')}")
        print(f"  Missed breakout: {data.get('missed_breakout', 'N/A')}")

print(f"\n{'='*80}")
print(f"Current UTC time: {datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')}")
