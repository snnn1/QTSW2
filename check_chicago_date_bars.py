import json
from pathlib import Path
from datetime import datetime
import re

log_file = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("CHECKING FOR BARS FROM 2026-01-16 (CHICAGO TIME)")
print("=" * 80)
print()

with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

# Find restart
restart_time = None
trading_date = None

for line in reversed(lines):
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            restart_time = entry.get('ts_utc', '')
            trading_date = entry.get('data', {}).get('payload', {}).get('trading_date')
            break
    except:
        pass

print(f"Trading Date: {trading_date}")
print(f"Restart Time: {restart_time}")
print()

# Look for bars where bar_timestamp_chicago shows 2026-01-16
bars_from_16_chicago = []
found_restart = False

for line in lines:
    try:
        entry = json.loads(line)
        ts = entry.get('ts_utc', '')
        
        if entry.get('event') == 'TRADING_DATE_LOCKED' and ts == restart_time:
            found_restart = True
            continue
        
        if found_restart:
            payload = entry.get('data', {}).get('payload', {})
            bar_time_chicago = payload.get('bar_timestamp_chicago', '')
            
            # Check if bar_timestamp_chicago shows 2026-01-16
            # Format: 2026-01-16T12:25:00.0000000-06:00
            if bar_time_chicago and '2026-01-16T' in bar_time_chicago:
                # Extract the date part
                date_match = re.search(r'2026-01-16T(\d{2}):(\d{2})', bar_time_chicago)
                if date_match:
                    bars_from_16_chicago.append({
                        'timestamp': ts,
                        'event': entry.get('event', ''),
                        'bar_time_chicago': bar_time_chicago,
                        'bar_time_utc': payload.get('bar_timestamp_utc', ''),
                        'bar_date_field': payload.get('bar_trading_date', ''),
                        'locked_date': payload.get('locked_trading_date', ''),
                        'hour': date_match.group(1),
                        'minute': date_match.group(2)
                    })
    except:
        pass

print(f"Found {len(bars_from_16_chicago)} bars with 2026-01-16 in Chicago timestamp")
print()

if bars_from_16_chicago:
    # Group by event type
    by_event = defaultdict(list)
    for bar in bars_from_16_chicago:
        by_event[bar['event']].append(bar)
    
    print("Bars from 2026-01-16 (Chicago time) by event type:")
    for event_type, events in sorted(by_event.items()):
        print(f"  {event_type}: {len(events)}")
    
    # Check if any are being processed
    processed = [b for b in bars_from_16_chicago if 'MISMATCH' not in b['event']]
    rejected = [b for b in bars_from_16_chicago if 'MISMATCH' in b['event']]
    
    print(f"\nProcessed: {len(processed)}")
    print(f"Rejected: {len(rejected)}")
    
    if rejected:
        print(f"\nFirst 5 rejected bars:")
        for i, bar in enumerate(rejected[:5], 1):
            print(f"\n  {i}. [{bar['timestamp']}]")
            print(f"     Bar Time (Chicago): {bar['bar_time_chicago']}")
            print(f"     Bar Date Field: {bar['bar_date_field']}")
            print(f"     Locked Date: {bar['locked_date']}")
            
            if bar['bar_date_field'] == bar['locked_date']:
                print(f"     *** BUG: Bar date matches locked date but rejected! ***")
            else:
                print(f"     Bar date field ({bar['bar_date_field']}) != Locked date ({bar['locked_date']})")
                print(f"     But Chicago timestamp shows 2026-01-16 - possible date extraction bug")
    
    if processed:
        print(f"\nFirst 5 processed bars:")
        for i, bar in enumerate(processed[:5], 1):
            print(f"  {i}. [{bar['timestamp']}] {bar['event']}")
            print(f"     Bar Time (Chicago): {bar['bar_time_chicago']}")
else:
    print("No bars found with 2026-01-16 in Chicago timestamp")
    print("This means bars from 2026-01-16 (Chicago time) are not arriving yet")

# Check time range of bars
print(f"\n{'=' * 80}")
print("BAR TIME RANGE ANALYSIS:")
print("=" * 80)

if bars_from_16_chicago:
    times = [bar['hour'] + ':' + bar['minute'] for bar in bars_from_16_chicago]
    print(f"\nBar times from 2026-01-16 (Chicago):")
    print(f"  First: {times[0]}")
    print(f"  Last: {times[-1]}")
    print(f"  Total: {len(times)} bars")
    
    # Check if these are overnight bars (before 02:00)
    early_bars = [t for t in times if int(t.split(':')[0]) < 2]
    if early_bars:
        print(f"\n  Early bars (< 02:00): {len(early_bars)}")
        print(f"  These are overnight bars from previous day's session")
        print(f"  They should be ignored (correct behavior)")
