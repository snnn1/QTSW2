#!/usr/bin/env python3
"""Check which streams are currently ARMED - simple version"""
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

# Get all streams
all_streams = set(e.get('stream') for e in today_events if e.get('stream'))
all_streams = sorted(all_streams)

print("="*80)
print("WHAT DOES ARMED STATE MEAN?")
print("="*80)
print("""
ARMED is a state in the stream state machine that means:

1. PRE_HYDRATION completed - Historical bars have been loaded
2. Range has been computed (if bars were available)
3. Stream is WAITING for range_start time to arrive
4. Once range_start time arrives, stream transitions to RANGE_BUILDING
5. In RANGE_BUILDING, the range is updated incrementally from live bars
6. At slot_time, range locks and stream watches for breakouts

State progression:
PRE_HYDRATION -> ARMED -> RANGE_BUILDING -> RANGE_LOCKED -> (trading or DONE)
""")

print("="*80)
print("CURRENT STREAM STATES")
print("="*80)

armed_streams = []
other_states = {}

for stream_id in all_streams:
    stream_events = [e for e in today_events if e.get('stream') == stream_id]
    
    # Get latest state from events
    state_events = [e for e in stream_events if e.get('state')]
    if not state_events:
        continue
    
    latest_state_event = max(state_events, key=lambda x: x.get('ts_utc', ''))
    latest_state = latest_state_event.get('state')
    latest_state_time = latest_state_event.get('ts_utc', '')[:19]
    
    # Check if committed
    commits = [e for e in stream_events 
              if 'COMMIT' in e.get('event', '') or 
              e.get('event') == 'STREAM_STAND_DOWN']
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

print(f"\nOTHER STATES ({len(other_states)}):")
if other_states:
    for stream_id, info in sorted(other_states.items()):
        status = "COMMITTED" if info['committed'] else "ACTIVE"
        print(f"  {stream_id}: {info['state']} ({status}, since {info['time']})")
else:
    print("  None")

print(f"\n{'='*80}")
print(f"Current UTC time: {datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')}")
