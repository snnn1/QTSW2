import json
from pathlib import Path
from datetime import datetime, timedelta

log_file = Path("logs/robot/robot_ENGINE.jsonl")

if not log_file.exists():
    print(f"Log file not found: {log_file}")
    exit(1)

print("=" * 80)
print("RECENT LOG ANALYSIS")
print("=" * 80)
print()

# Read last 100 lines
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()
    recent_lines = lines[-100:] if len(lines) > 100 else lines

print(f"Analyzing last {len(recent_lines)} log entries...")
print()

# Key events to look for
key_events = [
    'ENGINE_START',
    'TRADING_DATE_LOCKED',
    'STREAMS_CREATED',
    'OPERATOR_BANNER',
    'PLAYBACK_ACCOUNT_DETECTED',
    'SIM_ACCOUNT_VERIFIED',
    'ADAPTER_SELECTED',
    'EXECUTION_MODE_SET',
    'BAR_DATE_MISMATCH',
    'TIMETABLE_LOADED',
    'DATA_LOSS_DETECTED',
    'DATA_STALL_RECOVERED'
]

found_events = []
for line in recent_lines:
    try:
        entry = json.loads(line)
        event_type = entry.get('event', '')
        if event_type in key_events:
            found_events.append({
                'timestamp': entry.get('ts_utc', ''),
                'event': event_type,
                'level': entry.get('level', ''),
                'payload': entry.get('data', {}).get('payload', {})
            })
    except json.JSONDecodeError:
        continue

print(f"Found {len(found_events)} key events:")
print()

for event in found_events[-20:]:  # Show last 20
    print(f"[{event['timestamp']}] {event['event']} ({event['level']})")
    payload = event['payload']
    if payload:
        # Print key fields
        for key in ['trading_date', 'account_name', 'environment', 'mode', 'adapter', 
                    'session', 'bar_timestamp_chicago', 'bar_time_of_day_chicago',
                    'earliest_session_range_start', 'note', 'error']:
            if key in payload:
                value = payload[key]
                if isinstance(value, dict):
                    value = json.dumps(value)[:100]
                print(f"  {key}: {value}")
    print()

# Check for playback-specific events
playback_events = [e for e in found_events if 'PLAYBACK' in e['event'] or 
                   (e['payload'] and 'Playback101' in str(e['payload']))]
if playback_events:
    print("=" * 80)
    print("PLAYBACK-RELATED EVENTS:")
    print("=" * 80)
    for event in playback_events:
        print(f"[{event['timestamp']}] {event['event']}")
        print(f"  Payload: {json.dumps(event['payload'], indent=2)}")
        print()
else:
    print("=" * 80)
    print("NO PLAYBACK-RELATED EVENTS FOUND")
    print("=" * 80)
    print()
