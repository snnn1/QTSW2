import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("LATEST BARS ANALYSIS")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Get last 1000 lines
recent_lines = lines[-1000:] if len(lines) > 1000 else lines

print(f"Analyzing last {len(recent_lines)} log lines...")
print()

# Find trading date
trading_date = None
for line in reversed(recent_lines):
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            trading_date = entry.get('data', {}).get('payload', {}).get('trading_date')
            if trading_date:
                break
    except:
        pass

print(f"Trading Date (locked): {trading_date}")
print()

# Analyze all bar-related events
bar_events = []
for line in recent_lines:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        ts = entry.get('ts_utc', '')
        
        if event == 'BAR_DATE_MISMATCH':
            payload = entry.get('data', {}).get('payload', {})
            bar_events.append({
                'type': 'MISMATCH',
                'timestamp': ts,
                'bar_date': payload.get('bar_trading_date', ''),
                'locked_date': payload.get('locked_trading_date', ''),
                'bar_utc': payload.get('bar_timestamp_utc', ''),
                'bar_chicago': payload.get('bar_timestamp_chicago', '')
            })
        elif event == 'BAR_RECEIVED_BEFORE_DATE_LOCKED':
            payload = entry.get('data', {}).get('payload', {})
            bar_events.append({
                'type': 'BEFORE_LOCK',
                'timestamp': ts,
                'bar_date': payload.get('bar_trading_date', ''),
                'bar_utc': payload.get('bar_timestamp_utc', ''),
                'bar_chicago': payload.get('bar_timestamp_chicago', '')
            })
        elif event == 'ENGINE_BAR_HEARTBEAT':
            payload = entry.get('data', {}).get('payload', {})
            bar_events.append({
                'type': 'HEARTBEAT',
                'timestamp': ts,
                'bar_utc': payload.get('utc_time', ''),
                'bar_chicago': payload.get('chicago_time', '')
            })
        elif 'BAR' in event and 'MISMATCH' not in event:
            bar_events.append({
                'type': 'OTHER',
                'timestamp': ts,
                'event': event
            })
    except:
        pass

print(f"Found {len(bar_events)} bar-related events")
print()

# Group by date
bars_by_date = defaultdict(list)
for event in bar_events:
    if event.get('bar_date'):
        bars_by_date[event['bar_date']].append(event)

print("=" * 80)
print("BARS BY DATE:")
print("=" * 80)

if bars_by_date:
    for date in sorted(bars_by_date.keys()):
        bars = bars_by_date[date]
        match_status = "MATCHES" if date == trading_date else "WRONG DATE"
        print(f"\n{date}: {len(bars)} bars ({match_status})")
        
        if bars:
            # Show first and last
            first = bars[0]
            last = bars[-1]
            print(f"  First bar: {first.get('bar_chicago', first.get('bar_utc', 'N/A'))}")
            print(f"  Last bar: {last.get('bar_chicago', last.get('bar_utc', 'N/A'))}")
            print(f"  First event time: {first['timestamp']}")
            print(f"  Last event time: {last['timestamp']}")
else:
    print("No bar events with dates found")

# Check for bars from trading date
if trading_date:
    matching_bars = bars_by_date.get(trading_date, [])
    print(f"\n{'=' * 80}")
    print(f"BARS FROM TRADING DATE ({trading_date}):")
    print("=" * 80)
    
    if matching_bars:
        print(f"Found {len(matching_bars)} bars from {trading_date}")
        print("\nSample bars:")
        for i, bar in enumerate(matching_bars[:5], 1):
            print(f"  {i}. {bar.get('bar_chicago', bar.get('bar_utc', 'N/A'))} - {bar['timestamp']}")
    else:
        print(f"No bars from {trading_date} found")
        print("\nCurrent bars are from:")
        for date in sorted(bars_by_date.keys()):
            print(f"  - {date}: {len(bars_by_date[date])} bars")

# Show most recent events
print(f"\n{'=' * 80}")
print("MOST RECENT 10 BAR EVENTS:")
print("=" * 80)

for i, event in enumerate(bar_events[-10:], 1):
    event_type = event.get('type', 'UNKNOWN')
    bar_date = event.get('bar_date', 'N/A')
    bar_time = event.get('bar_chicago', event.get('bar_utc', 'N/A'))
    timestamp = event.get('timestamp', 'N/A')
    
    status = ""
    if event_type == 'MISMATCH':
        status = "REJECTED" if bar_date != trading_date else "ACCEPTED"
    elif event_type == 'BEFORE_LOCK':
        status = "BEFORE_LOCK"
    elif event_type == 'HEARTBEAT':
        status = "HEARTBEAT"
    
    print(f"{i}. [{timestamp[:19]}] {event_type:12} | Date: {bar_date:12} | Time: {bar_time[:19] if isinstance(bar_time, str) else 'N/A':19} | {status}")
