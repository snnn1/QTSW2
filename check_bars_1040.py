import json
from pathlib import Path
from datetime import datetime
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING RECENT BARS (AROUND 10:40)")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Get most recent events
recent_lines = lines[-500:] if len(lines) > 500 else lines

print(f"Checking last {len(recent_lines)} log lines...")
print()

# Find trading date
trading_date = None
for line in reversed(lines):
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            payload = entry.get('data', {}).get('payload', {})
            if payload.get('source') == 'TIMETABLE':
                trading_date = payload.get('trading_date')
                break
    except:
        pass

print(f"Trading Date (locked): {trading_date}")
print()

# Collect all bar events
bar_events = []
for line in recent_lines:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        ts = entry.get('ts_utc', '')
        
        if event in ['BAR_ACCEPTED', 'BAR_DATE_MISMATCH', 'ENGINE_BAR_HEARTBEAT']:
            payload = entry.get('data', {}).get('payload', {})
            bar_utc = payload.get('bar_timestamp_utc', '') or payload.get('utc_time', '')
            bar_chicago = payload.get('bar_timestamp_chicago', '') or payload.get('chicago_time', '')
            bar_date = payload.get('bar_trading_date', '')
            
            bar_events.append({
                'timestamp': ts,
                'event': event,
                'bar_utc': bar_utc,
                'bar_chicago': bar_chicago,
                'bar_date': bar_date,
                'locked_date': payload.get('locked_trading_date', ''),
                'instrument': payload.get('instrument', '')
            })
    except:
        pass

print(f"Found {len(bar_events)} bar events in recent logs")
print()

if bar_events:
    # Sort by timestamp
    bar_events.sort(key=lambda x: x['timestamp'])
    
    print("=" * 80)
    print("MOST RECENT 20 BAR EVENTS:")
    print("=" * 80)
    
    for i, bar in enumerate(bar_events[-20:], 1):
        # Extract time from Chicago timestamp
        chicago_time = bar['bar_chicago']
        time_str = chicago_time[:19] if chicago_time else 'N/A'
        
        status = "ACCEPTED" if bar['event'] == 'BAR_ACCEPTED' else "REJECTED" if bar['event'] == 'BAR_DATE_MISMATCH' else "HEARTBEAT"
        
        print(f"{i}. [{bar['timestamp'][:19]}] {bar['event']:20} | {time_str} | {status}")
        if bar['bar_date']:
            print(f"   Bar Date: {bar['bar_date']} | Locked: {bar['locked_date']}")
    
    # Check for bars around 10:40
    print(f"\n{'=' * 80}")
    print("BARS AROUND 10:40 CHICAGO TIME:")
    print("=" * 80)
    
    bars_1040 = []
    for bar in bar_events:
        chicago_time = bar['bar_chicago']
        if chicago_time and ('10:40' in chicago_time or '10:39' in chicago_time or '10:41' in chicago_time):
            bars_1040.append(bar)
    
    if bars_1040:
        print(f"Found {len(bars_1040)} bars around 10:40")
        for bar in bars_1040:
            print(f"\n  [{bar['timestamp']}] {bar['event']}")
            print(f"     Bar UTC: {bar['bar_utc']}")
            print(f"     Bar Chicago: {bar['bar_chicago']}")
            print(f"     Bar Date: {bar['bar_date']} | Locked: {bar['locked_date']}")
            print(f"     Status: {'ACCEPTED' if bar['event'] == 'BAR_ACCEPTED' else 'REJECTED'}")
    else:
        print("No bars found around 10:40")
        print("\nChecking what time range we have...")
        
        # Find time range of bars
        times = []
        for bar in bar_events:
            chicago_time = bar['bar_chicago']
            if chicago_time:
                try:
                    # Extract time part (HH:MM:SS)
                    time_part = chicago_time.split('T')[1].split('.')[0] if 'T' in chicago_time else ''
                    if time_part:
                        times.append(time_part)
                except:
                    pass
        
        if times:
            times.sort()
            print(f"  Earliest bar time: {times[0]}")
            print(f"  Latest bar time: {times[-1]}")
            print(f"  Total bars: {len(times)}")
    
    # Group by date and status
    print(f"\n{'=' * 80}")
    print("BAR SUMMARY BY DATE:")
    print("=" * 80)
    
    bars_by_date = defaultdict(lambda: {'accepted': 0, 'rejected': 0, 'heartbeat': 0})
    for bar in bar_events:
        date = bar['bar_date'] or 'UNKNOWN'
        if bar['event'] == 'BAR_ACCEPTED':
            bars_by_date[date]['accepted'] += 1
        elif bar['event'] == 'BAR_DATE_MISMATCH':
            bars_by_date[date]['rejected'] += 1
        elif bar['event'] == 'ENGINE_BAR_HEARTBEAT':
            bars_by_date[date]['heartbeat'] += 1
    
    for date in sorted(bars_by_date.keys()):
        stats = bars_by_date[date]
        match_status = "MATCHES" if date == trading_date else "WRONG DATE"
        print(f"\n{date} ({match_status}):")
        print(f"  Accepted: {stats['accepted']}")
        print(f"  Rejected: {stats['rejected']}")
        print(f"  Heartbeat: {stats['heartbeat']}")
else:
    print("No bar events found in recent logs")
