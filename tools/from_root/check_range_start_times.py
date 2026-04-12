#!/usr/bin/env python3
"""Check if RangeStartChicagoTime is initialized for streams"""
import json
from pathlib import Path

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

recent = events[-2000:] if len(events) > 2000 else events

print("="*80)
print("RANGE START TIME INITIALIZATION CHECK")
print("="*80)

# Look for any events that mention range_start_chicago
range_start_events = []
for e in recent:
    payload = e.get('data', {}).get('payload', {})
    if 'range_start_chicago' in str(payload):
        range_start_events.append(e)

print(f"\nEvents mentioning range_start_chicago: {len(range_start_events)}")

if range_start_events:
    print("\nRecent range_start_chicago events:")
    for e in range_start_events[-10:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        event = e.get('event', 'N/A')
        payload = e.get('data', {}).get('payload', {})
        range_start = payload.get('range_start_chicago', 'N/A')
        stream = payload.get('stream', payload.get('stream_id', payload.get('stream_id', 'N/A')))
        print(f"  {ts} | {event} | Stream: {stream} | RangeStart: {range_start}")

# Check for STREAM_ARMED events which should set RangeStartChicagoTime
stream_armed = [e for e in recent if 'STREAM_ARMED' in e.get('event', '')]
print(f"\nSTREAM_ARMED events: {len(stream_armed)}")
if stream_armed:
    print("Recent STREAM_ARMED events:")
    for e in stream_armed[-5:]:
        ts = e.get('ts_utc', 'N/A')[:19]
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream', 'N/A')
        range_start = payload.get('range_start_chicago', 'N/A')
        print(f"  {ts} | Stream: {stream} | RangeStart: {range_start}")

# Check timetable for range start times
timetable_path = Path("data/timetable/timetable_current.json")
if timetable_path.exists():
    timetable = json.loads(timetable_path.read_text())
    streams = timetable.get('streams', [])
    enabled = [s for s in streams if s.get('enabled', False)]
    
    print(f"\n" + "="*80)
    print("ENABLED STREAMS FROM TIMETABLE")
    print("="*80)
    for s in enabled:
        instrument = s.get('instrument', 'N/A')
        session = s.get('session', 'N/A')
        slot_time = s.get('slot_time', 'N/A')
        print(f"  {instrument} {session} {slot_time}")

print("\n" + "="*80)
