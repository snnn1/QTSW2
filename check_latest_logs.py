import json
from pathlib import Path
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING MOST RECENT LOG ENTRIES")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Get last 100 lines
recent_lines = lines[-100:] if len(lines) > 100 else lines

print(f"Checking last {len(recent_lines)} log lines...")
print()

events = []
for line in recent_lines:
    try:
        entry = json.loads(line)
        events.append({
            'timestamp': entry.get('ts_utc', ''),
            'event': entry.get('event', ''),
            'payload': entry.get('data', {}).get('payload', {})
        })
    except:
        pass

print("Most recent 30 events:")
print("-" * 80)

for i, evt in enumerate(events[-30:], 1):
    timestamp = evt['timestamp'][:19] if len(evt['timestamp']) > 19 else evt['timestamp']
    event_type = evt['event']
    
    # Extract bar info if available
    bar_info = ""
    payload = evt['payload']
    if 'bar_timestamp_chicago' in payload:
        bar_time = payload['bar_timestamp_chicago']
        if isinstance(bar_time, str):
            bar_info = f" | Bar: {bar_time[:19]}"
    elif 'bar_timestamp_utc' in payload:
        bar_time = payload['bar_timestamp_utc']
        if isinstance(bar_time, str):
            bar_info = f" | Bar UTC: {bar_time[:19]}"
    
    print(f"{i:2}. [{timestamp}] {event_type:30}{bar_info}")

# Check specifically for bars
print(f"\n{'=' * 80}")
print("BAR EVENTS IN RECENT LOGS:")
print("=" * 80)

bar_events = [e for e in events if 'BAR' in e['event']]
if bar_events:
    print(f"Found {len(bar_events)} bar events")
    print()
    for i, evt in enumerate(bar_events[-10:], 1):
        timestamp = evt['timestamp'][:19]
        event_type = evt['event']
        payload = evt['payload']
        
        bar_chicago = payload.get('bar_timestamp_chicago', '')
        bar_utc = payload.get('bar_timestamp_utc', '')
        bar_date = payload.get('bar_trading_date', '')
        
        print(f"{i}. [{timestamp}] {event_type}")
        if bar_chicago:
            print(f"   Bar Chicago: {bar_chicago[:19]}")
        if bar_utc:
            print(f"   Bar UTC: {bar_utc[:19]}")
        if bar_date:
            print(f"   Bar Date: {bar_date}")
        print()
else:
    print("No bar events found in recent logs")

# Check log file modification time
import os
mtime = os.path.getmtime(log_file)
last_modified = datetime.fromtimestamp(mtime)
print(f"\n{'=' * 80}")
print(f"Log file last modified: {last_modified}")
print("=" * 80)
