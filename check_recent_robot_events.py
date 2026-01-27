#!/usr/bin/env python3
"""Check recent robot events"""
import json
from pathlib import Path
from datetime import datetime

log_dir = Path("logs/robot")
events = []

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
    except:
        pass

# Get today's events, sorted by time
today_events = [e for e in events if e.get('ts_utc', '').startswith('2026-01-26')]
today_events.sort(key=lambda x: x.get('ts_utc', ''))

print("="*80)
print("RECENT ROBOT EVENTS (Last 30)")
print("="*80)

for e in today_events[-30:]:
    ts = e.get('ts_utc', '')[:19] if e.get('ts_utc') else 'N/A'
    event = e.get('event', 'N/A') or 'N/A'
    stream = e.get('stream', '') or ''
    state = e.get('state', '') or ''
    print(f"{ts} | {event:40} | stream: {stream:8} | state: {state}")

print(f"\n{'='*80}")
print("KEY EVENTS CHECK")
print(f"{'='*80}")

# Check for key events
engine_start = [e for e in today_events if e.get('event') == 'ENGINE_START']
trading_date_locked = [e for e in today_events if e.get('event') == 'TRADING_DATE_LOCKED']
streams_created = [e for e in today_events if e.get('event') == 'STREAMS_CREATED']
timetable_updated = [e for e in today_events if e.get('event') == 'TIMETABLE_UPDATED']

print(f"ENGINE_START: {len(engine_start)}")
if engine_start:
    print(f"  Latest: {engine_start[-1].get('ts_utc', '')[:19]}")

print(f"\nTRADING_DATE_LOCKED: {len(trading_date_locked)}")
if trading_date_locked:
    latest = trading_date_locked[-1]
    print(f"  Latest: {latest.get('ts_utc', '')[:19]}")
    print(f"  Trading date: {latest.get('data', {}).get('trading_date', 'N/A')}")

print(f"\nTIMETABLE_UPDATED: {len(timetable_updated)}")
if timetable_updated:
    latest = timetable_updated[-1]
    print(f"  Latest: {latest.get('ts_utc', '')[:19]}")
    data = latest.get('data', {})
    print(f"  Enabled streams: {data.get('enabled_stream_count', 'N/A')}")
    print(f"  Total streams: {data.get('total_stream_count', 'N/A')}")

print(f"\nSTREAMS_CREATED: {len(streams_created)}")
if streams_created:
    latest = streams_created[-1]
    print(f"  Latest: {latest.get('ts_utc', '')[:19]}")
    data = latest.get('data', {})
    print(f"  Stream count: {data.get('stream_count', 0)}")
    if data.get('stream_count', 0) == 0:
        print(f"  WARNING: 0 streams created!")

print(f"\n{'='*80}")
