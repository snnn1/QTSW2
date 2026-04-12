#!/usr/bin/env python3
"""Check which sessions streams are using"""
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

# Find STREAMS_CREATED events
streams_created = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'STREAMS_CREATED'):
        streams_created.append(e)

if streams_created:
    print("="*80)
    print("STREAMS CREATED (Latest):")
    print("="*80)
    latest = streams_created[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        streams = data.get('streams', [])
        if isinstance(streams, list):
            for s in streams:
                if isinstance(s, dict):
                    stream_id = s.get('stream_id', 'N/A')
                    session = s.get('session', 'N/A')
                    instrument = s.get('instrument', 'N/A')
                    slot_time = s.get('slot_time_chicago', 'N/A')
                    range_start = s.get('range_start_time', 'N/A')
                    if stream_id == 'NQ2':
                        print(f"  {stream_id}:")
                        print(f"    Session: {session}")
                        print(f"    Instrument: {instrument}")
                        print(f"    Range start: {range_start}")
                        print(f"    Slot time: {slot_time}")

# Find BARSREQUEST_STREAM_STATUS events
barsrequest_status = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_STREAM_STATUS'):
        barsrequest_status.append(e)

if barsrequest_status:
    print(f"\n{'='*80}")
    print("BARSREQUEST_STREAM_STATUS (Latest):")
    print(f"{'='*80}")
    latest = barsrequest_status[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        streams = data.get('streams', [])
        if isinstance(streams, list):
            print(f"  Total streams: {data.get('total_streams', 'N/A')}")
            print(f"  Instrument: {data.get('instrument', 'N/A')}")
            for s in streams:
                if isinstance(s, dict):
                    stream_id = s.get('stream_id', 'N/A')
                    session = s.get('session', 'N/A')
                    instrument = s.get('instrument', 'N/A')
                    slot_time = s.get('slot_time', 'N/A')
                    committed = s.get('committed', 'N/A')
                    state = s.get('state', 'N/A')
                    print(f"    {stream_id}: Session={session}, Slot={slot_time}, Committed={committed}, State={state}")

# Check RANGE_START_INITIALIZED for NQ2
range_start_init = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'RANGE_START_INITIALIZED' and
        e.get('stream') == 'NQ2'):
        range_start_init.append(e)

if range_start_init:
    print(f"\n{'='*80}")
    print("RANGE_START_INITIALIZED for NQ2:")
    print(f"{'='*80}")
    latest = range_start_init[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        payload = data.get('payload', {})
        if isinstance(payload, dict):
            range_start_chicago = payload.get('range_start_chicago', 'N/A')
            range_start_time_string = payload.get('range_start_time_string', 'N/A')
            print(f"  Range start time string: {range_start_time_string}")
            print(f"  Range start Chicago: {range_start_chicago}")

print(f"\n{'='*80}")
