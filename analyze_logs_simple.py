import json
from pathlib import Path

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("LOG ANALYSIS - Key Events")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    all_lines = f.readlines()

# Find key events
key_events = {}
for line in all_lines:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        if event in ['ENGINE_START', 'TRADING_DATE_LOCKED', 'STREAMS_CREATED', 
                     'OPERATOR_BANNER', 'PLAYBACK_ACCOUNT_DETECTED', 'SIM_ACCOUNT_VERIFIED',
                     'ADAPTER_SELECTED', 'EXECUTION_MODE_SET']:
            if event not in key_events:
                key_events[event] = []
            key_events[event].append({
                'timestamp': entry.get('ts_utc', ''),
                'payload': entry.get('data', {}).get('payload', {})
            })
    except:
        pass

# Print most recent of each event type
for event_type in ['ENGINE_START', 'TRADING_DATE_LOCKED', 'STREAMS_CREATED', 
                   'OPERATOR_BANNER', 'ADAPTER_SELECTED', 'EXECUTION_MODE_SET',
                   'SIM_ACCOUNT_VERIFIED', 'PLAYBACK_ACCOUNT_DETECTED']:
    if event_type in key_events and len(key_events[event_type]) > 0:
        latest = key_events[event_type][-1]
        print(f"\n{event_type}:")
        print(f"  Timestamp: {latest['timestamp']}")
        payload = latest['payload']
        if payload:
            for key, value in list(payload.items())[:10]:
                print(f"  {key}: {value}")
    else:
        print(f"\n{event_type}: NOT FOUND")

# Check recent BAR_DATE_MISMATCH
print("\n" + "=" * 80)
print("Recent BAR_DATE_MISMATCH Events:")
print("=" * 80)
mismatches = []
for line in all_lines[-200:]:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            mismatches.append({
                'timestamp': entry.get('ts_utc', ''),
                'locked_date': payload.get('locked_trading_date', ''),
                'bar_date': payload.get('bar_trading_date', ''),
                'bar_time': payload.get('bar_timestamp_chicago', '')
            })
    except:
        pass

if mismatches:
    print(f"\nFound {len(mismatches)} BAR_DATE_MISMATCH events (showing last 5):")
    for m in mismatches[-5:]:
        print(f"  [{m['timestamp']}] Locked: {m['locked_date']}, Bar: {m['bar_date']}, Bar Time: {m['bar_time']}")
else:
    print("\nNo BAR_DATE_MISMATCH events found")
