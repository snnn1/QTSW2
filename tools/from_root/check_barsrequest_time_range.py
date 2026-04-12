#!/usr/bin/env python3
"""Check BarsRequest time range calculation"""
import json
from pathlib import Path
from datetime import datetime

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

# Find BARSREQUEST_REQUESTED and BARSREQUEST_EXECUTED events
barsrequest_requested = []
barsrequest_executed = []

for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_REQUESTED'):
        barsrequest_requested.append(e)
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_EXECUTED'):
        barsrequest_executed.append(e)

print("="*80)
print("BARSREQUEST TIME RANGE INVESTIGATION:")
print("="*80)

if barsrequest_requested:
    latest_requested = barsrequest_requested[-1]
    print(f"\n  LATEST BARSREQUEST_REQUESTED:")
    print(f"    Timestamp: {latest_requested.get('ts_utc', 'N/A')[:19]}")
    data = latest_requested.get('data', {})
    if isinstance(data, dict):
        print(f"    Instrument: {data.get('instrument', 'N/A')}")
        print(f"    Trading date: {data.get('trading_date', 'N/A')}")
        print(f"    Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
        print(f"    Request end Chicago: {data.get('request_end_chicago', 'N/A')}")
        print(f"    Session: {data.get('session', 'N/A')}")
        print(f"    Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")

if barsrequest_executed:
    latest_executed = barsrequest_executed[-1]
    print(f"\n  LATEST BARSREQUEST_EXECUTED:")
    print(f"    Timestamp: {latest_executed.get('ts_utc', 'N/A')[:19]}")
    data = latest_executed.get('data', {})
    if isinstance(data, dict):
        print(f"    Instrument: {data.get('instrument', 'N/A')}")
        print(f"    Bars returned: {data.get('bars_returned', 'N/A')}")
        print(f"    Range start time: {data.get('range_start_time', 'N/A')}")
        print(f"    Slot time: {data.get('slot_time', 'N/A')}")
        print(f"    End time: {data.get('end_time', 'N/A')}")
        print(f"    Start time UTC: {data.get('start_time_utc', 'N/A')}")
        print(f"    End time UTC: {data.get('end_time_utc', 'N/A')}")
        print(f"    Start time Chicago: {data.get('start_time_chicago', 'N/A')}")
        print(f"    End time Chicago: {data.get('end_time_chicago', 'N/A')}")
        print(f"    Current time Chicago: {data.get('current_time_chicago', 'N/A')}")

# Check latest HYDRATION_SUMMARY for comparison
hydration = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'HYDRATION_SUMMARY' and
        e.get('stream') == 'NQ2'):
        hydration.append(e)

if hydration:
    latest = hydration[-1]
    print(f"\n  LATEST HYDRATION_SUMMARY:")
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"    Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
        print(f"    Slot time Chicago: {data.get('slot_time_chicago', 'N/A')}")
        print(f"    Now Chicago: {data.get('now_chicago', 'N/A')}")

# Check what bars were actually requested
print(f"\n  BAR TIMESTAMPS FROM BARSREQUEST:")
print(f"    (Checking first few bars received)")
bar_proof = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('stream') == 'NQ2' and
        e.get('event') == 'BAR_ADMISSION_PROOF' and
        e.get('data', {}).get('bar_source') == 'BARSREQUEST'):
        bar_proof.append(e)

if bar_proof:
    print(f"    Found {len(bar_proof)} bars from BARSREQUEST")
    for e in bar_proof[:10]:
        data = e.get('data', {})
        if isinstance(data, dict):
            bar_time = data.get('bar_time_chicago', 'N/A')
            range_start = data.get('range_start_chicago', 'N/A')
            slot_time = data.get('slot_time_chicago', 'N/A')
            result = data.get('comparison_result', 'N/A')
            print(f"      {bar_time} | In range: {result} | Window: [{range_start}, {slot_time})")

print(f"\n{'='*80}")
