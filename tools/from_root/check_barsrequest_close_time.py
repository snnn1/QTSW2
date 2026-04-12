#!/usr/bin/env python3
"""Check BarsRequest CLOSE time boundaries and assertions"""
import json
from pathlib import Path
from datetime import datetime, timedelta

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

# Find relevant events
close_time_boundaries = []
close_time_verified = []
barsrequest_requested = []
barsrequest_result = []

for e in events:
    if e.get('ts_utc', '').startswith('2026-01-26'):
        event_type = e.get('event', '')
        if event_type == 'BARSREQUEST_CLOSE_TIME_BOUNDARIES':
            close_time_boundaries.append(e)
        elif event_type == 'BARSREQUEST_CLOSE_TIME_VERIFIED':
            close_time_verified.append(e)
        elif event_type == 'BARSREQUEST_REQUESTED':
            barsrequest_requested.append(e)
        elif event_type == 'BARSREQUEST_RAW_RESULT':
            barsrequest_result.append(e)

print("="*80)
print("BARSREQUEST CLOSE TIME ANALYSIS:")
print("="*80)

if close_time_boundaries:
    latest = close_time_boundaries[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  Latest BARSREQUEST_CLOSE_TIME_BOUNDARIES:")
        print(f"    Desired OPEN start: {data.get('desired_open_start_chicago', 'N/A')}")
        print(f"    Desired OPEN end: {data.get('desired_open_end_chicago', 'N/A')}")
        print(f"    Request CLOSE start: {data.get('request_close_start_chicago', 'N/A')}")
        print(f"    Request CLOSE end: {data.get('request_close_end_chicago', 'N/A')}")
else:
    print(f"\n  No BARSREQUEST_CLOSE_TIME_BOUNDARIES events found")

if close_time_verified:
    latest = close_time_verified[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  Latest BARSREQUEST_CLOSE_TIME_VERIFIED:")
        print(f"    Bars returned: {data.get('bars_returned', 'N/A')}")
        print(f"    Bars filtered out: {data.get('bars_filtered_out', 'N/A')}")
        print(f"    First close time UTC: {data.get('first_close_time_utc', 'N/A')}")
        print(f"    Last close time UTC: {data.get('last_close_time_utc', 'N/A')}")
        print(f"    Requested start UTC: {data.get('requested_start_utc', 'N/A')}")
        print(f"    Requested end UTC: {data.get('requested_end_utc', 'N/A')}")
        print(f"    Assertion passed: {data.get('assertion_passed', 'N/A')}")
        
        # Parse times for analysis
        first_close = data.get('first_close_time_utc', '')
        last_close = data.get('last_close_time_utc', '')
        req_start = data.get('requested_start_utc', '')
        req_end = data.get('requested_end_utc', '')
        
        if first_close and last_close and req_start and req_end:
            try:
                first_dt = datetime.fromisoformat(first_close.replace('Z', '+00:00'))
                last_dt = datetime.fromisoformat(last_close.replace('Z', '+00:00'))
                start_dt = datetime.fromisoformat(req_start.replace('Z', '+00:00'))
                end_dt = datetime.fromisoformat(req_end.replace('Z', '+00:00'))
                
                # Convert to Chicago for display
                first_chicago = first_dt.replace(tzinfo=None) - timedelta(hours=6)
                last_chicago = last_dt.replace(tzinfo=None) - timedelta(hours=6)
                start_chicago = start_dt.replace(tzinfo=None) - timedelta(hours=6)
                end_chicago = end_dt.replace(tzinfo=None) - timedelta(hours=6)
                
                print(f"\n  Close Time Analysis (Chicago):")
                print(f"    Requested: [{start_chicago.strftime('%H:%M')}, {end_chicago.strftime('%H:%M')})")
                print(f"    Received: [{first_chicago.strftime('%H:%M')}, {last_chicago.strftime('%H:%M')}]")
                
                # Check assertions
                if first_dt >= start_dt:
                    print(f"    [PASS] First bar >= requested start")
                else:
                    print(f"    [FAIL] First bar < requested start")
                
                if last_dt < end_dt:
                    print(f"    [PASS] Last bar < requested end")
                else:
                    print(f"    [FAIL] Last bar >= requested end")
                
                # Show open times after conversion
                first_open = first_chicago - timedelta(minutes=1)
                last_open = last_chicago - timedelta(minutes=1)
                print(f"\n  After conversion to OPEN time (-1 minute):")
                print(f"    First bar open: {first_open.strftime('%H:%M')} CT")
                print(f"    Last bar open: {last_open.strftime('%H:%M')} CT")
                
            except Exception as ex:
                print(f"    Error parsing times: {ex}")
else:
    print(f"\n  No BARSREQUEST_CLOSE_TIME_VERIFIED events found")

if barsrequest_requested:
    latest = barsrequest_requested[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  Latest BARSREQUEST_REQUESTED:")
        print(f"    Range start: {data.get('range_start_chicago', 'N/A')}")
        print(f"    Request end: {data.get('request_end_chicago', 'N/A')}")

if barsrequest_result:
    latest = barsrequest_result[-1]
    data = latest.get('data', {})
    if isinstance(data, dict):
        print(f"\n  Latest BARSREQUEST_RAW_RESULT:")
        print(f"    Bars returned: {data.get('bars_returned_raw', 'N/A')}")

print(f"\n{'='*80}")
