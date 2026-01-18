import json
from pathlib import Path
from datetime import datetime, timedelta

log_file = Path("logs/robot/robot_ENGINE.jsonl")
es_log_file = Path("logs/robot/robot_ES.jsonl")

print("=" * 80)
print("LATEST ES LOGGING ANALYSIS (After Restart)")
print("=" * 80)
print()

# Get current time
now = datetime.now()
recent_threshold = now - timedelta(minutes=10)  # Last 10 minutes

# Read ENGINE log
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    engine_lines = f.readlines()

# Read ES log
es_events = []
if es_log_file.exists():
    with open(es_log_file, 'r', encoding='utf-8', errors='ignore') as f:
        es_lines = f.readlines()
        es_events = [json.loads(l) for l in es_lines[-50:] if l.strip()]

print(f"Analyzing last 200 ENGINE log entries and {len(es_events)} ES log entries...")
print()

# Find key events from recent restart
key_events = {
    'ENGINE_START': [],
    'TRADING_DATE_LOCKED': [],
    'STREAMS_CREATED': [],
    'OPERATOR_BANNER': [],
    'ADAPTER_SELECTED': [],
    'SIM_ACCOUNT_VERIFIED': [],
    'PLAYBACK_ACCOUNT_DETECTED': [],
    'BAR_DATE_MISMATCH': [],
    'ES-specific': []
}

for line in engine_lines[-200:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        ts_str = entry.get('ts_utc', '')
        payload = entry.get('data', {}).get('payload', {})
        instrument = payload.get('instrument', '')
        
        if event in key_events:
            key_events[event].append({
                'timestamp': ts_str,
                'payload': payload,
                'instrument': instrument
            })
        
        if instrument == 'ES' and event not in ['BAR_DATE_MISMATCH']:
            key_events['ES-specific'].append({
                'timestamp': ts_str,
                'event': event,
                'payload': payload
            })
    except:
        pass

# Print findings
print("KEY EVENTS FROM RESTART:")
print("=" * 80)

for event_type in ['ENGINE_START', 'TRADING_DATE_LOCKED', 'STREAMS_CREATED', 
                   'OPERATOR_BANNER', 'ADAPTER_SELECTED', 'SIM_ACCOUNT_VERIFIED',
                   'PLAYBACK_ACCOUNT_DETECTED']:
    events = key_events[event_type]
    if events:
        latest = events[-1]
        print(f"\n{event_type}:")
        print(f"  Timestamp: {latest['timestamp']}")
        payload = latest['payload']
        if payload:
            for key, value in list(payload.items())[:10]:
                if isinstance(value, (dict, list)):
                    value = str(value)[:100]
                print(f"    {key}: {value}")
    else:
        print(f"\n{event_type}: NOT FOUND")

# Check BAR_DATE_MISMATCH
mismatches = key_events['BAR_DATE_MISMATCH']
if mismatches:
    print(f"\n{'=' * 80}")
    print(f"BAR_DATE_MISMATCH Events: {len(mismatches)}")
    print("=" * 80)
    
    # Show first and last few
    print(f"\nFirst mismatch:")
    first = mismatches[0]
    print(f"  Timestamp: {first['timestamp']}")
    print(f"  Locked Date: {first['payload'].get('locked_trading_date', 'N/A')}")
    print(f"  Bar Date: {first['payload'].get('bar_trading_date', 'N/A')}")
    print(f"  Bar Time: {first['payload'].get('bar_timestamp_chicago', 'N/A')}")
    
    if len(mismatches) > 1:
        print(f"\nLast mismatch:")
        last = mismatches[-1]
        print(f"  Timestamp: {last['timestamp']}")
        print(f"  Locked Date: {last['payload'].get('locked_trading_date', 'N/A')}")
        print(f"  Bar Date: {last['payload'].get('bar_trading_date', 'N/A')}")
        print(f"  Bar Time: {last['payload'].get('bar_timestamp_chicago', 'N/A')}")
else:
    print(f"\n{'=' * 80}")
    print("✅ No BAR_DATE_MISMATCH events - Trading date is correct!")
    print("=" * 80)

# ES-specific events
es_specific = key_events['ES-specific']
if es_specific:
    print(f"\n{'=' * 80}")
    print(f"ES-Specific Events (last 10): {len(es_specific)}")
    print("=" * 80)
    for event in es_specific[-10:]:
        print(f"  [{event['timestamp']}] {event['event']}")
        payload = event['payload']
        if 'bar_timestamp_chicago' in payload:
            print(f"    Bar Time: {payload.get('bar_timestamp_chicago', 'N/A')}")
        if 'state' in payload:
            print(f"    State: {payload.get('state', 'N/A')}")

# Check ES log file
if es_events:
    print(f"\n{'=' * 80}")
    print(f"ES Log File Events (last 10): {len(es_events)}")
    print("=" * 80)
    for event in es_events[-10:]:
        print(f"  [{event.get('ts_utc', 'N/A')}] {event.get('event', 'N/A')}")
        payload = event.get('data', {}).get('payload', {})
        if 'state' in payload:
            print(f"    State: {payload.get('state', 'N/A')}")
        if 'bar_timestamp_chicago' in payload:
            print(f"    Bar Time: {payload.get('bar_timestamp_chicago', 'N/A')}")
