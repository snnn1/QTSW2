#!/usr/bin/env python3
"""Check what BarsRequest is requesting and what it returns"""
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

# Find BARSREQUEST_REQUESTED events
barsrequest_requested = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_REQUESTED'):
        barsrequest_requested.append(e)

# Find BARSREQUEST_RAW_RESULT events
barsrequest_result = []
for e in events:
    if (e.get('ts_utc', '').startswith('2026-01-26') and 
        e.get('event') == 'BARSREQUEST_RAW_RESULT'):
        barsrequest_result.append(e)

print("="*80)
print("BARSREQUEST ANALYSIS:")
print("="*80)

if barsrequest_requested:
    latest_request = barsrequest_requested[-1]
    data = latest_request.get('data', {})
    if isinstance(data, dict):
        print(f"\n  Latest BARSREQUEST_REQUESTED:")
        print(f"    Range start Chicago: {data.get('range_start_chicago', 'N/A')}")
        print(f"    Request end Chicago: {data.get('request_end_chicago', 'N/A')}")
        print(f"    Start UTC: {data.get('start_utc', 'N/A')}")
        print(f"    End UTC: {data.get('end_utc', 'N/A')}")

if barsrequest_result:
    latest_result = barsrequest_result[-1]
    data = latest_result.get('data', {})
    if isinstance(data, dict):
        print(f"\n  Latest BARSREQUEST_RAW_RESULT:")
        print(f"    Bars returned: {data.get('bars_returned_raw', 'N/A')}")
        print(f"    First bar time: {data.get('first_bar_time', 'N/A')}")
        print(f"    Last bar time: {data.get('last_bar_time', 'N/A')}")
        
        # Parse first and last bar times
        first_bar = data.get('first_bar_time', '')
        last_bar = data.get('last_bar_time', '')
        if first_bar and last_bar:
            try:
                first_dt = datetime.fromisoformat(first_bar.replace('Z', '+00:00'))
                last_dt = datetime.fromisoformat(last_bar.replace('Z', '+00:00'))
                
                # Convert to Chicago time for display
                # UTC to Chicago: subtract 6 hours (CST) or 5 hours (CDT)
                # For Jan 26, 2026, it's CST (UTC-6)
                first_chicago = first_dt.replace(tzinfo=None) - timedelta(hours=6)
                last_chicago = last_dt.replace(tzinfo=None) - timedelta(hours=6)
                
                print(f"\n  Bar close times (from NinjaTrader):")
                print(f"    First bar close: {first_chicago.strftime('%H:%M')} CT")
                print(f"    Last bar close: {last_chicago.strftime('%H:%M')} CT")
                
                print(f"\n  After conversion to open time (-1 minute):")
                first_open = first_chicago - timedelta(minutes=1)
                last_open = last_chicago - timedelta(minutes=1)
                print(f"    First bar open: {first_open.strftime('%H:%M')} CT")
                print(f"    Last bar open: {last_open.strftime('%H:%M')} CT")
                
            except Exception as ex:
                print(f"    Error parsing times: {ex}")

print(f"\n{'='*80}")
print("ANALYSIS:")
print(f"{'='*80}")
print("  If BarsRequest requests [08:00, 11:00) Chicago time:")
print("    - NinjaTrader returns bars with CLOSE times in that range")
print("    - We filter bars where close_time is in [08:00, 11:00)")
print("    - Then convert: close_time - 1 minute = open_time")
print("    - So we get bars with open times [07:59, 10:59)")
print("    - But we need [08:00, 11:00) open times!")
print("\n  SOLUTION: Request [08:01, 11:01) to get bars that convert to [08:00, 11:00)")

print(f"\n{'='*80}")
