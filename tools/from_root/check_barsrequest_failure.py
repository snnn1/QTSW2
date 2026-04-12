#!/usr/bin/env python3
"""Check BARSREQUEST_FAILED details"""
import json
from pathlib import Path

log_dir = Path("logs/robot")
events = []

# Read ENGINE log file
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

# Find latest BARSREQUEST_FAILED for MNQ/NQ
failed_events = []
for e in events:
    if (e.get('event') == 'BARSREQUEST_FAILED' and 
        e.get('ts_utc', '').startswith('2026-01-26')):
        data = e.get('data', {})
        if isinstance(data, dict):
            instrument = data.get('instrument', '')
            if 'MNQ' in str(instrument) or 'NQ' in str(instrument):
                failed_events.append(e)

if failed_events:
    latest = failed_events[-1]
    print("="*80)
    print("LATEST BARSREQUEST_FAILED EVENT:")
    print("="*80)
    print(f"Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"Instrument: {data.get('instrument', 'N/A')}")
        print(f"Error: {data.get('error', 'N/A')}")
        print(f"Exception: {data.get('exception', 'N/A')}")
        print(f"\nFull data:")
        for key, value in data.items():
            print(f"  {key}: {value}")
else:
    print("No BARSREQUEST_FAILED events found")

print("\n" + "="*80)
