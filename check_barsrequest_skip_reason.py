#!/usr/bin/env python3
"""Check why BARSREQUEST is being skipped"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

# Read ENGINE log file (most recent)
engine_log = log_dir / "robot_ENGINE.jsonl"
if engine_log.exists():
    with open(engine_log, 'r', encoding='utf-8') as f:
        for line in f:
            line = line.strip()
            if line:
                try:
                    events.append(json.loads(line))
                except:
                    pass

# Find latest BARSREQUEST_SKIPPED for MNQ/NQ
skipped_events = []
for e in events:
    if (e.get('event') == 'BARSREQUEST_SKIPPED' and 
        e.get('ts_utc', '').startswith('2026-01-26')):
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            if 'MNQ' in str(instrument) or 'NQ' in str(instrument):
                skipped_events.append(e)

if skipped_events:
    latest = skipped_events[-1]
    print("="*80)
    print("LATEST BARSREQUEST_SKIPPED EVENT:")
    print("="*80)
    print(f"Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"Instrument: {data.get('instrument', 'N/A')}")
        print(f"Reason: {data.get('reason', 'N/A')}")
        print(f"\nFull data:")
        for key, value in data.items():
            print(f"  {key}: {value}")
else:
    print("No BARSREQUEST_SKIPPED events found")

# Also check for STREAMS_CREATED events
streams_created = [e for e in events if e.get('event') == 'STREAMS_CREATED' and e.get('ts_utc', '').startswith('2026-01-26')]
if streams_created:
    print(f"\n{'='*80}")
    print("STREAMS_CREATED EVENTS (TODAY):")
    print(f"{'='*80}")
    for e in streams_created[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        data = e.get('data', {})
        print(f"  {ts}")
        if isinstance(data, dict):
            print(f"    Streams: {data.get('streams', 'N/A')}")
            print(f"    Trading date: {data.get('trading_date', 'N/A')}")

print("\n" + "="*80)
