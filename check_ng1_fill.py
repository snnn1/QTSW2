#!/usr/bin/env python3
"""Check NG1 fill and protective order placement"""
import json
from pathlib import Path
from datetime import datetime
import pytz

chicago_tz = pytz.timezone('America/Chicago')

# Find all robot log files
log_files = list(Path('logs/robot').glob('robot_*.jsonl'))
events = []
for f in log_files:
    try:
        with open(f, 'r', encoding='utf-8-sig') as file:
            for line in file:
                line = line.strip()
                if line:
                    events.append(json.loads(line))
    except Exception as e:
        print(f"Error reading {f}: {e}")

# Filter NG1 events
ng1_events = []
for e in events:
    instrument = e.get('instrument', '')
    data = e.get('data', {})
    if isinstance(data, dict):
        data_str = str(data)
    else:
        data_str = str(data)
    
    if 'NG' in str(instrument) or 'NG' in data_str:
        ng1_events.append(e)

# Sort by timestamp
recent = sorted([e for e in ng1_events if e.get('ts_utc')], 
                key=lambda x: x.get('ts_utc', ''))[-30:]

print("=" * 80)
print("NG1 RECENT EVENTS (Last 30)")
print("=" * 80)

for e in reversed(recent):
    ts_utc = e.get('ts_utc', '')
    try:
        if ts_utc:
            dt = datetime.fromisoformat(ts_utc.replace('Z', '+00:00'))
            chicago_dt = dt.astimezone(chicago_tz)
            time_str = chicago_dt.strftime('%H:%M:%S')
        else:
            time_str = "NO_TIME"
    except:
        time_str = "PARSE_ERROR"
    
    event_type = e.get('event', e.get('event_type', 'NO_TYPE'))
    instrument = e.get('instrument', 'N/A')
    data = e.get('data', {})
    
    print(f"\n{time_str} CT - {event_type}")
    print(f"  Instrument: {instrument}")
    if isinstance(data, dict):
        for k, v in data.items():
            print(f"  {k}: {v}")
    else:
        print(f"  Data: {data}")

# Check for specific execution events
print("\n" + "=" * 80)
print("NG1 EXECUTION EVENTS")
print("=" * 80)

execution_events = [e for e in ng1_events 
                    if 'EXECUTION' in e.get('event', e.get('event_type', '')) or
                       'FILL' in e.get('event', e.get('event_type', '')) or
                       'INTENT' in e.get('event', e.get('event_type', ''))]

for e in reversed(sorted(execution_events, key=lambda x: x.get('ts_utc', ''))[-20:]):
    ts_utc = e.get('ts_utc', '')
    try:
        if ts_utc:
            dt = datetime.fromisoformat(ts_utc.replace('Z', '+00:00'))
            chicago_dt = dt.astimezone(chicago_tz)
            time_str = chicago_dt.strftime('%H:%M:%S')
        else:
            time_str = "NO_TIME"
    except:
        time_str = "PARSE_ERROR"
    
    event_type = e.get('event', e.get('event_type', 'NO_TYPE'))
    print(f"\n{time_str} CT - {event_type}")
    data = e.get('data', {})
    if isinstance(data, dict):
        for k, v in data.items():
            print(f"  {k}: {v}")
