import json
from pathlib import Path
from datetime import datetime, timedelta
from collections import defaultdict

log_file = Path("logs/robot/robot_ENGINE.jsonl")
es_log_file = Path("logs/robot/robot_ES.jsonl")

print("=" * 80)
print("TRADING DATE PROCESSING CHECK (After Restart)")
print("=" * 80)
print()

# Get current time
now = datetime.now()
recent_threshold = now - timedelta(minutes=15)  # Last 15 minutes

# Read ENGINE log
with open(log_file, 'r', encoding='utf-8', errors='ignore') as f:
    engine_lines = f.readlines()

print(f"Analyzing last {len(engine_lines)} ENGINE log entries...")
print()

# Find key events
events = {
    'ENGINE_START': [],
    'TRADING_DATE_LOCKED': [],
    'STREAMS_CREATED': [],
    'BAR_DATE_MISMATCH': [],
    'BAR_RECEIVED': []  # Bars that were processed (not rejected)
}

for line in engine_lines[-1000:]:  # Last 1000 lines
    try:
        entry = json.loads(line)
        event = entry.get('event', '')
        ts_str = entry.get('ts_utc', '')
        payload = entry.get('data', {}).get('payload', {})
        
        if event in events:
            events[event].append({
                'timestamp': ts_str,
                'payload': payload
            })
    except:
        pass

# Print findings
print("KEY EVENTS:")
print("=" * 80)

# Trading date locked
locked_events = events['TRADING_DATE_LOCKED']
if locked_events:
    latest = locked_events[-1]
    print(f"\nTRADING_DATE_LOCKED:")
    print(f"  Timestamp: {latest['timestamp']}")
    payload = latest['payload']
    trading_date = payload.get('trading_date', 'N/A')
    source = payload.get('source', 'N/A')
    print(f"  Trading Date: {trading_date}")
    print(f"  Source: {source}")
    if source == 'TIMETABLE':
        print(f"  OK: Trading date locked from timetable")
    else:
        print(f"  WARNING: Trading date locked from {source} (expected TIMETABLE)")
else:
    print(f"\nTRADING_DATE_LOCKED: NOT FOUND")
    trading_date = None

# Streams created
streams_events = events['STREAMS_CREATED']
if streams_events:
    latest = streams_events[-1]
    print(f"\nSTREAMS_CREATED:")
    print(f"  Timestamp: {latest['timestamp']}")
    payload = latest['payload']
    print(f"  Stream Count: {payload.get('stream_count', 'N/A')}")
    print(f"  Trading Date: {payload.get('trading_date', 'N/A')}")

# Bar processing analysis
print(f"\n{'=' * 80}")
print("BAR PROCESSING ANALYSIS:")
print("=" * 80)

mismatches = events['BAR_DATE_MISMATCH']
if mismatches:
    # Group by bar date
    by_bar_date = defaultdict(int)
    for m in mismatches:
        bar_date = m['payload'].get('bar_trading_date', 'UNKNOWN')
        by_bar_date[bar_date] += 1
    
    print(f"\nBars Rejected (date mismatch): {len(mismatches)}")
    print(f"Locked Trading Date: {mismatches[0]['payload'].get('locked_trading_date', 'N/A') if mismatches else 'N/A'}")
    print("\nRejected by date:")
    for date, count in sorted(by_bar_date.items()):
        print(f"  {date}: {count} bars")
    
    # Check if any bars from locked date are being rejected (this would be a bug)
    if trading_date:
        bars_from_locked_date = [m for m in mismatches if m['payload'].get('bar_trading_date') == trading_date]
        if bars_from_locked_date:
            print(f"\nERROR: {len(bars_from_locked_date)} bars from locked trading date ({trading_date}) are being rejected!")
            print("  This indicates a bug - bars matching trading date should be processed")
        else:
            print(f"\nOK: All rejected bars are from different dates (expected behavior)")
else:
    print(f"\nNo BAR_DATE_MISMATCH events - all bars match trading date!")

