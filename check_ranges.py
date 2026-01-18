import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

log_file = Path("logs/robot/robot_ES.jsonl")
engine_log = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("RANGE ANALYSIS FOR 2026-01-16")
print("=" * 80)
print()

if not log_file.exists():
    print(f"ES log file not found: {log_file}")
    exit(1)

# Read ES log
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    es_lines = f.readlines()

print(f"Total ES log lines: {len(es_lines)}")
print(f"Checking last 1000 lines...")
print()

# Find range-related events
range_events = {
    'RANGE_COMPUTE_START': [],
    'RANGE_COMPUTE_FAILED': [],
    'RANGE_COMPUTE_NO_BARS_DIAGNOSTIC': [],
    'RANGE_LOCKED': [],
    'RANGE_COMPUTE_COMPLETE': [],
    'RANGE_INVALIDATED': [],
    'BAR_RECEIVED': [],
    'STREAM_STATE': []
}

# Track stream states
stream_states = defaultdict(list)
bar_counts = defaultdict(int)
range_info = {}

for line in es_lines[-1000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        stream_id = payload.get('stream_id', payload.get('stream', 'UNKNOWN'))
        
        if event in range_events:
            range_events[event].append({
                'timestamp': entry.get('ts_utc', ''),
                'stream_id': stream_id,
                'payload': payload
            })
        
        # Track stream states
        if 'state' in payload:
            stream_states[stream_id].append({
                'timestamp': entry.get('ts_utc', ''),
                'state': payload.get('state', ''),
                'event': event
            })
        
        # Count bars received
        if event == 'BAR_RECEIVED' or 'bar' in event.lower():
            bar_counts[stream_id] += 1
        
        # Track range info
        if event == 'RANGE_LOCKED':
            range_info[stream_id] = {
                'timestamp': entry.get('ts_utc', ''),
                'range_low': payload.get('range_low', 'N/A'),
                'range_high': payload.get('range_high', 'N/A'),
                'range_start_utc': payload.get('range_start_utc', 'N/A'),
                'range_end_utc': payload.get('range_end_utc', 'N/A'),
                'bar_count': payload.get('bar_count', 'N/A')
            }
    except:
        pass

# Print findings
print("RANGE COMPUTATION EVENTS:")
print("=" * 80)

for event_type in ['RANGE_COMPUTE_START', 'RANGE_COMPUTE_FAILED', 
                   'RANGE_COMPUTE_NO_BARS_DIAGNOSTIC', 'RANGE_LOCKED', 
                   'RANGE_COMPUTE_COMPLETE', 'RANGE_INVALIDATED']:
    events = range_events[event_type]
    if events:
        print(f"\n{event_type}: {len(events)} occurrence(s)")
        # Group by stream
        by_stream = defaultdict(list)
        for e in events:
            by_stream[e['stream_id']].append(e)
        
        for stream_id, stream_events in by_stream.items():
            print(f"  Stream: {stream_id} ({len(stream_events)} events)")
            latest = stream_events[-1]
            payload = latest['payload']
            if event_type == 'RANGE_LOCKED':
                print(f"    Range Low: {payload.get('range_low', 'N/A')}")
                print(f"    Range High: {payload.get('range_high', 'N/A')}")
                print(f"    Bar Count: {payload.get('bar_count', 'N/A')}")
                print(f"    Range Start: {payload.get('range_start_utc', 'N/A')}")
                print(f"    Range End: {payload.get('range_end_utc', 'N/A')}")
            elif event_type == 'RANGE_COMPUTE_FAILED':
                print(f"    Reason: {payload.get('reason', 'N/A')}")
            elif event_type == 'RANGE_COMPUTE_NO_BARS_DIAGNOSTIC':
                print(f"    Trading Date: {payload.get('trading_date', 'N/A')}")
                print(f"    Range Window: {payload.get('range_window_chicago', 'N/A')}")
                print(f"    Bar Buffer Count: {payload.get('bar_buffer_count', 'N/A')}")
    else:
        print(f"\n{event_type}: None")

# Show current stream states
print(f"\n{'=' * 80}")
print("CURRENT STREAM STATES:")
print("=" * 80)

for stream_id, states in stream_states.items():
    if states:
        latest_state = states[-1]
        print(f"\n{stream_id}:")
        print(f"  Current State: {latest_state['state']}")
        print(f"  Last Update: {latest_state['timestamp']}")
        print(f"  Event: {latest_state['event']}")
        print(f"  Bars Received: {bar_counts.get(stream_id, 0)}")
        
        # Show range info if available
        if stream_id in range_info:
            info = range_info[stream_id]
            print(f"  Range Locked:")
            print(f"    Low: {info['range_low']}")
            print(f"    High: {info['range_high']}")
            print(f"    Bars: {info['bar_count']}")

# Check for bars from 2026-01-16
print(f"\n{'=' * 80}")
print("BAR ANALYSIS:")
print("=" * 80)

# Read ENGINE log to see bar dates
with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
    engine_lines = f.readlines()

bars_2026_01_16 = []
bars_other = []

for line in engine_lines[-500:]:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            bar_date = payload.get('bar_trading_date', '')
            if bar_date == '2026-01-16':
                bars_2026_01_16.append(entry)
            else:
                bars_other.append(entry)
    except:
        pass

print(f"\nBars from 2026-01-16 (should be processed): {len(bars_2026_01_16)}")
print(f"Bars from other dates (rejected): {len(bars_other)}")

if bars_2026_01_16:
    print(f"\nFirst bar from 2026-01-16:")
    first = bars_2026_01_16[0]
    payload = first.get('data', {}).get('payload', {})
    print(f"  Timestamp: {first.get('ts_utc', 'N/A')}")
    print(f"  Bar Time: {payload.get('bar_timestamp_chicago', 'N/A')}")
