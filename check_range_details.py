import json
from pathlib import Path
from collections import defaultdict

log_file = Path("logs/robot/robot_ES.jsonl")
engine_log = Path("logs/robot/robot_ENGINE.jsonl")

print("=" * 80)
print("DETAILED RANGE ANALYSIS")
print("=" * 80)
print()

# Read ES log
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    es_lines = f.readlines()

print(f"Analyzing last 2000 ES log lines...")
print()

# Track range failures with details
range_failures = []
range_locked = []
bar_received = []
stream_states = defaultdict(list)

for line in es_lines[-2000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        # Try to get stream_id from various possible fields
        stream_id = (payload.get('stream_id') or 
                    payload.get('stream') or 
                    payload.get('streamId') or 
                    'UNKNOWN')
        
        if event == 'RANGE_COMPUTE_FAILED':
            range_failures.append({
                'timestamp': entry.get('ts_utc', ''),
                'stream_id': stream_id,
                'reason': payload.get('reason', 'N/A'),
                'payload': payload
            })
        
        if event == 'RANGE_LOCKED':
            range_locked.append({
                'timestamp': entry.get('ts_utc', ''),
                'stream_id': stream_id,
                'range_low': payload.get('range_low', 'N/A'),
                'range_high': payload.get('range_high', 'N/A'),
                'bar_count': payload.get('bar_count', 'N/A'),
                'payload': payload
            })
        
        if 'BAR' in event and 'RECEIVED' in event:
            bar_received.append({
                'timestamp': entry.get('ts_utc', ''),
                'stream_id': stream_id,
                'event': event,
                'payload': payload
            })
        
        if 'state' in payload:
            stream_states[stream_id].append({
                'timestamp': entry.get('ts_utc', ''),
                'state': payload.get('state', ''),
                'event': event
            })
    except:
        pass

# Check ENGINE log for bars that match trading date
print("Checking ENGINE log for bars matching trading date 2026-01-16...")
print()

with open(engine_log, 'r', encoding='utf-8', errors='ignore') as f:
    engine_lines = f.readlines()

bars_processed = []
bars_rejected = []

for line in engine_lines[-1000:]:
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        payload = entry.get('data', {}).get('payload', {})
        
        if event == 'BAR_DATE_MISMATCH':
            bars_rejected.append({
                'timestamp': entry.get('ts_utc', ''),
                'bar_date': payload.get('bar_trading_date', 'N/A'),
                'locked_date': payload.get('locked_trading_date', 'N/A'),
                'bar_time': payload.get('bar_timestamp_chicago', 'N/A')
            })
        elif event == 'BAR_RECEIVED_NO_STREAMS':
            bars_processed.append({
                'timestamp': entry.get('ts_utc', ''),
                'event': event,
                'bar_time': payload.get('bar_timestamp_utc', 'N/A')
            })
    except:
        pass

print(f"Bars rejected (date mismatch): {len(bars_rejected)}")
print(f"Bars processed (no streams): {len(bars_processed)}")
print()

# Show range failures
print("=" * 80)
print("RANGE COMPUTE FAILURES:")
print("=" * 80)

if range_failures:
    # Group by reason
    by_reason = defaultdict(list)
    for f in range_failures:
        by_reason[f['reason']].append(f)
    
    for reason, failures in by_reason.items():
        print(f"\n{reason}: {len(failures)} failures")
        if failures:
            latest = failures[-1]
            print(f"  Latest: {latest['timestamp']}")
            print(f"  Stream: {latest['stream_id']}")
            # Show full payload for diagnostic events
            if 'RANGE_COMPUTE_NO_BARS_DIAGNOSTIC' in str(latest.get('payload', {})):
                print(f"  Full payload: {json.dumps(latest['payload'], indent=2)}")
else:
    print("No range compute failures found")

# Show range locked
print(f"\n{'=' * 80}")
print("RANGES LOCKED:")
print("=" * 80)

if range_locked:
    for r in range_locked:
        print(f"\nStream: {r['stream_id']}")
        print(f"  Timestamp: {r['timestamp']}")
        print(f"  Range Low: {r['range_low']}")
        print(f"  Range High: {r['range_high']}")
        print(f"  Bar Count: {r['bar_count']}")
        # Show full payload
        print(f"  Full payload keys: {list(r['payload'].keys())}")
else:
    print("No ranges locked")

# Show bar dates being rejected
print(f"\n{'=' * 80}")
print("BAR DATE ANALYSIS:")
print("=" * 80)

if bars_rejected:
    bar_dates = defaultdict(int)
    for b in bars_rejected:
        bar_dates[b['bar_date']] += 1
    
    print("\nBar dates being rejected:")
    for date, count in sorted(bar_dates.items()):
        print(f"  {date}: {count} bars")
    
    print(f"\nLocked trading date: {bars_rejected[0]['locked_date'] if bars_rejected else 'N/A'}")
    
    # Check if any bars from locked date are being processed
    bars_from_locked_date = [b for b in bars_rejected if b['bar_date'] == bars_rejected[0]['locked_date']]
    if bars_from_locked_date:
        print(f"\n⚠️  WARNING: {len(bars_from_locked_date)} bars from locked date are being rejected!")
        print(f"  This suggests a bug - bars matching trading date should be processed")
    else:
        print(f"\nOK: All rejected bars are from different dates (expected behavior)")

# Check stream states
print(f"\n{'=' * 80}")
print("STREAM STATES:")
print("=" * 80)

if stream_states:
    for stream_id, states in stream_states.items():
        if states:
            latest = states[-1]
            print(f"\n{stream_id}: {latest['state']} (last update: {latest['timestamp']})")
else:
    print("No stream state information found")