# Check ES log for range formation
if es_log_file.exists():
    print(f"\n{'=' * 80}")
    print("RANGE FORMATION CHECK:")
    print("=" * 80)
    
    with open(es_log_file, 'r', encoding='utf-8', errors='ignore') as f:
        es_lines = f.readlines()
    
    range_events = {
        'RANGE_COMPUTE_START': [],
        'RANGE_COMPUTE_FAILED': [],
        'RANGE_COMPUTE_NO_BARS_DIAGNOSTIC': [],
        'RANGE_LOCKED': [],
        'BAR_RECEIVED': []
    }
    
    for line in es_lines[-500:]:  # Last 500 lines
        try:
            entry = json.loads(line)
            event = entry.get('event', '')
            if event in range_events:
                range_events[event].append({
                    'timestamp': entry.get('ts_utc', ''),
                    'payload': entry.get('data', {}).get('payload', {})
                })
        except:
            pass
    
    # Check range locked
    range_locked = range_events['RANGE_LOCKED']
    if range_locked:
        print(f"\nRANGES LOCKED: {len(range_locked)} occurrence(s)")
        for r in range_locked[-3:]:  # Show last 3
            payload = r['payload']
            print(f"  [{r['timestamp']}]")
            print(f"    Range Low: {payload.get('range_low', 'N/A')}")
            print(f"    Range High: {payload.get('range_high', 'N/A')}")
            print(f"    Bar Count: {payload.get('bar_count', 'N/A')}")
    else:
        print(f"\nRANGES LOCKED: None yet")
    
    # Check range failures
    range_failures = range_events['RANGE_COMPUTE_FAILED']
    if range_failures:
        print(f"\nRANGE_COMPUTE_FAILED: {len(range_failures)} occurrence(s)")
        # Group by reason
        by_reason = defaultdict(int)
        for f in range_failures:
            reason = f['payload'].get('reason', 'UNKNOWN')
            by_reason[reason] += 1
        for reason, count in by_reason.items():
            print(f"  {reason}: {count}")
    
    # Check diagnostic
    diagnostics = range_events['RANGE_COMPUTE_NO_BARS_DIAGNOSTIC']
    if diagnostics:
        print(f"\nRANGE_COMPUTE_NO_BARS_DIAGNOSTIC: {len(diagnostics)} occurrence(s)")
        latest = diagnostics[-1]
        payload = latest['payload']
        print(f"  Expected Trading Date: {payload.get('expected_trading_date', 'N/A')}")
        print(f"  Bar Buffer Count: {payload.get('bar_buffer_count', 'N/A')}")
        print(f"  Bar Buffer Date Range: {payload.get('bar_buffer_date_range', 'N/A')}")
        print(f"  Bars From Wrong Date: {payload.get('bars_from_wrong_date', 'N/A')}")
        print(f"  Bars From Correct Date: {payload.get('bars_from_correct_date', 'N/A')}")
        print(f"  Note: {payload.get('note', 'N/A')}")
    
    # Check bars received
    bars_received = range_events['BAR_RECEIVED']
    if bars_received:
        print(f"\nBARS RECEIVED (in ES log): {len(bars_received)}")
        print(f"  Latest: {bars_received[-1]['timestamp']}")
    else:
        print(f"\nBARS RECEIVED (in ES log): None")

# Summary
print(f"\n{'=' * 80}")
print("SUMMARY:")
print("=" * 80)

if trading_date:
    print(f"\nTrading Date: {trading_date}")
    if mismatches:
        bars_from_other_dates = len([m for m in mismatches if m['payload'].get('bar_trading_date') != trading_date])
        print(f"Bars from other dates rejected: {bars_from_other_dates}")
        print(f"Bars from {trading_date} should be processed (check ES log for BAR_RECEIVED events)")
    else:
        print(f"All bars match trading date - processing should be working!")
else:
    print(f"\nTrading date not locked yet - check TRADING_DATE_LOCKED event")
