#!/usr/bin/env python3
"""Check bar loading details"""
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

# Find PRE_HYDRATION_BARS_LOADED events for today
bars_loaded = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'PRE_HYDRATION_BARS_LOADED'):
        bars_loaded.append(e)

if bars_loaded:
    print("="*80)
    print(f"PRE_HYDRATION_BARS_LOADED EVENTS (Found {len(bars_loaded)}):")
    print("="*80)
    for e in bars_loaded[-5:]:
        print(f"\n  Timestamp: {e.get('ts_utc', 'N/A')[:19]}")
        print(f"  Stream: {e.get('stream', 'N/A')}")
        data = e.get('data', {})
        if isinstance(data, dict):
            payload = data.get('payload', {})
            if isinstance(payload, dict):
                print(f"  Instrument: {payload.get('instrument', 'N/A')}")
                print(f"  Stream ID: {payload.get('stream_id', 'N/A')}")
                print(f"  Bar count: {payload.get('bar_count', 'N/A')}")
                print(f"  Source: {payload.get('source', 'N/A')}")
                print(f"  Full payload keys: {list(payload.keys())}")

# Find BARSREQUEST_EXECUTED for today
barsrequest = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_EXECUTED'):
        barsrequest.append(e)

if barsrequest:
    print(f"\n{'='*80}")
    print(f"BARSREQUEST_EXECUTED EVENTS (Found {len(barsrequest)}):")
    print(f"{'='*80}")
    latest = barsrequest[-1]
    print(f"  Timestamp: {latest.get('ts_utc', 'N/A')[:19]}")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"  Instrument: {data.get('instrument', 'N/A')}")
        print(f"  Bars returned: {data.get('bars_returned', data.get('bar_count', 'N/A'))}")
        print(f"  Start time: {data.get('start_time', 'N/A')}")
        print(f"  End time: {data.get('end_time', 'N/A')}")
        print(f"  Full data keys: {list(data.keys())}")

# Check for any bar admission events
bar_admitted = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        'BAR' in e.get('event', '')):
        bar_admitted.append(e)

if bar_admitted:
    print(f"\n{'='*80}")
    print(f"BAR EVENTS FOR NQ2 (Found {len(bar_admitted)}):")
    print(f"{'='*80}")
    for e in bar_admitted[-10:]:
        print(f"  {e.get('ts_utc', 'N/A')[:19]} | {e.get('event', 'N/A')}")

print(f"\n{'='*80}")
