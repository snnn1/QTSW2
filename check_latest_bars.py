import json
from pathlib import Path
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")
es_log_file = Path("logs/robot/robot_ES.jsonl")

print("=" * 80)
print("LATEST BAR PROCESSING CHECK")
print("=" * 80)
print()

# Read last 500 lines
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    lines = f.readlines()

print(f"Checking last 500 ENGINE log lines...")
print()

# Find trading date locked
trading_date = None
for line in lines[-500:]:
    try:
        entry = json.loads(line)
        if entry.get('event') == 'TRADING_DATE_LOCKED':
            payload = entry.get('data', {}).get('payload', {})
            trading_date = payload.get('trading_date')
            source = payload.get('source', 'N/A')
            print(f"TRADING_DATE_LOCKED:")
            print(f"  Trading Date: {trading_date}")
            print(f"  Source: {source}")
            print(f"  Timestamp: {entry.get('ts_utc', 'N/A')}")
            print()
            break
    except:
        pass

if not trading_date:
    print("WARNING: Trading date not found in recent logs")
    print()

# Check bar processing
bars_by_date = defaultdict(int)
bars_processed = 0
bars_rejected = 0

for line in lines[-500:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        if event == 'BAR_DATE_MISMATCH':
            bars_rejected += 1
            bar_date = payload.get('bar_trading_date', 'UNKNOWN')
            bars_by_date[bar_date] += 1
        elif 'BAR' in event and 'RECEIVED' in event:
            bars_processed += 1
    except:
        pass

print(f"BAR PROCESSING:")
print(f"  Bars Rejected: {bars_rejected}")
print(f"  Bars Processed: {bars_processed}")
print()

if bars_by_date:
    print(f"Rejected bars by date:")
    for date, count in sorted(bars_by_date.items()):
        match = " (MATCHES)" if date == trading_date else " (WRONG DATE)"
        print(f"  {date}: {count} bars{match}")

# Check ES log for bars being processed
if es_log_file.exists():
    print(f"\n{'=' * 80}")
    print("ES LOG - BAR PROCESSING:")
    print("=" * 80)
    
    with open(es_log_file, 'r', encoding='utf-8', errors='ignore') as f:
        es_lines = f.readlines()
    
    # Check for bars received in ES streams
    bars_in_streams = []
    range_locked_recent = []
    
    for line in es_lines[-1000:]:
        try:
            entry = json.loads(line)
            event = entry.get('event', '')
            payload = entry.get('data', {}).get('payload', {})
            
            if 'BAR' in event and 'RECEIVED' in event:
                bars_in_streams.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'stream': payload.get('stream_id', payload.get('stream', 'UNKNOWN')),
                    'event': event
                })
            
            if event == 'RANGE_LOCKED':
                range_locked_recent.append({
                    'timestamp': entry.get('ts_utc', ''),
                    'payload': payload
                })
        except:
            pass
    
    print(f"\nBars received in ES streams: {len(bars_in_streams)}")
    if bars_in_streams:
        print(f"  Latest: {bars_in_streams[-1]['timestamp']}")
        print(f"  Stream: {bars_in_streams[-1]['stream']}")
        print(f"  Event: {bars_in_streams[-1]['event']}")
    
    print(f"\nRanges locked (recent): {len(range_locked_recent)}")
    if range_locked_recent:
        latest = range_locked_recent[-1]
        payload = latest['payload']
        print(f"  Timestamp: {latest['timestamp']}")
        print(f"  Range Low: {payload.get('range_low', 'N/A')}")
        print(f"  Range High: {payload.get('range_high', 'N/A')}")
    
    # Check for range compute failures with diagnostics
    print(f"\n{'=' * 80}")
    print("RANGE COMPUTE DIAGNOSTICS:")
    print("=" * 80)
    
    diagnostics = []
    for line in es_lines[-500:]:
        try:
            entry = json.loads(line)
            if entry.get('event') == 'RANGE_COMPUTE_NO_BARS_DIAGNOSTIC':
                diagnostics.append(entry)
        except:
            pass
    
    if diagnostics:
        latest = diagnostics[-1]
        payload = latest.get('data', {}).get('payload', {})
        print(f"\nLatest diagnostic:")
        print(f"  Expected Trading Date: {payload.get('expected_trading_date', 'N/A')}")
        print(f"  Bar Buffer Count: {payload.get('bar_buffer_count', 'N/A')}")
        print(f"  Bar Buffer Date Range: {payload.get('bar_buffer_date_range', 'N/A')}")
        print(f"  Bars From Wrong Date: {payload.get('bars_from_wrong_date', 'N/A')}")
        print(f"  Bars From Correct Date: {payload.get('bars_from_correct_date', 'N/A')}")
        print(f"  Note: {payload.get('note', 'N/A')}")
    else:
        print("\nNo RANGE_COMPUTE_NO_BARS_DIAGNOSTIC events found")

# Final summary
print(f"\n{'=' * 80}")
print("FINAL SUMMARY:")
print("=" * 80)

if trading_date:
    print(f"\nTrading Date: {trading_date}")
    if bars_rejected > 0:
        bars_from_other = sum(count for date, count in bars_by_date.items() if date != trading_date)
        print(f"Bars from other dates rejected: {bars_from_other}")
    
    if bars_processed > 0 or len(bars_in_streams) > 0:
        print(f"Bars from {trading_date} being processed: YES")
        print(f"  ENGINE log: {bars_processed} bars")
        print(f"  ES log: {len(bars_in_streams)} bars")
    else:
        print(f"Bars from {trading_date} being processed: NO (waiting for bars from correct date)")
        print(f"  All bars received so far are from other dates and correctly rejected")
else:
    print("\nTrading date not found - check if engine started correctly")
