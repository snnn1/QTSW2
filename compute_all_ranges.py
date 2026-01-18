#!/usr/bin/env python3
"""
Compute and display all ranges for all streams from DRYRUN logs.
"""
import json
import os
from collections import defaultdict

# Check multiple log files - async logging routes to per-instrument files
log_files = [
    "logs/robot/robot_skeleton.jsonl",  # Fallback/legacy
    "logs/robot/robot_ENGINE.jsonl",     # Engine events
    "logs/robot/robot_ES.jsonl",         # ES stream events
    "logs/robot/robot_NQ.jsonl",         # NQ stream events
    "logs/robot/robot_GC.jsonl",         # GC stream events
    "logs/robot/robot_CL.jsonl",         # CL stream events
]

# Check if any log files exist
log_files_exist = [f for f in log_files if os.path.exists(f)]
if not log_files_exist:
    print("No log files found. Please run a DRYRUN first.")
    exit(1)

# Collect all RANGE_COMPUTE_COMPLETE events and related range events
range_events = []
range_initialized_events = []
range_locked_events = []
streams_seen = set()

# Search all log files
for log_file in log_files:
    if not os.path.exists(log_file):
        continue
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                try:
                    event = json.loads(line.strip())
                    # Handle both old format (event_type) and new format (event)
                    event_type = event.get('event_type') or event.get('event', '')
                    
                    if event_type == 'RANGE_COMPUTE_COMPLETE':
                        range_events.append(event)
                        # Handle both formats for stream/trading_date
                        stream = event.get('stream') or event.get('data', {}).get('stream_id', '')
                        trading_date = event.get('trading_date') or event.get('data', {}).get('trading_date', '')
                        if stream and trading_date:
                            streams_seen.add((stream, trading_date))
                    elif event_type == 'RANGE_LOCKED_INCREMENTAL':
                        range_locked_events.append(event)
                        # Extract stream info for tracking
                        data = event.get('data', {})
                        trading_date = data.get('trading_date', '')
                        instrument = event.get('instrument', '')
                        session = data.get('session', '')
                        if trading_date and instrument and session:
                            session_num = session.replace('S', '') if session.startswith('S') else session
                            stream = f"{instrument}{session_num}"
                            streams_seen.add((stream, trading_date))
                    elif event_type == 'RANGE_INITIALIZED_FROM_HISTORY':
                        range_initialized_events.append(event)
                except json.JSONDecodeError:
                    continue
    except Exception as e:
        # Skip files that can't be read
        continue

# Use RANGE_COMPUTE_COMPLETE first, fall back to RANGE_LOCKED_INCREMENTAL or RANGE_INITIALIZED_FROM_HISTORY
# RANGE_LOCKED_INCREMENTAL is the most common in replay mode
# Prefer RANGE_LOCKED_INCREMENTAL if we have more of those (replay mode)
if len(range_locked_events) > len(range_events):
    all_range_data = range_locked_events
elif range_events:
    all_range_data = range_events
elif range_locked_events:
    all_range_data = range_locked_events
else:
    all_range_data = range_initialized_events

if not all_range_data:
    print("No range computation events found in logs.")
    print("\nPossible reasons:")
    print("  1. DRYRUN hasn't been run yet")
    print("  2. Streams didn't reach RANGE_BUILDING state")
    print("  3. Range computation didn't complete")
    print("  4. Logs were cleared/rotated")
    print(f"\nFound {len(range_initialized_events)} RANGE_INITIALIZED events")
    print(f"Found {len(range_locked_events)} RANGE_LOCKED_INCREMENTAL events")
    print(f"Found {len(range_events)} RANGE_COMPUTE_COMPLETE events")
    print("\nTo compute ranges:")
    print("  1. Ensure timetable has enabled streams")
    print("  2. Run: dotnet run --project modules/robot/harness/Robot.Harness.csproj --mode DRYRUN --replay --start YYYY-MM-DD --end YYYY-MM-DD")
    print("  3. RANGE_COMPUTE_COMPLETE events are logged when streams reach slot_time")
    exit(1)

# Group by trading date, then by stream
by_date = defaultdict(lambda: defaultdict(list))

