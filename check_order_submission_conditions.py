#!/usr/bin/env python3
"""Check if streams are ready to submit orders"""
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
print("ORDER SUBMISSION CONDITIONS CHECK")
print("="*80)
print("""
For orders to be submitted, streams must:

1. Be in RANGE_LOCKED state (not RANGE_BUILDING or ARMED)
2. Have detected a breakout (price > range_high OR price < range_low)
3. Pass all execution gates (risk gates, connection state, etc.)
4. Not be committed (COMMITTED streams won't trade)

Current time is past slot_time for most streams, so they should be in RANGE_LOCKED.
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

print("\n" + "="*80)
print("STREAM STATES AND ORDER READINESS")
print("="*80)

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
    
    # Check if committed
    commits = [e for e in stream_events 
              if 'COMMIT' in e.get('event', '') or 
              e.get('event') == 'STREAM_STAND_DOWN' or
              'NO_TRADE' in e.get('event', '')]
    is_committed = len(commits) > 0
    
    # Get range info
    hydration_summaries = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    latest_hydration = max(hydration_summaries, key=lambda x: x.get('ts_utc', '')) if hydration_summaries else None
    
    # Check for breakout events
    breakouts = [e for e in stream_events if 'BREAKOUT' in e.get('event', '')]
    
    # Check for order submission events
    orders = [e for e in stream_events if 'ORDER' in e.get('event', '') or 'EXECUTION' in e.get('event', '')]
    
    print(f"\n{stream_id}:")
    print(f"  State: {latest_state} (since {latest_state_time})")
    print(f"  Committed: {is_committed}")
    
    if latest_hydration:
        data = latest_hydration.get('data', {})
        range_high = data.get('reconstructed_range_high')
        range_low = data.get('reconstructed_range_low')
        slot_time = data.get('slot_time_chicago', '')
        
        print(f"  Range: [{range_low}, {range_high}]")
        print(f"  Slot time: {slot_time}")
    
    print(f"  Breakout events: {len(breakouts)}")
    if breakouts:
        latest_breakout = max(breakouts, key=lambda x: x.get('ts_utc', ''))
        print(f"    Latest: {latest_breakout.get('ts_utc', '')[:19]} - {latest_breakout.get('event', 'N/A')}")
    
    print(f"  Order events: {len(orders)}")
    if orders:
        for order in orders[-3:]:
            print(f"    {order.get('ts_utc', '')[:19]} - {order.get('event', 'N/A')}")
    
    # Determine if ready for orders
    can_submit_orders = (
        latest_state == 'RANGE_LOCKED' and
        not is_committed and
        len(breakouts) > 0
    )
    
    print(f"  Can submit orders: {can_submit_orders}")
    if not can_submit_orders:
        reasons = []
        if latest_state != 'RANGE_LOCKED':
            reasons.append(f"State is {latest_state} (needs RANGE_LOCKED)")
        if is_committed:
            reasons.append("Stream is COMMITTED")
        if len(breakouts) == 0:
            reasons.append("No breakout detected yet")
        print(f"    Reasons: {', '.join(reasons)}")

print(f"\n{'='*80}")
print(f"Current UTC time: {datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')}")
