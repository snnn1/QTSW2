#!/usr/bin/env python3
"""Check BarsRequest range determination details"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

# Read all robot log files
for log_file in log_dir.glob("robot_*.jsonl"):
    try:
        with open(log_file, 'r', encoding='utf-8') as f:
            for line in f:
                line = line.strip()
                if line:
                    try:
                        events.append(json.loads(line))
                    except:
                        pass
    except Exception as e:
        pass

# Find BARSREQUEST_RANGE_DETERMINED events
range_determined = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_RANGE_DETERMINED'):
        range_determined.append(e)

if range_determined:
    print("="*80)
    print("BARSREQUEST_RANGE_DETERMINED EVENTS:")
    print("="*80)
    latest = range_determined[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Instrument: {data.get('instrument', 'N/A')}")
        print(f"  Earliest range start: {data.get('earliest_range_start', 'N/A')}")
        print(f"  Latest slot time: {data.get('latest_slot_time', 'N/A')}")
        print(f"  Enabled stream count: {data.get('enabled_stream_count', 'N/A')}")
        print(f"  Sessions used: {data.get('sessions_used', 'N/A')}")
        
        # Check stream_windows if available
        stream_windows = data.get('stream_windows', [])
        if stream_windows:
            print(f"\n  Stream Windows:")
            for w in stream_windows:
                if isinstance(w, dict):
                    print(f"    {w.get('stream_id', 'N/A')}: Session={w.get('session', 'N/A')}, Range={w.get('range_start', 'N/A')}, Slot={w.get('slot_time', 'N/A')}")
        
        # Check session_range_starts
        session_range_starts = data.get('session_range_starts', {})
        if session_range_starts:
            print(f"\n  Session Range Starts:")
            if isinstance(session_range_starts, dict):
                for session, range_start in session_range_starts.items():
                    print(f"    {session}: {range_start}")

# Find BARSREQUEST_STREAM_STATUS events
stream_status = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_STREAM_STATUS'):
        stream_status.append(e)

if stream_status:
    print(f"\n{'='*80}")
    print("BARSREQUEST_STREAM_STATUS (Latest):")
    print(f"{'='*80}")
    latest = stream_status[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        streams = data.get('streams', [])
        if isinstance(streams, list):
            print(f"  Instrument: {data.get('instrument', 'N/A')}")
            print(f"  Total streams: {data.get('total_streams', 'N/A')}")
            for s in streams:
                if isinstance(s, dict):
                    stream_id = s.get('stream_id', 'N/A')
                    session = s.get('session', 'N/A')
                    slot_time = s.get('slot_time', 'N/A')
                    committed = s.get('committed', 'N/A')
                    state = s.get('state', 'N/A')
                    print(f"    {stream_id}: Session={session}, Slot={slot_time}, Committed={committed}, State={state}")

print(f"\n{'='*80}")
