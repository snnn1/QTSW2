#!/usr/bin/env python3
"""Check if breakouts are being detected"""
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
print("BREAKOUT DETECTION AND ORDER SUBMISSION CHECK")
print("="*80)
print("""
For orders to be submitted:
1. Stream must be in RANGE_LOCKED state
2. Breakout must be detected (price > range_high OR price < range_low)
3. Execution gates must pass (no violations)
4. Stream must not be committed
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
    
    # Check if committed
    commits = [e for e in stream_events 
              if 'COMMIT' in e.get('event', '') or 
              e.get('event') == 'STREAM_STAND_DOWN' or
              'NO_TRADE' in e.get('event', '')]
    is_committed = len(commits) > 0
    
    # Get range info
    hydration_summaries = [e for e in stream_events if e.get('event') == 'HYDRATION_SUMMARY']
    latest_hydration = max(hydration_summaries, key=lambda x: x.get('ts_utc', '')) if hydration_summaries else None
    
    # Check for breakout detection events
    breakouts = [e for e in stream_events 
                if 'BREAKOUT' in e.get('event', '') and 
                'DETECTED' in e.get('event', '')]
    
    # Check for execution gate violations
    gate_violations = [e for e in stream_events 
                      if 'EXECUTION_GATE' in e.get('event', '') and 
                      'VIOLATION' in e.get('event', '')]
    
    # Check for order submission attempts
    order_attempts = [e for e in stream_events 
                     if 'ORDER' in e.get('event', '') or 
                     'SUBMIT' in e.get('event', '')]
    
    print(f"\n{stream_id}:")
    print(f"  State: {latest_state} (since {latest_state_time})")
    print(f"  Committed: {is_committed}")
    
    if latest_hydration:
        data = latest_hydration.get('data', {})
        range_high = data.get('reconstructed_range_high')
        range_low = data.get('reconstructed_range_low')
        print(f"  Range: [{range_low}, {range_high}]")
    
    print(f"  Breakout detection events: {len(breakouts)}")
    if breakouts:
        for b in breakouts[-3:]:
            b_data = b.get('data', {})
            print(f"    {b.get('ts_utc', '')[:19]} - {b.get('event', 'N/A')}")
            if isinstance(b_data, dict):
                print(f"      Direction: {b_data.get('direction', 'N/A')}")
                print(f"      Price: {b_data.get('breakout_price', 'N/A')}")
    
    print(f"  Execution gate violations: {len(gate_violations)}")
    if gate_violations:
        latest_violation = max(gate_violations, key=lambda x: x.get('ts_utc', ''))
        v_data = latest_violation.get('data', {})
        print(f"    Latest: {latest_violation.get('ts_utc', '')[:19]}")
        if isinstance(v_data, dict):
            print(f"      Reason: {v_data.get('reason', 'N/A')}")
            print(f"      Failed gates: {v_data.get('failed_gates', 'N/A')}")
    
    print(f"  Order submission attempts: {len(order_attempts)}")
    if order_attempts:
        for o in order_attempts[-3:]:
            print(f"    {o.get('ts_utc', '')[:19]} - {o.get('event', 'N/A')}")
    
    # Summary
    can_trade = (
        latest_state == 'RANGE_LOCKED' and
        not is_committed and
        len(breakouts) > 0
    )
    
    print(f"  Can trade: {can_trade}")
    if not can_trade:
        reasons = []
        if latest_state != 'RANGE_LOCKED':
            reasons.append(f"State={latest_state}")
        if is_committed:
            reasons.append("COMMITTED")
        if len(breakouts) == 0:
            reasons.append("No breakout detected")
        print(f"    Blocking: {', '.join(reasons)}")

print(f"\n{'='*80}")
print(f"Current UTC time: {datetime.utcnow().strftime('%Y-%m-%d %H:%M:%S')}")