for event in all_range_data:
    # Handle both formats for stream/trading_date
    # New format has trading_date in data.trading_date
    data = event.get('data', {})
    trading_date = event.get('trading_date') or data.get('trading_date', 'UNKNOWN')
    
    # Derive stream from instrument + session
    instrument = event.get('instrument', 'UNKNOWN')
    session = event.get('session') or data.get('session', 'UNKNOWN')
    if instrument != 'UNKNOWN' and session != 'UNKNOWN':
        session_num = session.replace('S', '') if session.startswith('S') else session
        stream = f"{instrument}{session_num}"
    else:
        stream = event.get('stream') or data.get('stream_id', 'UNKNOWN')
    
    if trading_date != 'UNKNOWN' and stream != 'UNKNOWN':
        by_date[trading_date][stream].append(event)

# Display results
print("=" * 80)
print("RANGE COMPUTATION RESULTS FROM DRYRUN")
print("=" * 80)

for trading_date in sorted(by_date.keys()):
    print(f"\nTrading Date: {trading_date}")
    print("-" * 80)
    
    streams = by_date[trading_date]
    for stream in sorted(streams.keys()):
        events = streams[stream]
        # Get the most recent event for this stream (in case of multiple)
        latest_event = max(events, key=lambda e: e.get('ts_utc', ''))
        
        # Handle both old format (data) and new format (data.payload)
        data = latest_event.get('data', {})
        if isinstance(data, dict) and 'payload' in data:
            payload = data.get('payload', {})
            data = payload  # Use payload as the data dict
        
        # Extract stream, instrument, session from event structure
        # Stream ID format: <INSTRUMENT><SESSION_NUMBER> (e.g., ES1, ES2, NQ1)
        instrument = latest_event.get('instrument', data.get('instrument', 'UNKNOWN'))
        session = latest_event.get('session') or latest_event.get('data', {}).get('session', 'UNKNOWN')
        
        # Derive stream ID from instrument + session (S1 -> 1, S2 -> 2)
        if instrument != 'UNKNOWN' and session != 'UNKNOWN':
            session_num = session.replace('S', '') if session.startswith('S') else session
            stream = f"{instrument}{session_num}"
        else:
            stream = latest_event.get('stream') or latest_event.get('data', {}).get('stream_id', 'UNKNOWN')
        
        range_high = data.get('range_high')
        range_low = data.get('range_low')
        bar_count = data.get('bar_count', 0)
        expected_bar_count = data.get('expected_bar_count')
        range_size = data.get('range_size')
        
        range_start_chicago = data.get('range_start_chicago')
        slot_time_chicago = data.get('slot_time_chicago') or latest_event.get('slot_time_chicago') or latest_event.get('data', {}).get('slot_time_chicago', '')
        
        print(f"\n  Stream: {stream}")
        print(f"    Instrument: {instrument}")
        print(f"    Session: {session}")
        
        if range_start_chicago:
            print(f"    Range Start: {range_start_chicago}")
        if slot_time_chicago:
            print(f"    Slot Time: {slot_time_chicago}")
        
        if range_high is not None and range_low is not None:
            print(f"    Range High:  {range_high}")
            print(f"    Range Low:   {range_low}")
            if range_size is not None:
                print(f"    Range Size:  {range_size}")
        else:
            print(f"    Range: NOT COMPUTED")
        
        print(f"    Bar Count: {bar_count}", end="")
        if expected_bar_count is not None:
            diff = bar_count - expected_bar_count
            if abs(diff) <= 5:
                print(f" (expected: {expected_bar_count}, diff: {diff:+d}) ✓")
            else:
                print(f" (expected: {expected_bar_count}, diff: {diff:+d}) ⚠")
        else:
            print()
        
        # Show first and last bar times if available
        first_bar_chicago = data.get('first_bar_chicago')
        last_bar_chicago = data.get('last_bar_chicago')
        if first_bar_chicago and last_bar_chicago:
            print(f"    Bar Window: {first_bar_chicago} to {last_bar_chicago}")

print("\n" + "=" * 80)
print(f"Total streams with ranges: {len(streams_seen)}")
print("=" * 80)
