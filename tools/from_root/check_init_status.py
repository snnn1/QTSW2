#!/usr/bin/env python3
"""Check engine initialization and stream status"""
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

recent = events[-500:] if len(events) > 500 else events

print("="*80)
print("ENGINE INITIALIZATION STATUS")
print("="*80)

# Check for initialization events
engine_start = [e for e in recent if 'ENGINE_START' in e.get('event', '')]
trading_date_locked = [e for e in recent if 'TRADING_DATE_LOCKED' in e.get('event', '')]
timetable_validated = [e for e in recent if 'TIMETABLE_VALIDATED' in e.get('event', '')]
stream_armed = [e for e in recent if 'STREAM_ARMED' in e.get('event', '')]

print(f"\nENGINE_START events: {len(engine_start)}")
print(f"TRADING_DATE_LOCKED events: {len(trading_date_locked)}")
print(f"TIMETABLE_VALIDATED events: {len(timetable_validated)}")
print(f"STREAM_ARMED events: {len(stream_armed)}")

if engine_start:
    print(f"\nLatest ENGINE_START:")
    latest = engine_start[-1]
    print(f"  {latest.get('ts_utc', 'N/A')[:19]} | {latest.get('event', 'N/A')}")

if trading_date_locked:
    print(f"\nLatest TRADING_DATE_LOCKED:")
    latest = trading_date_locked[-1]
    payload = latest.get('data', {}).get('payload', {})
    print(f"  {latest.get('ts_utc', 'N/A')[:19]} | Trading Date: {payload.get('trading_date', 'N/A')}")

if stream_armed:
    print(f"\nRecent STREAM_ARMED events:")
    for e in stream_armed[-5:]:
        payload = e.get('data', {}).get('payload', {})
        stream = payload.get('stream', 'N/A')
        print(f"  {e.get('ts_utc', 'N/A')[:19]} | Stream: {stream}")

# Check journal files
journal_dir = Path("logs/robot/journal")
if journal_dir.exists():
    journals = list(journal_dir.glob("2026-01-21_*.json"))
    print(f"\nJournal files for today: {len(journals)}")
    if journals:
        print("\nStream states from journals:")
        for j in sorted(journals)[:10]:
            try:
                data = json.loads(j.read_text())
                stream = data.get('Stream', 'N/A')
                state = data.get('LastState', 'N/A')
                print(f"  {j.name}: {stream} -> {state}")
            except Exception as ex:
                print(f"  {j.name}: Error reading - {ex}")

# Check for PRE_HYDRATION state in recent events
pre_hydration = [e for e in recent if 'PRE_HYDRATION' in e.get('event', '')]
print(f"\nPRE_HYDRATION events: {len(pre_hydration)}")
if pre_hydration:
    print("Recent PRE_HYDRATION events:")
    for e in pre_hydration[-5:]:
        print(f"  {e.get('ts_utc', 'N/A')[:19]} | {e.get('event', 'N/A')}")

print("\n" + "="*80)
