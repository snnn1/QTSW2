import json
from pathlib import Path
from collections import defaultdict
from datetime import datetime

log_file = Path("logs/robot/robot_ENGINE.jsonl")
es_log_file = Path("logs/robot/robot_ES.jsonl")

print("=" * 80)
print("CHECKING ALL BARS RECEIVED (Not Just Rejected)")
print("=" * 80)
print()

# Read ENGINE log - look for ALL bar-related events
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

print(f"Checking last 3000 ENGINE log lines for ALL bar events...")
print()

# Track all bars by date
bars_by_date = defaultdict(list)
trading_date_locked = None
all_bar_events = []

for line in lines[-3000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        ts = entry.get('ts_utc', '')
        
        if event == 'TRADING_DATE_LOCKED':
            trading_date_locked = payload.get('trading_date')
        
        # Look for ANY bar-related event
        if 'BAR' in event:
            all_bar_events.append({
                'timestamp': ts,
                'event': event,
                'payload': payload
            })
            
            # Try to extract bar date from various fields
            bar_date = None
            bar_time_chicago = payload.get('bar_timestamp_chicago', '')
            bar_time_utc = payload.get('bar_timestamp_utc', '')
            bar_trading_date = payload.get('bar_trading_date', '')
            
            if bar_trading_date:
                bar_date = bar_trading_date
            elif bar_time_chicago:
                # Try to extract date from timestamp string
                if '2026-01-16' in bar_time_chicago:
                    bar_date = '2026-01-16'
                elif '2026-01-15' in bar_time_chicago:
                    bar_date = '2026-01-15'
            elif bar_time_utc:
                if '2026-01-16' in bar_time_utc:
                    bar_date = '2026-01-16'
                elif '2026-01-15' in bar_time_utc:
                    bar_date = '2026-01-15'
            
            if bar_date:
                bars_by_date[bar_date].append({
                    'timestamp': ts,
                    'event': event,
                    'bar_time_chicago': bar_time_chicago,
                    'bar_time_utc': bar_time_utc
                })
    except:
        pass

print(f"Trading Date Locked: {trading_date_locked}")
print()

print(f"Total bar-related events found: {len(all_bar_events)}")
print()

# Show breakdown by date
print("Bars by Date:")
print("=" * 80)

for date in sorted(bars_by_date.keys()):
    bars = bars_by_date[date]
    print(f"\n{date}: {len(bars)} bar events")
    
    # Group by event type
    by_event = defaultdict(int)
    for bar in bars:
        by_event[bar['event']] += 1
    
    for event_type, count in by_event.items():
        print(f"  {event_type}: {count}")
    
    if bars:
        print(f"  First: {bars[0]['timestamp']}")
        print(f"  Last: {bars[-1]['timestamp']}")
        if bars[0]['bar_time_chicago']:
            print(f"  First Bar Time (Chicago): {bars[0]['bar_time_chicago']}")
        if bars[-1]['bar_time_chicago']:
            print(f"  Last Bar Time (Chicago): {bars[-1]['bar_time_chicago']}")

# Check for bars from 2026-01-16 that might be processed
print(f"\n{'=' * 80}")
print("CHECKING FOR PROCESSED BARS FROM 2026-01-16:")
print("=" * 80)

bars_2026_01_16 = bars_by_date.get('2026-01-16', [])
if bars_2026_01_16:
    print(f"\nFound {len(bars_2026_01_16)} bar events from 2026-01-16")
    
    # Check event types
    processed = [b for b in bars_2026_01_16 if 'MISMATCH' not in b['event']]
    rejected = [b for b in bars_2026_01_16 if 'MISMATCH' in b['event']]
    
    print(f"  Processed (not rejected): {len(processed)}")
    print(f"  Rejected (mismatch): {len(rejected)}")
    
    if processed:
        print(f"\nProcessed bars from 2026-01-16:")
        for bar in processed[:10]:
            print(f"  [{bar['timestamp']}] {bar['event']}")
            if bar['bar_time_chicago']:
                print(f"    Bar Time: {bar['bar_time_chicago']}")
else:
    print(f"\nNo bar events from 2026-01-16 found in ENGINE log")

# Check ES log for any bars from 2026-01-16
if es_log_file.exists():
    print(f"\n{'=' * 80}")
    print("CHECKING ES LOG FOR BARS FROM 2026-01-16:")
    print("=" * 80)
    
    with open(es_log_file, 'r', encoding='utf-8', errors='ignore') as f:
        es_lines = f.readlines()
    
    bars_in_es = []
    for line in es_lines[-5000:]:  # Check more lines
        try:
            entry = json.loads(line)
            event = entry.get('event', '')
            payload = entry.get('data', {}).get('payload', {})
            
            # Look for any mention of 2026-01-16
            entry_str = line
            if '2026-01-16' in entry_str:
                bars_in_es.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'event': event,
                    'payload': payload
                })
        except:
            pass
    
    print(f"\nFound {len(bars_in_es)} events in ES log mentioning 2026-01-16")
    if bars_in_es:
        print(f"\nFirst 10:")
        for i, bar in enumerate(bars_in_es[:10], 1):
            print(f"  {i}. [{bar['timestamp']}] {bar['event']}")
            payload = bar['payload']
            if 'bar_timestamp' in str(payload):
                print(f"     Bar timestamp in payload")
            if 'stream' in payload:
                print(f"     Stream: {payload.get('stream_id', payload.get('stream', 'N/A'))}")

# Check for OnBar calls that might not be logged
print(f"\n{'=' * 80}")
print("CHECKING FOR BAR PROCESSING WITHOUT EXPLICIT LOGS:")
print("=" * 80)

# Look for any events that might indicate bar processing
processing_indicators = []
for line in lines[-2000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        # Look for events that happen when bars are processed
        if event in ['BAR_RECEIVED_NO_STREAMS', 'RANGE_COMPUTE_START', 'RANGE_BUILDING']:
            processing_indicators.append({
                'timestamp': entry.get('ts_utc', ''),
                'event': event,
                'payload': payload
            })
    except:
        pass

print(f"\nFound {len(processing_indicators)} processing indicator events")
if processing_indicators:
    print(f"\nRecent processing indicators:")
    for ind in processing_indicators[-10:]:
        print(f"  [{ind['timestamp']}] {ind['event']}")

# Final analysis
print(f"\n{'=' * 80}")
print("FINAL ANALYSIS:")
print("=" * 80)

if trading_date_locked:
    print(f"\nTrading Date: {trading_date_locked}")
    
    bars_from_locked = bars_by_date.get(trading_date_locked, [])
    bars_from_other = sum(len(bars_by_date[d]) for d in bars_by_date.keys() if d != trading_date_locked)
    
    print(f"\nBar Events from {trading_date_locked}: {len(bars_from_locked)}")
    print(f"Bar Events from other dates: {bars_from_other}")
    
    if len(bars_from_locked) == 0:
        print(f"\nCONCLUSION: No bars from {trading_date_locked} are being received")
        print(f"  - NinjaTrader playback is only sending bars from other dates")
        print(f"  - System is correctly waiting for bars from {trading_date_locked}")
    else:
        processed_count = len([b for b in bars_from_locked if 'MISMATCH' not in b['event']])
        if processed_count > 0:
            print(f"\nCONCLUSION: Bars from {trading_date_locked} ARE being processed!")
            print(f"  - {processed_count} bars processed")
            print(f"  - Check ES log for range formation")
        else:
            print(f"\nCONCLUSION: Bars from {trading_date_locked} are arriving but being rejected")
            print(f"  - This indicates a bug - bars matching trading date should be processed")
