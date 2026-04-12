#!/usr/bin/env python3
"""Check current time in logs vs actual time"""
import json
from pathlib import Path
from datetime import datetime
import pytz

log_file = Path("logs/robot/robot_ENGINE.jsonl")
if not log_file.exists():
    print("Log file not found")
    exit(1)

events = []
with open(log_file, 'r', encoding='utf-8') as f:
    for line in f:
        line = line.strip()
        if line:
            try:
                events.append(json.loads(line))
            except:
                pass

if not events:
    print("No events found")
    exit(1)

# Get current time
chicago_tz = pytz.timezone('America/Chicago')
now_chicago = datetime.now(chicago_tz)
now_utc = datetime.now(pytz.UTC)

print("="*80)
print("TIME VERIFICATION")
print("="*80)
print(f"\nCurrent system time:")
print(f"  UTC: {now_utc.strftime('%Y-%m-%d %H:%M:%S %Z')}")
print(f"  Chicago: {now_chicago.strftime('%Y-%m-%d %H:%M:%S %Z')}")

# Get most recent events
recent = events[-10:] if len(events) > 10 else events
print(f"\n=== MOST RECENT LOG ENTRIES (last {len(recent)}) ===")
for e in recent:
    ts_utc_str = e.get('ts_utc', '')
    if ts_utc_str:
        try:
            # Parse UTC timestamp
            if ts_utc_str.endswith('Z'):
                ts_utc_str = ts_utc_str[:-1] + '+00:00'
            dt_utc = datetime.fromisoformat(ts_utc_str)
            if dt_utc.tzinfo is None:
                dt_utc = pytz.UTC.localize(dt_utc)
            
            # Convert to Chicago
            dt_chicago = dt_utc.astimezone(chicago_tz)
            
            event_name = e.get('event', 'N/A')
            print(f"UTC: {dt_utc.strftime('%Y-%m-%d %H:%M:%S')} | Chicago: {dt_chicago.strftime('%Y-%m-%d %H:%M:%S')} | {event_name}")
        except Exception as ex:
            print(f"UTC: {ts_utc_str[:19]} | Event: {e.get('event', 'N/A')} | Error: {ex}")
    else:
        print(f"No timestamp | Event: {e.get('event', 'N/A')}")

# Check time difference
if recent:
    latest = recent[-1]
    ts_utc_str = latest.get('ts_utc', '')
    if ts_utc_str:
        try:
            if ts_utc_str.endswith('Z'):
                ts_utc_str = ts_utc_str[:-1] + '+00:00'
            dt_utc = datetime.fromisoformat(ts_utc_str)
            if dt_utc.tzinfo is None:
                dt_utc = pytz.UTC.localize(dt_utc)
            
            dt_chicago = dt_utc.astimezone(chicago_tz)
            time_diff = (now_chicago - dt_chicago).total_seconds()
            
            print(f"\n=== TIME DIFFERENCE ===")
            print(f"Latest log entry Chicago time: {dt_chicago.strftime('%Y-%m-%d %H:%M:%S')}")
            print(f"Current Chicago time: {now_chicago.strftime('%Y-%m-%d %H:%M:%S')}")
            print(f"Difference: {time_diff:.1f} seconds ({time_diff/60:.1f} minutes)")
            
            if abs(time_diff) < 60:
                print("✓ Log timestamps are current (within 1 minute)")
            elif abs(time_diff) < 300:
                print("⚠ Log timestamps are recent (within 5 minutes)")
            else:
                print("✗ Log timestamps are stale (more than 5 minutes old)")
        except Exception as ex:
            print(f"Error parsing timestamp: {ex}")

print("\n" + "="*80)
