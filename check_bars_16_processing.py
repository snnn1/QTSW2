import json
from pathlib import Path
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")
es_log_file = Path("logs/robot/robot_ES.jsonl")

print("=" * 80)
print("CHECKING IF BARS FROM 2026-01-16 ARE ARRIVING BUT NOT PROCESSED")
print("=" * 80)
print()

# Find restart time
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

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

print(f"Restart Time: {restart_time}")
print(f"Trading Date: {trading_date}")
print()

# Check for bars from 2026-01-16 in timestamps (not just bar_trading_date field)
print("Checking for bars with timestamps from 2026-01-16...")
print()

bars_with_16_timestamp = []
found_restart = False

for line in lines:
    try:
        entry = json.loads(line)
        ts = entry.get('ts_utc', '')
        
        if entry.get('event') == 'TRADING_DATE_LOCKED' and ts == restart_time:
            found_restart = True
            continue
        
        if found_restart:
            # Check if this event mentions 2026-01-16 in any timestamp field
            payload = entry.get('data', {}).get('payload', {})
            bar_time_chicago = payload.get('bar_timestamp_chicago', '')
            bar_time_utc = payload.get('bar_timestamp_utc', '')
            
            if '2026-01-16' in bar_time_chicago or '2026-01-16' in bar_time_utc:
                bars_with_16_timestamp.append({
                    'timestamp': ts,
                    'event': entry.get('event', ''),
                    'bar_time_chicago': bar_time_chicago,
                    'bar_time_utc': bar_time_utc,
                    'bar_date_field': payload.get('bar_trading_date', ''),
                    'locked_date': payload.get('locked_trading_date', ''),
                    'payload': payload
                })
    except:
        pass

print(f"Found {len(bars_with_16_timestamp)} events with 2026-01-16 in bar timestamps")
print()

if bars_with_16_timestamp:
    # Group by event type
    by_event = defaultdict(list)
    for bar in bars_with_16_timestamp:
        by_event[bar['event']].append(bar)
    
    print("Events by type:")
    for event_type, events in sorted(by_event.items()):
        print(f"  {event_type}: {len(events)}")
    
    # Check BAR_DATE_MISMATCH events
    mismatches = by_event.get('BAR_DATE_MISMATCH', [])
    if mismatches:
        print(f"\nBAR_DATE_MISMATCH events with 2026-01-16 timestamps: {len(mismatches)}")
        print("\nFirst 5:")
        for i, m in enumerate(mismatches[:5], 1):
            print(f"\n  {i}. [{m['timestamp']}]")
            print(f"     Locked Date: {m['locked_date']}")
            print(f"     Bar Date Field: {m['bar_date_field']}")
            print(f"     Bar Time (Chicago): {m['bar_time_chicago']}")
            
            # Check if bar_date_field matches the timestamp date
            if '2026-01-16' in m['bar_time_chicago']:
                timestamp_date = '2026-01-16'
                if m['bar_date_field'] != timestamp_date:
                    print(f"     *** INCONSISTENCY: Timestamp shows {timestamp_date} but bar_date_field is {m['bar_date_field']} ***")
                elif m['locked_date'] == timestamp_date:
                    print(f"     *** BUG: Bar date matches locked date but still rejected! ***")
    
    # Check for any processed bars (not mismatches)
    processed = [b for b in bars_with_16_timestamp if 'MISMATCH' not in b['event']]
    if processed:
        print(f"\nProcessed bars (not rejected): {len(processed)}")
        for i, p in enumerate(processed[:5], 1):
            print(f"  {i}. [{p['timestamp']}] {p['event']}")
    else:
        print(f"\nNo processed bars found - all bars with 2026-01-16 timestamps are being rejected")

# Check ES log for any processing
if es_log_file.exists():
    print(f"\n{'=' * 80}")
    print("CHECKING ES LOG FOR PROCESSING:")
    print("=" * 80)
    
    with open(es_log_file, 'r', encoding='utf-8', errors='ignore') as f:
        es_lines = f.readlines()
    
    # Find events after restart
    es_events_16 = []
    found_restart_es = False
    
    for line in es_lines:
        try:
            entry = json.loads(line)
            ts = entry.get('ts_utc', '')
            
            # Check if we're past the restart time
            if ts >= restart_time:
                found_restart_es = True
            
            if found_restart_es:
                # Look for any mention of 2026-01-16
                entry_str = json.dumps(entry)
                if '2026-01-16' in entry_str:
                    payload = entry.get('data', {}).get('payload', {})
                    es_events_16.append({
                        'timestamp': ts,
                        'event': entry.get('event', ''),
                        'stream': payload.get('stream_id', payload.get('stream', 'UNKNOWN'))
                    })
        except:
            pass
    
    print(f"\nES log events mentioning 2026-01-16 (after restart): {len(es_events_16)}")
    
    if es_events_16:
        # Group by event type
        by_event_es = defaultdict(int)
        for e in es_events_16:
            by_event_es[e['event']] += 1
        
        print("\nEvent types:")
        for event_type, count in sorted(by_event_es.items()):
            print(f"  {event_type}: {count}")
        
        # Check for bar processing
        bar_events = [e for e in es_events_16 if 'BAR' in e['event']]
        if bar_events:
            print(f"\nBar processing events: {len(bar_events)}")
            print("Bars from 2026-01-16 ARE being processed in streams!")
        else:
            print(f"\nNo bar processing events found")
    else:
        print(f"\nNo ES log events mentioning 2026-01-16 after restart")

# Final summary
print(f"\n{'=' * 80}")
print("FINAL SUMMARY:")
print("=" * 80)

if trading_date:
    print(f"\nTrading Date: {trading_date}")
    
    bars_from_16_in_timestamps = len([b for b in bars_with_16_timestamp if '2026-01-16' in b.get('bar_time_chicago', '')])
    bars_from_16_processed = len([b for b in bars_with_16_timestamp if 'MISMATCH' not in b['event']])
    
    print(f"\nBars with 2026-01-16 timestamps: {bars_from_16_in_timestamps}")
    print(f"Bars from 2026-01-16 processed: {bars_from_16_processed}")
    
    if bars_from_16_in_timestamps > 0 and bars_from_16_processed == 0:
        print(f"\nCONCLUSION: Bars from 2026-01-16 ARE arriving but ALL are being rejected")
        print(f"  This indicates a bug in the date comparison logic")
    elif bars_from_16_in_timestamps == 0:
        print(f"\nCONCLUSION: No bars from 2026-01-16 are arriving yet")
        print(f"  System is correctly waiting for bars from {trading_date}")
    else:
        print(f"\nCONCLUSION: Bars from 2026-01-16 are being processed correctly")
